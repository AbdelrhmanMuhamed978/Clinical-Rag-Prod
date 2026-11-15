using System.Text.Json;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using ClinicalRag.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace ClinicalRag.Tests;

internal static class RuleTestDefinitions
{
    public static CreateRuleDto Create(string? key = null, bool caseLevel = false, bool enabled = true) => new(
        key ?? $"screen-{Guid.NewGuid():N}", "Synthetic medication review", caseLevel ? "[]" : "[\"nsaid\"]",
        "{\"any\":[{\"field\":\"creatinine\",\"op\":\">=\",\"value\":1.5}]}", "individual_review", "Test only.",
        "Synthetic fixture, not clinical advice.", "[{\"title\":\"Synthetic source\",\"url\":\"https://example.org/source\",\"quote\":\"Test evidence.\",\"provenance\":{\"keep\":true}}]",
        "kidney|\\bAKI\\b", caseLevel, enabled);

    public static JsonElement Catalog => JsonSerializer.SerializeToElement(new Dictionary<string, object>
    {
        ["syntheticdrug"] = new { classes = new[] { "nsaid", "nephrotoxin" }, brands = new[] { "Synthetic Brand" }, regional_names = new[] { "اسم تجريبي" } }
    });
}

public class RuleLifecycleTests
{
    private static DbContextOptions<AppDbContext> Options(string? name = null, IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name ?? $"Rules_{Guid.NewGuid():N}");
        if (interceptor != null) builder.AddInterceptors(interceptor);
        return builder.Options;
    }

    private static async Task<(User Owner, Workspace Workspace)> SeedAsync(AppDbContext db)
    {
        var owner = db.Users.Add(new User { FirebaseUid = $"owner-{Guid.NewGuid():N}", Email = "synthetic@example.org" }).Entity;
        var workspace = db.Workspaces.Add(new Workspace { Name = "Synthetic rules", OwnerId = owner.Id }).Entity;
        db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = workspace.Id, UserId = owner.Id, Role = "owner" });
        await db.SaveChangesAsync();
        return (owner, workspace);
    }

    [Fact]
    public async Task Actual_edits_require_the_saved_revision_but_null_empty_and_exact_noops_preserve_review()
    {
        await using var db = new AppDbContext(Options());
        var (owner, workspace) = await SeedAsync(db);
        var service = new RuleService(db);
        var rule = await service.CreateAsync(workspace.Id, RuleTestDefinitions.Create(), owner.Id);
        Assert.Empty((await service.ApprovedSnapshotAsync(workspace.Id)).Rules);
        await service.TransitionAsync(workspace.Id, rule.Id, "approve", 1, owner.Id);
        var reviewedAt = rule.ReviewedAt;
        foreach (var dto in new[] { new UpdateRuleDto(), new UpdateRuleDto(CaseLevel: null, Title: null),
                     new UpdateRuleDto(Title: rule.Title, ExpectedRevision: 2) })
        {
            var saved = await service.UpdateAsync(workspace.Id, rule.Id, dto, owner.Id);
            Assert.Equal(2, saved!.Revision);
            Assert.Equal("approved", saved.Status);
            Assert.Equal(reviewedAt, saved.ReviewedAt);
        }
        Assert.Equal(2, await db.RuleAuditEntries.CountAsync());
        var missing = await Assert.ThrowsAsync<RuleLifecycleException>(() => service.UpdateAsync(workspace.Id, rule.Id, new UpdateRuleDto(Title: "Changed"), owner.Id));
        Assert.Equal(428, missing.StatusCode);
        var stale = await Assert.ThrowsAsync<RuleLifecycleException>(() => service.UpdateAsync(workspace.Id, rule.Id, new UpdateRuleDto(Title: "Changed", ExpectedRevision: 1), owner.Id));
        Assert.Equal(409, stale.StatusCode);
        Assert.Equal("approved", rule.Status);

        var changed = await service.UpdateAsync(workspace.Id, rule.Id, new UpdateRuleDto(Title: "Changed", ExpectedRevision: 2), owner.Id);
        Assert.Equal(3, changed!.Revision);
        Assert.Equal("draft", changed.Status);
        Assert.Null(changed.ReviewedBy); Assert.Null(changed.ReviewedAt); Assert.Null(changed.ReviewedCatalogRevision);
        Assert.True(changed.Enabled);
        Assert.Empty((await service.ApprovedSnapshotAsync(workspace.Id)).Rules);
        var audit = await db.RuleAuditEntries.SingleAsync(a => a.Action == "updated");
        Assert.Equal(owner.Id, audit.ActorId);
        Assert.Equal("approved", JsonDocument.Parse(audit.BeforeJson!).RootElement.GetProperty("status").GetString());
        Assert.Equal("draft", JsonDocument.Parse(audit.AfterJson!).RootElement.GetProperty("status").GetString());
        Assert.Contains("provenance", changed.Sources!);
    }

    [Fact]
    public async Task Submit_approve_toggle_retire_and_reopen_are_revision_bound_and_definition_review_is_preserved_only_for_toggles()
    {
        await using var db = new AppDbContext(Options());
        var (owner, workspace) = await SeedAsync(db);
        var service = new RuleService(db);
        var rule = await service.CreateAsync(workspace.Id, RuleTestDefinitions.Create(), owner.Id);
        await service.TransitionAsync(workspace.Id, rule.Id, "submit", 1, owner.Id);
        Assert.Equal("in_review", rule.Status); Assert.Equal(2, rule.Revision);
        await Assert.ThrowsAsync<RuleLifecycleException>(() => service.TransitionAsync(workspace.Id, rule.Id, "submit", 2, owner.Id));
        await service.TransitionAsync(workspace.Id, rule.Id, "approve", 2, owner.Id);
        Assert.Equal("approved", rule.Status); Assert.Equal(3, rule.Revision);
        Assert.Equal(0, rule.ReviewedCatalogRevision);
        var reviewedAt = rule.ReviewedAt; var updatedAt = rule.UpdatedAt;
        Assert.Single((await service.ApprovedSnapshotAsync(workspace.Id)).Rules);
        await service.SetEnabledAsync(workspace.Id, rule.Id, new RuleEnablementDto(false, 3), owner.Id);
        Assert.Equal(reviewedAt, rule.ReviewedAt); Assert.Equal(updatedAt, rule.UpdatedAt);
        Assert.Empty((await service.ApprovedSnapshotAsync(workspace.Id)).Rules);
        await service.SetEnabledAsync(workspace.Id, rule.Id, new RuleEnablementDto(true, 4), owner.Id);
        Assert.Single((await service.ApprovedSnapshotAsync(workspace.Id)).Rules);
        await service.TransitionAsync(workspace.Id, rule.Id, "retire", 5, owner.Id);
        Assert.Equal("retired", rule.Status); Assert.False(rule.Enabled); Assert.Equal(reviewedAt, rule.ReviewedAt);
        await Assert.ThrowsAsync<RuleLifecycleException>(() => service.SetEnabledAsync(workspace.Id, rule.Id, new RuleEnablementDto(true, 6), owner.Id));
        await Assert.ThrowsAsync<RuleLifecycleException>(() => service.UpdateAsync(workspace.Id, rule.Id, new UpdateRuleDto(Title: "Changed", ExpectedRevision: 6), owner.Id));
        await service.TransitionAsync(workspace.Id, rule.Id, "reopen", 6, owner.Id);
        Assert.Equal("draft", rule.Status); Assert.False(rule.Enabled); Assert.Equal(7, rule.Revision);
        Assert.Null(rule.ReviewedBy); Assert.Null(rule.ReviewedAt);
        await service.TransitionAsync(workspace.Id, rule.Id, "approve", 7, owner.Id);
        Assert.Empty((await service.ApprovedSnapshotAsync(workspace.Id)).Rules); // Approval never silently enables a disabled candidate.
        await service.SetEnabledAsync(workspace.Id, rule.Id, new RuleEnablementDto(true, 8), owner.Id);
        Assert.Single((await service.ApprovedSnapshotAsync(workspace.Id)).Rules);
        Assert.Equal(new[] { "created", "submit", "approve", "disabled", "enabled", "retire", "reopen", "approve", "enabled" },
            (await db.RuleAuditEntries.OrderBy(a => a.CreatedAt).ToListAsync()).Select(a => a.Action));
    }

    [Fact]
    public async Task Catalog_edits_invalidate_nonretired_medication_reviews_but_not_case_rules_or_other_workspaces()
    {
        await using var db = new AppDbContext(Options());
        var (owner, workspace) = await SeedAsync(db);
        var (_, other) = await SeedAsync(db);
        var service = new RuleService(db);
        var med = await service.CreateAsync(workspace.Id, RuleTestDefinitions.Create(), owner.Id);
        var caseRule = await service.CreateAsync(workspace.Id, RuleTestDefinitions.Create(caseLevel: true), owner.Id);
        var retired = await service.CreateAsync(workspace.Id, RuleTestDefinitions.Create(), owner.Id);
        var otherRule = await service.CreateAsync(other.Id, RuleTestDefinitions.Create(), owner.Id);
        foreach (var (w, r) in new[] { (workspace.Id, med), (workspace.Id, caseRule), (workspace.Id, retired), (other.Id, otherRule) })
            await service.TransitionAsync(w, r.Id, "approve", 1, owner.Id);
        await service.TransitionAsync(workspace.Id, retired.Id, "retire", 2, owner.Id);
        var retiredRevision = retired.Revision;
        var catalog = await service.UpdateCatalogAsync(workspace.Id, new UpdateDrugCatalogDto(RuleTestDefinitions.Catalog, 0), owner.Id);
        Assert.Equal(1, catalog.Revision); Assert.False(catalog.IsDefault);
        Assert.Equal("draft", med.Status); Assert.Null(med.ReviewedCatalogRevision); Assert.Equal(3, med.Revision);
        Assert.Equal("approved", caseRule.Status); Assert.Equal(2, caseRule.Revision);
        Assert.Equal("retired", retired.Status); Assert.Equal(retiredRevision, retired.Revision);
        Assert.Equal("approved", otherRule.Status); Assert.Equal(0, (await service.CatalogAsync(other.Id)).Revision);
        Assert.Equal(new[] { caseRule.RuleId }, (await service.ApprovedSnapshotAsync(workspace.Id)).Rules.Select(r => r.Id));
        Assert.Single((await service.ApprovedSnapshotAsync(other.Id)).Rules);
        await service.TransitionAsync(workspace.Id, med.Id, "approve", 3, owner.Id);
        Assert.Equal(1, med.ReviewedCatalogRevision);
        var audits = await db.RuleAuditEntries.CountAsync();
        await service.UpdateCatalogAsync(workspace.Id, new UpdateDrugCatalogDto(catalog.Catalog, 1), owner.Id);
        Assert.Equal(audits, await db.RuleAuditEntries.CountAsync()); // Exact no-op.
        Assert.Equal("approved", med.Status);
    }

    [Fact]
    public async Task Explicit_empty_catalog_is_not_default_and_corrupt_recognition_fails_closed_but_can_be_repaired()
    {
        await using var db = new AppDbContext(Options());
        var (owner, workspace) = await SeedAsync(db);
        var service = new RuleService(db);
        var initial = await service.CatalogAsync(workspace.Id);
        Assert.True(initial.IsDefault); Assert.Equal(0, initial.Revision);
        Assert.True(initial.Catalog.TryGetProperty("metformin", out _));
        var missing = await Assert.ThrowsAsync<RuleLifecycleException>(() => service.UpdateCatalogAsync(workspace.Id, new UpdateDrugCatalogDto(JsonSerializer.SerializeToElement(new { })), owner.Id));
        Assert.Equal(428, missing.StatusCode);
        await service.UpdateCatalogAsync(workspace.Id, new UpdateDrugCatalogDto(JsonSerializer.SerializeToElement(new { }), 0), owner.Id);
        var empty = await service.CatalogAsync(workspace.Id);
        Assert.False(empty.IsDefault); Assert.Empty(empty.Catalog.EnumerateObject()); Assert.Equal(1, empty.Revision);
        var row = await db.DrugCatalogs.SingleAsync(); row.CatalogJson = "not json";
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<RuleValidationException>(() => service.CatalogAsync(workspace.Id));
        await Assert.ThrowsAsync<RuleValidationException>(() => service.ApprovedSnapshotAsync(workspace.Id));
        var evidence = await service.CatalogRepairAsync(workspace.Id);
        Assert.Equal(1, evidence.Revision); Assert.Equal("not json", evidence.StoredCatalogJson); Assert.NotEmpty(evidence.Errors);
        await Assert.ThrowsAsync<RuleLifecycleException>(() => service.UpdateCatalogAsync(workspace.Id, new UpdateDrugCatalogDto(RuleTestDefinitions.Catalog, 0), owner.Id));
        await service.UpdateCatalogAsync(workspace.Id, new UpdateDrugCatalogDto(RuleTestDefinitions.Catalog, 1), owner.Id);
        Assert.Equal(2, (await service.CatalogAsync(workspace.Id)).Revision);
        await Assert.ThrowsAsync<RuleLifecycleException>(() => service.CatalogRepairAsync(workspace.Id));
        Assert.Contains(await db.RuleAuditEntries.ToListAsync(), a => a.Action == "catalog_updated" && a.BeforeJson == "not json");
    }

    [Fact]
    public async Task Default_import_is_explicit_idempotent_sourced_disabled_and_never_overwrites_existing_rules()
    {
        await using var db = new AppDbContext(Options());
        var (owner, workspace) = await SeedAsync(db);
        var service = new RuleService(db);
        Assert.Empty((await service.ApprovedSnapshotAsync(workspace.Id)).Rules);
        Assert.Empty(await db.Rules.ToListAsync()); // Reading analysis policy does not seed disk rules.
        var seed = RuleAssets.DefaultRules.GetProperty("rules").EnumerateArray().First();
        var existing = await service.CreateAsync(workspace.Id, RuleTestDefinitions.Create(seed.GetProperty("id").GetString()!.ToUpperInvariant()), owner.Id);
        var first = await service.ImportDefaultsAsync(workspace.Id, owner.Id);
        Assert.Equal(1, first.Skipped); Assert.Equal(6, first.Imported);
        Assert.Equal("Synthetic medication review", existing.Title);
        foreach (var rule in first.Rules)
        {
            Assert.Equal("draft", rule.Status); Assert.False(rule.Enabled); Assert.Null(rule.ReviewedBy);
            Assert.Equal(owner.Id, rule.CreatedBy);
            foreach (var source in JsonDocument.Parse(rule.Sources!).RootElement.EnumerateArray())
                Assert.False(string.IsNullOrWhiteSpace(source.GetProperty("quote").GetString()));
        }
        var audits = await db.RuleAuditEntries.CountAsync();
        var second = await service.ImportDefaultsAsync(workspace.Id, owner.Id);
        Assert.Equal(0, second.Imported); Assert.Equal(7, second.Skipped);
        Assert.Equal(audits, await db.RuleAuditEntries.CountAsync());
        Assert.Empty((await service.ApprovedSnapshotAsync(workspace.Id)).Rules);
    }

    [Fact]
    public async Task Snapshot_excludes_disabled_stale_unreviewed_catalog_mismatched_and_invalid_legacy_definitions()
    {
        await using var db = new AppDbContext(Options());
        var (owner, workspace) = await SeedAsync(db);
        var service = new RuleService(db);
        Rule Valid(string key)
        {
            var rule = RuleTestDefinitions.Create(key).ToRule();
            rule.WorkspaceId = workspace.Id; rule.Status = "approved"; rule.ReviewedBy = owner.Id;
            rule.ReviewedAt = rule.UpdatedAt = DateTime.UtcNow; rule.ReviewedCatalogRevision = 0;
            db.Rules.Add(rule); return rule;
        }
        Valid("valid");
        Valid("disabled").Enabled = false;
        Valid("stale").UpdatedAt = DateTime.UtcNow.AddMinutes(1);
        Valid("missing-reviewer").ReviewedBy = null;
        Valid("wrong-catalog").ReviewedCatalogRevision = 1;
        Valid("missing-catalog").ReviewedCatalogRevision = null;
        Valid("invalid").Sources = "[]";
        Valid("draft").Status = "draft";
        Valid("in-review").Status = "in_review";
        Valid("retired").Status = "retired";
        await db.SaveChangesAsync();
        Assert.Equal(new[] { "valid" }, (await service.ApprovedSnapshotAsync(workspace.Id)).Rules.Select(r => r.Id));
        var legacy = await db.Rules.SingleAsync(r => r.RuleId == "invalid");
        Assert.Equal("approved", (await service.UpdateAsync(workspace.Id, legacy.Id, new UpdateRuleDto(), owner.Id))!.Status);
        legacy.Status = "draft";
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<RuleValidationException>(() => service.TransitionAsync(workspace.Id, legacy.Id, "approve", 1, owner.Id));
    }

    [Fact]
    public async Task Deletion_preserves_full_audit_snapshots_and_audit_rows_cannot_be_updated_or_deleted()
    {
        await using var db = new AppDbContext(Options());
        var (owner, workspace) = await SeedAsync(db);
        var service = new RuleService(db);
        var rule = await service.CreateAsync(workspace.Id, RuleTestDefinitions.Create(), owner.Id);
        await service.TransitionAsync(workspace.Id, rule.Id, "approve", 1, owner.Id);
        await Assert.ThrowsAsync<RuleLifecycleException>(() => service.DeleteAsync(workspace.Id, rule.Id, null, owner.Id));
        Assert.True(await service.DeleteAsync(workspace.Id, rule.Id, 2, owner.Id));
        Assert.Empty(await db.Rules.ToListAsync());
        Assert.Equal(3, await db.RuleAuditEntries.CountAsync());
        var deleted = await db.RuleAuditEntries.SingleAsync(a => a.Action == "deleted");
        Assert.Equal(rule.Id, deleted.RuleId); Assert.Null(deleted.AfterJson);
        Assert.Equal("approved", JsonDocument.Parse(deleted.BeforeJson!).RootElement.GetProperty("status").GetString());
        deleted.Action = "tampered";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.Entry(deleted).State = EntityState.Unchanged;
        db.RuleAuditEntries.Remove(deleted);
        Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
    }

    private sealed class PauseCatalogSave : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<WorkspaceDrugCatalog>().Any(e => e.State is EntityState.Added or EntityState.Modified))
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    [Fact]
    public async Task Snapshot_waits_for_catalog_and_review_reset_to_commit_as_one_workspace_snapshot()
    {
        var name = $"Snapshot_{Guid.NewGuid():N}";
        await using var seed = new AppDbContext(Options(name));
        var (owner, workspace) = await SeedAsync(seed);
        var rule = await new RuleService(seed).CreateAsync(workspace.Id, RuleTestDefinitions.Create(), owner.Id);
        await new RuleService(seed).TransitionAsync(workspace.Id, rule.Id, "approve", 1, owner.Id);
        var before = await new RuleService(seed).ApprovedSnapshotAsync(workspace.Id);
        var pause = new PauseCatalogSave();
        await using var writer = new AppDbContext(Options(name, pause));
        await using var reader = new AppDbContext(Options(name));
        var change = new RuleService(writer).UpdateCatalogAsync(workspace.Id, new UpdateDrugCatalogDto(RuleTestDefinitions.Catalog, 0), owner.Id);
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var snapshot = new RuleService(reader).ApprovedSnapshotAsync(workspace.Id);
            Assert.False(snapshot.IsCompleted);
            pause.Release.TrySetResult();
            await change;
            var after = await snapshot.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, before.CatalogRevision); Assert.Single(before.Rules);
            Assert.Equal(1, after.CatalogRevision); Assert.Empty(after.Rules);
            Assert.True(after.DrugCatalog.TryGetProperty("syntheticdrug", out _));
        }
        finally { pause.Release.TrySetResult(); }
    }

    [Fact]
    public async Task Catalog_change_and_approval_cannot_publish_an_old_recognition_review()
    {
        var name = $"ReviewRace_{Guid.NewGuid():N}";
        await using var seed = new AppDbContext(Options(name));
        var (owner, workspace) = await SeedAsync(seed);
        var rule = await new RuleService(seed).CreateAsync(workspace.Id, RuleTestDefinitions.Create(), owner.Id);
        var pause = new PauseCatalogSave();
        await using var writer = new AppDbContext(Options(name, pause));
        await using var reviewer = new AppDbContext(Options(name));
        var change = new RuleService(writer).UpdateCatalogAsync(workspace.Id, new UpdateDrugCatalogDto(RuleTestDefinitions.Catalog, 0), owner.Id);
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var approval = new RuleService(reviewer).TransitionAsync(workspace.Id, rule.Id, "approve", 1, owner.Id);
            Assert.False(approval.IsCompleted);
            pause.Release.TrySetResult();
            await change;
            var conflict = await Assert.ThrowsAsync<RuleLifecycleException>(() => approval);
            Assert.Equal(409, conflict.StatusCode);
            Assert.Empty((await new RuleService(reviewer).ApprovedSnapshotAsync(workspace.Id)).Rules);
        }
        finally { pause.Release.TrySetResult(); }
    }

    [Fact]
    public async Task Cancelled_mutation_releases_the_workspace_gate_without_partial_definition_or_catalog_writes()
    {
        var name = $"Cancel_{Guid.NewGuid():N}";
        await using var seed = new AppDbContext(Options(name));
        var (owner, workspace) = await SeedAsync(seed);
        var rule = await new RuleService(seed).CreateAsync(workspace.Id, RuleTestDefinitions.Create(), owner.Id);
        await new RuleService(seed).TransitionAsync(workspace.Id, rule.Id, "approve", 1, owner.Id);
        var pause = new PauseCatalogSave();
        await using var writer = new AppDbContext(Options(name, pause));
        using var cancellation = new CancellationTokenSource();
        var change = new RuleService(writer).UpdateCatalogAsync(workspace.Id, new UpdateDrugCatalogDto(RuleTestDefinitions.Catalog, 0), owner.Id, cancellation.Token);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => change);
        await using var reader = new AppDbContext(Options(name));
        var snapshot = await new RuleService(reader).ApprovedSnapshotAsync(workspace.Id).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, snapshot.CatalogRevision); Assert.Single(snapshot.Rules);
        Assert.Equal(2, await reader.RuleAuditEntries.CountAsync());
    }

    [Fact]
    public async Task Repair_metadata_waits_for_a_catalog_replacement_and_never_returns_mixed_or_obsolete_evidence()
    {
        var name = $"Repair_{Guid.NewGuid():N}";
        await using var seed = new AppDbContext(Options(name));
        var (owner, workspace) = await SeedAsync(seed);
        seed.DrugCatalogs.Add(new WorkspaceDrugCatalog { WorkspaceId = workspace.Id, Revision = 4, CatalogJson = "corrupt evidence" });
        await seed.SaveChangesAsync();
        var pause = new PauseCatalogSave();
        await using var writer = new AppDbContext(Options(name, pause));
        await using var reader = new AppDbContext(Options(name));
        var change = new RuleService(writer).UpdateCatalogAsync(workspace.Id, new UpdateDrugCatalogDto(RuleTestDefinitions.Catalog, 4), owner.Id);
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var evidence = new RuleService(reader).CatalogRepairAsync(workspace.Id);
            Assert.False(evidence.IsCompleted);
            pause.Release.TrySetResult();
            await change;
            await Assert.ThrowsAsync<RuleLifecycleException>(() => evidence);
            Assert.Equal(5, (await new RuleService(reader).CatalogAsync(workspace.Id)).Revision);
        }
        finally { pause.Release.TrySetResult(); }
    }

    [Theory]
    [InlineData("{\"x\":{\"classes\":[\"unknown\"],\"brands\":[],\"regional_names\":[]}}")]
    [InlineData("{\"x\":{\"classes\":[\"nsaid\"],\"brands\":[\"same\"],\"regional_names\":[]},\"y\":{\"classes\":[\"metformin\"],\"brands\":[\"ＳＡＭＥ\"],\"regional_names\":[]}}")]
    [InlineData("{\"x\":{\"classes\":[\"nsaid\"],\"brands\":[],\"regional_names\":[],\"extra\":true}}")]
    [InlineData("{\"x\":{\"classes\":[\"nsaid\"],\"brands\":[\"bad\\u0000name\"],\"regional_names\":[]}}")]
    public async Task Invalid_catalog_changes_never_reset_reviews_or_create_audits(string json)
    {
        await using var db = new AppDbContext(Options());
        var (owner, workspace) = await SeedAsync(db);
        var service = new RuleService(db);
        var rule = await service.CreateAsync(workspace.Id, RuleTestDefinitions.Create(), owner.Id);
        await service.TransitionAsync(workspace.Id, rule.Id, "approve", 1, owner.Id);
        await Assert.ThrowsAsync<RuleValidationException>(() => service.UpdateCatalogAsync(workspace.Id, new UpdateDrugCatalogDto(JsonDocument.Parse(json).RootElement, 0), owner.Id));
        Assert.Equal("approved", rule.Status); Assert.Equal(2, rule.Revision);
        Assert.Empty(await db.DrugCatalogs.ToListAsync()); Assert.Equal(2, await db.RuleAuditEntries.CountAsync());
    }
}
