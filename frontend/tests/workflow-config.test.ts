import { describe, expect, it, vi } from "vitest";
import { ApiError } from "@/lib/api";
import { boundConfigRun, composeWorkflowConfig, configActionErrors, configDifferences, mergeWorkflowConfig,
  parseWorkflowConfig, publishBlockingReason, validateWorkflowFields, workflowFields, type JsonObject, type WorkflowField } from "@/lib/workflow-config";
import type { ConfigTestRun, ConfigVersionDetails } from "@/lib/types";

vi.mock("@/lib/api", () => ({ ApiError: class extends Error { constructor(public status: number, message: string) { super(message); } } }));
const version: ConfigVersionDetails = {
  id: "draft-2", workspaceId: "workspace-1", version: 2, status: "draft", configJson: "{}",
  createdAt: "2026-11-15T00:00:00Z", latestTestRun: null, publishBlockingReason: null,
};
const passed: ConfigTestRun = {
  id: "run-1", workspaceId: version.workspaceId, configId: version.id, configVersion: version.version,
  configHash: "synthetic-server-bound-hash", status: "passed", startedAt: "2026-11-15T00:01:00Z",
  completedAt: "2026-11-15T00:02:00Z", completedSteps: [1, 2, 3, 4, 5, 6],
};

describe("Complete workflow configuration helpers", () => {
  it.each(["{broken", "[]", "null", "1"])("rejects unreadable/non-object configuration %s without an exception", json => {
    expect(parseWorkflowConfig(json)).toBeNull();
  });

  it("deep merges without mutating inputs, retaining unknown arrays, nulls and nested keys", () => {
    const base = { retrieval: { topK: 8, future: true }, future: { left: 1, right: 2, list: [1, 2] } };
    const before = JSON.stringify(base);
    const merged = mergeWorkflowConfig(base, { retrieval: { topK: 12 }, future: { right: 4, list: [3], nullable: null } });
    expect(merged).toEqual({ retrieval: { topK: 12, future: true }, future: { left: 1, right: 4, list: [3], nullable: null } });
    expect(JSON.stringify(base)).toBe(before);
  });

  it("retains prototype-looking JSON keys as data without mutating object prototypes", () => {
    const base = parseWorkflowConfig('{"__proto__":{"keep":true},"constructor":{"prototype":{"safe":true}}}')!;
    const merged = mergeWorkflowConfig(base, { retrieval: { topK: 8 } });
    expect(Object.hasOwn(merged, "__proto__")).toBe(true); expect(Object.getPrototypeOf(merged)).toBe(Object.prototype);
    expect(JSON.parse(JSON.stringify(merged)).__proto__).toEqual({ keep: true });
    expect(Object.hasOwn(Object.prototype, "keep")).toBe(false);
  });

  it("fills schema defaults, keeps untouched future values and forces the verifier lock", () => {
    const base: JsonObject = { retrieval: { candidates: 60 }, safety: { verifierLocked: false, minQuoteMatch: 0.93 }, future: { keep: [1, 2] } };
    const fields = workflowFields(base); const complete = composeWorkflowConfig(base, fields);
    expect(complete.retrieval).toEqual({ minRerank: 0.3, topK: 8, candidates: 60, queriesPerProblem: 2, maxChunks: 8, hydeEnabled: false });
    expect(complete.chunking).toEqual({ maxTokens: 400, hybrid: true });
    expect(complete.safety).toEqual({ verifierLocked: true, minQuoteMatch: 0.93 }); expect(complete.future).toEqual(base.future);
    expect(validateWorkflowFields(fields)).toEqual({}); expect(base.safety).toEqual({ verifierLocked: false, minQuoteMatch: 0.93 });
  });

  it("retains cleared/invalid numeric preview strings instead of coercing them to zero", () => {
    const fields = workflowFields({}); fields.topK = ""; fields.candidates = "invalid";
    const preview = composeWorkflowConfig({}, fields);
    expect(preview.retrieval).toMatchObject({ topK: "", candidates: "invalid" });
    expect(Object.keys(validateWorkflowFields(fields))).toEqual(["topK", "candidates"]);
  });

  it.each<[WorkflowField, string]>([
    ["minRerank", "-0.01"], ["minRerank", "1.01"], ["topK", "0"], ["topK", "21"], ["topK", "1.5"],
    ["candidates", "4"], ["candidates", "101"], ["queriesPerProblem", "0"], ["queriesPerProblem", "6"],
    ["maxChunks", "0"], ["maxChunks", "21"], ["topK", ""],
  ])("rejects out-of-range %s=%s", (field, value) => {
    const fields = workflowFields({}); fields[field] = value; expect(validateWorkflowFields(fields)[field]).toBeTruthy();
  });

  it("accepts all inclusive range boundaries and literal JSON/unknown braces", () => {
    const lower = { ...workflowFields({}), minRerank: "0", topK: "1", candidates: "5", queriesPerProblem: "1", maxChunks: "1" };
    const upper = { ...workflowFields({}), minRerank: "1", topK: "20", candidates: "100", queriesPerProblem: "5", maxChunks: "20" };
    lower.plan += ' JSON {"unknown":true}, literal {futureToken}';
    expect(validateWorkflowFields(lower)).toEqual({}); expect(validateWorkflowFields(upper)).toEqual({});
  });

  it("shares the required placeholder rules and reports no submitted prompt content", () => {
    const fields = workflowFields({}); fields.plan = "PRIVATE_PROMPT_MARKER {case}";
    fields.answer = "{problem} {patient} {k}"; fields.rewrite = "missing";
    const errors = validateWorkflowFields(fields);
    expect(errors.plan).toContain("{n}"); expect(errors.answer).toContain("{context}"); expect(errors.rewrite).toContain("{q}");
    expect(JSON.stringify(errors)).not.toContain("PRIVATE_PROMPT_MARKER");
  });

  it("compares object values rather than serialization order and reports every changed path", () => {
    expect(configDifferences({ a: 1, nested: { b: 2, c: 3 } }, { nested: { c: 3, b: 2 }, a: 1 })).toEqual([]);
    expect(configDifferences({ a: 1, future: [1, 2], old: null }, { a: 2, future: [2], added: true })).toEqual([
      { path: "a", published: 1, selected: 2 }, { path: "added", published: undefined, selected: true },
      { path: "future", published: [1, 2], selected: [2] }, { path: "old", published: null, selected: undefined },
    ]);
  });

  it("distinguishes nested paths from unknown keys containing dots, quotes, brackets or empty names", () => {
    const published: JsonObject = { future: { a: { b: 1 }, "a.b": 2, "": 3, 'quoted"key': 4, "items[0]": 5 } };
    const selected: JsonObject = { future: { a: { b: 11 }, "a.b": 12, "": 13, 'quoted"key': 14, "items[0]": 15 } };
    const differences = configDifferences(published, selected);
    expect(differences.map(change => change.path)).toEqual([
      'future[""]', "future.a.b", 'future["a.b"]', 'future["items[0]"]', 'future["quoted\\"key"]',
    ]);
    expect(new Set(differences.map(change => change.path)).size).toBe(differences.length);
    expect(differences.map(change => [change.published, change.selected])).toEqual([[3, 13], [1, 11], [2, 12], [5, 15], [4, 14]]);
  });
});

describe("Draft-bound evidence and safe blocking reasons", () => {
  it("refuses evidence for another workspace/version, old runs and malformed step IDs", () => {
    for (const patch of [
      { workspaceId: "other" }, { configId: "other" }, { configVersion: 3 }, { configHash: "" }, { id: "" },
      { startedAt: "2026-11-14T00:00:00Z" }, { startedAt: "invalid" }, { completedSteps: [0] }, { completedSteps: [1, 1] },
    ]) expect(boundConfigRun({ ...version, latestTestRun: { ...passed, ...patch } })).toBeNull();
    expect(boundConfigRun({ ...version, latestTestRun: passed })).toEqual(passed);
  });

  it("requires owner access, a saved draft, fresh server status and a completed latest pass", () => {
    const tested = { ...version, latestTestRun: passed };
    expect(publishBlockingReason(tested, "owner", false, false)).toBeNull();
    expect(publishBlockingReason(tested, "editor", false, false)).toBe("only owners can publish");
    expect(publishBlockingReason(tested, undefined, false, false)).toBe("only owners can publish");
    expect(publishBlockingReason({ ...tested, status: "archived" }, "owner", false, false)).toBe("only drafts can be published");
    expect(publishBlockingReason(tested, "owner", true, false)).toContain("save unsaved changes");
    expect(publishBlockingReason(tested, "owner", false, true)).toContain("refreshing");
    expect(publishBlockingReason(version, "owner", false, false)).toBe("no test run");
    expect(publishBlockingReason({ ...tested, latestTestRun: { ...passed, status: "failed" } }, "owner", false, false)).toBe("latest test run did not pass");
    expect(publishBlockingReason({ ...tested, latestTestRun: { ...passed, status: "running", completedAt: null } }, "owner", false, false)).toBe("latest test run is still running");
    for (const completedAt of [null, "invalid", "2026-11-15T00:00:30Z"])
      expect(publishBlockingReason({ ...tested, latestTestRun: { ...passed, completedAt } }, "owner", false, false)).toBe("latest test run is not complete");
    expect(publishBlockingReason({ ...tested, publishBlockingReason: "no test run" }, "owner", false, false)).toBe("no test run");
  });

  it("uses only structured safe validation/lifecycle errors and never exposes arbitrary failures", () => {
    expect(configActionErrors(new ApiError(400, JSON.stringify({ errors: ["Top-k is invalid", 12] })))).toEqual(["Top-k is invalid"]);
    expect(configActionErrors(new ApiError(409, JSON.stringify({ error: "no test run" })))).toEqual(["no test run"]);
    for (const error of [new Error("PRIVATE_FAILURE"), new ApiError(500, "PRIVATE_FAILURE"), new ApiError(400, "PRIVATE_FAILURE")])
      expect(configActionErrors(error).join()).not.toContain("PRIVATE_FAILURE");
    expect(configActionErrors(new ApiError(403, "PRIVATE_FAILURE"))[0]).toContain("role");
    expect(configActionErrors(new ApiError(404, "PRIVATE_FAILURE"))[0]).toContain("unavailable");
  });
});
