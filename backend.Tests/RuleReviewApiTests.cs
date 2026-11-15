using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using ClinicalRag.Api.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace ClinicalRag.Tests;

public sealed class FakeRulePreviewClient : IRulePreviewClient
{
    public PipelineRulePreviewRequest? Request { get; private set; }
    public string? Authorization { get; private set; }
    public bool Fail { get; set; }
    public Task<JsonElement> PreviewAsync(PipelineRulePreviewRequest request, string? authorization, CancellationToken cancellationToken = default)
    {
        Request = request; Authorization = authorization;
        if (Fail) throw new InvalidOperationException("PRIVATE_CASE_MARKER PRIVATE_REGEX_MARKER");
        return Task.FromResult(JsonSerializer.SerializeToElement(new
        {
            would_fire = true, catalog_revision = request.CatalogRevision, regex = new { matched = true, timed_out = false },
            values = new { creatinine = 2 }, derived = new { }, meds = Array.Empty<object>(), reasons = new[] { "creatinine 2 >= 1.5" },
            warnings = Array.Empty<string>(), unit_details = new { }, notice = "Synthetic preview."
        }));
    }
}

[Collection("Sequential")]
public class RuleReviewApiTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    public RuleReviewApiTests(CustomWebApplicationFactory factory) => _factory = factory;

    private sealed record Fixture(Guid WorkspaceId, Rule Rule, IReadOnlyDictionary<string, User> Users);

    private async Task<Fixture> SeedAsync(WebApplicationFactory<Program>? factory = null)
    {
        using var scope = (factory ?? _factory).Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var users = new Dictionary<string, User>();
        foreach (var role in new[] { "owner", "editor", "reviewer", "viewer", "outsider" })
            users[role] = db.Users.Add(new User { FirebaseUid = $"{role}-{Guid.NewGuid():N}", Email = $"{role}@example.org" }).Entity;
        var workspace = db.Workspaces.Add(new Workspace { Name = "Rule review API", OwnerId = users["owner"].Id }).Entity;
        foreach (var role in new[] { "owner", "editor", "reviewer", "viewer" })
            db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = workspace.Id, UserId = users[role].Id, Role = role });
        await db.SaveChangesAsync();
        var rule = await new RuleService(db).CreateAsync(workspace.Id, RuleTestDefinitions.Create(), users["editor"].Id);
        return new Fixture(workspace.Id, rule, users);
    }

    private static void SignIn(User user) => TestPrincipal.Current = new ClaimsPrincipal(new ClaimsIdentity(
        new[] { new Claim(ClaimTypes.NameIdentifier, user.FirebaseUid), new Claim(ClaimTypes.Email, user.Email), new Claim("email_verified", "true") }, "Test"));

    [Theory]
    [InlineData("owner", true, true, true)]
    [InlineData("editor", true, false, false)]
    [InlineData("reviewer", false, true, false)]
    [InlineData("viewer", false, false, false)]
    public async Task Access_reports_separate_editor_reviewer_and_owner_permissions(string role, bool edit, bool review, bool delete)
    {
        var data = await SeedAsync();
        try
        {
            SignIn(data.Users[role]);
            using var client = _factory.CreateClient();
            var access = await client.GetFromJsonAsync<JsonElement>($"/api/workspaces/{data.WorkspaceId}/rules/access");
            Assert.Equal(role, access.GetProperty("role").GetString());
            Assert.Equal(edit, access.GetProperty("canEdit").GetBoolean());
            Assert.Equal(review, access.GetProperty("canReview").GetBoolean());
            Assert.Equal(delete, access.GetProperty("canDelete").GetBoolean());
            Assert.Equal(data.Users[role].Id, access.GetProperty("userId").GetGuid());
        }
        finally { TestPrincipal.Current = null; }
    }

    [Fact]
    public async Task Reviewer_can_read_approve_toggle_and_retire_but_cannot_edit_or_administer_the_workspace()
    {
        var data = await SeedAsync(); var root = $"/api/workspaces/{data.WorkspaceId}";
        try
        {
            SignIn(data.Users["reviewer"]);
            using var client = _factory.CreateClient();
            foreach (var path in new[] { "/rules", "/rules/vocabulary", "/rules/access", $"/rules/{data.Rule.Id}/audit", "/rules/audit", "/drugs" })
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(root + path)).StatusCode);
            var approved = await client.PostAsJsonAsync(root + $"/rules/{data.Rule.Id}/approve", new { expectedRevision = 1 });
            Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
            var reviewed = await approved.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(data.Users["reviewer"].Id, reviewed.GetProperty("reviewedBy").GetGuid());
            Assert.Equal("approved", reviewed.GetProperty("status").GetString());
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(root + $"/rules/{data.Rule.Id}/enablement", new { enabled = false, expectedRevision = 2 })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(root + $"/rules/{data.Rule.Id}/retire", new { expectedRevision = 3 })).StatusCode);
            var forbidden = new (HttpMethod Method, string Path, object? Body)[]
            {
                (HttpMethod.Post, "/rules", RuleTestDefinitions.Create()),
                (HttpMethod.Post, "/rules/import-default", new { }),
                (HttpMethod.Patch, $"/rules/{data.Rule.Id}", new { title = "Changed", expectedRevision = 4 }),
                (HttpMethod.Post, $"/rules/{data.Rule.Id}/submit", new { expectedRevision = 4 }),
                (HttpMethod.Post, $"/rules/{data.Rule.Id}/reopen", new { expectedRevision = 4 }),
                (HttpMethod.Delete, $"/rules/{data.Rule.Id}?expectedRevision=4", null),
                (HttpMethod.Patch, "/drugs", new UpdateDrugCatalogDto(RuleTestDefinitions.Catalog, 0)),
                (HttpMethod.Post, "/config/draft", new { configJson = "{}" }),
                (HttpMethod.Get, "/members", null),
                (HttpMethod.Patch, $"/members/{data.Users["editor"].Id}/role", new { role = "reviewer", expectedRole = "editor" }),
                (HttpMethod.Post, "/tests/run", new { })
            };
            foreach (var (method, path, body) in forbidden)
            {
                using var request = new HttpRequestMessage(method, root + path);
                if (body != null) request.Content = JsonContent.Create(body);
                Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
            }
        }
        finally { TestPrincipal.Current = null; }
    }

    [Theory]
    [InlineData("editor")]
    [InlineData("viewer")]
    public async Task Editors_and_viewers_cannot_approve_retire_or_toggle(string role)
    {
        var data = await SeedAsync();
        try
        {
            SignIn(data.Users[role]);
            using var client = _factory.CreateClient();
            foreach (var action in new[] { "approve", "retire", "enablement" })
                Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/workspaces/{data.WorkspaceId}/rules/{data.Rule.Id}/{action}", new { expectedRevision = 1, enabled = false })).StatusCode);
        }
        finally { TestPrincipal.Current = null; }
    }

    [Fact]
    public async Task New_rule_and_catalog_routes_hide_workspace_existence_from_nonmembers()
    {
        var data = await SeedAsync(); var root = $"/api/workspaces/{data.WorkspaceId}";
        try
        {
            SignIn(data.Users["outsider"]);
            using var client = _factory.CreateClient();
            foreach (var path in new[] { "/rules", "/rules/access", "/rules/vocabulary", "/rules/audit", $"/rules/{data.Rule.Id}/audit", "/drugs" })
                Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(root + path)).StatusCode);
            foreach (var action in new[] { "submit", "approve", "retire", "reopen", "enablement" })
                Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(root + $"/rules/{data.Rule.Id}/{action}", new { expectedRevision = 1, enabled = true })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(root + "/rules/import-default", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(root + "/rules/preview", new RulePreviewDto(RuleTestDefinitions.Create()))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PatchAsJsonAsync(root + "/drugs", new UpdateDrugCatalogDto(RuleTestDefinitions.Catalog, 0))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PatchAsJsonAsync(root + $"/members/{data.Users["editor"].Id}/role", new { role = "reviewer", expectedRole = "editor" })).StatusCode);
        }
        finally { TestPrincipal.Current = null; }
    }

    [Fact]
    public async Task Lifecycle_actions_and_deletion_require_a_current_revision_and_audit_survives_deletion()
    {
        var data = await SeedAsync(); var root = $"/api/workspaces/{data.WorkspaceId}/rules/{data.Rule.Id}";
        try
        {
            SignIn(data.Users["owner"]);
            using var client = _factory.CreateClient();
            Assert.Equal((HttpStatusCode)428, (await client.PostAsJsonAsync(root + "/approve", new { })).StatusCode);
            Assert.Equal((HttpStatusCode)428, (await client.PatchAsJsonAsync(root, new { title = "Changed" })).StatusCode);
            Assert.Equal((HttpStatusCode)428, (await client.DeleteAsync(root)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(root + "/approve", new { expectedRevision = 1 })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(root + "/enablement", new { enabled = false, expectedRevision = 1 })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync(root + "?expectedRevision=1")).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(root + "?expectedRevision=2")).StatusCode);
            var history = await client.GetFromJsonAsync<JsonElement>(root + "/audit?take=2");
            Assert.True(history.GetProperty("hasMore").GetBoolean());
            Assert.Equal(2, history.GetProperty("nextSkip").GetInt32());
            Assert.Equal("deleted", history.GetProperty("entries")[0].GetProperty("action").GetString());
            Assert.Equal(JsonValueKind.Null, history.GetProperty("entries")[0].GetProperty("afterJson").ValueKind);
            var remaining = await client.GetFromJsonAsync<JsonElement>(root + "/audit?skip=2&take=2");
            Assert.False(remaining.GetProperty("hasMore").GetBoolean());
            Assert.Equal("created", remaining.GetProperty("entries")[0].GetProperty("action").GetString());
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(root + "/audit?skip=-1")).StatusCode);
        }
        finally { TestPrincipal.Current = null; }
    }

    [Fact]
    public async Task Only_owners_can_provision_existing_reviewers_and_owner_identity_cannot_be_demoted_or_forged()
    {
        var data = await SeedAsync(); var root = $"/api/workspaces/{data.WorkspaceId}/members";
        try
        {
            SignIn(data.Users["owner"]);
            using var client = _factory.CreateClient();
            var editorPath = root + $"/{data.Users["editor"].Id}/role";
            Assert.Equal((HttpStatusCode)428, (await client.PatchAsJsonAsync(editorPath, new { role = "reviewer" })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PatchAsJsonAsync(editorPath, new { role = "reviewer", expectedRole = "viewer" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PatchAsJsonAsync(editorPath, new { role = "admin", expectedRole = "editor" })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PatchAsJsonAsync(root + $"/{data.Users["owner"].Id}/role", new { role = "reviewer", expectedRole = "owner" })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PatchAsJsonAsync(root + $"/{data.Users["outsider"].Id}/role", new { role = "reviewer", expectedRole = "viewer" })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync(editorPath, new { role = "reviewer", expectedRole = "editor" })).StatusCode);
            SignIn(data.Users["editor"]);
            var access = await client.GetFromJsonAsync<JsonElement>($"/api/workspaces/{data.WorkspaceId}/rules/access");
            Assert.True(access.GetProperty("canReview").GetBoolean()); Assert.False(access.GetProperty("canEdit").GetBoolean());
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(root)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/workspaces/{data.WorkspaceId}/rules/{data.Rule.Id}/approve", new { expectedRevision = 1 })).StatusCode);
        }
        finally { TestPrincipal.Current = null; }
    }

    [Fact]
    public async Task Preview_forwards_only_a_request_local_draft_and_catalog_and_never_persists_case_or_regex_text()
    {
        var preview = new FakeRulePreviewClient();
        using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IRulePreviewClient>(); services.AddSingleton<IRulePreviewClient>(preview);
        }));
        var data = await SeedAsync(factory); var root = $"/api/workspaces/{data.WorkspaceId}/rules/preview";
        try
        {
            SignIn(data.Users["viewer"]);
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", "forwarded-synthetic-user-token");
            using var beforeScope = factory.Services.CreateScope();
            var db = beforeScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var auditCount = await db.RuleAuditEntries.CountAsync(); var analysisCount = await db.AnalysisRuns.CountAsync();
            var response = await client.PostAsJsonAsync(root, new RulePreviewDto(RuleTestDefinitions.Create(enabled: false), "PRIVATE_CASE_MARKER creat 2", "PRIVATE_REGEX_MARKER AKI", 0));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
            Assert.NotNull(preview.Request);
            Assert.Equal("PRIVATE_CASE_MARKER creat 2", preview.Request.Text); Assert.Equal("PRIVATE_REGEX_MARKER AKI", preview.Request.RegexSample);
            Assert.Equal("draft", preview.Request.Rule.Status); Assert.False(preview.Request.Rule.Enabled);
            Assert.Equal("Bearer forwarded-synthetic-user-token", preview.Authorization);
            Assert.True(preview.Request.DrugCatalog.TryGetProperty("metformin", out _)); Assert.Equal(0, preview.Request.CatalogRevision);
            Assert.Equal(auditCount, await db.RuleAuditEntries.CountAsync()); Assert.Equal(analysisCount, await db.AnalysisRuns.CountAsync());
            Assert.Equal("draft", (await db.Rules.AsNoTracking().SingleAsync(r => r.Id == data.Rule.Id)).Status);
            Assert.DoesNotContain("PRIVATE_CASE_MARKER", string.Join(' ', await db.RuleAuditEntries.Select(a => a.AfterJson).ToListAsync()));
            preview.Fail = true;
            var failure = await client.PostAsJsonAsync(root, new RulePreviewDto(RuleTestDefinitions.Create(), "PRIVATE_CASE_MARKER", "PRIVATE_REGEX_MARKER", 0));
            Assert.Equal(HttpStatusCode.BadGateway, failure.StatusCode);
            Assert.DoesNotContain("PRIVATE", await failure.Content.ReadAsStringAsync()); Assert.Contains("no-store", failure.Headers.CacheControl!.ToString());
            Assert.Equal(auditCount, await db.RuleAuditEntries.CountAsync());
        }
        finally { TestPrincipal.Current = null; }
    }

    [Fact]
    public async Task Preview_rejects_stale_catalog_and_malformed_or_oversized_inputs_before_the_preview_client()
    {
        var preview = new FakeRulePreviewClient();
        using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IRulePreviewClient>(); services.AddSingleton<IRulePreviewClient>(preview);
        }));
        var data = await SeedAsync(factory);
        try
        {
            SignIn(data.Users["viewer"]);
            using var client = factory.CreateClient();
            var root = $"/api/workspaces/{data.WorkspaceId}/rules/preview";
            var stale = await client.PostAsJsonAsync(root, new RulePreviewDto(RuleTestDefinitions.Create(), "PRIVATE_CASE_MARKER", "", 1));
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            var oversized = await client.PostAsJsonAsync(root, new RulePreviewDto(RuleTestDefinitions.Create(), new string('x', 100001), "PRIVATE_REGEX_MARKER"));
            Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
            var malformed = await client.PostAsync(root, new StringContent("{\"text\": PRIVATE_CASE_MARKER}", System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
            foreach (var response in new[] { stale, oversized, malformed })
            {
                Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
                Assert.DoesNotContain("PRIVATE", await response.Content.ReadAsStringAsync());
            }
            Assert.Null(preview.Request);
        }
        finally { TestPrincipal.Current = null; }
    }

    [Fact]
    public async Task Corrupt_catalog_repair_metadata_is_editor_authorized_revision_bound_and_not_a_default_substitution()
    {
        var data = await SeedAsync(); var root = $"/api/workspaces/{data.WorkspaceId}/drugs";
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.DrugCatalogs.Add(new WorkspaceDrugCatalog { WorkspaceId = data.WorkspaceId, Revision = 6,
                CatalogJson = "corrupt stored evidence", UpdatedBy = data.Users["editor"].Id, UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        try
        {
            using var client = _factory.CreateClient();
            foreach (var role in new[] { "owner", "editor", "reviewer", "viewer", "outsider" })
            {
                SignIn(data.Users[role]);
                var catalog = await client.GetAsync(root);
                Assert.Equal(role == "outsider" ? HttpStatusCode.NotFound : HttpStatusCode.Conflict, catalog.StatusCode);
                Assert.DoesNotContain("corrupt stored evidence", await catalog.Content.ReadAsStringAsync());
                var repair = await client.GetAsync(root + "/repair");
                Assert.Equal(role == "outsider" ? HttpStatusCode.NotFound : role is "owner" or "editor" ? HttpStatusCode.OK : HttpStatusCode.Forbidden, repair.StatusCode);
                if (role is "owner" or "editor")
                {
                    var evidence = await repair.Content.ReadFromJsonAsync<JsonElement>();
                    Assert.Equal(6, evidence.GetProperty("revision").GetInt32());
                    Assert.Equal("corrupt stored evidence", evidence.GetProperty("storedCatalogJson").GetString());
                    Assert.Equal(data.Users["editor"].Id, evidence.GetProperty("updatedBy").GetGuid());
                    Assert.False(evidence.TryGetProperty("catalog", out _));
                    Assert.Contains("no-store", repair.Headers.CacheControl!.ToString());
                }
            }
            SignIn(data.Users["editor"]);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PatchAsJsonAsync(root, new UpdateDrugCatalogDto(RuleTestDefinitions.Catalog, 5))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync(root, new UpdateDrugCatalogDto(RuleTestDefinitions.Catalog, 6))).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync(root + "/repair")).StatusCode);
            var saved = await client.GetFromJsonAsync<JsonElement>(root);
            Assert.Equal(7, saved.GetProperty("revision").GetInt32());
            using var scope = _factory.Services.CreateScope();
            var audit = await scope.ServiceProvider.GetRequiredService<AppDbContext>().RuleAuditEntries.SingleAsync(a => a.WorkspaceId == data.WorkspaceId && a.Action == "catalog_updated");
            Assert.Equal("corrupt stored evidence", audit.BeforeJson);
        }
        finally { TestPrincipal.Current = null; }
    }
}
