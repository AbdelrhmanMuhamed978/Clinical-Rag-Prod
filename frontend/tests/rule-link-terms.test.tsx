import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ComponentProps, MouseEvent } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { RuleLinkTerms } from "@/components/workflow/rule-link-terms";
import { GuardedLink, NavigationGuardProvider } from "@/components/navigation-guard";
import { ApiError } from "@/lib/api";
import type { Rule } from "@/lib/types";
import { catalogSnapshot, deferred, syntheticRule, unloadIsBlocked } from "./rules-fixtures";

const transport = vi.hoisted(() => ({ get: vi.fn(), patch: vi.fn(), post: vi.fn(), navigate: vi.fn() }));
vi.mock("@/lib/api", () => ({ ...transport, ApiError: class extends Error { constructor(public status: number, message: string) { super(message); } } }));
vi.mock("next/link", () => ({ default: ({ href, onNavigate, ...props }: ComponentProps<typeof GuardedLink>) => <a {...props}
  href={typeof href === "string" ? href : href.pathname ?? undefined} onClick={(event: MouseEvent<HTMLAnchorElement>) => {
    event.preventDefault(); let denied = false; onNavigate?.({ preventDefault: () => { denied = true; } }); if (!denied) transport.navigate(href);
  }} /> }));
type Props = Parameters<typeof RuleLinkTerms>[0];
const key = ["rules", "workspace-1", "user-1"];
const clients: QueryClient[] = [];
let rules: Rule[];
function mount(overrides: Partial<Props> = {}) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Infinity }, mutations: { retry: false } } }); clients.push(client);
  let props: Props = { workspaceId: "workspace-1", userId: "user-1", role: "owner", hidden: false, ...overrides };
  const tree = () => <QueryClientProvider client={client}><NavigationGuardProvider><RuleLinkTerms {...props} /></NavigationGuardProvider></QueryClientProvider>;
  const view = render(tree());
  return { client, user: userEvent.setup(), rerender: (next: Partial<Props>) => { props = { ...props, ...next }; view.rerender(tree()); } };
}
const terms = () => screen.getByLabelText("Link terms (regular expression)") as HTMLInputElement;
const change = (value: string) => fireEvent.change(terms(), { target: { value } });
beforeEach(() => {
  rules = [syntheticRule()]; Object.values(transport).forEach(mock => mock.mockReset());
  transport.get.mockImplementation(async (path: string) => path.endsWith("/drugs") ? catalogSnapshot() : [...rules]);
  transport.patch.mockImplementation(async (_path, body: { linkTerms: string }) => { rules = [{ ...rules[0], linkTerms: body.linkTerms, status: "draft", enabled: false, revision: rules[0].revision + 1 }]; return rules[0]; });
  transport.post.mockImplementation(async () => { rules = [{ ...rules[0], status: "approved", revision: rules[0].revision + 1, reviewedBy: "clinician", reviewedAt: "2026-11-15T00:04:00Z", reviewedCatalogRevision: 2 }]; return rules[0]; });
});
afterEach(() => clients.splice(0).forEach(client => client.clear()));

describe("Workflow lifecycle-compatible rule link terms", () => {
  it.each(["owner", "editor", "reviewer", "viewer", undefined])("separates edit and review capabilities for %s", async role => {
    mount({ role }); await screen.findByLabelText("Link terms (regular expression)");
    expect(terms().readOnly).toBe(!["owner", "editor"].includes(role ?? ""));
    expect(screen.getByRole("button", { name: "Approve rule" }).hasAttribute("disabled")).toBe(!["owner", "reviewer"].includes(role ?? ""));
  });
  it("preserves even invalid legacy regex on an exact no-op without forcing re-review", async () => {
    rules = [syntheticRule({ linkTerms: "[z-a]", status: "approved", reviewedBy: "clinician", reviewedAt: "2026-11-15T00:02:00Z", reviewedCatalogRevision: 2 })];
    const { user } = mount(); await screen.findByLabelText("Link terms (regular expression)"); await user.click(screen.getByRole("button", { name: "Save link terms" }));
    expect(transport.patch).not.toHaveBeenCalled(); expect(screen.getByText(/No link-term changes to save/)).toBeTruthy();
  });
  it("rejects nonportable regex on submit, routes focus, and leaves service compilation authoritative", async () => {
    const { user } = mount(); await screen.findByLabelText("Link terms (regular expression)"); change("a++");
    await user.click(screen.getByRole("button", { name: "Save link terms" })); const alert = screen.getByRole("alert");
    expect(document.activeElement).toBe(alert); expect(terms().getAttribute("aria-invalid")).toBe("true"); expect(transport.patch).not.toHaveBeenCalled();
    transport.patch.mockRejectedValueOnce(new ApiError(400, JSON.stringify({ errors: ["Link terms are not a valid regular expression."] })));
    change("[z-a]"); await user.click(screen.getByRole("button", { name: "Save link terms" }));
    await screen.findByText("Link terms are not a valid regular expression."); expect(transport.patch).toHaveBeenCalledWith("/api/workspaces/workspace-1/rules/rule-1", { linkTerms: "[z-a]", expectedRevision: 3 });
  });
  it("retains typing across hidden steps and late saves and sends the rebased revision next", async () => {
    const pending = deferred<Rule>(); transport.patch.mockReturnValueOnce(pending.promise);
    const view = mount(); await screen.findByLabelText("Link terms (regular expression)"); change("renal|kidney|first");
    await view.user.click(screen.getByRole("button", { name: "Save link terms" })); await waitFor(() => expect(transport.patch).toHaveBeenCalledOnce());
    change("renal|kidney|later"); view.rerender({ hidden: true }); expect(unloadIsBlocked()).toBe(true);
    rules = [syntheticRule({ linkTerms: "renal|kidney|first", revision: 4 })]; await act(async () => { pending.resolve(rules[0]); }); view.rerender({ hidden: false });
    expect(terms().value).toBe("renal|kidney|later"); await view.user.click(screen.getByRole("button", { name: "Save link terms" }));
    await waitFor(() => expect(transport.patch).toHaveBeenLastCalledWith("/api/workspaces/workspace-1/rules/rule-1", { linkTerms: "renal|kidney|later", expectedRevision: 4 }));
  });
  it("freezes dirty remote revisions and only reloads after explicit discard", async () => {
    const { client, user } = mount(); await screen.findByLabelText("Link terms (regular expression)"); change("local|terms");
    rules = [syntheticRule({ linkTerms: "remote|terms", revision: 4 })]; await act(async () => { client.setQueryData(key, [...rules]); });
    await screen.findByText(/This rule changed remotely/); expect(terms().value).toBe("local|terms");
    expect(screen.getByRole("button", { name: "Save link terms" }).hasAttribute("disabled")).toBe(true);
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(false); await user.click(screen.getByRole("button", { name: "Reload saved rule" })); expect(terms().value).toBe("local|terms");
    confirm.mockReturnValue(true); await user.click(screen.getByRole("button", { name: "Reload saved rule" })); expect(terms().value).toBe("remote|terms"); expect(unloadIsBlocked()).toBe(false);
  });
  it("rebases clean snapshots forward and never regresses to a lower revision", async () => {
    const { client } = mount(); await screen.findByLabelText("Link terms (regular expression)");
    await act(async () => { client.setQueryData(key, [syntheticRule({ linkTerms: "newer", revision: 4 })]); }); await screen.findByDisplayValue("newer");
    await act(async () => { client.setQueryData(key, [syntheticRule({ linkTerms: "older", revision: 3 })]); }); await screen.findByText(/This rule changed remotely/); expect(terms().value).toBe("newer");
  });
  it("confirms only the captured saved revision and never enables approval implicitly", async () => {
    const { client, user } = mount({ role: "reviewer" }); await screen.findByLabelText("Link terms (regular expression)");
    await user.click(screen.getByRole("button", { name: "Approve rule" }));
    rules = [syntheticRule({ revision: 4 })]; await act(async () => { client.setQueryData(key, [...rules]); });
    await user.click(screen.getByRole("button", { name: "Approve saved definition" }));
    await waitFor(() => expect(transport.post).toHaveBeenCalledWith("/api/workspaces/workspace-1/rules/rule-1/approve", { expectedRevision: 3 }));
    expect(rules[0].enabled).toBe(false); expect(screen.getByText(/does not mark a workflow step complete/)).toBeTruthy();
  });
  it("guards the full Rules link while hidden edits remain dirty", async () => {
    const { user } = mount(); await screen.findByLabelText("Link terms (regular expression)"); change("dirty|terms");
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(false); await user.click(screen.getByRole("link", { name: /Open the full Rules editor/ }));
    expect(transport.navigate).not.toHaveBeenCalled(); confirm.mockReturnValue(true); await user.click(screen.getByRole("link", { name: /Open the full Rules editor/ }));
    expect(transport.navigate).toHaveBeenCalledWith("/w/workspace-1/rules");
  });
  it("blocks approval when saved recognition fails and hides unrelated response details", async () => {
    transport.get.mockImplementation(async (path: string) => { if (path.endsWith("/drugs")) throw new Error("PRIVATE_CATALOG_ERROR"); return [...rules]; });
    mount(); await screen.findByRole("alert"); expect(screen.getByRole("button", { name: "Approve rule" }).hasAttribute("disabled")).toBe(true);
    expect(screen.queryByText("PRIVATE_CATALOG_ERROR")).toBeNull();
  });
});
