export const INTENDED_USE =
  "Decision support only; requires clinical review. “Not covered” means no verifiable statement in the loaded guidelines, not that a topic or patient is safe.";

export interface Citation {
  id: string;
  label: string;
  section: string;
  page: string;
  strength: string;
  certainty?: string;
  draft: boolean;
  score?: number | null;
  title?: string;
  version?: string;
  superseded?: boolean;
  effective_date?: string;
  review_by?: string;
  source_url?: string;
  permission_note?: string;
}

export interface PatientNote {
  id: string;
  title: string;
  level: string | null;
  reasons: string[];
}

export interface VerifiedItem {
  recommendation: string;
  quote: string;
  citation: Citation;
  table?: { headers: string[]; row: string[] };
  repaired?: string;
  patient_notes?: PatientNote[];
}

export interface UnverifiedItem {
  recommendation: string;
  quote: string;
  citation?: Citation;
  issues: string[];
}

export interface PipelineSource extends Citation {
  excerpt: string;
}

export type NotCoveredReason = "nothing_retrieved" | "retrieved_unverified" | "only_draft_sources";
export const NOT_COVERED_REASONS: readonly NotCoveredReason[] = ["nothing_retrieved", "retrieved_unverified", "only_draft_sources"];

export const NOT_COVERED_REASON_LABELS: Record<NotCoveredReason, string> = {
  nothing_retrieved: "Nothing was retrieved from the loaded guidelines.",
  retrieved_unverified: "Sources were retrieved, but no statement could be verified.",
  only_draft_sources: "Only draft sources were retrieved; no final source supported a verifiable statement.",
};

export interface PipelineProblem {
  title: string;
  status: "ok" | "not_found" | "unverified" | "error";
  covers?: string;
  queries: string[];
  items: VerifiedItem[];
  related?: VerifiedItem[];
  unverified: UnverifiedItem[];
  sources: PipelineSource[];
  detail?: string;
  reason?: NotCoveredReason;
}

export interface PipelineConflict {
  point: string;
  items: { recommendation: string; citation: Citation }[];
}

export type CoverageStatus = "answered" | "related_only" | "not_covered";
export interface CoverageEntry { part: string; status: CoverageStatus; }
export const COVERAGE_LABELS: Record<CoverageStatus, string> = {
  answered: "Answered",
  related_only: "Related only",
  not_covered: "Not covered",
};

export interface PipelineResult {
  plan_source: string;
  chat?: string;
  problems: PipelineProblem[];
  coverage?: CoverageEntry[];
  conflicts?: PipelineConflict[];
  not_covered: string[];
  warnings: string[];
  disclaimer: string;
  version: string;
  runId?: string;
  configVersion?: number | null;
}

export type AnalysisMode = "case" | "question" | "chat";
export interface PipelineProgress {
  message: string;
  state: "started" | "completed" | "failed" | "reported";
  stage?: "routing" | "facts" | "planning" | "retrieval" | "verification" | "patient_checks";
  stepId?: string;
  mode?: AnalysisMode;
}

export function analysisMode(result: PipelineResult): AnalysisMode | null {
  if (result.plan_source === "single_question") return "question";
  if (result.plan_source === "rules" || result.plan_source === "llm+rules") return "case";
  return result.plan_source === "chat" ? "chat" : null;
}

export function analysisModeLabel(mode: AnalysisMode | null): string {
  return mode === "case" ? "Case mode" : mode === "question" ? "Question mode"
    : mode === "chat" ? "Greeting — not a clinical answer" : "Mode not reported";
}

export function verifiedItemCount(result: PipelineResult): number {
  return result.problems.reduce((count, problem) => count + problem.items.length, 0);
}

export interface NotCoveredEntry {
  title: string;
  reason?: NotCoveredReason;
}

export function notCoveredReasonLabel(reason: NotCoveredReason | undefined): string {
  return reason ? NOT_COVERED_REASON_LABELS[reason] : "No verifiable statement is available in the loaded guidelines.";
}

/** An interrupted search is not evidence that the guidelines lack coverage.
 * Also protects saved results produced before the pipeline separated errors. */
export function notCoveredEntries(result: Pick<PipelineResult, "problems" | "not_covered">): NotCoveredEntry[] {
  const failed = new Set(result.problems.filter(problem => problem.status === "error").map(problem => problem.title));
  return result.not_covered.filter(title => !failed.has(title)).map(title => ({
    title,
    reason: result.problems.find(problem => problem.title === title && problem.status !== "error")?.reason,
  }));
}

export function notCoveredTitles(result: Pick<PipelineResult, "problems" | "not_covered">): string[] {
  return notCoveredEntries(result).map(entry => entry.title);
}

export function parsePipelineProgress(value: unknown): PipelineProgress {
  if (!isRecord(value) || !isNonemptyString(value.message) || value.message.length > 500)
    throw new Error("Invalid pipeline progress event.");
  const fields = ["stage", "state", "stepId", "mode"];
  if (!fields.some(field => field in value)) return { message: value.message, state: "reported" };
  if (!fields.every(field => typeof value[field] === "string")
    || !["routing", "facts", "planning", "retrieval", "verification", "patient_checks"].includes(value.stage as string)
    || !["started", "completed", "failed"].includes(value.state as string)
    || !["case", "question", "chat"].includes(value.mode as string)
    || (value.stepId as string).length > 64 || !/^[a-z_]+(?:-[0-9]+)?$/.test(value.stepId as string))
    throw new Error("Invalid pipeline progress metadata.");
  return { message: value.message, stage: value.stage as PipelineProgress["stage"],
    state: value.state as PipelineProgress["state"], stepId: value.stepId as string, mode: value.mode as AnalysisMode };
}

export interface AnalysisExportOptions {
  protectPatientContext?: boolean;
  /** Only pass this from the live Playground; saved results must not contain input. */
  sourceText?: string | null;
  /** False by default. Case text is never included unless the user opts in. */
  includeCaseText?: boolean;
  generatedAt?: Date;
  configVersion?: number | null;
}

export function escapeHtml(value: string): string {
  return value.replace(/[&<>"']/g, character => ({
    "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;",
  })[character]!);
}

/** A text export is built from validated evidence, never from rendered DOM text.
 * Expanding a rejected-draft disclosure or patient checks cannot leak into it.
 * Case input is excluded unless `includeCaseText` is explicitly true. */
export function formatAnalysisExport(result: PipelineResult, options: boolean | AnalysisExportOptions = false): string {
  const opts = typeof options === "boolean" ? { protectPatientContext: options } : options;
  const caseMode = opts.protectPatientContext === true || analysisMode(result) === "case";
  const configVersion = opts.configVersion ?? result.configVersion;
  const generated = (opts.generatedAt ?? new Date()).toISOString().slice(0, 10);
  const includeCaseText = opts.includeCaseText === true && !!opts.sourceText?.trim();
  const lines = [
    "# Guideline pipeline result",
    `Mode: ${analysisModeLabel(analysisMode(result))}`,
    `Config version: ${configVersion == null ? "not recorded" : `v${configVersion}`}`,
    `Exported: ${generated}`,
    `Intended use: ${INTENDED_USE}`,
    includeCaseText
      ? "Patient-specific checks, retrieved source excerpts and rejected drafts are omitted. The original input is included below by explicit choice."
      : "Patient input, patient-specific checks, retrieved source excerpts and rejected drafts are omitted.",
    "",
  ];
  if (result.coverage?.length) {
    lines.push("Question coverage:");
    for (const row of result.coverage) lines.push(`- ${row.part} — ${COVERAGE_LABELS[row.status]}`);
    lines.push("");
  }
  for (const [index, problem] of result.problems.entries()) {
    lines.push(caseMode ? `Problem ${index + 1}` : problem.title);
    if (problem.status === "error") lines.push("Analysis failed. Coverage is unknown; retry this problem. No recommendation was verified.");
    else if (problem.items.length === 0 && (problem.related?.length ?? 0) === 0) lines.push(`No verified recommendation. Not covered by the loaded guidelines. ${notCoveredReasonLabel(problem.reason)}`);
    for (const item of problem.items) {
      const citation = item.citation;
      const exact = item.table
        ? `${item.table.headers.join(" | ")}\n${item.table.row.join(" | ")}`
        : item.quote;
      // Case paraphrases may repeat patient values even when the source quote is
      // verified. Match saved-result privacy: export source words, not that prose.
      lines.push(`${caseMode ? "Verified guideline statement" : "Verified recommendation"}: ${caseMode ? exact : item.recommendation}`,
        `Exact quote: ${exact}`,
        `[${citation.id}] ${citation.label} | ${citation.section || "Section not stated"} | p.${citation.page || "not recorded"} | ${citation.strength || "Strength not stated"}${citation.certainty ? ` | ${citation.certainty} certainty` : " | certainty not stated"}${citation.draft ? " | DRAFT — not final" : ""}${citation.source_url ? ` | ${citation.source_url}` : ""}`);
      if (item.patient_notes?.length) lines.push("Live patient checks are omitted from this export. Review them in the Playground before applying this statement.");
      lines.push("");
    }
    if (problem.related?.length) {
      lines.push("Related, may not answer your question:");
      for (const item of problem.related) {
        const exact = item.table
          ? `${item.table.headers.join(" | ")}\n${item.table.row.join(" | ")}`
          : item.quote;
        lines.push(`- ${exact} [${item.citation.id}] p.${item.citation.page || "not recorded"}`);
      }
      lines.push("");
    }
    lines.push("");
  }
  let uncoveredEntries = notCoveredEntries(result);
  if (!caseMode) {
    const failedTitles = new Set(result.problems.filter(problem => problem.status === "error").map(problem => problem.title));
    const byTitle = new Map(uncoveredEntries.map(entry => [entry.title, entry]));
    for (const problem of result.problems) {
      if (failedTitles.has(problem.title)) continue;
      if (!["not_found", "unverified"].includes(problem.status)) continue;
      if (!byTitle.has(problem.title)) byTitle.set(problem.title, { title: problem.title, reason: problem.reason });
    }
    uncoveredEntries = [...byTitle.values()];
  }
  const uncovered = caseMode
    ? result.problems.flatMap((problem, index) => ["not_found", "unverified"].includes(problem.status)
      ? [`Problem ${index + 1} — ${notCoveredReasonLabel(problem.reason)}`] : [])
    : uncoveredEntries.map(entry => `${entry.title} — ${notCoveredReasonLabel(entry.reason)}`);
  if (uncovered.length) lines.push("Not covered:", ...uncovered.map(title => `- ${title}`), "");
  const failed = result.problems.flatMap((problem, index) => problem.status === "error" ? [caseMode ? `Problem ${index + 1}` : problem.title] : []);
  if (failed.length) lines.push("Analysis incomplete — coverage unknown:", ...failed.map(title => `- ${title}`), "");
  // Case warnings can repeat case values. Preserve guideline-level cautions only,
  // just as the saved-result privacy boundary does, and keep the clinical disclaimer.
  const warnings = caseMode ? [
    ...(result.problems.some(p => p.items.some(item => item.citation.draft)) ? ["Some citations come from DRAFT guidelines. Do not treat them as final."] : []),
    ...(uncovered.length ? ["Not covered does not mean the topic is unimportant or safe."] : []),
    ...(failed.length ? ["Some problems could not be analyzed. Retry them; their guideline coverage is unknown."] : []),
    "Review the live warnings and patient checks before applying an exported statement.",
  ] : result.warnings;
  if (warnings.length) lines.push("Warnings:", ...warnings.map(warning => `- ${warning}`), "");
  if (includeCaseText) lines.push("Original input (explicitly included):", "", opts.sourceText!.trim(), "");
  lines.push(result.disclaimer);
  return lines.join("\n");
}

/** Safe print document used by the browser's Print / Save as PDF flow. */
export function formatAnalysisPrintHtml(result: PipelineResult, options: boolean | AnalysisExportOptions = false): string {
  const text = formatAnalysisExport(result, options);
  return `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Guideline pipeline result</title>
<style>
  :root { color-scheme: light; }
  body { font-family: ui-sans-serif, system-ui, sans-serif; margin: 2rem auto; max-width: 52rem; padding: 0 1rem; color: #0f172a; }
  pre { white-space: pre-wrap; overflow-wrap: anywhere; font: inherit; line-height: 1.5; }
  @media print { body { margin: 0; max-width: none; } }
</style>
</head>
<body><pre>${escapeHtml(text)}</pre></body>
</html>`;
}

export interface ConversationTurn {
  question: string;
  verifiedSummary: string;
}

export interface AnalysisMetadata {
  id: string;
  workspaceId: string;
  configVersion: number | null;
  inputHash: string;
  createdAt: string;
}

export interface AnalysisListEntry extends AnalysisMetadata {
  hasResult: boolean;
}

export interface SavedAnalysis extends AnalysisMetadata {
  result: PipelineResult | null;
}

export const analysesQueryKey = (workspaceId: string, userId: string) => ["analyses", "mine", workspaceId, userId] as const;

export function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function isNonemptyString(value: unknown): value is string {
  return typeof value === "string" && value.trim().length > 0;
}

function isStringArray(value: unknown): value is string[] {
  return Array.isArray(value) && value.every((item) => typeof item === "string");
}

function isCitation(value: unknown): value is Citation {
  return isRecord(value)
    && typeof value.id === "string" && value.id.trim().length > 0
    && typeof value.label === "string" && value.label.trim().length > 0
    && typeof value.section === "string" && typeof value.page === "string"
    && typeof value.strength === "string" && typeof value.draft === "boolean"
    && (value.certainty === undefined || typeof value.certainty === "string")
    && (value.score === undefined || value.score === null || (typeof value.score === "number" && Number.isFinite(value.score)))
    && (value.superseded === undefined || typeof value.superseded === "boolean")
    && ["title", "version", "effective_date", "review_by", "source_url", "permission_note"]
      .every(key => value[key] === undefined || typeof value[key] === "string");
}

function isPatientNote(value: unknown): value is PatientNote {
  return isRecord(value) && typeof value.id === "string" && typeof value.title === "string"
    && (value.level === null || typeof value.level === "string") && isStringArray(value.reasons);
}

function isVerifiedTable(value: unknown): value is { headers: string[]; row: string[] } {
  return isRecord(value) && isStringArray(value.headers) && isStringArray(value.row)
    && value.headers.length >= 2 && value.headers.length <= 20 && value.row.length === value.headers.length;
}

function isVerifiedItem(value: unknown): value is VerifiedItem {
  return isRecord(value) && typeof value.recommendation === "string" && value.recommendation.trim().length > 0
    && typeof value.quote === "string" && value.quote.trim().length > 0 && isCitation(value.citation)
    && (value.issues === undefined || (isStringArray(value.issues) && value.issues.length === 0))
    && (value.repaired === undefined || typeof value.repaired === "string")
    && (value.table === undefined || isVerifiedTable(value.table))
    && (value.patient_notes === undefined || (Array.isArray(value.patient_notes) && value.patient_notes.every(isPatientNote)));
}

function isUnverifiedItem(value: unknown): value is UnverifiedItem {
  return isRecord(value) && typeof value.recommendation === "string" && typeof value.quote === "string"
    && isStringArray(value.issues) && value.issues.length > 0
    && (value.citation === undefined || isCitation(value.citation));
}

function isSource(value: unknown): value is PipelineSource {
  return isCitation(value) && isRecord(value) && typeof value.excerpt === "string";
}

function isConflict(value: unknown): value is PipelineConflict {
  return isRecord(value) && isNonemptyString(value.point) && Array.isArray(value.items)
    && value.items.length === 2 && value.items.every(item => isRecord(item)
      && isNonemptyString(item.recommendation) && isCitation(item.citation));
}

function isCoverageEntry(value: unknown): value is CoverageEntry {
  return isRecord(value) && isNonemptyString(value.part) && value.part.length <= 4000
    && typeof value.status === "string" && ["answered", "related_only", "not_covered"].includes(value.status);
}

function isProblem(value: unknown): value is PipelineProblem {
  return isRecord(value) && typeof value.title === "string" && value.title.trim().length > 0
    && typeof value.status === "string" && ["ok", "not_found", "unverified", "error"].includes(value.status)
    && (value.covers === undefined || (isNonemptyString(value.covers) && value.covers.length <= 4000))
    && isStringArray(value.queries) && Array.isArray(value.items) && value.items.every(isVerifiedItem)
    && (value.related === undefined || (Array.isArray(value.related) && value.related.every(isVerifiedItem)))
    && (value.status === "ok") === (value.items.length + (Array.isArray(value.related) ? value.related.length : 0) > 0)
    && Array.isArray(value.unverified) && value.unverified.every(isUnverifiedItem)
    && Array.isArray(value.sources) && value.sources.every(isSource)
    && (value.detail === undefined || typeof value.detail === "string")
    && (value.reason === undefined || (["not_found", "unverified"].includes(value.status)
      && typeof value.reason === "string" && NOT_COVERED_REASONS.includes(value.reason as NotCoveredReason)));
}

export function parsePipelineResult(value: unknown): PipelineResult {
  if (!isRecord(value) || !isNonemptyString(value.plan_source)
    || !["single_question", "rules", "llm+rules", "chat"].includes(value.plan_source) || !isNonemptyString(value.version)
    || !isNonemptyString(value.disclaimer) || !isStringArray(value.warnings) || !isStringArray(value.not_covered)
    || !Array.isArray(value.problems) || !value.problems.every(isProblem)
    || (value.coverage !== undefined && (!Array.isArray(value.coverage) || value.coverage.length > 20 || !value.coverage.every(isCoverageEntry)))
    || (value.conflicts !== undefined && (!Array.isArray(value.conflicts) || !value.conflicts.every(isConflict)))
    || (value.runId !== undefined && !isNonemptyString(value.runId))
    || (value.configVersion !== undefined && value.configVersion !== null && typeof value.configVersion !== "number")
    || (value.plan_source === "chat" && value.problems.length > 0)) {
    throw new Error("The verified pipeline returned an invalid response. No answer was displayed.");
  }
  return {
    plan_source: value.plan_source, version: value.version, disclaimer: value.disclaimer,
    warnings: [...value.warnings], not_covered: notCoveredTitles({ problems: value.problems, not_covered: value.not_covered }),
    ...(value.coverage === undefined ? {} : { coverage: value.coverage.map(row => ({ part: row.part, status: row.status })) }),
    ...(value.conflicts === undefined ? {} : { conflicts: value.conflicts.map(conflict => ({
      point: conflict.point, items: conflict.items.map(item => ({
        recommendation: item.recommendation, citation: projectCitation(item.citation),
      })),
    })) }),
    ...(value.runId === undefined ? {} : { runId: value.runId }),
    ...(value.configVersion === undefined ? {} : { configVersion: value.configVersion }),
    problems: value.problems.map((problem) => ({
      title: problem.title, status: problem.status, queries: [...problem.queries],
      ...(problem.covers === undefined ? {} : { covers: problem.covers }),
      sources: problem.sources.map((source) => ({ ...projectCitation(source), excerpt: source.excerpt })),
      items: problem.items.map((item) => ({
        recommendation: item.recommendation, quote: item.quote, citation: projectCitation(item.citation),
        ...(item.table === undefined ? {} : { table: { headers: [...item.table.headers], row: [...item.table.row] } }),
        ...(item.repaired === undefined ? {} : { repaired: item.repaired }),
        ...(item.patient_notes === undefined ? {} : { patient_notes: item.patient_notes.map((note) => ({
          id: note.id, title: note.title, level: note.level, reasons: [...note.reasons],
        })) }),
      })),
      ...(problem.related === undefined ? {} : { related: problem.related.map((item) => ({
        recommendation: item.recommendation, quote: item.quote, citation: projectCitation(item.citation),
        ...(item.table === undefined ? {} : { table: { headers: [...item.table.headers], row: [...item.table.row] } }),
        ...(item.repaired === undefined ? {} : { repaired: item.repaired }),
        ...(item.patient_notes === undefined ? {} : { patient_notes: item.patient_notes.map((note) => ({
          id: note.id, title: note.title, level: note.level, reasons: [...note.reasons],
        })) }),
      })) }),
      unverified: problem.unverified.map((item) => ({
        recommendation: item.recommendation, quote: item.quote, issues: [...item.issues],
        ...(item.citation === undefined ? {} : { citation: projectCitation(item.citation) }),
      })),
      ...(problem.detail === undefined ? {} : { detail: problem.detail }),
      ...(problem.reason === undefined ? {} : { reason: problem.reason }),
    })),
    ...(value.plan_source === "chat" && value.problems.length === 0 && typeof value.chat === "string"
      ? { chat: value.chat } : {}),
  };
}

export function displaySection(value: string): string {
  const parts = String(value || "")
    .replace(/\[\/?TABLES\]/gi, " ")
    .split(/[|·>]/)
    .map(part => part.trim())
    .filter(part => part.length > 0 && part.toUpperCase() !== "TABLES");
  return parts.length > 0 ? parts.join(" > ") : "Section not stated";
}

function projectCitation(citation: Citation): Citation {
  return {
    id: citation.id, label: citation.label, section: displaySection(citation.section), page: citation.page,
    strength: citation.strength, draft: citation.draft,
    ...(citation.certainty === undefined ? {} : { certainty: citation.certainty }),
    ...(citation.score === undefined ? {} : { score: citation.score }),
    ...(citation.superseded === undefined ? {} : { superseded: citation.superseded }),
    ...(citation.title === undefined ? {} : { title: citation.title }),
    ...(citation.version === undefined ? {} : { version: citation.version }),
    ...(citation.effective_date === undefined ? {} : { effective_date: citation.effective_date }),
    ...(citation.review_by === undefined ? {} : { review_by: citation.review_by }),
    ...(citation.source_url === undefined ? {} : { source_url: citation.source_url }),
    ...(citation.permission_note === undefined ? {} : { permission_note: citation.permission_note }),
  };
}

export function verifiedSummary(result: PipelineResult): string {
  const statements: string[] = [];
  let length = 0;
  for (const problem of result.problems) {
    if (problem.status !== "ok") continue;
    for (const item of problem.items) {
      if (!isVerifiedItem(item)) continue;
      // Planner labels are not verified clinical statements. Only verified items
      // and their source metadata belong in the summary used to resolve references.
      const statement = `${item.recommendation} [${item.citation.label}, p.${item.citation.page}${item.citation.draft ? ", DRAFT" : ""}]`;
      if (length + statement.length + 1 > 4000) continue;
      statements.push(statement);
      length += statement.length + 1;
    }
  }
  return statements.join(" ");
}

export function shouldSubmitOnEnter(event: Pick<KeyboardEvent, "key" | "shiftKey" | "isComposing" | "keyCode">): boolean {
  // keyCode 229 catches Safari's out-of-order IME composition-end event.
  return event.key === "Enter" && !event.shiftKey && !event.isComposing && event.keyCode !== 229;
}

const GUID = /^[\da-f]{8}-[\da-f]{4}-[\da-f]{4}-[\da-f]{4}-[\da-f]{12}$/i;

function parseAnalysisMetadata(value: unknown, workspaceId: string): AnalysisMetadata {
  if (!isRecord(value) || typeof value.id !== "string" || !GUID.test(value.id)
    || typeof value.workspaceId !== "string" || value.workspaceId.toLowerCase() !== workspaceId.toLowerCase()
    || !(value.configVersion === null || (typeof value.configVersion === "number" && Number.isInteger(value.configVersion)))
    || !isNonemptyString(value.createdAt) || typeof value.inputHash !== "string") {
    throw new Error("The saved analysis response is invalid.");
  }
  return {
    id: value.id, workspaceId: value.workspaceId, configVersion: value.configVersion,
    createdAt: value.createdAt, inputHash: value.inputHash,
  };
}

export function parseAnalysisList(value: unknown, workspaceId: string): AnalysisListEntry[] {
  if (!Array.isArray(value)) throw new Error("The saved analysis list is invalid.");
  return value.map((entry: unknown) => {
    const metadata = parseAnalysisMetadata(entry, workspaceId);
    if (!isRecord(entry) || typeof entry.hasResult !== "boolean") throw new Error("The saved analysis list is invalid.");
    return { ...metadata, hasResult: entry.hasResult };
  });
}

export function parseSavedAnalysis(value: unknown, workspaceId: string, id: string): SavedAnalysis {
  const metadata = parseAnalysisMetadata(value, workspaceId);
  if (metadata.id.toLowerCase() !== id.toLowerCase() || !isRecord(value) || !("result" in value)) {
    throw new Error("The saved analysis response is invalid.");
  }
  return { ...metadata, result: value.result === null ? null : parsePipelineResult(value.result) };
}

export function withVerifiedTurn(history: ConversationTurn[], question: string, result: PipelineResult): ConversationTurn[] {
  const summary = verifiedSummary(result);
  if (!summary || !question.trim()) return history.slice(-6);
  return [...history, { question: question.trim().slice(0, 2000), verifiedSummary: summary }].slice(-6);
}
