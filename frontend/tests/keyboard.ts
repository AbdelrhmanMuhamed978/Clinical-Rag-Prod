import type userEvent from "@testing-library/user-event";

type User = ReturnType<typeof userEvent.setup>;

/** Tab from the current focus until a target is reached, like a keyboard-only
 * walkthrough. Throws with the requested control when the page is unreachable. */
export async function tabTo(user: User, matches: (element: HTMLElement) => boolean, label: string, limit = 120) {
  for (let index = 0; index < limit; index++) {
    const active = document.activeElement;
    if (active instanceof HTMLElement && active !== document.body && matches(active)) return active;
    await user.tab();
  }
  throw new Error(`Keyboard walkthrough could not tab to ${label}.`);
}

export function control(name: string, tag?: string) {
  return (element: HTMLElement) =>
    (!tag || element.tagName === tag) && element.textContent?.trim() === name;
}
