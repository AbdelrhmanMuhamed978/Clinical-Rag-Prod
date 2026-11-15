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
public class RuleValidationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    public RuleValidationTests(CustomWebApplicationFactory factory) => _factory = factory;

    private async Task<Guid> WorkspaceAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var owner = db.Users.Add(new User { FirebaseUid = $"validation-{Guid.NewGuid()}", Email = "owner@example.org" }).Entity;
        var workspace = db.Workspaces.Add(new Workspace { Name = "Rule validation", OwnerId = owner.Id }).Entity;
        db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = workspace.Id, UserId = owner.Id, Role = "owner" });
        await db.SaveChangesAsync();
        TestPrincipal.Current = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, owner.FirebaseUid) }, "Test"));
        return workspace.Id;
    }

    public static Dictionary<string, object?> ValidRule() => new()
    {
        ["ruleId"] = $"synthetic-{Guid.NewGuid():N}", ["title"] = "Synthetic review", ["message"] = "Test only.",
        ["level"] = "individual_review", ["drugClasses"] = "[\"nsaid\"]", ["caseLevel"] = false,
        ["appliesWhen"] = "{\"any\":[{\"field\":\"creatinine\",\"op\":\">=\",\"value\":1.5}]}",
        ["sources"] = "[{\"title\":\"Synthetic source\",\"url\":\"https://example.org/source\",\"quote\":\"Test evidence.\"}]",
        ["linkTerms"] = "kidney|\\bAKI\\b"
    };

    [Theory]
    [InlineData("appliesWhen", "not json")]
    [InlineData("appliesWhen", "[]")]
    [InlineData("appliesWhen", "{\"any\":[]}")]
    [InlineData("appliesWhen", "{\"or\":[{\"field\":\"sbp\",\"op\":\"<\",\"value\":90}]}")]
    [InlineData("appliesWhen", "{\"any\":[{\"field\":\"aki\",\"op\":\"<\",\"value\":1}]}")]
    [InlineData("appliesWhen", "{\"any\":[{\"field\":\"sbp\",\"op\":\"==\",\"value\":90}]}")]
    [InlineData("appliesWhen", "{\"any\":[{\"field\":\"sbp\",\"op\":\"<\",\"value\":\"90\"}]}")]
    [InlineData("appliesWhen", "{\"any\":[{\"field\":\"sbp\",\"op\":\"<\",\"value\":true}]}")]
    [InlineData("appliesWhen", "{\"any\":[{\"field\":\"sbp\",\"op\":\"<\",\"value\":1e999}]}")]
    [InlineData("appliesWhen", "{\"any\":[{\"field\":\"sbp\",\"op\":\"<\",\"value\":90,\"extra\":true}]}")]
    [InlineData("appliesWhen", "{\"any\":[{\"field\":\"sbp\",\"op\":\"<\",\"value\":90}],\"extra\":true}")]
    [InlineData("appliesWhen", "{\"any\":[{\"field\":\"sbp\",\"field\":\"dbp\",\"op\":\"<\",\"value\":90}]}")]
    [InlineData("appliesWhen", "{\"any\":[{\"field\":\"sbp\",\"op\":\"<\",\"value\":90}],\"all\":[{\"field\":\"sbp\",\"op\":\"<\",\"value\":90}]}")]
    [InlineData("drugClasses", "[\"unknown\"]")]
    [InlineData("drugClasses", "{\"nsaid\":true}")]
    [InlineData("drugClasses", "[]")]
    [InlineData("sources", "[]")]
    [InlineData("sources", "[{\"title\":\"Source\"}]")]
    [InlineData("sources", "[{\"url\":\"https://example.org\"}]")]
    [InlineData("sources", "[{\"title\":\"Source\",\"url\":\"javascript:alert(1)\"}]")]
    [InlineData("sources", "[{\"title\":\"Source\",\"url\":\"https://user:secret@example.org\"}]")]
    [InlineData("linkTerms", "[")]
    [InlineData("linkTerms", "(?<name>x)")]
    [InlineData("linkTerms", "\\p{L}")]
    [InlineData("linkTerms", "(x)\\1")]
    [InlineData("linkTerms", "(?=kidney)")]
    public async Task Invalid_rule_definitions_are_rejected_on_create_and_partial_update(string field, string invalid)
    {
        var workspaceId = await WorkspaceAsync();
        try
        {
            using var client = _factory.CreateClient();
            var payload = ValidRule(); payload[field] = invalid;
            var rejected = await client.PostAsJsonAsync($"/api/workspaces/{workspaceId}/rules", payload);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            payload = ValidRule();
            var created = await client.PostAsJsonAsync($"/api/workspaces/{workspaceId}/rules", payload);
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            var patch = await client.PatchAsJsonAsync($"/api/workspaces/{workspaceId}/rules/{id}", new Dictionary<string, object?> { [field] = invalid });
            Assert.Equal(HttpStatusCode.BadRequest, patch.StatusCode);
            var saved = (await client.GetFromJsonAsync<JsonElement>($"/api/workspaces/{workspaceId}/rules")).EnumerateArray().Single();
            Assert.Equal(payload[field], saved.GetProperty(field).GetString());
        }
        finally { TestPrincipal.Current = null; }
    }

    [Theory]
    [InlineData("any", false)]
    [InlineData("all", true)]
    public async Task Known_fields_and_classes_are_accepted_with_exact_seed_shape(string group, bool caseLevel)
    {
        var workspaceId = await WorkspaceAsync();
        try
        {
            using var client = _factory.CreateClient();
            var payload = ValidRule();
            payload["caseLevel"] = caseLevel;
            payload["drugClasses"] = "[\"anticoagulant\",\"metformin\",\"aminoglycoside\",\"nephrotoxin\",\"qt_prolonging\",\"potassium_sparing\"]";
            var fields = "sbp dbp map hr rr spo2 temp hb wbc platelets creatinine potassium magnesium calcium albumin alt ast bilirubin shock_index qsofa corrected_ca age".Split(' ');
            payload["appliesWhen"] = JsonSerializer.Serialize(new Dictionary<string, object> { [group] = fields.Select(field => new { field, op = ">=", value = 1.0 }) });
            var result = await client.PostAsJsonAsync($"/api/workspaces/{workspaceId}/rules", payload);
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            var saved = await result.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("draft", saved.GetProperty("status").GetString());
            Assert.True(saved.GetProperty("enabled").GetBoolean());
        }
        finally { TestPrincipal.Current = null; }
    }

    [Fact]
    public async Task Empty_and_null_partial_updates_preserve_case_level_and_other_fields()
    {
        var workspaceId = await WorkspaceAsync();
        try
        {
            using var client = _factory.CreateClient();
            var payload = ValidRule(); payload["caseLevel"] = true;
            var created = await client.PostAsJsonAsync($"/api/workspaces/{workspaceId}/rules", payload);
            var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            foreach (var body in new[] { new Dictionary<string, object?>(), new Dictionary<string, object?> { ["caseLevel"] = null, ["title"] = null } })
            {
                var updated = await client.PatchAsJsonAsync($"/api/workspaces/{workspaceId}/rules/{id}", body);
                Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
                var saved = await updated.Content.ReadFromJsonAsync<JsonElement>();
                Assert.True(saved.GetProperty("caseLevel").GetBoolean());
                Assert.Equal("Synthetic review", saved.GetProperty("title").GetString());
            }
        }
        finally { TestPrincipal.Current = null; }
    }
}
