import { act, fireEvent, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AnalysisResult } from "@/components/analysis-result";
import { AnalysisActions } from "@/components/analysis-actions";
import { ProcessingSteps } from "@/components/processing-steps";
import { formatAnalysisExport, parsePipelineProgress, parsePipelineResult } from "@/lib/analysis";
import { deferred, pipelineResult, progressEvent } from "./analysis-fixtures";

const writeText = vi.fn();
beforeEach(() => {
  writeText.mockReset().mockResolvedValue(undefined);
  Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText } });
});
afterEach(() => { vi.unstubAllGlobals(); vi.useRealTimers(); });

describe("structured guideline result rendering", () => {
  it("renders verified recommendations, exact source whitespace, citation chips and DRAFT badges", () => {
    const result = pipelineResult(); const { container } = render(<AnalysisResult result={result} />);
    expect(screen.getByText("Synthetic verified recommendation.")).toBeTruthy();
    const quote = container.querySelector("blockquote")!;
    expect(quote.textContent).toBe(`“${result.problems[0].items[0].quote}”`);
    expect(quote.classList.contains("whitespace-pre-wrap")).toBe(true);
    const verified = quote.closest('[data-slot="card"]') as HTMLElement;
    for (const chip of ["S1", "Synthetic Society 2024", "Source section", "p.12", "strong", "DRAFT"])
      expect(within(verified).getByText(chip, { exact: true }).getAttribute("data-slot")).toBe("badge");
    expect(within(verified).queryByText(/REJECTED_/)).toBeNull();
  });

  it("keeps rejected statements, draft quotes and issues inside a collapsed, explicitly excluded disclosure", async () => {
    const { container } = render(<AnalysisResult result={pipelineResult()} />); const user = userEvent.setup();
    const summary = screen.getByText("Unverified (not shown as answers) (1)");
    const disclosure = summary.closest("details")!;
    expect(disclosure.open).toBe(false);
    expect(screen.getByText(/REJECTED_STATEMENT_MARKER/).closest("details")).toBe(disclosure);
    expect(screen.getByText(/REJECTED_QUOTE_MARKER/).closest("details")).toBe(disclosure);
    expect(screen.getByText("Scope mismatch.").closest("details")).toBe(disclosure);
    expect(container.querySelectorAll('[data-slot="card"]')).toHaveLength(1);
    await user.click(summary); expect(disclosure.open).toBe(true);
    expect(within(disclosure).getByText(/Do not use them as answers/)).toBeTruthy();
  });

  it("renders Not covered as a separate list, warnings and the clinical disclaimer", () => {
    const result = pipelineResult(); render(<AnalysisResult result={result} />);
    const notCovered = screen.getByRole("region", { name: "Not covered" });
    expect(within(notCovered).getByRole("listitem").textContent).toBe("Uncovered topic");
    expect(within(screen.getByRole("complementary", { name: "Warnings" })).getByText(result.warnings[0])).toBeTruthy();
    expect(screen.getByText(result.disclaimer)).toBeTruthy();
    expect(screen.getByText(/This does not mean the topic is unimportant or safe/)).toBeTruthy();
  });

  it("keeps patient-specific checks separate and can suppress them for saved results", () => {
    const result = pipelineResult(); const view = render(<AnalysisResult result={result} />);
    const checks = screen.getByRole("complementary", { name: "Check against this patient" });
    expect(within(checks).getByText("PRIVATE_PATIENT_CHECK_MARKER")).toBeTruthy();
    expect(within(checks).getByText(/Deterministic rule checks, not guideline evidence/)).toBeTruthy();
    view.rerender(<AnalysisResult result={result} showPatientChecks={false} />);
    expect(screen.queryByRole("complementary", { name: "Check against this patient" })).toBeNull();
    expect(screen.queryByText("PRIVATE_PATIENT_CHECK_MARKER")).toBeNull();
  });

  it.each(["unverified", "error", "not_found"] as const)("never labels an all-%s result verified", status => {
    const result = pipelineResult(); result.problems = [{ ...result.problems[0], status, items: [], detail: "Safe pipeline diagnostic." }];
    render(<AnalysisResult result={result} />);
    expect(screen.getByRole("heading", { name: "Guideline pipeline result" })).toBeTruthy();
    expect(screen.queryByText("Verified recommendation", { exact: true })).toBeNull();
    expect(screen.queryByRole("heading", { name: "Verified guideline analysis" })).toBeNull();
    expect(screen.getByText(/Pipeline diagnostic: Safe pipeline diagnostic/)).toBeTruthy();
  });

  it("renders a deterministic greeting as nonclinical, not an alternative raw answer", () => {
    const result = parsePipelineResult({ ...pipelineResult(), plan_source: "chat", chat: "Ask a guideline question.", problems: [], not_covered: [], warnings: [] });
    render(<AnalysisResult result={result} />);
    expect(screen.getByText("Greeting — not a clinical answer")).toBeTruthy();
    expect(screen.getByText("Ask a guideline question.")).toBeTruthy();
    expect(screen.queryByText("Verified recommendation", { exact: true })).toBeNull();
  });

  it("separates failed problems from absent evidence in live and legacy saved results", () => {
    const result = pipelineResult(); result.problems[1].status = "error";
    render(<AnalysisResult result={result} />);
    const incomplete = screen.getByRole("region", { name: "Analysis incomplete" });
    expect(within(incomplete).getByRole("listitem").textContent).toBe("Uncovered topic");
    expect(within(incomplete).getByText(/guideline coverage is unknown/)).toBeTruthy();
    expect(screen.queryByRole("region", { name: "Not covered" })).toBeNull();
    expect(screen.queryByText(/Not covered by the loaded guidelines with a verifiable statement/)).toBeNull();
    expect(parsePipelineResult(result).not_covered).toEqual([]);
    expect(screen.getByText("Synthetic verified recommendation.")).toBeTruthy();
  });

  it("rejects a chat mode carrying clinical problems instead of hiding verified items behind greeting text", () => {
    expect(() => parsePipelineResult({ ...pipelineResult(), plan_source: "chat", chat: "RAW_ALTERNATIVE_ANSWER" })).toThrow();
  });

  it("marks legacy progress completion unknown and does not invent checklist steps", async () => {
    render(<AnalysisResult result={pipelineResult()} processSteps={["Legacy pipeline message"]} />);
    await userEvent.setup().click(screen.getByText("Processing steps (1 reported events)"));
    expect(screen.getByText("Reported — completion unknown", { exact: true })).toBeTruthy();
    expect(screen.queryByText("Completed", { exact: true })).toBeNull();
    expect(screen.queryByText("Read case", { exact: true })).toBeNull();
  });

  it("shows only the actual started, completed and failed events, retaining failure context", () => {
    const events = [progressEvent("started"), progressEvent("completed"), progressEvent("failed")];
    render(<ProcessingSteps events={events} outcome="failed" />);
    expect(screen.getByText("Started", { exact: true })).toBeTruthy();
    expect(screen.getByText("Completed", { exact: true })).toBeTruthy();
    expect(screen.getByText("Failed", { exact: true })).toBeTruthy();
    expect(screen.getAllByRole("listitem", { hidden: true })).toHaveLength(3);
    expect(screen.getByText(/This attempt failed/)).toBeTruthy();
  });
});

describe("safe copy and export", () => {
  it("copies the safe serializer even after rejected drafts and retrieved sources are expanded", async () => {
    const result = pipelineResult(); render(<AnalysisResult result={result} />); const user = userEvent.setup();
    // user-event supplies its own clipboard stub; restore the measured API after setup.
    Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText } });
    await user.click(screen.getByText("Unverified (not shown as answers) (1)"));
    await user.click(screen.getByText("Retrieved sources — not additional answers (1)"));
    await user.click(screen.getByRole("button", { name: "Copy result" }));
    expect(writeText).toHaveBeenCalledOnce(); expect(writeText).toHaveBeenCalledWith(formatAnalysisExport(result));
    expect(writeText.mock.calls[0][0]).not.toMatch(/REJECTED_STATEMENT_MARKER|REJECTED_QUOTE_MARKER|PRIVATE_PATIENT_CHECK_MARKER|PRIVATE_QUERY_MARKER|RETRIEVED_EXCERPT_MARKER/);
    expect(screen.getByRole("status").textContent).toContain("Result copied");
  });

  it("uses exactly one native command path, resolves the target and refreshes the result on rerender", async () => {
    vi.stubGlobal("CommandEvent", class extends Event {});
    const first = pipelineResult(); const view = render(<AnalysisActions result={first} />);
    const button = screen.getByRole("button", { name: "Copy result" });
    const target = document.getElementById(button.getAttribute("commandfor")!)!;
    expect(button.getAttribute("command")).toBe("--copy"); expect(target).toBeTruthy();
    fireEvent.click(button); expect(writeText).not.toHaveBeenCalled();
    const command = Object.assign(new Event("command", { bubbles: false }), { command: "--copy" });
    await act(async () => { target.dispatchEvent(command); });
    expect(writeText).toHaveBeenCalledOnce();
    const second = pipelineResult(); second.problems[0].items[0].recommendation = "Updated verified recommendation.";
    view.rerender(<AnalysisActions result={second} />);
    await act(async () => { target.dispatchEvent(Object.assign(new Event("command"), { command: "--copy" })); });
    expect(writeText).toHaveBeenCalledTimes(2); expect(writeText.mock.calls[1][0]).toContain("Updated verified recommendation.");
  });

  it("shows and announces clipboard failure without disabling retries or using deprecated copying", async () => {
    writeText.mockRejectedValue(new Error("permission denied PRIVATE_BROWSER_ERROR"));
    render(<AnalysisActions result={pipelineResult()} />);
    fireEvent.click(screen.getByRole("button", { name: "Copy result" }));
    await act(async () => { await Promise.resolve(); });
    expect(screen.getByRole("status").textContent).toBe("Copy failed. Allow clipboard access or use Export result.");
    expect(screen.getByRole("button", { name: "Copy result" }).hasAttribute("disabled")).toBe(false);
    expect(screen.queryByText(/PRIVATE_BROWSER_ERROR/)).toBeNull();
    writeText.mockResolvedValue(undefined);
    fireEvent.click(screen.getByRole("button", { name: "Copy result" }));
    await act(async () => { await Promise.resolve(); });
    expect(writeText).toHaveBeenCalledTimes(2);
    expect(screen.getByRole("status").textContent).toContain("Result copied");
  });

  it("clears feedback, distinguishes repeat announcements and cancels pending copies on unmount", async () => {
    vi.useFakeTimers(); const view = render(<AnalysisActions result={pipelineResult()} />);
    const button = screen.getByRole("button", { name: "Copy result" });
    await act(async () => { fireEvent.click(button); });
    const first = screen.getByRole("status").textContent;
    await act(async () => { fireEvent.click(button); });
    expect(screen.getByRole("status").textContent).not.toBe(first);
    await act(async () => { await vi.advanceTimersByTimeAsync(4000); });
    expect(screen.getByRole("status").textContent).toBe("");
    const pending = deferred<void>(); writeText.mockReturnValue(pending.promise);
    fireEvent.click(button); view.unmount();
    const timers = vi.getTimerCount();
    await act(async () => { pending.resolve(); });
    expect(vi.getTimerCount()).toBe(timers);
  });

  it("exports a safe downloadable text file and releases its object URL", async () => {
    vi.useFakeTimers(); let blob!: Blob;
    const create = vi.fn((value: Blob) => { blob = value; return "blob:synthetic-result"; });
    const revoke = vi.fn();
    Object.defineProperty(URL, "createObjectURL", { configurable: true, value: create });
    Object.defineProperty(URL, "revokeObjectURL", { configurable: true, value: revoke });
    const requested: HTMLAnchorElement[] = [];
    vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(function (this: HTMLAnchorElement) { requested.push(this); });
    const result = pipelineResult(); const view = render(<AnalysisActions result={result} />);
    fireEvent.click(screen.getByRole("button", { name: "Export result" }));
    expect(create).toHaveBeenCalledOnce(); expect(blob.type).toBe("text/plain;charset=utf-8");
    expect(requested).toHaveLength(1);
    expect(requested[0].download).toBe("guideline-result.txt"); expect(requested[0].href).toBe("blob:synthetic-result");
    expect(document.contains(requested[0])).toBe(false);
    vi.useRealTimers();
    const exported = await new Promise<string>((resolve, reject) => {
      const reader = new FileReader(); reader.onload = () => resolve(reader.result as string); reader.onerror = reject; reader.readAsText(blob);
    });
    expect(exported).toBe(formatAnalysisExport(result));
    expect(exported).not.toMatch(/REJECTED_|PRIVATE_|RETRIEVED_EXCERPT_MARKER/);
    view.unmount(); expect(revoke).toHaveBeenCalledWith("blob:synthetic-result");
  });

  it("uses guideline quotes rather than patient-derived paraphrases for case and case-follow-up exports", () => {
    const result = pipelineResult(); result.plan_source = "rules";
    result.problems[0].title = "PRIVATE_CASE_TITLE_MARKER";
    result.problems[0].items[0].recommendation = "PRIVATE_CASE_PARAPHRASE_MARKER";
    result.warnings = ["PRIVATE_CASE_WARNING_MARKER"];
    const exported = formatAnalysisExport(result);
    expect(exported).not.toMatch(/PRIVATE_|REJECTED_|RETRIEVED_EXCERPT_MARKER/);
    expect(exported).toContain(result.problems[0].items[0].quote); expect(exported).toContain("DRAFT — not final");
    expect(exported).toContain("Not covered does not mean the topic is unimportant or safe.");
    expect(exported).toContain(result.disclaimer);
    result.plan_source = "single_question";
    expect(formatAnalysisExport(result, true)).not.toMatch(/PRIVATE_|REJECTED_/);
    expect(formatAnalysisExport(result, true)).toContain("Question mode"); // Privacy is not a mode override.
  });

  it.each(["single_question", "rules"])("preserves failure uncertainty in %s copy/export without exporting rejected or patient text", planSource => {
    const result = pipelineResult(); result.plan_source = planSource; result.problems[1].status = "error";
    const exported = formatAnalysisExport(result);
    expect(exported).toContain("Analysis failed. Coverage is unknown; retry this problem.");
    expect(exported).toContain("Analysis incomplete — coverage unknown:");
    expect(exported).not.toContain("Not covered:");
    expect(exported).not.toMatch(/REJECTED_|PRIVATE_|RETRIEVED_EXCERPT_MARKER/);
    expect(exported).toContain(result.problems[0].items[0].quote);
  });

  it.each([{ message: "" }, { ...progressEvent(), state: "done" }, { ...progressEvent(), mode: "case-ish" },
    { message: "Checking", stage: "verification" }, { ...progressEvent(), stepId: "../secret" }, { ...progressEvent(), message: "x".repeat(501) }])("rejects invalid progress metadata (%j)", value => {
    expect(() => parsePipelineProgress(value)).toThrow();
  });
});
