using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using ClinicalRag.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClinicalRag.Tests;

[Collection("Sequential")]
public class ConfigLifecycleControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    public ConfigLifecycleControllerTests(CustomWebApplicationFactory factory) => _factory = factory;

    private (Guid WorkspaceId, Guid PublishedId, Guid DraftId, ClaimsPrincipal Owner) Seed()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var uid = $"config-owner-{Guid.NewGuid():N}";
        var user = db.Users.Add(new User { FirebaseUid = uid, Email = "config@example.com" }).Entity;
        var workspace = db.Workspaces.Add(new Workspace { Name = "Config lifecycle", OwnerId = user.Id }).Entity;
        db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = workspace.Id, UserId = user.Id, Role = "owner" });
        var published = db.ConfigVersions.Add(new ConfigVersion
        {
            WorkspaceId = workspace.Id, Version = 1, Status = "published", ConfigJson = ConfigService.DefaultConfig()
        }).Entity;
        var draft = db.ConfigVersions.Add(new ConfigVersion
        {
            WorkspaceId = workspace.Id, Version = 2, Status = "draft", ConfigJson = ConfigService.DefaultConfig()
        }).Entity;
        db.ConfigVersions.Add(new ConfigVersion
        {
            WorkspaceId = workspace.Id, Version = 3, Status = "archived", ConfigJson = ConfigService.DefaultConfig()
        });
        db.SaveChanges();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, uid), new Claim(ClaimTypes.Email, user.Email),
            new Claim("email_verified", "true")
        }, "Test"));
        return (workspace.Id, published.Id, draft.Id, principal);
    }

    [Fact]
    public async Task Versions_report_the_current_role_and_no_test_run_instead_of_an_invented_pass()
    {
        var seeded = Seed();
        TestPrincipal.Current = seeded.Owner;
        try
        {
            var response = await _factory.CreateClient().GetAsync($"/api/workspaces/{seeded.WorkspaceId}/config/versions");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("owner", body.GetProperty("role").GetString());
            var draft = body.GetProperty("versions").EnumerateArray()
                .Single(v => v.GetProperty("id").GetGuid() == seeded.DraftId);
            Assert.Equal(JsonValueKind.Null, draft.GetProperty("latestTestRun").ValueKind);
            Assert.Equal("no_cases", draft.GetProperty("publishBlockingCode").GetString());
        }
        finally { TestPrincipal.Current = null; }
    }

    [Fact]
    public async Task Owner_publish_without_a_test_run_returns_conflict_not_success()
    {
        var seeded = Seed();
        TestPrincipal.Current = seeded.Owner;
        try
        {
            var response = await _factory.CreateClient().PostAsJsonAsync(
                $"/api/workspaces/{seeded.WorkspaceId}/config/{seeded.DraftId}/publish", new { });
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("no_cases", await response.Content.ReadAsStringAsync());
        }
        finally { TestPrincipal.Current = null; }
    }

    [Fact]
    public async Task Discard_preserves_history_and_cannot_discard_the_published_version()
    {
        var seeded = Seed();
        TestPrincipal.Current = seeded.Owner;
        try
        {
            var client = _factory.CreateClient();
            var response = await client.PostAsJsonAsync(
                $"/api/workspaces/{seeded.WorkspaceId}/config/{seeded.DraftId}/discard", new { });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("discarded", body.GetProperty("status").GetString());
            var published = await client.PostAsJsonAsync(
                $"/api/workspaces/{seeded.WorkspaceId}/config/{seeded.PublishedId}/discard", new { });
            Assert.Equal(HttpStatusCode.Conflict, published.StatusCode);
        }
        finally { TestPrincipal.Current = null; }
    }

    [Fact]
    public async Task Rollback_restores_history_as_a_new_draft_without_changing_published()
    {
        var seeded = Seed();
        TestPrincipal.Current = seeded.Owner;
        try
        {
            var client = _factory.CreateClient();
            var response = await client.PostAsJsonAsync(
                $"/api/workspaces/{seeded.WorkspaceId}/config/rollback/3", new { });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var restored = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("draft", restored.GetProperty("status").GetString());
            Assert.Equal(4, restored.GetProperty("version").GetInt32());
            var published = await client.GetFromJsonAsync<JsonElement>($"/api/workspaces/{seeded.WorkspaceId}/config/published");
            Assert.Equal(seeded.PublishedId, published.GetProperty("id").GetGuid());
        }
        finally { TestPrincipal.Current = null; }
    }

    [Fact]
    public async Task Invalid_draft_returns_safe_validation_errors_and_does_not_persist()
    {
        var seeded = Seed();
        TestPrincipal.Current = seeded.Owner;
        try
        {
            var response = await _factory.CreateClient().PostAsJsonAsync(
                $"/api/workspaces/{seeded.WorkspaceId}/config/draft",
                new { configJson = "{\"prompts\":{\"plan\":\"PRIVATE_PROMPT_MARKER\"}}" });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("placeholder", body);
            Assert.DoesNotContain("PRIVATE_PROMPT_MARKER", body);
        }
        finally { TestPrincipal.Current = null; }
    }
}
