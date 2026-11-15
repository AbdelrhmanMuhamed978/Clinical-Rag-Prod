import { act, fireEvent, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ComponentProps, MouseEvent, ReactNode } from "react";
import { useState } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import AppLayout from "@/app/(app)/layout";
import { GuardedLink, NavigationGuardProvider, useNavigationGuard, useUnsavedChanges } from "@/components/navigation-guard";

const navigation = vi.hoisted(() => ({ navigate: vi.fn(), push: vi.fn(), signOut: vi.fn() }));
vi.mock("next/navigation", () => ({ usePathname: () => "/w/workspace-1/workflow", useRouter: () => ({ push: navigation.push }) }));
vi.mock("@/providers/AuthProvider", () => ({ useAuth: () => ({ user: { uid: "user-1", email: "synthetic@example.com" }, loading: false, emailVerified: true }) }));
vi.mock("@/lib/firebase", () => ({ auth: {} }));
vi.mock("firebase/auth", () => ({ signOut: navigation.signOut }));
vi.mock("next/link", () => ({
  default: ({ href, onNavigate, onClick, ...props }: ComponentProps<typeof GuardedLink>) => <a {...props}
    href={typeof href === "string" ? href : href.pathname ?? undefined} onClick={(event: MouseEvent<HTMLAnchorElement>) => {
      onClick?.(event);
      // Model Next's onNavigate contract, not every click (new-tab/download clicks leave this editor intact).
      if (event.defaultPrevented) return;
      if (event.ctrlKey || event.metaKey || props.target === "_blank" || props.download) { event.preventDefault(); return; }
      event.preventDefault(); let blocked = false;
      onNavigate?.({ preventDefault: () => { blocked = true; } });
      if (!blocked) navigation.navigate(href);
    }} />,
}));

function DirtyForm({ initiallyDirty = false }: { initiallyDirty?: boolean }) {
  const [dirty, setDirty] = useState(initiallyDirty);
  useUnsavedChanges(dirty);
  return <div>
    <button type="button" onClick={() => setDirty(true)}>Edit form</button>
    <button type="button" onClick={() => setDirty(false)}>Save form</button>
  </div>;
}
function CheckLeave() {
  const { confirmLeave } = useNavigationGuard();
  return <button type="button" onClick={() => { if (confirmLeave()) navigation.navigate("leave"); }}>Leave</button>;
}
const wrap = (children: ReactNode) => <NavigationGuardProvider>{children}</NavigationGuardProvider>;
const unload = () => { const event = new Event("beforeunload", { cancelable: true }); window.dispatchEvent(event); return event; };
const navigateEvent = (type: string, cancelable: boolean, url = "http://localhost/w/workspace-1/documents") => {
  const event = new Event("navigate", { cancelable });
  Object.assign(event, { navigationType: type, destination: { url } }); return event;
};

beforeEach(() => {
  window.history.replaceState(null, "", "/w/workspace-1/workflow");
  navigation.signOut.mockResolvedValue(undefined);
});
afterEach(() => { Reflect.deleteProperty(window, "navigation"); });

describe("Unsaved navigation guards", () => {
  it("registers unload protection only while dirty and removes it after save or unmount", async () => {
    const user = userEvent.setup(); const view = render(wrap(<DirtyForm />));
    expect(unload().defaultPrevented).toBe(false);
    await user.click(screen.getByRole("button", { name: "Edit form" })); expect(unload().defaultPrevented).toBe(true);
    await user.click(screen.getByRole("button", { name: "Save form" })); expect(unload().defaultPrevented).toBe(false);
    await user.click(screen.getByRole("button", { name: "Edit form" })); view.unmount(); expect(unload().defaultPrevented).toBe(false);
  });

  it("keeps independent dirty forms registered until each is saved or unmounted", async () => {
    const user = userEvent.setup(); const confirm = vi.spyOn(window, "confirm").mockReturnValue(false);
    const view = render(wrap(<><DirtyForm initiallyDirty /><DirtyForm initiallyDirty /><CheckLeave /></>));
    await user.click(screen.getAllByRole("button", { name: "Save form" })[0]);
    await user.click(screen.getByRole("button", { name: "Leave" })); expect(confirm).toHaveBeenCalledOnce(); expect(navigation.navigate).not.toHaveBeenCalled();
    view.rerender(wrap(<CheckLeave />));
    await user.click(screen.getByRole("button", { name: "Leave" })); expect(confirm).toHaveBeenCalledOnce(); expect(navigation.navigate).toHaveBeenCalledWith("leave");
  });

  it("cancels guarded app links on refusal and follows them after explicit confirmation", async () => {
    const user = userEvent.setup(); const confirm = vi.spyOn(window, "confirm").mockReturnValue(false); const onNavigate = vi.fn();
    render(wrap(<><DirtyForm initiallyDirty /><GuardedLink href="/w/workspace-1/documents" onNavigate={onNavigate}>Documents</GuardedLink></>));
    await user.click(screen.getByRole("link", { name: "Documents" })); expect(navigation.navigate).not.toHaveBeenCalled(); expect(onNavigate).not.toHaveBeenCalled();
    confirm.mockReturnValue(true); await user.click(screen.getByRole("link", { name: "Documents" }));
    expect(navigation.navigate).toHaveBeenCalledWith("/w/workspace-1/documents"); expect(onNavigate).toHaveBeenCalledOnce();
  });

  it("does not warn for a same-page anchor or clicks opening a different tab", async () => {
    const user = userEvent.setup(); const confirm = vi.spyOn(window, "confirm");
    render(wrap(<><DirtyForm initiallyDirty />
      <GuardedLink href="#settings">Settings anchor</GuardedLink>
      <GuardedLink href="/w/workspace-1/documents" target="_blank">New tab</GuardedLink>
      <GuardedLink href="/w/workspace-1/documents">Modified link</GuardedLink>
    </>));
    await user.click(screen.getByRole("link", { name: "Settings anchor" }));
    fireEvent.click(screen.getByRole("link", { name: "New tab" }));
    fireEvent.click(screen.getByRole("link", { name: "Modified link" }), { ctrlKey: true });
    expect(confirm).not.toHaveBeenCalled(); expect(navigation.navigate).toHaveBeenCalledOnce();
  });

  it("cancels cancelable history traversal before the editor leaves without patching history", async () => {
    const nativeNavigation = new EventTarget(); Object.defineProperty(window, "navigation", { configurable: true, value: nativeNavigation });
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(false); const push = vi.spyOn(window.history, "pushState");
    const replace = vi.spyOn(window.history, "replaceState");
    const view = render(wrap(<DirtyForm initiallyDirty />));
    const denied = navigateEvent("traverse", true); act(() => { nativeNavigation.dispatchEvent(denied); });
    expect(denied.defaultPrevented).toBe(true); expect(confirm).toHaveBeenCalledOnce();
    expect(push).not.toHaveBeenCalled(); expect(replace).not.toHaveBeenCalled();
    confirm.mockReturnValue(true);
    const allowed = navigateEvent("traverse", true); act(() => { nativeNavigation.dispatchEvent(allowed); }); expect(allowed.defaultPrevented).toBe(false);
    view.unmount(); const after = navigateEvent("traverse", true); nativeNavigation.dispatchEvent(after); expect(confirm).toHaveBeenCalledTimes(2);
  });

  it("ignores noncancelable or non-traversal events and keeps the unload fallback when the API is absent", () => {
    const nativeNavigation = new EventTarget(); Object.defineProperty(window, "navigation", { configurable: true, value: nativeNavigation });
    const confirm = vi.spyOn(window, "confirm"); const view = render(wrap(<DirtyForm initiallyDirty />));
    nativeNavigation.dispatchEvent(navigateEvent("traverse", false)); nativeNavigation.dispatchEvent(navigateEvent("push", true));
    expect(confirm).not.toHaveBeenCalled(); expect(unload().defaultPrevented).toBe(true);
    view.unmount(); Reflect.deleteProperty(window, "navigation"); render(wrap(<DirtyForm initiallyDirty />));
    expect(unload().defaultPrevented).toBe(true);
  });
});

describe("App layout integration", () => {
  it("guards sidebar, workspace and breadcrumb links but not the current-page breadcrumb", async () => {
    const user = userEvent.setup(); const confirm = vi.spyOn(window, "confirm").mockReturnValue(false);
    render(<AppLayout><DirtyForm initiallyDirty /></AppLayout>);
    for (const name of ["Workspaces", "Documents", "Workspace"])
      await user.click(screen.getByRole("link", { name }));
    expect(confirm).toHaveBeenCalledTimes(3); expect(navigation.navigate).not.toHaveBeenCalled();
    await user.click(screen.getAllByRole("link", { name: "Workflow" })[1]);
    expect(confirm).toHaveBeenCalledTimes(3); expect(navigation.navigate).toHaveBeenCalledWith("/w/workspace-1/workflow");
  });

  it("does not sign out dirty editors until confirmation and navigates only after successful sign-out", async () => {
    const user = userEvent.setup(); const confirm = vi.spyOn(window, "confirm").mockReturnValue(false);
    render(<AppLayout><DirtyForm initiallyDirty /></AppLayout>);
    await user.click(screen.getByRole("button", { name: "Sign out" })); expect(navigation.signOut).not.toHaveBeenCalled(); expect(navigation.push).not.toHaveBeenCalled();
    confirm.mockReturnValue(true); await user.click(screen.getByRole("button", { name: "Sign out" }));
    expect(navigation.signOut).toHaveBeenCalledOnce(); expect(navigation.push).toHaveBeenCalledWith("/login");
  });

  it("shows a safe sign-out error rather than logging credentials or navigating on failure", async () => {
    navigation.signOut.mockRejectedValueOnce(new Error("PRIVATE_AUTH_FAILURE")); const user = userEvent.setup(); const log = vi.spyOn(console, "error");
    render(<AppLayout><DirtyForm /></AppLayout>); await user.click(screen.getByRole("button", { name: "Sign out" }));
    expect((await screen.findByRole("alert")).textContent).toBe("Could not sign out. Try again.");
    expect(navigation.push).not.toHaveBeenCalled(); expect(log).not.toHaveBeenCalled(); expect(screen.queryByText("PRIVATE_AUTH_FAILURE")).toBeNull();
  });
});
