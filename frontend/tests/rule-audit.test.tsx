import { act, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { RuleAudit } from "@/components/rules/rule-audit";
import type { RuleAuditEntry, RuleAuditPage } from "@/lib/types";
import { deferred } from "./rules-fixtures";

const transport = vi.hoisted(() => ({ get: vi.fn() }));
vi.mock("@/lib/api", () => transport);
type Props = Parameters<typeof RuleAudit>[0];
const clients: QueryClient[] = [];
const entry = (overrides: Partial<RuleAuditEntry> = {}): RuleAuditEntry => ({ id: "audit-1", workspaceId: "workspace-1", ruleId: "deleted-rule", ruleKey: "deleted-key",
  action: "rule_deleted", actorId: "clinician", beforeJson: '{"status":"retired"}', afterJson: null, createdAt: "2026-11-15T00:00:00Z", ...overrides });
function mount(overrides: Partial<Props> = {}) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Infinity } } }); clients.push(client);
  let props: Props = { workspaceId: "workspace-1", userId: "user-1", ruleId: null, active: true, onWorkspaceHistory: vi.fn(), ...overrides };
  const tree = () => <QueryClientProvider client={client}><RuleAudit {...props} /></QueryClientProvider>;
  const view = render(tree());
  return { client, user: userEvent.setup(), rerender: (next: Partial<Props>) => { props = { ...props, ...next }; view.rerender(tree()); } };
}
beforeEach(() => {
  transport.get.mockReset().mockImplementation(async (path: string) => {
    const skip = Number(new URL(path, "https://example.org").searchParams.get("skip"));
    return { entries: [entry({ id: `audit-${skip}`, ruleKey: `entry-${skip}` })], hasMore: skip === 0, nextSkip: skip === 0 ? 25 : null };
  });
});
afterEach(() => clients.splice(0).forEach(client => client.clear()));

describe("scoped append-only rule audit viewer", () => {
  it("does not fetch hidden history and requests bounded pages only when active", async () => {
    const view = mount({ active: false }); expect(transport.get).not.toHaveBeenCalled(); view.rerender({ active: true });
    await screen.findByText("rule deleted — entry-0");
    expect(transport.get.mock.calls[0][0]).toBe("/api/workspaces/workspace-1/rules/audit?skip=0&take=25");
    expect(transport.get.mock.calls[0][1].signal).toBeInstanceOf(AbortSignal);
    expect(view.client.getQueryCache().getAll()[0].queryKey).toEqual(["rules", "workspace-1", "user-1", "audit", "workspace", 0]);
  });
  it("uses server hasMore/nextSkip and can navigate back without inventing audit pages", async () => {
    const { user } = mount(); await screen.findByText("rule deleted — entry-0");
    expect(screen.getByRole("button", { name: "Newer entries" }).hasAttribute("disabled")).toBe(true);
    await user.click(screen.getByRole("button", { name: "Older entries" })); await screen.findByText("rule deleted — entry-25");
    expect(transport.get.mock.calls[1][0]).toContain("skip=25&take=25"); expect(screen.getByRole("button", { name: "Older entries" }).hasAttribute("disabled")).toBe(true);
    await user.click(screen.getByRole("button", { name: "Newer entries" })); await screen.findByText("rule deleted — entry-0");
  });
  it("shows retained deleted-rule evidence as inert text, never HTML or case previews", async () => {
    transport.get.mockResolvedValueOnce({ entries: [entry({ beforeJson: '<script>PRIVATE_INERT_EVIDENCE</script>' })], hasMore: false, nextSkip: null });
    const { user } = mount({ ruleId: "deleted-rule" }); await screen.findByText("rule deleted — deleted-key");
    expect(transport.get.mock.calls[0][0]).toContain("/rules/deleted-rule/audit"); await user.click(screen.getByText("Before snapshot"));
    expect(screen.getByText("<script>PRIVATE_INERT_EVIDENCE</script>")).toBeTruthy(); expect(document.querySelector("script")).toBeNull();
    expect(screen.getByText("No snapshot (creation or deletion).")).toBeTruthy(); expect(screen.queryByRole("textbox")).toBeNull();
  });
  it.each([
    { replacement: { ruleId: "different-rule" }, path: "/api/workspaces/workspace-1/rules/different-rule/audit?skip=0&take=25" },
    { replacement: { ruleId: null }, path: "/api/workspaces/workspace-1/rules/audit?skip=0&take=25" },
    { replacement: { workspaceId: "workspace-2" }, path: "/api/workspaces/workspace-2/rules/deleted-rule/audit?skip=0&take=25" },
    { replacement: { userId: "user-2" }, path: "/api/workspaces/workspace-1/rules/deleted-rule/audit?skip=0&take=25" },
  ])("resets pagination before fetching a changed scope: $replacement", async ({ replacement, path }) => {
    const view = mount({ ruleId: "deleted-rule" }); await screen.findByText("rule deleted — entry-0");
    await view.user.click(screen.getByRole("button", { name: "Older entries" })); await screen.findByText("rule deleted — entry-25");
    transport.get.mockClear(); view.rerender(replacement);
    await waitFor(() => expect(transport.get.mock.calls.at(-1)?.[0]).toBe(path));
    expect(transport.get.mock.calls.every(([requestedPath]) => requestedPath === path)).toBe(true);
    expect(screen.getByText("Page 1")).toBeTruthy();
  });
  it("does not show late old-scope history after replacement", async () => {
    const old = deferred<RuleAuditPage>(); transport.get.mockReturnValueOnce(old.promise);
    const view = mount(); await waitFor(() => expect(transport.get).toHaveBeenCalledOnce());
    view.rerender({ workspaceId: "workspace-2", userId: "user-2" }); await screen.findByText("rule deleted — entry-0");
    await act(async () => { old.resolve({ entries: [entry({ ruleKey: "OLD_SCOPE_EVIDENCE" })], hasMore: false, nextSkip: null }); });
    expect(screen.queryByText(/OLD_SCOPE_EVIDENCE/)).toBeNull();
  });
  it("shows a safe load failure, disables forward paging, and retries explicitly", async () => {
    transport.get.mockRejectedValueOnce(new Error("PRIVATE_AUDIT_ERROR")); const { user } = mount();
    const alert = await screen.findByRole("alert"); expect(alert.textContent).toBe("Could not load audit history.");
    expect(screen.getByRole("button", { name: "Older entries" }).hasAttribute("disabled")).toBe(true);
    await user.click(screen.getByRole("button", { name: "Retry history" })); await screen.findByText("rule deleted — entry-0");
    expect(transport.get).toHaveBeenCalledTimes(2);
  });
});
