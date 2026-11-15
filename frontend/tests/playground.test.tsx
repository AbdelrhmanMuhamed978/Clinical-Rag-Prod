import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import PlaygroundPage from "@/app/(app)/w/[workspaceId]/playground/page";
import { ApiError } from "@/lib/api";
import type { ServerSentEvent } from "@/lib/sse";
import { deferred, pipelineResult, progressEvent } from "./analysis-fixtures";

const mocks = vi.hoisted(() => ({ postStream: vi.fn(), workspaceId: "workspace-1", uid: "playground-user", toast: { success: vi.fn(), info: vi.fn(), error: vi.fn() } }));
vi.mock("@/lib/api", () => ({ postStream: mocks.postStream, ApiError: class extends Error { constructor(public status: number, message: string) { super(message); } } }));
vi.mock("next/navigation", () => ({ useParams: () => ({ workspaceId: mocks.workspaceId }) }));
vi.mock("@/providers/AuthProvider", () => ({ useAuth: () => ({ user: { uid: mocks.uid } }) }));
vi.mock("sonner", () => ({ toast: mocks.toast }));
vi.mock("@/components/my-analyses", () => ({ MyAnalyses: ({ workspaceId, userId }: { workspaceId: string; userId: string }) => <div data-testid="my-analyses">Private history: {workspaceId}/{userId}</div> }));

const clients: QueryClient[] = [];
type Emit = (event: ServerSentEvent) => void;
const subtitle = "This response contains items that passed quote and scope verification. Read the citations and cautions.";
function mount() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Infinity } } }); clients.push(client);
  const view = render(<QueryClientProvider client={client}><PlaygroundPage /></QueryClientProvider>);
  return { ...view, client, user: userEvent.setup() };
}
async function send(user: ReturnType<typeof userEvent.setup>, text = "Synthetic guideline question?") {
  await user.type(screen.getByRole("textbox", { name: "Clinical question or case" }), text);
  await user.click(screen.getByRole("button", { name: "Send" }));
}
async function sendValue(user: ReturnType<typeof userEvent.setup>, text: string) {
  // These regressions exercise memory/export, not character-by-character input.
  fireEvent.change(screen.getByRole("textbox", { name: "Clinical question or case" }), { target: { value: text } });
  await user.click(screen.getByRole("button", { name: "Send" }));
}
beforeEach(() => {
  mocks.uid = "playground-user"; mocks.workspaceId = "workspace-1"; mocks.postStream.mockReset();
  mocks.postStream.mockImplementation(async (_path: string, _body: unknown, emit: Emit) => {
    emit({ event: "progress", data: progressEvent("completed") }); emit({ event: "result", data: pipelineResult() });
  });
});
afterEach(() => { clients.splice(0).forEach(client => client.clear()); vi.useRealTimers(); });

describe("verified-only Playground conversation", () => {
  it("shows the verified subtitle only after a response with verified items", async () => {
    const { user } = mount(); expect(screen.queryByText(subtitle)).toBeNull();
    await send(user); await screen.findByText(subtitle);
    expect(screen.getByText("Synthetic verified recommendation.")).toBeTruthy();
    expect(screen.getByText("Question mode", { exact: true })).toBeTruthy();
    const uncovered = pipelineResult(); uncovered.problems = [{ ...uncovered.problems[1] }];
    mocks.postStream.mockImplementationOnce(async (_path: string, _body: unknown, emit: Emit) => emit({ event: "result", data: uncovered }));
    await send(user, "Uncovered question?"); await waitFor(() => expect(screen.queryByText(subtitle)).toBeNull());
    expect(screen.getByText("Synthetic verified recommendation.")).toBeTruthy(); // Previous result is not silently deleted.
    expect(screen.getByText(/Memory keeps only the last six/).textContent).toContain("(1/6)");
  });

  it("uses real SSE progress and authoritative mode while loading, without pretending started steps completed", async () => {
    const pending = deferred<void>(); let emit!: Emit;
    mocks.postStream.mockImplementation((_path: string, _body: unknown, onEvent: Emit) => { emit = onEvent; return pending.promise; });
    const { user } = mount(); await send(user, "A short input whose mode is reported by the pipeline.");
    expect(screen.queryByText(subtitle)).toBeNull(); expect(screen.getByRole("progressbar", { name: "Analysis in progress" }).hasAttribute("value")).toBe(false);
    expect(screen.getByText("Mode not reported", { exact: true })).toBeTruthy();
    await act(async () => { emit({ event: "progress", data: progressEvent("started", "case") }); });
    expect(screen.getByText("Case mode", { exact: true })).toBeTruthy();
    await user.click(screen.getByText("Processing steps (1 reported events)"));
    expect(screen.getByText("Started", { exact: true })).toBeTruthy(); expect(screen.queryByText("Completed", { exact: true })).toBeNull();
    const result = pipelineResult(); result.plan_source = "rules";
    await act(async () => { emit({ event: "result", data: result }); pending.resolve(); });
    await screen.findByText(subtitle); expect(screen.queryByRole("progressbar")).toBeNull();
    expect(screen.getByText("Case mode", { exact: true })).toBeTruthy();
  });

  it("archives actual progress on failure without displaying raw errors or generated text", async () => {
    mocks.postStream.mockImplementationOnce(async (_path: string, _body: unknown, emit: Emit) => {
      emit({ event: "progress", data: progressEvent("started") }); emit({ event: "progress", data: progressEvent("failed") });
      throw new Error("PRIVATE_PIPELINE_ERROR_AND_RAW_ANSWER");
    });
    const { user } = mount(); await send(user); await screen.findByRole("alert");
    expect(screen.getByRole("alert").textContent).toContain("No unverified answer was displayed");
    expect(screen.queryByText(/PRIVATE_PIPELINE_ERROR_AND_RAW_ANSWER/)).toBeNull(); expect(screen.queryByText(subtitle)).toBeNull();
    await user.click(screen.getByText("Processing steps (2 reported events)"));
    expect(screen.getByText("Started", { exact: true })).toBeTruthy(); expect(screen.getByText("Failed", { exact: true })).toBeTruthy();
    expect(screen.queryByText("Completed", { exact: true })).toBeNull(); expect(screen.getByText(/This attempt failed/)).toBeTruthy();
  });

  it("cancels and retains started diagnostics, ignores late results, and leaves no verified memory", async () => {
    const pending = deferred<void>(); let emit!: Emit; let signal!: AbortSignal;
    mocks.postStream.mockImplementationOnce((_path: string, _body: unknown, onEvent: Emit, requestSignal: AbortSignal) => { emit = onEvent; signal = requestSignal; return pending.promise; });
    const { user } = mount(); await send(user);
    await act(async () => { emit({ event: "progress", data: progressEvent("started") }); });
    await user.click(screen.getByRole("button", { name: "Cancel analysis" })); expect(signal.aborted).toBe(true);
    await user.click(screen.getByText("Processing steps (1 reported events)"));
    expect(screen.getByText(/This attempt was cancelled/)).toBeTruthy();
    await act(async () => { emit({ event: "result", data: pipelineResult() }); pending.resolve(); });
    expect(screen.queryByText("Synthetic verified recommendation.")).toBeNull(); expect(screen.queryByText(subtitle)).toBeNull();
    expect(screen.getByText(/Memory keeps only the last six/).textContent).toContain("(0/6)");
    expect(screen.queryByRole("progressbar")).toBeNull(); expect(mocks.toast.success).not.toHaveBeenCalled();
  });

  it.each([{ answer: "RAW_LLM_ANSWER_MARKER" }, { ...pipelineResult(), problems: [{ ...pipelineResult().problems[0], status: "error" }] }])("fails closed on malformed or contradictory results", async result => {
    mocks.postStream.mockImplementationOnce(async (_path: string, _body: unknown, emit: Emit) => emit({ event: "result", data: result }));
    const { user } = mount(); await send(user); await screen.findByRole("alert");
    expect(screen.queryByText("RAW_LLM_ANSWER_MARKER")).toBeNull(); expect(screen.queryByText("Synthetic verified recommendation.")).toBeNull();
    expect(screen.queryByText(subtitle)).toBeNull();
  });

  it.each(["unknown", "llm", "case", "question"])("rejects unrecognized result mode %s, including its items and memory", async planSource => {
    mocks.postStream.mockImplementationOnce(async (_path: string, _body: unknown, emit: Emit) => emit({ event: "result", data: { ...pipelineResult(), plan_source: planSource } }));
    const { user } = mount(); await send(user);
    await waitFor(() => expect(screen.getByRole("alert").textContent).toContain("Verified analysis failed"));
    expect(screen.queryByText("Synthetic verified recommendation.")).toBeNull();
    expect(screen.queryByText(subtitle)).toBeNull();
    expect(screen.getByText(/Memory keeps only the last six/).textContent).toContain("(0/6)");
  });

  it("keeps verified items in a partial result without calling the failed problem Not covered", async () => {
    const result = pipelineResult(); result.problems[1].status = "error";
    mocks.postStream.mockImplementationOnce(async (_path: string, _body: unknown, emit: Emit) => emit({ event: "result", data: result }));
    const { user } = mount(); await send(user); await screen.findByRole("region", { name: "Analysis incomplete" });
    expect(screen.getByText("Synthetic verified recommendation.")).toBeTruthy();
    expect(screen.queryByRole("region", { name: "Not covered" })).toBeNull();
    expect(screen.getAllByRole("status").some(region => region.textContent?.includes("Some problems failed; their guideline coverage is unknown"))).toBe(true);
    expect(screen.getByText(/Memory keeps only the last six/).textContent).toContain("(1/6)");
  });

  it("does not announce an error-only result as evidence of absent guideline coverage", async () => {
    const result = pipelineResult(); result.problems = [{ ...result.problems[1], status: "error" }];
    mocks.postStream.mockImplementationOnce(async (_path: string, _body: unknown, emit: Emit) => emit({ event: "result", data: result }));
    const { user } = mount(); await send(user); await screen.findByRole("region", { name: "Analysis incomplete" });
    expect(mocks.toast.info).toHaveBeenCalledWith("Analysis incomplete. Retry the failed problems; their guideline coverage is unknown.");
    expect(mocks.toast.success).not.toHaveBeenCalled(); expect(screen.queryByText(subtitle)).toBeNull();
    expect(screen.queryByRole("region", { name: "Not covered" })).toBeNull();
  });

  it.each(["missing-result", "invalid-progress", "mode-change", "result-mode-change", "after-result"])("rejects %s stream evidence", async scenario => {
    mocks.postStream.mockImplementationOnce(async (_path: string, _body: unknown, emit: Emit) => {
      if (scenario === "after-result") emit({ event: "result", data: pipelineResult() });
      emit({ event: "progress", data: scenario === "invalid-progress" ? { ...progressEvent(), state: "imagined-success" } : progressEvent() });
      if (scenario === "mode-change") emit({ event: "progress", data: progressEvent("started", "case") });
      if (scenario === "result-mode-change") emit({ event: "result", data: { ...pipelineResult(), plan_source: "rules" } });
    });
    const { user } = mount(); await send(user); await screen.findByRole("alert");
    expect(screen.queryByText(subtitle)).toBeNull(); expect(screen.queryByText("Synthetic verified recommendation.")).toBeNull();
  });

  it("sends a stable conversationId and bounded verified memory, excluding drafts, source excerpts and patient checks", async () => {
    const { user } = mount();
    for (let index = 0; index < 8; index++) { await sendValue(user, `Question ${index}?`); await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledTimes(index + 1)); }
    const conversationId = mocks.postStream.mock.calls[0][1].conversationId;
    expect(typeof conversationId).toBe("string"); expect(conversationId.length).toBeGreaterThan(0);
    for (const [, body] of mocks.postStream.mock.calls) expect(body.conversationId).toBe(conversationId);
    const lastHistory = mocks.postStream.mock.calls[7][1].history;
    expect(lastHistory).toHaveLength(6); expect(lastHistory[0].question).toBe("Question 1?");
    expect(JSON.stringify(lastHistory)).toContain("Synthetic verified recommendation."); expect(JSON.stringify(lastHistory)).toContain("DRAFT");
    expect(JSON.stringify(lastHistory)).not.toMatch(/REJECTED_|PRIVATE_|RETRIEVED_EXCERPT_MARKER|Uncovered topic/);
    expect(screen.getByText(/Memory keeps only the last six/).textContent).toContain("(6/6)");
  });

  it("protects case-follow-up exports while leaving the pipeline's short-question mode truthful", async () => {
    const caseResult = pipelineResult(); caseResult.plan_source = "rules";
    mocks.postStream.mockImplementationOnce(async (_path: string, _body: unknown, emit: Emit) => emit({ event: "result", data: caseResult }));
    const { user } = mount(); await sendValue(user, Array(45).fill("de-identified").join(" ")); await screen.findByText(subtitle);
    const followUp = pipelineResult(); followUp.problems[0].items[0].recommendation = "PRIVATE_FOLLOW_UP_PARAPHRASE"; followUp.warnings = ["PRIVATE_CASE_WARNING"];
    mocks.postStream.mockImplementationOnce(async (_path: string, _body: unknown, emit: Emit) => emit({ event: "result", data: followUp }));
    await sendValue(user, "What about the mother?"); await screen.findByText("PRIVATE_FOLLOW_UP_PARAPHRASE");
    expect(screen.getByText("Question mode", { exact: true })).toBeTruthy();
    const writeText = vi.fn().mockResolvedValue(undefined); Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText } });
    await user.click(screen.getAllByRole("button", { name: "Copy result" }).at(-1)!);
    expect(writeText).toHaveBeenCalledOnce(); expect(writeText.mock.calls[0][0]).not.toMatch(/PRIVATE_|REJECTED_|RETRIEVED_EXCERPT_MARKER/);
    expect(writeText.mock.calls[0][0]).toContain("Question mode"); expect(writeText.mock.calls[0][0]).toContain(followUp.problems[0].items[0].quote);
  });

  it("clears conversation memory and IDs without deleting saved analysis history", async () => {
    const { user } = mount(); await send(user); await screen.findByText(subtitle);
    const firstId = mocks.postStream.mock.calls[0][1].conversationId;
    await user.click(screen.getByRole("button", { name: "Clear history" }));
    expect(screen.queryByText("Synthetic verified recommendation.")).toBeNull(); expect(screen.queryByText(subtitle)).toBeNull();
    expect(screen.getByTestId("my-analyses").textContent).toContain("workspace-1/playground-user");
    await send(user, "New conversation?"); await screen.findByText(subtitle);
    expect(mocks.postStream.mock.calls[1][1].conversationId).not.toBe(firstId); expect(mocks.postStream.mock.calls[1][1].history).toEqual([]);
  });

  it.each(["user", "workspace"])("isolates patient input, history and late responses after a %s switch", async identity => {
    const pending = deferred<void>(); let emit!: Emit; let signal!: AbortSignal;
    mocks.postStream.mockImplementationOnce((_path: string, _body: unknown, onEvent: Emit, requestSignal: AbortSignal) => { emit = onEvent; signal = requestSignal; return pending.promise; });
    const { user, client, rerender } = mount(); await send(user, "PRIVATE_PREVIOUS_INPUT");
    if (identity === "user") mocks.uid = "another-user"; else mocks.workspaceId = "workspace-2";
    rerender(<QueryClientProvider client={client}><PlaygroundPage /></QueryClientProvider>);
    expect(signal.aborted).toBe(true); expect(screen.queryByText("PRIVATE_PREVIOUS_INPUT")).toBeNull();
    await act(async () => { emit({ event: "result", data: pipelineResult() }); pending.resolve(); });
    expect(screen.queryByText("Synthetic verified recommendation.")).toBeNull(); expect(mocks.toast.success).not.toHaveBeenCalled();
    await send(user, "New identity question?"); await screen.findByText(subtitle);
    expect(mocks.postStream.mock.calls[1][1].history).toEqual([]);
    expect(mocks.postStream.mock.calls[1][0]).toBe(`/api/workspaces/${mocks.workspaceId}/analyze/stream`);
  });

  it("keeps IME composition and Shift+Enter from submitting and accepts plain Enter once", async () => {
    mount(); const input = screen.getByRole("textbox", { name: "Clinical question or case" });
    fireEvent.change(input, { target: { value: "Synthetic input?" } });
    fireEvent.keyDown(input, { key: "Enter", isComposing: true }); fireEvent.keyDown(input, { key: "Enter", keyCode: 229 }); fireEvent.keyDown(input, { key: "Enter", shiftKey: true });
    expect(mocks.postStream).not.toHaveBeenCalled();
    await act(async () => { fireEvent.keyDown(input, { key: "Enter" }); });
    expect(mocks.postStream).toHaveBeenCalledOnce();
  });

  it("shows actionable permission failure without retaining a result", async () => {
    mocks.postStream.mockRejectedValueOnce(new ApiError(403, "PRIVATE_SERVER_BODY")); const { user } = mount(); await send(user);
    const alert = await screen.findByRole("alert"); expect(within(alert).getByText("Editor access to this workspace is required to run analysis.")).toBeTruthy();
    expect(screen.queryByText(/PRIVATE_SERVER_BODY/)).toBeNull(); expect(screen.queryByText(subtitle)).toBeNull();
  });

  it.each([[0, "Check your connection"], [401, "Sign in again"], [408, "Analysis timed out"], [429, "Wait before retrying"], [503, "check service readiness"]])("provides safe actionable HTTP failure feedback (%i)", async (status, expected) => {
    mocks.postStream.mockRejectedValueOnce(new ApiError(status as number, "PRIVATE_SERVER_BODY"));
    const { user } = mount(); await send(user);
    await waitFor(() => expect(screen.getByRole("alert").textContent).toContain(expected));
    expect(screen.queryByText(/PRIVATE_SERVER_BODY/)).toBeNull(); expect(screen.queryByText(subtitle)).toBeNull();
    expect(screen.queryByRole("progressbar")).toBeNull();
  });

  it.each(["colab", "pipeline", "PRIVATE_DEPENDENCY"])("sanitizes SSE errors and describes only recognized unavailable services (%s)", async dependency => {
    mocks.postStream.mockImplementationOnce(async (_path: string, _body: unknown, emit: Emit) => {
      emit({ event: "progress", data: progressEvent("started") });
      emit({ event: "error", data: { dependency, message: "PRIVATE_RAW_ANSWER_AND_STACK", retryAfterSeconds: 30 } });
    });
    const { user } = mount(); await send(user);
    const expected = dependency === "colab" ? "Ask an administrator to check its connection" : dependency === "pipeline" ? "pipeline service is temporarily unavailable" : "Verified analysis failed";
    await waitFor(() => expect(screen.getByRole("alert").textContent).toContain(expected));
    expect(screen.queryByText(/PRIVATE_RAW_ANSWER_AND_STACK|PRIVATE_DEPENDENCY/)).toBeNull();
    expect(screen.queryByText(subtitle)).toBeNull(); expect(mocks.toast.success).not.toHaveBeenCalled();
  });
});
