using System.Text.Json.Nodes;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using ClinicalRag.Api.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ClinicalRag.Tests;

public class ConfigServiceTests
{
    private static AppDbContext Database() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"Config_{Guid.NewGuid():N}").Options);

    private static ConfigVersion Published(AppDbContext db, Guid workspaceId, JsonObject? config = null)
    {
        var published = db.ConfigVersions.Add(new ConfigVersion
        {
            WorkspaceId = workspaceId, Version = 1, Status = "published",
            ConfigJson = (config ?? JsonNode.Parse(ConfigService.DefaultConfig())!.AsObject()).ToJsonString()
        }).Entity;
        db.SaveChanges();
        return published;
    }

    [Fact]
    public async Task Draft_deep_merges_edits_without_losing_untouched_or_unknown_keys()
    {
        using var db = Database();
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var config = JsonNode.Parse(ConfigService.DefaultConfig())!.AsObject();
        config["retrieval"]!["candidates"] = 55;
        config["retrieval"]!["queriesPerProblem"] = 3;
        config["retrieval"]!["maxChunks"] = 12;
        config["chunking"]!["custom"] = JsonNode.Parse("{\"keep\":[1,2]}");
        config["safety"]!["futureCheck"] = true;
        config["future"] = JsonNode.Parse("{\"untouched\":true,\"nested\":{\"left\":1,\"right\":2}}");
        var original = Published(db, workspaceId, config);
        var originalJson = original.ConfigJson;
        var edits = JsonNode.Parse("{\"retrieval\":{\"topK\":11},\"future\":{\"nested\":{\"right\":4}}}")!;
        edits["prompts"] = new JsonObject { ["plan"] = config["prompts"]!["plan"]!.GetValue<string>() + "\nKeep edits." };

        var draft = await new ConfigService(db).CreateDraftAsync(workspaceId, edits.ToJsonString(), userId);
        var saved = JsonNode.Parse(draft.ConfigJson)!;

        Assert.Equal(2, draft.Version);
        Assert.Equal("draft", draft.Status);
        Assert.Equal(userId, draft.CreatedBy);
        Assert.Equal(11, saved["retrieval"]!["topK"]!.GetValue<int>());
        Assert.Equal(55, saved["retrieval"]!["candidates"]!.GetValue<int>());
        Assert.Equal(3, saved["retrieval"]!["queriesPerProblem"]!.GetValue<int>());
        Assert.Equal(12, saved["retrieval"]!["maxChunks"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(config["chunking"], saved["chunking"]));
        Assert.True(saved["safety"]!["futureCheck"]!.GetValue<bool>());
        Assert.True(saved["future"]!["untouched"]!.GetValue<bool>());
        Assert.Equal(1, saved["future"]!["nested"]!["left"]!.GetValue<int>());
        Assert.Equal(4, saved["future"]!["nested"]!["right"]!.GetValue<int>());
        Assert.Equal(originalJson, original.ConfigJson);
        Assert.Equal("published", original.Status);
    }

    [Fact]
    public async Task Missing_legacy_keys_are_filled_from_defaults_and_verifier_is_always_locked()
    {
        using var db = Database();
        var workspaceId = Guid.NewGuid();
        Published(db, workspaceId, JsonNode.Parse("{\"retrieval\":{\"topK\":10},\"future\":{\"keep\":true}}")!.AsObject());

        var draft = await new ConfigService(db).CreateDraftAsync(workspaceId,
            "{\"safety\":{\"verifierLocked\":false}}", null);
        var saved = JsonNode.Parse(draft.ConfigJson)!;

        Assert.Equal(10, saved["retrieval"]!["topK"]!.GetValue<int>());
        Assert.Equal(30, saved["retrieval"]!["candidates"]!.GetValue<int>());
        Assert.Equal(2, saved["retrieval"]!["queriesPerProblem"]!.GetValue<int>());
        Assert.Equal(8, saved["retrieval"]!["maxChunks"]!.GetValue<int>());
        Assert.Equal(400, saved["chunking"]!["maxTokens"]!.GetValue<int>());
        Assert.True(saved["safety"]!["verifierLocked"]!.GetValue<bool>());
        Assert.Contains("{case}", saved["prompts"]!["plan"]!.GetValue<string>());
        Assert.Contains("{context}", saved["prompts"]!["answer"]!.GetValue<string>());
        Assert.Contains("{q}", saved["prompts"]!["rewrite"]!.GetValue<string>());
        Assert.True(saved["future"]!["keep"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("10.0", "35.0")]
    [InlineData("1e1", "3.5e1")]
    public async Task Draft_normalizes_schema_integer_numbers_and_preserves_literal_unknown_keys(string topK, string candidates)
    {
        using var db = Database();
        var workspaceId = Guid.NewGuid();
        var config = JsonNode.Parse(ConfigService.DefaultConfig())!.AsObject();
        config["future"] = JsonNode.Parse("{\"a\":{\"b\":1},\"a.b\":2,\"\":3}");
        var published = Published(db, workspaceId, config);

        var draft = await new ConfigService(db).CreateDraftAsync(workspaceId,
            $"{{\"retrieval\":{{\"topK\":{topK},\"candidates\":{candidates}}},\"future\":{{\"a.b\":4}}}}", null, published.Id);
        var saved = JsonNode.Parse(draft.ConfigJson)!;

        Assert.Equal(10, saved["retrieval"]!["topK"]!.GetValue<int>());
        Assert.Equal(35, saved["retrieval"]!["candidates"]!.GetValue<int>());
        Assert.Equal(1, saved["future"]!["a"]!["b"]!.GetValue<int>());
        Assert.Equal(4, saved["future"]!["a.b"]!.GetValue<int>());
        Assert.Equal(3, saved["future"]![""]!.GetValue<int>());
        Assert.True(saved["safety"]!["verifierLocked"]!.GetValue<bool>());
        Assert.Equal(draft.ConfigJson, ConfigValidator.Normalize(draft.ConfigJson));
    }

    [Theory]
    [InlineData("{\"retrieval\":{\"topK\":8,\"topK\":10}}")]
    [InlineData("{\"future\":{\"a\":{\"keep\":1,\"keep\":2}}}")]
    [InlineData("{\"future\":[{\"keep\":1,\"keep\":2}]}")]
    public async Task Duplicate_keys_are_rejected_before_a_first_draft_or_bootstrap_can_be_created(string configJson)
    {
        using var db = Database();
        var error = await Assert.ThrowsAsync<ConfigValidationException>(() => new ConfigService(db)
            .CreateDraftAsync(Guid.NewGuid(), configJson, null));

        Assert.Contains("duplicate keys", error.Errors.Single());
        Assert.Empty(db.ConfigVersions);
    }

    [Theory]
    [InlineData("{\"retrieval\":{\"minRerank\":-0.01}}")]
    [InlineData("{\"retrieval\":{\"minRerank\":1.01}}")]
    [InlineData("{\"retrieval\":{\"topK\":0}}")]
    [InlineData("{\"retrieval\":{\"topK\":21}}")]
    [InlineData("{\"retrieval\":{\"topK\":1.5}}")]
    [InlineData("{\"retrieval\":{\"topK\":true}}")]
    [InlineData("{\"retrieval\":{\"candidates\":4}}")]
    [InlineData("{\"retrieval\":{\"candidates\":101}}")]
    [InlineData("{\"retrieval\":{\"queriesPerProblem\":0}}")]
    [InlineData("{\"retrieval\":{\"queriesPerProblem\":6}}")]
    [InlineData("{\"retrieval\":{\"minRerank\":\"0.3\"}}")]
    [InlineData("{\"retrieval\":{\"candidates\":null}}")]
    [InlineData("{\"retrieval\":[]}")]
    [InlineData("{\"prompts\":null}")]
    [InlineData("{\"chunking\":false}")]
    [InlineData("{\"safety\":null}")]
    [InlineData("{\"safety\":{\"minQuoteMatch\":0.1}}")]
    [InlineData("{\"prompts\":{\"plan\":\"Missing variables\"}}")]
    [InlineData("{\"prompts\":{\"answer\":\"{problem} {patient} {k}\"}}")]
    [InlineData("{\"prompts\":{\"rewrite\":\"\"}}")]
    [InlineData("{\"prompts\":{\"plan\":null}}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{invalid json}")]
    public async Task Invalid_drafts_are_rejected_without_persisting_a_version(string edits)
    {
        using var db = Database();
        var workspaceId = Guid.NewGuid();
        Published(db, workspaceId);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => new ConfigService(db)
            .CreateDraftAsync(workspaceId, edits, null));

        Assert.Single(db.ConfigVersions);
        Assert.Equal("published", db.ConfigVersions.Single().Status);
    }

    [Theory]
    [InlineData(0.0, 1, 5, 1)]
    [InlineData(1.0, 20, 100, 5)]
    public async Task Inclusive_range_boundaries_are_valid(double minRerank, int topK, int candidates, int queries)
    {
        using var db = Database();
        var workspaceId = Guid.NewGuid();
        Published(db, workspaceId);
        var edits = new JsonObject
        {
            ["retrieval"] = new JsonObject
            {
                ["minRerank"] = minRerank, ["topK"] = topK,
                ["candidates"] = candidates, ["queriesPerProblem"] = queries
            }
        };

        var draft = await new ConfigService(db).CreateDraftAsync(workspaceId, edits.ToJsonString(), null);

        Assert.Equal("draft", draft.Status);
    }

    [Fact]
    public async Task First_draft_bootstraps_a_distinct_published_version()
    {
        using var db = Database();
        var workspaceId = Guid.NewGuid();
        var service = new ConfigService(db);

        var draft = await service.CreateDraftAsync(workspaceId, "{}", null);
        var published = await service.GetPublishedAsync(workspaceId);

        Assert.Equal(1, published.Version);
        Assert.Equal(2, draft.Version);
        Assert.NotEqual(draft.Id, published.Id);
    }

    [Fact]
    public async Task Draft_without_a_real_test_run_cannot_publish()
    {
        using var db = Database();
        var workspaceId = Guid.NewGuid();
        var published = Published(db, workspaceId);
        var service = new ConfigService(db);
        var draft = await service.CreateDraftAsync(workspaceId, "{}", null);

        var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => service.PublishAsync(workspaceId, draft.Id));

        Assert.Contains("no test run", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("published", published.Status);
        Assert.Equal("draft", draft.Status);
    }
}
