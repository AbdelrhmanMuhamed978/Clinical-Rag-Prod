import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import DocumentsPage from "@/app/(app)/w/[workspaceId]/documents/page";
import { ApiError } from "@/lib/api";
import { canRetryParse, DOCUMENT_STATUSES, documentsQueryKey } from "@/lib/documents";
import type { Document } from "@/lib/types";
import { deferred } from "./analysis-fixtures";
import { seriousViolations } from "./axe";
import { control, tabTo } from "./keyboard";

const mocks = vi.hoisted(() => ({ api: vi.fn(), uid: "documents-user", workspaceId: "workspace-1", toast: { error: vi.fn(), success: vi.fn(), warning: vi.fn() } }));
vi.mock("@/lib/api", () => ({ api: mocks.api, ApiError: class extends Error { constructor(public status: number, message: string) { super(message); } } }));
vi.mock("next/navigation", () => ({ useParams: () => ({ workspaceId: mocks.workspaceId }) }));
vi.mock("@/providers/AuthProvider", () => ({ useAuth: () => ({ user: { uid: mocks.uid } }) }));
vi.mock("sonner", () => ({ toast: mocks.toast }));

let documents: Document[];
const clients: QueryClient[] = [];
const route = "/api/workspaces/workspace-1/documents";
function document(status = "uploaded", changes: Partial<Document> = {}): Document {
  return {
    id: `doc-${status}`, workspaceId: "workspace-1", fileName: `${status}.pdf`, status,
    society: "Synthetic Society", year: 2024, guidelineStatus: "draft", revision: 1,
    parsedTextLength: status === "uploaded" || status === "parsing" || status === "failed" ? 0 : 100,
    pageCount: 10, chunkedPagesCount: 0, processingOperation: null,
    chunkCounts: { total: 0, pending: 0, accepted: 0, rejected: 0, embedded: 0, other: 0 },
    createdAt: "2026-11-15T00:00:00Z", updatedAt: "2026-11-15T00:00:00Z", ...changes,
  };
}
function mount() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Infinity, refetchOnWindowFocus: false } } });
  clients.push(client);
  const view = render(<QueryClientProvider client={client}><DocumentsPage /></QueryClientProvider>);
  return { ...view, client, user: userEvent.setup() };
}
function card(fileName: string) { return screen.getByRole("heading", { name: fileName }).closest('[data-slot="card"]') as HTMLElement; }
async function uploadForm(user: ReturnType<typeof userEvent.setup>) {
  await user.click(screen.getByRole("button", { name: "Upload guideline" }));
  const dialog = screen.getByRole("dialog", { name: "Upload guideline" });
  await user.upload(within(dialog).getByLabelText("PDF (required, up to 50 MB)"), new File(["%PDF-1.7 synthetic"], "guideline.pdf", { type: "application/pdf" }));
  await user.type(within(dialog).getByLabelText("Publishing society / organization (required)"), "  Synthetic Society  ");
  await user.type(within(dialog).getByLabelText("Guideline year (required, 1900–2100)"), "2024");
  return dialog;
}

beforeEach(() => {
  documents = []; mocks.uid = "documents-user"; mocks.workspaceId = "workspace-1";
  mocks.api.mockReset();
  mocks.api.mockImplementation(async (path: string, init?: RequestInit) => {
    if (!init?.method) return path === route ? documents.map(doc => ({ ...doc })) : [];
    if (path === route && init.method === "POST") {
      const doc = document("uploaded", { id: "uploaded-1", fileName: "guideline.pdf" });
      documents = [doc, ...documents]; return doc;
    }
    if (init.method === "DELETE") { documents = documents.filter(doc => !path.endsWith(doc.id)); return { deleted: true }; }
    if (path.endsWith("/parse")) { documents = documents.map(doc => path.includes(doc.id) ? { ...doc, status: "parsed", parsedTextLength: 100 } : doc); return { parsedTextLength: 100 }; }
    throw new Error(`Unexpected mutation: ${path}`);
  });
});
afterEach(() => { clients.splice(0).forEach(client => client.clear()); vi.useRealTimers(); });

describe("truthful document processing", () => {
  it("has no serious or critical structural axe issues", async () => {
    documents = [document("indexed", { chunkCounts: { total: 2, pending: 0, accepted: 0, rejected: 0, embedded: 2, other: 0 } })];
    const view = mount(); await screen.findByRole("heading", { name: "indexed.pdf" });
    expect(await seriousViolations(view.container, "documents")).toEqual([]);
  });

  it("opens and dismisses the upload dialog from the keyboard alone", async () => {
    const { user } = mount(); await screen.findByText("No documents yet.");
    const trigger = await tabTo(user, control("Upload guideline", "BUTTON"), "Upload guideline");
    await user.keyboard("{Enter}");
    const dialog = await screen.findByRole("dialog", { name: "Upload guideline" });
    expect(dialog.hasAttribute("open")).toBe(true);
    expect(trigger.getAttribute("aria-expanded")).toBeNull();
    await tabTo(user, element => element.tagName === "BUTTON" && !!element.textContent?.startsWith("Cancel"), "Cancel upload");
    await user.keyboard("{Enter}");
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });

  it("renders all seven distinct status classes, metadata and counts without claiming automatic chunking", async () => {
    documents = Object.keys(DOCUMENT_STATUSES).map(status => document(status, status === "indexed"
      ? { chunkCounts: { total: 3, pending: 0, accepted: 0, rejected: 1, embedded: 2, other: 0 } }
      : status === "review" ? { chunkCounts: { total: 8, pending: 2, accepted: 3, rejected: 1, embedded: 2, other: 0 } }
      : {}));
    mount(); await screen.findByRole("heading", { name: "indexed.pdf" });
    for (const [status, display] of Object.entries(DOCUMENT_STATUSES)) {
      const badge = within(card(`${status}.pdf`)).getByText(display.label, { exact: true });
      expect(screen.getByRole("group", { name: `${status}.pdf` })).toBe(card(`${status}.pdf`));
      for (const color of display.color.split(" ")) expect(badge.classList.contains(color)).toBe(true);
    }
    const review = card("review.pdf");
    for (const [label, count] of [["Total chunks", "8"], ["Pending review", "2"], ["Accepted (awaiting embedding)", "3"], ["Rejected", "1"], ["Embedded", "2"]])
      expect(within(review).getByText(label).nextElementSibling?.textContent).toBe(count);
    expect(within(review).getByText("DRAFT", { exact: true })).toBeTruthy();
    expect(screen.getByText("Upload -> Parse -> choose pages -> Review -> Embed")).toBeTruthy();
    expect(screen.queryByText(/parsed and chunked automatically/i)).toBeNull();
    expect(screen.getAllByRole("button", { name: "Retry Parse" })).toHaveLength(1);
    expect(within(card("uploaded.pdf")).queryByRole("button", { name: /parse/i })).toBeNull();
  });

  it("surfaces image-only pages as needs OCR instead of implying full text extraction", async () => {
    documents = [document("parsed", { needsOcrPages: [7, 9] })];
    mount();
    await screen.findByRole("heading", { name: "parsed.pdf" });
    const notice = within(card("parsed.pdf")).getByRole("status");
    expect(notice.textContent).toContain("Needs OCR: pages 7, 9");
    expect(notice.textContent).toContain("not indexed");
    expect(notice.textContent).toContain("OCR is not included");
  });

  it.each(["uploaded", "parsing", "chunking", "embedding"])("polls %s work and stops when server processing ends", async status => {
    vi.useFakeTimers();
    documents = [document(status === "embedding" ? "review" : status, { processingOperation: status === "uploaded" ? null : status })];
    mount(); await act(async () => { await vi.advanceTimersByTimeAsync(10); });
    expect(mocks.api).toHaveBeenCalledOnce();
    await act(async () => { await vi.advanceTimersByTimeAsync(2000); });
    expect(mocks.api).toHaveBeenCalledTimes(2);
    documents = [{ ...documents[0], status: "parsed", parsedTextLength: 100, processingOperation: null }];
    await act(async () => { await vi.advanceTimersByTimeAsync(2000); });
    expect(screen.getByText("Parsed", { exact: true })).toBeTruthy();
    const calls = mocks.api.mock.calls.length;
    await act(async () => { await vi.advanceTimersByTimeAsync(6000); });
    expect(mocks.api).toHaveBeenCalledTimes(calls);
  });

  it.each(["parsed", "review", "indexed", "failed"])("does not poll idle %s documents", async status => {
    vi.useFakeTimers();
    documents = [document(status, status === "indexed" ? { chunkCounts: { total: 1, pending: 0, accepted: 0, rejected: 0, embedded: 1, other: 0 } } : {})];
    mount(); await act(async () => { await vi.advanceTimersByTimeAsync(6000); });
    expect(mocks.api).toHaveBeenCalledOnce();
  });

  it.each([undefined, { total: 0, pending: 0, accepted: 0, rejected: 0, embedded: 0, other: 0 },
    { total: 0, pending: 0, accepted: 0, rejected: 0, embedded: 1, other: 0 },
    { total: 1, pending: 0, accepted: 0, rejected: -1, embedded: 2, other: 0 },
    { total: 1, pending: 0, accepted: 0, rejected: 0.5, embedded: 0.5, other: 0 },
    { total: 2, pending: 0, accepted: 0, rejected: 0, embedded: 1, other: 0 },
    { total: 1, pending: 0, accepted: 0, rejected: 0, embedded: 1, other: Number.NaN },
    { total: 2, pending: 1, accepted: 0, rejected: 0, embedded: 1, other: 0 },
    { total: 2, pending: 0, accepted: 1, rejected: 0, embedded: 1, other: 0 },
    { total: 2, pending: 0, accepted: 0, rejected: 0, embedded: 1, other: 1 }])("does not repeat unsupported indexed assertions (%j)", async counts => {
    documents = [document("indexed", { chunkCounts: counts })]; mount();
    await screen.findByRole("heading", { name: "indexed.pdf" });
    expect(within(card("indexed.pdf")).getByText("Review", { exact: true })).toBeTruthy();
    expect(within(card("indexed.pdf")).queryByText("Indexed", { exact: true })).toBeNull();
  });

  it.each(["parsing", "chunking", "embedding"])("uses the active %s server operation rather than an old indexed assertion", async operation => {
    documents = [document("indexed", { processingOperation: operation, chunkCounts: { total: 1, pending: 0, accepted: 0, rejected: 0, embedded: 1, other: 0 } })];
    mount(); await screen.findByRole("heading", { name: "indexed.pdf" });
    expect(within(card("indexed.pdf")).queryByText("Indexed", { exact: true })).toBeNull();
    expect(within(card("indexed.pdf")).getByText(operation === "embedding" ? "Review" : operation === "parsing" ? "Parsing" : "Chunking", { exact: true })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Delete" }).hasAttribute("disabled")).toBe(true);
  });

  it("does not describe an active parse as still queued", async () => {
    documents = [document("uploaded", { processingOperation: "parsing" })];
    mount(); await screen.findByRole("heading", { name: "uploaded.pdf" });
    expect(within(card("uploaded.pdf")).getByText("Parsing", { exact: true })).toBeTruthy();
    expect(screen.queryByText("Queued for automatic parsing on the server.")).toBeNull();
  });

  it("shows persisted failure reasons and offers retry only for failed, unparsed documents", async () => {
    documents = [document("failed", { failureStage: "parsing", failureReason: "Scanned pages need OCR. Retry Parse." }),
      document("failed", { id: "chunk-failure", fileName: "chunk-failure.pdf", parsedTextLength: 100, failureStage: "chunking", failureReason: "Completed pages were kept." }),
      document("failed", { id: "embedding-failure", fileName: "embedding-failure.pdf", failureStage: "embedding" })];
    const { user } = mount(); await screen.findByText("Scanned pages need OCR. Retry Parse.");
    expect(screen.getAllByRole("button", { name: "Retry Parse" })).toHaveLength(1);
    await user.click(screen.getByRole("button", { name: "Retry Parse" }));
    await waitFor(() => expect(mocks.api.mock.calls.filter(([path]) => path.endsWith("/parse"))).toHaveLength(1));
    await waitFor(() => expect(within(card("failed.pdf")).getByText("Parsed", { exact: true })).toBeTruthy());
    expect(canRetryParse(document("failed", { chunkCounts: { total: 1, pending: 1, accepted: 0, rejected: 0, embedded: 0, other: 0 } }))).toBe(false);
  });

  it("uploads required metadata once and leaves automatic parsing to the durable server queue", async () => {
    const { user } = mount(); await screen.findByText("No documents yet.");
    const dialog = await uploadForm(user);
    expect((within(dialog).getByRole("radio", { name: "DRAFT" }) as HTMLInputElement).checked).toBe(true);
    await user.click(within(dialog).getByRole("radio", { name: "FINAL" }));
    await user.click(within(dialog).getByRole("button", { name: "Upload and parse" }));
    await screen.findByRole("heading", { name: "guideline.pdf" });
    const mutations = mocks.api.mock.calls.filter(([, init]) => init?.method);
    expect(mutations).toHaveLength(1);
    expect(mutations[0][0]).toBe(route);
    const body = mutations[0][1].body as FormData;
    expect(body.get("society")).toBe("Synthetic Society"); expect(body.get("year")).toBe("2024"); expect(body.get("guidelineStatus")).toBe("final");
    expect((body.get("file") as File).name).toBe("guideline.pdf");
    expect(screen.getByText("Queued for automatic parsing on the server.")).toBeTruthy();
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(mocks.toast.success).toHaveBeenCalledWith("PDF uploaded. Parsing will start automatically.");
  });

  it("uses native required/range validation before uploading", async () => {
    const { user } = mount(); const dialog = await uploadForm(user);
    const year = within(dialog).getByLabelText("Guideline year (required, 1900–2100)") as HTMLInputElement;
    await user.clear(year); await user.type(year, "2101");
    await user.click(within(dialog).getByRole("button", { name: "Upload and parse" }));
    expect(year.validity.rangeOverflow).toBe(true);
    expect(mocks.api.mock.calls.filter(([, init]) => init?.method)).toHaveLength(0);
  });

  it("shows safe upload-error feedback and retains metadata for correction", async () => {
    mocks.api.mockImplementation(async (_path: string, init?: RequestInit) => {
      if (init?.method === "POST") throw new ApiError(400, JSON.stringify({ error: "The selected file is not a PDF." }));
      return [];
    });
    const { user } = mount(); const dialog = await uploadForm(user);
    await user.click(within(dialog).getByRole("button", { name: "Upload and parse" }));
    await screen.findByRole("alert");
    expect(screen.getByRole("alert").textContent).toBe("The selected file is not a PDF.");
    expect(mocks.toast.error).toHaveBeenCalledWith("The selected file is not a PDF.");
    expect((within(dialog).getByLabelText("Guideline year (required, 1900–2100)") as HTMLInputElement).value).toBe("2024");
  });

  it.each([400, 409, 500, 502])("never displays arbitrary structured upload errors (%i)", async status => {
    mocks.api.mockImplementation(async (_path: string, init?: RequestInit) => {
      if (init?.method === "POST") throw new ApiError(status, JSON.stringify({ error: "PRIVATE_UPSTREAM_ERROR_MARKER" }));
      return [];
    });
    const { user } = mount(); const dialog = await uploadForm(user);
    await user.click(within(dialog).getByRole("button", { name: "Upload and parse" }));
    const fallback = "The upload could not be confirmed. Refresh to check whether the PDF was saved before trying again.";
    await waitFor(() => expect(screen.getByRole("alert").textContent).toBe(fallback));
    expect(mocks.toast.error).toHaveBeenCalledWith(fallback); expect(screen.queryByText(/PRIVATE_UPSTREAM_ERROR_MARKER/)).toBeNull();
    expect((within(dialog).getByLabelText("Publishing society / organization (required)") as HTMLInputElement).value).toBe("  Synthetic Society  ");
    expect((within(dialog).getByLabelText("Guideline year (required, 1900–2100)") as HTMLInputElement).value).toBe("2024");
    expect(mocks.toast.success).not.toHaveBeenCalled();
  });

  it("cancels an upload without a late callback or duplicate parse, then can discover a server-saved upload", async () => {
    const pending = deferred<Document>(); let signal!: AbortSignal;
    mocks.api.mockImplementation(async (_path: string, init?: RequestInit) => {
      if (init?.method === "POST") { signal = init.signal as AbortSignal; return pending.promise; }
      return documents;
    });
    const { user } = mount(); const dialog = await uploadForm(user);
    await user.click(within(dialog).getByRole("button", { name: "Upload and parse" }));
    await user.click(within(dialog).getByRole("button", { name: "Cancel upload" }));
    expect(signal.aborted).toBe(true);
    const saved = document("uploaded", { fileName: "server-saved.pdf" }); documents = [saved];
    await act(async () => { pending.resolve(saved); });
    expect(mocks.toast.success).not.toHaveBeenCalled();
    await user.click(screen.getByRole("button", { name: "Refresh" }));
    await screen.findByRole("heading", { name: "server-saved.pdf" });
    expect(mocks.api.mock.calls.filter(([, init]) => init?.method)).toHaveLength(1);
    expect(screen.getByText("Queued for automatic parsing on the server.")).toBeTruthy();
  });

  it("requires confirmation before deleting every chunk and allows cancellation", async () => {
    documents = [document("review", { chunkCounts: { total: 4, pending: 1, accepted: 1, rejected: 1, embedded: 1, other: 0 } })];
    const { user } = mount(); await screen.findByRole("heading", { name: "review.pdf" });
    await user.click(screen.getByRole("button", { name: "Delete" }));
    let dialog = screen.getByRole("dialog", { name: "Delete this document?" });
    expect(dialog.textContent).toContain("all pending, accepted, rejected and embedded chunks");
    expect(mocks.api.mock.calls.filter(([, init]) => init?.method === "DELETE")).toHaveLength(0);
    await user.click(within(dialog).getByRole("button", { name: "Cancel" }));
    expect(screen.queryByRole("dialog")).toBeNull();
    await user.click(screen.getByRole("button", { name: "Delete" })); dialog = screen.getByRole("dialog");
    await user.click(within(dialog).getByRole("button", { name: "Delete document" }));
    await screen.findByText("No documents yet.");
    expect(mocks.api.mock.calls.filter(([, init]) => init?.method === "DELETE")).toHaveLength(1);
  });

  it("blocks deleting active server work", async () => {
    documents = [document("parsing")]; mount(); await screen.findByRole("heading", { name: "parsing.pdf" });
    expect(screen.getByRole("button", { name: "Delete" }).hasAttribute("disabled")).toBe(true);
  });

  it("isolates document caches and aborts late upload results after an identity change", async () => {
    const pending = deferred<Document>(); let signal!: AbortSignal;
    mocks.api.mockImplementation(async (path: string, init?: RequestInit) => {
      if (init?.method === "POST") { signal = init.signal as AbortSignal; return pending.promise; }
      return path === route && mocks.uid === "documents-user" ? documents : [];
    });
    const { user, client, rerender } = mount(); const dialog = await uploadForm(user);
    await user.click(within(dialog).getByRole("button", { name: "Upload and parse" }));
    mocks.uid = "different-user";
    rerender(<QueryClientProvider client={client}><DocumentsPage /></QueryClientProvider>);
    await screen.findByText("No documents yet."); expect(signal.aborted).toBe(true);
    await act(async () => { pending.resolve(document("uploaded", { fileName: "PRIVATE_OLD_UPLOAD.pdf" })); });
    expect(screen.queryByText("PRIVATE_OLD_UPLOAD.pdf")).toBeNull();
    expect(client.getQueryData(documentsQueryKey("workspace-1", "different-user"))).toEqual([]);
    expect(mocks.toast.success).not.toHaveBeenCalled();
  });

  it("retains truthful stale-status feedback after a refresh error", async () => {
    documents = [document("parsed")]; const { user } = mount(); await screen.findByRole("heading", { name: "parsed.pdf" });
    mocks.api.mockRejectedValue(new ApiError(502, "<html>PRIVATE_UPSTREAM_BODY</html>"));
    await user.click(screen.getByRole("button", { name: "Refresh" })); await screen.findByRole("alert");
    expect(screen.getByRole("alert").textContent).toContain("Displayed statuses may be out of date");
    expect(screen.queryByText(/PRIVATE_UPSTREAM_BODY/)).toBeNull();
    expect(screen.getByText("Parsed", { exact: true })).toBeTruthy();
  });
});
