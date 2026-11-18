import { act, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { ConfigVersion } from "@/lib/types";
import WorkflowPage from "@/app/(app)/w/[workspaceId]/workflow/page";

const api = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn(), patch: vi.fn() }));
vi.mock("@/lib/api", () => ({ ...api, ApiError: class extends Error {} }));
vi.mock("next/navigation", () => ({ useParams: () => ({ workspaceId: "workspace-1" }) }));
vi.mock("@/providers/AuthProvider", () => ({
  useAuth: () => ({ user: { uid: "workflow-user" }, loading: false, emailVerified: true }),
}));

const initialConfig = {
  prompts: { plan: "Plan {case} {n}", answer: "Answer {problem} {patient} {k} {context}", rewrite: "Rewrite {q}" },
  retrieval: { minRerank: 0.3, topK: 8, candidates: 45, queriesPerProblem: 3, maxChunks: 12, hydeEnabled: false, futureRetrieval: true },
  chunking: { maxTokens: 500, hybrid: true, futureChunking: [1, 2] },
  safety: { verifierLocked: true, minQuoteMatch: 0.92, futureCheck: true },
  future: { nested: { untouched: "keep me" } },
};

let cfg: ConfigVersion;
const clients: QueryClient[] = [];

function mount() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Infinity } } });
  clients.push(client);
  render(<QueryClientProvider client={client}><WorkflowPage /></QueryClientProvider>);
  return { client, user: userEvent.setup() };
}

async function prompt(user: ReturnType<typeof userEvent.setup>, step: string, label: string) {
  await screen.findByText(`Published v${cfg.version}`);
  await user.click(screen.getByText(step, { exact: true }));
  const control = screen.getByText(label, { exact: true }).parentElement!.querySelector("textarea")!;
  await waitFor(() => expect(control.value).not.toBe(""));
  return control;
}

beforeEach(() => {
  cfg = { id: "published-1", workspaceId: "workspace-1", version: 1, status: "published",
    configJson: JSON.stringify(initialConfig), createdAt: "2026-11-15T00:00:00Z" };
  api.get.mockImplementation(async (path: string) => {
    if (path.endsWith("/config/published")) return { ...cfg };
    if (path.endsWith("/config/versions")) return { role: "owner", versions: [{ ...cfg, latestTestRun: null, publishBlockingReason: "only drafts can be published" }] };
    if (path.endsWith("/rules")) return [];
    if (path.endsWith("/drugs")) return { catalog: {}, revision: 0, isDefault: true, updatedBy: null, updatedAt: null };
    throw new Error(`Unexpected GET ${path}`);
  });
  api.post.mockImplementation(async (path: string, body: { configJson: string }) => {
    if (!path.endsWith("/config/draft")) throw new Error(`Unexpected POST ${path}`);
    return { ...cfg, id: "draft-2", version: 2, status: "draft", configJson: body.configJson };
  });
});

afterEach(() => { clients.splice(0).forEach(client => client.clear()); });

describe("Workflow typing and full-config saves", () => {
  it("keeps each keystroke in the plan textarea instead of resetting to published text", async () => {
    const { user } = mount();
    const plan = await prompt(user, "Plan", "Plan prompt");
    await user.type(plan, " — persistent edits");
    expect(plan.value).toBe(initialConfig.prompts.plan + " — persistent edits");
  });

  it("keeps answer edits when switching settings and refetching the same published id", async () => {
    const { client, user } = mount();
    const answer = await prompt(user, "Draft", "Answer prompt");
    await user.type(answer, " — answer edits");
    const query = client.getQueryCache().findAll({ queryKey: ["config", "workspace-1"] })
      .find(entry => entry.queryKey.at(-1) === "published")!;
    await act(async () => {
      client.setQueryData(query.queryKey, { ...cfg, configJson: JSON.stringify({ ...initialConfig,
        prompts: { ...initialConfig.prompts, answer: "Remote {problem} {patient} {k} {context}" } }) });
    });
    await user.click(screen.getByText("Retrieve", { exact: true }));
    await user.click(screen.getByText("Draft", { exact: true }));
    const restored = screen.getByText("Answer prompt", { exact: true }).parentElement!.querySelector("textarea")!;
    expect(restored.value).toBe(initialConfig.prompts.answer + " — answer edits");
  });

  it("saves all published sections and unknown nested keys, not just visible fields", async () => {
    const { user } = mount();
    const plan = await prompt(user, "Plan", "Plan prompt");
    await user.type(plan, " — saved edit");
    await user.click(screen.getByRole("button", { name: "Save draft" }));
    await waitFor(() => expect(api.post).toHaveBeenCalled());
    const [, body] = api.post.mock.calls.find(([path]) => path.endsWith("/config/draft"))!;
    const saved = JSON.parse(body.configJson);
    expect(saved.retrieval).toEqual(initialConfig.retrieval);
    expect(saved.chunking).toEqual(initialConfig.chunking);
    expect(saved.safety).toEqual(initialConfig.safety);
    expect(saved.future).toEqual(initialConfig.future);
    expect(saved.prompts.plan).toBe(initialConfig.prompts.plan + " — saved edit");
    expect(initialConfig.prompts.plan).toBe("Plan {case} {n}");
  });

  it("warns on browser unload only while edits are unsaved", async () => {
    const { user } = mount();
    const plan = await prompt(user, "Plan", "Plan prompt");
    const clean = new Event("beforeunload", { cancelable: true });
    window.dispatchEvent(clean);
    expect(clean.defaultPrevented).toBe(false);
    await user.type(plan, " — unsaved");
    const dirty = new Event("beforeunload", { cancelable: true });
    window.dispatchEvent(dirty);
    expect(dirty.defaultPrevented).toBe(true);
    await user.click(screen.getByRole("button", { name: "Save draft" }));
    await waitFor(() => expect(api.post).toHaveBeenCalled());
    await waitFor(() => expect(screen.queryByText("Unsaved changes", { exact: true })).toBeNull());
    const saved = new Event("beforeunload", { cancelable: true });
    window.dispatchEvent(saved);
    expect(saved.defaultPrevented).toBe(false);
  });

  it("initializes a newly published id once when there are no unsaved edits", async () => {
    const { client, user } = mount();
    await prompt(user, "Plan", "Plan prompt");
    const query = client.getQueryCache().findAll({ queryKey: ["config", "workspace-1"] })
      .find(entry => entry.queryKey.at(-1) === "published")!;
    cfg = { ...cfg, id: "published-3", version: 3, configJson: JSON.stringify({ ...initialConfig,
      prompts: { ...initialConfig.prompts, plan: "New plan {case} {n}" } }) };
    await act(async () => { client.setQueryData(query.queryKey, { ...cfg }); });
    const plan = await prompt(user, "Plan", "Plan prompt");
    await waitFor(() => expect(plan.value).toBe("New plan {case} {n}"));
    await user.type(plan, " plus edits");
    expect(plan.value).toBe("New plan {case} {n} plus edits");
  });
});
