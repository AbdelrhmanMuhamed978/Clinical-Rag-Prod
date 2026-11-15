using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using ClinicalRag.Api.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace ClinicalRag.Tests;

[Collection("Sequential")]
public class ConfigAuthorizationTests : IClassFixture<CustomWebApplicationFactory>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly FakeConfigTestRuns _runs = new();
    private sealed record Snapshot(Guid WorkspaceId, Guid PublishedId, Guid DraftId, Guid ArchivedId, ClaimsPrincipal Owner);

    public ConfigAuthorizationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IConfigTestRunProvider>();
            services.AddSingleton<IConfigTestRunProvider>(_runs);
        }));
    }

    public void Dispose() { TestPrincipal.Current = null; _factory.Dispose(); }

    private static ClaimsPrincipal Principal(User user) => new(new ClaimsIdentity(new[]
    {
        new Claim(ClaimTypes.NameIdentifier, user.FirebaseUid), new Claim(ClaimTypes.Email, user.Email),
        new Claim("email_verified", "true")
    }, "Test"));

    private Snapshot Seed()
    {
        using var scope = _factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = db.Users.Add(new User { FirebaseUid = $"config-auth-{Guid.NewGuid():N}", Email = "synthetic@example.com" }).Entity;
        var workspace = db.Workspaces.Add(new Workspace { Name = "Synthetic lifecycle", OwnerId = user.Id }).Entity;
        db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = workspace.Id, UserId = user.Id, Role = "owner" });
        var json = JsonNode.Parse(ConfigService.DefaultConfig())!;
        json["future"] = new JsonObject { ["workspaceMarker"] = workspace.Id.ToString() };
        var published = db.ConfigVersions.Add(new ConfigVersion { WorkspaceId = workspace.Id, Version = 1, Status = "published", ConfigJson = json.ToJsonString() }).Entity;
        var draft = db.ConfigVersions.Add(new ConfigVersion { WorkspaceId = workspace.Id, Version = 2, Status = "draft", ConfigJson = json.ToJsonString() }).Entity;
        var archived = db.ConfigVersions.Add(new ConfigVersion { WorkspaceId = workspace.Id, Version = 3, Status = "archived", ConfigJson = json.ToJsonString() }).Entity;
        db.SaveChanges();
        return new(workspace.Id, published.Id, draft.Id, archived.Id, Principal(user));
    }

    private ClaimsPrincipal Member(Guid workspaceId, string role)
    {
        using var scope = _factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = db.Users.Add(new User { FirebaseUid = $"config-member-{Guid.NewGuid():N}", Email = "synthetic-member@example.com" }).Entity;
        db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = workspaceId, UserId = user.Id, Role = role }); db.SaveChanges();
        return Principal(user);
    }

    private ConfigVersion Read(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AppDbContext>().ConfigVersions.AsNoTracking().Single(version => version.Id == id);
    }

    private int Count(Guid workspaceId)
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AppDbContext>().ConfigVersions.Count(version => version.WorkspaceId == workspaceId);
    }

    [Fact]
    public async Task Owner_can_publish_a_passing_exact_draft_without_touching_another_workspace()
    {
        var own = Seed(); var other = Seed(); var draft = Read(own.DraftId); _runs.Runs.Add(FakeConfigTestRuns.Passed(draft));
        TestPrincipal.Current = own.Owner; using var client = _factory.CreateClient();
        var details = await client.GetFromJsonAsync<JsonElement>($"/api/workspaces/{own.WorkspaceId}/config/versions");
        var listed = details.GetProperty("versions").EnumerateArray().Single(version => version.GetProperty("id").GetGuid() == draft.Id);
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("publishBlockingReason").ValueKind);
        Assert.Equal("passed", listed.GetProperty("latestTestRun").GetProperty("status").GetString());

        var response = await client.PostAsJsonAsync($"/api/workspaces/{own.WorkspaceId}/config/{draft.Id}/publish", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(draft.ConfigJson, Read(draft.Id).ConfigJson); Assert.Equal("published", Read(draft.Id).Status);
        Assert.Equal("archived", Read(own.PublishedId).Status); Assert.Equal("published", Read(other.PublishedId).Status);
        Assert.NotNull(Read(draft.Id).PublishedAt);
    }

    [Theory]
    [InlineData("editor")]
    [InlineData("viewer")]
    public async Task Nonowners_cannot_publish_even_with_trusted_passing_evidence(string role)
    {
        var seeded = Seed(); _runs.Runs.Add(FakeConfigTestRuns.Passed(Read(seeded.DraftId)));
        TestPrincipal.Current = Member(seeded.WorkspaceId, role);
        var response = await _factory.CreateClient().PostAsJsonAsync($"/api/workspaces/{seeded.WorkspaceId}/config/{seeded.DraftId}/publish", new { });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); Assert.Empty(_runs.Calls);
        Assert.Equal("draft", Read(seeded.DraftId).Status); Assert.Equal("published", Read(seeded.PublishedId).Status);
    }

    [Fact]
    public async Task Editors_save_complete_merged_drafts_with_locked_safety_but_cannot_rollback()
    {
        var seeded = Seed();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>(); var published = db.ConfigVersions.Single(version => version.Id == seeded.PublishedId);
            var json = JsonNode.Parse(published.ConfigJson)!; json["retrieval"]!["candidates"] = 55;
            json["chunking"]!["futureFlag"] = true; published.ConfigJson = json.ToJsonString(); db.SaveChanges();
        }
        TestPrincipal.Current = Member(seeded.WorkspaceId, "editor"); using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/workspaces/{seeded.WorkspaceId}/config/draft", new
        {
            publishedConfigId = seeded.PublishedId, configJson = "{\"retrieval\":{\"topK\":20},\"safety\":{\"verifierLocked\":false}}"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<ConfigVersion>())!; var saved = JsonNode.Parse(created.ConfigJson)!;
        Assert.Equal(4, created.Version); Assert.Equal("draft", created.Status); Assert.NotNull(created.CreatedBy);
        Assert.Equal(20, saved["retrieval"]!["topK"]!.GetValue<int>()); Assert.Equal(55, saved["retrieval"]!["candidates"]!.GetValue<int>());
        Assert.True(saved["chunking"]!["futureFlag"]!.GetValue<bool>()); Assert.True(saved["safety"]!["verifierLocked"]!.GetValue<bool>());
        Assert.Equal(seeded.WorkspaceId.ToString(), saved["future"]!["workspaceMarker"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/workspaces/{seeded.WorkspaceId}/config/rollback/3", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/workspaces/{seeded.WorkspaceId}/config/{created.Id}/discard", new { })).StatusCode);
    }

    [Fact]
    public async Task Viewers_can_inspect_versions_but_cannot_save_discard_or_rollback()
    {
        var seeded = Seed(); TestPrincipal.Current = Member(seeded.WorkspaceId, "viewer"); using var client = _factory.CreateClient();
        var details = await client.GetFromJsonAsync<JsonElement>($"/api/workspaces/{seeded.WorkspaceId}/config/versions");
        Assert.Equal("viewer", details.GetProperty("role").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/workspaces/{seeded.WorkspaceId}/config/draft", new { configJson = "{}" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/workspaces/{seeded.WorkspaceId}/config/{seeded.DraftId}/discard", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/workspaces/{seeded.WorkspaceId}/config/rollback/3", new { })).StatusCode);
        Assert.Equal(3, Count(seeded.WorkspaceId)); Assert.Equal("draft", Read(seeded.DraftId).Status);
    }

    [Fact]
    public async Task Nonmembers_cannot_read_or_mutate_foreign_workspace_lifecycle_resources()
    {
        var own = Seed(); var other = Seed(); TestPrincipal.Current = own.Owner; using var client = _factory.CreateClient();
        foreach (var resource in new[] { "published", "versions" })
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/workspaces/{other.WorkspaceId}/config/{resource}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/workspaces/{other.WorkspaceId}/config/draft", new { configJson = "{}" })).StatusCode);
        foreach (var resource in new[] { $"{other.DraftId}/publish", $"{other.DraftId}/discard", "rollback/3" })
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/workspaces/{other.WorkspaceId}/config/{resource}", new { })).StatusCode);
        Assert.Equal(3, Count(other.WorkspaceId)); Assert.Equal("published", Read(other.PublishedId).Status); Assert.Empty(_runs.Calls);
    }

    [Fact]
    public async Task Foreign_ids_do_not_cross_workspace_boundaries_and_rollback_uses_local_history()
    {
        var own = Seed(); var other = Seed(); TestPrincipal.Current = own.Owner; using var client = _factory.CreateClient();
        foreach (var action in new[] { "publish", "discard" })
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/workspaces/{own.WorkspaceId}/config/{other.DraftId}/{action}", new { })).StatusCode);
        var stale = await client.PostAsJsonAsync($"/api/workspaces/{own.WorkspaceId}/config/draft", new { configJson = "{}", publishedConfigId = other.PublishedId });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode); Assert.Equal(3, Count(own.WorkspaceId));
        var response = await client.PostAsJsonAsync($"/api/workspaces/{own.WorkspaceId}/config/rollback/3", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var restored = (await response.Content.ReadFromJsonAsync<ConfigVersion>())!;
        Assert.Equal(own.WorkspaceId, restored.WorkspaceId);
        Assert.Equal(own.WorkspaceId.ToString(), JsonNode.Parse(restored.ConfigJson)!["future"]!["workspaceMarker"]!.GetValue<string>());
        Assert.Equal(3, Count(other.WorkspaceId)); Assert.Equal("archived", Read(other.ArchivedId).Status);
    }

    [Fact]
    public async Task Anonymous_requests_are_rejected_without_bootstrapping_a_configuration()
    {
        var workspaceId = Guid.NewGuid(); TestPrincipal.Current = null; using var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/workspaces/{workspaceId}/config/published")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync($"/api/workspaces/{workspaceId}/config/draft", new { configJson = "{}" })).StatusCode);
        Assert.Equal(0, Count(workspaceId));
    }

    [Fact]
    public async Task Client_written_test_results_and_the_stub_runner_never_authorize_publication()
    {
        var seeded = Seed(); TestPrincipal.Current = seeded.Owner; using var client = _factory.CreateClient();
        var forged = FakeConfigTestRuns.Passed(Read(seeded.DraftId));
        var created = await client.PostAsJsonAsync($"/api/workspaces/{seeded.WorkspaceId}/tests", new
        {
            name = "Synthetic client result", inputText = "Synthetic case", lastRunJson = JsonSerializer.Serialize(forged)
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var stub = await client.PostAsJsonAsync($"/api/workspaces/{seeded.WorkspaceId}/tests/run", new { });
        Assert.Equal(HttpStatusCode.OK, stub.StatusCode);
        var stubResult = await stub.Content.ReadFromJsonAsync<JsonElement>();
        // Real runs attempt the pipeline; in the test environment it is unavailable, so runs fail closed.
        Assert.All(stubResult.GetProperty("results").EnumerateArray(), result => Assert.Equal("failed", result.GetProperty("status").GetString()));
        var response = await client.PostAsJsonAsync($"/api/workspaces/{seeded.WorkspaceId}/config/{seeded.DraftId}/publish", new { latestTestRun = forged });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); Assert.Contains("no test run", await response.Content.ReadAsStringAsync());
        Assert.Equal("draft", Read(seeded.DraftId).Status); Assert.Equal("published", Read(seeded.PublishedId).Status);
    }

    [Fact]
    public async Task Stale_save_preconditions_after_publication_do_not_create_a_new_version()
    {
        var seeded = Seed(); _runs.Runs.Add(FakeConfigTestRuns.Passed(Read(seeded.DraftId))); TestPrincipal.Current = seeded.Owner;
        using var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/workspaces/{seeded.WorkspaceId}/config/{seeded.DraftId}/publish", new { })).StatusCode);
        var response = await client.PostAsJsonAsync($"/api/workspaces/{seeded.WorkspaceId}/config/draft", new { configJson = "{}", publishedConfigId = seeded.PublishedId });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); Assert.Contains("Reload", await response.Content.ReadAsStringAsync());
        Assert.Equal(3, Count(seeded.WorkspaceId)); Assert.Equal("published", Read(seeded.DraftId).Status);
    }

    [Fact]
    public async Task Published_archived_and_discarded_versions_cannot_bypass_lifecycle_restrictions()
    {
        var seeded = Seed(); TestPrincipal.Current = seeded.Owner; using var client = _factory.CreateClient();
        foreach (var id in new[] { seeded.PublishedId, seeded.ArchivedId })
        {
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/workspaces/{seeded.WorkspaceId}/config/{id}/discard", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/workspaces/{seeded.WorkspaceId}/config/{id}/publish", new { })).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/workspaces/{seeded.WorkspaceId}/config/rollback/2", new { })).StatusCode);
        foreach (var _ in Enumerable.Range(0, 2))
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/workspaces/{seeded.WorkspaceId}/config/{seeded.DraftId}/discard", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/workspaces/{seeded.WorkspaceId}/config/{seeded.DraftId}/publish", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/workspaces/{seeded.WorkspaceId}/config/rollback/2", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/workspaces/{seeded.WorkspaceId}/config/rollback/999", new { })).StatusCode);
        Assert.Equal(3, Count(seeded.WorkspaceId)); Assert.Equal("discarded", Read(seeded.DraftId).Status);
    }

    [Fact]
    public async Task Newer_failure_during_publication_returns_conflict_without_archiving_published()
    {
        var seeded = Seed(); var passed = FakeConfigTestRuns.Passed(Read(seeded.DraftId));
        _runs.Handler = call => call == 1 ? passed : passed with { Id = Guid.NewGuid(), Status = "failed", StartedAt = passed.StartedAt.AddMinutes(1), CompletedAt = passed.CompletedAt!.Value.AddMinutes(1) };
        TestPrincipal.Current = seeded.Owner;
        var response = await _factory.CreateClient().PostAsJsonAsync($"/api/workspaces/{seeded.WorkspaceId}/config/{seeded.DraftId}/publish", new { });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); Assert.Contains("latest test run did not pass", await response.Content.ReadAsStringAsync());
        Assert.Equal("draft", Read(seeded.DraftId).Status); Assert.Equal("published", Read(seeded.PublishedId).Status);
    }
}
