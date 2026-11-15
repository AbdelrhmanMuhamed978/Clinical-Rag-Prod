import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import ChunkReviewPage from "@/app/(app)/w/[workspaceId]/documents/[docId]/chunks/page";
import { ApiError } from "@/lib/api";
import type { Chunk, Document, DocumentChunkCounts, RuleAccess } from "@/lib/types";
import { deferred } from "./analysis-fixtures";

const mocks = vi.hoisted(() => ({ get: vi.fn(), api: vi.fn(), workspaceId: "workspace-1", docId: "document-1", uid: "chunk-user",
  toast: { success: vi.fn(), error: vi.fn(), warning: vi.fn() } }));
vi.mock("@/lib/api", () => ({ get: mocks.get, api: mocks.api, ApiError: class extends Error { constructor(public status: number, message: string) { super(message); } } }));
vi.mock("next/navigation", () => ({ useParams: () => ({ workspaceId: mocks.workspaceId, docId: mocks.docId }) }));
vi.mock("@/providers/AuthProvider", () => ({ useAuth: () => ({ user: mocks.uid ? { uid: mocks.uid } : null }) }));
vi.mock("sonner", () => ({ toast: mocks.toast }));

let record: Document;
let chunks: Chunk[];
let access: RuleAccess;
let progress: { totalPages: number; chunkedPages: number; chunkedPageNumbers: number[]; chunkingStatus: string; progressPercent: number };
const clients: QueryClient[] = [];
const documentRoot = () => `/api/workspaces/${mocks.workspaceId}/documents/${mocks.docId}`;
const chunkRoot = () => `/api/workspaces/${mocks.workspaceId}/chunks`;
const body = (init?: RequestInit) => JSON.parse(init?.body as string);
const disabled = (element: HTMLElement) => expect(element.hasAttribute("disabled")).toBe(true);
function makeDocument(changes: Partial<Document> = {}): Document {
  return { id: mocks.docId, workspaceId: mocks.workspaceId, fileName: "Synthetic guideline.pdf", status: "review", pageCount: 3,
    parsedText: [1, 2, 3].map(page => `--- Page ${page} ---\nSynthetic page ${page}.`).join("\n\n"), processingOperation: null,
    society: "Synthetic Society", year: 2024, guidelineStatus: "draft", revision: 1,
    createdAt: "2026-11-15T00:00:00Z", updatedAt: "2026-11-15T00:00:00Z", ...changes };
}
function makeChunk(id: string, changes: Partial<Chunk> = {}): Chunk {
  return { id, workspaceId: mocks.workspaceId, documentId: mocks.docId, page: 1, text: `Exact synthetic source passage ${id}.`,
    status: "pending", confidence: 0.9, verbatim: true, type: "clinical", createdAt: "2026-11-15T00:00:00Z", ...changes };
}
function counts(): DocumentChunkCounts {
  const count = (status: string) => chunks.filter(chunk => chunk.status === status).length;
  return { total: chunks.length, pending: count("pending"), accepted: count("accepted"), rejected: count("rejected"), embedded: count("embedded"),
    other: chunks.filter(chunk => !["pending", "accepted", "rejected", "embedded"].includes(chunk.status)).length };
}
function reconcile() {
  const evidence = counts();
  record = { ...record, status: evidence.embedded > 0 && evidence.pending === 0 && evidence.accepted === 0 && evidence.other === 0 ? "indexed" : "review",
    processingOperation: null, failureStage: null, failureReason: null, revision: (record.revision ?? 0) + 1 };
}
function checkpoint(pages: number[], status = "partial") {
  progress = { totalPages: record.pageCount ?? 0, chunkedPages: pages.length, chunkedPageNumbers: pages, chunkingStatus: status,
    progressPercent: 0 }; // The UI must derive its percentage from saved page counts.
}
function mount() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Infinity, refetchOnWindowFocus: false } } });
  clients.push(client);
  const tree = () => <QueryClientProvider client={client}><ChunkReviewPage /></QueryClientProvider>;
  const view = render(tree());
  return { client, user: userEvent.setup(), unmount: view.unmount, rerender: () => view.rerender(tree()) };
}
async function ready() { await screen.findByRole("group", { name: "Pages to chunk" }); }
function pageInputs() { return within(screen.getByRole("group", { name: "Pages to chunk" })); }
function selected() { return screen.getByRole("button", { name: /^Chunk Selected/ }); }
async function pendingView(user: ReturnType<typeof userEvent.setup>) { await user.click(screen.getByRole("tab", { name: /^Pending Chunks/ })); }

beforeEach(() => {
  mocks.workspaceId = "workspace-1"; mocks.docId = "document-1"; mocks.uid = "chunk-user";
  mocks.get.mockReset(); mocks.api.mockReset();
  record = makeDocument(); chunks = [makeChunk("chunk-1")]; checkpoint([1]);
  access = { userId: "chunk-member", role: "editor", canEdit: true, canReview: false, canDelete: false };
  mocks.get.mockImplementation(async (path: string) => {
    if (path === documentRoot()) return { ...record, chunkCounts: counts() };
    if (path === `/api/workspaces/${mocks.workspaceId}/rules/access`) return { ...access };
    if (path === `${documentRoot()}/chunking-progress`) return { ...progress, chunkedPageNumbers: [...progress.chunkedPageNumbers] };
    if (path === `${documentRoot()}/chunker-guidance`) return { guidance: "keep each recommendation with its strength grade; never split tables" };
    if (path === `${chunkRoot()}?documentId=${mocks.docId}&includeEmbedded=true`) return chunks.map(chunk => ({ ...chunk }));
    throw new Error(`Unexpected GET ${path}`);
  });
  mocks.api.mockImplementation(async (path: string, init?: RequestInit) => {
    if (path === `${documentRoot()}/chunk-portions`) {
      const pages: number[] = body(init).pages;
      chunks.push(...pages.map(page => makeChunk(`new-${page}`, { page })));
      checkpoint([...new Set([...progress.chunkedPageNumbers, ...pages])].sort((a, b) => a - b),
        progress.chunkedPages + pages.length === progress.totalPages ? "completed" : "partial");
      reconcile();
      return { chunksCreated: pages.length, pagesChunked: pages.length, pagesSkipped: 0, failedPages: [] };
    }
    if (path === `${documentRoot()}/generate-embeddings`) {
      const embedded = chunks.filter(chunk => chunk.status === "accepted").length;
      chunks = chunks.map(chunk => chunk.status === "accepted" ? { ...chunk, status: "embedded" } : chunk); reconcile();
      return { embedded, documentStatus: record.status };
    }
    if (path === `${chunkRoot()}/bulk-accept`) {
      const threshold: number = body(init).minConfidence;
      let accepted = 0;
      chunks = chunks.map(chunk => chunk.status === "pending" && chunk.confidence != null && chunk.confidence >= threshold
        ? (accepted++, { ...chunk, status: "accepted" }) : chunk); reconcile();
      return { accepted };
    }
    if (init?.method === "PATCH") {
      const chunkId = path.split("/").at(-1);
      chunks = chunks.map(chunk => chunk.id === chunkId ? { ...chunk, status: body(init).status } : chunk); reconcile();
      return chunks.find(chunk => chunk.id === chunkId);
    }
    throw new Error(`Unexpected mutation ${path}`);
  });
});
afterEach(() => { clients.splice(0).forEach(client => client.clear()); vi.useRealTimers(); });

describe("Chunk Review page selection and completion evidence", () => {
  it("prefills last saved guidance without overwriting an edit on refetch", async () => {
    const original = mocks.get.getMockImplementation()!;
    let saved = "Preserve recommendation grades.";
    mocks.get.mockImplementation((path: string, init: RequestInit) => path.endsWith("/chunker-guidance") ? Promise.resolve({ guidance: saved }) : original(path, init));
    const view = mount(); await ready(); await waitFor(() => expect((screen.getByLabelText("Chunker guidance") as HTMLTextAreaElement).value).toBe(saved));
    fireEvent.change(screen.getByLabelText("Chunker guidance"), { target: { value: "My local guidance." } }); saved = "Remote guidance.";
    await view.user.click(screen.getByRole("button", { name: "Refresh" }));
    await waitFor(() => expect((screen.getByLabelText("Chunker guidance") as HTMLTextAreaElement).value).toBe("My local guidance."));
  });
  it("resets processed pages only after confirmation, refreshes server flags and does not automatically re-chunk", async () => {
    const original = mocks.api.getMockImplementation()!;
    mocks.api.mockImplementation((path: string, init: RequestInit) => {
      if (!path.endsWith("/reset-pages")) return original(path, init);
      chunks = []; checkpoint([]); return Promise.resolve({ chunksDeleted: 1, resetPages: [1] });
    });
    const view = mount(); await ready(); await view.user.click(screen.getByText("Document reset controls"));
    await view.user.click(screen.getByRole("checkbox", { name: "Reset page 1" })); await view.user.click(screen.getByRole("button", { name: "Reset selected pages" }));
    expect(mocks.api).not.toHaveBeenCalled(); await view.user.click(screen.getByRole("button", { name: "Reset pages" }));
    await screen.findByText(/Deleted 1 chunks\. Reset pages: 1/); expect(mocks.api).toHaveBeenCalledOnce();
    expect(mocks.api.mock.calls[0][0]).toBe(`${documentRoot()}/reset-pages`); expect(body(mocks.api.mock.calls[0][1])).toEqual({ pages: [1] });
    await waitFor(() => expect((pageInputs().getByRole("checkbox", { name: "Page 1" }) as HTMLInputElement).checked).toBe(true));
    expect(selected().textContent).toContain("(1)");
  });
  it("resets embeddings only after confirmation and refreshes accepted work", async () => {
    chunks = [makeChunk("embedded", { status: "embedded" })]; reconcile();
    mocks.api.mockImplementation(async path => { expect(path).toBe(`${documentRoot()}/reset-embeddings`); chunks = chunks.map(chunk => ({ ...chunk, status: "accepted" })); reconcile(); return { embeddingsReset: 1 }; });
    const view = mount(); await ready(); await view.user.click(screen.getByText("Document reset controls"));
    await view.user.click(screen.getByRole("button", { name: "Reset embeddings" })); expect(mocks.api).not.toHaveBeenCalled();
    await view.user.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Reset embeddings" }));
    await screen.findByText(/Reset embeddings for 1 chunks/); await waitFor(() => expect(screen.getByRole("button", { name: "Embed (1)" }).hasAttribute("disabled")).toBe(false));
    expect(mocks.api).toHaveBeenCalledOnce();
  });
  it("defaults only to unprocessed pages, exposes native labels and does not start processing on load", async () => {
    mount(); await ready();
    const inputs = pageInputs();
    disabled(inputs.getByRole("checkbox", { name: "Page 1" }));
    expect((inputs.getByRole("checkbox", { name: "Page 2" }) as HTMLInputElement).checked).toBe(true);
    expect((inputs.getByRole("checkbox", { name: "Page 3" }) as HTMLInputElement).checked).toBe(true);
    expect(selected().textContent).toContain("(2)"); expect(mocks.api).not.toHaveBeenCalled();
    expect(screen.getByText("1/3 pages (33.3%)")).toBeTruthy();
    expect((screen.getByRole("progressbar") as HTMLProgressElement).value).toBe(33.3);
    expect((screen.getByLabelText("Chunker guidance") as HTMLTextAreaElement).maxLength).toBe(4000);
    expect(screen.getByLabelText("Min confidence:")).toBeTruthy(); expect(screen.getByText("DRAFT", { exact: true })).toBeTruthy();
    const ids = [...document.querySelectorAll("[id]")].map(element => element.id);
    expect(new Set(ids).size).toBe(ids.length);
  });

  it("keeps Clear empty across rerenders and refreshed checkpoints until selection is explicitly restored", async () => {
    const view = mount(); await ready(); await view.user.click(screen.getByRole("button", { name: "Clear" }));
    disabled(selected()); view.rerender(); checkpoint([1, 2]);
    await view.user.click(screen.getByRole("button", { name: "Refresh" }));
    await screen.findByText("2/3 pages (66.7%)");
    expect((pageInputs().getByRole("checkbox", { name: "Page 3" }) as HTMLInputElement).checked).toBe(false); disabled(selected());
    await view.user.click(screen.getByRole("button", { name: "Select unchunked" }));
    expect((pageInputs().getByRole("checkbox", { name: "Page 3" }) as HTMLInputElement).checked).toBe(true);
    expect(selected().textContent).toContain("(1)"); expect(mocks.api).not.toHaveBeenCalled();
  });

  it("keeps deselecting the final page empty after refresh", async () => {
    const { user } = mount(); await ready();
    await user.click(pageInputs().getByRole("checkbox", { name: "Page 2" }));
    await user.click(pageInputs().getByRole("checkbox", { name: "Page 3" }));
    await user.click(screen.getByRole("button", { name: "Refresh" }));
    await waitFor(() => expect(selected().textContent).toContain("(0)")); disabled(selected());
    expect(pageInputs().getAllByRole("checkbox").every(input => !(input as HTMLInputElement).checked)).toBe(true);
  });

  it("sends every unprocessed page in one server-owned batch request, not a client batch loop", async () => {
    record = makeDocument({ pageCount: 7, parsedText: Array.from({ length: 7 }, (_, index) => `--- Page ${index + 1} ---\nSynthetic page.`).join("\n\n") });
    checkpoint([1]); const { user } = mount(); await ready();
    await user.click(screen.getByRole("button", { name: "Chunk All Portions (6)" }));
    await screen.findByText("7/7 pages (100%)");
    expect(mocks.api).toHaveBeenCalledOnce(); expect(mocks.api.mock.calls[0][0]).toBe(`${documentRoot()}/chunk-portions`);
    expect(body(mocks.api.mock.calls[0][1])).toMatchObject({ pages: [2, 3, 4, 5, 6, 7], batchSize: 2, maxTokens: 400 });
    expect(mocks.api.mock.calls[0][1].signal).toBeInstanceOf(AbortSignal);
    expect(mocks.toast.success).toHaveBeenCalledWith("6 page(s) chunked; 6 chunks created; 0 page(s) skipped. Review chunks before embedding.");
  });

  it("chunks only the explicit subset and does not reselect remaining pages after success", async () => {
    const { user } = mount(); await ready(); await user.click(pageInputs().getByRole("checkbox", { name: "Page 2" }));
    fireEvent.change(screen.getByLabelText("Chunker guidance"), { target: { value: "Keep source tables together." } });
    await user.click(selected()); await screen.findByText("2/3 pages (66.7%)");
    expect(body(mocks.api.mock.calls[0][1])).toEqual({ pages: [3], guidance: "Keep source tables together.", maxTokens: 400, batchSize: 2 });
    disabled(selected()); expect((pageInputs().getByRole("checkbox", { name: "Page 2" }) as HTMLInputElement).checked).toBe(false);
    expect(screen.getByText("Chunking status: Partial")).toBeTruthy();
  });

  it("keeps committed pages on partial failure and selects only failed pages for the next retry", async () => {
    mocks.api.mockImplementationOnce(async () => {
      chunks.push(makeChunk("saved-2", { page: 2 })); checkpoint([1, 2], "failed");
      record = { ...record, status: "failed", failureStage: "chunking", failureReason: "Could not chunk page 3. Completed pages were kept." };
      return { chunksCreated: 1, pagesChunked: 1, pagesSkipped: 0, failedPages: [3] };
    });
    const { user } = mount(); await ready(); await user.click(selected()); await screen.findByText("2/3 pages (66.7%)");
    disabled(pageInputs().getByRole("checkbox", { name: "Page 2" }));
    expect((pageInputs().getByRole("checkbox", { name: "Page 3" }) as HTMLInputElement).checked).toBe(true);
    expect(mocks.toast.warning).toHaveBeenCalledWith(expect.stringContaining("Failed pages: p.3. Completed pages were kept"));
    expect(mocks.toast.success).not.toHaveBeenCalled();
    await user.click(selected()); await screen.findByText("3/3 pages (100%)");
    expect(body(mocks.api.mock.calls[1][1]).pages).toEqual([3]); expect(chunks.some(chunk => chunk.id === "saved-2")).toBe(true);
  });

  it("reports skipped pages numerically without displaying upstream messages or skip reasons", async () => {
    mocks.api.mockImplementationOnce(async () => {
      checkpoint([1, 2], "failed");
      return { chunksCreated: 0, pagesChunked: 0, pagesSkipped: 1, failedPages: [3],
        message: "PRIVATE_UPSTREAM_MESSAGE", skippedPages: [{ page: 2, reason: "PRIVATE_SKIP_REASON" }] };
    });
    const { user } = mount(); await ready(); await user.click(selected()); await screen.findByText("2/3 pages (66.7%)");
    expect(mocks.toast.warning).toHaveBeenCalledWith(expect.stringContaining("0 page(s) chunked; 0 chunks created; 1 page(s) skipped"));
    expect(screen.queryByText(/PRIVATE_/)).toBeNull();
    expect(screen.getByText(/Processed pages include explicitly skipped pages/)).toBeTruthy();
  });

  it.each([{}, { chunksCreated: "2", pagesChunked: 2, pagesSkipped: 0, failedPages: [] },
    { chunksCreated: 1, pagesChunked: 3, pagesSkipped: 0, failedPages: [] },
    { chunksCreated: 1, pagesChunked: 1, pagesSkipped: 0, failedPages: [99] }])("does not claim chunking completion from malformed evidence (%j)", async response => {
    mocks.api.mockResolvedValueOnce({ ...response, message: "PRIVATE_UPSTREAM_MESSAGE" });
    const { user } = mount(); await ready(); await user.click(selected());
    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalledWith(expect.stringContaining("Chunking did not complete")));
    expect(mocks.toast.success).not.toHaveBeenCalled(); expect(selected().textContent).toContain("(2)");
    expect(screen.queryByText(/PRIVATE_/)).toBeNull(); expect(screen.getByText("1/3 pages (33.3%)")).toBeTruthy();
  });

  it.each([{}, { embedded: "1" }, { embedded: -1 }, { embedded: 0.5 }, { embedded: Number.NaN }])("does not substitute a zero embedding count for missing or invalid evidence (%j)", async response => {
    chunks = [makeChunk("accepted-1", { status: "accepted" })]; mocks.api.mockResolvedValueOnce(response);
    const { user } = mount(); await ready(); await user.click(screen.getByRole("button", { name: "Embed (1)" }));
    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalledWith(expect.stringContaining("No completion was confirmed")));
    expect(mocks.toast.success).not.toHaveBeenCalled(); expect(chunks[0].status).toBe("accepted");
    expect(screen.queryByText("Indexed", { exact: true })).toBeNull();
  });

  it("requires real bulk-accept counts and retains pending chunks after an unconfirmed response", async () => {
    mocks.api.mockResolvedValueOnce({ message: "PRIVATE_ACCEPT_BODY" });
    const { user } = mount(); await ready(); await user.click(screen.getByRole("button", { name: "Accept all ≥ 0.85 (1)" }));
    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalledWith(expect.stringContaining("Bulk acceptance was not confirmed")));
    expect(mocks.toast.success).not.toHaveBeenCalled(); expect(chunks[0].status).toBe("pending"); expect(screen.queryByText(/PRIVATE_/)).toBeNull();
  });
});

describe("Chunk Review processing ownership and cancellation", () => {
  it("keeps reset and review disabled for read-only members", async () => {
    access = { ...access, role: "viewer", canEdit: false };
    const view = mount(); await ready(); disabled(selected());
    await view.user.click(screen.getByText("Document reset controls"));
    disabled(screen.getByRole("button", { name: "Reset all pages" }));
    expect(mocks.api).not.toHaveBeenCalled();
  });
  it("dismisses reset confirmation on permission loss and never replays it after access returns", async () => {
    const view = mount(); await ready(); await view.user.click(screen.getByText("Document reset controls"));
    await view.user.click(screen.getByRole("button", { name: "Reset all pages" }));
    await act(async () => { view.client.setQueryData(["rules", "workspace-1", "chunk-user", "access"], { ...access, role: "viewer", canEdit: false }); });
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    await act(async () => { view.client.setQueryData(["rules", "workspace-1", "chunk-user", "access"], access); });
    await waitFor(() => expect(screen.getByRole("button", { name: "Reset all pages" }).hasAttribute("disabled")).toBe(false));
    expect(screen.queryByRole("dialog")).toBeNull(); expect(mocks.api).not.toHaveBeenCalled();
  });
  it("aborts in-flight reset on permission loss and suppresses late success", async () => {
    const pending = deferred<unknown>(); mocks.api.mockReturnValueOnce(pending.promise);
    const view = mount(); await ready(); await view.user.click(screen.getByText("Document reset controls"));
    await view.user.click(screen.getByRole("button", { name: "Reset all pages" }));
    await view.user.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Reset pages" }));
    const signal: AbortSignal = mocks.api.mock.calls[0][1].signal;
    await act(async () => { view.client.setQueryData(["rules", "workspace-1", "chunk-user", "access"], { ...access, role: "viewer", canEdit: false }); });
    await waitFor(() => expect(signal.aborted).toBe(true));
    await act(async () => { pending.resolve({ chunksDeleted: 1, resetPages: [1, 2, 3] }); });
    expect(screen.queryByText(/Deleted 1 chunks/)).toBeNull();
  });
  it.each(["uploaded", "parsing", "chunking", "embedding"])("polls %s processing and stops all polling when it ends", async stage => {
    vi.useFakeTimers(); record = makeDocument({ status: stage === "embedding" ? "review" : stage, processingOperation: stage === "uploaded" ? null : stage });
    mount(); await act(async () => { await vi.advanceTimersByTimeAsync(10); });
    expect(mocks.get).toHaveBeenCalledTimes(5); // Guidance and access load once; only processing evidence polls while idle.
    await act(async () => { await vi.advanceTimersByTimeAsync(2000); }); expect(mocks.get).toHaveBeenCalledTimes(8);
    record = { ...record, status: "review", processingOperation: null };
    await act(async () => { await vi.advanceTimersByTimeAsync(2000); });
    const calls = mocks.get.mock.calls.length;
    await act(async () => { await vi.advanceTimersByTimeAsync(6000); }); expect(mocks.get).toHaveBeenCalledTimes(calls);
  });

  it("blocks every review and embedding control while server processing is active", async () => {
    record.processingOperation = "chunking";
    chunks = [makeChunk("clinical"), makeChunk("front", { type: "front_matter" }), makeChunk("accepted", { status: "accepted" }), makeChunk("embedded", { status: "embedded" })];
    const { user } = mount(); await ready();
    for (const input of pageInputs().getAllByRole("checkbox")) disabled(input);
    disabled(screen.getByLabelText("Chunker guidance")); disabled(screen.getByLabelText("Min confidence:"));
    await pendingView(user);
    for (const button of screen.getAllByRole("button", { name: /^(Accept|Reject|Reset|Embed|Chunk)/ })) disabled(button);
    await user.click(screen.getByRole("tab", { name: /^Reviewed/ }));
    for (const button of screen.getAllByRole("button", { name: /^(Reset|Embed)/ })) disabled(button);
    expect(mocks.api).not.toHaveBeenCalled();
  });

  it("polls committed checkpoints during one pending request and blocks same-render duplicate actions", async () => {
    vi.useFakeTimers(); const pending = deferred<unknown>(); mocks.api.mockReturnValueOnce(pending.promise);
    const { client } = mount(); await act(async () => { await vi.advanceTimersByTimeAsync(10); }); const button = selected();
    act(() => { fireEvent.click(button); fireEvent.click(button); }); expect(mocks.api).toHaveBeenCalledOnce();
    disabled(selected()); disabled(screen.getByRole("button", { name: /^Chunk All/ }));
    checkpoint([1, 2]); record.processingOperation = "chunking";
    await act(async () => { await vi.advanceTimersByTimeAsync(2000); });
    expect(mocks.get).toHaveBeenCalledTimes(9); // In-flight work also checks for permission loss.
    expect(client.getQueryData(["chunking-progress", mocks.workspaceId, mocks.docId, mocks.uid])).toMatchObject({ chunkedPages: 2 });
    // The polling request fires exactly at the interval boundary. Flush its
    // separately scheduled query notification before asserting the rendered UI.
    await act(async () => { await vi.advanceTimersByTimeAsync(1); });
    expect(screen.getByText("2/3 pages (66.7%)")).toBeTruthy(); expect(mocks.api).toHaveBeenCalledOnce();
    expect(screen.queryByText(/Chunking batch/)).toBeNull();
    record.processingOperation = null; checkpoint([1, 2, 3], "completed");
    await act(async () => { pending.resolve({ chunksCreated: 2, pagesChunked: 2, pagesSkipped: 0, failedPages: [] }); await vi.advanceTimersByTimeAsync(10); });
    expect(mocks.api).toHaveBeenCalledOnce();
  });

  it("cancels the transport, suppresses late completion, and retains saved checkpoints without starting another batch", async () => {
    const pending = deferred<unknown>(); mocks.api.mockReturnValueOnce(pending.promise);
    const { user } = mount(); await ready(); await user.click(selected());
    const signal = mocks.api.mock.calls[0][1].signal as AbortSignal;
    chunks.push(makeChunk("saved-2", { page: 2 })); checkpoint([1, 2]); record.processingOperation = "chunking";
    await user.click(screen.getByRole("button", { name: "Cancel request" })); await screen.findByText("2/3 pages (66.7%)");
    expect(signal.aborted).toBe(true); disabled(selected());
    await act(async () => { pending.resolve({ chunksCreated: 2, pagesChunked: 2, pagesSkipped: 0, failedPages: [] }); });
    expect(mocks.toast.success).not.toHaveBeenCalled(); expect(mocks.api).toHaveBeenCalledOnce();
    record = { ...record, status: "failed", processingOperation: null, failureStage: "chunking", failureReason: "Interrupted. Completed pages were kept." };
    await user.click(screen.getByRole("button", { name: "Refresh" })); await screen.findByText("Interrupted. Completed pages were kept.");
    expect(chunks.some(chunk => chunk.id === "saved-2")).toBe(true); expect(selected().textContent).toContain("(1)");
  });

  it.each(["chunking", "embedding", "review"])("aborts %s on unmount and ignores a transport that resolves after cancellation", async action => {
    const pending = deferred<unknown>(); mocks.api.mockReturnValueOnce(pending.promise);
    if (action === "embedding") chunks = [makeChunk("accepted", { status: "accepted" })];
    const view = mount(); await ready();
    if (action === "review") { await pendingView(view.user); await view.user.click(screen.getByRole("button", { name: "Accept chunk 1 on page 1" })); }
    else await view.user.click(action === "embedding" ? screen.getByRole("button", { name: "Embed (1)" }) : selected());
    const signal = mocks.api.mock.calls[0][1].signal as AbortSignal;
    const invalidate = vi.spyOn(view.client, "invalidateQueries"); view.unmount(); expect(signal.aborted).toBe(true);
    await act(async () => { pending.resolve(action === "embedding" ? { embedded: 1 } : action === "review" ? makeChunk("chunk-1", { status: "accepted" })
      : { chunksCreated: 2, pagesChunked: 2, pagesSkipped: 0, failedPages: [] }); });
    expect(mocks.toast.success).not.toHaveBeenCalled(); expect(mocks.toast.error).not.toHaveBeenCalled(); expect(invalidate).not.toHaveBeenCalled();
  });

  it("refreshes committed front-matter changes after a partial failure and never announces blanket success", async () => {
    chunks = ["front-1", "front-2", "front-3"].map(id => makeChunk(id, { type: "front_matter" }));
    const mutation = mocks.api.getMockImplementation()!;
    mocks.api.mockImplementation(async (path: string, init?: RequestInit) => {
      if (path.endsWith("front-2")) throw new ApiError(502, "<html>PRIVATE_UPSTREAM_ERROR</html>");
      return mutation(path, init);
    });
    const { user } = mount(); await ready(); await user.click(screen.getByRole("button", { name: "Reject Front Matter (3)" }));
    await screen.findByRole("button", { name: "Reject Front Matter (2)" }); expect(mocks.api).toHaveBeenCalledTimes(2);
    expect(chunks[0].status).toBe("rejected"); expect(chunks[2].status).toBe("pending"); expect(mocks.toast.success).not.toHaveBeenCalled();
    expect(mocks.toast.error).toHaveBeenCalledWith(expect.stringContaining("Some changes may have completed")); expect(screen.queryByText(/PRIVATE_/)).toBeNull();
  });

  it("does not send the next front-matter PATCH after cancellation even if the first ignores its token", async () => {
    chunks = ["front-1", "front-2"].map(id => makeChunk(id, { type: "front_matter" }));
    const pending = deferred<unknown>(); mocks.api.mockReturnValueOnce(pending.promise);
    const { user } = mount(); await ready(); await user.click(screen.getByRole("button", { name: "Reject Front Matter (2)" }));
    await user.click(screen.getByRole("button", { name: "Cancel request" }));
    expect((mocks.api.mock.calls[0][1].signal as AbortSignal).aborted).toBe(true);
    chunks[0] = { ...chunks[0], status: "rejected" }; await act(async () => { pending.resolve(chunks[0]); });
    expect(mocks.api).toHaveBeenCalledOnce(); expect(mocks.toast.success).not.toHaveBeenCalled();
    await user.click(screen.getByRole("button", { name: "Refresh" })); await screen.findByRole("button", { name: "Reject Front Matter (1)" });
  });

  it.each(["workspace", "document", "user"])("isolates caches and cancels late callbacks after %s replacement", async identity => {
    chunks[0].text = "PRIVATE_OLD_CHUNK"; const pending = deferred<unknown>(); mocks.api.mockReturnValueOnce(pending.promise);
    const view = mount(); await ready(); fireEvent.change(screen.getByLabelText("Chunker guidance"), { target: { value: "PRIVATE_OLD_GUIDANCE" } });
    await view.user.click(selected()); const signal = mocks.api.mock.calls[0][1].signal as AbortSignal;
    if (identity === "workspace") mocks.workspaceId = "workspace-2";
    if (identity === "document") mocks.docId = "document-2";
    if (identity === "user") mocks.uid = "different-user";
    record = makeDocument({ fileName: "New identity guideline.pdf" }); chunks = [makeChunk("new-identity")]; checkpoint([1]); view.rerender();
    await screen.findByText(/New identity guideline.pdf/); expect(signal.aborted).toBe(true);
    await act(async () => { pending.resolve({ chunksCreated: 2, pagesChunked: 2, pagesSkipped: 0, failedPages: [] }); });
    expect(screen.queryByText(/PRIVATE_OLD/)).toBeNull(); expect((screen.getByLabelText("Chunker guidance") as HTMLTextAreaElement).value).not.toContain("PRIVATE_OLD");
    expect(view.client.getQueryData(["chunks", mocks.workspaceId, mocks.docId, mocks.uid])).toEqual(chunks);
    expect(mocks.toast.success).not.toHaveBeenCalled();
  });
});

describe("Chunk Review read failures and reviewed subsets", () => {
  it("shows an initial read failure rather than invented zero counts or completed empty review", async () => {
    mocks.get.mockRejectedValue(new ApiError(502, "<html>PRIVATE_READ_BODY</html>")); mount();
    const alert = await screen.findByRole("alert"); expect(alert.textContent).toContain("Refresh before reviewing");
    expect(screen.queryByRole("progressbar")).toBeNull(); expect(screen.queryByText(/No pending chunks/)).toBeNull(); expect(screen.queryByText(/PRIVATE_/)).toBeNull();
  });

  it("retains stale evidence but disables every mutation until all reads refresh successfully", async () => {
    const get = mocks.get.getMockImplementation()!; const { user } = mount(); await ready();
    mocks.get.mockRejectedValue(new ApiError(502, "<html>PRIVATE_READ_BODY</html>")); await user.click(screen.getByRole("button", { name: "Refresh" }));
    await screen.findByText(/Displayed statuses and counts may be out of date/); disabled(selected());
    expect(screen.getByText("1/3 pages (33.3%)")).toBeTruthy(); await pendingView(user); disabled(screen.getByRole("button", { name: "Accept chunk 1 on page 1" }));
    expect(screen.queryByText(/PRIVATE_/)).toBeNull(); mocks.get.mockImplementation(get); await user.click(screen.getByRole("button", { name: "Refresh" }));
    await waitFor(() => expect(selected().hasAttribute("disabled")).toBe(false));
  });

  it.each([{ totalPages: 3, chunkedPages: -1, chunkedPageNumbers: [] }, { totalPages: 3, chunkedPages: 2, chunkedPageNumbers: [1, 1] },
    { totalPages: 0, chunkedPages: 1, chunkedPageNumbers: [1] }, { totalPages: 3, chunkedPages: 1, chunkedPageNumbers: [0] }])("refuses invalid saved page evidence (%j)", async invalid => {
    const get = mocks.get.getMockImplementation()!;
    mocks.get.mockImplementation(async (path: string) => path.endsWith("/chunking-progress") ? { ...invalid, chunkingStatus: "completed" } : get(path));
    mount(); await screen.findByRole("alert"); expect(screen.queryByRole("progressbar")).toBeNull(); expect(mocks.api).not.toHaveBeenCalled();
  });

  it.each(["document", "chunk"])("refuses a foreign %s response without rendering its text", async foreign => {
    if (foreign === "document") record = { ...record, id: "foreign-document", fileName: "PRIVATE_FOREIGN_FILE" };
    else chunks = [makeChunk("foreign", { workspaceId: "foreign-workspace", text: "PRIVATE_FOREIGN_CHUNK" })];
    mount(); await screen.findByRole("alert"); expect(screen.queryByText(/PRIVATE_FOREIGN/)).toBeNull(); expect(mocks.api).not.toHaveBeenCalled();
  });

  it("indexes only a fully reviewed embedded subset, keeps embedded chunks visible and resets them back to pending", async () => {
    chunks = [makeChunk("accepted", { status: "accepted" }), makeChunk("rejected", { status: "rejected", page: 2 })]; checkpoint([1, 2]);
    const { user } = mount(); await ready(); expect(screen.queryByText("Indexed", { exact: true })).toBeNull();
    await user.click(screen.getByRole("button", { name: "Embed (1)" })); await screen.findByText("Indexed", { exact: true });
    expect(screen.getByText("2/3 pages (66.7%)")).toBeTruthy(); disabled(screen.getByRole("button", { name: "Embed (0)" }));
    await user.click(screen.getByRole("tab", { name: "Reviewed (2)" }));
    const embedded = screen.getByRole("article", { name: "chunk 1 on page 1" });
    expect(within(embedded).getByText("embedded", { exact: true })).toBeTruthy();
    expect(within(embedded).getByText(/Resetting removes this chunk from the index/)).toBeTruthy();
    await user.click(within(embedded).getByRole("button", { name: "Reset to Pending chunk 1 on page 1" }));
    await waitFor(() => expect(screen.queryByText("Indexed", { exact: true })).toBeNull());
    expect(chunks[0].status).toBe("pending"); expect(screen.getByRole("tab", { name: "Pending Chunks (1)" })).toBeTruthy();
  });

  it("does not treat unknown verbatim checks or invalid scores as evidence and expands only the chosen passage", async () => {
    chunks = [makeChunk("unknown", { verbatim: undefined, confidence: Number.POSITIVE_INFINITY, text: "First exact passage. ".repeat(20) }),
      makeChunk("second", { page: 2, text: "Second exact passage. ".repeat(20) })];
    const { user } = mount(); await ready(); await pendingView(user);
    const first = screen.getByRole("article", { name: "chunk 1 on page 1" });
    expect(within(first).getByText("Verbatim check unknown")).toBeTruthy(); expect(within(first).getByText("N/A")).toBeTruthy();
    expect(within(first).queryByText("Verbatim ✓")).toBeNull();
    await user.click(within(first).getByRole("button", { name: /^Show more/ })); expect(within(first).getByRole("button", { name: /^Show less/ }).getAttribute("aria-expanded")).toBe("true");
    expect(screen.getByRole("button", { name: "Show more of chunk 2 on page 2" }).getAttribute("aria-expanded")).toBe("false");
    await user.click(within(first).getByRole("button", { name: /^Show less/ })); expect(within(first).getByRole("button", { name: /^Show more/ })).toBeTruthy();
  });

  it("resets filter pagination so a matching first-page result cannot appear empty", async () => {
    chunks = Array.from({ length: 6 }, (_, index) => makeChunk(`chunk-${index + 1}`, { page: index === 5 ? 2 : 1 }));
    const { user } = mount(); await ready(); await pendingView(user); await user.click(screen.getByRole("button", { name: "Next" }));
    expect(screen.getByText("Page 2 of 2")).toBeTruthy(); await user.selectOptions(screen.getByLabelText("Page:"), "1");
    expect(screen.getAllByRole("article")).toHaveLength(5); expect(screen.queryByText("No pending chunks to review.")).toBeNull();
    expect(screen.getByLabelText("Min score:")).toBeTruthy(); expect(screen.getByLabelText("Type:")).toBeTruthy(); expect(screen.getByLabelText("Group by:")).toBeTruthy();
  });
});
