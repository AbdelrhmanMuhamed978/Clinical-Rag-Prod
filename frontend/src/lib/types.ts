export type Workspace = { id: string; name: string; ownerId: string; createdAt: string; allowSupersededGuidelines?: boolean };
export type AnswerFlag = {
  id: string; analysisRunId: string; configVersion?: number | null; reason: string; note?: string | null;
  createdBy?: string | null; createdAt: string;
};
export type DocumentChunkCounts = { total: number; pending: number; accepted: number; rejected: number; embedded: number; other: number };
export type ChunkJobStatus = "queued" | "running" | "pausing" | "paused" | "stopping" | "cancelled" | "completed" | "partial" | "failed" | "blocked" | "interrupted";
export type ChunkProcessingState = {
  documentId: string; workspaceId: string; status: string;
  processingOperation?: string | null; failureStage?: string | null; failureReason?: string | null;
  revision: number; updatedAt: string; pageCount?: number | null;
  chunkedPagesCount: number; chunkedPageNumbers: number[]; chunkingStatus?: string | null;
  chunkCounts: DocumentChunkCounts; job: ChunkJob | null; serverTime: string;
};
export type ChunkJob = {
  id: string; workspaceId: string; documentId: string; status: ChunkJobStatus;
  pages: number[]; completedPages: number[]; failedPages: number[]; skippedPages: number[];
  batchSize: number; maxTokens: number; batchTimeoutSeconds: number; maxRetries: number;
  failureMode: "continue" | "stop"; totalBatches: number; completedBatches: number;
  currentBatchStart?: number | null; currentBatchEnd?: number | null; errorCode?: string | null;
  retryOfJobId?: string | null; createdAt: string; startedAt?: string | null;
  completedAt?: string | null; lastHeartbeatAt?: string | null;
  canPause: boolean; canResume: boolean; canCancel: boolean; canRetry: boolean;
  nextPollAfterMs?: number | null; serverTime: string;
};
export type Document = {
  id: string; workspaceId: string; fileName: string; status: string;
  pageCount?: number | null; parsedText?: string | null; parsedTextLength?: number;
  needsOcrPages?: number[] | null;
  chunkedPagesCount?: number | null; chunkingStatus?: string | null; chunkCounts?: DocumentChunkCounts;
  society?: string | null; title?: string | null; version?: string | null; year?: number | null; guidelineStatus?: string;
  effectiveDate?: string | null; reviewByDate?: string | null; sourceUrl?: string | null; permissionNote?: string | null;
  processingOperation?: string | null; failureStage?: string | null; failureReason?: string | null; revision?: number;
  createdAt: string; updatedAt: string;
};
export type Chunk = {
  id: string; workspaceId: string; documentId: string; page: number; pageStart?: number; pageEnd?: number; section?: string;
  sectionHierarchy?: string; year?: number; strength?: string; certainty?: string;
  text: string; status: string; confidence?: number; metadata?: unknown; createdAt: string; reviewedBy?: string | null; reviewedAt?: string;
  verbatim?: boolean; type?: string; validationError?: string; reReviewRequired?: boolean;  // verbatim: is chunk text verbatim slice of source; type: front_matter | clinical
};
export type ConfigVersion = { id: string; workspaceId: string; version: number; status: string; configJson: string; createdBy?: string | null; createdAt: string; publishedAt?: string | null };
export type ConfigTestRun = {
  id: string; workspaceId: string; configId: string; configVersion: number; configHash: string;
  status: string; startedAt: string; completedAt: string | null; completedSteps: number[];
};
export type ConfigVersionDetails = ConfigVersion & { latestTestRun: ConfigTestRun | null; publishBlockingReason: string | null; publishBlockingCode?: string | null };
export type ConfigVersionsResponse = { role: string; versions: ConfigVersionDetails[] };
export type Rule = {
  id: string; workspaceId: string; ruleId: string; title: string; drugClasses?: string; appliesWhen?: string;
  level?: string; message?: string; caveat?: string; sources?: string; linkTerms?: string; caseLevel: boolean;
  status: "draft" | "in_review" | "approved" | "retired"; enabled: boolean; revision: number;
  createdBy?: string | null; updatedBy?: string | null; reviewedCatalogRevision?: number | null;
  reviewedBy?: string | null; reviewerName?: string | null; approvedSourceUrl?: string | null;
  reviewedAt?: string | null; createdAt?: string; updatedAt?: string;
};
export type RuleAccess = { userId: string; role: string; canEdit: boolean; canReview: boolean; canDelete: boolean };
export type RuleVocabulary = {
  fields: Record<string, { label: string; unit: string }>; operators: string[];
  drug_classes: Record<string, string>; statuses: Rule["status"][];
};
export type DrugCatalog = Record<string, { classes: string[]; brands: string[]; regional_names: string[] }>;
export type DrugCatalogSnapshot = {
  catalog: DrugCatalog; revision: number; isDefault: boolean; updatedBy: string | null; updatedAt: string | null;
};
export type DrugCatalogRepairSnapshot = {
  revision: number; storedCatalogJson: string; updatedBy: string | null; updatedAt: string | null; errors: string[];
};
export type RulePreview = {
  would_fire: boolean; catalog_revision: number; reasons: string[]; warnings: string[];
  values: Record<string, string | number | boolean | null>; derived: Record<string, number>;
  unit_details: Record<string, { unit: string; source_unit: string | null; converted: boolean; assumed: boolean }>;
  meds: { generic: string; input: string; classes: string[]; confidence: string; verify: boolean }[];
  regex: { matched: boolean; timed_out: boolean }; notice: string;
};
export type RuleAuditEntry = {
  id: string; workspaceId: string; ruleId: string | null; ruleKey: string | null; action: string;
  actorId: string | null; beforeJson: string | null; afterJson: string | null; createdAt: string;
};
export type RuleAuditPage = { entries: RuleAuditEntry[]; hasMore: boolean; nextSkip: number | null };
export type WorkspaceMember = { userId: string; email: string; role: string };
export type TestCase = { id: string; workspaceId: string; name: string; inputText: string; expectedJson?: string | null; lastRunJson?: string | null; lastRunAt?: string | null };
export type TestRun = { id: string; configId?: string; configHash?: string; configVersion: number; status: string; failureCode?: string | null; diff?: string | null; durationMilliseconds: number; startedAt: string; completedAt?: string | null };
export type TestRunResult = { id: string; caseId: string; caseName: string; status: string; failureCode?: string | null; diff?: string | null; durationMilliseconds: number; completedAt?: string };
export type RunTestsResponse = { ran: number; results: TestRunResult[] };
export type User = { uid: string; email: string | null };
