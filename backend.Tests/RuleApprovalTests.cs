using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClinicalRag.Tests;

[Collection("Sequential")]
public class RuleApprovalTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    public RuleApprovalTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Editors_cannot_self_approve_rules_and_edits_invalidate_owner_review()
    {
        Guid workspaceId;
        Guid ownerId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var owner = db.Users.Add(new User { FirebaseUid = $"review-owner-{Guid.NewGuid()}", Email = "owner@example.com" }).Entity;
            var editor = db.Users.Add(new User { FirebaseUid = $"review-editor-{Guid.NewGuid()}", Email = "editor@example.com" }).Entity;
            var workspace = db.Workspaces.Add(new Workspace { Name = "Reviewed rules", OwnerId = owner.Id }).Entity;
            db.WorkspaceMembers.AddRange(
                new WorkspaceMember { WorkspaceId = workspace.Id, UserId = owner.Id, Role = "owner" },
                new WorkspaceMember { WorkspaceId = workspace.Id, UserId = editor.Id, Role = "editor" });
            await db.SaveChangesAsync();
            workspaceId = workspace.Id;
            ownerId = owner.Id;
            TestPrincipal.Current = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, editor.FirebaseUid) }, "Test"));
        }

        try
        {
            using var client = _factory.CreateClient();
            var created = await client.PostAsJsonAsync($"/api/workspaces/{workspaceId}/rules", new
            {
                ruleId = "review-required", title = "Medication check", reviewedAt = DateTime.UtcNow, reviewedBy = ownerId,
                drugClasses = "[\"antiplatelet\"]", appliesWhen = "{\"any\":[{\"field\":\"sbp\",\"op\":\"<\",\"value\":90}]}",
                level = "caution", message = "Review medication", caseLevel = true,
                status = "approved", enabled = true,
                sources = "[{\"title\":\"Synthetic source\",\"url\":\"https://example.org/source\"}]"
            });
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            var rule = await created.Content.ReadFromJsonAsync<JsonElement>();
            var id = rule.GetProperty("id").GetGuid();
            Assert.Equal(JsonValueKind.Null, rule.GetProperty("reviewedAt").ValueKind);
            Assert.Equal("draft", rule.GetProperty("status").GetString());
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/workspaces/{workspaceId}/rules/{id}/approve", new { expectedRevision = 1 })).StatusCode);

            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var owner = await db.Users.FindAsync(ownerId);
                TestPrincipal.Current = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, owner!.FirebaseUid) }, "Test"));
            }
            var approved = await client.PostAsJsonAsync($"/api/workspaces/{workspaceId}/rules/{id}/approve", new { expectedRevision = rule.GetProperty("revision").GetInt32() });
            Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
            var reviewedRule = await approved.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(ownerId, reviewedRule.GetProperty("reviewedBy").GetGuid());
            var snapshot = await client.GetAsync($"/api/workspaces/{workspaceId}/rules/approved-snapshot");
            Assert.Equal(HttpStatusCode.OK, snapshot.StatusCode);
            var snapshotJson = await snapshot.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Contains(snapshotJson.GetProperty("rules").EnumerateArray(),
                item => item.GetProperty("id").GetString() == "review-required");
            Assert.True(snapshotJson.GetProperty("catalogRevision").GetInt32() >= 0);
            var edited = await client.PatchAsJsonAsync($"/api/workspaces/{workspaceId}/rules/{id}", new { title = "Changed medication check", expectedRevision = reviewedRule.GetProperty("revision").GetInt32() });
            Assert.True(edited.StatusCode == HttpStatusCode.OK, await edited.Content.ReadAsStringAsync());
            var editedRule = await edited.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(JsonValueKind.Null, editedRule.GetProperty("reviewedAt").ValueKind);
            Assert.Equal(JsonValueKind.Null, editedRule.GetProperty("reviewedBy").ValueKind);
            Assert.Equal("review-required", editedRule.GetProperty("ruleId").GetString());
            Assert.Equal("[\"antiplatelet\"]", editedRule.GetProperty("drugClasses").GetString());
            Assert.Equal("Review medication", editedRule.GetProperty("message").GetString());
            Assert.True(editedRule.GetProperty("caseLevel").GetBoolean());
        }
        finally { TestPrincipal.Current = null; }
    }
}
