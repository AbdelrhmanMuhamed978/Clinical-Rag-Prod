import { cleanup } from "@testing-library/react";
import { afterEach, vi } from "vitest";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

Object.defineProperty(window, "matchMedia", {
  writable: true,
  value: vi.fn((query: string) => ({
    matches: false, media: query, onchange: null,
    addListener: vi.fn(), removeListener: vi.fn(),
    addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
  })),
});

// jsdom lacks native dialog behavior. This test-only shim exercises open/close callbacks;
// actual top-layer, Esc, scroll containment and focus restoration are checked in browser QA.
const invokers = new WeakMap<HTMLDialogElement, Element | null>();
Object.defineProperty(HTMLDialogElement.prototype, "showModal", {
  configurable: true,
  value: function (this: HTMLDialogElement) {
    invokers.set(this, document.activeElement);
    this.setAttribute("open", "");
    this.querySelector<HTMLElement>("[autofocus], button, input")?.focus();
  },
});
Object.defineProperty(HTMLDialogElement.prototype, "close", {
  configurable: true,
  value: function (this: HTMLDialogElement, value?: string) {
    if (!this.open) return;
    if (value !== undefined) this.returnValue = value;
    this.removeAttribute("open");
    this.dispatchEvent(new Event("close"));
    (invokers.get(this) as HTMLElement | null)?.focus();
  },
});
