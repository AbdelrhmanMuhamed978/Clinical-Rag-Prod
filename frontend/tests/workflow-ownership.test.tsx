import { act, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { ConfigVersion, Rule } from "@/lib/types";
import WorkflowPage from "@/app/(app)/w/[workspaceId]/workflow/page";

const scope = vi.hoisted(() => ({ workspaceId: "workspace-1", userId: "user-1" }));
const api = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn(), patch: vi.fn() }));
vi.mock("@/lib/api", () => ({ ...api, ApiError: class extends Error {} }));
vi.mock("next/navigation", () => ({ useParams: () => ({ workspaceId: scope.workspaceId }) }));
vi.mock("@/providers/AuthProvider", () => ({ useAuth: () => ({ user: { uid: scope.userId } }) }));
const clients: QueryClient[] = [];
const config = { prompts: { plan: "Plan {case} {n}", answer: "Answer {problem} {patient} {k} {context}", rewrite: "Rewrite {q}" },
  retrieval: { minRerank: 0.3, topK: 8, candidates: 30, queriesPerProblem: 2, maxChunks: 8 },
  chunking: { maxTokens: 400, hybrid: true }, safety: { verifierLocked: true, minQuoteMatch: 0.88 } };
function published(workspaceId: string): ConfigVersion {
  return { id: `${workspaceId}-published`, workspaceId, version: 1, status: "published", createdAt: "2026-11-15T00:00:00Z",
    configJson: JSON.stringify({ ...config, prompts: { ...config.prompts, plan: `${workspaceId} {case} {n}` } }) };
}
function rule(workspaceId: string): Rule {
  return { id: `${workspaceId}-rule`, workspaceId, title: "Synthetic rule", ruleId: "synthetic", caseLevel: true,
    status: "approved", enabled: true, revision: 3, reviewedCatalogRevision: 0, updatedAt: "2026-11-15T00:00:00Z",
    linkTerms: workspaceId, reviewedAt: "2026-11-15T00:00:00Z", reviewedBy: "clinician" };
}
function mount() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Infinity } } }); clients.push(client);
  const tree = () => <QueryClientProvider client={client}><WorkflowPage /></QueryClientProvider>;
  const view = render(tree()); return { client, user: userEvent.setup(), rerender: () => view.rerender(tree()) };
}
beforeEach(() => {
  scope.workspaceId = "workspace-1"; scope.userId = "user-1";
  api.get.mockImplementation(async (path: string) => {
    const workspaceId = path.match(/workspaces\/([^/]+)/)![1]; const version = published(workspaceId);
    if (path.endsWith("/published")) return version;
    if (path.endsWith("/versions")) return { role: "owner", versions: [{ ...version, latestTestRun: null, publishBlockingReason: "only drafts can be published" }] };
    if (path.endsWith("/rules")) return [rule(workspaceId)];
    if (path.endsWith("/drugs")) return { catalog: {}, revision: 0, isDefault: true, updatedBy: null, updatedAt: null };
    throw new Error("Unexpected synthetic endpoint");
  });
});
afterEach(() => clients.splice(0).forEach(client => client.clear()));

describe("Workflow workspace/auth ownership", () => {
  it("ignores a late draft save after workspace/user replacement rather than editing the new workspace", async () => {
    let finish!: (value: ConfigVersion) => void;
    api.post.mockImplementationOnce(() => new Promise<ConfigVersion>(resolve => { finish = resolve; }));
    const { client, user, rerender } = mount(); await screen.findByText("Published v1");
    await user.click(screen.getByText("Plan", { exact: true })); await user.type(screen.getByLabelText("Plan prompt"), " old unsaved");
    await user.click(screen.getByRole("button", { name: "Save draft" })); await waitFor(() => expect(api.post).toHaveBeenCalledOnce());
    const body = api.post.mock.calls[0][1];
    scope.workspaceId = "workspace-2"; scope.userId = "user-2"; rerender();
    await screen.findByText("Published v1"); await user.click(screen.getByText("Plan", { exact: true }));
    await waitFor(() => expect((screen.getByLabelText("Plan prompt") as HTMLTextAreaElement).value).toBe("workspace-2 {case} {n}"));
    await act(async () => { finish({ ...published("workspace-1"), id: "old-draft", version: 2, status: "draft", configJson: body.configJson }); });
    expect((screen.getByLabelText("Plan prompt") as HTMLTextAreaElement).value).toBe("workspace-2 {case} {n}");
    expect(screen.queryByText(/Saved draft v2/)).toBeNull(); expect(screen.queryByText("Unsaved changes", { exact: true })).toBeNull();
    expect(client.getQueryCache().findAll({ queryKey: ["config", "workspace-2", "user-2"] })).toHaveLength(2);
    const event = new Event("beforeunload", { cancelable: true }); window.dispatchEvent(event); expect(event.defaultPrevented).toBe(false);
  });

  it("ignores late rule saves after a workspace change and does not carry their dirty state or approval", async () => {
    let finish!: (value: Rule) => void;
    api.patch.mockImplementationOnce(() => new Promise<Rule>(resolve => { finish = resolve; }));
    const { user, rerender } = mount(); await screen.findByText("Published v1");
    await user.click(screen.getByText("Reconcile", { exact: true })); await user.type(await screen.findByLabelText("Link terms (regular expression)"), "|old");
    await user.click(screen.getByRole("button", { name: "Save link terms" })); await waitFor(() => expect(api.patch).toHaveBeenCalledOnce());
    scope.workspaceId = "workspace-2"; scope.userId = "user-2"; rerender(); await screen.findByText("Published v1");
    await user.click(screen.getByText("Reconcile", { exact: true }));
    await waitFor(() => expect((screen.getByLabelText("Link terms (regular expression)") as HTMLInputElement).value).toBe("workspace-2"));
    await act(async () => { finish({ ...rule("workspace-1"), status: "draft", revision: 4,
      linkTerms: "workspace-1|old", reviewedAt: null, reviewedBy: null, reviewedCatalogRevision: null }); });
    expect((screen.getByLabelText("Link terms (regular expression)") as HTMLInputElement).value).toBe("workspace-2");
    expect(screen.getByText("Clinician approved", { exact: true })).toBeTruthy(); expect(screen.queryByText("Unsaved link terms")).toBeNull();
    expect(screen.queryByText(/Link terms saved/)).toBeNull();
  });
});
