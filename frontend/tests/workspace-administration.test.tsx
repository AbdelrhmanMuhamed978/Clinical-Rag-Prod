import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { WorkspaceAdministration } from "@/components/workspace-administration";
import { deferred } from "./rules-fixtures";

const mocks = vi.hoisted(() => ({ get: vi.fn(), api: vi.fn(), replace: vi.fn(), warning: vi.fn() }));
vi.mock("@/lib/api", () => ({ get: mocks.get, api: mocks.api, ApiError: class extends Error { constructor(public status: number, message: string, public details?: unknown) { super(message); } } }));
vi.mock("next/navigation", () => ({ useRouter: () => ({ replace: mocks.replace }) }));
vi.mock("sonner", () => ({ toast: { warning: mocks.warning } }));
let workspace: { id: string; name: string; ownerId: string; createdAt: string };
let invitations: { id: string; email: string; role: string }[];
const clients: QueryClient[] = [];
function mount(role: string | undefined = "owner") {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Infinity }, mutations: { retry: false } } }); clients.push(client);
  let props = { workspaceId: "workspace-1", userId: "user-1", role };
  const tree = () => <QueryClientProvider client={client}><WorkspaceAdministration {...props} /></QueryClientProvider>;
  const view = render(tree());
  return { client, user: userEvent.setup(), rerender: (next: Partial<typeof props>) => { props = { ...props, ...next }; view.rerender(tree()); }, unmount: view.unmount };
}
async function ready() { await waitFor(() => expect(screen.getByRole("button", { name: "Leave workspace" }).hasAttribute("disabled")).toBe(false)); }
beforeEach(() => {
  workspace = { id: "workspace-1", name: "Synthetic workspace", ownerId: "owner", createdAt: "2026-11-15" };
  invitations = [{ id: "invite-1", email: "pending@example.org", role: "viewer" }];
  mocks.get.mockReset().mockImplementation(async (path: string) => path.endsWith("/invitations") ? [...invitations] : { ...workspace });
  mocks.api.mockReset().mockImplementation(async (path: string, init: RequestInit) => {
    const body = init.body ? JSON.parse(String(init.body)) : undefined;
    if (path.endsWith("/invite")) { const saved = { id: "invite-2", ...body, status: "pending" }; invitations.push(saved); return saved; }
    if (path.includes("/invitations/")) { invitations = []; return { cancelled: true }; }
    if (path.endsWith("/leave")) return { removed: true };
    if (init.method === "DELETE") return { deleted: true, warning: null };
    workspace = { ...workspace, name: body.name }; return { ...workspace };
  });
  mocks.replace.mockReset(); mocks.warning.mockReset();
});
afterEach(() => clients.splice(0).forEach(client => client.clear()));

describe("workspace settings administration", () => {
  it("does not fetch or expose administration before permissions are verified", () => {
    mount(""); expect(mocks.get).not.toHaveBeenCalled(); expect(screen.queryByLabelText("Invite email")).toBeNull();
    expect(screen.getByRole("button", { name: "Leave workspace" }).hasAttribute("disabled")).toBe(true);
  });
  it("allows a viewer to leave but hides all owner controls and invitation reads", async () => {
    const view = mount("viewer"); await ready(); expect(mocks.get).toHaveBeenCalledTimes(1); expect(screen.queryByRole("button", { name: "Delete workspace" })).toBeNull();
    await view.user.click(screen.getByRole("button", { name: "Leave workspace" })); expect(mocks.api).not.toHaveBeenCalled();
    await view.user.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Leave workspace" }));
    await waitFor(() => expect(mocks.replace).toHaveBeenCalledWith("/workspaces"));
    expect(mocks.api).toHaveBeenCalledWith("/api/workspaces/workspace-1/leave", expect.objectContaining({ method: "POST", signal: expect.any(AbortSignal) }));
  });
  it("saves normalized pending invites with a native email field and role select, without claiming mail was sent", async () => {
    const view = mount(); await ready();
    fireEvent.change(screen.getByLabelText("Invite email"), { target: { value: "New@Example.org" } });
    await view.user.selectOptions(screen.getByLabelText("Invitation role"), "reviewer"); await view.user.click(screen.getByRole("button", { name: "Save invitation" }));
    expect(mocks.api).not.toHaveBeenCalled();
    await view.user.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Confirm invitation" }));
    await screen.findByText(/Pending invitation saved/); expect(screen.getByText(/no email is sent/)).toBeTruthy();
    const [path, init] = mocks.api.mock.calls[0]; expect(path).toBe("/api/workspaces/workspace-1/invite");
    expect(JSON.parse(init.body)).toEqual({ email: "new@example.org", role: "reviewer" });
    expect((screen.getByLabelText("Invite email") as HTMLInputElement).value).toBe("");
  });
  it("captures confirmed owner access and preserves edits made after opening the invitation dialog", async () => {
    const view = mount(); await ready();
    fireEvent.change(screen.getByLabelText("Invite email"), { target: { value: "Owner@Example.org" } });
    await view.user.selectOptions(screen.getByLabelText("Invitation role"), "owner"); await view.user.click(screen.getByRole("button", { name: "Save invitation" }));
    expect(screen.getByRole("dialog").textContent).toContain("Owner grants full administration, publication and workspace deletion.");
    fireEvent.change(screen.getByLabelText("Invite email"), { target: { value: "local@example.org" } });
    fireEvent.change(screen.getByLabelText("Invitation role"), { target: { value: "viewer" } });
    await view.user.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Confirm invitation" }));
    await screen.findByText(/Pending invitation saved/);
    expect(JSON.parse(mocks.api.mock.calls[0][1].body)).toEqual({ email: "owner@example.org", role: "owner" });
    expect((screen.getByLabelText("Invite email") as HTMLInputElement).value).toBe("local@example.org");
  });
  it("prefills the workspace name, preserves local edits across refresh and sends expectedName", async () => {
    const view = mount(); await ready(); expect((screen.getByLabelText("Workspace name") as HTMLInputElement).value).toBe("Synthetic workspace");
    fireEvent.change(screen.getByLabelText("Workspace name"), { target: { value: "My local name" } });
    await act(async () => { view.client.setQueryData(["workspace", "workspace-1", "user-1"], { ...workspace, name: "Remote name" }); });
    expect((screen.getByLabelText("Workspace name") as HTMLInputElement).value).toBe("My local name");
    await view.user.click(screen.getByRole("button", { name: "Save workspace name" })); await screen.findByText("Workspace renamed.");
    expect(JSON.parse(mocks.api.mock.calls[0][1].body)).toEqual({ name: "My local name", expectedName: "Remote name" });
  });
  it("cancels invitations only after confirmation and never replays a destructive request", async () => {
    const view = mount(); await ready(); await view.user.click(screen.getByRole("button", { name: "Cancel invitation" }));
    await view.user.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Cancel" })); expect(mocks.api).not.toHaveBeenCalled();
    await view.user.click(screen.getByRole("button", { name: "Cancel invitation" }));
    await view.user.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Cancel invitation" })); await screen.findByText("No pending invitations.");
    expect(mocks.api).toHaveBeenCalledOnce();
  });
  it("soft-delete clears scoped caches, leaves unrelated caches and preserves cleanup warnings after navigation", async () => {
    mocks.api.mockResolvedValueOnce({ deleted: true, warning: "Durable file cleanup is pending." });
    const view = mount(); await ready(); view.client.setQueryData(["chunks", "workspace-1"], [{ text: "Old data" }]); view.client.setQueryData(["chunks", "workspace-2"], []);
    await view.user.click(screen.getByRole("button", { name: "Delete workspace" })); expect(mocks.api).not.toHaveBeenCalled();
    await view.user.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Delete workspace" }));
    await waitFor(() => expect(mocks.replace).toHaveBeenCalledWith("/workspaces"));
    expect(view.client.getQueryData(["chunks", "workspace-1"])).toBeUndefined(); expect(view.client.getQueryData(["chunks", "workspace-2"])).toEqual([]);
    expect(mocks.warning).toHaveBeenCalledWith("Durable file cleanup is pending.");
  });
  it("last-owner leave errors do not navigate or reflect raw server text", async () => {
    mocks.api.mockRejectedValueOnce(new Error("PRIVATE_LAST_OWNER")); const view = mount(); await ready();
    await view.user.click(screen.getByRole("button", { name: "Leave workspace" })); await view.user.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Leave workspace" }));
    expect((await screen.findByRole("alert")).textContent).not.toContain("PRIVATE"); expect(mocks.replace).not.toHaveBeenCalled(); expect(mocks.api).toHaveBeenCalledOnce();
  });
  it("discards pending owner confirmation when permission is lost", async () => {
    const view = mount(); await ready(); await view.user.click(screen.getByRole("button", { name: "Delete workspace" }));
    view.rerender({ role: "viewer" }); expect(screen.queryByRole("dialog")).toBeNull(); expect(screen.queryByLabelText("Invite email")).toBeNull();
    view.rerender({ role: "owner" }); expect(screen.queryByRole("dialog")).toBeNull(); expect(mocks.api).not.toHaveBeenCalled();
  });
  it("aborts and ignores late confirmation responses when the workspace or user changes", async () => {
    const pending = deferred<unknown>(); mocks.api.mockReturnValueOnce(pending.promise);
    const view = mount(); await ready(); await view.user.click(screen.getByRole("button", { name: "Leave workspace" }));
    await view.user.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Leave workspace" }));
    const signal: AbortSignal = mocks.api.mock.calls[0][1].signal;
    workspace = { ...workspace, id: "workspace-2", name: "New workspace" }; view.rerender({ workspaceId: "workspace-2", userId: "user-2" });
    expect(signal.aborted).toBe(true); await act(async () => { pending.resolve({ removed: true }); });
    expect(mocks.replace).not.toHaveBeenCalled(); expect(screen.queryByText("Leave workspace completed.")).toBeNull();
  });
});
