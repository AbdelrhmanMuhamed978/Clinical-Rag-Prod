import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { WorkspaceMemberRoles } from "@/components/rules/workspace-member-roles";
import { ApiError } from "@/lib/api";
import type { WorkspaceMember } from "@/lib/types";
import { deferred, unloadIsBlocked } from "./rules-fixtures";

const transport = vi.hoisted(() => ({ get: vi.fn(), patch: vi.fn(), api: vi.fn() }));
vi.mock("@/lib/api", () => ({ ...transport, ApiError: class extends Error { constructor(public status: number, message: string) { super(message); } } }));
type Props = Parameters<typeof WorkspaceMemberRoles>[0];
const key = ["workspace-members", "workspace-1", "owner-1"];
const clients: QueryClient[] = [];
let members: WorkspaceMember[];
function mount(overrides: Partial<Props> = {}) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Infinity }, mutations: { retry: false } } }); clients.push(client);
  let props = { workspaceId: "workspace-1", userId: "owner-1", canAdmin: true, ...overrides };
  const tree = () => <QueryClientProvider client={client}><WorkspaceMemberRoles {...props} /></QueryClientProvider>;
  const view = render(tree());
  return { client, user: userEvent.setup(), rerender: (next: Partial<Props>) => { props = { ...props, ...next }; view.rerender(tree()); } };
}
const editorSection = () => screen.getByRole("region", { name: "synthetic-editor@example.org" });
const choose = (role: string) => fireEvent.click(within(editorSection()).getByRole("radio", { name: new RegExp(`^${role}`) }));
const saveButton = () => within(editorSection()).getByRole("button", { name: "Save member role" });
async function save(user: ReturnType<typeof userEvent.setup>) {
  await user.click(saveButton()); await user.click(await screen.findByRole("button", { name: "Save member access" }));
}
beforeEach(() => {
  members = [{ userId: "owner-1", email: "synthetic-owner@example.org", role: "owner" },
    { userId: "member-1", email: "synthetic-editor@example.org", role: "editor" }];
  transport.get.mockReset().mockImplementation(async () => [...members]);
  transport.patch.mockReset().mockImplementation(async (_path, body: { role: string }) => {
    members = members.map(member => member.userId === "member-1" ? { ...member, role: body.role } : member);
    return { userId: "member-1", role: body.role };
  });
  transport.api.mockReset().mockImplementation(async (path: string) => {
    members = members.filter(member => !path.includes(member.userId)); return { removed: true };
  });
});
afterEach(() => clients.splice(0).forEach(client => client.clear()));

describe("owner-only clinician reviewer provisioning", () => {
  it("does not load or expose member data before owner permission is verified", async () => {
    mount({ canAdmin: false }); expect(transport.get).not.toHaveBeenCalled();
    expect(screen.queryByRole("heading", { name: "synthetic-editor@example.org" })).toBeNull();
    expect(screen.getByText(/Owner permission must be verified/)).toBeTruthy();
  });
  it("guards the last owner, offers explicit owner promotion and sends the saved-role precondition", async () => {
    const { user, client } = mount(); await screen.findByRole("region", { name: "synthetic-editor@example.org" });
    const owner = screen.getByRole("region", { name: "synthetic-owner@example.org" });
    expect(within(owner).getByRole("radio", { name: /^Viewer/ }).hasAttribute("disabled")).toBe(true);
    expect(within(owner).getByRole("button", { name: "Remove member" }).hasAttribute("disabled")).toBe(true);
    expect(within(editorSection()).getByRole("radio", { name: /^Owner/ })).toBeTruthy();
    expect(screen.queryByRole("button", { name: /invite|transfer/i })).toBeNull();
    const invalidate = vi.spyOn(client, "invalidateQueries"); choose("Reviewer"); expect(unloadIsBlocked()).toBe(true); await save(user);
    await waitFor(() => expect(transport.patch).toHaveBeenCalledWith("/api/workspaces/workspace-1/members/member-1/role", { role: "reviewer", expectedRole: "editor" }, { signal: expect.any(AbortSignal) }));
    await screen.findByText(/Saved reviewer access/); expect(unloadIsBlocked()).toBe(false);
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ["rules", "workspace-1"] }); expect(invalidate).toHaveBeenCalledWith({ queryKey: ["config", "workspace-1"] });
  });
  it("saves no-op selections without opening confirmation or writing", async () => {
    const { user } = mount(); await screen.findByRole("region", { name: "synthetic-editor@example.org" }); await user.click(saveButton());
    expect(screen.getByText("No access changes to save.")).toBeTruthy(); expect(screen.queryByRole("dialog")).toBeNull(); expect(transport.patch).not.toHaveBeenCalled();
  });
  it("binds confirmation to the captured role but preserves a later unsaved selection", async () => {
    const { user } = mount(); await screen.findByRole("region", { name: "synthetic-editor@example.org" }); choose("Reviewer");
    await user.click(saveButton()); choose("Viewer"); await user.click(screen.getByRole("button", { name: "Save member access" }));
    await screen.findByText(/Saved reviewer access/);
    expect(transport.patch.mock.calls[0][1]).toEqual({ role: "reviewer", expectedRole: "editor" });
    expect((within(editorSection()).getByRole("radio", { name: /^Viewer/ }) as HTMLInputElement).checked).toBe(true);
    expect(screen.getByText("Unsaved member-role change")).toBeTruthy();
  });
  it("rebases selections made during a save onto the returned role for the next request", async () => {
    const pending = deferred<Pick<WorkspaceMember, "userId" | "role">>(); transport.patch.mockReturnValueOnce(pending.promise);
    const { user } = mount(); await screen.findByRole("region", { name: "synthetic-editor@example.org" }); choose("Reviewer"); await save(user);
    await waitFor(() => expect(transport.patch).toHaveBeenCalledOnce()); choose("Viewer");
    members[1] = { ...members[1], role: "reviewer" }; await act(async () => { pending.resolve({ userId: "member-1", role: "reviewer" }); });
    await screen.findByText(/Saved reviewer access/); await save(user);
    await waitFor(() => expect(transport.patch).toHaveBeenLastCalledWith("/api/workspaces/workspace-1/members/member-1/role", { role: "viewer", expectedRole: "reviewer" }, { signal: expect.any(AbortSignal) }));
  });
  it("freezes dirty selections on remote changes and requires confirmed reload before retrying", async () => {
    const { client, user } = mount(); await screen.findByRole("region", { name: "synthetic-editor@example.org" }); choose("Reviewer");
    members[1] = { ...members[1], role: "viewer" }; await act(async () => { client.setQueryData(key, [...members]); });
    await screen.findByText(/Member access changed remotely/); expect(saveButton().hasAttribute("disabled")).toBe(true);
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(false); await user.click(within(editorSection()).getByRole("button", { name: "Reload member role" }));
    expect((within(editorSection()).getByRole("radio", { name: /^Reviewer/ }) as HTMLInputElement).checked).toBe(true);
    confirm.mockReturnValue(true); await user.click(within(editorSection()).getByRole("button", { name: "Reload member role" }));
    expect((within(editorSection()).getByRole("radio", { name: /^Viewer/ }) as HTMLInputElement).checked).toBe(true); expect(unloadIsBlocked()).toBe(false);
  });
  it("retains a conflicting selection and shows only a safe role-specific error", async () => {
    transport.patch.mockRejectedValueOnce(new ApiError(409, "PRIVATE_ROLE_ERROR"));
    const { user } = mount(); await screen.findByRole("region", { name: "synthetic-editor@example.org" }); choose("Reviewer"); await save(user);
    const alert = await screen.findByRole("alert"); expect(alert.textContent).toContain("member access action could not be completed"); expect(alert.textContent).not.toContain("PRIVATE");
    expect(document.activeElement).toBe(alert); expect(transport.patch).toHaveBeenCalledOnce(); expect(unloadIsBlocked()).toBe(true);
  });
  it("hides already-loaded member details when owner permission is lost without transmitting more data", async () => {
    const view = mount(); await screen.findByRole("region", { name: "synthetic-editor@example.org" }); choose("Reviewer");
    const calls = transport.get.mock.calls.length; view.rerender({ canAdmin: false });
    expect(screen.queryByRole("heading", { name: "synthetic-editor@example.org" })).toBeNull(); expect(screen.queryByRole("radio")).toBeNull();
    expect(document.body.textContent).not.toContain("synthetic-editor@example.org");
    expect(document.body.textContent).not.toContain("member-1");
    expect(transport.get).toHaveBeenCalledTimes(calls); expect(transport.patch).not.toHaveBeenCalled();
    view.rerender({ canAdmin: true }); await screen.findByRole("region", { name: "synthetic-editor@example.org" });
    expect((within(editorSection()).getByRole("radio", { name: /^Reviewer/ }) as HTMLInputElement).checked).toBe(true);
  });
  it("dismisses pending role confirmation on permission loss and never reopens or executes it", async () => {
    const view = mount(); await screen.findByRole("region", { name: "synthetic-editor@example.org" }); choose("Reviewer");
    await view.user.click(saveButton()); expect(screen.getByRole("dialog")).toBeTruthy();
    view.rerender({ canAdmin: false }); expect(document.querySelector("dialog")).toBeNull();
    expect(transport.patch).not.toHaveBeenCalled();
    view.rerender({ canAdmin: true }); await screen.findByRole("region", { name: "synthetic-editor@example.org" });
    expect(screen.queryByRole("dialog")).toBeNull(); expect(transport.patch).not.toHaveBeenCalled();
    expect((within(editorSection()).getByRole("radio", { name: /^Reviewer/ }) as HTMLInputElement).checked).toBe(true);
  });
  it("keeps snapshots but disables saves on failed member refresh", async () => {
    const { client } = mount(); await screen.findByRole("region", { name: "synthetic-editor@example.org" }); choose("Reviewer");
    transport.get.mockRejectedValueOnce(new Error("PRIVATE_MEMBERS_ERROR")); await act(async () => { await client.refetchQueries({ queryKey: key }); });
    await screen.findByText(/Member access could not be refreshed/); expect(saveButton().hasAttribute("disabled")).toBe(true);
    expect((within(editorSection()).getByRole("radio", { name: /^Reviewer/ }) as HTMLInputElement).checked).toBe(true);
    expect(screen.queryByText("PRIVATE_MEMBERS_ERROR")).toBeNull();
  });
  it("ignores a late mutation after user/workspace replacement", async () => {
    const pending = deferred<Pick<WorkspaceMember, "userId" | "role">>(); transport.patch.mockReturnValueOnce(pending.promise);
    const view = mount(); await screen.findByRole("region", { name: "synthetic-editor@example.org" }); choose("Reviewer"); await save(view.user);
    await waitFor(() => expect(transport.patch).toHaveBeenCalledOnce());
    const signal: AbortSignal = transport.patch.mock.calls[0][2].signal;
    members = [{ userId: "new-member", email: "new-workspace@example.org", role: "viewer" }]; view.rerender({ workspaceId: "workspace-2", userId: "owner-2" });
    await screen.findByRole("heading", { name: "new-workspace@example.org" });
    expect(signal.aborted).toBe(true);
    await act(async () => { pending.resolve({ userId: "member-1", role: "reviewer" }); });
    expect(screen.queryByRole("heading", { name: "synthetic-editor@example.org" })).toBeNull(); expect(screen.queryByText(/Saved reviewer access/)).toBeNull();
  });
  it("aborts role saves on permission loss and ignores their late response after permission returns", async () => {
    const pending = deferred<Pick<WorkspaceMember, "userId" | "role">>(); transport.patch.mockReturnValueOnce(pending.promise);
    const view = mount(); await screen.findByRole("region", { name: "synthetic-editor@example.org" }); choose("Reviewer"); await save(view.user);
    await waitFor(() => expect(transport.patch).toHaveBeenCalledOnce());
    const signal: AbortSignal = transport.patch.mock.calls[0][2].signal;
    view.rerender({ canAdmin: false }); expect(signal.aborted).toBe(true);
    view.rerender({ canAdmin: true }); await screen.findByRole("region", { name: "synthetic-editor@example.org" });
    await act(async () => { pending.resolve({ userId: "member-1", role: "reviewer" }); });
    expect(view.client.getQueryData<WorkspaceMember[]>(key)?.find(item => item.userId === "member-1")?.role).toBe("editor");
    expect(screen.queryByText(/Saved reviewer access/)).toBeNull();
  });
  it("requires confirmation to remove a member and binds removal to the saved role", async () => {
    const view = mount(); await screen.findByRole("region", { name: "synthetic-editor@example.org" });
    await view.user.click(within(editorSection()).getByRole("button", { name: "Remove member" }));
    expect(transport.api).not.toHaveBeenCalled();
    await view.user.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Remove member" }));
    await waitFor(() => expect(transport.api).toHaveBeenCalledWith("/api/workspaces/workspace-1/members/member-1?expectedRole=editor", expect.objectContaining({ method: "DELETE", signal: expect.any(AbortSignal) })));
    await waitFor(() => expect(screen.queryByRole("region", { name: "synthetic-editor@example.org" })).toBeNull());
  });
  it("keeps members on failed removal and shows a safe error", async () => {
    transport.api.mockRejectedValueOnce(new ApiError(409, "PRIVATE_REMOVAL_ERROR"));
    const view = mount(); await screen.findByRole("region", { name: "synthetic-editor@example.org" });
    await view.user.click(within(editorSection()).getByRole("button", { name: "Remove member" }));
    await view.user.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Remove member" }));
    expect((await screen.findByRole("alert")).textContent).not.toContain("PRIVATE"); expect(editorSection()).toBeTruthy();
  });
});
