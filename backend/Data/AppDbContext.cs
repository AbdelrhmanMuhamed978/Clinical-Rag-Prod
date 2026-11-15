using ClinicalRag.Api.Models;
using ClinicalRag.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace ClinicalRag.Api.Data;

public class AppDbContext : DbContext
{
    internal DbContextOptions<AppDbContext> ContextOptions { get; }
    private readonly bool _ownsVectorCache;
    public ChunkVectorCache VectorCache { get; }
    private readonly HashSet<Guid> _pendingCacheInvalidations = [];
    private Guid? _pendingInvalidationTransaction;
    public AppDbContext(DbContextOptions<AppDbContext> options, ChunkVectorCache? vectorCache = null) : base(options)
    { ContextOptions = options; VectorCache = vectorCache ?? new ChunkVectorCache(); _ownsVectorCache = vectorCache == null; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.AddInterceptors(ChunkCacheTransactionInterceptor.Instance);
        base.OnConfiguring(optionsBuilder);
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<WorkspaceMember> WorkspaceMembers => Set<WorkspaceMember>();
    public DbSet<WorkspaceInvitation> WorkspaceInvitations => Set<WorkspaceInvitation>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<Chunk> Chunks => Set<Chunk>();
    public DbSet<ConfigVersion> ConfigVersions => Set<ConfigVersion>();
    public DbSet<Rule> Rules => Set<Rule>();
    public DbSet<WorkspaceDrugCatalog> DrugCatalogs => Set<WorkspaceDrugCatalog>();
    public DbSet<RuleAuditEntry> RuleAuditEntries => Set<RuleAuditEntry>();
    public DbSet<TestCase> TestCases => Set<TestCase>();
    public DbSet<ChunkerGuidance> ChunkerGuidances => Set<ChunkerGuidance>();
    public DbSet<PendingUploadDeletion> PendingUploadDeletions => Set<PendingUploadDeletion>();
    public DbSet<AnalysisRun> AnalysisRuns => Set<AnalysisRun>();
    public DbSet<TestRun> TestRuns => Set<TestRun>();
    public DbSet<ColabConnectionSetting> ColabConnections => Set<ColabConnectionSetting>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<User>().HasKey(u => u.Id);
        mb.Entity<User>().HasIndex(u => u.FirebaseUid).IsUnique();
        mb.Entity<Workspace>().HasKey(w => w.Id);
        mb.Entity<Workspace>().Property(w => w.MembershipRevision).IsConcurrencyToken().HasDefaultValue(1);
        mb.Entity<Workspace>().Property(w => w.DeletedAt).IsConcurrencyToken();
        mb.Entity<WorkspaceInvitation>().HasKey(i => i.Id);
        mb.Entity<WorkspaceInvitation>().HasIndex(i => new { i.WorkspaceId, i.Email }).IsUnique();
        mb.Entity<WorkspaceInvitation>().Property(i => i.Role).IsConcurrencyToken();
        mb.Entity<Workspace>().Property(w => w.EmbeddingModel).IsConcurrencyToken();
        mb.Entity<Workspace>().Property(w => w.EmbeddingDimensions).IsConcurrencyToken();
        mb.Entity<Workspace>().ToTable(t => t.HasCheckConstraint("CK_Workspaces_EmbeddingDimensions",
            "[EmbeddingDimensions] IS NULL OR [EmbeddingDimensions] BETWEEN 1 AND 16384"));
        mb.Entity<WorkspaceMember>().HasKey(m => new { m.WorkspaceId, m.UserId });
        mb.Entity<WorkspaceMember>().Property(m => m.Role).IsConcurrencyToken();
        mb.Entity<Document>().HasKey(d => d.Id);
        mb.Entity<Document>().HasIndex(d => d.WorkspaceId);
        mb.Entity<Document>().Property(d => d.Revision).IsConcurrencyToken().HasDefaultValue(1);
        mb.Entity<Document>().Property(d => d.EmbeddingCompleted).HasDefaultValue(0);
        mb.Entity<Document>().ToTable(t =>
        {
            t.HasCheckConstraint("CK_Documents_EmbeddingProgress", "[EmbeddingCompleted] >= 0 AND ([EmbeddingTotal] IS NULL OR ([EmbeddingTotal] >= 0 AND [EmbeddingCompleted] <= [EmbeddingTotal]))");
            t.HasCheckConstraint("CK_Documents_EmbeddingBatchSize", "[EmbeddingBatchSize] IS NULL OR [EmbeddingBatchSize] BETWEEN 1 AND 32");
        });
        mb.Entity<Chunk>().HasKey(c => c.Id);
        // byte[] maps to varbinary(max) on SQL Server and BLOB in SQLite race tests.
        mb.Entity<Chunk>().HasIndex(c => new { c.WorkspaceId, c.Status });
        mb.Entity<Chunk>().HasIndex(c => new { c.DocumentId, c.Page });
        if (Database.IsSqlServer())
        {
            // Model identities are ordinal in the vector codec. SQL's default
            // case-insensitive collation must not collapse spaces or let a stale
            // binding concurrency token match a different-cased model.
            mb.Entity<Workspace>().Property(w => w.EmbeddingModel).UseCollation("Latin1_General_100_BIN2");
            mb.Entity<Chunk>().Property(c => c.EmbeddingModel).UseCollation("Latin1_General_100_BIN2");
        }
        mb.Entity<ConfigVersion>().HasKey(c => c.Id);
        mb.Entity<ConfigVersion>().HasIndex(c => new { c.WorkspaceId, c.Version }).IsUnique();
        // Guard both lifecycle and the exact tested artifact without adding a new database column.
        mb.Entity<ConfigVersion>().Property(c => c.Status).IsConcurrencyToken();
        mb.Entity<ConfigVersion>().Property(c => c.ConfigJson).IsConcurrencyToken();
        mb.Entity<Rule>().HasKey(r => r.Id);
        mb.Entity<Rule>().HasIndex(r => new { r.WorkspaceId, r.RuleId });
        mb.Entity<Rule>().Property(r => r.Revision).IsConcurrencyToken();
        mb.Entity<Rule>().Property(r => r.Status).HasDefaultValue("draft");
        mb.Entity<Rule>().Property(r => r.Revision).HasDefaultValue(1);
        mb.Entity<Rule>().ToTable(t =>
        {
            t.HasCheckConstraint("CK_Rules_Status", "[Status] IN ('draft', 'in_review', 'approved', 'retired')");
            t.HasCheckConstraint("CK_Rules_Revision", "[Revision] >= 1");
        });
        mb.Entity<WorkspaceDrugCatalog>().HasKey(c => c.WorkspaceId);
        mb.Entity<WorkspaceDrugCatalog>().Property(c => c.Revision).IsConcurrencyToken();
        mb.Entity<WorkspaceDrugCatalog>().ToTable(t => t.HasCheckConstraint("CK_DrugCatalogs_Revision", "[Revision] >= 1"));
        mb.Entity<RuleAuditEntry>().HasKey(a => a.Id);
        mb.Entity<RuleAuditEntry>().HasIndex(a => new { a.WorkspaceId, a.RuleId, a.CreatedAt });
        mb.Entity<TestCase>().HasKey(t => t.Id);
        mb.Entity<ChunkerGuidance>().HasKey(g => g.Id);
        mb.Entity<ChunkerGuidance>().HasIndex(g => new { g.DocumentId, g.CreatedAt });
        mb.Entity<PendingUploadDeletion>().HasKey(p => p.Id);
        mb.Entity<PendingUploadDeletion>().HasIndex(p => p.LastAttemptAt);
        mb.Entity<AnalysisRun>().HasKey(a => a.Id);
        mb.Entity<TestRun>().HasKey(t => t.Id);
        mb.Entity<TestRun>().HasIndex(t => new { t.WorkspaceId, t.CaseId, t.StartedAt });
        mb.Entity<ColabConnectionSetting>().HasKey(c => c.Id);
        mb.Entity<ColabConnectionSetting>().Property(c => c.Id).ValueGeneratedNever();
        mb.Entity<ColabConnectionSetting>().Property(c => c.Revision).IsConcurrencyToken();
        mb.Entity<ColabConnectionSetting>().ToTable(t =>
        {
            t.HasCheckConstraint("CK_ColabConnections_Singleton", "[Id] = 1");
            t.HasCheckConstraint("CK_ColabConnections_Revision", "[Revision] >= 1");
        });

        // Avoid SQL Server multiple-cascade-path errors: restrict deletes, app handles cleanup.
        foreach (var fk in mb.Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys()))
            fk.DeleteBehavior = DeleteBehavior.Restrict;
    }

    private void GuardAuditHistory()
    {
        if (ChangeTracker.Entries<RuleAuditEntry>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Rule audit history is append-only.");
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardAuditHistory();
        var workspaces = ChangedSearchWorkspaces();
        var result = base.SaveChanges(acceptAllChangesOnSuccess);
        SearchSaved(workspaces);
        return result;
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GuardAuditHistory();
        var workspaces = ChangedSearchWorkspaces();
        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        SearchSaved(workspaces);
        return result;
    }

    private Guid[] ChangedSearchWorkspaces()
    {
        ChangeTracker.DetectChanges();
        return ChangeTracker.Entries<Chunk>().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(e => e.Entity.WorkspaceId)
            .Concat(ChangeTracker.Entries<Document>().Where(e => e.State is EntityState.Modified or EntityState.Deleted).Select(e => e.Entity.WorkspaceId))
            .Concat(ChangeTracker.Entries<Workspace>().Where(e => e.State is EntityState.Modified or EntityState.Deleted).Select(e => e.Entity.Id))
            .Distinct().ToArray();
    }

    private void SearchSaved(IEnumerable<Guid> workspaces)
    {
        // Explicit transactions may not be committed yet. Their interceptor
        // invalidates AFTER commit, never while another reader can see old rows.
        var transactionId = Database.CurrentTransaction?.TransactionId;
        if (_pendingInvalidationTransaction != transactionId) _pendingCacheInvalidations.Clear();
        _pendingInvalidationTransaction = transactionId;
        _pendingCacheInvalidations.UnionWith(workspaces);
        if (transactionId == null) CommitSearchInvalidations(null);
    }

    internal void CommitSearchInvalidations(Guid? transactionId)
    {
        if (_pendingInvalidationTransaction != transactionId) return;
        foreach (var workspace in _pendingCacheInvalidations) VectorCache.Invalidate(workspace);
        _pendingCacheInvalidations.Clear();
        _pendingInvalidationTransaction = null;
    }

    internal void RollbackSearchInvalidations(Guid transactionId)
    {
        if (_pendingInvalidationTransaction != transactionId) return;
        _pendingCacheInvalidations.Clear();
        _pendingInvalidationTransaction = null;
    }

    public override void Dispose()
    {
        base.Dispose();
        if (_ownsVectorCache) VectorCache.Dispose();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_ownsVectorCache) VectorCache.Dispose();
    }
}
