using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClinicalRag.Tests;

[Collection("Sequential")]
public class WorkspaceManagementApiTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private static void SignIn(User user, bool verified = true) => TestPrincipal.Current = new(new ClaimsIdentity([
        new Claim(ClaimTypes.NameIdentifier, user.FirebaseUid), new Claim(ClaimTypes.Email, user.Email), new Claim("email_verified", verified ? "true" : "false")], "Test"));
    private async Task<(Workspace Workspace, User Owner, User Member, User Outsider, Document Document)> Seed(string role = "viewer")
    {
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        User User() => new() { FirebaseUid = Guid.NewGuid().ToString(), Email = $"{Guid.NewGuid():N}@example.org" };
        var owner = User(); var member = User(); var outsider = User();
        var workspace = new Workspace { Name = "Synthetic management", OwnerId = owner.Id };
        var document = new Document { WorkspaceId = workspace.Id, FileName = "Synthetic.pdf", ParsedText = "Source words.", PageCount = 1, Status = "parsed" };
        db.Users.AddRange(owner, member, outsider); db.Workspaces.Add(workspace); db.Documents.Add(document);
        db.WorkspaceMembers.AddRange(new WorkspaceMember { WorkspaceId = workspace.Id, UserId = owner.Id, Role = "owner" }, new WorkspaceMember { WorkspaceId = workspace.Id, UserId = member.Id, Role = role });
        await db.SaveChangesAsync(); return (workspace, owner, member, outsider, document);
    }

    [Theory]
    [InlineData("viewer")]
    [InlineData("editor")]
    [InlineData("reviewer")]
    public async Task Only_owners_can_invite_remove_rename_delete_or_administer_pending_invites(string role)
    {
        var data = await Seed(role); var root = $"/api/workspaces/{data.Workspace.Id}";
        try
        {
            SignIn(data.Member); using var client = factory.CreateClient();
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(root + "/invite", new { email = "invited@example.org", role = "viewer" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(root + "/invitations")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync(root + $"/invitations/{Guid.NewGuid()}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync(root + $"/members/{data.Owner.Id}?expectedRole=owner")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PatchAsJsonAsync(root, new { name = "Forged", expectedName = data.Workspace.Name })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync(root)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(root + "/leave", null)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(root)).StatusCode);
        }
        finally { TestPrincipal.Current = null; }
    }

    [Fact]
    public async Task Verified_first_sign_in_accepts_pending_invite_and_exposes_only_its_workspace()
    {
        var data = await Seed(); var invitee = new User { FirebaseUid = Guid.NewGuid().ToString(), Email = $"{Guid.NewGuid():N}@example.org" };
        try
        {
            using var client = factory.CreateClient(); SignIn(data.Owner);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/workspaces/{data.Workspace.Id}/invite", new { email = invitee.Email, role = "editor" })).StatusCode);
            SignIn(invitee, false);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/workspaces")).StatusCode);
            SignIn(invitee); var workspaces = await client.GetFromJsonAsync<Workspace[]>("/api/workspaces"); Assert.Equal(data.Workspace.Id, Assert.Single(workspaces!).Id);
            var access = await client.GetFromJsonAsync<JsonElement>($"/api/workspaces/{data.Workspace.Id}/rules/access"); Assert.Equal("editor", access.GetProperty("role").GetString());
        }
        finally { TestPrincipal.Current = null; }
    }

    [Fact]
    public async Task Last_owner_guards_are_enforced_through_http_and_owner_promotion_is_explicit()
    {
        var data = await Seed(); var root = $"/api/workspaces/{data.Workspace.Id}";
        try
        {
            SignIn(data.Owner); using var client = factory.CreateClient();
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync(root + "/leave", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync(root + $"/members/{data.Owner.Id}?expectedRole=owner")).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PatchAsJsonAsync(root + $"/members/{data.Owner.Id}/role", new { role = "viewer", expectedRole = "owner" })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync(root + $"/members/{data.Member.Id}/role", new { role = "owner", expectedRole = "viewer" })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(root + "/leave", null)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(root)).StatusCode);
            SignIn(data.Member); Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync(root + "/leave", null)).StatusCode);
        }
        finally { TestPrincipal.Current = null; }
    }

    [Fact]
    public async Task Soft_deleted_workspace_is_hidden_from_members_on_all_resource_routes_and_invites()
    {
        var data = await Seed(); var root = $"/api/workspaces/{data.Workspace.Id}";
        try
        {
            SignIn(data.Owner); using var client = factory.CreateClient(); Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync(root)).StatusCode);
            foreach (var person in new[] { data.Owner, data.Member, data.Outsider })
            {
                SignIn(person);
                foreach (var path in new[] { "", "/documents", $"/documents/{data.Document.Id}/chunker-guidance", "/chunks", "/rules", "/rules/access", "/config", "/analyses", "/members", "/invitations" })
                    Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(root + path)).StatusCode);
                Assert.DoesNotContain(await client.GetFromJsonAsync<Workspace[]>("/api/workspaces") ?? [], w => w.Id == data.Workspace.Id);
                Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(root + "/invite", new { email = "pending@example.org" })).StatusCode);
            }
        }
        finally { TestPrincipal.Current = null; }
    }

    [Fact]
    public async Task Bulk_import_rejects_spoofed_review_status_or_vectors_and_normal_import_is_pending()
    {
        var data = await Seed("editor"); var root = $"/api/workspaces/{data.Workspace.Id}/chunks/bulk";
        try
        {
            SignIn(data.Member); using var client = factory.CreateClient();
            foreach (var field in new[] { "id", "workspaceId", "status", "embedding", "reviewedBy" })
            {
                var body = new Dictionary<string, object> { ["documentId"] = data.Document.Id, ["page"] = 1, ["text"] = "Synthetic source", [field] = "forged" };
                Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(root, new[] { body })).StatusCode);
            }
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(root, new[] { new { documentId = data.Document.Id, page = 1, text = "Synthetic source" } })).StatusCode);
            using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal("pending", (await db.Chunks.SingleAsync(c => c.DocumentId == data.Document.Id)).Status);
        }
        finally { TestPrincipal.Current = null; }
    }
}
