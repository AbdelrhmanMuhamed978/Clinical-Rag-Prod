import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { ConfigTestRun, ConfigVersion, ConfigVersionDetails, Rule } from "@/lib/types";
import WorkflowPage from "@/app/(app)/w/[workspaceId]/workflow/page";
import { seriousViolations } from "./axe";
import { tabTo } from "./keyboard";
import vocabulary from "../../python-backend/clinical-vocabulary.json";

const api = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn(), patch: vi.fn() }));
vi.mock("@/lib/api", () => ({ ...api, ApiError: class extends Error { constructor(public status: number, message: string) { super(message); } } }));
vi.mock("next/navigation", () => ({ useParams: () => ({ workspaceId: "workspace-1" }) }));
vi.mock("@/providers/AuthProvider", () => ({ useAuth: () => ({ user: { uid: "workflow-user" }, loading: false, emailVerified: true }) }));

const config = {
  prompts: { plan: "Plan {case} {n}", answer: "Answer {problem} {patient} {k} {context}", rewrite: "Rewrite {q}" },
  retrieval: { minRerank: 0.3, topK: 8, candidates: 30, queriesPerProblem: 2, maxChunks: 8 },
  chunking: { maxTokens: 400, hybrid: true }, safety: { verifierLocked: true, minQuoteMatch: 0.88 },
  future: { keep: [1, 2] },
};
let published: ConfigVersion;
let versions: ConfigVersionDetails[];
let role: string;
let rules: Rule[];
const clients: QueryClient[] = [];

function detail(version: ConfigVersion, run: ConfigTestRun | null = null, reason: string | null = "no test run"): ConfigVersionDetails {
  return { ...version, latestTestRun: run, publishBlockingReason: reason };
}
function testRun(version: ConfigVersion, status = "passed", steps = [1, 2, 3, 4, 5, 6]): ConfigTestRun {
  return { id: "real-run-1", workspaceId: version.workspaceId, configId: version.id, configVersion: version.version,
    configHash: "synthetic-server-bound-hash", status, startedAt: "2026-11-15T00:01:00Z",
    completedAt: status === "running" ? null : "2026-11-15T00:02:00Z", completedSteps: steps };
}
function mount() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Infinity } } });
  clients.push(client);
  const view = render(<QueryClientProvider client={client}><WorkflowPage /></QueryClientProvider>);
  return { client, user: userEvent.setup(), container: view.container, unmount: view.unmount };
}
async function ready() { await screen.findByText("Published v1"); await screen.findByLabelText("Configuration version"); }

beforeEach(() => {
  api.get.mockReset(); api.post.mockReset(); api.patch.mockReset();
  role = "owner"; rules = [];
  published = { id: "published-1", workspaceId: "workspace-1", version: 1, status: "published",
    configJson: JSON.stringify(config), createdAt: "2026-11-15T00:00:00Z" };
  versions = [detail({ ...published, id: "draft-2", version: 2, status: "draft",
    configJson: JSON.stringify({ ...config, retrieval: { ...config.retrieval, topK: 12 } }) }), detail(published, null, "only drafts can be published")];
  api.get.mockImplementation(async (path: string) => {
    if (path.endsWith("/published")) return { ...published };
    if (path.endsWith("/versions")) return { role, versions: [...versions] };
    if (path.endsWith("/rules")) return [...rules];
    if (path.endsWith("/drugs")) return { catalog: {}, revision: 0, isDefault: true, updatedBy: null, updatedAt: null };
    throw new Error(`Unexpected GET ${path}`);
  });
  api.post.mockImplementation(async (path: string, body?: { configJson?: string }) => {
    if (path.endsWith("/draft") || path.includes("/rollback/")) {
      const draft = { ...published, id: "draft-4", version: 4, status: "draft", configJson: body?.configJson ?? published.configJson };
      versions = [detail(draft), ...versions]; return draft;
    }
    if (path.endsWith("/discard")) { const draft = versions[0]; versions[0] = { ...draft, status: "discarded" }; return versions[0]; }
    if (path.endsWith("/publish")) { published = { ...versions[0], status: "published" }; versions[0] = detail(published, versions[0].latestTestRun, "only drafts can be published"); return published; }
    if (path.endsWith("/approve")) { rules[0] = { ...rules[0], status: "approved", revision: rules[0].revision + 1,
      reviewedAt: "2026-11-15T00:04:00Z", reviewedBy: "clinician", reviewedCatalogRevision: 0 }; return rules[0]; }
    throw new Error(`Unexpected POST ${path}`);
  });
  api.patch.mockImplementation(async (_path: string, body: { linkTerms: string }) => {
    rules[0] = { ...rules[0], ...body, status: "draft", revision: rules[0].revision + 1, reviewedAt: null,
      reviewedBy: null, reviewedCatalogRevision: null, updatedAt: "2026-11-15T00:03:00Z" }; return rules[0];
  });
});
afterEach(() => clients.splice(0).forEach(client => client.clear()));

describe("Workflow lifecycle and real test evidence", () => {
  it("has no serious or critical structural axe issues", async () => {
    const view = mount(); await screen.findByText("Published v1");
    expect(await seriousViolations(view.container, "workflow")).toEqual([]);
  });

  it("opens workflow step settings with the keyboard and moves focus to the settings heading", async () => {
    const { user } = mount(); await ready();
    const verify = await tabTo(user, element => element.tagName === "BUTTON" && element.textContent?.includes("Verify") === true, "Verify step");
    expect(verify.getAttribute("aria-current")).toBeNull();
    await user.keyboard("{Enter}");
    const heading = await screen.findByRole("heading", { name: "Verify settings" });
    await waitFor(() => expect(document.activeElement).toBe(heading));
    expect(verify.getAttribute("aria-current")).toBe("step");
    expect(verify.getAttribute("aria-controls")).toBe("workflow-settings-panel");
  });

  it("keeps publication disabled until a successful server gate refresh completes", async () => {
    const originalGet = api.get.getMockImplementation()!;
    let refreshRequested = false;
    let finishGate!: (value: unknown) => void;
    api.get.mockImplementation(async (path: string) => {
      if (path.endsWith("/tests")) return [{ id: "case-1", name: "Synthetic case" }];
      if (path.includes("/tests/runs/")) return [];
      if (path.endsWith("/versions") && refreshRequested) return new Promise(resolve => { finishGate = resolve; });
      return originalGet(path);
    });
    api.post.mockImplementationOnce(async () => {
      refreshRequested = true;
      return { ran: 1, results: [{ id: "run-1", caseId: "case-1", caseName: "Synthetic case", status: "passed" }] };
    });
    const { user } = mount(); await ready();
    await user.click(screen.getByRole("button", { name: "Run tests on this draft" }));
    await waitFor(() => expect(finishGate).toBeTypeOf("function"));
    expect(screen.getByRole("button", { name: "Publish draft v2" }).hasAttribute("disabled")).toBe(true);
    await act(async () => finishGate({ role, versions: [detail(versions[0], testRun(versions[0], "passed", []), null), versions[1]] }));
    await waitFor(() => expect(screen.getByRole("button", { name: "Publish draft v2" }).hasAttribute("disabled")).toBe(false));
    expect(screen.getAllByText("Not completed", { exact: true })).toHaveLength(6);
  });

  it("shows only new persisted progress while the batch is pending and aborts on unmount", async () => {
    const originalGet = api.get.getMockImplementation()!;
    let historyReads = 0;
    api.get.mockImplementation(async (path: string) => {
      if (path.endsWith("/tests")) return [{ id: "case-1", name: "Synthetic case" }];
      if (path.includes("/tests/runs/")) return ++historyReads === 1
        ? [{ id: "old-pass", configId: "draft-2", status: "passed" }]
        : [{ id: "new-failure", configId: "draft-2", status: "failed", failureCode: "comparison", diff: "Persisted comparison diff." }];
      return originalGet(path);
    });
    let finish!: (value: unknown) => void;
    api.post.mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
    const { user, unmount } = mount(); await ready();
    await user.click(screen.getByRole("button", { name: "Run tests on this draft" }));
    await screen.findByText("Persisted comparison diff.");
    expect(screen.getByRole("progressbar").getAttribute("value")).toBe("1");
    expect(screen.getByRole("button", { name: "Publish draft v2" }).hasAttribute("disabled")).toBe(true);
    const signal = api.post.mock.calls[0][2].signal as AbortSignal;
    const invalidate = vi.spyOn(clients[0], "invalidateQueries");
    unmount(); expect(signal.aborted).toBe(true);
    await act(async () => finish({ ran: 1, results: [] }));
    expect(invalidate).not.toHaveBeenCalled();
  });

  it("runs the selected draft, shows per-case progress/diffs, and uses the refreshed server gate", async () => {
    const originalGet = api.get.getMockImplementation()!;
    api.get.mockImplementation(async (path: string) => {
      if (path.endsWith("/tests")) return [{ id: "case-1", name: "Synthetic case", workspaceId: "workspace-1" }];
      if (path.includes("/tests/runs/")) return [];
      return originalGet(path);
    });
    let finish!: (value: unknown) => void;
    api.post.mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
    const { user } = mount(); await ready();
    await user.click(screen.getByRole("button", { name: "Run tests on this draft" }));
    await waitFor(() => expect(api.post).toHaveBeenCalledWith("/api/workspaces/workspace-1/tests/run",
      { configId: "draft-2", caseIds: ["case-1"] }, expect.objectContaining({ signal: expect.any(AbortSignal) })));
    expect(screen.getByRole("progressbar", { name: "Draft test progress" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Publish draft v2" }).hasAttribute("disabled")).toBe(true);
    versions[0] = detail(versions[0], testRun(versions[0], "failed", []), "Latest run for test case case-1 did not pass.");
    await act(async () => finish({ ran: 1, results: [{ id: "run-1", caseId: "case-1", caseName: "Synthetic case", status: "failed",
      failureCode: "comparison", diff: "Missing expected problem (mustHaveProblems #1).", durationMilliseconds: 10 }] }));
    await screen.findByText("Missing expected problem (mustHaveProblems #1).");
    await screen.findByText("Publishing blocked: Latest run for test case case-1 did not pass.");
    expect(screen.getByRole("button", { name: "Publish draft v2" }).hasAttribute("disabled")).toBe(true);
  });

  it("does not infer publication permission from a successful run response", async () => {
    const originalGet = api.get.getMockImplementation()!;
    api.get.mockImplementation(async (path: string) => path.endsWith("/tests") ? [{ id: "case-1", name: "Synthetic case" }]
      : path.includes("/tests/runs/") ? [] : originalGet(path));
    api.post.mockResolvedValueOnce({ ran: 1, results: [{ id: "run-1", caseId: "case-1", caseName: "Synthetic case", status: "passed" }] });
    const { user } = mount(); await ready();
    await user.click(screen.getByRole("button", { name: "Run tests on this draft" }));
    await screen.findByText("Synthetic case");
    await waitFor(() => expect(screen.getByRole("button", { name: "Run tests on this draft" }).hasAttribute("disabled")).toBe(false));
    expect(screen.getByRole("button", { name: "Publish draft v2" }).hasAttribute("disabled")).toBe(true);
    expect(screen.getByText("Publishing blocked: no test run")).toBeTruthy();
  });

  it("rejects an over-cap suite instead of silently selecting or running a subset", async () => {
    const originalGet = api.get.getMockImplementation()!;
    api.get.mockImplementation(async (path: string) => path.endsWith("/tests")
      ? Array.from({ length: 11 }, (_, index) => ({ id: `case-${index}`, name: "Synthetic case" })) : originalGet(path));
    const { user } = mount(); await ready();
    await user.click(screen.getByRole("button", { name: "Run tests on this draft" }));
    await screen.findByText(/Run at most 10 cases per request/);
    expect(api.post).not.toHaveBeenCalled();
  });

  it("never completes a step by opening its settings or invents a test run", async () => {
    const { user } = mount(); await ready();
    for (const step of ["Read case", "Plan", "Retrieve", "Draft", "Verify", "Reconcile"]) await user.click(screen.getByText(step, { exact: true }));
    expect(screen.getAllByText("Not completed", { exact: true })).toHaveLength(6);
    expect(api.post).not.toHaveBeenCalled();
    expect(screen.getByRole("button", { name: "Publish draft v2" }).hasAttribute("disabled")).toBe(true);
    expect(screen.getByText("Publishing blocked: no test run", { exact: true })).toBeTruthy();
  });

  it("shows a draft's changes against published without changing the editor", async () => {
    const { user } = mount(); await ready();
    await user.click(screen.getByRole("button", { name: "Show differences" }));
    const differences = screen.getByTestId("config-differences");
    expect(within(differences).getByText("retrieval.topK")).toBeTruthy();
    expect(within(differences).getByText("8", { exact: true })).toBeTruthy();
    expect(within(differences).getByText("12", { exact: true })).toBeTruthy();
    await user.click(screen.getByText("Retrieve", { exact: true }));
    expect((screen.getByLabelText("Top-k") as HTMLInputElement).value).toBe("8");
  });

  it("shows unknown dotted keys as distinct changes without duplicate React keys or changing the editor", async () => {
    const original = { ...config, future: { a: { b: 1 }, "a.b": 2 } };
    published = { ...published, configJson: JSON.stringify(original) };
    versions = [detail({ ...versions[0], configJson: JSON.stringify({ ...original, future: { a: { b: 3 }, "a.b": 4 } }) }),
      detail(published, null, "only drafts can be published")];
    const consoleError = vi.spyOn(console, "error");
    const { user } = mount(); await ready();
    await user.click(screen.getByRole("button", { name: "Show differences" }));
    const differences = screen.getByTestId("config-differences");
    expect(within(differences).getByText("future.a.b", { exact: true })).toBeTruthy();
    expect(within(differences).getByText('future["a.b"]', { exact: true })).toBeTruthy();
    expect(within(differences).getAllByRole("listitem")).toHaveLength(2);
    await user.click(screen.getByText("Plan", { exact: true }));
    expect((screen.getByLabelText("Plan prompt") as HTMLTextAreaElement).value).toBe(config.prompts.plan);
    expect(consoleError).not.toHaveBeenCalled();
    expect(api.post).not.toHaveBeenCalled();
  });

  it("explains canonical parser units and the unitless count assumption without editable controls", async () => {
    mount(); await ready();
    const panel = screen.getByRole("group", { name: "Read-only unit assumptions" });
    for (const key of ["sbp", "hr", "spo2", "temp", "hb", "wbc", "platelets", "creatinine", "potassium", "alt", "age"] as const) {
      const field = vocabulary.fields[key];
      const row = within(panel).getByText(field.label, { exact: true }).parentElement!;
      expect(within(row).getByText(field.unit, { exact: true })).toBeTruthy();
    }
    expect(within(panel).getByText(/unitless WBC and platelet counts ≥1000 are interpreted as \/µL/i)).toBeTruthy();
    expect(panel.querySelector("input, textarea, select, button")).toBeNull();
    expect(api.post).not.toHaveBeenCalled();
  });

  it("explains the locked number, wording, scope and topic checks without offering an unlock control", async () => {
    const { user } = mount(); await ready(); await user.click(screen.getByText("Verify", { exact: true }));
    const panel = screen.getByRole("group", { name: "Locked verifier checks" });
    expect(within(panel).getByText(/numbers must appear in the cited source/i)).toBeTruthy();
    expect(within(panel).getByText(/strength, scope, conditions and negation/i)).toBeTruthy();
    expect(within(panel).getByText(/source must mention the question topic/i)).toBeTruthy();
    expect(panel.querySelector("input, textarea, select, button")).toBeNull();
    expect(screen.getAllByText("Not completed", { exact: true })).toHaveLength(6);
    expect(api.post).not.toHaveBeenCalled();
  });

  it("publishes only the selected passing draft for an owner", async () => {
    versions[0] = detail(versions[0], testRun(versions[0]), null);
    const { user } = mount(); await ready();
    await user.click(screen.getByRole("button", { name: "Publish draft v2" }));
    await waitFor(() => expect(api.post).toHaveBeenCalledWith("/api/workspaces/workspace-1/config/draft-2/publish", {}));
    await screen.findByText("Published v2");
  });

  it.each(["editor", "reviewer", "viewer"])("does not let a %s publish even a passing draft", async permission => {
    role = permission; versions[0] = detail(versions[0], testRun(versions[0]), null);
    mount(); await ready();
    expect(screen.getByRole("button", { name: "Publish draft v2" }).hasAttribute("disabled")).toBe(true);
    expect(screen.getByText("Publishing blocked: only owners can publish", { exact: true })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Save draft" }).hasAttribute("disabled")).toBe(permission !== "editor");
  });

  it("uses only the loaded configuration's latest run, clearing previous completion on a newer failure", async () => {
    versions[0] = detail(versions[0], testRun(versions[0]), null);
    const { client, user } = mount(); await ready();
    expect(screen.getAllByText("Not completed", { exact: true })).toHaveLength(6);
    await user.click(screen.getByRole("button", { name: "Load draft v2" }));
    expect(screen.getAllByText("Completed", { exact: true })).toHaveLength(6);
    versions[0] = detail(versions[0], { ...testRun(versions[0], "failed", [1, 2]), id: "newer-run-2" }, "latest test run did not pass");
    const query = client.getQueryCache().findAll({ queryKey: ["config", "workspace-1"] }).find(entry => entry.queryKey.at(-1) === "versions")!;
    await act(async () => { client.setQueryData(query.queryKey, { role, versions: [...versions] }); });
    await waitFor(() => expect(screen.getAllByText("Completed", { exact: true })).toHaveLength(2));
    expect(screen.getAllByText("Not completed", { exact: true })).toHaveLength(4);
    expect(screen.getByRole("button", { name: "Publish draft v2" }).hasAttribute("disabled")).toBe(true);
    await user.click(screen.getByText("Plan", { exact: true }));
    await user.type(screen.getByLabelText("Plan prompt"), " unsaved");
    expect(screen.getAllByText("Not completed", { exact: true })).toHaveLength(6);
  });

  it("keeps dirty edits when a different publication arrives and confirms an explicit reload", async () => {
    const { client, user } = mount(); await ready();
    await user.click(screen.getByText("Plan", { exact: true }));
    await user.type(screen.getByLabelText("Plan prompt"), " keep edits");
    published = { ...published, id: "published-3", version: 3,
      configJson: JSON.stringify({ ...config, prompts: { ...config.prompts, plan: "Remote {case} {n}" } }) };
    const query = client.getQueryCache().findAll({ queryKey: ["config", "workspace-1"] }).find(entry => entry.queryKey.at(-1) === "published")!;
    await act(async () => { client.setQueryData(query.queryKey, { ...published }); });
    expect((screen.getByLabelText("Plan prompt") as HTMLTextAreaElement).value).toBe("Plan {case} {n} keep edits");
    await waitFor(() => expect(screen.getByRole("button", { name: "Save draft" }).hasAttribute("disabled")).toBe(true));
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(false);
    await user.click(screen.getByRole("button", { name: "Reload published" }));
    expect((screen.getByLabelText("Plan prompt") as HTMLTextAreaElement).value).toContain("keep edits");
    confirm.mockReturnValue(true);
    await user.click(screen.getByRole("button", { name: "Reload published" }));
    expect((screen.getByLabelText("Plan prompt") as HTMLTextAreaElement).value).toBe("Remote {case} {n}");
  });

  it("keeps a cleared numeric input blank and reports range errors instead of sending invalid values", async () => {
    const { user } = mount(); await ready(); await user.click(screen.getByText("Retrieve", { exact: true }));
    const topK = screen.getByLabelText("Top-k") as HTMLInputElement;
    await user.clear(topK); expect(topK.value).toBe("");
    await user.type(topK, "21"); await user.click(screen.getByRole("button", { name: "Save draft" }));
    expect(screen.getByRole("alert").textContent).toContain("Top-k must be an integer between 1 and 20");
    expect(topK.getAttribute("aria-invalid")).toBe("true"); expect(api.post).not.toHaveBeenCalled();
  });

  it("preserves edits typed while an earlier save is in flight and sends the published-id precondition", async () => {
    let finish!: (value: ConfigVersion) => void;
    api.post.mockImplementationOnce(() => new Promise<ConfigVersion>(resolve => { finish = resolve; }));
    const { user } = mount(); await ready(); await user.click(screen.getByText("Plan", { exact: true }));
    const plan = screen.getByLabelText("Plan prompt") as HTMLTextAreaElement;
    await user.type(plan, " first"); await user.click(screen.getByRole("button", { name: "Save draft" }));
    await waitFor(() => expect(api.post).toHaveBeenCalled());
    const [, body] = api.post.mock.calls[0]; expect(body.publishedConfigId).toBe("published-1");
    await user.type(plan, " later");
    await act(async () => { finish({ ...published, id: "draft-4", version: 4, status: "draft", configJson: body.configJson }); });
    expect((screen.getByLabelText("Plan prompt") as HTMLTextAreaElement).value).toBe("Plan {case} {n} first later");
    expect(screen.getByText("Unsaved changes", { exact: true })).toBeTruthy();
    expect(screen.getAllByText("Not completed", { exact: true })).toHaveLength(6);
  });

  it("confirms discard, preserves version history, and restores old versions only as new drafts", async () => {
    const { user } = mount(); await ready();
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(false);
    await user.click(screen.getByRole("button", { name: "Discard draft v2" })); expect(api.post).not.toHaveBeenCalled();
    confirm.mockReturnValue(true); await user.click(screen.getByRole("button", { name: "Discard draft v2" }));
    await waitFor(() => expect(api.post).toHaveBeenCalledWith("/api/workspaces/workspace-1/config/draft-2/discard", {}));
    await user.selectOptions(screen.getByLabelText("Configuration version"), "published-1");
    await user.click(screen.getByRole("button", { name: "Rollback v1 to draft" }));
    await waitFor(() => expect(api.post).toHaveBeenCalledWith("/api/workspaces/workspace-1/config/rollback/1", {}));
    expect(screen.getByText("Published v1", { exact: true })).toBeTruthy();
    await screen.findByRole("button", { name: "Publish draft v4" });
    expect(screen.getByText("Publishing blocked: no test run", { exact: true })).toBeTruthy();
  });

  it("edits rule link terms through the approval-clearing rules API, retaining edits across steps", async () => {
    rules = [{ id: "rule-1", workspaceId: "workspace-1", ruleId: "kidney", title: "Kidney caution", caseLevel: true,
      status: "approved", enabled: true, revision: 3, reviewedCatalogRevision: 0,
      linkTerms: "renal|kidney", reviewedAt: "2026-11-15T00:00:00Z", reviewedBy: "owner", updatedAt: "2026-11-15T00:00:00Z" }];
    const { user } = mount(); await ready(); await user.click(screen.getByText("Reconcile", { exact: true }));
    const terms = await screen.findByLabelText("Link terms (regular expression)");
    await user.type(terms, "|aki"); await user.click(screen.getByText("Plan", { exact: true }));
    await user.click(screen.getByText("Reconcile", { exact: true }));
    expect((screen.getByLabelText("Link terms (regular expression)") as HTMLInputElement).value).toBe("renal|kidney|aki");
    const dirty = new Event("beforeunload", { cancelable: true }); window.dispatchEvent(dirty); expect(dirty.defaultPrevented).toBe(true);
    await user.click(screen.getByRole("button", { name: "Save link terms" }));
    await waitFor(() => expect(api.patch).toHaveBeenCalledWith("/api/workspaces/workspace-1/rules/rule-1", { linkTerms: "renal|kidney|aki", expectedRevision: 3 }));
    expect(api.post.mock.calls.some(([path]) => path.endsWith("/config/draft"))).toBe(false);
    await screen.findByText("DRAFT: pending clinician review", { exact: true });
    await user.click(screen.getByRole("button", { name: "Approve rule" }));
    await user.click(await screen.findByRole("button", { name: "Approve saved definition" }));
    await waitFor(() => expect(api.post).toHaveBeenCalledWith("/api/workspaces/workspace-1/rules/rule-1/approve", { expectedRevision: 4 }));
    expect(screen.getAllByText("Not completed", { exact: true })).toHaveLength(6);
  });
});
