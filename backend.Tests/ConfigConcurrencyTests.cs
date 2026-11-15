using System.Text.Json.Nodes;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using ClinicalRag.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ClinicalRag.Tests;

public class ConfigConcurrencyTests
{
    private sealed class CaseInsensitiveDbContext : AppDbContext
    {
        public CaseInsensitiveDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<ConfigVersion>().Property(version => version.ConfigJson).UseCollation("NOCASE");
        }
    }

    [Theory]
    [InlineData("discarded")]
    [InlineData("published")]
    [InlineData("changed content")]
    public async Task A_stale_tracked_draft_cannot_overwrite_a_concurrent_lifecycle_or_content_change(string change)
    {
        using var connection = new SqliteConnection("DataSource=:memory:"); connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        using var seed = new AppDbContext(options); seed.Database.EnsureCreated();
        var service = new ConfigService(seed); var published = await service.GetPublishedAsync(Guid.NewGuid());
        var draft = await service.CreateDraftAsync(published.WorkspaceId, "{}", null);
        using var stale = new AppDbContext(options);
        var tracked = await stale.ConfigVersions.SingleAsync(version => version.Id == draft.Id);
        var provider = new FakeConfigTestRuns(); provider.Runs.Add(FakeConfigTestRuns.Passed(tracked));
        using (var concurrent = new AppDbContext(options))
        {
            var changed = await concurrent.ConfigVersions.SingleAsync(version => version.Id == draft.Id);
            if (change == "changed content") changed.ConfigJson = changed.ConfigJson.Replace("\"topK\":8", "\"topK\":9");
            else changed.Status = change;
            await concurrent.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => new ConfigService(stale, provider).PublishAsync(draft.WorkspaceId, draft.Id));

        using var read = new AppDbContext(options);
        Assert.Equal("published", (await read.ConfigVersions.SingleAsync(version => version.Id == published.Id)).Status);
        var saved = await read.ConfigVersions.SingleAsync(version => version.Id == draft.Id);
        Assert.Equal(change == "changed content" ? "draft" : change, saved.Status);
        if (change == "changed content") Assert.Contains("\"topK\":9", saved.ConfigJson);
    }

    [Fact]
    public async Task Exact_artifact_guards_do_not_depend_on_case_sensitive_database_collation()
    {
        using var connection = new SqliteConnection("DataSource=:memory:"); connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        using var seed = new CaseInsensitiveDbContext(options); seed.Database.EnsureCreated();
        var service = new ConfigService(seed); var published = await service.GetPublishedAsync(Guid.NewGuid());
        var draft = await service.CreateDraftAsync(published.WorkspaceId, "{\"future\":{\"marker\":\"upper\"}}", null);
        using var stale = new CaseInsensitiveDbContext(options);
        var tracked = await stale.ConfigVersions.SingleAsync(version => version.Id == draft.Id);
        var provider = new FakeConfigTestRuns(); provider.Runs.Add(FakeConfigTestRuns.Passed(tracked));
        using (var concurrent = new CaseInsensitiveDbContext(options))
        {
            var changed = await concurrent.ConfigVersions.SingleAsync(version => version.Id == draft.Id);
            changed.ConfigJson = changed.ConfigJson.Replace("\"upper\"", "\"UPPER\"", StringComparison.Ordinal);
            await concurrent.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => new ConfigService(stale, provider).PublishAsync(draft.WorkspaceId, draft.Id));

        using var read = new CaseInsensitiveDbContext(options);
        Assert.Equal("published", (await read.ConfigVersions.SingleAsync(version => version.Id == published.Id)).Status);
        Assert.Equal("draft", (await read.ConfigVersions.SingleAsync(version => version.Id == draft.Id)).Status);
        Assert.Equal("UPPER", JsonNode.Parse((await read.ConfigVersions.SingleAsync(version => version.Id == draft.Id)).ConfigJson)!["future"]!["marker"]!.GetValue<string>());
    }

    [Fact]
    public async Task Discard_cannot_overwrite_a_concurrently_published_draft()
    {
        using var connection = new SqliteConnection("DataSource=:memory:"); connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        using var seed = new AppDbContext(options); seed.Database.EnsureCreated();
        var service = new ConfigService(seed); var published = await service.GetPublishedAsync(Guid.NewGuid());
        var draft = await service.CreateDraftAsync(published.WorkspaceId, "{}", null);
        using var stale = new AppDbContext(options);
        await stale.ConfigVersions.SingleAsync(version => version.Id == draft.Id);
        var provider = new FakeConfigTestRuns(); provider.Runs.Add(FakeConfigTestRuns.Passed(draft));
        await new ConfigService(seed, provider).PublishAsync(draft.WorkspaceId, draft.Id);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => new ConfigService(stale).DiscardAsync(draft.WorkspaceId, draft.Id));

        using var read = new AppDbContext(options);
        Assert.Equal("published", (await read.ConfigVersions.SingleAsync(version => version.Id == draft.Id)).Status);
        Assert.Equal("archived", (await read.ConfigVersions.SingleAsync(version => version.Id == published.Id)).Status);
    }
}
