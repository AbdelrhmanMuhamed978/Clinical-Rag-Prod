using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using ClinicalRag.Api.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ClinicalRag.Tests;

public class MigrationTests : IDisposable
{
    private readonly List<string> _databasesToDrop = new();

    public void Dispose()
    {
        foreach (var name in _databasesToDrop)
        {
            try
            {
                var master = new SqlConnectionStringBuilder
                {
                    DataSource = @"(localdb)\MSSQLLocalDB",
                    InitialCatalog = "master",
                    IntegratedSecurity = true,
                    TrustServerCertificate = true
                }.ConnectionString;
                using var cn = new SqlConnection(master);
                cn.Open();
                using var cmd = cn.CreateCommand();
                cmd.CommandText = $@"
IF DB_ID('{name}') IS NOT NULL
BEGIN
    ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [{name}];
END";
                cmd.ExecuteNonQuery();
            }
            catch { /* best-effort cleanup */ }
        }
    }

    private string NewDatabaseName()
    {
        var name = $"ClinicalRagTest_{Guid.NewGuid():N}";
        _databasesToDrop.Add(name);
        return name;
    }

    private AppDbContext CreateContext(string databaseName)
    {
        var cs = new SqlConnectionStringBuilder
        {
            DataSource = @"(localdb)\MSSQLLocalDB",
            InitialCatalog = databaseName,
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            MultipleActiveResultSets = true
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(cs)
            .Options;
        return new AppDbContext(options);
    }

    private static string LongHierarchy(int length)
    {
        var sb = new StringBuilder("Clinical management > ");
        while (sb.Length < length) sb.Append('a');
        return sb.ToString()[..length];
    }

    private static void SeedMinimal(AppDbContext db)
    {
        var user = new User
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            FirebaseUid = "uid-seed",
            Email = "seed@example.com"
        };
        var ws = new Workspace
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Name = "WS",
            OwnerId = user.Id
        };
        var doc = new Document
        {
            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            WorkspaceId = ws.Id,
            FileName = "doc.pdf",
            Status = "uploaded"
        };
        db.Users.Add(user);
        db.Workspaces.Add(ws);
        db.Documents.Add(doc);
        db.SaveChanges();
    }

    // Fresh database: all migrations apply and a 200-char SectionHierarchy round-trips
    // (validates the SectionHierarchy 64 -> 256 fix and the new metadata columns).
    [Fact]
    public async Task Fresh_database_reaches_current_schema_and_accepts_long_section_hierarchy()
    {
        var name = NewDatabaseName();
        using var db = CreateContext(name);
        await db.Database.MigrateAsync();

        SeedMinimal(db);
        var hierarchy = LongHierarchy(200);
        var chunk = new Chunk
        {
            Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            WorkspaceId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            DocumentId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Page = 1,
            Text = "chunk text",
            Status = "pending",
            Type = "clinical",
            Verbatim = true,
            SectionHierarchy = hierarchy,
            ValidationError = null,
            EmbeddingModel = "pubmedbert"
        };
        db.Chunks.Add(chunk);
        await db.SaveChangesAsync();

        var saved = await db.Chunks.AsNoTracking().FirstOrDefaultAsync(c => c.Id == chunk.Id);
        Assert.NotNull(saved);
        Assert.Equal(200, saved.SectionHierarchy!.Length);
        Assert.Equal("clinical", saved.Type);
        Assert.True(saved.Verbatim);
        Assert.Equal("pubmedbert", saved.EmbeddingModel);
        Assert.Contains(await db.Database.GetAppliedMigrationsAsync(), m => m.EndsWith("_RuleReviewAndDrugCatalog"));
        Assert.Empty(await db.Rules.ToListAsync()); // Migrations do not activate or import seed rules.
        Assert.Empty(await db.DrugCatalogs.ToListAsync());
        Assert.Empty(await db.RuleAuditEntries.ToListAsync());
        Assert.Contains(await db.Database.GetAppliedMigrationsAsync(), m => m.EndsWith("_DocumentProcessingState"));
        Assert.Contains(await db.Database.GetAppliedMigrationsAsync(), m => m.EndsWith("_BinaryChunkEmbeddings"));
        Assert.Contains(await db.Database.GetAppliedMigrationsAsync(), m => m.EndsWith("_ColabOperationalConnection"));
        Assert.Empty(await db.ColabConnections.ToListAsync()); // No tunnel/key fallback or automatic administrator bootstrap.
        Assert.Equal(1, (await db.Documents.SingleAsync()).Revision);
    }

    // Existing old database: stamped at the Initial baseline, then ChunkMetadata brings it
    // to the current schema (SectionHierarchy widened to 256 + new columns/indexes).
    [Fact]
    public async Task Old_database_stamped_at_initial_reaches_current_schema_after_chunkmetadata()
    {
        var name = NewDatabaseName();
        using var db = CreateContext(name);
        await db.Database.MigrateAsync("Initial");

        var applied = await db.Database.GetAppliedMigrationsAsync();
        Assert.Contains(applied, m => m.EndsWith("_Initial"));
        Assert.DoesNotContain(applied, m => m.EndsWith("_ChunkMetadata"));

        // Apply the remaining migration (simulates updating an existing database).
        await db.Database.MigrateAsync();

        applied = await db.Database.GetAppliedMigrationsAsync();
        Assert.Contains(applied, m => m.EndsWith("_Initial"));
        Assert.Contains(applied, m => m.EndsWith("_ChunkMetadata"));

        SeedMinimal(db);
        var hierarchy = LongHierarchy(200);
        var chunk = new Chunk
        {
            Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
            WorkspaceId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            DocumentId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Page = 2,
            Text = "chunk text",
            Status = "pending",
            SectionHierarchy = hierarchy
        };
        db.Chunks.Add(chunk);
        await db.SaveChangesAsync();

        var saved = await db.Chunks.AsNoTracking().FirstOrDefaultAsync(c => c.Id == chunk.Id);
        Assert.NotNull(saved);
        Assert.Equal(200, saved.SectionHierarchy!.Length);
    }

    private static readonly Guid WorkspaceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ReviewerId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTime LegacyReviewedAt = new(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
    private const string LegacySources = "[{\"title\":\"Synthetic evidence: اسم\",\"url\":\"https://example.org/source\",\"quote\":\"Preserve this quotation.\",\"provenance\":{\"status\":\"draft\"}}]";

    private static async Task<Guid[]> SeedLegacyRulesAsync(AppDbContext db)
    {
        // The expanded current Document entity must never be inserted against
        // the pre-DocumentProcessingState schema.
        await SeedLegacyBaselineAsync(db);
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        // Use only baseline columns: the current Rule entity cannot be saved against an old schema.
        for (var i = 0; i < ids.Length; i++)
        {
            Guid? reviewer = i == 0 ? ReviewerId : null;
            DateTime? reviewedAt = i == 0 ? LegacyReviewedAt : null;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [Rules] ([Id], [WorkspaceId], [RuleId], [Title], [DrugClasses], [AppliesWhen],
                    [Level], [Message], [Caveat], [Sources], [LinkTerms], [CaseLevel], [ReviewedBy], [ReviewedAt], [CreatedAt], [UpdatedAt])
                VALUES ({ids[i]}, {WorkspaceId}, {$"legacy-{i}"}, {$"Legacy rule {i}"}, {"[\"nsaid\"]"},
                    {"{\"any\":[{\"field\":\"creatinine\",\"op\":\">=\",\"value\":1.5}]}"},
                    {"individual_review"}, {"Test only."}, {"Screening, not a diagnosis."}, {LegacySources},
                    {"kidney|\\bAKI\\b"}, {false}, {reviewer}, {reviewedAt}, {LegacyReviewedAt}, {LegacyReviewedAt});
                """);
        }
        return ids;
    }

    private static async Task SeedLegacyBaselineAsync(AppDbContext db)
    {
        db.Users.Add(new User { Id = ReviewerId, FirebaseUid = "uid-seed", Email = "seed@example.com" });
        await db.SaveChangesAsync();
        // Current Workspace binding columns do not exist at the old baseline.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [Workspaces] ([Id], [Name], [OwnerId], [CreatedAt])
            VALUES ({WorkspaceId}, {"WS"}, {ReviewerId}, {LegacyReviewedAt});
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [Documents] ([Id], [WorkspaceId], [FileName], [Status], [CreatedAt], [UpdatedAt])
            VALUES ({Guid.Parse("22222222-2222-2222-2222-222222222222")}, {WorkspaceId}, {"doc.pdf"}, {"uploaded"}, {LegacyReviewedAt}, {LegacyReviewedAt});
            """);
    }

    private static async Task AssertLegacyResetAsync(AppDbContext db, Guid[] ids)
    {
        var rules = await db.Rules.AsNoTracking().OrderBy(r => r.RuleId).ToListAsync();
        Assert.Equal(2, rules.Count);
        var audit = await db.RuleAuditEntries.AsNoTracking().OrderBy(a => a.RuleKey).ToListAsync();
        Assert.Equal(2, audit.Count);
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            Assert.Equal(ids[i], rule.Id);
            Assert.Equal("draft", rule.Status); Assert.False(rule.Enabled); Assert.Equal(1, rule.Revision);
            Assert.Null(rule.ReviewedBy); Assert.Null(rule.ReviewedAt); Assert.Null(rule.ReviewedCatalogRevision);
            Assert.Null(rule.CreatedBy); Assert.Null(rule.UpdatedBy);
            Assert.Equal(LegacySources, rule.Sources);
            Assert.Equal(LegacyReviewedAt, rule.CreatedAt);
            Assert.True(rule.UpdatedAt > LegacyReviewedAt);
            var entry = audit[i];
            Assert.Equal(rule.Id, entry.RuleId); Assert.Equal(WorkspaceId, entry.WorkspaceId);
            Assert.Equal("legacy_review_reset", entry.Action); Assert.Null(entry.ActorId);
            var before = JsonDocument.Parse(entry.BeforeJson!).RootElement;
            Assert.Equal(LegacySources, before.GetProperty("sources").GetString());
            Assert.Equal(rule.LinkTerms, before.GetProperty("linkTerms").GetString());
            Assert.Equal(LegacyReviewedAt, before.GetProperty("updatedAt").GetDateTime());
            if (i == 0)
            {
                Assert.Equal(ReviewerId, before.GetProperty("reviewedBy").GetGuid());
                Assert.Equal(LegacyReviewedAt, before.GetProperty("reviewedAt").GetDateTime());
            }
            else Assert.Equal(JsonValueKind.Null, before.GetProperty("reviewedAt").ValueKind);
            var after = JsonDocument.Parse(entry.AfterJson!).RootElement;
            Assert.Equal("draft", after.GetProperty("status").GetString());
            Assert.False(after.GetProperty("enabled").GetBoolean());
            Assert.Equal(JsonValueKind.Null, after.GetProperty("reviewedBy").ValueKind);
            Assert.Equal(rule.UpdatedAt, after.GetProperty("updatedAt").GetDateTime());
            Assert.Equal(rule.UpdatedAt, entry.CreatedAt);
        }
        Assert.Empty(await db.DrugCatalogs.ToListAsync());
    }

    [Fact]
    public async Task Legacy_review_is_exported_to_audit_and_all_legacy_rules_become_disabled_drafts_only_once()
    {
        using var db = CreateContext(NewDatabaseName());
        await db.Database.MigrateAsync("ChunkMetadata");
        var ids = await SeedLegacyRulesAsync(db);
        await db.Database.MigrateAsync();
        await AssertLegacyResetAsync(db, ids);
        var before = (await db.RuleAuditEntries.AsNoTracking().ToListAsync()).ToDictionary(a => a.Id, a => a.AfterJson);
        await db.Database.MigrateAsync();
        await AssertLegacyResetAsync(db, ids);
        Assert.Equal(before.OrderBy(p => p.Key), (await db.RuleAuditEntries.AsNoTracking().ToListAsync()).ToDictionary(a => a.Id, a => a.AfterJson).OrderBy(p => p.Key));
    }

    private static string SchemaPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "db", "schema.sql");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Could not locate db/schema.sql.");
    }

    private static async Task ApplySchemaScriptAsync(AppDbContext db)
    {
        var script = await File.ReadAllTextAsync(SchemaPath());
        await db.Database.OpenConnectionAsync();
        foreach (var batch in Regex.Split(script, @"^[ \t]*GO[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
            if (!string.IsNullOrWhiteSpace(batch)) await db.Database.ExecuteSqlRawAsync(batch);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Checked_in_idempotent_schema_supports_fresh_and_legacy_databases_and_repeat_application(bool legacy)
    {
        using var db = CreateContext(NewDatabaseName());
        Guid[] ids = [];
        if (legacy)
        {
            await db.Database.MigrateAsync("ChunkMetadata");
            ids = await SeedLegacyRulesAsync(db);
        }
        else await db.GetService<IRelationalDatabaseCreator>().CreateAsync();
        await ApplySchemaScriptAsync(db);
        Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
        if (legacy) await AssertLegacyResetAsync(db, ids);
        else
        {
            Assert.Empty(await db.Rules.ToListAsync());
            Assert.Empty(await db.RuleAuditEntries.ToListAsync());
        }
        var audits = await db.RuleAuditEntries.AsNoTracking().ToListAsync();
        await ApplySchemaScriptAsync(db);
        Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(audits.Select(a => a.Id).Order(), (await db.RuleAuditEntries.ToListAsync()).Select(a => a.Id).Order());
        if (legacy) await AssertLegacyResetAsync(db, ids);
    }

    [Fact]
    public void Current_model_matches_frozen_migration_metadata_and_checked_in_schema()
    {
        using var db = CreateContext("MetadataOnly");
        Assert.False(db.Database.HasPendingModelChanges());
        var assembly = db.GetService<IMigrationsAssembly>();
        var migration = assembly.CreateMigration(assembly.Migrations.Last().Value, db.Database.ProviderName!);
        var targetRule = migration.TargetModel.FindEntityType(typeof(Rule))!;
        Assert.True(targetRule.FindProperty(nameof(Rule.Revision))!.IsConcurrencyToken);
        Assert.True(migration.TargetModel.FindEntityType(typeof(WorkspaceMember))!.FindProperty(nameof(WorkspaceMember.Role))!.IsConcurrencyToken);
        Assert.Equal("draft", targetRule.FindProperty(nameof(Rule.Status))!.GetDefaultValue());
        var targetDocument = migration.TargetModel.FindEntityType(typeof(Document))!;
        Assert.True(targetDocument.FindProperty(nameof(Document.Revision))!.IsConcurrencyToken);
        Assert.Equal(1, targetDocument.FindProperty(nameof(Document.Revision))!.GetDefaultValue());
        Assert.Equal(512, targetDocument.FindProperty(nameof(Document.FailureReason))!.GetMaxLength());
        Assert.Equal(0, targetDocument.FindProperty(nameof(Document.EmbeddingCompleted))!.GetDefaultValue());
        var targetChunk = migration.TargetModel.FindEntityType(typeof(Chunk))!;
        Assert.Equal(typeof(byte[]), targetChunk.FindProperty(nameof(Chunk.Embedding))!.ClrType);
        Assert.Equal("varbinary(max)", targetChunk.FindProperty(nameof(Chunk.Embedding))!.GetColumnType());
        Assert.Equal(typeof(string), targetChunk.FindProperty(nameof(Chunk.LegacyEmbeddingJson))!.ClrType);
        Assert.Equal("Latin1_General_100_BIN2", targetChunk.FindProperty(nameof(Chunk.EmbeddingModel))!.GetCollation());
        var targetWorkspace = migration.TargetModel.FindEntityType(typeof(Workspace))!;
        Assert.True(targetWorkspace.FindProperty(nameof(Workspace.EmbeddingModel))!.IsConcurrencyToken);
        Assert.True(targetWorkspace.FindProperty(nameof(Workspace.EmbeddingDimensions))!.IsConcurrencyToken);
        Assert.True(targetWorkspace.FindProperty(nameof(Workspace.MembershipRevision))!.IsConcurrencyToken);
        Assert.Equal(1, targetWorkspace.FindProperty(nameof(Workspace.MembershipRevision))!.GetDefaultValue());
        Assert.True(targetWorkspace.FindProperty(nameof(Workspace.DeletedAt))!.IsConcurrencyToken);
        Assert.True(migration.TargetModel.FindEntityType(typeof(WorkspaceInvitation))!.FindProperty(nameof(WorkspaceInvitation.Role))!.IsConcurrencyToken);
        Assert.Equal(512, migration.TargetModel.FindEntityType(typeof(PendingUploadDeletion))!.FindProperty(nameof(PendingUploadDeletion.StoragePath))!.GetMaxLength());
        Assert.Equal("Latin1_General_100_BIN2", targetWorkspace.FindProperty(nameof(Workspace.EmbeddingModel))!.GetCollation());
        var targetConnection = migration.TargetModel.FindEntityType(typeof(ColabConnectionSetting))!;
        Assert.True(targetConnection.FindProperty(nameof(ColabConnectionSetting.Revision))!.IsConcurrencyToken);
        Assert.Equal(Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never, targetConnection.FindProperty(nameof(ColabConnectionSetting.Id))!.ValueGenerated);
        Assert.Equal(2048, targetConnection.FindProperty(nameof(ColabConnectionSetting.BaseUrl))!.GetMaxLength());
        Assert.Equal(8192, targetConnection.FindProperty(nameof(ColabConnectionSetting.ProtectedApiKey))!.GetMaxLength());
        var generated = db.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        static string Normalize(string value) => value.Replace("\r\n", "\n").Trim('\uFEFF', ' ', '\r', '\n');
        Assert.Equal(Normalize(generated), Normalize(File.ReadAllText(SchemaPath())));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Document_upgrade_preserves_checkpoints_and_metadata_and_never_invents_indexing(bool schemaScript)
    {
        using var db = CreateContext(NewDatabaseName());
        await db.Database.MigrateAsync("RuleReviewAndDrugCatalog");
        await SeedLegacyBaselineAsync(db);
        var fixtures = new[]
        {
            (Name: "parsing", Status: "parsing", Chunking: "not_started", Expected: "failed"),
            (Name: "chunking", Status: "review", Chunking: "in_progress", Expected: "failed"),
            (Name: "failed", Status: "failed", Chunking: "not_started", Expected: "failed"),
            (Name: "forged", Status: "indexed", Chunking: "partial", Expected: "review"),
            (Name: "pending", Status: "indexed", Chunking: "partial", Expected: "review"),
            (Name: "missing-vector", Status: "indexed", Chunking: "partial", Expected: "review"),
            (Name: "accepted", Status: "indexed", Chunking: "partial", Expected: "review"),
            (Name: "unknown", Status: "indexed", Chunking: "partial", Expected: "review"),
            (Name: "indexed", Status: "indexed", Chunking: "partial", Expected: "review"), // Binary upgrade requires real re-embedding.
            (Name: "parsed", Status: "parsed", Chunking: "not_started", Expected: "parsed")
        };
        var ids = new Dictionary<string, Guid>();
        foreach (var fixture in fixtures)
        {
            var id = ids[fixture.Name] = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [Documents] ([Id], [WorkspaceId], [FileName], [Status], [ParsedText], [PageCount],
                    [ChunkedPagesCount], [ChunkedPageNumbers], [ChunkingStatus], [Society], [Year], [GuidelineStatus], [CreatedAt], [UpdatedAt])
                VALUES ({id}, {WorkspaceId}, {$"{fixture.Name}.pdf"}, {fixture.Status}, {"--- Page 1 ---\nRetained source words."}, {10},
                    {2}, {"[1,3]"}, {fixture.Chunking}, {"Synthetic society"}, {2024}, {"draft"}, {LegacyReviewedAt}, {LegacyReviewedAt});
                """);
            if (fixture.Name is "pending" or "accepted" or "unknown" or "indexed")
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO [Chunks] ([Id], [WorkspaceId], [DocumentId], [Page], [Text], [Status], [Embedding], [CreatedAt])
                    VALUES ({Guid.NewGuid()}, {WorkspaceId}, {id}, {1}, {"Retained evidence"}, {"embedded"}, {"[1,0]"}, {LegacyReviewedAt});
                    """);
            if (fixture.Name is "pending" or "accepted" or "unknown" or "missing-vector" or "indexed")
            {
                var status = fixture.Name switch { "pending" => "pending", "accepted" => "accepted", "unknown" => "legacy_unknown", "missing-vector" => "embedded", _ => "rejected" };
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO [Chunks] ([Id], [WorkspaceId], [DocumentId], [Page], [Text], [Status], [CreatedAt])
                    VALUES ({Guid.NewGuid()}, {WorkspaceId}, {id}, {3}, {"Retained review"}, {status}, {LegacyReviewedAt});
                    """);
            }
        }
        await db.SaveChangesAsync();
        if (schemaScript) await ApplySchemaScriptAsync(db); else await db.Database.MigrateAsync();
        db.ChangeTracker.Clear();
        var documents = await db.Documents.AsNoTracking().Where(d => d.Id != Guid.Parse("22222222-2222-2222-2222-222222222222")).ToListAsync();
        foreach (var fixture in fixtures)
        {
            var document = documents.Single(d => d.Id == ids[fixture.Name]);
            Assert.Equal(fixture.Expected, document.Status);
            Assert.Equal("[1,3]", document.ChunkedPageNumbers); Assert.Equal(2, document.ChunkedPagesCount);
            Assert.Equal("--- Page 1 ---\nRetained source words.", document.ParsedText);
            Assert.Equal("Synthetic society", document.Society); Assert.Equal(2024, document.Year); Assert.Equal("draft", document.GuidelineStatus);
            Assert.Equal(fixture.Name == "indexed" ? 2 : 1, document.Revision); Assert.Null(document.ProcessingOperation);
            if (fixture.Expected == "failed") { Assert.NotNull(document.FailureReason); Assert.NotNull(document.FailureStage); }
            else { Assert.Null(document.FailureReason); Assert.Null(document.FailureStage); }
        }
        Assert.Equal("failed", documents.Single(d => d.Id == ids["chunking"]).ChunkingStatus);
        Assert.Equal("partial", documents.Single(d => d.Id == ids["indexed"]).ChunkingStatus); // Page selection is preserved, not treated as failed chunking.
        var archived = await db.Chunks.AsNoTracking().Where(c => c.DocumentId == ids["indexed"]).SingleAsync(c => c.Page == 1);
        Assert.Equal("[1,0]", archived.LegacyEmbeddingJson); Assert.Null(archived.Embedding); Assert.Equal("accepted", archived.Status);
        var before = JsonSerializer.Serialize(documents.OrderBy(d => d.Id));
        if (schemaScript) await ApplySchemaScriptAsync(db); else await db.Database.MigrateAsync();
        Assert.Equal(before, JsonSerializer.Serialize((await db.Documents.AsNoTracking().Where(d => d.Id != Guid.Parse("22222222-2222-2222-2222-222222222222")).ToListAsync()).OrderBy(d => d.Id)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Binary_upgrade_archives_all_legacy_values_verbatim_and_releases_interrupted_embedding_without_inventing_vectors(bool schemaScript)
    {
        using var db = CreateContext(NewDatabaseName());
        await db.Database.MigrateAsync("DocumentProcessingState");
        await SeedLegacyBaselineAsync(db);
        var documentId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var interruptedId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE [Documents] SET [Status] = N'indexed', [Revision] = 7, [ParsedText] = {"Retained source text"},
                [PageCount] = 10, [ChunkedPagesCount] = 2, [ChunkedPageNumbers] = {"[1,3]"}, [ChunkingStatus] = N'partial'
            WHERE [Id] = {documentId};
            INSERT INTO [Documents] ([Id], [WorkspaceId], [FileName], [Status], [ProcessingOperation], [Revision], [CreatedAt], [UpdatedAt])
            VALUES ({interruptedId}, {WorkspaceId}, {"interrupted.pdf"}, {"review"}, {"embedding"}, {9}, {LegacyReviewedAt}, {LegacyReviewedAt});
            """);
        var fixtures = new[]
        {
            (Id: Guid.NewGuid(), Status: "embedded", Vector: (string?)"  [3.0, 4e0]  "),
            (Id: Guid.NewGuid(), Status: "embedded", Vector: (string?)null),
            (Id: Guid.NewGuid(), Status: "accepted", Vector: (string?)""),
            (Id: Guid.NewGuid(), Status: "pending", Vector: (string?)"not JSON: اسم 😀\n"),
            (Id: Guid.NewGuid(), Status: "rejected", Vector: (string?)"[1e400, NaN]")
        };
        for (var i = 0; i < fixtures.Length; i++)
        {
            var fixture = fixtures[i];
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [Chunks] ([Id], [WorkspaceId], [DocumentId], [Page], [Text], [Section], [SectionHierarchy],
                    [Status], [Embedding], [EmbeddingModel], [Metadata], [ReviewedBy], [ReviewedAt], [CreatedAt])
                VALUES ({fixture.Id}, {WorkspaceId}, {documentId}, {i + 1}, {"archiveonlyneedle retained quotation"}, {"Retained section"},
                    {LongHierarchy(200)}, {fixture.Status}, {fixture.Vector}, {"LegacyModel"}, {LegacySources}, {ReviewerId}, {LegacyReviewedAt}, {LegacyReviewedAt});
                """);
        }

        if (schemaScript) await ApplySchemaScriptAsync(db); else await db.Database.MigrateAsync();
        var chunks = await db.Chunks.AsNoTracking().OrderBy(c => c.Page).ToListAsync();
        Assert.Equal(fixtures.Length, chunks.Count);
        for (var i = 0; i < fixtures.Length; i++)
        {
            var saved = chunks[i]; var fixture = fixtures[i];
            Assert.Equal(fixture.Id, saved.Id); Assert.Equal(fixture.Vector, saved.LegacyEmbeddingJson); Assert.Null(saved.Embedding);
            Assert.Equal(fixture.Status == "embedded" ? "accepted" : fixture.Status, saved.Status);
            Assert.Equal("LegacyModel", saved.EmbeddingModel); Assert.Equal(LegacySources, saved.Metadata);
            Assert.Equal("archiveonlyneedle retained quotation", saved.Text); Assert.Equal("Retained section", saved.Section);
            Assert.Equal(LongHierarchy(200), saved.SectionHierarchy); Assert.Equal(ReviewerId, saved.ReviewedBy);
            Assert.Equal(LegacyReviewedAt, saved.ReviewedAt); Assert.Equal(LegacyReviewedAt, saved.CreatedAt);
        }
        var document = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == documentId);
        Assert.Equal("review", document.Status); Assert.Equal(8, document.Revision);
        Assert.Equal("Retained source text", document.ParsedText); Assert.Equal("[1,3]", document.ChunkedPageNumbers);
        Assert.Null(document.EmbeddingTotal); Assert.Equal(0, document.EmbeddingCompleted); Assert.Null(document.EmbeddingBatchSize);
        var interrupted = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == interruptedId);
        Assert.Equal("failed", interrupted.Status); Assert.Null(interrupted.ProcessingOperation); Assert.Equal(10, interrupted.Revision);
        Assert.Equal("embedding", interrupted.FailureStage); Assert.Contains("Retry Embed", interrupted.FailureReason);
        var workspace = await db.Workspaces.AsNoTracking().SingleAsync();
        Assert.Null(workspace.EmbeddingModel); Assert.Null(workspace.EmbeddingDimensions);
        var query = EmbeddingVectors.Parse(EmbeddingTestData.Result(1, EmbeddingTestData.Model, 1, 0), 1);
        Assert.Empty(await new ChunkSearchService(db, db.VectorCache).CandidatesAsync(WorkspaceId, "archiveonlyneedle", query, 30));

        if (schemaScript) await ApplySchemaScriptAsync(db); else await db.Database.MigrateAsync();
        Assert.Equal(fixtures.Select(f => f.Vector), (await db.Chunks.AsNoTracking().OrderBy(c => c.Page).ToListAsync()).Select(c => c.LegacyEmbeddingJson));
        Assert.Equal(8, (await db.Documents.AsNoTracking().SingleAsync(d => d.Id == documentId)).Revision);
        Assert.Equal(10, (await db.Documents.AsNoTracking().SingleAsync(d => d.Id == interruptedId)).Revision);
    }

    [Fact]
    public async Task Binary_vectors_round_trip_as_float32_and_an_explicit_reembed_clears_the_superseded_archive()
    {
        using var db = CreateContext(NewDatabaseName()); await db.Database.MigrateAsync(); SeedMinimal(db);
        var bytes = EmbeddingVectors.EncodeNormalized([3, 4]);
        var chunk = new Chunk { WorkspaceId = WorkspaceId, DocumentId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Page = 1, Text = "Retained evidence", Status = "accepted", Embedding = bytes, EmbeddingModel = EmbeddingTestData.Model,
            LegacyEmbeddingJson = "[1,0]" };
        db.Chunks.Add(chunk); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var saved = await db.Chunks.AsNoTracking().SingleAsync();
        Assert.Equal(bytes, saved.Embedding); Assert.Equal(new[] { .6f, .8f }, EmbeddingVectors.ReadNormalized(saved.Embedding!));
        Assert.Equal(8L, await db.Database.SqlQuery<long>($"SELECT DATALENGTH([Embedding]) AS [Value] FROM [Chunks] WHERE [Id] = {chunk.Id}").SingleAsync());
        var json = JsonSerializer.SerializeToElement(saved);
        Assert.False(json.TryGetProperty("Embedding", out _)); Assert.False(json.TryGetProperty("LegacyEmbeddingJson", out _));
        var client = new FakeColabClient { EmbedHandler = texts => Task.FromResult(EmbeddingTestData.Result(texts.Count, EmbeddingTestData.Model, 1, 0)) };
        var service = new DocumentEmbeddingService(db, new EmbeddingBatchClient(client, new ConfigurationBuilder().Build()));
        Assert.Equal(200, (await service.EmbedAsync(WorkspaceId, chunk.DocumentId, CancellationToken.None)).StatusCode);
        db.ChangeTracker.Clear(); saved = await db.Chunks.AsNoTracking().SingleAsync();
        Assert.Null(saved.LegacyEmbeddingJson); Assert.Equal("embedded", saved.Status);
        Assert.Equal(EmbeddingVectors.EncodeNormalized([1, 0]), saved.Embedding);
    }

    [Fact]
    public async Task Empty_management_schema_can_downgrade_and_upgrade_without_changing_existing_workspace_data()
    {
        using var db = CreateContext(NewDatabaseName()); await db.Database.MigrateAsync(); SeedMinimal(db);
        await db.Database.MigrateAsync("TestRunHarness");
        Assert.DoesNotContain(await db.Database.GetAppliedMigrationsAsync(), m => m.EndsWith("_WorkspaceAdministration"));
        Assert.Equal("WS", await db.Database.SqlQuery<string>($"SELECT [Name] AS [Value] FROM [Workspaces] WHERE [Id] = {WorkspaceId}").SingleAsync());
        await db.Database.MigrateAsync();
        db.ChangeTracker.Clear(); Assert.Null((await db.Workspaces.SingleAsync()).DeletedAt);
        Assert.Equal(1, (await db.Workspaces.SingleAsync()).MembershipRevision);
        Assert.Empty(await db.WorkspaceInvitations.ToListAsync()); Assert.Empty(await db.PendingUploadDeletions.ToListAsync());
    }

    [Theory]
    [InlineData("tombstone")]
    [InlineData("invitation")]
    [InlineData("cleanup")]
    public async Task Management_downgrade_refuses_to_discard_each_durable_record_and_preserves_schema_and_data(string record)
    {
        using var db = CreateContext(NewDatabaseName()); await db.Database.MigrateAsync(); SeedMinimal(db);
        if (record == "tombstone") { var workspace = await db.Workspaces.SingleAsync(); workspace.DeletedAt = LegacyReviewedAt; workspace.DeletedBy = ReviewerId; }
        if (record == "invitation") db.WorkspaceInvitations.Add(new() { WorkspaceId = WorkspaceId, Email = "synthetic-invite@example.org", Role = "owner", InvitedBy = ReviewerId });
        if (record == "cleanup") db.PendingUploadDeletions.Add(new() { StoragePath = "synthetic-pending.pdf", LastAttemptAt = LegacyReviewedAt });
        await db.SaveChangesAsync();
        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        var exception = await Assert.ThrowsAsync<SqlException>(() => db.Database.MigrateAsync("TestRunHarness"));
        Assert.Equal(51002, exception.Number);
        Assert.Equal(applied, await db.Database.GetAppliedMigrationsAsync());
        db.ChangeTracker.Clear();
        Assert.Equal(record == "tombstone" ? LegacyReviewedAt : (DateTime?)null, (await db.Workspaces.SingleAsync()).DeletedAt);
        Assert.Equal(record == "invitation" ? 1 : 0, await db.WorkspaceInvitations.CountAsync());
        Assert.Equal(record == "cleanup" ? 1 : 0, await db.PendingUploadDeletions.CountAsync());
        Assert.Single(await db.Documents.ToListAsync());
    }

    [Fact]
    public async Task Downgrade_refuses_to_discard_new_binary_vectors_and_leaves_the_schema_and_data_unchanged()
    {
        using var db = CreateContext(NewDatabaseName()); await db.Database.MigrateAsync(); SeedMinimal(db);
        var vector = EmbeddingVectors.EncodeNormalized([3, 4]);
        db.Chunks.Add(new Chunk { WorkspaceId = WorkspaceId, DocumentId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Text = "Retained binary evidence", Page = 1, Status = "embedded", Embedding = vector, EmbeddingModel = EmbeddingTestData.Model });
        await db.SaveChangesAsync();
        var exception = await Assert.ThrowsAsync<SqlException>(() => db.Database.MigrateAsync("DocumentProcessingState"));
        Assert.Equal(51000, exception.Number);
        // ColabOperationalConnection down-committed before BinaryChunkEmbeddings down threw, so five remain applied.
        // Binary vectors and all other data are unchanged.
        Assert.Equal(5, (await db.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Equal(vector, (await db.Chunks.AsNoTracking().SingleAsync()).Embedding);
        Assert.Equal("embedded", (await db.Chunks.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Downgrade_without_new_vectors_restores_the_exact_legacy_column_but_never_reasserts_indexing()
    {
        using var db = CreateContext(NewDatabaseName()); await db.Database.MigrateAsync("DocumentProcessingState");
        await SeedLegacyBaselineAsync(db); var id = Guid.NewGuid(); const string legacy = "  [3.0,4e0]  ";
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [Chunks] ([Id], [WorkspaceId], [DocumentId], [Page], [Text], [Status], [Embedding], [CreatedAt])
            VALUES ({id}, {WorkspaceId}, {Guid.Parse("22222222-2222-2222-2222-222222222222")}, {1}, {"Retained source"}, {"embedded"}, {legacy}, {LegacyReviewedAt});
            """);
        await db.Database.MigrateAsync(); await db.Database.MigrateAsync("DocumentProcessingState");
        Assert.Equal(4, (await db.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Equal(legacy, await db.Database.SqlQuery<string>($"SELECT [Embedding] AS [Value] FROM [Chunks] WHERE [Id] = {id}").SingleAsync());
        Assert.Equal("accepted", await db.Database.SqlQuery<string>($"SELECT [Status] AS [Value] FROM [Chunks] WHERE [Id] = {id}").SingleAsync());
        await db.Database.MigrateAsync();
        var saved = await db.Chunks.AsNoTracking().SingleAsync(); Assert.Equal(legacy, saved.LegacyEmbeddingJson); Assert.Null(saved.Embedding);
    }
}
