import { ApiError } from "./api";
import type { Document } from "./types";

export const DOCUMENT_STATUSES = {
  uploaded: { label: "Uploaded", color: "bg-slate-50 border-slate-200 text-slate-700" },
  parsing: { label: "Parsing", color: "bg-blue-50 border-blue-200 text-blue-700" },
  parsed: { label: "Parsed", color: "bg-indigo-50 border-indigo-200 text-indigo-700" },
  chunking: { label: "Chunking", color: "bg-amber-50 border-amber-200 text-amber-800" },
  review: { label: "Review", color: "bg-teal-50 border-teal-200 text-teal-800" },
  indexed: { label: "Indexed", color: "bg-green-50 border-green-200 text-green-800" },
  failed: { label: "Failed", color: "bg-rose-50 border-rose-200 text-rose-700" },
} as const;

export type DocumentStatus = keyof typeof DOCUMENT_STATUSES;
export const DOCUMENT_POLL_MILLISECONDS = 2000;
export const documentsQueryKey = (workspaceId: string, userId: string) => ["documents", workspaceId, userId] as const;

export function documentIsBusy(document: Pick<Document, "status" | "processingOperation">): boolean {
  return document.status === "parsing" || document.status === "chunking" || !!document.processingOperation;
}

export function documentPollInterval(documents?: readonly Pick<Document, "status" | "processingOperation">[]): number | false {
  return documents?.some(document => document.status === "uploaded" || documentIsBusy(document)) ? DOCUMENT_POLL_MILLISECONDS : false;
}

export function documentStatus(document: Document): { label: string; color: string } {
  if (document.processingOperation === "parsing" || document.processingOperation === "chunking")
    return DOCUMENT_STATUSES[document.processingOperation];
  if (document.processingOperation === "embedding") return DOCUMENT_STATUSES.review;
  // Do not repeat an old/contradictory client's "indexed" assertion without counts.
  const counts = document.chunkCounts;
  const validCounts = counts && [counts.total, counts.pending, counts.accepted, counts.rejected, counts.embedded, counts.other]
    .every(count => Number.isSafeInteger(count) && count >= 0)
    && counts.total === counts.pending + counts.accepted + counts.rejected + counts.embedded + counts.other;
  if (document.status === "indexed" && (!validCounts || !counts || counts.embedded === 0 || counts.pending > 0 || counts.accepted > 0 || counts.other > 0))
    return DOCUMENT_STATUSES.review;
  return DOCUMENT_STATUSES[document.status as DocumentStatus] ?? { label: "Unknown status", color: DOCUMENT_STATUSES.uploaded.color };
}

export function canRetryParse(document: Document): boolean {
  return document.status === "failed" && !documentIsBusy(document)
    && (document.parsedTextLength ?? document.parsedText?.length ?? 0) === 0
    && (document.chunkCounts?.total ?? 0) === 0
    && (!document.failureStage || document.failureStage === "parsing");
}

// These are fixed messages owned by DocumentsController, ChunkReviewController
// and DocumentParser, not arbitrary strings returned by a proxy/model service.
const documentErrorMessages = new Set([
  "Choose a non-empty PDF of at most 50 MB.",
  "Enter a publishing society (1–256 characters), a year from 1900 to 2100, and FINAL or DRAFT status.",
  "The selected file is not a PDF.",
  "The PDF could not be uploaded. Try again.",
  "This document is being processed or changed. Refresh before trying again.",
  "This document is being processed or changed. Refresh before reviewing again.",
  "This document is already parsed. Choose pages and review chunks instead of parsing again.",
  "Choose 1–1000 pages, a batch size of 1–20, and 100–2000 tokens. Guidance must be at most 4000 characters.",
  "Some selected pages already contain reviewed chunks. Reset their review before replacing them.",
  "Document status is set by parsing, chunk review and embedding, not by the client.",
  "Review status must be pending, accepted or rejected. Embedding is a server operation.",
  "Chunk text cannot be empty; section must be at most 256 characters.",
  "Confidence must be between 0 and 1.",
  "Invalid chunks. Imported chunks cannot be marked embedded.",
  "The uploaded file is unavailable. Delete this document and upload the PDF again.",
  "No extractable text was found or the parsing response was invalid. Check the PDF; scanned pages may need OCR before upload. Retry Parse.",
  "Parsing was interrupted or timed out. Retry Parse.",
  "The PDF parsing service is unavailable. Retry Parse when the service is available.",
  "The PDF could not be parsed. Check the PDF and retry Parse.",
  "Chunking was interrupted or the service failed. Completed pages were kept. Choose the remaining pages to retry.",
  "The embedding service returned invalid or mismatched vectors. No chunks from this request were marked embedded. Retry Embed.",
  "Embedding was interrupted or timed out. No chunks from this request were marked embedded. Retry Embed.",
  "Embedding failed. No chunks from this request were marked embedded. Retry Embed when the service is available.",
]);

export function documentActionError(cause: unknown, fallback: string): string {
  if (!(cause instanceof ApiError)) return fallback;
  if (cause.status === 403) return "Editor access to this workspace is required for this action.";
  if (cause.status === 401) return "Sign in again before trying this action.";
  if (cause.status === 413) return "Choose a PDF of at most 50 MB.";
  if (cause.message.length > 2000) return fallback;
  try {
    const value: unknown = cause.details ?? JSON.parse(cause.message);
    if (typeof value === "object" && value !== null && "error" in value
      && typeof value.error === "string") {
      if (documentErrorMessages.has(value.error)) return value.error;
      if (/^Invalid page numbers: -?\d+(?:, -?\d+)*$/.test(value.error)) return "Choose page numbers that exist in this document.";
    }
  } catch { /* Never display an HTML error page, stack trace, or upstream response body. */ }
  return fallback;
}
