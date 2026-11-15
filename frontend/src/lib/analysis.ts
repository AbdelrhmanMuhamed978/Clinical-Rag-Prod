export interface Citation {
  id: string;
  label: string;
  section: string;
  page: string;
  strength: string;
  draft: boolean;
  score?: number | null;
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

export interface PipelineProblem {
  title: string;
  status: "ok" | "not_found" | "unverified" | "error";
  queries: string[];
  items: VerifiedItem[];
  unverified: UnverifiedItem[];
  sources: PipelineSource[];
  detail?: string;
}

export interface PipelineResult {
  plan_source: string;
  chat?: string;
  problems: PipelineProblem[];
  not_covered: string[];
  warnings: string[];
  disclaimer: string;
  version: string;
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

/** An interrupted search is not evidence that the guidelines lack coverage.
 * Also protects saved results produced before the pipeline separated errors. */
export function notCoveredTitles(result: Pick<PipelineResult, "problems" | "not_covered">): string[] {
  const failed = new Set(result.problems.filter(problem => problem.status === "error").map(problem => problem.title));
  return result.not_covered.filter(title => !failed.has(title));
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

/** A text export is built from validated evidence, never from rendered DOM text.
 * Expanding a rejected-draft disclosure cannot leak it into copied answers. */
export function formatAnalysisExport(result: PipelineResult, protectPatientContext = false): string {
  const caseMode = protectPatientContext || analysisMode(result) === "case";
  const lines = ["Guideline pipeline result", analysisModeLabel(analysisMode(result)),
    "Patient input, patient-specific checks, retrieved excerpts and rejected drafts are omitted.", ""];
  for (const [index, problem] of result.problems.entries()) {
    lines.push(caseMode ? `Problem ${index + 1}` : problem.title);
    if (problem.status === "error") lines.push("Analysis failed. Coverage is unknown; retry this problem. No recommendation was verified.");
    else if (problem.items.length === 0) lines.push("No verified recommendation. Not covered by the loaded guidelines.");
    for (const item of problem.items) {
      const citation = item.citation;
      // Case paraphrases may repeat patient values even when the source quote is
      // verified. Match saved-result privacy: export source words, not that prose.
      lines.push(`${caseMode ? "Verified guideline statement" : "Verified recommendation"}: ${caseMode ? item.quote : item.recommendation}`, `Exact quote: ${item.quote}`,
        `[${citation.id}] ${citation.label} | ${citation.section || "Section not recorded"} | p.${citation.page || "not recorded"} | ${citation.strength || "Strength not recorded"}${citation.draft ? " | DRAFT — not final" : ""}`);
      if (item.patient_notes?.length) lines.push("Check against this patient: patient-specific checks are omitted. Review them in the Playground before applying this statement.");
      lines.push("");
    }
    lines.push("");
  }
  const uncovered = caseMode
    ? result.problems.flatMap((problem, index) => ["not_found", "unverified"].includes(problem.status) ? [`Problem ${index + 1}`] : [])
    : notCoveredTitles(result);
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
  lines.push(result.disclaimer);
  return lines.join("\n");
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
    && (value.score === undefined || value.score === null || (typeof value.score === "number" && Number.isFinite(value.score)));
}

function isPatientNote(value: unknown): value is PatientNote {
  return isRecord(value) && typeof value.id === "string" && typeof value.title === "string"
    && (value.level === null || typeof value.level === "string") && isStringArray(value.reasons);
}

function isVerifiedItem(value: unknown): value is VerifiedItem {
  return isRecord(value) && typeof value.recommendation === "string" && value.recommendation.trim().length > 0
    && typeof value.quote === "string" && value.quote.trim().length > 0 && isCitation(value.citation)
    && (value.issues === undefined || (isStringArray(value.issues) && value.issues.length === 0))
    && (value.repaired === undefined || typeof value.repaired === "string")
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

function isProblem(value: unknown): value is PipelineProblem {
  return isRecord(value) && typeof value.title === "string" && value.title.trim().length > 0
    && typeof value.status === "string" && ["ok", "not_found", "unverified", "error"].includes(value.status)
    && isStringArray(value.queries) && Array.isArray(value.items) && value.items.every(isVerifiedItem)
    && (value.status === "ok") === (value.items.length > 0)
    && Array.isArray(value.unverified) && value.unverified.every(isUnverifiedItem)
    && Array.isArray(value.sources) && value.sources.every(isSource)
    && (value.detail === undefined || typeof value.detail === "string");
}

export function parsePipelineResult(value: unknown): PipelineResult {
  if (!isRecord(value) || !isNonemptyString(value.plan_source)
    || !["single_question", "rules", "llm+rules", "chat"].includes(value.plan_source) || !isNonemptyString(value.version)
    || !isNonemptyString(value.disclaimer) || !isStringArray(value.warnings) || !isStringArray(value.not_covered)
    || !Array.isArray(value.problems) || !value.problems.every(isProblem)
    || (value.plan_source === "chat" && value.problems.length > 0)) {
    throw new Error("The verified pipeline returned an invalid response. No answer was displayed.");
  }
  return {
    plan_source: value.plan_source, version: value.version, disclaimer: value.disclaimer,
    warnings: [...value.warnings], not_covered: notCoveredTitles({ problems: value.problems, not_covered: value.not_covered }),
    problems: value.problems.map((problem) => ({
      title: problem.title, status: problem.status, queries: [...problem.queries],
      sources: problem.sources.map((source) => ({ ...projectCitation(source), excerpt: source.excerpt })),
      items: problem.items.map((item) => ({
        recommendation: item.recommendation, quote: item.quote, citation: projectCitation(item.citation),
        ...(item.repaired === undefined ? {} : { repaired: item.repaired }),
        ...(item.patient_notes === undefined ? {} : { patient_notes: item.patient_notes.map((note) => ({
          id: note.id, title: note.title, level: note.level, reasons: [...note.reasons],
        })) }),
      })),
      unverified: problem.unverified.map((item) => ({
        recommendation: item.recommendation, quote: item.quote, issues: [...item.issues],
        ...(item.citation === undefined ? {} : { citation: projectCitation(item.citation) }),
      })),
      ...(problem.detail === undefined ? {} : { detail: problem.detail }),
    })),
    ...(value.plan_source === "chat" && value.problems.length === 0 && typeof value.chat === "string"
      ? { chat: value.chat } : {}),
  };
}

function projectCitation(citation: Citation): Citation {
  return {
    id: citation.id, label: citation.label, section: citation.section, page: citation.page,
    strength: citation.strength, draft: citation.draft,
    ...(citation.score === undefined ? {} : { score: citation.score }),
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
