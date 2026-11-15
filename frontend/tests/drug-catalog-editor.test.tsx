import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider, useQuery } from "@tanstack/react-query";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { DrugCatalogEditor } from "@/components/rules/drug-catalog-editor";
import { ApiError } from "@/lib/api";
import type { DrugCatalogSnapshot } from "@/lib/types";
import { catalogSnapshot, deferred, unloadIsBlocked, vocabulary } from "./rules-fixtures";

const transport = vi.hoisted(() => ({ patch: vi.fn() }));
vi.mock("@/lib/api", () => ({ ...transport, ApiError: class extends Error { constructor(public status: number, message: string) { super(message); } } }));
type Props = Parameters<typeof DrugCatalogEditor>[0];
const key = ["drugs", "workspace-1", "user-1"];
const clients: QueryClient[] = [];
function CatalogHost(props: Props) {
  const query = useQuery<DrugCatalogSnapshot>({ queryKey: ["drugs", props.workspaceId, props.userId], initialData: props.snapshot,
    queryFn: async () => props.snapshot!, enabled: !!props.snapshot, staleTime: Infinity });
  return <DrugCatalogEditor {...props} snapshot={query.data} />;
}
function mount(overrides: Partial<Props> = {}) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Infinity }, mutations: { retry: false } } }); clients.push(client);
  let props: Props = { workspaceId: "workspace-1", userId: "user-1", vocabulary, snapshot: catalogSnapshot(), canEdit: true, loading: false, failed: false, onRefresh: vi.fn(), ...overrides };
  const tree = () => <QueryClientProvider client={client}><CatalogHost {...props} /></QueryClientProvider>;
  const view = render(tree());
  return { client, user: userEvent.setup(), unmount: view.unmount, rerender: (next: Partial<Props>) => { props = { ...props, ...next }; view.rerender(tree()); } };
}
const name = () => screen.getByLabelText("Generic medication name (required)") as HTMLInputElement;
const brands = () => screen.getByLabelText("Brand names (one per line)") as HTMLTextAreaElement;
const change = (element: HTMLElement, value: string) => fireEvent.change(element, { target: { value } });
async function save(user: ReturnType<typeof userEvent.setup>) {
  await user.click(screen.getByRole("button", { name: "Save drug catalog" }));
  await user.click(await screen.findByRole("button", { name: "Save recognition changes" }));
}
beforeEach(() => {
  transport.patch.mockReset().mockImplementation(async (_path: string, body: { catalog: DrugCatalogSnapshot["catalog"]; expectedRevision: number }) =>
    catalogSnapshot({ catalog: body.catalog, revision: body.expectedRevision + 1 }));
});
afterEach(() => clients.splice(0).forEach(client => client.clear()));

describe("structured medication recognition editor", () => {
  it("uses explicit confirmation and revision binding, preserves Unicode aliases, and invalidates workspace rules", async () => {
    const { client, user } = mount(); const invalidate = vi.spyOn(client, "invalidateQueries");
    change(brands(), "Synthetic Brand\nStraße"); await save(user);
    await waitFor(() => expect(transport.patch).toHaveBeenCalledOnce());
    expect(transport.patch.mock.calls[0]).toEqual(["/api/workspaces/workspace-1/drugs", { expectedRevision: 2,
      catalog: { syntheticdrug: { classes: ["nsaid", "nephrotoxin"], brands: ["Synthetic Brand", "Straße"], regional_names: ["اسم تجريبي"] } } }]);
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ["rules", "workspace-1", "user-1"] });
    expect(unloadIsBlocked()).toBe(false);
  });
  it("preserves exact no-ops without opening a dialog or invalidating current reviews", async () => {
    const { user } = mount(); await user.click(screen.getByRole("button", { name: "Save drug catalog" }));
    expect(screen.queryByRole("dialog")).toBeNull(); expect(transport.patch).not.toHaveBeenCalled();
    expect(screen.getByText("No recognition changes to save. Existing reviews are preserved.")).toBeTruthy();
  });
  it("accurately reports a server-side semantic no-op rather than claiming approval was cleared", async () => {
    transport.patch.mockImplementationOnce(async (_path, body) => catalogSnapshot({ catalog: body.catalog }));
    const { user } = mount(); change(brands(), "Synthetic Brand\n\n"); await save(user);
    await screen.findByText("No recognition definition changes; existing reviews are preserved."); expect(unloadIsBlocked()).toBe(false);
  });
  it("submits a snapshot captured by the confirmation, while retaining edits made after it opened", async () => {
    const { user } = mount(); change(brands(), "First submitted brand"); await user.click(screen.getByRole("button", { name: "Save drug catalog" }));
    change(brands(), "Later unsaved brand"); await user.click(screen.getByRole("button", { name: "Save recognition changes" }));
    await waitFor(() => expect(transport.patch).toHaveBeenCalledOnce());
    expect(transport.patch.mock.calls[0][1].catalog.syntheticdrug.brands).toEqual(["First submitted brand"]);
    expect(brands().value).toBe("Later unsaved brand"); expect(unloadIsBlocked()).toBe(true);
  });
  it("rebases in-flight edits onto the returned revision for their next save", async () => {
    const pending = deferred<DrugCatalogSnapshot>(); transport.patch.mockReturnValueOnce(pending.promise);
    const { user } = mount(); change(brands(), "First brand"); await save(user); await waitFor(() => expect(transport.patch).toHaveBeenCalledOnce());
    change(brands(), "Later brand"); const submitted = transport.patch.mock.calls[0][1];
    await act(async () => { pending.resolve(catalogSnapshot({ revision: 3, catalog: submitted.catalog })); });
    expect(brands().value).toBe("Later brand"); await save(user);
    await waitFor(() => expect(transport.patch).toHaveBeenCalledTimes(2)); expect(transport.patch.mock.calls[1][1].expectedRevision).toBe(3);
  });
  it("freezes dirty remote-refetch snapshots and reloads only after confirmation", async () => {
    const { client, user } = mount(); change(brands(), "Keep dirty alias");
    const remote = catalogSnapshot({ revision: 3, catalog: { remote: { classes: ["nsaid"], brands: ["Remote Brand"], regional_names: [] } } });
    await act(async () => { client.setQueryData(key, remote); });
    await waitFor(() => expect(screen.getByRole("button", { name: "Save drug catalog" }).hasAttribute("disabled")).toBe(true));
    expect(brands().value).toBe("Keep dirty alias");
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(false); await user.click(screen.getByRole("button", { name: "Reload saved recognition" }));
    expect(brands().value).toBe("Keep dirty alias"); confirm.mockReturnValue(true); await user.click(screen.getByRole("button", { name: "Reload saved recognition" }));
    expect(name().value).toBe("remote"); expect(brands().value).toBe("Remote Brand"); expect(unloadIsBlocked()).toBe(false);
  });
  it("auto-rebases clean data forward only and does not replace it with a lower revision", async () => {
    const { client } = mount(); const remote = catalogSnapshot({ revision: 4, catalog: { newer: { classes: ["nsaid"], brands: [], regional_names: [] } } });
    await act(async () => { client.setQueryData(key, remote); }); await screen.findByDisplayValue("newer");
    await act(async () => { client.setQueryData(key, catalogSnapshot({ revision: 2 })); });
    await waitFor(() => expect(screen.getByRole("button", { name: "Save drug catalog" }).hasAttribute("disabled")).toBe(true)); expect(name().value).toBe("newer");
  });
  it("blocks invalid generics/classes/collisions and focuses errors without sending", async () => {
    const { user } = mount(); await user.click(screen.getByRole("button", { name: "Add generic" })); change(name(), "second"); change(brands(), "Synthetic Brand");
    await user.click(screen.getByRole("button", { name: "Save drug catalog" }));
    const alert = screen.getByRole("alert"); expect(document.activeElement).toBe(alert); expect(alert.textContent).toContain("two drugs");
    expect(alert.textContent).toContain("Each drug needs distinct known classes"); expect(transport.patch).not.toHaveBeenCalled();
  });
  it("removes a generic only after confirmation, never nests forms, and saves explicit empty recognition", async () => {
    const { user } = mount(); await user.click(screen.getByRole("button", { name: "Remove generic" }));
    const dialog = screen.getByRole("dialog"); expect(dialog.closest("form")).toBeNull();
    for (const form of document.querySelectorAll("form")) expect(form.parentElement?.closest("form")).toBeNull();
    await user.click(screen.getByRole("button", { name: "Cancel" })); expect(name().value).toBe("syntheticdrug");
    await user.click(screen.getByRole("button", { name: "Remove generic" })); await user.click(screen.getByRole("button", { name: "Remove generic entry" }));
    expect(screen.getByText(/This catalog is empty/)).toBeTruthy(); await save(user);
    await waitFor(() => expect(transport.patch).toHaveBeenCalledWith("/api/workspaces/workspace-1/drugs", { catalog: {}, expectedRevision: 2 }));
  });
  it("keeps conflicted input and shows an artifact-specific safe error without retrying", async () => {
    transport.patch.mockRejectedValueOnce(new ApiError(409, "PRIVATE_UNPARSEABLE_ERROR"));
    const { user } = mount(); change(brands(), "Conflicting alias"); await save(user);
    const alert = await screen.findByRole("alert"); expect(alert.textContent).toContain("recognition action could not be completed");
    expect(alert.textContent).not.toContain("PRIVATE"); expect(brands().value).toBe("Conflicting alias"); expect(transport.patch).toHaveBeenCalledOnce();
  });
  it("does not let reviewers edit or save recognition", () => {
    mount({ canEdit: false }); expect(name().readOnly).toBe(true); expect(brands().readOnly).toBe(true);
    expect(screen.getByRole("button", { name: "Add generic" }).hasAttribute("disabled")).toBe(true);
    expect(screen.getByRole("button", { name: "Save drug catalog" }).hasAttribute("disabled")).toBe(true);
  });
  it("clears editor ownership and ignores a late old-scope response", async () => {
    const pending = deferred<DrugCatalogSnapshot>(); transport.patch.mockReturnValueOnce(pending.promise);
    const view = mount(); change(brands(), "Old scope alias"); await save(view.user); await waitFor(() => expect(transport.patch).toHaveBeenCalledOnce());
    view.rerender({ workspaceId: "workspace-2", userId: "user-2", snapshot: catalogSnapshot({ catalog: { newdrug: { classes: ["nsaid"], brands: [], regional_names: [] } } }) });
    expect(name().value).toBe("newdrug"); await act(async () => { pending.resolve(catalogSnapshot({ revision: 3 })); });
    expect(name().value).toBe("newdrug"); expect(screen.queryByText(/Saved recognition revision 3/)).toBeNull();
  });
});

describe("explicit corrupt-catalog recovery", () => {
  const repair = { revision: 6, storedCatalogJson: "CORRUPT_SYNTHETIC_EVIDENCE", updatedBy: "editor", updatedAt: null, errors: ["Stored JSON is invalid."] };
  it("starts no fallback editor until confirmed, then replaces only the evidence's revision", async () => {
    const { user } = mount({ snapshot: undefined, failed: true, invalid: true, repair });
    expect(screen.queryByRole("button", { name: "Add generic" })).toBeNull(); expect(transport.patch).not.toHaveBeenCalled();
    await user.click(screen.getByRole("button", { name: "Rebuild invalid catalog" })); await user.click(screen.getByRole("button", { name: "Cancel" }));
    expect(screen.queryByRole("button", { name: "Add generic" })).toBeNull();
    await user.click(screen.getByRole("button", { name: "Rebuild invalid catalog" })); await user.click(screen.getByRole("button", { name: "Start catalog repair" }));
    expect(unloadIsBlocked()).toBe(true); expect(screen.getByText(/no recognition has been saved or substituted/)).toBeTruthy();
    await save(user); await waitFor(() => expect(transport.patch).toHaveBeenCalledWith("/api/workspaces/workspace-1/drugs", { catalog: {}, expectedRevision: 6 }));
  });
  it("does not treat network failure as corruption or offer unauthorized repair evidence", () => {
    const view = mount({ snapshot: undefined, failed: true, invalid: false });
    expect(screen.queryByRole("button", { name: "Rebuild invalid catalog" })).toBeNull();
    view.rerender({ canEdit: false, invalid: true, repair });
    expect(screen.queryByText("CORRUPT_SYNTHETIC_EVIDENCE")).toBeNull(); expect(screen.queryByRole("button", { name: "Rebuild invalid catalog" })).toBeNull();
  });
  it("blocks a rebuilding snapshot if refreshed evidence changes or fails", async () => {
    const view = mount({ snapshot: undefined, failed: true, invalid: true, repair });
    await view.user.click(screen.getByRole("button", { name: "Rebuild invalid catalog" })); await view.user.click(screen.getByRole("button", { name: "Start catalog repair" }));
    view.rerender({ repair: { ...repair, revision: 7 } });
    expect(screen.getByRole("button", { name: "Save drug catalog" }).hasAttribute("disabled")).toBe(true);
    view.rerender({ repair, repairFailed: true });
    expect(screen.getByRole("button", { name: "Save drug catalog" }).hasAttribute("disabled")).toBe(true); expect(transport.patch).not.toHaveBeenCalled();
  });
});
