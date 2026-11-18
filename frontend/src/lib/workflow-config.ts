import schema from "../../../python-backend/config/config.schema.json";
import defaultPrompts from "../../../python-backend/config/default_prompts.json";
import { ApiError } from "./api";
import type { ConfigTestRun, ConfigVersion, ConfigVersionDetails } from "./types";

export type JsonValue = string | number | boolean | null | JsonValue[] | JsonObject;
export type JsonObject = { [key: string]: JsonValue };
type SchemaRule = { type: string; default?: JsonValue; properties?: Record<string, SchemaRule> };
export const PROMPT_RULES = schema.properties.prompts.properties;
export const RETRIEVAL_RULES = schema.properties.retrieval.properties;
export const PROMPT_KEYS = ["plan", "answer", "rewrite"] as const;
export const RETRIEVAL_KEYS = ["minRerank", "topK", "candidates", "queriesPerProblem", "maxChunks"] as const;
export type WorkflowField = typeof PROMPT_KEYS[number] | typeof RETRIEVAL_KEYS[number];
export type WorkflowFields = Record<WorkflowField, string>;
export type WorkflowErrors = Partial<Record<WorkflowField, string>>;
export const FIELD_LABELS: Record<WorkflowField, string> = {
  plan: "Plan prompt", answer: "Answer prompt", rewrite: "Rewrite prompt", minRerank: "Min rerank",
  topK: "Top-k", candidates: "Candidates", queriesPerProblem: "Queries per problem", maxChunks: "Max chunks",
};

export function isJsonObject(value: unknown): value is JsonObject {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

export function parseWorkflowConfig(json: string): JsonObject | null {
  try {
    const value: unknown = JSON.parse(json);
    return isJsonObject(value) ? value : null;
  } catch { return null; }
}

function clone(value: JsonValue): JsonValue {
  if (Array.isArray(value)) return value.map(clone);
  if (isJsonObject(value)) return Object.fromEntries(Object.entries(value).map(([key, item]) => [key, clone(item)]));
  return value;
}

/** Preserve every unknown key. Object.fromEntries also treats __proto__ as data, not a prototype setter. */
export function mergeWorkflowConfig(base: JsonObject, edits: JsonObject): JsonObject {
  return Object.fromEntries([...new Set([...Object.keys(base), ...Object.keys(edits)])].map(key => {
    const original = Object.hasOwn(base, key) ? base[key] : undefined;
    const edited = Object.hasOwn(edits, key) ? edits[key] : undefined;
    return [key, edited === undefined ? clone(original!)
      : isJsonObject(original) && isJsonObject(edited) ? mergeWorkflowConfig(original, edited) : clone(edited)];
  }));
}

function schemaDefaults(rule: SchemaRule): JsonValue {
  if (rule.default !== undefined) return clone(rule.default);
  return Object.fromEntries(Object.entries(rule.properties ?? {})
    .filter(([, child]) => child.default !== undefined || child.type === "object")
    .map(([key, child]) => [key, schemaDefaults(child)]));
}

const DEFAULTS = { ...(schemaDefaults(schema) as JsonObject), prompts: defaultPrompts };

export function workflowFields(config: JsonObject): WorkflowFields {
  const value = (section: string, key: string, fallback: string | number) => {
    const group = config[section];
    const current = isJsonObject(group) ? group[key] : undefined;
    return typeof current === "string" || typeof current === "number" ? String(current) : String(fallback);
  };
  return Object.fromEntries([
    ...PROMPT_KEYS.map(key => [key, value("prompts", key, defaultPrompts[key])]),
    ...RETRIEVAL_KEYS.map(key => [key, value("retrieval", key, RETRIEVAL_RULES[key].default)]),
  ]) as WorkflowFields;
}

export function sameWorkflowFields(left: WorkflowFields, right: WorkflowFields): boolean {
  return [...PROMPT_KEYS, ...RETRIEVAL_KEYS].every(key => left[key] === right[key]);
}

/** Only submit after validateWorkflowFields succeeds; previews retain blank/invalid input instead of coercing it to zero. */
export function composeWorkflowConfig(base: JsonObject, fields: WorkflowFields): JsonObject {
  const numeric = (text: string): JsonValue => text.trim() && Number.isFinite(Number(text)) ? Number(text) : text;
  return mergeWorkflowConfig(mergeWorkflowConfig(DEFAULTS, base), {
    prompts: Object.fromEntries(PROMPT_KEYS.map(key => [key, fields[key]])),
    retrieval: Object.fromEntries(RETRIEVAL_KEYS.map(key => [key, numeric(fields[key])])),
    safety: { verifierLocked: true },
  });
}

export function validateWorkflowFields(fields: WorkflowFields): WorkflowErrors {
  const errors: WorkflowErrors = {};
  for (const key of PROMPT_KEYS) {
    const rule = PROMPT_RULES[key];
    const length = Array.from(fields[key]).length;
    const missing = rule["x-requiredPlaceholders"].filter(token => !fields[key].includes(`{${token}}`));
    if (length < rule.minLength || length > rule.maxLength) errors[key] = `${FIELD_LABELS[key]} must contain 1–${rule.maxLength} characters`;
    else if (missing.length) errors[key] = `${FIELD_LABELS[key]} is missing required placeholder(s): ${missing.map(token => `{${token}}`).join(", ")}`;
  }
  for (const key of RETRIEVAL_KEYS) {
    const rule = RETRIEVAL_RULES[key];
    const value = Number(fields[key]);
    if (!fields[key].trim() || !Number.isFinite(value) || (rule.type === "integer" && !Number.isInteger(value))
      || value < rule.minimum || value > rule.maximum) {
      errors[key] = `${FIELD_LABELS[key]} must be ${rule.type === "integer" ? "an integer" : "a number"} between ${rule.minimum} and ${rule.maximum}`;
    }
  }
  return errors;
}

function equal(left: JsonValue | undefined, right: JsonValue | undefined): boolean {
  if (left === right) return true;
  if (Array.isArray(left) && Array.isArray(right)) return left.length === right.length && left.every((item, index) => equal(item, right[index]));
  if (isJsonObject(left) && isJsonObject(right)) return Object.keys(left).length === Object.keys(right).length
    && Object.keys(left).every(key => Object.hasOwn(right, key) && equal(left[key], right[key]));
  return false;
}
export type ConfigDifference = { path: string; published: JsonValue | undefined; selected: JsonValue | undefined };
// Keep ordinary paths readable, but quote literal keys so a.b and ["a.b"] never collide.
// These labels are display-only; they must not be evaluated as JavaScript.
function differencePath(path: string[]): string {
  return path.reduce((label, key) => /^[A-Za-z_$][A-Za-z0-9_$]*$/.test(key)
    ? `${label}${label ? "." : ""}${key}` : `${label}[${JSON.stringify(key)}]`, "");
}
export function configDifferences(published: JsonObject, selected: JsonObject): ConfigDifference[] {
  const differences: ConfigDifference[] = [];
  function visit(left: JsonValue | undefined, right: JsonValue | undefined, path: string[]) {
    if (equal(left, right)) return;
    if (isJsonObject(left) && isJsonObject(right)) {
      for (const key of [...new Set([...Object.keys(left), ...Object.keys(right)])].sort())
        visit(Object.hasOwn(left, key) ? left[key] : undefined, Object.hasOwn(right, key) ? right[key] : undefined, [...path, key]);
    } else differences.push({ path: differencePath(path), published: left, selected: right });
  }
  visit(published, selected, []);
  return differences;
}

/** The server validates the hash; the UI additionally refuses evidence for another version or malformed step IDs. */
export function boundConfigRun(version: ConfigVersionDetails | undefined): ConfigTestRun | null {
  const run = version?.latestTestRun;
  if (!version || !run || !run.id || run.workspaceId !== version.workspaceId || run.configId !== version.id
    || run.configVersion !== version.version || !run.configHash || !Array.isArray(run.completedSteps)
    || run.completedSteps.some(step => !Number.isInteger(step) || step < 1 || step > 6)
    || new Set(run.completedSteps).size !== run.completedSteps.length
    || !Number.isFinite(Date.parse(run.startedAt)) || Date.parse(run.startedAt) < Date.parse(version.createdAt)) return null;
  return run;
}

export function publishBlockingReason(version: ConfigVersionDetails | undefined, role: string | undefined,
  dirty: boolean, refreshing: boolean): string | null {
  if (role?.toLowerCase() !== "owner") return "only owners can publish";
  if (!version || version.status !== "draft") return "only drafts can be published";
  if (dirty) return "save unsaved changes as a new draft and test it first";
  if (refreshing) return "refreshing versions and test status";
  if (version.publishBlockingReason) return version.publishBlockingReason;
  const run = boundConfigRun(version);
  if (!run) return "no test run";
  if (run.status === "running") return "latest test run is still running";
  if (run.status !== "passed") return "latest test run did not pass";
  if (!run.completedAt || !Number.isFinite(Date.parse(run.completedAt)) || Date.parse(run.completedAt) < Date.parse(run.startedAt)) return "latest test run is not complete";
  if (version.publishBlockingReason !== null) return "refresh versions and test status";
  return null;
}

export function configActionErrors(error: unknown, artifact = "configuration"): string[] {
  if (error instanceof ApiError) {
    if (error.status === 401) return ["Sign in again before changing workspace data."];
    if (error.status === 403) return ["Your workspace role does not allow this action."];
    if (error.status === 404) return [artifact === "configuration" ? "This workspace or version is unavailable. Refresh before trying again."
      : `This workspace or saved ${artifact} is unavailable. Refresh before trying again.`];
    if (error.status === 400 || error.status === 409 || error.status === 428) {
      try {
        const body: unknown = error.details ?? JSON.parse(error.message);
        if (isJsonObject(body)) {
          const errors = (Array.isArray(body.errors) ? body.errors : isJsonObject(body.errors) ? Object.values(body.errors).flat() : [])
            .filter(item => typeof item === "string");
          if (errors.length) return errors.slice(0, 20) as string[];
          if (typeof body.error === "string") return [body.error];
        }
      } catch { /* Never show an arbitrary response body, parser exception, or submitted prompt. */ }
    }
  }
  return [`The ${artifact} action could not be completed. Refresh and try again.`];
}

export type WorkflowEditorState = { source: ConfigVersion; publishedId: string; base: JsonObject; fields: WorkflowFields; savedFields: WorkflowFields };
export function workflowEditorState(source: ConfigVersion, publishedId: string, base = parseWorkflowConfig(source.configJson)): WorkflowEditorState | null {
  if (!base) return null;
  const fields = workflowFields(base);
  return { source, publishedId, base, fields, savedFields: fields };
}
