using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ClinicalRag.Api.Models;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [Required, MaxLength(128)] public string FirebaseUid { get; set; } = "";
    [Required, MaxLength(256)] public string Email { get; set; } = "";
    [MaxLength(128)] public string? DisplayName { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Workspace
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [Required, MaxLength(128)] public string Name { get; set; } = "";
    public Guid OwnerId { get; set; }
    public User? Owner { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeletedAt { get; set; }
    public Guid? DeletedBy { get; set; }
    [JsonIgnore] public int MembershipRevision { get; set; } = 1;
    [JsonIgnore] public long SearchGeneration { get; set; }
    [JsonIgnore, MaxLength(256)] public string? EmbeddingModel { get; set; }
    [JsonIgnore] public int? EmbeddingDimensions { get; set; }
    public bool AllowSupersededGuidelines { get; set; }
}

public class WorkspaceMember
{
    public Guid WorkspaceId { get; set; }
    public Workspace? Workspace { get; set; }
    public Guid UserId { get; set; }
    public User? User { get; set; }
    [Required, MaxLength(16)] public string Role { get; set; } = "viewer";
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Only pending invitations. Acceptance/cancellation is retained in append-only audit history.</summary>
public class WorkspaceInvitation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Workspace? Workspace { get; set; }
    [Required, MaxLength(256)] public string Email { get; set; } = "";
    [Required, MaxLength(16)] public string Role { get; set; } = "viewer";
    public Guid InvitedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Document
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Workspace? Workspace { get; set; }
    [Required, MaxLength(256)] public string FileName { get; set; } = "";
    [MaxLength(512)] public string? StoragePath { get; set; }
    public string? ParsedText { get; set; }  // extracted text with page markers (nvarchar(max))
    public string? FootnoteMarkers { get; set; }  // JSON page/offset markers removed from visible text
    [Required, MaxLength(32)] public string Status { get; set; } = "uploaded";
    [MaxLength(32)] public string? ProcessingOperation { get; set; } // parsing | chunking | embedding; null when idle
    [MaxLength(32)] public string? FailureStage { get; set; }
    [MaxLength(512)] public string? FailureReason { get; set; } // safe, actionable text; never an upstream exception/body
    public int Revision { get; set; } = 1;
    public int? EmbeddingTotal { get; set; }
    public int EmbeddingCompleted { get; set; }
    public int? EmbeddingBatchSize { get; set; }
    public int? PageCount { get; set; }
    [MaxLength(1024)] public string? NeedsOcrPages { get; set; } // JSON array of image-only/near-empty page numbers
    public int? ChunkedPagesCount { get; set; } = 0;  // how many pages have been chunked
    public string? ChunkingStatus { get; set; } = "not_started"; // not_started | in_progress | partial | completed | failed
    public string? ChunkedPageNumbers { get; set; } // JSON array of chunked page numbers
    public string? Society { get; set; }  // publishing society/organization
    [MaxLength(512)] public string? Title { get; set; }
    [MaxLength(64)] public string? Version { get; set; }
    public int? Year { get; set; }  // source guideline year
    [Required, MaxLength(16)] public string GuidelineStatus { get; set; } = "unknown"; // final | draft | superseded
    public DateTime? EffectiveDate { get; set; }
    public DateTime? ReviewByDate { get; set; }
    [MaxLength(2048)] public string? SourceUrl { get; set; }
    [MaxLength(1024)] public string? PermissionNote { get; set; }
    public Guid? UpdatedBy { get; set; }  // user who last updated the document
    public Guid? UploadedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class Chunk
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Guid DocumentId { get; set; }
    public Document? Document { get; set; }
    public int Page { get; set; }
    public int? PageStart { get; set; }  // start page if chunk spans pages
    public int? PageEnd { get; set; }    // end page if chunk spans pages
    [MaxLength(256)] public string? Section { get; set; }
    [MaxLength(256)] public string? SectionHierarchy { get; set; }  // e.g., "Clinical management > Induction therapy"
    public int? Year { get; set; }       // extracted year from source
    [MaxLength(32)] public string? Strength { get; set; }  // strong/weak/conditional
    [MaxLength(32)] public string? Certainty { get; set; }  // high/moderate/low certainty
    [Required, MaxLength(16)] public string Type { get; set; } = "clinical";  // clinical | front_matter
    public bool Verbatim { get; set; }  // chunk text is verbatim to source page
    public string? ValidationError { get; set; }  // validation failure message, if any
    public bool ReReviewRequired { get; set; }  // source text changed after re-parse; exclude until reviewed
    public string? EmbeddingModel { get; set; }  // model used to generate the embedding
    [Required] public string Text { get; set; } = "";
    [Required, MaxLength(16)] public string Status { get; set; } = "pending";
    public double? Confidence { get; set; }
    public string? Metadata { get; set; }  // JSON
    [JsonIgnore] public byte[]? Embedding { get; set; }  // normalised little-endian float32, varbinary(max)
    [JsonIgnore] public string? LegacyEmbeddingJson { get; set; } // migration archive only; never searched
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid? ReviewedBy { get; set; }
    public DateTime? ReviewedAt { get; set; }
}

public class ConfigVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public int Version { get; set; }
    [Required, MaxLength(16)] public string Status { get; set; } = "draft";
    [Required] public string ConfigJson { get; set; } = "{}";
    public Guid? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? PublishedAt { get; set; }
}

public class Rule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    [Required, MaxLength(64)] public string RuleId { get; set; } = "";
    [Required, MaxLength(256)] public string Title { get; set; } = "";
    public string? DrugClasses { get; set; }
    public string? AppliesWhen { get; set; }
    [MaxLength(64)] public string? Level { get; set; }
    public string? Message { get; set; }
    public string? Caveat { get; set; }
    public string? Sources { get; set; }
    [MaxLength(512)] public string? LinkTerms { get; set; }
    public bool CaseLevel { get; set; }
    [Required, MaxLength(16)] public string Status { get; set; } = "draft";
    public bool Enabled { get; set; } = true;
    public int Revision { get; set; } = 1;
    public int? ReviewedCatalogRevision { get; set; }
    public Guid? ReviewedBy { get; set; }
    [MaxLength(256)] public string? ReviewerName { get; set; }
    [MaxLength(2048)] public string? ApprovedSourceUrl { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class WorkspaceDrugCatalog
{
    public Guid WorkspaceId { get; set; }
    [Required] public string CatalogJson { get; set; } = "{}";
    public int Revision { get; set; } = 1;
    public Guid? UpdatedBy { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Append-only definition/lifecycle history. No preview or case inputs are recorded.</summary>
public class RuleAuditEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    // Intentionally not a foreign key: deletion must not delete the audit history.
    public Guid? RuleId { get; set; }
    [MaxLength(64)] public string? RuleKey { get; set; }
    [Required, MaxLength(32)] public string Action { get; set; } = "";
    public Guid? ActorId { get; set; }
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class TestCase
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    [Required, MaxLength(128)] public string Name { get; set; } = "";
    [Required] public string InputText { get; set; } = "";
    public string? ExpectedJson { get; set; }
    public string? LastRunJson { get; set; }
    public DateTime? LastRunAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public int Revision { get; set; } = 1;
}

public class ChunkerGuidance
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    [Required] public string Guidance { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Durable, text-free chunking job. Page numbers and safe status evidence only;
/// chunk text stays in Chunks and is never copied into the job record.
/// </summary>
public class ChunkJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Guid DocumentId { get; set; }
    public Document? Document { get; set; }
    [Required, MaxLength(16)] public string Status { get; set; } = "queued";
    public string PagesJson { get; set; } = "[]";
    public string CompletedPagesJson { get; set; } = "[]";
    public string FailedPagesJson { get; set; } = "[]";
    public string SkippedPagesJson { get; set; } = "[]";
    [Required, MaxLength(4000)] public string Guidance { get; set; } = "";
    public int BatchSize { get; set; } = 2;
    public int MaxTokens { get; set; } = 400;
    public int BatchTimeoutSeconds { get; set; } = 300;
    public int MaxRetries { get; set; } = 1;
    [Required, MaxLength(16)] public string FailureMode { get; set; } = "continue";
    public int TotalBatches { get; set; }
    public int CompletedBatches { get; set; }
    public int? CurrentBatchStart { get; set; }
    public int? CurrentBatchEnd { get; set; }
    [MaxLength(64)] public string? ErrorCode { get; set; }
    public bool PauseRequested { get; set; }
    public bool CancelRequested { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? RetryOfJobId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? LastHeartbeatAt { get; set; }
    [MaxLength(128)] public string? LeaseOwner { get; set; }
    public DateTime? LeaseUntil { get; set; }
    public int Revision { get; set; } = 1;
}

/// <summary>Durable filesystem cleanup after the database deletion commits. No FK to deleted documents.</summary>
public class PendingUploadDeletion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [JsonIgnore, Required, MaxLength(512)] public string StoragePath { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastAttemptAt { get; set; }
}

public class AnalysisRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? WorkspaceId { get; set; }
    public Guid? UserId { get; set; }
    [Required, MaxLength(64)] public string InputHash { get; set; } = "";
    public int? ConfigVersion { get; set; }
    public string? ResultJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Clinician review flag; deliberately no case text or generated answer.</summary>
public class AnswerFlag
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Guid AnalysisRunId { get; set; }
    public int? ConfigVersion { get; set; }
    [Required, MaxLength(32)] public string Reason { get; set; } = "";
    [MaxLength(1000)] public string? Note { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class TestRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Guid CaseId { get; set; }
    public Guid? ConfigId { get; set; }
    public int ConfigVersion { get; set; }
    public string ConfigHash { get; set; } = "";
    public int CaseRevision { get; set; } // Zero means legacy, untrusted evidence.
    [Required, MaxLength(16)] public string Status { get; set; } = "running"; // running | passed | failed
    [MaxLength(32)] public string? FailureCode { get; set; }
    public string? Diff { get; set; } // human-readable diff, never case text or prompts
    public double DurationMilliseconds { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}
