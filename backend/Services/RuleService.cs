using System.Collections.Concurrent;
using IsolationLevel = System.Data.IsolationLevel;
using System.Text.Json;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ClinicalRag.Api.Services;

public sealed class RuleLifecycleException : InvalidOperationException
{
    public int StatusCode { get; }
    public RuleLifecycleException(string message, int statusCode = 409) : base(message) => StatusCode = statusCode;
}

public sealed record DrugCatalogSnapshot(JsonElement Catalog, int Revision, bool IsDefault, Guid? UpdatedBy, DateTime? UpdatedAt);
public sealed record DrugCatalogRepairSnapshot(int Revision, string StoredCatalogJson, Guid? UpdatedBy, DateTime? UpdatedAt, IReadOnlyList<string> Errors);
public sealed record RuleRequestSnapshot(IReadOnlyList<PipelineRule> Rules, JsonElement DrugCatalog, int CatalogRevision);
public sealed record RuleImportResult(int Imported, int Skipped, IReadOnlyList<Rule> Rules);

/// <summary>
/// Owns definitions, approval and recognition snapshots. All catalog/rule mutations
/// and snapshot reads share a workspace lock; external pipeline calls run after it is released.
/// </summary>
public sealed class RuleService
{
    private readonly AppDbContext _db;
    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web);
    public RuleService(AppDbContext db) => _db = db;

    public async Task<Rule> CreateAsync(Guid workspaceId, CreateRuleDto dto, Guid actor, CancellationToken token = default)
    {
        var rule = dto.ToRule();
        RuleValidator.Validate(rule);
        await using var scope = await WorkspaceScope.OpenAsync(_db, workspaceId, token);
        await EnsureUniqueAsync(workspaceId, rule, token);
        rule.WorkspaceId = workspaceId;
        rule.CreatedBy = rule.UpdatedBy = actor;
        rule.CreatedAt = rule.UpdatedAt = DateTime.UtcNow;
        _db.Rules.Add(rule);
        Audit(rule, actor, "created", null);
        await _db.SaveChangesAsync(token);
        await scope.CommitAsync(token);
        return rule;
    }

    public async Task<Rule?> UpdateAsync(Guid workspaceId, Guid id, UpdateRuleDto dto, Guid actor, CancellationToken token = default)
    {
        await using var scope = await WorkspaceScope.OpenAsync(_db, workspaceId, token);
        var rule = await _db.Rules.FirstOrDefaultAsync(r => r.WorkspaceId == workspaceId && r.Id == id, token);
        if (rule == null) return null;
        var candidate = new Rule
        {
            Id = rule.Id, WorkspaceId = workspaceId, RuleId = dto.RuleId ?? rule.RuleId, Title = dto.Title ?? rule.Title,
            DrugClasses = dto.DrugClasses ?? rule.DrugClasses, AppliesWhen = dto.AppliesWhen ?? rule.AppliesWhen,
            Level = dto.Level ?? rule.Level, Message = dto.Message ?? rule.Message, Caveat = dto.Caveat ?? rule.Caveat,
            Sources = dto.Sources ?? rule.Sources, LinkTerms = dto.LinkTerms ?? rule.LinkTerms, CaseLevel = dto.CaseLevel ?? rule.CaseLevel
        };
        if (SameDefinition(rule, candidate))
        {
            if (dto.ExpectedRevision != null) RequireRevision(dto.ExpectedRevision, rule.Revision);
            await scope.CommitAsync(token);
            return rule;
        }
        RuleValidator.Validate(candidate);
        RequireRevision(dto.ExpectedRevision, rule.Revision);
        if (rule.Status == "retired") throw new RuleLifecycleException("Reopen a retired rule as a draft before editing it.");
        await EnsureUniqueAsync(workspaceId, candidate, token);
        var before = Snapshot(rule);
        rule.RuleId = candidate.RuleId; rule.Title = candidate.Title; rule.DrugClasses = candidate.DrugClasses;
        rule.AppliesWhen = candidate.AppliesWhen; rule.Level = candidate.Level; rule.Message = candidate.Message;
        rule.Caveat = candidate.Caveat; rule.Sources = candidate.Sources; rule.LinkTerms = candidate.LinkTerms; rule.CaseLevel = candidate.CaseLevel;
        Invalidate(rule, actor);
        Audit(rule, actor, "updated", before);
        await _db.SaveChangesAsync(token);
        await scope.CommitAsync(token);
        return rule;
    }

    public async Task<Rule?> TransitionAsync(Guid workspaceId, Guid id, string action, int? expectedRevision, Guid actor, CancellationToken token = default)
    {
        await using var scope = await WorkspaceScope.OpenAsync(_db, workspaceId, token);
        var rule = await _db.Rules.FirstOrDefaultAsync(r => r.WorkspaceId == workspaceId && r.Id == id, token);
        if (rule == null) return null;
        RequireRevision(expectedRevision, rule.Revision);
        var before = Snapshot(rule);
        switch (action)
        {
            case "submit":
                if (rule.Status != "draft") throw new RuleLifecycleException("Only saved drafts can be submitted for review.");
                RuleValidator.Validate(rule);
                rule.Status = "in_review";
                break;
            case "approve":
                if (rule.Status is not ("draft" or "in_review")) throw new RuleLifecycleException("Only saved drafts or rules in review can be approved.");
                RuleValidator.Validate(rule);
                await EnsureUniqueAsync(workspaceId, rule, token);
                rule.Status = "approved";
                rule.ReviewedBy = actor;
                rule.ReviewedAt = DateTime.UtcNow;
                rule.ReviewedCatalogRevision = (await CatalogCoreAsync(workspaceId, token)).Revision;
                break;
            case "retire":
                if (rule.Status == "retired") throw new RuleLifecycleException("This rule is already retired.");
                rule.Status = "retired";
                rule.Enabled = false;
                break;
            case "reopen":
                if (rule.Status != "retired") throw new RuleLifecycleException("Only retired rules can be reopened.");
                Invalidate(rule, actor);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(action));
        }
        if (action != "reopen") { rule.Revision++; rule.UpdatedBy = actor; }
        Audit(rule, actor, action, before);
        await _db.SaveChangesAsync(token);
        await scope.CommitAsync(token);
        return rule;
    }

    public async Task<Rule?> SetEnabledAsync(Guid workspaceId, Guid id, RuleEnablementDto dto, Guid actor, CancellationToken token = default)
    {
        await using var scope = await WorkspaceScope.OpenAsync(_db, workspaceId, token);
        var rule = await _db.Rules.FirstOrDefaultAsync(r => r.WorkspaceId == workspaceId && r.Id == id, token);
        if (rule == null) return null;
        RequireRevision(dto.ExpectedRevision, rule.Revision);
        if (dto.Enabled && rule.Status == "retired") throw new RuleLifecycleException("Retired rules cannot be enabled. Reopen and review them first.");
        if (dto.Enabled && rule.Status == "approved")
        {
            RuleValidator.Validate(rule);
            var catalog = await CatalogCoreAsync(workspaceId, token);
            if (!CurrentApproval(rule, catalog.Revision)) throw new RuleLifecycleException("Approval is no longer current. Edit and re-review this rule.");
        }
        if (rule.Enabled != dto.Enabled)
        {
            var before = Snapshot(rule);
            rule.Enabled = dto.Enabled; rule.Revision++; rule.UpdatedBy = actor;
            // Enablement changes the active switch, not the reviewed definition timestamp.
            Audit(rule, actor, dto.Enabled ? "enabled" : "disabled", before);
            await _db.SaveChangesAsync(token);
        }
        await scope.CommitAsync(token);
        return rule;
    }

    public async Task<bool> DeleteAsync(Guid workspaceId, Guid id, int? expectedRevision, Guid actor, CancellationToken token = default)
    {
        await using var scope = await WorkspaceScope.OpenAsync(_db, workspaceId, token);
        var rule = await _db.Rules.FirstOrDefaultAsync(r => r.WorkspaceId == workspaceId && r.Id == id, token);
        if (rule == null) return false;
        RequireRevision(expectedRevision, rule.Revision);
        Audit(rule, actor, "deleted", Snapshot(rule), deleted: true);
        _db.Rules.Remove(rule);
        await _db.SaveChangesAsync(token);
        await scope.CommitAsync(token);
        return true;
    }

    public async Task<RuleImportResult> ImportDefaultsAsync(Guid workspaceId, Guid actor, CancellationToken token = default)
    {
        var defaults = RuleAssets.DefaultRules.GetProperty("rules").EnumerateArray().Select(RuleValidator.FromSeed).ToList();
        foreach (var rule in defaults)
        {
            RuleValidator.Validate(rule);
            if (JsonSerializer.Deserialize<JsonElement>(rule.Sources!).EnumerateArray()
                .Any(source => !source.TryGetProperty("quote", out var quote) || quote.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(quote.GetString())))
                throw new RuleValidationException(["Every default candidate must include its supporting source quote."]);
        }
        await using var scope = await WorkspaceScope.OpenAsync(_db, workspaceId, token);
        var existing = (await _db.Rules.Where(r => r.WorkspaceId == workspaceId).Select(r => r.RuleId).ToListAsync(token)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = new List<Rule>();
        foreach (var rule in defaults)
        {
            if (!existing.Add(rule.RuleId)) continue; // Never overwrite workspace rules, including retired or edited defaults.
            rule.WorkspaceId = workspaceId; rule.CreatedBy = rule.UpdatedBy = actor;
            rule.CreatedAt = rule.UpdatedAt = DateTime.UtcNow;
            _db.Rules.Add(rule); added.Add(rule); Audit(rule, actor, "imported", null);
        }
        await _db.SaveChangesAsync(token);
        await scope.CommitAsync(token);
        return new RuleImportResult(added.Count, defaults.Count - added.Count, added);
    }

    public async Task<DrugCatalogSnapshot> CatalogAsync(Guid workspaceId, CancellationToken token = default)
    {
        await using var scope = await WorkspaceScope.OpenAsync(_db, workspaceId, token);
        var result = await CatalogCoreAsync(workspaceId, token);
        await scope.CommitAsync(token);
        return result;
    }

    /// <summary>Editor-authorized recovery metadata is one revision-bound read, never a fallback catalog.</summary>
    public async Task<DrugCatalogRepairSnapshot> CatalogRepairAsync(Guid workspaceId, CancellationToken token = default)
    {
        await using var scope = await WorkspaceScope.OpenAsync(_db, workspaceId, token);
        var row = await _db.DrugCatalogs.AsNoTracking().FirstOrDefaultAsync(c => c.WorkspaceId == workspaceId, token);
        if (row == null) throw new RuleLifecycleException("Stored recognition is no longer invalid. Refresh the saved catalog.");
        IReadOnlyList<string> errors;
        try
        {
            ValidatedCatalog(row);
            throw new RuleLifecycleException("Stored recognition is no longer invalid. Refresh the saved catalog.");
        }
        catch (RuleValidationException error) { errors = error.Errors; }
        var result = new DrugCatalogRepairSnapshot(row.Revision, row.CatalogJson, row.UpdatedBy, row.UpdatedAt, errors);
        await scope.CommitAsync(token);
        return result;
    }

    public async Task<DrugCatalogSnapshot> UpdateCatalogAsync(Guid workspaceId, UpdateDrugCatalogDto dto, Guid actor, CancellationToken token = default)
    {
        var json = RuleValidator.ValidateCatalog(dto.Catalog);
        await using var scope = await WorkspaceScope.OpenAsync(_db, workspaceId, token);
        // A valid replacement can also repair a corrupt persisted catalog. Never substitute
        // defaults for the old value or let a corrupt definition bypass revision binding.
        var previous = await _db.DrugCatalogs.AsNoTracking().FirstOrDefaultAsync(c => c.WorkspaceId == workspaceId, token);
        var previousJson = previous?.CatalogJson ?? RuleAssets.DefaultDrugs.GetRawText();
        RequireRevision(dto.ExpectedRevision, previous?.Revision ?? 0);
        // Exact no-ops do not invalidate approval. Equivalent reformats are conservative changes.
        if (json == previousJson)
        {
            await scope.CommitAsync(token);
            return new DrugCatalogSnapshot(dto.Catalog.Clone(), previous?.Revision ?? 0, previous == null, previous?.UpdatedBy, previous?.UpdatedAt);
        }
        var row = await _db.DrugCatalogs.FirstOrDefaultAsync(c => c.WorkspaceId == workspaceId, token);
        if (row == null) { row = new WorkspaceDrugCatalog { WorkspaceId = workspaceId, Revision = 0 }; _db.DrugCatalogs.Add(row); }
        row.CatalogJson = json; row.Revision++; row.UpdatedBy = actor; row.UpdatedAt = DateTime.UtcNow;
        _db.RuleAuditEntries.Add(new RuleAuditEntry { WorkspaceId = workspaceId, ActorId = actor, Action = "catalog_updated",
            BeforeJson = previousJson, AfterJson = json });
        var affected = await _db.Rules.Where(r => r.WorkspaceId == workspaceId && !r.CaseLevel && r.Status != "retired").ToListAsync(token);
        foreach (var rule in affected)
        {
            var before = Snapshot(rule);
            Invalidate(rule, actor); Audit(rule, actor, "catalog_review_reset", before);
        }
        await _db.SaveChangesAsync(token);
        await scope.CommitAsync(token);
        return new DrugCatalogSnapshot(dto.Catalog.Clone(), row.Revision, false, row.UpdatedBy, row.UpdatedAt);
    }

    public async Task<RuleRequestSnapshot> ApprovedSnapshotAsync(Guid workspaceId, CancellationToken token = default)
    {
        await using var scope = await WorkspaceScope.OpenAsync(_db, workspaceId, token);
        var catalog = await CatalogCoreAsync(workspaceId, token);
        var definitions = await _db.Rules.AsNoTracking().Where(r => r.WorkspaceId == workspaceId
            && r.Status == "approved" && r.Enabled && r.ReviewedAt != null && r.ReviewedBy != null && r.ReviewedAt >= r.UpdatedAt
            && (r.CaseLevel || r.ReviewedCatalogRevision == catalog.Revision)).OrderBy(r => r.RuleId).ToListAsync(token);
        var rules = new List<PipelineRule>();
        foreach (var rule in definitions)
        {
            try { RuleValidator.Validate(rule); rules.Add(RuleValidator.ToPipeline(rule)); }
            catch (RuleValidationException) { /* Invalid legacy rows are never active policy. */ }
        }
        await scope.CommitAsync(token);
        return new RuleRequestSnapshot(rules, catalog.Catalog, catalog.Revision);
    }

    private async Task<DrugCatalogSnapshot> CatalogCoreAsync(Guid workspaceId, CancellationToken token)
    {
        var row = await _db.DrugCatalogs.AsNoTracking().FirstOrDefaultAsync(c => c.WorkspaceId == workspaceId, token);
        var catalog = ValidatedCatalog(row);
        return new DrugCatalogSnapshot(catalog, row?.Revision ?? 0, row == null, row?.UpdatedBy, row?.UpdatedAt);
    }

    private static JsonElement ValidatedCatalog(WorkspaceDrugCatalog? row)
    {
        JsonElement catalog;
        try { catalog = row == null ? RuleAssets.DefaultDrugs.Clone() : JsonSerializer.Deserialize<JsonElement>(row.CatalogJson); }
        catch (JsonException) { throw new RuleValidationException(["Stored drug catalog is not valid JSON."]); }
        RuleValidator.ValidateCatalog(catalog); // Fail closed on invalid persisted recognition, never fall back to a different catalog.
        return catalog;
    }

    private async Task EnsureUniqueAsync(Guid workspaceId, Rule rule, CancellationToken token)
    {
        var key = rule.RuleId.ToUpperInvariant();
        if (await _db.Rules.AnyAsync(r => r.WorkspaceId == workspaceId && r.Id != rule.Id && r.RuleId.ToUpper() == key, token))
            throw new RuleLifecycleException("This rule ID already exists in the workspace. Choose a different ID.");
    }

    private static bool CurrentApproval(Rule rule, int catalogRevision) => rule.Status == "approved" && rule.ReviewedBy != null
        && rule.ReviewedAt != null && rule.ReviewedAt >= rule.UpdatedAt && (rule.CaseLevel || rule.ReviewedCatalogRevision == catalogRevision);
    private static void RequireRevision(int? expected, int actual)
    {
        if (expected == null) throw new RuleLifecycleException("Reload the saved artifact and include expectedRevision before changing it.", 428);
        if (expected != actual) throw new RuleLifecycleException("The saved artifact changed. Your edits are kept; reload before trying again.");
    }
    private static void Invalidate(Rule rule, Guid actor)
    {
        rule.Status = "draft"; rule.ReviewedAt = null; rule.ReviewedBy = null; rule.ReviewedCatalogRevision = null;
        rule.Revision++; rule.UpdatedBy = actor; rule.UpdatedAt = DateTime.UtcNow;
    }
    private static bool SameDefinition(Rule a, Rule b) => a.RuleId == b.RuleId && a.Title == b.Title && a.DrugClasses == b.DrugClasses
        && a.AppliesWhen == b.AppliesWhen && a.Level == b.Level && a.Message == b.Message && a.Caveat == b.Caveat
        && a.Sources == b.Sources && a.LinkTerms == b.LinkTerms && a.CaseLevel == b.CaseLevel;
    private static string Snapshot(Rule rule) => JsonSerializer.Serialize(rule, AuditJson);
    private void Audit(Rule rule, Guid actor, string action, string? before, bool deleted = false) => _db.RuleAuditEntries.Add(new RuleAuditEntry
    { WorkspaceId = rule.WorkspaceId, RuleId = rule.Id, RuleKey = rule.RuleId, ActorId = actor, Action = action, BeforeJson = before, AfterJson = deleted ? null : Snapshot(rule) });

    private sealed class WorkspaceScope : IAsyncDisposable
    {
        private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Locks = new();
        private readonly SemaphoreSlim _gate;
        private readonly IDbContextTransaction? _transaction;
        private WorkspaceScope(SemaphoreSlim gate, IDbContextTransaction? transaction) { _gate = gate; _transaction = transaction; }
        public static async Task<WorkspaceScope> OpenAsync(AppDbContext db, Guid workspaceId, CancellationToken token)
        {
            var gate = Locks.GetOrAdd(workspaceId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(token);
            IDbContextTransaction? transaction = null;
            try
            {
                if (db.Database.IsRelational())
                {
                    transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
                    if (db.Database.IsSqlServer())
                        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT [Id] FROM [Workspaces] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {workspaceId}", token);
                }
                return new WorkspaceScope(gate, transaction);
            }
            catch { if (transaction != null) await transaction.DisposeAsync(); gate.Release(); throw; }
        }
        public async Task CommitAsync(CancellationToken token) { if (_transaction != null) await _transaction.CommitAsync(token); }
        public async ValueTask DisposeAsync()
        {
            try { if (_transaction != null) await _transaction.DisposeAsync(); }
            finally { _gate.Release(); }
        }
    }
}
