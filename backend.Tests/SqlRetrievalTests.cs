using System.Data.Common;
using System.Text.Json;
using ClinicalRag.Api.Controllers;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using ClinicalRag.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace ClinicalRag.Tests;

public class SqlRetrievalTests(ITestOutputHelper output)
{
    private static readonly EmbeddingBatch QueryVector = EmbeddingVectors.Parse(EmbeddingTestData.Result(1, EmbeddingTestData.Model, 1, 0), 1);

    private static AnalysisController Controller(RetrievalSqlStore store, AppDbContext db)
    {
        var client = new FakeColabClient { EmbedHandler = texts => Task.FromResult(EmbeddingTestData.Result(texts.Count, EmbeddingTestData.Model, 1, 0)) };
        return new(db, client, new ConfigService(db), new FakePipelineClient(), new FakeCurrentUser(store.Owner),
            new ConfigurationBuilder().Build(), NullLogger<AnalysisController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
    }

    private static Chunk Chunk(Document document, string text = "Retained source", string? section = null, string status = "embedded",
        string model = EmbeddingTestData.Model, float x = 1, float y = 0) => new()
    {
        WorkspaceId = document.WorkspaceId, DocumentId = document.Id, Page = 1, Text = text, Section = section, Status = status,
        EmbeddingModel = model, Embedding = EmbeddingVectors.EncodeNormalized([x, y])
    };

    private static Guid[] Ids(object result) => JsonSerializer.SerializeToElement(result).GetProperty("chunks").EnumerateArray()
        .Select(chunk => Guid.Parse(chunk.GetProperty("chunk_id").GetString()!)).ToArray();

    [Fact]
    public async Task SQL_Bm25_fallback_searches_section_and_isolates_workspace_review_state_and_legacy_archives()
    {
        await using var store = await RetrievalSqlStore.CreateAsync();
        var target = Chunk(store.Document, "A retained quotation", "cystatin monitoring", x: 0, y: 1);
        var pending = Chunk(store.Document, "cystatin", status: "pending", model: "unreviewed-model");
        var rejected = Chunk(store.Document, "cystatin", status: "rejected", model: "rejected-model");
        var legacy = new Chunk { WorkspaceId = store.Workspace.Id, DocumentId = store.Document.Id, Page = 1,
            Text = "cystatin", Status = "accepted", LegacyEmbeddingJson = "[1,0]", EmbeddingModel = "legacy-model" };
        var otherWorkspace = new Workspace { OwnerId = store.Owner.Id, Name = "Other workspace" };
        var otherDocument = new Document { WorkspaceId = otherWorkspace.Id, FileName = "other.pdf" };
        var other = Chunk(otherDocument, "cystatin", model: "other-workspace-model");
        await using (var seed = store.Context())
        {
            seed.Workspaces.Add(otherWorkspace); seed.Documents.Add(otherDocument);
            seed.Chunks.AddRange(target, pending, rejected, legacy, other);
            seed.Chunks.AddRange(Enumerable.Range(0, 80).Select(i => Chunk(store.Document, $"General dense background {i}")));
            await seed.SaveChangesAsync();
        }
        var recorder = new SqlQueryRecorder();
        await using var first = store.Context(recorder);
        var ids = Ids(await Controller(store, first).DoRetrieve(store.Workspace.Id, "cystatin"));
        Assert.Contains(target.Id, ids); Assert.DoesNotContain(pending.Id, ids); Assert.DoesNotContain(rejected.Id, ids);
        Assert.DoesNotContain(legacy.Id, ids); Assert.DoesNotContain(other.Id, ids);
        Assert.DoesNotContain(recorder.Commands, c => c.Text.Contains("FREETEXTTABLE", StringComparison.Ordinal));
        Assert.Single(recorder.Commands, c => c.Select.Contains("[c].[Embedding]", StringComparison.Ordinal));
        Assert.Empty(first.ChangeTracker.Entries<Chunk>());

        recorder.Commands.Clear(); await using var warm = store.Context(recorder);
        Assert.Contains(target.Id, Ids(await Controller(store, warm).DoRetrieve(store.Workspace.Id, "cystatin")));
        var metadata = Assert.Single(recorder.Commands, c => c.Text.Contains("FROM [Chunks] AS [c]", StringComparison.Ordinal));
        Assert.DoesNotContain("[c].[Embedding]", metadata.Select); Assert.DoesNotContain("LegacyEmbeddingJson", metadata.Text);
        Assert.Contains("WHERE", metadata.Text); Assert.Contains(store.Workspace.Id, metadata.Parameters.Values);
        Assert.Empty(warm.ChangeTracker.Entries<Chunk>());
    }

    [Fact]
    public async Task Raw_chunk_changes_bump_the_shared_generation_and_reload_another_cache()
    {
        await using var store = await RetrievalSqlStore.CreateAsync();
        Guid chunkId;
        long before;
        await using (var seed = store.Context())
        {
            var chunk = Chunk(store.Document, "Original exactterm", x: 0, y: 1);
            seed.Chunks.Add(chunk); await seed.SaveChangesAsync(); chunkId = chunk.Id;
            before = await seed.Workspaces.Where(w => w.Id == store.Workspace.Id).Select(w => w.SearchGeneration).SingleAsync();
        }
        using var cache = new ChunkVectorCache();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(store.ConnectionString).Options;
        await using var reader = new AppDbContext(options, cache);
        var oldIndex = await cache.GetAsync(store.Workspace.Id, before, _ => Task.FromResult(new ChunkSearchIndex([
            new(chunkId, "Original exactterm", null, EmbeddingTestData.Model, EmbeddingVectors.EncodeNormalized([0, 1]))])));
        await using (var writer = store.Context())
            await writer.Database.ExecuteSqlInterpolatedAsync($"UPDATE [Chunks] SET [Text] = {"Rotated exactterm"} WHERE [Id] = {chunkId}");
        long after;
        await using (var observer = store.Context())
            after = await observer.Workspaces.Where(w => w.Id == store.Workspace.Id).Select(w => w.SearchGeneration).SingleAsync();
        Assert.Equal(before + 1, after);
        var newIndex = await cache.GetAsync(store.Workspace.Id, after, _ => Task.FromResult(new ChunkSearchIndex([
            new(chunkId, "Rotated exactterm", null, EmbeddingTestData.Model, EmbeddingVectors.EncodeNormalized([0, 1]))])));
        Assert.NotSame(oldIndex, newIndex);
        Assert.Contains(newIndex.Lexical.Search("Rotated", 30), hit => hit.Id == chunkId);
        Assert.DoesNotContain(oldIndex.Lexical.Search("Rotated", 30), hit => hit.Id == chunkId);
    }

    [Fact]
    public async Task Warm_SQL_retrieval_projects_current_source_metadata_not_a_tracked_document_or_vector_corpus()
    {
        await using var store = await RetrievalSqlStore.CreateAsync();
        await using (var seed = store.Context()) { seed.Chunks.Add(Chunk(store.Document)); await seed.SaveChangesAsync(); }
        var recorder = new SqlQueryRecorder(); await using var db = store.Context(recorder);
        var old = await db.Documents.SingleAsync(); Assert.Equal("Original society", old.Society);
        await Controller(store, db).DoRetrieve(store.Workspace.Id, "source");
        // Simulate another process changing scalar source metadata. The cached
        // vectors stay warm; AsNoTracking must still read the current SQL values.
        await using (var update = store.Context())
            await update.Database.ExecuteSqlInterpolatedAsync($"UPDATE [Documents] SET [Society] = {"Current society"}, [Year] = {2026}, [GuidelineStatus] = {"draft"} WHERE [Id] = {store.Document.Id}");
        recorder.Commands.Clear();
        var result = JsonSerializer.SerializeToElement(await Controller(store, db).DoRetrieve(store.Workspace.Id, "source"));
        var source = Assert.Single(result.GetProperty("chunks").EnumerateArray());
        Assert.Equal("Current society", source.GetProperty("society").GetString()); Assert.Equal("2026", source.GetProperty("year").GetString());
        Assert.Equal("draft", source.GetProperty("status").GetString()); Assert.Contains("(DRAFT)", result.GetProperty("context").GetString());
        Assert.Equal("Original society", old.Society);
        Assert.DoesNotContain(recorder.Commands, c => c.Select.Contains("[c].[Embedding]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Warm_SQL_retrieval_refuses_model_labels_that_only_match_due_to_SQL_trailing_space_equality()
    {
        await using var store = await RetrievalSqlStore.CreateAsync(); var target = Chunk(store.Document);
        await using (var seed = store.Context()) { seed.Chunks.Add(target); await seed.SaveChangesAsync(); }
        var recorder = new SqlQueryRecorder(); await using var db = store.Context(recorder);
        var controller = Controller(store, db);
        Assert.Equal(target.Id, Assert.Single(Ids(await controller.DoRetrieve(store.Workspace.Id, "source"))));
        await using (var update = store.Context())
            await update.Database.ExecuteSqlInterpolatedAsync($"UPDATE [Chunks] SET [EmbeddingModel] = {EmbeddingTestData.Model + " "} WHERE [Id] = {target.Id}");
        // SQL's predicate still considers these equal; the durable generation
        // invalidates the cache and the fresh ordinal label check fails closed
        // instead of serving the old valid-looking snapshot.
        Assert.Equal(1, await db.Chunks.CountAsync(c => c.Id == target.Id && c.EmbeddingModel == EmbeddingTestData.Model));
        recorder.Commands.Clear();
        await Assert.ThrowsAsync<EmbeddingModelMismatchException>(() => controller.DoRetrieve(store.Workspace.Id, "source"));
    }

    [Fact]
    public async Task SQL_model_distinctness_and_workspace_concurrency_tokens_are_case_sensitive()
    {
        await using var store = await RetrievalSqlStore.CreateAsync();
        await using (var seed = store.Context())
        {
            seed.Chunks.AddRange(Chunk(store.Document, model: "model-a"), Chunk(store.Document, model: "MODEL-A"));
            var workspace = await seed.Workspaces.SingleAsync(); workspace.EmbeddingModel = "model-a"; workspace.EmbeddingDimensions = 2;
            await seed.SaveChangesAsync();
        }
        await using var stale = store.Context();
        var batch = EmbeddingVectors.Parse(EmbeddingTestData.Result(1, "model-a", 1, 0), 1);
        await Assert.ThrowsAsync<EmbeddingModelMismatchException>(() => EmbeddingModels.RequireCompatibleAsync(stale, store.Workspace.Id, batch, CancellationToken.None));
        Assert.Equal(2, await stale.Chunks.Select(c => c.EmbeddingModel).Distinct().CountAsync());
        await using (var update = store.Context())
        {
            await update.Database.ExecuteSqlRawAsync("UPDATE [Chunks] SET [Embedding] = NULL;");
            (await update.Workspaces.SingleAsync()).EmbeddingModel = "MODEL-A"; await update.SaveChangesAsync();
        }
        await EmbeddingModels.RequireCompatibleAsync(stale, store.Workspace.Id, batch, CancellationToken.None);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync());
        await using var observe = store.Context(); Assert.Equal("MODEL-A", (await observe.Workspaces.SingleAsync()).EmbeddingModel);
    }

    [Fact]
    public async Task A_cached_FullText_index_removed_before_query_uses_Bm25_and_rechecks_capability_next_time()
    {
        await using var store = await RetrievalSqlStore.CreateAsync(); var target = Chunk(store.Document, "Retained quotation", "cystatin", x: 0, y: 1);
        long generation;
        await using (var seed = store.Context())
        {
            seed.Chunks.Add(target); await seed.SaveChangesAsync();
            generation = await seed.Workspaces.Where(w => w.Id == store.Workspace.Id).Select(w => w.SearchGeneration).SingleAsync();
        }
        // Represents a Full-Text index dropped after this immutable snapshot was loaded.
        await store.Cache.GetAsync(store.Workspace.Id, generation, _ => Task.FromResult(new ChunkSearchIndex([
            new(target.Id, target.Text, target.Section, target.EmbeddingModel, target.Embedding!)], fullTextAvailable: true)));
        var recorder = new SqlQueryRecorder(); await using var db = store.Context(recorder);
        const string query = "cystatin'); DROP TABLE [dbo].[Chunks];--";
        Assert.Equal(target.Id, Assert.Single(await new ChunkSearchService(db, store.Cache).CandidatesAsync(store.Workspace.Id, query, QueryVector, 30)));
        var lexical = Assert.Single(recorder.Commands, c => c.Text.Contains("FREETEXTTABLE", StringComparison.Ordinal));
        Assert.DoesNotContain(query, lexical.Text); Assert.Contains(query, lexical.Parameters.Values);
        Assert.Contains(store.Workspace.Id, lexical.Parameters.Values); Assert.Contains("WHERE c.[WorkspaceId]", lexical.Text);
        Assert.DoesNotContain("top_n_by_rank", lexical.Text); Assert.Equal(1, await db.Chunks.CountAsync());
        recorder.Commands.Clear();
        Assert.Equal(target.Id, Assert.Single(await new ChunkSearchService(db, store.Cache).CandidatesAsync(store.Workspace.Id, "cystatin", QueryVector, 30)));
        Assert.DoesNotContain(recorder.Commands, c => c.Text.Contains("FREETEXTTABLE", StringComparison.Ordinal));
        Assert.Contains(recorder.Commands, c => c.Text.Contains("FULLTEXTSERVICEPROPERTY", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unexpected_SQL_errors_are_not_silently_converted_to_search_results_or_exposed_to_the_callback()
    {
        await using var store = await RetrievalSqlStore.CreateAsync(); var target = Chunk(store.Document);
        long generation;
        await using (var seed = store.Context())
        {
            seed.Chunks.Add(target); await seed.SaveChangesAsync();
            generation = await seed.Workspaces.Where(w => w.Id == store.Workspace.Id).Select(w => w.SearchGeneration).SingleAsync();
        }
        await store.Cache.GetAsync(store.Workspace.Id, generation, _ => Task.FromResult(new ChunkSearchIndex([
            new(target.Id, target.Text, target.Section, target.EmbeddingModel, target.Embedding!)], fullTextAvailable: true)));
        var recorder = new SqlQueryRecorder { BeforeRead = command =>
        {
            if (command.CommandText.Contains("FREETEXTTABLE", StringComparison.Ordinal))
                command.CommandText = "THROW 51005, N'PRIVATE_DATABASE_DETAIL', 1;";
        } };
        await using var db = store.Context(recorder); var controller = Controller(store, db);
        var result = Assert.IsType<ObjectResult>(await controller.Retrieve(store.Workspace.Id, new RetrieveDto("source")));
        Assert.Equal(502, result.StatusCode); Assert.DoesNotContain("PRIVATE_DATABASE_DETAIL", JsonSerializer.Serialize(result.Value));
        Assert.Equal("no-store", controller.Response.Headers.CacheControl);
    }

    [Fact]
    public async Task FullText_setup_is_idempotent_and_live_search_filters_the_workspace_before_the_candidate_limit()
    {
        await using var store = await RetrievalSqlStore.CreateAsync(); await using var db = store.Context();
        if (!await CanRunFullTextAsync(db)) return;
        var target = Chunk(store.Document, "Retained synthetic quotation", "cystatin", x: 0, y: 1);
        var otherWorkspace = new Workspace { OwnerId = store.Owner.Id, Name = "Other Full-Text corpus" };
        var otherDocument = new Document { WorkspaceId = otherWorkspace.Id, FileName = "other.pdf" };
        db.Workspaces.Add(otherWorkspace); db.Documents.Add(otherDocument); db.Chunks.Add(target);
        db.Chunks.AddRange(Enumerable.Range(0, 125).Select(_ => Chunk(otherDocument, string.Join(' ', Enumerable.Repeat("cystatin", 50)), "cystatin", model: "other-model")));
        db.Chunks.AddRange(Chunk(store.Document, "cystatin", status: "pending"), Chunk(store.Document, "cystatin", status: "rejected"));
        await db.SaveChangesAsync();
        await EnableFullTextAsync(db); var originalKey = await db.Database.SqlQueryRaw<int>("SELECT [unique_index_id] AS [Value] FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(N'dbo.Chunks')").SingleAsync();
        await EnableFullTextAsync(db);
        Assert.Equal(originalKey, await db.Database.SqlQueryRaw<int>("SELECT [unique_index_id] AS [Value] FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(N'dbo.Chunks')").SingleAsync());
        await WaitForFullTextAsync(db, target.Id);
        var recorder = new SqlQueryRecorder(); await using var query = store.Context(recorder);
        Assert.Equal(target.Id, Assert.Single(Ids(await Controller(store, query).DoRetrieve(store.Workspace.Id, "cystatin"))));
        Assert.Single(recorder.Commands, c => c.Text.Contains("FREETEXTTABLE", StringComparison.Ordinal));
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.fulltext_catalogs WHERE [name] = N'ClinicalRagChunks'").SingleAsync());
    }

    [Fact]
    public async Task A_Text_only_FullText_index_uses_Bm25_until_the_setup_script_adds_Section()
    {
        await using var store = await RetrievalSqlStore.CreateAsync(); await using var db = store.Context();
        if (!await CanRunFullTextAsync(db)) return;
        var target = Chunk(store.Document, "Retained quotation", "cystatin", x: 0, y: 1); db.Chunks.Add(target); await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FULLTEXT CATALOG [ExistingChunks];
            CREATE FULLTEXT INDEX ON [dbo].[Chunks] ([Text] LANGUAGE 1033) KEY INDEX [PK_Chunks]
                ON [ExistingChunks] WITH (CHANGE_TRACKING = AUTO, STOPLIST = OFF);
            """);
        var recorder = new SqlQueryRecorder(); await using var query = store.Context(recorder);
        Assert.Equal(target.Id, Assert.Single(Ids(await Controller(store, query).DoRetrieve(store.Workspace.Id, "cystatin"))));
        Assert.DoesNotContain(recorder.Commands, c => c.Text.Contains("FREETEXTTABLE", StringComparison.Ordinal));
        await EnableFullTextAsync(db); await EnableFullTextAsync(db); await WaitForFullTextAsync(db, target.Id);
        store.Cache.Invalidate(store.Workspace.Id); recorder.Commands.Clear();
        Assert.Equal(target.Id, Assert.Single(Ids(await Controller(store, query).DoRetrieve(store.Workspace.Id, "cystatin"))));
        Assert.Single(recorder.Commands, c => c.Text.Contains("FREETEXTTABLE", StringComparison.Ordinal));
        Assert.Equal(2, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.fulltext_index_columns WHERE object_id = OBJECT_ID(N'dbo.Chunks')").SingleAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.fulltext_catalogs WHERE [name] = N'ClinicalRagChunks'").SingleAsync()); // Existing catalog is preserved.
    }

    private async Task<bool> CanRunFullTextAsync(AppDbContext db)
    {
        var installed = await db.Database.SqlQueryRaw<int>("SELECT CAST(COALESCE(FULLTEXTSERVICEPROPERTY('IsFullTextInstalled'), 0) AS int) AS [Value]").SingleAsync();
        if (Environment.GetEnvironmentVariable("CLINICALRAG_TEST_REQUIRE_FULLTEXT") == "1") Assert.Equal(1, installed);
        if (installed == 1) return true;
        var exception = await Assert.ThrowsAsync<SqlException>(() => EnableFullTextAsync(db)); Assert.Equal(51001, exception.Number);
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.fulltext_indexes").SingleAsync());
        output.WriteLine("Full-Text is not installed on this server: verified setup refusal. Set CLINICALRAG_TEST_SQLSERVER and CLINICALRAG_TEST_REQUIRE_FULLTEXT=1 to require live Full-Text execution.");
        return false;
    }

    private static Task<int> EnableFullTextAsync(AppDbContext db) => db.Database.ExecuteSqlRawAsync(File.ReadAllText(RepositoryFile("db", "enable-chunk-fulltext.sql")));

    private static async Task WaitForFullTextAsync(AppDbContext db, Guid id)
    {
        // Full-Text population is asynchronous. Poll only the test database, with
        // a hard deadline; capability alone does not prove rows are searchable.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            var count = await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM FREETEXTTABLE([dbo].[Chunks], ([Text], [Section]), {"cystatin"}) h WHERE h.[KEY] = {id}").SingleAsync(deadline.Token);
            if (count == 1) return;
            await Task.Delay(100, deadline.Token);
        }
    }

    private static string RepositoryFile(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(string.Join('/', parts));
    }
}

internal sealed class SqlQueryRecorder : DbCommandInterceptor
{
    internal sealed record Command(string Text, Dictionary<string, object?> Parameters)
    {
        public string Select => Text[..Math.Max(0, Text.IndexOf("FROM", StringComparison.OrdinalIgnoreCase))];
    }
    public List<Command> Commands { get; } = [];
    public Action<DbCommand>? BeforeRead { get; init; }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Commands.Add(new(command.CommandText, command.Parameters.Cast<DbParameter>().ToDictionary(p => p.ParameterName, p => (object?)p.Value)));
        BeforeRead?.Invoke(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}

/// <summary>Always creates/deletes a GUID-named test database, never the configured application database.</summary>
internal sealed class RetrievalSqlStore : IAsyncDisposable
{
    private readonly string _connectionString = new SqlConnectionStringBuilder
    {
        DataSource = Environment.GetEnvironmentVariable("CLINICALRAG_TEST_SQLSERVER") ?? @"(localdb)\MSSQLLocalDB",
        InitialCatalog = $"ClinicalRagRetrievalTest_{Guid.NewGuid():N}", IntegratedSecurity = true, TrustServerCertificate = true,
        MultipleActiveResultSets = true
    }.ConnectionString;
    public ChunkVectorCache Cache { get; } = new();
    public string ConnectionString => _connectionString;
    public User Owner { get; } = new() { FirebaseUid = $"retrieval-test-{Guid.NewGuid():N}", Email = "synthetic@example.invalid" };
    public Workspace Workspace { get; }
    public Document Document { get; }

    private RetrievalSqlStore()
    {
        Workspace = new() { OwnerId = Owner.Id, Name = "SQL retrieval test" };
        Document = new() { WorkspaceId = Workspace.Id, FileName = "synthetic.pdf", Society = "Original society", GuidelineStatus = "final" };
    }

    public static async Task<RetrievalSqlStore> CreateAsync()
    {
        var store = new RetrievalSqlStore();
        try
        {
            await using var db = store.Context(); await db.Database.MigrateAsync();
            db.Users.Add(store.Owner); db.Workspaces.Add(store.Workspace); db.Documents.Add(store.Document);
            db.ConfigVersions.Add(new ConfigVersion { WorkspaceId = store.Workspace.Id, Version = 1, Status = "published",
                ConfigJson = "{\"retrieval\":{\"topK\":8,\"candidates\":30,\"minRerank\":0}}" });
            await db.SaveChangesAsync(); return store;
        }
        catch { await store.DisposeAsync(); throw; }
    }

    public AppDbContext Context(SqlQueryRecorder? recorder = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(_connectionString);
        if (recorder != null) options.AddInterceptors(recorder);
        return new(options.Options, Cache);
    }

    public async ValueTask DisposeAsync()
    {
        try { await using var db = Context(); await db.Database.EnsureDeletedAsync(); }
        finally { Cache.Dispose(); }
    }
}
