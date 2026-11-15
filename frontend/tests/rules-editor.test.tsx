import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import RulesPage from "@/app/(app)/w/[workspaceId]/rules/page";
import { ApiError } from "@/lib/api";
import type { Rule, RuleAccess } from "@/lib/types";
import { catalogSnapshot, deferred, previewResult, syntheticRule, unloadIsBlocked, vocabulary } from "./rules-fixtures";

const transport = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn(), patch: vi.fn(), del: vi.fn(), api: vi.fn() }));
const scope = vi.hoisted(() => ({ workspaceId: "workspace-1", userId: "user-1" }));
vi.mock("@/lib/api", () => ({ ...transport, ApiError: class extends Error { constructor(public status: number, message: string) { super(message); } } }));
vi.mock("next/navigation", () => ({ useParams: () => ({ workspaceId: scope.workspaceId }) }));
vi.mock("@/providers/AuthProvider", () => ({ useAuth: () => ({ user: { uid: scope.userId } }) }));

let rules: Rule[];
let role: RuleAccess["role"];
const root = "/api/workspaces/workspace-1/rules";
const clients: QueryClient[] = [];
function mount() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Infinity }, mutations: { retry: false } } });
  clients.push(client);
  const tree = () => <QueryClientProvider client={client}><RulesPage /></QueryClientProvider>;
  const view = render(tree());
  return { client, user: userEvent.setup(), unmount: view.unmount, rerender: () => view.rerender(tree()) };
}
const title = () => screen.getByLabelText("Rule title (required)") as HTMLInputElement;
const set = (label: string, value: string) => fireEvent.change(screen.getByLabelText(label), { target: { value } });
async function ready() { await screen.findByLabelText("Rule title (required)"); }
async function confirmAction(user: ReturnType<typeof userEvent.setup>, label: string, confirmLabel: string) {
  await user.click(screen.getByRole("button", { name: label }));
  await user.click(await screen.findByRole("button", { name: confirmLabel }));
}
function fillNew() {
  // Search the actual editor, not every hidden recognition/preview control.
  // Keep real save-button interactions and every revision assertion below.
  const titleInput = title();
  const form = within(titleInput.closest("form")!);
  const conditions = within(form.getByRole("group", { name: "Clinical conditions (required)" }));
  const sources = within(form.getByRole("group", { name: "Supporting sources (at least one required)" }));
  fireEvent.change(form.getByLabelText("Rule ID (required)"), { target: { value: "new-synthetic" } });
  fireEvent.change(titleInput, { target: { value: "New synthetic candidate" } });
  fireEvent.click(form.getByLabelText("Case-level rule (does not require a recognized medication)"));
  fireEvent.click(conditions.getByRole("button", { name: "Add condition" }));
  fireEvent.change(conditions.getByLabelText("Threshold 1"), { target: { value: "90" } });
  fireEvent.click(sources.getByRole("button", { name: "Add source" }));
  fireEvent.change(sources.getByLabelText("Source title 1 (required)"), { target: { value: "Synthetic source" } });
  fireEvent.change(sources.getByLabelText("Source URL 1 (required)"), { target: { value: "https://example.org/source" } });
  return { titleInput, saveButton: form.getByRole("button", { name: "Save rule draft" }) };
}

beforeEach(() => {
  scope.workspaceId = "workspace-1"; scope.userId = "user-1"; role = "owner"; rules = [syntheticRule()];
  Object.values(transport).forEach(mock => mock.mockReset());
  transport.api.mockResolvedValue(previewResult());
  transport.get.mockImplementation(async (path: string) => {
    if (path.endsWith("/access")) return { userId: scope.userId, role, canEdit: ["owner", "editor"].includes(role), canReview: ["owner", "reviewer"].includes(role), canDelete: role === "owner" };
    if (path.endsWith("/vocabulary")) return vocabulary;
    if (path.endsWith("/rules")) return [...rules];
    if (path.endsWith("/drugs")) return catalogSnapshot();
    if (path.includes("/audit?")) return { entries: [], hasMore: false, nextSkip: null };
    throw new Error(`Unexpected GET ${path}`);
  });
  transport.patch.mockImplementation(async (path: string, body: Partial<Rule>) => {
    const id = path.split("/").at(-1); const previous = rules.find(rule => rule.id === id)!;
    const saved = { ...previous, ...body, status: "draft" as const, enabled: false, revision: previous.revision + 1,
      updatedAt: "2026-11-15T00:03:00Z", reviewedAt: null, reviewedBy: null, reviewedCatalogRevision: null };
    rules = rules.map(rule => rule.id === id ? saved : rule); return saved;
  });
  transport.post.mockImplementation(async (path: string, body: Partial<Rule>) => {
    if (path === root) {
      const created = syntheticRule({ ...body, id: "created-1", revision: 1, status: "draft", enabled: false }); rules = [created, ...rules]; return created;
    }
    if (path.endsWith("/import-default")) return { imported: 0, skipped: rules.length, rules: [] };
    const parts = path.split("/"); const id = parts.at(-2); const action = parts.at(-1);
    const previous = rules.find(rule => rule.id === id)!;
    const saved = { ...previous, revision: previous.revision + 1 };
    if (action === "approve") Object.assign(saved, { status: "approved", reviewedBy: "clinician", reviewedAt: "2026-11-15T00:04:00Z", reviewedCatalogRevision: 2 });
    if (action === "submit") saved.status = "in_review";
    if (action === "retire") { saved.status = "retired"; saved.enabled = false; }
    if (action === "reopen") { saved.status = "draft"; saved.enabled = false; }
    if (action === "enablement") saved.enabled = body.enabled!;
    rules = rules.map(rule => rule.id === id ? saved : rule); return saved;
  });
  transport.del.mockImplementation(async () => { rules = []; });
});
afterEach(() => clients.splice(0).forEach(client => client.clear()));

describe("Rules editor snapshots and saves", () => {
  it("keeps the static Rule ID constraint valid under modern HTML Unicode-set syntax", async () => {
    mount(); await ready();
    const input = screen.getByLabelText("Rule ID (required)") as HTMLInputElement;
    // Compile the application's fixed HTML constraint, never a clinician-supplied link-term regex.
    const pattern = new RegExp(`^(?:${input.pattern})$`, "v");
    for (const value of ["synthetic-screen", "Synthetic_123", "a", "a".repeat(64)]) expect(pattern.test(value)).toBe(true);
    for (const value of ["", "synthetic(id)", "synthetic/id", "a".repeat(65), "synthetic\n"]) expect(pattern.test(value)).toBe(false);
    expect(input.getAttribute("aria-describedby")).toContain("key-help");
  });
  it("validates an empty structured candidate on submit and focuses the summary without sending", async () => {
    rules = []; const { user } = mount(); await user.click(await screen.findByRole("button", { name: "New rule" }));
    await user.click(screen.getByRole("button", { name: "Save rule draft" }));
    const summary = screen.getByRole("alert"); expect(document.activeElement).toBe(summary);
    expect(summary.textContent).toContain("Provide 1–64 condition rows"); expect(transport.post).not.toHaveBeenCalled();
    expect(title().getAttribute("aria-invalid")).toBe("true");
  });
  it("creates a disabled draft then rebases subsequent edits onto its new database ID and revision", async () => {
    rules = []; const { user } = mount(); await user.click(await screen.findByRole("button", { name: "New rule" }));
    const { titleInput, saveButton } = fillNew();
    await user.click(saveButton);
    await screen.findByText("Saved draft revision 1. Clinician review is required before analysis.");
    expect(transport.post.mock.calls[0][0]).toBe(root); expect(transport.post.mock.calls[0][1].enabled).toBe(false);
    expect(transport.post.mock.calls[0][1]).not.toHaveProperty("status");
    fireEvent.change(titleInput, { target: { value: "Edited created candidate" } }); await user.click(saveButton);
    await waitFor(() => expect(transport.patch).toHaveBeenCalledWith(`${root}/created-1`, { title: "Edited created candidate", expectedRevision: 1 }));
    expect(transport.post).toHaveBeenCalledTimes(1); expect(unloadIsBlocked()).toBe(false);
  });
  it("retains edits typed while creation is in flight and rebases their next save", async () => {
    rules = []; const pending = deferred<Rule>(); transport.post.mockReturnValueOnce(pending.promise);
    const { user } = mount(); await user.click(await screen.findByRole("button", { name: "New rule" }));
    const { titleInput, saveButton } = fillNew();
    await user.click(saveButton); await waitFor(() => expect(transport.post).toHaveBeenCalledOnce());
    const submitted = transport.post.mock.calls[0][1]; fireEvent.change(titleInput, { target: { value: "Later unsaved title" } });
    const created = syntheticRule({ ...submitted, id: "created-1", revision: 1 }); rules = [created];
    await act(async () => { pending.resolve(created); });
    expect(titleInput.value).toBe("Later unsaved title"); expect(screen.getByText("Unsaved rule changes")).toBeTruthy();
    await user.click(saveButton);
    await waitFor(() => expect(transport.patch).toHaveBeenCalledWith(`${root}/created-1`, { title: "Later unsaved title", expectedRevision: 1 }));
  });
  it("preserves source metadata and untouched JSON when saving just a title", async () => {
    const { user } = mount(); await ready();
    set("Rule title (required)", "Edited synthetic title"); await user.click(screen.getByRole("button", { name: "Save rule draft" }));
    await waitFor(() => expect(transport.patch).toHaveBeenCalledWith(`${root}/rule-1`, { title: "Edited synthetic title", expectedRevision: 3 }));
    expect(JSON.parse(rules[0].sources!)[0].provenance.keep).toEqual([1, 2]);
    expect(rules[0].drugClasses).toBe('[ "nsaid" ]'); expect(transport.patch.mock.calls[0][1]).not.toHaveProperty("enabled");
  });
  it("keeps newer edits during an update and uses the saved response's revision for the next PATCH", async () => {
    const pending = deferred<Rule>(); transport.patch.mockReturnValueOnce(pending.promise);
    const { user } = mount(); await ready(); set("Rule title (required)", "First submitted title");
    await user.click(screen.getByRole("button", { name: "Save rule draft" })); await waitFor(() => expect(transport.patch).toHaveBeenCalledOnce());
    set("Rule title (required)", "Second unsaved title"); rules = [syntheticRule({ title: "First submitted title", revision: 4 })];
    await act(async () => { pending.resolve(rules[0]); }); expect(title().value).toBe("Second unsaved title");
    await user.click(screen.getByRole("button", { name: "Save rule draft" }));
    await waitFor(() => expect(transport.patch).toHaveBeenLastCalledWith(`${root}/rule-1`, { title: "Second unsaved title", expectedRevision: 4 }));
  });
  it.each(["renal|kidney", "[z-a]"])("preserves review for an exact no-op, even with legacy regex %s", async linkTerms => {
    rules = [syntheticRule({ status: "approved", reviewedAt: "2026-11-15T00:02:00Z", reviewedBy: "clinician", reviewedCatalogRevision: 2, linkTerms })];
    const { user } = mount(); await ready(); await user.click(screen.getByRole("button", { name: "Save rule draft" }));
    expect(screen.getByText("No definition changes to save. Existing review is preserved.")).toBeTruthy(); expect(transport.patch).not.toHaveBeenCalled();
    expect(screen.getByText(/Review is current/)).toBeTruthy();
  });
  it("freezes dirty snapshots on remote changes, blocks writes, and reloads only after discard confirmation", async () => {
    const { client, user } = mount(); await ready(); set("Rule title (required)", "Keep my local edits");
    rules = [syntheticRule({ revision: 4, title: "Remote saved title" })];
    await act(async () => { client.setQueryData(["rules", "workspace-1", "user-1"], [...rules]); });
    await waitFor(() => expect(screen.getByRole("button", { name: "Save rule draft" }).hasAttribute("disabled")).toBe(true));
    expect(title().value).toBe("Keep my local edits");
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(false); await user.click(screen.getByRole("button", { name: "Reload saved rule" }));
    expect(title().value).toBe("Keep my local edits"); confirm.mockReturnValue(true); await user.click(screen.getByRole("button", { name: "Reload saved rule" }));
    expect(title().value).toBe("Remote saved title"); expect(unloadIsBlocked()).toBe(false);
  });
  it("auto-rebases clean snapshots only to a higher revision, never to stale older data", async () => {
    const { client } = mount(); await ready(); rules = [syntheticRule({ revision: 4, title: "Newer saved title" })];
    await act(async () => { client.setQueryData(["rules", "workspace-1", "user-1"], [...rules]); }); await screen.findByDisplayValue("Newer saved title");
    await act(async () => { client.setQueryData(["rules", "workspace-1", "user-1"], [syntheticRule({ revision: 3, title: "Older title" })]); });
    await waitFor(() => expect(screen.getByRole("button", { name: "Save rule draft" }).hasAttribute("disabled")).toBe(true)); expect(title().value).toBe("Newer saved title");
  });
  it("keeps the exact editor on a conflict and never retries it as an unconditional update", async () => {
    transport.patch.mockRejectedValueOnce(new ApiError(409, JSON.stringify({ error: "This rule changed. Reload the saved revision before trying again." })));
    const { user } = mount(); await ready(); set("Rule title (required)", "Conflicting local title"); await user.click(screen.getByRole("button", { name: "Save rule draft" }));
    const summary = await screen.findByRole("alert"); expect(summary.textContent).toContain("Reload the saved revision"); expect(document.activeElement).toBe(summary);
    expect(title().value).toBe("Conflicting local title"); expect(transport.patch).toHaveBeenCalledOnce(); expect(unloadIsBlocked()).toBe(true);
  });
  it("does not silently replace legacy JSON; rebuilding requires explicit confirmation and complete rows", async () => {
    rules = [syntheticRule({ drugClasses: "legacy classes", appliesWhen: "legacy conditions", sources: "legacy sources" })];
    const { user } = mount(); await ready(); expect(screen.getByRole("button", { name: "Save rule draft" }).hasAttribute("disabled")).toBe(true);
    await user.click(screen.getByRole("button", { name: "Rebuild legacy definition" })); await user.click(screen.getByRole("button", { name: "Cancel" }));
    expect(transport.patch).not.toHaveBeenCalled();
    await confirmAction(user, "Rebuild legacy definition", "Start explicit repair");
    fireEvent.click(screen.getByLabelText("Case-level rule (does not require a recognized medication)"));
    fireEvent.click(screen.getByRole("button", { name: "Add condition" })); set("Threshold 1", "90");
    fireEvent.click(screen.getByRole("button", { name: "Add source" })); set("Source title 1 (required)", "Synthetic"); set("Source URL 1 (required)", "https://example.org/source");
    await user.click(screen.getByRole("button", { name: "Save rule draft" })); await waitFor(() => expect(transport.patch).toHaveBeenCalledOnce());
    expect(transport.patch.mock.calls[0][1]).toMatchObject({ expectedRevision: 3, caseLevel: true, drugClasses: "[]" });
    expect(transport.patch.mock.calls[0][1].sources).not.toContain("legacy sources");
  }, 10000);
  it("keeps the dirty editor across local sections and guards switching to another rule", async () => {
    rules.push(syntheticRule({ id: "rule-2", title: "Other synthetic candidate", ruleId: "other" }));
    const { user } = mount(); await ready(); set("Rule title (required)", "Dirty retained title");
    await user.click(screen.getByRole("button", { name: "Drug recognition" })); await user.click(screen.getByRole("button", { name: "Rule definitions" }));
    expect(title().value).toBe("Dirty retained title"); expect(unloadIsBlocked()).toBe(true);
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(false);
    await user.click(screen.getByRole("button", { name: "Other synthetic candidate" })); expect(title().value).toBe("Dirty retained title");
    expect(confirm).toHaveBeenCalledOnce();
  });
  it("ignores a late save after workspace/user ownership replacement", async () => {
    const pending = deferred<Rule>(); transport.patch.mockReturnValueOnce(pending.promise);
    const view = mount(); await ready(); set("Rule title (required)", "Old private editor title");
    await view.user.click(screen.getByRole("button", { name: "Save rule draft" })); await waitFor(() => expect(transport.patch).toHaveBeenCalledOnce());
    scope.workspaceId = "workspace-2"; scope.userId = "user-2"; rules = [syntheticRule({ workspaceId: "workspace-2", id: "other", title: "New workspace title" })];
    view.rerender(); await screen.findByDisplayValue("New workspace title");
    await act(async () => { pending.resolve(syntheticRule({ title: "Old private editor title", revision: 4 })); });
    expect(title().value).toBe("New workspace title"); expect(screen.queryByText(/Saved draft revision 4/)).toBeNull();
  });
});

describe("Rules lifecycle permissions and saved-revision actions", () => {
  it.each(["owner", "editor", "reviewer", "viewer"])("exposes distinct editing/review/delete permissions for %s", async permission => {
    role = permission; mount(); await ready();
    expect(title().readOnly).toBe(!["owner", "editor"].includes(permission));
    expect(screen.getByRole("button", { name: "Approve rule" }).hasAttribute("disabled")).toBe(!["owner", "reviewer"].includes(permission));
    expect(screen.getByRole("button", { name: "Delete rule" }).hasAttribute("disabled")).toBe(permission !== "owner");
    expect(screen.getByRole("button", { name: "Import default rules" }).hasAttribute("disabled")).toBe(!["owner", "editor"].includes(permission));
  });
  it("approves only the captured saved revision and leaves disabled candidates disabled", async () => {
    const { user, client } = mount(); await ready(); await user.click(screen.getByRole("button", { name: "Approve rule" }));
    expect(screen.getByRole("dialog").textContent).toContain("revision 3");
    rules = [syntheticRule({ revision: 4, title: "Remotely updated candidate" })];
    await act(async () => { client.setQueryData(["rules", "workspace-1", "user-1"], [...rules]); });
    await user.click(screen.getByRole("button", { name: "Approve saved definition" }));
    await waitFor(() => expect(transport.post).toHaveBeenCalledWith(`${root}/rule-1/approve`, { expectedRevision: 3 }));
    expect(transport.post.mock.calls[0][1]).not.toHaveProperty("enabled");
  });
  it("keeps enablement separate from approval and uses new revisions for retirement and reopening", async () => {
    const { user } = mount(); await ready(); await confirmAction(user, "Approve rule", "Approve saved definition");
    await waitFor(() => expect(screen.getByRole("button", { name: "Enable rule" }).hasAttribute("disabled")).toBe(false));
    expect(rules[0].enabled).toBe(false); expect(rules[0].status).toBe("approved");
    await confirmAction(user, "Enable rule", "Enable saved rule"); await waitFor(() => expect(rules[0].enabled).toBe(true));
    expect(transport.post).toHaveBeenLastCalledWith(`${root}/rule-1/enablement`, { expectedRevision: 4, enabled: true });
    await confirmAction(user, "Retire rule", "Retire saved rule"); await waitFor(() => expect(rules[0].status).toBe("retired"));
    expect(rules[0].enabled).toBe(false); await user.click(screen.getByRole("button", { name: "Reopen as draft" }));
    await waitFor(() => expect(transport.post).toHaveBeenLastCalledWith(`${root}/rule-1/reopen`, { expectedRevision: 6 }));
  });
  it("does not approve unsaved fields or mutate when permission verification fails", async () => {
    const { client } = mount(); await ready(); set("Rule title (required)", "Unsaved title");
    expect(screen.getByRole("button", { name: "Approve rule" }).hasAttribute("disabled")).toBe(true);
    transport.get.mockImplementation(async (path: string) => { if (path.endsWith("/access")) throw new ApiError(403, "PRIVATE_PERMISSION_ERROR");
      if (path.endsWith("/rules")) return rules; if (path.endsWith("/vocabulary")) return vocabulary; if (path.endsWith("/drugs")) return catalogSnapshot(); });
    await act(async () => { await client.refetchQueries({ queryKey: ["rules", "workspace-1", "user-1", "access"] }); });
    await screen.findByText("Could not verify editing or review permissions. All mutation controls stay disabled.");
    expect(title().value).toBe("Unsaved title"); expect(title().readOnly).toBe(true);
    expect(screen.getByRole("button", { name: "Save rule draft" }).hasAttribute("disabled")).toBe(true);
    expect(screen.queryByText("PRIVATE_PERMISSION_ERROR")).toBeNull();
  });
  it("imports defaults only after confirmation without approving or activating anything", async () => {
    const { user } = mount(); await ready(); await user.click(screen.getByRole("button", { name: "Import default rules" }));
    await user.click(screen.getByRole("button", { name: "Cancel" })); expect(transport.post).not.toHaveBeenCalled();
    await confirmAction(user, "Import default rules", "Import disabled drafts");
    await waitFor(() => expect(transport.post).toHaveBeenCalledWith(`${root}/import-default`, {}));
    expect(screen.getByText(/Nothing was approved or activated/)).toBeTruthy();
  });
  it("deletes by expectedRevision and opens retained rule audit rather than removing evidence", async () => {
    const { user } = mount(); await ready(); await confirmAction(user, "Delete rule", "Delete saved rule");
    await waitFor(() => expect(transport.del).toHaveBeenCalledWith(`${root}/rule-1?expectedRevision=3`));
    await screen.findByRole("heading", { name: "Rule audit history" });
    await waitFor(() => expect(transport.get.mock.calls.some(([path]) => path === `${root}/rule-1/audit?skip=0&take=25`)).toBe(true));
    expect(screen.getByText("Rule deleted. Its audit history is retained.")).toBeTruthy();
  });
  it.each(["editor", "reviewer"])("blocks preview on corrupt recognition and reveals repair evidence only to an %s", async permission => {
    role = permission;
    const get = transport.get.getMockImplementation()!;
    transport.get.mockImplementation(async (path: string) => {
      if (path.endsWith("/drugs")) throw new ApiError(409, JSON.stringify({ error: "Stored drug recognition is invalid." }));
      if (path.endsWith("/drugs/repair")) return { revision: 6, storedCatalogJson: "CORRUPT_EVIDENCE", updatedBy: "editor", updatedAt: null, errors: ["Invalid JSON."] };
      return get(path);
    });
    const { user } = mount(); await ready(); expect(screen.getByRole("button", { name: "Run canonical preview" }).hasAttribute("disabled")).toBe(true);
    await user.click(screen.getByRole("button", { name: "Drug recognition" }));
    if (permission === "editor") await screen.findByRole("button", { name: "Rebuild invalid catalog" });
    else expect(screen.queryByRole("button", { name: "Rebuild invalid catalog" })).toBeNull();
    expect(transport.get.mock.calls.some(([path]) => path.endsWith("/drugs/repair"))).toBe(permission === "editor");
    expect(within(screen.getByRole("region", { name: /Invalid stored recognition/ })).getByText(/No default or empty catalog/)).toBeTruthy();
  });
});
