using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ClinicalRag.Api.Attributes;
using ClinicalRag.Api.Controllers;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using ClinicalRag.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace ClinicalRag.Tests;

/// <summary>
/// Reflection keeps the write-path matrix closed: a new mutating action in these
/// workspace-scoped controllers fails the coverage test until it is exercised.
/// </summary>
[Collection("Sequential")]
public class WritePathAuthorizationMatrixTests : IClassFixture<WritePathAuthorizationMatrixTests.MatrixFactory>
{
    private sealed record Fixture(
        Guid WorkspaceId,
        Guid ParsedDocumentId,
        Guid UploadedDocumentId,
        Guid StatusDocumentId,
        Guid ChunkId,
        Guid Chunk2Id,
        Guid DraftRuleId,
        Guid InReviewRuleId,
        Guid ApprovedRuleId,
        Guid RetiredRuleId,
        Guid TestCaseId,
        Guid PublishDraftId,
        Guid DiscardDraftId,
        Guid InvitationId,
        Guid EditorId,
        Guid OwnerAnalysisId,
        Guid ViewerAnalysisId,
        int CatalogRevision,
        string WorkspaceName);

    private sealed record RouteCase(
        string Key,
        HttpMethod Method,
        Func<Fixture, string> Path,
        Func<Fixture, HttpContent?> Content,
        WorkspaceRole MinimumRole,
        bool ViewerAllowed = false,
        Func<Fixture, string>? ViewerPath = null);

    public sealed class MatrixFactory : CustomWebApplicationFactory
    {
        internal FakeConfigTestRuns Runs { get; } = new();
        public string Uploads => _uploads;
        private readonly string _uploads = Path.Combine(Path.GetTempPath(), $"clinical-rag-matrix-{Guid.NewGuid():N}");
        private readonly string? _temporarySeedFile;

        public MatrixFactory()
        {
            var seed = Path.Combine(Directory.GetCurrentDirectory(), "tests.json");
            if (!File.Exists(seed))
            {
                File.WriteAllText(seed, "{\"cases\":[]}");
                _temporarySeedFile = seed;
            }
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(_uploads);
            builder.UseSetting("Documents:UploadsDirectory", _uploads);
            builder.UseSetting("Documents:AutomaticParsing", "false");
            builder.UseSetting("Documents:AutomaticFileCleanup", "false");
            builder.UseSetting("AbuseLimits:RequestsPerWindow", "1000");
            builder.UseSetting("AbuseLimits:Concurrency", "100");
            builder.UseSetting("Tests:MaxDurationSeconds", "30");
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPipelineClient>();
                services.AddSingleton<IPipelineClient, MatrixPipelineClient>();
                services.RemoveAll<IColabClient>();
                services.AddSingleton<IColabClient, MatrixColabClient>();
                services.RemoveAll<IConfigTestRunProvider>();
                services.AddSingleton<IConfigTestRunProvider>(Runs);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing) return;
            try { Directory.Delete(_uploads, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (_temporarySeedFile != null)
                try { File.Delete(_temporarySeedFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class MatrixPipelineClient : IPipelineClient
    {
        private static readonly JsonElement Result = JsonSerializer.SerializeToElement(new
        {
            plan_source = "single_question",
            version = "synthetic",
            disclaimer = "Synthetic verification fixture only.",
            warnings = Array.Empty<string>(),
            not_covered = Array.Empty<string>(),
            problems = Array.Empty<object>()
        });

        public Task<JsonElement> AnalyzeAsync(PipelineAnalyzeRequest request, string? authorization,
            CancellationToken cancellationToken = default) => Task.FromResult(Result);

        public async IAsyncEnumerable<PipelineStreamEvent> StreamAnalyzeAsync(PipelineAnalyzeRequest request,
            string? authorization, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new PipelineStreamEvent("result", Result);
        }
    }

    private sealed class MatrixColabClient : IColabClient
    {
        private static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });
        private static readonly JsonElement Parsed = JsonSerializer.SerializeToElement(new
        {
            text = "--- Page 1 ---\nSynthetic source text.",
            pageCount = 1
        });
        private static readonly JsonElement Chunks = JsonSerializer.SerializeToElement(new
        {
            chunks = new[] { new { page = 1, text = "Synthetic chunk." } }
        });
        private static readonly JsonElement Embedded = JsonSerializer.SerializeToElement(new
        {
            model = "synthetic-embedding",
            dimensions = 2,
            embeddings = new[] { new[] { 1.0, 0.0 } }
        });

        public Task<JsonElement> RetrieveAsync(string query, CancellationToken cancellationToken = default) => Task.FromResult(Empty);
        public Task<JsonElement> GenerateAsync(string prompt, int maxNewTokens = 400, CancellationToken cancellationToken = default) => Task.FromResult(Empty);
        public Task<bool> HealthAsync() => Task.FromResult(true);
        public Task<ColabReadiness> ReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ColabReadiness(true));
        public Task<JsonElement> PostRawAsync(string path, JsonElement body, CancellationToken cancellationToken = default) => Task.FromResult(Empty);
        public Task<JsonElement> EmbedAsync(List<string> texts, CancellationToken cancellationToken = default) => Task.FromResult(Embedded);
        public Task<JsonElement> RerankAsync(string query, List<string> passages, CancellationToken cancellationToken = default) => Task.FromResult(Empty);
        public Task<JsonElement> ParsePdfAsync(byte[] bytes, string fileName, CancellationToken cancellationToken = default) => Task.FromResult(Parsed);
        public Task<JsonElement?> RunChunkJobAsync(JsonElement body, int? timeoutSeconds = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<JsonElement?>(Chunks);
    }

    private static readonly IReadOnlyDictionary<string, RouteCase> Routes = BuildRoutes();

    private static IReadOnlyDictionary<string, RouteCase> BuildRoutes()
    {
        HttpContent Json(object value) => JsonContent.Create(value);

        RouteCase Add(string key, HttpMethod method, Func<Fixture, string> path, Func<Fixture, HttpContent?> content,
            WorkspaceRole role, bool viewerAllowed = false, Func<Fixture, string>? viewerPath = null) =>
            new(key, method, path, content, role, viewerAllowed, viewerPath);

        return new[]
        {
            Add("DocumentsController.Upload", HttpMethod.Post,
                f => $"/api/workspaces/{f.WorkspaceId}/documents",
                _ =>
                {
                    var form = new MultipartFormDataContent();
                    var file = new ByteArrayContent(Encoding.UTF8.GetBytes("%PDF-1.4\n%%EOF"));
                    file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
                    form.Add(file, "file", "synthetic.pdf");
                    form.Add(new StringContent("Synthetic Society"), "society");
                    form.Add(new StringContent("2026"), "year");
                    form.Add(new StringContent("final"), "guidelineStatus");
                    return form;
                }, WorkspaceRole.Editor),
            Add("DocumentsController.Delete", HttpMethod.Delete,
                f => $"/api/workspaces/{f.WorkspaceId}/documents/{f.StatusDocumentId}", _ => null, WorkspaceRole.Editor),
            Add("DocumentsController.ResetPages", HttpMethod.Post,
                f => $"/api/workspaces/{f.WorkspaceId}/documents/{f.ParsedDocumentId}/reset-pages",
                _ => Json(new { pages = new[] { 1 } }), WorkspaceRole.Editor),
            Add("DocumentsController.ResetEmbeddings", HttpMethod.Post,
                f => $"/api/workspaces/{f.WorkspaceId}/documents/{f.ParsedDocumentId}/reset-embeddings", _ => null, WorkspaceRole.Editor),
            Add("DocumentsController.Parse", HttpMethod.Post,
                f => $"/api/workspaces/{f.WorkspaceId}/documents/{f.UploadedDocumentId}/parse", _ => null, WorkspaceRole.Editor),
            Add("DocumentsController.Reparse", HttpMethod.Post,
                f => $"/api/workspaces/{f.WorkspaceId}/documents/{f.UploadedDocumentId}/reparse", _ => null, WorkspaceRole.Editor),
            Add("DocumentsController.ChunkPortions", HttpMethod.Post,
                f => $"/api/workspaces/{f.WorkspaceId}/documents/{f.ParsedDocumentId}/chunk-portions",
                _ => Json(new { pages = new[] { 1 }, guidance = "Synthetic guidance", maxTokens = 400, batchSize = 1 }), WorkspaceRole.Editor),
            Add("DocumentsController.UpdateStatus", HttpMethod.Patch,
                f => $"/api/workspaces/{f.WorkspaceId}/documents/{f.StatusDocumentId}/status",
                _ => Json(new { status = "parsed" }), WorkspaceRole.Editor),
            Add("DocumentsController.GenerateEmbeddings", HttpMethod.Post,
                f => $"/api/workspaces/{f.WorkspaceId}/documents/{f.StatusDocumentId}/generate-embeddings", _ => null, WorkspaceRole.Editor),

            Add("ChunkReviewController.Review", HttpMethod.Patch,
                f => $"/api/workspaces/{f.WorkspaceId}/chunks/{f.ChunkId}",
                _ => Json(new { status = "accepted" }), WorkspaceRole.Editor),
            Add("ChunkReviewController.BulkAccept", HttpMethod.Post,
                f => $"/api/workspaces/{f.WorkspaceId}/chunks/bulk-accept",
                f => Json(new { documentId = f.ParsedDocumentId }), WorkspaceRole.Editor),
            Add("ChunkReviewController.BulkCreate", HttpMethod.Post,
                f => $"/api/workspaces/{f.WorkspaceId}/chunks/bulk",
                f => Json(new[] { new { documentId = f.ParsedDocumentId, page = 1, text = "Synthetic import." } }), WorkspaceRole.Editor),
            Add("ChunkReviewController.Merge", HttpMethod.Post,
                f => $"/api/workspaces/{f.WorkspaceId}/chunks/merge",
                f => Json(new { ids = new[] { f.ChunkId, f.Chunk2Id } }), WorkspaceRole.Editor),

            Add("RulesController.Create", HttpMethod.Post,
                f => $"/api/workspaces/{f.WorkspaceId}/rules",
                _ => Json(RuleTestDefinitions.Create()), WorkspaceRole.Editor),
            Add("RulesController.Import", HttpMethod.Post,
                f => $"/api/workspaces/{f.WorkspaceId}/rules/import-default", _ => null, WorkspaceRole.Editor),
            Add("RulesController.Update", HttpMethod.Patch,
                f => $"/api/workspaces/{f.WorkspaceId}/rules/{f.DraftRuleId}",
                _ => Json(new { title = "Updated synthetic rule", expectedRevision = 1 }), WorkspaceRole.Editor),
            Add("RulesController.Submit", HttpMethod.Post,
                f => $"/api/workspaces/{f.WorkspaceId}/rules/{f.DraftRuleId}/submit",
                _ => Json(new { expectedRevision = 1 }), WorkspaceRole.Editor),
            Add("RulesController.Approve", HttpMethod.Post,
                f => $"/api/workspaces/{f.WorkspaceId}/rules/{f.InReviewRuleId}/approve",
                _ => Json(new { expectedRevision = 2 }), WorkspaceRole.Reviewer),
            Add("RulesController.Retire", HttpMethod.Post,
                f => $"/api/workspaces/{f.WorkspaceId}/rules/{f.ApprovedRuleId}/retire",
                _ => Json(new { expectedRevision = 3 }), WorkspaceRole.Reviewer),
            Add("RulesController.Reopen", HttpMethod.Post,
                f => $"/api/workspaces/{f.WorkspaceId}/rules/{f.RetiredRuleId}/reopen",
                _ => Json(new { expectedRevision = 4 }), WorkspaceRole.Editor),
            Add("RulesController.Enablement", HttpMethod.Post,
                f => $"/api/workspaces/{f.WorkspaceId}/rules/{f.ApprovedRuleId}/enablement",
                _ => Json(new { enabled = false, expectedRevision = 3 }), WorkspaceRole.Reviewer),
            Add("RulesController.Delete", HttpMethod.Delete,
                f => $"/api/workspaces/{f.WorkspaceId}/rules/{f.DraftRuleId}?expectedRevision=1", _ => null, WorkspaceRole.Owner),

            Add("TestsController.Create", HttpMethod.Post,
                f => $"/api/workspaces/{f.WorkspaceId}/tests",
                _ => Json(new { name = "Synthetic matrix case", inputText = "Synthetic input." }), WorkspaceRole.Editor),
            Add("TestsController.Update", HttpMethod.Patch,
                f => $"/api/workspaces/{f.WorkspaceId}/tests/{f.TestCaseId}",
                _ => Json(new { name = "Updated synthetic case" }), WorkspaceRole.Editor),
            Add("TestsController.Run", HttpMethod.Post,
                f => $"/api/workspaces/{f.WorkspaceId}/tests/run", _ => Json(new { }), WorkspaceRole.Editor),
            Add("TestsController.ImportSeed", HttpMethod.Post,
                f => $"/api/workspaces/{f.WorkspaceId}/tests/import-seed", _ => null, WorkspaceRole.Editor),

            Add("ConfigController.Draft", HttpMethod.Post,
                f => $"/api/workspaces/{f.WorkspaceId}/config/draft",
                _ => Json(new { configJson = "{}" }), WorkspaceRole.Editor),
            Add("ConfigController.Publish", HttpMethod.Post,
                f => $"/api/workspaces/{f.WorkspaceId}/config/{f.PublishDraftId}/publish", _ => null, WorkspaceRole.Owner),
            Add("ConfigController.Discard", HttpMethod.Post,
                f => $"/api/workspaces/{f.WorkspaceId}/config/{f.DiscardDraftId}/discard",
                _ => Json(new { }), WorkspaceRole.Editor),
            Add("ConfigController.Rollback", HttpMethod.Post,
                f => $"/api/workspaces/{f.WorkspaceId}/config/rollback/1", _ => Json(new { }), WorkspaceRole.Owner),

            Add("WorkspacesController.MemberRole", HttpMethod.Patch,
                f => $"/api/workspaces/{f.WorkspaceId}/members/{f.EditorId}/role",
                _ => Json(new { role = "viewer", expectedRole = "editor" }), WorkspaceRole.Owner),
            Add("WorkspacesController.Invite", HttpMethod.Post,
                f => $"/api/workspaces/{f.WorkspaceId}/invite",
                _ => Json(new { email = "matrix-invite@example.org", role = "viewer" }), WorkspaceRole.Owner),
            Add("WorkspacesController.CancelInvitation", HttpMethod.Delete,
                f => $"/api/workspaces/{f.WorkspaceId}/invitations/{f.InvitationId}", _ => null, WorkspaceRole.Owner),
            Add("WorkspacesController.RemoveMember", HttpMethod.Delete,
                f => $"/api/workspaces/{f.WorkspaceId}/members/{f.EditorId}?expectedRole=editor", _ => null, WorkspaceRole.Owner),
            Add("WorkspacesController.Leave", HttpMethod.Post,
                f => $"/api/workspaces/{f.WorkspaceId}/leave", _ => null, WorkspaceRole.Viewer, viewerAllowed: true),
            Add("WorkspacesController.Rename", HttpMethod.Patch,
                f => $"/api/workspaces/{f.WorkspaceId}",
                f => Json(new { name = "Renamed synthetic workspace", expectedName = f.WorkspaceName }), WorkspaceRole.Owner),
            Add("WorkspacesController.UpdateGuidelinePolicy", HttpMethod.Patch,
                f => $"/api/workspaces/{f.WorkspaceId}/guideline-policy",
                _ => Json(new { allowSupersededGuidelines = true }), WorkspaceRole.Owner),
            Add("WorkspacesController.Delete", HttpMethod.Delete,
                f => $"/api/workspaces/{f.WorkspaceId}", _ => null, WorkspaceRole.Owner),

            Add("AnalysisController.Analyze", HttpMethod.Post,
                f => $"/api/workspaces/{f.WorkspaceId}/analyze",
                _ => Json(new { message = "Synthetic question." }), WorkspaceRole.Editor),
            Add("AnalysisController.Stream", HttpMethod.Post,
                f => $"/api/workspaces/{f.WorkspaceId}/analyze/stream",
                _ => Json(new { message = "Synthetic question." }), WorkspaceRole.Editor),

            Add("AnalysesController.Flag", HttpMethod.Post,
                f => $"/api/workspaces/{f.WorkspaceId}/analyses/{f.OwnerAnalysisId}/flag",
                _ => Json(new { reason = "unsafe", note = "Synthetic clinician note" }), WorkspaceRole.Editor),

            Add("AnalysesController.Delete", HttpMethod.Delete,
                f => $"/api/workspaces/{f.WorkspaceId}/analyses/{f.OwnerAnalysisId}", _ => null,
                WorkspaceRole.Viewer, viewerAllowed: true,
                viewerPath: f => $"/api/workspaces/{f.WorkspaceId}/analyses/{f.ViewerAnalysisId}"),

            Add("DrugCatalogController.Update", HttpMethod.Patch,
                f => $"/api/workspaces/{f.WorkspaceId}/drugs",
                f => Json(new { catalog = RuleTestDefinitions.Catalog, expectedRevision = f.CatalogRevision }), WorkspaceRole.Editor)
        }.ToDictionary(route => route.Key, StringComparer.Ordinal);
    }

    private readonly MatrixFactory _factory;

    public WritePathAuthorizationMatrixTests(MatrixFactory factory) => _factory = factory;

    private static ClaimsPrincipal Principal(User user) => new(new ClaimsIdentity(new[]
    {
        new Claim(ClaimTypes.NameIdentifier, user.FirebaseUid),
        new Claim(ClaimTypes.Email, user.Email),
        new Claim("email_verified", "true")
    }, "Test"));

    private async Task<(Fixture Fixture, Dictionary<string, User> Users)> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var users = new Dictionary<string, User>();
        foreach (var role in new[] { "owner", "second-owner", "editor", "reviewer", "viewer", "outsider" })
            users[role] = db.Users.Add(new User
            {
                FirebaseUid = $"matrix-{role}-{Guid.NewGuid():N}",
                Email = $"matrix-{role}-{Guid.NewGuid():N}@example.org"
            }).Entity;
        var workspace = db.Workspaces.Add(new Workspace { Name = "Synthetic matrix workspace", OwnerId = users["owner"].Id }).Entity;
        foreach (var role in new[] { "owner", "second-owner", "editor", "reviewer", "viewer" })
            db.WorkspaceMembers.Add(new WorkspaceMember
            {
                WorkspaceId = workspace.Id,
                UserId = users[role].Id,
                Role = role == "second-owner" ? "owner" : role
            });
        await db.SaveChangesAsync();

        var upload = Path.Combine(_factory.Uploads, $"{Guid.NewGuid():N}.pdf");
        Directory.CreateDirectory(_factory.Uploads);
        await File.WriteAllBytesAsync(upload, Encoding.UTF8.GetBytes("%PDF-1.4\n%%EOF"));
        var parsed = db.Documents.Add(new Document
        {
            WorkspaceId = workspace.Id, FileName = "synthetic-parsed.pdf", Status = "parsed",
            ParsedText = "--- Page 1 ---\nSynthetic first page.\n\n--- Page 2 ---\nSynthetic second page.",
            PageCount = 2, ChunkingStatus = "not_started", ChunkedPageNumbers = "[]"
        }).Entity;
        var uploaded = db.Documents.Add(new Document
        {
            WorkspaceId = workspace.Id, FileName = "synthetic-uploaded.pdf", Status = "uploaded", StoragePath = upload
        }).Entity;
        var statusDocument = db.Documents.Add(new Document
        {
            WorkspaceId = workspace.Id, FileName = "synthetic-status.pdf", Status = "parsed",
            ParsedText = "Synthetic status text.", PageCount = 1, ChunkingStatus = "not_started", ChunkedPageNumbers = "[]"
        }).Entity;
        var chunk1 = db.Chunks.Add(new Chunk
        {
            WorkspaceId = workspace.Id, DocumentId = parsed.Id, Page = 1, Text = "Synthetic first chunk.", Status = "pending", Confidence = 0.9
        }).Entity;
        var chunk2 = db.Chunks.Add(new Chunk
        {
            WorkspaceId = workspace.Id, DocumentId = parsed.Id, Page = 2, Text = "Synthetic second chunk.", Status = "pending", Confidence = 0.9
        }).Entity;

        var rules = new RuleService(db);
        var draftRule = await rules.CreateAsync(workspace.Id, RuleTestDefinitions.Create(), users["owner"].Id);
        var inReviewRule = await rules.CreateAsync(workspace.Id, RuleTestDefinitions.Create(), users["owner"].Id);
        await rules.TransitionAsync(workspace.Id, inReviewRule.Id, "submit", 1, users["owner"].Id);
        var approvedRule = await rules.CreateAsync(workspace.Id, RuleTestDefinitions.Create(), users["owner"].Id);
        await rules.TransitionAsync(workspace.Id, approvedRule.Id, "submit", 1, users["owner"].Id);
        await rules.TransitionAsync(workspace.Id, approvedRule.Id, "approve", 2, users["owner"].Id);
        var retiredRule = await rules.CreateAsync(workspace.Id, RuleTestDefinitions.Create(), users["owner"].Id);
        await rules.TransitionAsync(workspace.Id, retiredRule.Id, "submit", 1, users["owner"].Id);
        await rules.TransitionAsync(workspace.Id, retiredRule.Id, "approve", 2, users["owner"].Id);
        await rules.TransitionAsync(workspace.Id, retiredRule.Id, "retire", 3, users["owner"].Id);

        var testCase = db.TestCases.Add(new TestCase
        {
            WorkspaceId = workspace.Id, Name = "Synthetic matrix case", InputText = "Synthetic case input."
        }).Entity;
        var configs = new ConfigService(db, _factory.Runs);
        var published = await configs.GetPublishedAsync(workspace.Id);
        var publishDraft = await configs.CreateDraftAsync(workspace.Id, "{}", users["owner"].Id, published.Id);
        var discardDraft = await configs.CreateDraftAsync(workspace.Id, "{}", users["owner"].Id, published.Id);
        _factory.Runs.Runs.Clear();
        _factory.Runs.Runs.Add(FakeConfigTestRuns.Passed(publishDraft));
        var invitation = db.WorkspaceInvitations.Add(new WorkspaceInvitation
        {
            WorkspaceId = workspace.Id, Email = "matrix-seeded-invite@example.org", Role = "viewer", InvitedBy = users["owner"].Id
        }).Entity;
        var ownerAnalysis = db.AnalysisRuns.Add(new AnalysisRun
        {
            WorkspaceId = workspace.Id, UserId = users["owner"].Id, InputHash = "matrix-owner", ConfigVersion = 1
        }).Entity;
        var viewerAnalysis = db.AnalysisRuns.Add(new AnalysisRun
        {
            WorkspaceId = workspace.Id, UserId = users["viewer"].Id, InputHash = "matrix-viewer", ConfigVersion = 1
        }).Entity;
        await db.SaveChangesAsync();

        return (new Fixture(workspace.Id, parsed.Id, uploaded.Id, statusDocument.Id, chunk1.Id, chunk2.Id,
            draftRule.Id, inReviewRule.Id, approvedRule.Id, retiredRule.Id, testCase.Id,
            publishDraft.Id, discardDraft.Id, invitation.Id, users["editor"].Id,
            ownerAnalysis.Id, viewerAnalysis.Id, 0, workspace.Name), users);
    }

    private static WorkspaceRole EffectiveRole(MethodInfo method)
    {
        var methodAttributes = method.GetCustomAttributes<WorkspaceAuthorizeAttribute>(inherit: true).ToList();
        var attributes = methodAttributes.Count > 0
            ? methodAttributes
            : method.DeclaringType!.GetCustomAttributes<WorkspaceAuthorizeAttribute>(inherit: true).ToList();
        return attributes.Count == 0 ? WorkspaceRole.Viewer : attributes[^1].MinimumRole;
    }

    private static IEnumerable<string> DiscoveredMutatingActions()
    {
        var controllers = typeof(Program).Assembly.GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type) && !type.IsAbstract)
            .Where(type => type.GetCustomAttribute<RouteAttribute>(inherit: false)?.Template?.Contains("{workspaceId}") == true
                || type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Any(method => method.GetCustomAttribute<HttpMethodAttribute>(inherit: true)?.Template?.Contains("{workspaceId}") == true));
        foreach (var controller in controllers)
        {
            foreach (var method in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (!method.GetParameters().Any(parameter => parameter.Name == "workspaceId")) continue;
                if (method.GetCustomAttribute<HttpMethodAttribute>(inherit: true) is not { } http
                    || http.HttpMethods.All(verb => verb is "GET" or "HEAD" or "OPTIONS")) continue;
                if (method.Name is nameof(AnalysisController.Retrieve) or nameof(RulesController.Preview)) continue;
                yield return $"{controller.Name}.{method.Name}";
            }
        }
    }

    public static IEnumerable<object[]> RouteKeys => Routes.Keys.OrderBy(key => key).Select(key => new object[] { key });

    [Fact]
    public void Reflection_route_set_matches_the_matrix_and_role_metadata()
    {
        var discovered = DiscoveredMutatingActions().OrderBy(key => key).ToArray();
        Assert.Equal(discovered, Routes.Keys.OrderBy(key => key).ToArray());
        foreach (var (key, route) in Routes)
        {
            var type = typeof(Program).Assembly.GetType($"ClinicalRag.Api.Controllers.{key.Split('.')[0]}")!;
            var method = type.GetMethod(key.Split('.')[1])!;
            Assert.Equal(EffectiveRole(method), route.MinimumRole);
        }
    }

    [Theory]
    [MemberData(nameof(RouteKeys))]
    public async Task Non_member_gets_404_on_every_mutating_route(string key)
    {
        var (fixture, users) = await SeedAsync();
        using var client = _factory.CreateClient();
        try
        {
            TestPrincipal.Current = Principal(users["outsider"]);
            var response = await Send(client, Routes[key], fixture);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound,
                $"{key} returned {(int)response.StatusCode}, expected 404.");
        }
        finally { TestPrincipal.Current = null; }
    }

    [Theory]
    [MemberData(nameof(RouteKeys))]
    public async Task Viewer_gets_403_on_every_mutating_route_except_explicit_self_service(string key)
    {
        var route = Routes[key];
        var (fixture, users) = await SeedAsync();
        using var client = _factory.CreateClient();
        try
        {
            TestPrincipal.Current = Principal(users["viewer"]);
            var response = await Send(client, route, fixture, viewer: true);
            if (route.ViewerAllowed)
                Assert.True(response.IsSuccessStatusCode,
                    $"{key} is viewer self-service and returned {(int)response.StatusCode}.");
            else
                Assert.True(response.StatusCode == HttpStatusCode.Forbidden,
                    $"{key} returned {(int)response.StatusCode}, expected 403.");
        }
        finally { TestPrincipal.Current = null; }
    }

    [Theory]
    [MemberData(nameof(RouteKeys))]
    public async Task Owner_can_execute_every_mutating_route(string key)
    {
        var (fixture, users) = await SeedAsync();
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic");
        try
        {
            TestPrincipal.Current = Principal(users["owner"]);
            var response = await Send(client, Routes[key], fixture);
            Assert.True(response.IsSuccessStatusCode,
                $"{key} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }
        finally { TestPrincipal.Current = null; }
    }

    [Fact]
    public async Task Editor_cannot_publish_configuration_or_approve_rules()
    {
        var (fixture, users) = await SeedAsync();
        using var client = _factory.CreateClient();
        try
        {
            TestPrincipal.Current = Principal(users["editor"]);
            var publish = await client.PostAsync(
                $"/api/workspaces/{fixture.WorkspaceId}/config/{fixture.PublishDraftId}/publish", null);
            Assert.Equal(HttpStatusCode.Forbidden, publish.StatusCode);

            var approve = await client.PostAsJsonAsync(
                $"/api/workspaces/{fixture.WorkspaceId}/rules/{fixture.InReviewRuleId}/approve",
                new { expectedRevision = 2 });
            Assert.Equal(HttpStatusCode.Forbidden, approve.StatusCode);
        }
        finally { TestPrincipal.Current = null; }
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, RouteCase route, Fixture fixture, bool viewer = false)
    {
        var path = viewer && route.ViewerPath != null ? route.ViewerPath(fixture) : route.Path(fixture);
        using var request = new HttpRequestMessage(route.Method, path);
        request.Content = route.Content(fixture);
        return await client.SendAsync(request);
    }
}
