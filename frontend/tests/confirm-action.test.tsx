import { act, fireEvent, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { ConfirmAction } from "@/components/rules/confirm-action";

const props = { label: "Delete synthetic row", title: "Delete this saved synthetic row?", description: "Only the captured revision is affected.", confirmLabel: "Confirm deletion", disabled: false };
describe("native snapshot-bound confirmations", () => {
  it("opens a named native modal with safe initial focus and no redundant custom dialog semantics", async () => {
    const user = userEvent.setup(); render(<ConfirmAction {...props} onConfirm={vi.fn()} />);
    await user.click(screen.getByRole("button", { name: props.label }));
    const dialog = screen.getByRole("dialog") as HTMLDialogElement;
    expect(dialog.tagName).toBe("DIALOG"); expect(dialog.getAttribute("role")).toBeNull(); expect(dialog.getAttribute("aria-modal")).toBeNull();
    expect(dialog.getAttribute("aria-labelledby")).toBeTruthy(); expect(dialog.getAttribute("aria-describedby")).toBeTruthy();
    expect(document.activeElement).toBe(screen.getByRole("button", { name: "Cancel" }));
    const destructive = screen.getByRole("button", { name: props.confirmLabel });
    // Tailwind's outline-none also sets --tw-outline-style: none. Width and
    // color utilities alone cannot restore a visible, forced-colors-safe ring.
    for (const style of ["text-red-800", "dark:text-red-200", "focus-visible:outline-solid", "focus-visible:outline-2",
      "focus-visible:outline-offset-2", "focus-visible:outline-red-800", "dark:focus-visible:outline-red-200"])
      expect(destructive.classList.contains(style)).toBe(true);
  });
  it("cancels without executing and restores the invoker's focus", async () => {
    const action = vi.fn(); const user = userEvent.setup(); render(<ConfirmAction {...props} onConfirm={action} />);
    const invoker = screen.getByRole("button", { name: props.label }); await user.click(invoker); await user.click(screen.getByRole("button", { name: "Cancel" }));
    expect(action).not.toHaveBeenCalled(); expect(screen.queryByRole("dialog")).toBeNull(); expect(document.activeElement).toBe(invoker);
  });
  it("resets returnValue so later Esc-style dismissal cannot reuse a destructive choice", async () => {
    const action = vi.fn(); const user = userEvent.setup(); render(<ConfirmAction {...props} onConfirm={action} />);
    await user.click(screen.getByRole("button", { name: props.label })); await user.click(screen.getByRole("button", { name: props.confirmLabel }));
    expect(action).toHaveBeenCalledOnce(); await user.click(screen.getByRole("button", { name: props.label }));
    const dialog = screen.getByRole("dialog") as HTMLDialogElement; expect(dialog.returnValue).toBe("");
    fireEvent(dialog, new Event("cancel", { cancelable: true })); act(() => { dialog.close(); });
    expect(action).toHaveBeenCalledOnce(); expect(dialog.returnValue).toBe("");
  });
  it("executes the callback captured on opening, not one from changed props", async () => {
    const oldAction = vi.fn(); const newAction = vi.fn(); const user = userEvent.setup();
    const view = render(<ConfirmAction {...props} onConfirm={oldAction} />); await user.click(screen.getByRole("button", { name: props.label }));
    view.rerender(<ConfirmAction {...props} title="New revision was loaded" disabled onConfirm={newAction} />);
    expect(screen.getByRole("dialog").textContent).toContain(props.title); await user.click(screen.getByRole("button", { name: props.confirmLabel }));
    expect(oldAction).toHaveBeenCalledOnce(); expect(newAction).not.toHaveBeenCalled();
  });
  it("does not open when disabled or execute a pending choice after unmount", async () => {
    const action = vi.fn(); const user = userEvent.setup(); const view = render(<ConfirmAction {...props} disabled onConfirm={action} />);
    await user.click(screen.getByRole("button", { name: props.label })); expect(screen.queryByRole("dialog")).toBeNull();
    view.rerender(<ConfirmAction {...props} onConfirm={action} />); await user.click(screen.getByRole("button", { name: props.label }));
    view.unmount(); expect(action).not.toHaveBeenCalled();
  });
});
