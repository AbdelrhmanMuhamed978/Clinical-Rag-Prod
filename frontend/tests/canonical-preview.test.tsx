import { act, fireEvent, render, screen } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { CanonicalPreview } from "@/components/rules/canonical-preview";
import { readRuleForm, ruleDefinition } from "@/lib/rules";
import type { RulePreview } from "@/lib/types";
import { deferred, previewResult, syntheticRule, vocabulary } from "./rules-fixtures";

const transport = vi.hoisted(() => ({ api: vi.fn() }));
vi.mock("@/lib/api", () => transport);
const definition = ruleDefinition(readRuleForm(syntheticRule()).form);
const defaults = { workspaceId: "workspace-1", userId: "user-1", definition, vocabulary, catalogRevision: 2, active: true };
type Props = Parameters<typeof CanonicalPreview>[0];
const clients: QueryClient[] = [];
function mount(overrides: Partial<Props> = {}) {
  const client = new QueryClient(); clients.push(client);
  let props = { ...defaults, ...overrides };
  const tree = () => <QueryClientProvider client={client}><CanonicalPreview {...props} /></QueryClientProvider>;
  const view = render(tree());
  return { client, unmount: view.unmount, rerender: (next: Partial<Props>) => { props = { ...props, ...next }; view.rerender(tree()); } };
}
const text = () => screen.getByLabelText("Case text for preview") as HTMLTextAreaElement;
const sample = () => screen.getByLabelText("Regex sample text") as HTMLTextAreaElement;
const changeText = (value: string) => fireEvent.change(text(), { target: { value } });
const run = () => fireEvent.click(screen.getByRole("button", { name: "Run canonical preview" }));
const live = () => fireEvent.click(screen.getByLabelText("Live preview (after a pause in typing)"));
async function tick(ms = 400) { await act(async () => { await vi.advanceTimersByTimeAsync(ms); }); }
const currentResult = () => screen.queryByTestId("canonical-rule-result");

beforeEach(() => { vi.useFakeTimers(); transport.api.mockReset().mockResolvedValue(previewResult()); });
afterEach(() => { clients.splice(0).forEach(client => client.clear()); vi.useRealTimers(); });

describe("private canonical preview request binding", () => {
  it("never transmits text or sample edits until the explicit manual run", async () => {
    mount(); changeText("PRIVATE_SYNTHETIC_CASE creat 2"); fireEvent.change(sample(), { target: { value: "PRIVATE_SYNTHETIC_SAMPLE renal" } });
    await tick(1000); expect(transport.api).not.toHaveBeenCalled();
    run(); await tick();
    const [path, request] = transport.api.mock.calls[0];
    expect(path).toBe("/api/workspaces/workspace-1/rules/preview");
    expect(path).not.toContain("PRIVATE");
    expect(request.method).toBe("POST"); expect(request.cache).toBe("no-store"); expect(request.signal).toBeInstanceOf(AbortSignal);
    expect(JSON.parse(request.body)).toEqual({ rule: definition, text: "PRIVATE_SYNTHETIC_CASE creat 2", regexSample: "PRIVATE_SYNTHETIC_SAMPLE renal", expectedCatalogRevision: 2 });
    expect(currentResult()).not.toBeNull();
  });
  it("hides a manual result after edits and does not resend, including edit-and-undo", async () => {
    mount(); changeText("creat 2"); run(); await tick();
    changeText("creat 3"); expect(currentResult()).toBeNull(); await tick(1000);
    changeText("creat 2"); await tick(1000);
    expect(transport.api).toHaveBeenCalledTimes(1); expect(currentResult()).toBeNull();
    expect(screen.getByText(/manual mode does not send while you type/)).toBeTruthy();
    run(); await tick(); expect(transport.api).toHaveBeenCalledTimes(2); expect(currentResult()).not.toBeNull();
  });
  it("invalidates manual binding when definition, sample or saved catalog changes", async () => {
    const view = mount(); changeText("creat 2"); run(); await tick();
    view.rerender({ definition: { ...definition, message: "Changed definition" } }); await tick(500);
    expect(currentResult()).toBeNull(); expect(transport.api).toHaveBeenCalledTimes(1);
    run(); await tick();
    fireEvent.change(sample(), { target: { value: "changed sample" } }); await tick();
    view.rerender({ catalogRevision: 3 }); await tick();
    expect(currentResult()).toBeNull(); expect(transport.api).toHaveBeenCalledTimes(2);
    transport.api.mockResolvedValueOnce(previewResult({ catalog_revision: 3 })); run(); await tick();
    expect(JSON.parse(transport.api.mock.calls[2][1].body).expectedCatalogRevision).toBe(3);
  });
  it("debounces live input, aborts old requests, hides old results and ignores late responses", async () => {
    const old = deferred<RulePreview>(); transport.api.mockReturnValueOnce(old.promise);
    mount(); live(); changeText("creat 2"); await tick(300); changeText("creat 3"); await tick(399);
    expect(transport.api).not.toHaveBeenCalled(); await tick(1);
    const oldSignal = transport.api.mock.calls[0][1].signal as AbortSignal;
    changeText("creat 4"); expect(oldSignal.aborted).toBe(true); await tick();
    expect(transport.api).toHaveBeenCalledTimes(2); expect(currentResult()).not.toBeNull();
    await act(async () => { old.resolve(previewResult({ would_fire: false, reasons: ["OLD_RESPONSE"] })); });
    expect(screen.queryByText("OLD_RESPONSE")).toBeNull(); expect(screen.getByText("Would fire (simulation only)")).toBeTruthy();
  });
  it("stops live transmission immediately when live mode is turned off", async () => {
    const pending = deferred<RulePreview>(); transport.api.mockReturnValueOnce(pending.promise);
    mount(); live(); await tick(); const signal = transport.api.mock.calls[0][1].signal as AbortSignal;
    live(); expect(signal.aborted).toBe(true); changeText("creat 5"); await tick(1000);
    expect(transport.api).toHaveBeenCalledTimes(1); expect(currentResult()).toBeNull();
    run(); await tick(); expect(transport.api).toHaveBeenCalledTimes(2);
  });
  it("never accepts a result from a different recognition revision", async () => {
    transport.api.mockResolvedValueOnce(previewResult({ catalog_revision: 1 }));
    mount(); run(); await tick();
    expect(currentResult()).toBeNull(); expect(screen.getByRole("alert").textContent).toContain("Recognition changed");
  });
  it("does not retransmit a completed manual run when returning to an inactive tab", async () => {
    const view = mount(); changeText("creat 2"); run(); await tick();
    view.rerender({ active: false }); expect(currentResult()).toBeNull(); await tick(1000);
    view.rerender({ active: true }); await tick(1000);
    expect(transport.api).toHaveBeenCalledTimes(1); expect(currentResult()).not.toBeNull();
  });
  it("aborts an in-flight preview while inactive and cannot reveal its late response", async () => {
    const pending = deferred<RulePreview>(); transport.api.mockReturnValueOnce(pending.promise);
    const view = mount(); run(); await tick(); const signal = transport.api.mock.calls[0][1].signal as AbortSignal;
    view.rerender({ active: false }); expect(signal.aborted).toBe(true);
    await act(async () => { pending.resolve(previewResult({ reasons: ["OLD_HIDDEN_RESPONSE"] })); });
    expect(screen.queryByText("OLD_HIDDEN_RESPONSE")).toBeNull();
    view.rerender({ active: true }); await tick(); expect(transport.api).toHaveBeenCalledTimes(2);
  });
  it("clears private inputs, result and live binding without sending an empty replacement", async () => {
    const pending = deferred<RulePreview>(); transport.api.mockReturnValueOnce(pending.promise);
    mount(); changeText("PRIVATE_SYNTHETIC"); fireEvent.change(sample(), { target: { value: "PRIVATE_SAMPLE" } }); live(); await tick();
    const signal = transport.api.mock.calls[0][1].signal as AbortSignal;
    fireEvent.click(screen.getByRole("button", { name: "Clear private preview" }));
    expect(signal.aborted).toBe(true); expect(text().value).toBe(""); expect(sample().value).toBe("");
    expect((screen.getByLabelText("Live preview (after a pause in typing)") as HTMLInputElement).checked).toBe(false);
    await act(async () => { pending.resolve(previewResult({ reasons: ["CLEARED_RESPONSE"] })); }); await tick(1000);
    expect(transport.api).toHaveBeenCalledTimes(1); expect(currentResult()).toBeNull(); expect(screen.queryByText("CLEARED_RESPONSE")).toBeNull();
  });
  it.each([{ workspaceId: "workspace-2" }, { userId: "user-2" }])("clears ownership-bound inputs and ignores old work after %j replacement", async replacement => {
    const pending = deferred<RulePreview>(); transport.api.mockReturnValueOnce(pending.promise);
    const view = mount(); changeText("PRIVATE_OLD_SCOPE"); run(); await tick();
    const signal = transport.api.mock.calls[0][1].signal as AbortSignal;
    view.rerender(replacement); expect(signal.aborted).toBe(true); expect(text().value).toBe("");
    await act(async () => { pending.resolve(previewResult({ reasons: ["OLD_SCOPE_RESPONSE"] })); }); await tick(1000);
    expect(transport.api).toHaveBeenCalledTimes(1); expect(currentResult()).toBeNull();
  });
  it("aborts on unmount and leaves no query-cache, storage or console copy of preview data", async () => {
    const pending = deferred<RulePreview>(); transport.api.mockReturnValueOnce(pending.promise);
    const storage = vi.spyOn(Storage.prototype, "setItem"); const log = vi.spyOn(console, "log"); const error = vi.spyOn(console, "error");
    const view = mount(); changeText("PRIVATE_UNMOUNT_CASE"); run(); await tick();
    const signal = transport.api.mock.calls[0][1].signal as AbortSignal; view.unmount(); expect(signal.aborted).toBe(true);
    await act(async () => { pending.reject(new Error("PRIVATE_TRANSPORT_ERROR")); });
    expect(view.client.getQueryCache().getAll()).toEqual([]); expect(view.client.getMutationCache().getAll()).toEqual([]);
    expect(storage).not.toHaveBeenCalled(); expect(log).not.toHaveBeenCalled(); expect(error).not.toHaveBeenCalled();
  });
  it("shows only a safe failure message and does not automatically retry rejected manual work", async () => {
    transport.api.mockRejectedValueOnce(new Error("PRIVATE_CASE_AND_REGEX_ERROR"));
    mount(); run(); await tick();
    expect(screen.getByRole("alert").textContent).toContain("Canonical preview could not be completed");
    expect(screen.queryByText(/PRIVATE_CASE_AND_REGEX_ERROR/)).toBeNull(); await tick(2000);
    expect(transport.api).toHaveBeenCalledTimes(1);
  });
  it.each([{ definition: null }, { catalogRevision: undefined }, { blockedReason: "recognition unavailable" }])("does not transmit blocked inputs: %j", async blocked => {
    mount(blocked); live(); changeText("creat 2"); run(); await tick(1000);
    expect(transport.api).not.toHaveBeenCalled();
    expect(screen.getByRole("button", { name: "Run canonical preview" }).hasAttribute("disabled")).toBe(true);
  });
  it("renders canonical units, conversions, warnings and explicit fuzzy VERIFY flags only", async () => {
    transport.api.mockResolvedValueOnce(previewResult({ values: { creatinine: 2, untrusted_field: "DO_NOT_RENDER" }, derived: { shock_index: 1.2 },
      unit_details: { creatinine: { unit: "mg/dL", source_unit: "µmol/L", converted: true, assumed: false } },
      meds: [{ generic: "syntheticdrug", input: "syntehticdrug", classes: ["nsaid", "nephrotoxin"], confidence: "fuzzy", verify: true }],
      warnings: ["Repeated creatinine: latest occurrence used."], regex: { matched: false, timed_out: true } }));
    mount(); run(); await tick();
    expect(screen.getByText(/VERIFY: fuzzy name match/)).toBeTruthy(); expect(screen.getByText(/converted from µmol\/L/)).toBeTruthy();
    expect(screen.getByText("Repeated creatinine: latest occurrence used.")).toBeTruthy();
    expect(screen.getByText(/timed out safely; no match/)).toBeTruthy(); expect(screen.queryByText("DO_NOT_RENDER")).toBeNull();
  });
});
