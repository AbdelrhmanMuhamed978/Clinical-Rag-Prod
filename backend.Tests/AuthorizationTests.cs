using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using ClinicalRag.Api.Attributes;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClinicalRag.Tests;

[Collection("Sequential")]
public class AuthorizationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    public AuthorizationTests(CustomWebApplicationFactory factory) => _factory = factory;

    private sealed class Data
    {
        public string UserAFirebaseUid = "uid-user-a";
        public string UserBFirebaseUid = "uid-user-b";
        public string UserCFirebaseUid = "uid-user-c"; // exists, but not a member of workspaceB
        public string UserBEmail = "b@example.com";
        public Guid WorkspaceAId; // userA owner
        public Guid WorkspaceBId; // userB owner, userA viewer
        public Guid DocAId;
        public Guid DocBId;
        public Guid ChunkBId;
        public Guid RuleBId;
        public Guid TestBId;
        public Guid ConfigBId;
    }

    private Data Seed()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var userA = db.Users.FirstOrDefault(u => u.FirebaseUid == "uid-user-a")
            ?? db.Users.Add(new User { FirebaseUid = "uid-user-a", Email = "a@example.com" }).Entity;
        var userB = db.Users.FirstOrDefault(u => u.FirebaseUid == "uid-user-b")
            ?? db.Users.Add(new User { FirebaseUid = "uid-user-b", Email = "b@example.com" }).Entity;
        var userC = db.Users.FirstOrDefault(u => u.FirebaseUid == "uid-user-c")
            ?? db.Users.Add(new User { FirebaseUid = "uid-user-c", Email = "c@example.com" }).Entity;
        db.SaveChanges();

        var wsA = db.Workspaces.Add(new Workspace { Id = Guid.NewGuid(), Name = "WS-A", OwnerId = userA.Id }).Entity;
        var wsB = db.Workspaces.Add(new Workspace { Id = Guid.NewGuid(), Name = "WS-B", OwnerId = userB.Id }).Entity;
        db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = wsA.Id, UserId = userA.Id, Role = "owner" });
        db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = wsB.Id, UserId = userB.Id, Role = "owner" });
        db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = wsB.Id, UserId = userA.Id, Role = "viewer" }); // userA is only a viewer in B
        db.SaveChanges();

        var docA = db.Documents.Add(new Document { Id = Guid.NewGuid(), WorkspaceId = wsA.Id, FileName = "a.pdf", Status = "uploaded" }).Entity;
        var docB = db.Documents.Add(new Document { Id = Guid.NewGuid(), WorkspaceId = wsB.Id, FileName = "b.pdf", Status = "uploaded" }).Entity;
        var chunkB = db.Chunks.Add(new Chunk { Id = Guid.NewGuid(), WorkspaceId = wsB.Id, DocumentId = docB.Id, Page = 1, Text = "chunk", Status = "pending", Confidence = 0.8 }).Entity;
        var ruleB = db.Rules.Add(new Rule { Id = Guid.NewGuid(), WorkspaceId = wsB.Id, RuleId = "r1", Title = "Rule 1" }).Entity;
        var testB = db.TestCases.Add(new TestCase { Id = Guid.NewGuid(), WorkspaceId = wsB.Id, Name = "t1", InputText = "hi" }).Entity;
        var configB = db.ConfigVersions.Add(new ConfigVersion { Id = Guid.NewGuid(), WorkspaceId = wsB.Id, Version = 1, Status = "draft", ConfigJson = "{}" }).Entity;
        db.SaveChanges();

        return new Data
        {
            WorkspaceAId = wsA.Id,
            WorkspaceBId = wsB.Id,
            DocAId = docA.Id,
            DocBId = docB.Id,
            ChunkBId = chunkB.Id,
            RuleBId = ruleB.Id,
            TestBId = testB.Id,
            ConfigBId = configB.Id,
        };
    }

    private static ClaimsPrincipal Principal(string firebaseUid, string email) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, firebaseUid),
            new Claim(ClaimTypes.Email, email),
            new Claim("email_verified", "true"),
        }, "Test"));

    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string url, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        if (body != null) req.Content = JsonContent.Create(body);
        return await client.SendAsync(req);
    }

    // userA is NOT a member of workspaceB at all -> every workspaceB endpoint returns 404 (existence not revealed).
    [Fact]
    public async Task Non_member_cannot_reach_workspace_B_returns_404_on_every_endpoint()
    {
        var d = Seed();
        var client = _factory.CreateClient();
        var userC = Principal(d.UserCFirebaseUid, "c@example.com"); // not a member of workspaceB
        var b = d.WorkspaceBId;

        var endpoints = new (HttpMethod method, string path, object? body)[]
        {
            (HttpMethod.Get, $"/api/workspaces/{b}/documents", null),
            (HttpMethod.Get, $"/api/workspaces/{b}/documents/{d.DocBId}", null),
            (HttpMethod.Get, $"/api/workspaces/{b}/documents/{d.DocBId}/chunking-progress", null),
            (HttpMethod.Get, $"/api/workspaces/{b}/documents/{d.DocBId}/embedding-progress", null),
            (HttpMethod.Get, $"/api/workspaces/{b}/chunks?documentId={d.DocBId}", null),
            (HttpMethod.Get, $"/api/workspaces/{b}/rules", null),
            (HttpMethod.Get, $"/api/workspaces/{b}/tests", null),
            (HttpMethod.Get, $"/api/workspaces/{b}/config/published", null),
            (HttpMethod.Get, $"/api/workspaces/{b}/members", null),
            (HttpMethod.Post, $"/api/workspaces/{b}/documents", null),
            (HttpMethod.Delete, $"/api/workspaces/{b}/documents/{d.DocBId}", null),
            (HttpMethod.Post, $"/api/workspaces/{b}/documents/{d.DocBId}/parse", new { }),
            (HttpMethod.Post, $"/api/workspaces/{b}/documents/{d.DocBId}/chunk-portions", new { startPage = 1, endPage = 1 }),
            (HttpMethod.Patch, $"/api/workspaces/{b}/documents/{d.DocBId}/status", new { status = "parsed" }),
            (HttpMethod.Post, $"/api/workspaces/{b}/documents/{d.DocBId}/generate-embeddings", new { }),
            (HttpMethod.Patch, $"/api/workspaces/{b}/chunks/{d.ChunkBId}", new { status = "accepted" }),
            (HttpMethod.Post, $"/api/workspaces/{b}/chunks/bulk-accept", new { documentId = d.DocBId }),
            (HttpMethod.Post, $"/api/workspaces/{b}/chunks/bulk", new List<object>()),
            (HttpMethod.Post, $"/api/workspaces/{b}/chunks/merge", new { ids = new[] { d.ChunkBId } }),
            (HttpMethod.Post, $"/api/workspaces/{b}/rules", new { ruleId = "r2", title = "t" }),
            (HttpMethod.Patch, $"/api/workspaces/{b}/rules/{d.RuleBId}", new { title = "t2" }),
            (HttpMethod.Delete, $"/api/workspaces/{b}/rules/{d.RuleBId}", null),
            (HttpMethod.Post, $"/api/workspaces/{b}/tests", new { name = "t2", inputText = "x" }),
            (HttpMethod.Patch, $"/api/workspaces/{b}/tests/{d.TestBId}", new { name = "t2" }),
            (HttpMethod.Post, $"/api/workspaces/{b}/tests/run", new { }),
            (HttpMethod.Post, $"/api/workspaces/{b}/config/draft", new { configJson = "{}" }),
            (HttpMethod.Post, $"/api/workspaces/{b}/config/{d.ConfigBId}/publish", null),
            (HttpMethod.Post, $"/api/workspaces/{b}/analyze", new { message = "hi" }),
            (HttpMethod.Post, $"/api/workspaces/{b}/analyze/stream", new { message = "hi" }),
            (HttpMethod.Post, $"/api/workspaces/{b}/analyze/retrieve", new { query = "hi" }),
            (HttpMethod.Get, $"/api/workspaces/{b}/analyses?mine=true", null),
            (HttpMethod.Get, $"/api/workspaces/{b}/analyses/{Guid.NewGuid()}", null),
            (HttpMethod.Delete, $"/api/workspaces/{b}/analyses/{Guid.NewGuid()}", null),
        };

        var failures = new List<string>();
        foreach (var (method, path, body) in endpoints)
        {
            TestPrincipal.Current = userC;
            var res = await Send(client, method, path, body);
            if (res.StatusCode != HttpStatusCode.NotFound)
                failures.Add($"{method} {path} -> {res.StatusCode} (expected 404)");
        }
        TestPrincipal.Current = null;
        Assert.Empty(failures);
    }

    // userA is a VIEWER in workspaceB -> reads succeed, every write returns 403.
    [Fact]
    public async Task Viewer_in_workspace_B_gets_403_on_writes_but_can_read()
    {
        var d = Seed();
        var client = _factory.CreateClient();
        var userA = Principal(d.UserAFirebaseUid, "a@example.com");
        var b = d.WorkspaceBId;

        // Reads succeed for a viewer.
        TestPrincipal.Current = userA;
        var docs = await client.GetAsync($"/api/workspaces/{b}/documents");
        Assert.Equal(HttpStatusCode.OK, docs.StatusCode);
        var rules = await client.GetAsync($"/api/workspaces/{b}/rules");
        Assert.Equal(HttpStatusCode.OK, rules.StatusCode);
        var progress = await client.GetAsync($"/api/workspaces/{b}/documents/{d.DocBId}/chunking-progress");
        Assert.Equal(HttpStatusCode.OK, progress.StatusCode);
        var embeddingProgress = await client.GetAsync($"/api/workspaces/{b}/documents/{d.DocBId}/embedding-progress");
        Assert.Equal(HttpStatusCode.OK, embeddingProgress.StatusCode);
        TestPrincipal.Current = null;

        var writeEndpoints = new (HttpMethod method, string path, object? body)[]
        {
            (HttpMethod.Post, $"/api/workspaces/{b}/documents", null),
            (HttpMethod.Delete, $"/api/workspaces/{b}/documents/{d.DocBId}", null),
            (HttpMethod.Post, $"/api/workspaces/{b}/documents/{d.DocBId}/parse", new { }),
            (HttpMethod.Post, $"/api/workspaces/{b}/documents/{d.DocBId}/chunk-portions", new { startPage = 1, endPage = 1 }),
            (HttpMethod.Patch, $"/api/workspaces/{b}/documents/{d.DocBId}/status", new { status = "parsed" }),
            (HttpMethod.Post, $"/api/workspaces/{b}/documents/{d.DocBId}/generate-embeddings", new { }),
            (HttpMethod.Patch, $"/api/workspaces/{b}/chunks/{d.ChunkBId}", new { status = "accepted" }),
            (HttpMethod.Post, $"/api/workspaces/{b}/chunks/bulk-accept", new { documentId = d.DocBId }),
            (HttpMethod.Post, $"/api/workspaces/{b}/chunks/bulk", new List<object>()),
            (HttpMethod.Post, $"/api/workspaces/{b}/chunks/merge", new { ids = new[] { d.ChunkBId } }),
            (HttpMethod.Post, $"/api/workspaces/{b}/rules", new { ruleId = "r2", title = "t" }),
            (HttpMethod.Patch, $"/api/workspaces/{b}/rules/{d.RuleBId}", new { title = "t2" }),
            (HttpMethod.Delete, $"/api/workspaces/{b}/rules/{d.RuleBId}", null),
            (HttpMethod.Post, $"/api/workspaces/{b}/tests", new { name = "t2", inputText = "x" }),
            (HttpMethod.Patch, $"/api/workspaces/{b}/tests/{d.TestBId}", new { name = "t2" }),
            (HttpMethod.Post, $"/api/workspaces/{b}/tests/run", new { }),
            (HttpMethod.Post, $"/api/workspaces/{b}/config/draft", new { configJson = "{}" }),
            (HttpMethod.Post, $"/api/workspaces/{b}/config/{d.ConfigBId}/publish", null),
            (HttpMethod.Post, $"/api/workspaces/{b}/analyze", new { message = "hi" }),
            (HttpMethod.Post, $"/api/workspaces/{b}/analyze/stream", new { message = "hi" }),
            (HttpMethod.Get, $"/api/workspaces/{b}/members", null),
        };

        var failures = new List<string>();
        foreach (var (method, path, body) in writeEndpoints)
        {
            TestPrincipal.Current = userA;
            var res = await Send(client, method, path, body);
            if (res.StatusCode != HttpStatusCode.Forbidden)
                failures.Add($"{method} {path} -> {res.StatusCode} (expected 403)");
        }
        TestPrincipal.Current = null;
        Assert.Empty(failures);
    }

    // Positive control: an owner can write in their own workspace.
    [Fact]
    public async Task Owner_can_write_own_workspace_positive_control()
    {
        var d = Seed();
        var client = _factory.CreateClient();
        var userB = Principal(d.UserBFirebaseUid, "b@example.com");
        var b = d.WorkspaceBId;

        TestPrincipal.Current = userB;
        var rules = await client.PostAsJsonAsync($"/api/workspaces/{b}/rules", RuleValidationTests.ValidRule());
        Assert.Equal(HttpStatusCode.OK, rules.StatusCode);

        var draft = await client.PostAsJsonAsync($"/api/workspaces/{b}/config/draft", new { configJson = "{}" });
        Assert.Equal(HttpStatusCode.OK, draft.StatusCode);

        var members = await client.GetAsync($"/api/workspaces/{b}/members");
        Assert.Equal(HttpStatusCode.OK, members.StatusCode);
        TestPrincipal.Current = null;
    }

    // Reflection guard: every controller action that binds a workspaceId route parameter
    // must carry WorkspaceAuthorizeAttribute on the method or its controller class.
    [Fact]
    public void Every_action_with_workspaceId_parameter_has_WorkspaceAuthorizeAttribute()
    {
        var apiAssembly = typeof(Program).Assembly;
        var missing = new List<string>();
        foreach (var type in apiAssembly.GetTypes())
        {
            if (!type.Name.EndsWith("Controller")) continue;
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (method.GetParameters().All(p => p.Name != "workspaceId")) continue;
                bool has = method.IsDefined(typeof(WorkspaceAuthorizeAttribute))
                    || type.IsDefined(typeof(WorkspaceAuthorizeAttribute), false);
                if (!has) missing.Add($"{type.Name}.{method.Name}");
            }
        }
        Assert.Empty(missing);
    }
}
