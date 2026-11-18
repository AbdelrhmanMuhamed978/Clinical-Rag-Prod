using System.Text.Json;
using ClinicalRag.Api.Controllers;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using ClinicalRag.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClinicalRag.Tests;

public class FakeColabClient : IColabClient
{
    public List<string> LastSubQueries = new();
    public int GenerateCallCount;
    public Func<List<string>, Task<JsonElement>>? EmbedHandler { get; set; }
    public CancellationToken LastEmbeddingCancellationToken { get; private set; }
    public Func<string, List<string>, Task<JsonElement>>? RerankHandler { get; set; }
    public IColabClient? Transport { get; set; }

    public Task<JsonElement> RetrieveAsync(string query, CancellationToken cancellationToken = default) =>
        Task.FromResult(JsonSerializer.SerializeToElement(new { context = "NOT_FOUND", chunks = Array.Empty<object>() }));

    public Task<JsonElement> GenerateAsync(string prompt, int maxNewTokens = 400, CancellationToken cancellationToken = default)
    {
        GenerateCallCount++;
        return Task.FromResult(JsonSerializer.SerializeToElement(new
        {
            response = "unexpected generation",
            finish_reason = "stop",
            truncated = false
        }));
    }

    public Task<bool> HealthAsync() => Task.FromResult(true);
    public Task<ColabReadiness> ReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ColabReadiness(true, "synthetic-model"));
    public Task<JsonElement> ParsePdfAsync(byte[] bytes, string fileName, CancellationToken cancellationToken = default) =>
        Transport?.ParsePdfAsync(bytes, fileName, cancellationToken) ?? throw new InvalidOperationException("No synthetic document transport configured.");
    public Task<JsonElement?> RunChunkJobAsync(JsonElement body, int? timeoutSeconds = null, CancellationToken cancellationToken = default) =>
        Transport?.RunChunkJobAsync(body, timeoutSeconds, cancellationToken) ?? throw new InvalidOperationException("No synthetic document transport configured.");
    public Task<JsonElement> PostRawAsync(string path, JsonElement body, CancellationToken cancellationToken = default) =>
        Task.FromResult(JsonSerializer.SerializeToElement(new { }));

    public Task<JsonElement> EmbedAsync(List<string> texts, CancellationToken cancellationToken = default)
    {
        LastSubQueries = texts;
        LastEmbeddingCancellationToken = cancellationToken;
        return EmbedHandler != null
            ? EmbedHandler(texts)
            : Task.FromResult(JsonSerializer.SerializeToElement(new
             {
                 model = EmbeddingTestData.Model,
                 dimensions = 3,
                 embeddings = texts.Select(_ => new[] { 1.0, 0.0, 0.0 })
            }));
    }

    public Task<JsonElement> RerankAsync(string query, List<string> passages, CancellationToken cancellationToken = default) =>
        RerankHandler != null
            ? RerankHandler(query, passages)
            : Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                scores = passages.Select(_ => 0.9)
            }));
}

[Collection("Sequential")]
public class AnalysisControllerTests
{
    private sealed record Seeded(
        AppDbContext Db,
        User User,
        Guid WorkspaceId,
        Guid DraftChunkId,
        Guid FinalChunkId,
        Guid UnknownChunkId);

    private static Seeded Seed()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"Retrieve_{Guid.NewGuid():N}").Options;
        var db = new AppDbContext(options);
        var user = db.Users.Add(new User { FirebaseUid = "retrieval-user", Email = "user@example.com" }).Entity;
        var workspace = db.Workspaces.Add(new Workspace { Name = "Clinical", OwnerId = user.Id }).Entity;
        db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = workspace.Id, UserId = user.Id, Role = "owner" });
        db.ConfigVersions.Add(new ConfigVersion
        {
            WorkspaceId = workspace.Id,
            Version = 1,
            Status = "published",
            ConfigJson = ConfigService.DefaultConfig()
        });

        var draftDocument = db.Documents.Add(new Document
        {
            WorkspaceId = workspace.Id,
            FileName = "KDIGO-draft.pdf",
            Society = "KDIGO",
            Year = 2026,
            GuidelineStatus = "draft"
        }).Entity;
        var finalDocument = db.Documents.Add(new Document
        {
            WorkspaceId = workspace.Id,
            FileName = "SCCM-final.pdf",
            Society = "SCCM",
            Year = 2024,
            GuidelineStatus = "final"
        }).Entity;
        var unknownDocument = db.Documents.Add(new Document
        {
            WorkspaceId = workspace.Id,
            FileName = "unknown.pdf",
            Society = "Unknown",
            GuidelineStatus = "unknown"
        }).Entity;
        db.SaveChanges();

        var draft = db.Chunks.Add(new Chunk
        {
            WorkspaceId = workspace.Id, DocumentId = draftDocument.Id, Page = 1,
            Text = "draft recommendation", Status = "embedded"
        }).Entity;
        var final = db.Chunks.Add(new Chunk
        {
            WorkspaceId = workspace.Id, DocumentId = finalDocument.Id, Page = 2,
            Text = "final recommendation", Status = "embedded"
        }).Entity;
        var unknown = db.Chunks.Add(new Chunk
        {
            WorkspaceId = workspace.Id, DocumentId = unknownDocument.Id, Page = 3,
            Text = "unknown recommendation", Status = "embedded"
        }).Entity;
        EmbeddingTestData.Set(draft, 1, 0, 0);
        EmbeddingTestData.Set(final, 0, 1, 0);
        EmbeddingTestData.Set(unknown, 0, 0, 1);
        db.SaveChanges();
        return new Seeded(db, user, workspace.Id, draft.Id, final.Id, unknown.Id);
    }

    private static AnalysisController Controller(Seeded seeded, FakeColabClient colab)
    {
        return new AnalysisController(
            seeded.Db,
            colab,
            new ConfigService(seeded.Db),
            new FakePipelineClient(),
            new FakeCurrentUser(seeded.User),
            new ConfigurationBuilder().Build(),
            NullLogger<AnalysisController>.Instance);
    }

    [Theory]
    [InlineData("{\"retrieval\":{\"minRerank\":0.3,\"topK\":8}}")]
    [InlineData("{}")]
    [InlineData("{\"retrieval\":null}")]
    [InlineData("{\"retrieval\":{\"minRerank\":\"bad\",\"topK\":0,\"candidates\":null}}")]
    public async Task Retrieve_uses_safe_defaults_for_incomplete_legacy_configurations(string configJson)
    {
        var seeded = Seed();
        using var db = seeded.Db;
        db.ConfigVersions.Single().ConfigJson = configJson;
        await db.SaveChangesAsync();

        var result = await Controller(seeded, new FakeColabClient()).DoRetrieve(seeded.WorkspaceId, "focused query");
        var json = JsonSerializer.SerializeToElement(result);

        Assert.NotEmpty(json.GetProperty("chunks").EnumerateArray());
        Assert.Equal(new[] { "focused query" }, json.GetProperty("subQueries").EnumerateArray().Select(q => q.GetString()));
    }

    [Theory]
    [InlineData("10", "35", 10, 35)]
    [InlineData("10.0", "35.0", 10, 35)]
    [InlineData("1e1", "3.5e1", 10, 35)]
    [InlineData("10.5", "35.5", 8, 30)]
    [InlineData("\"10\"", "\"35\"", 8, 30)]
    [InlineData("1e400", "1e400", 8, 30)]
    public async Task Retrieve_honors_schema_integer_numbers_but_defaults_invalid_values(
        string topK, string candidates, int expectedTopK, int expectedCandidates)
    {
        var seeded = Seed();
        using var db = seeded.Db;
        db.ConfigVersions.Single().ConfigJson = $"{{\"retrieval\":{{\"topK\":{topK},\"candidates\":{candidates}}}}}";
        var documentId = db.Chunks.Single(chunk => chunk.Id == seeded.DraftChunkId).DocumentId;
        db.Chunks.AddRange(Enumerable.Range(1, 40).Select(index => new Chunk
        {
            WorkspaceId = seeded.WorkspaceId, DocumentId = documentId, Page = index,
            Text = $"Synthetic guideline passage {index}", Status = "embedded",
            Embedding = EmbeddingVectors.EncodeNormalized([1, 0, 0]), EmbeddingModel = EmbeddingTestData.Model
        }));
        await db.SaveChangesAsync();
        var rerankedCandidates = 0;
        var colab = new FakeColabClient
        {
            RerankHandler = (_, passages) =>
            {
                rerankedCandidates = passages.Count;
                return Task.FromResult(JsonSerializer.SerializeToElement(new { scores = passages.Select(_ => 0.9) }));
            }
        };

        var result = await Controller(seeded, colab).DoRetrieve(seeded.WorkspaceId, "focused query");
        var json = JsonSerializer.SerializeToElement(result);

        Assert.Equal(Math.Min(expectedCandidates, RetrievalLimits.MaxRerankPassages), rerankedCandidates);
        Assert.Equal(expectedTopK, json.GetProperty("chunks").GetArrayLength());
        Assert.Equal(0, colab.GenerateCallCount);
        Assert.Equal(new[] { "focused query" }, colab.LastSubQueries);
    }

    [Fact]
    public async Task Superseded_guideline_is_excluded_until_owner_opts_in()
    {
        var seeded = Seed();
        using var db = seeded.Db;
        var document = db.Documents.Add(new Document
        {
            WorkspaceId = seeded.WorkspaceId, FileName = "superseded.pdf", Society = "Synthetic",
            Title = "Superseded synthetic guideline", Year = 2023, GuidelineStatus = "superseded"
        }).Entity;
        await db.SaveChangesAsync();
        var chunk = db.Chunks.Add(new Chunk
        {
            WorkspaceId = seeded.WorkspaceId, DocumentId = document.Id, Page = 4,
            Text = "superseded recommendation", Status = "embedded"
        }).Entity;
        EmbeddingTestData.Set(chunk, 1, 0, 0);
        await db.SaveChangesAsync();

        var controller = Controller(seeded, new FakeColabClient());
        var before = JsonSerializer.SerializeToElement(await controller.DoRetrieve(seeded.WorkspaceId, "superseded"));
        Assert.DoesNotContain(before.GetProperty("chunks").EnumerateArray(),
            item => item.GetProperty("chunk_id").GetString() == chunk.Id.ToString());

        var workspace = await db.Workspaces.SingleAsync();
        workspace.AllowSupersededGuidelines = true;
        await db.SaveChangesAsync();
        var after = JsonSerializer.SerializeToElement(await controller.DoRetrieve(seeded.WorkspaceId, "superseded"));
        var included = after.GetProperty("chunks").EnumerateArray()
            .Single(item => item.GetProperty("chunk_id").GetString() == chunk.Id.ToString());
        Assert.Equal("superseded", included.GetProperty("status").GetString());
        Assert.True(included.GetProperty("superseded").GetBoolean());
    }

    [Fact]
    public async Task Retrieve_embeds_exact_query_without_free_text_decomposition()
    {
        var seeded = Seed();
        var colab = new FakeColabClient
        {
            EmbedHandler = texts => Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                model = EmbeddingTestData.Model, dimensions = 3,
                embeddings = texts.Select(_ => new[] { 1.0, 0.001, 0.0 })
            })),
            RerankHandler = (query, passages) => Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                scores = passages.Select(text => text.StartsWith("final") ? 0.95 : 0.1)
            }))
        };

        var result = await Controller(seeded, colab).DoRetrieve(seeded.WorkspaceId, "focused query");
        var json = JsonSerializer.SerializeToElement(result);
        var ids = json.GetProperty("chunks").EnumerateArray()
            .Select(item => item.GetProperty("chunk_id").GetString()).ToList();

        Assert.Equal(0, colab.GenerateCallCount);
        Assert.Equal(new[] { "focused query" }, colab.LastSubQueries);
        Assert.Contains(seeded.FinalChunkId.ToString(), ids);
        Assert.DoesNotContain(seeded.DraftChunkId.ToString(), ids);
    }

    [Fact]
    public async Task Draft_and_unknown_documents_are_labelled_draft_from_document_metadata()
    {
        var seeded = Seed();
        var colab = new FakeColabClient
        {
            EmbedHandler = texts => Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                model = EmbeddingTestData.Model, dimensions = 3,
                embeddings = texts.Select(_ => new[] { 1.0, 1.0, 1.0 })
            })),
            RerankHandler = (query, passages) => Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                scores = passages.Select(_ => 0.95)
            }))
        };

        var result = await Controller(seeded, colab).DoRetrieve(seeded.WorkspaceId, "guideline query");
        var json = JsonSerializer.SerializeToElement(result);
        var chunks = json.GetProperty("chunks").EnumerateArray().ToList();
        var draft = chunks.Single(item => item.GetProperty("chunk_id").GetString() == seeded.DraftChunkId.ToString());
        var unknown = chunks.Single(item => item.GetProperty("chunk_id").GetString() == seeded.UnknownChunkId.ToString());

        Assert.Equal("draft", draft.GetProperty("status").GetString());
        Assert.Equal("KDIGO", draft.GetProperty("society").GetString());
        Assert.Equal("2026", draft.GetProperty("year").GetString());
        Assert.Equal("draft", unknown.GetProperty("status").GetString());
        Assert.True(json.GetProperty("unknownGuidelineStatusTreatedAsDraft").GetBoolean());
        Assert.Contains("(DRAFT)", json.GetProperty("context").GetString());
    }
}
