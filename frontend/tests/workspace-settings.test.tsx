import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import SettingsPage from "@/app/(app)/w/[workspaceId]/settings/page";
import { clearWorkspaceQueries } from "@/lib/workspace-management";
import { deferred } from "./rules-fixtures";

const mocks = vi.hoisted(() => ({ get: vi.fn(), api: vi.fn(), patch: vi.fn(), replace: vi.fn(), workspaceId: "workspace-1", uid: "firebase-owner" }));
vi.mock("@/lib/api", () => ({ ...mocks, ApiError: class extends Error {} }));
vi.mock("next/navigation", () => ({ useParams: () => ({ workspaceId: mocks.workspaceId }), useRouter: () => ({ replace: mocks.replace }) }));
vi.mock("@/providers/AuthProvider", () => ({ useAuth: () => ({ user: { uid: mocks.uid } }) }));
vi.mock("@/components/operations/colab-settings", () => ({ ColabSettings: () => null }));

const clients: QueryClient[] = [];
function mount() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Infinity, refetchOnWindowFocus: false } } });
  clients.push(client);
  const tree = () => <QueryClientProvider client={client}><SettingsPage /></QueryClientProvider>;
  const view = render(tree());
  return { client, user: userEvent.setup(), rerender: () => view.rerender(tree()) };
}
async function removeSelf(user: ReturnType<typeof userEvent.setup>) {
  const region = await screen.findByRole("region", { name: "self@example.org" });
  await user.click(within(region).getByRole("button", { name: "Remove member" }));
  await user.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Remove member" }));
}
beforeEach(() => {
  mocks.workspaceId = "workspace-1"; mocks.uid = "firebase-owner"; mocks.replace.mockReset();
  mocks.get.mockReset().mockImplementation(async (path: string) => {
    if (path.endsWith("/rules/access")) return { userId: "internal-self", role: "owner", canEdit: true, canReview: true, canDelete: true };
    if (path.endsWith("/members")) return [
      { userId: "internal-self", email: "self@example.org", role: "owner" },
      { userId: "internal-other", email: "other@example.org", role: "owner" },
    ];
    if (path.endsWith("/invitations")) return [];
    return { id: mocks.workspaceId, name: "Synthetic workspace", ownerId: "internal-self", createdAt: "2026-11-15" };
  });
  mocks.api.mockReset().mockResolvedValue({ removed: true });
});
afterEach(() => clients.splice(0).forEach(client => client.clear()));

describe("Settings self-removal and cache ownership", () => {
  it("exits after removing the signed-in internal member and clears only that workspace's caches", async () => {
    const view = mount();
    view.client.setQueryData(["chunks", "workspace-1"], ["old evidence"]);
    view.client.setQueryData(["chunks", "workspace-2"], ["unrelated evidence"]);
    const invalidate = vi.spyOn(view.client, "invalidateQueries");
    await removeSelf(view.user);
    await waitFor(() => expect(mocks.replace).toHaveBeenCalledWith("/workspaces"));
    expect(mocks.api).toHaveBeenCalledWith("/api/workspaces/workspace-1/members/internal-self?expectedRole=owner", expect.objectContaining({ method: "DELETE" }));
    expect(view.client.getQueryData(["chunks", "workspace-1"])).toBeUndefined();
    expect(view.client.getQueryData(["chunks", "workspace-2"])).toEqual(["unrelated evidence"]);
    expect(invalidate.mock.calls.every(([filter]) => !filter?.queryKey?.includes("workspace-1"))).toBe(true);
  });
  it("does not navigate a replacement user/workspace when old self-removal cleanup finishes late", async () => {
    const view = mount();
    const cleanup = deferred<void>();
    vi.spyOn(view.client, "cancelQueries").mockImplementationOnce(() => cleanup.promise);
    await removeSelf(view.user);
    await waitFor(() => expect(view.client.cancelQueries).toHaveBeenCalled());
    mocks.workspaceId = "workspace-2"; mocks.uid = "new-user"; view.rerender();
    await act(async () => { cleanup.resolve(); });
    expect(mocks.replace).not.toHaveBeenCalled();
  });
  it("cancels scoped reads before removing their caches and cannot be repopulated by a late transport", async () => {
    const client = new QueryClient(); clients.push(client);
    const pending = deferred<string[]>(); let signal: AbortSignal | undefined;
    const read = client.fetchQuery({ queryKey: ["chunks", "workspace-1"], queryFn: ({ signal: token }) => { signal = token; return pending.promise; } });
    const rejected = read.catch(() => undefined);
    client.setQueryData(["chunks", "workspace-2"], ["unrelated evidence"]);
    await clearWorkspaceQueries(client, "workspace-1");
    expect(signal?.aborted).toBe(true);
    pending.resolve(["late evidence"]); await rejected;
    expect(client.getQueryData(["chunks", "workspace-1"])).toBeUndefined();
    expect(client.getQueryData(["chunks", "workspace-2"])).toEqual(["unrelated evidence"]);
  });
});
