import { describe, expect, it, vi } from "vitest";
import { ApiError } from "@/lib/api";
import { documentActionError } from "@/lib/documents";

vi.mock("@/lib/api", () => ({ ApiError: class extends Error { constructor(public status: number, message: string) { super(message); } } }));

const fallback = "No completion was confirmed. Refresh before retrying.";
describe("document action error privacy boundary", () => {
  it.each([
    { error: "PRIVATE_UPSTREAM_ERROR_MARKER" },
    { error: "The selected file is not a PDF. PRIVATE_MARKER" },
    { error: "Invalid page numbers: 2, PRIVATE_MARKER" },
    { error: "Error: PRIVATE_STACK_TRACE\n at PRIVATE_FILE_PATH" },
    { error: "<html>PRIVATE_BODY</html>" },
    { error: 42 }, { message: "PRIVATE_UPSTREAM_ERROR_MARKER" }, null, [],
  ])("rejects non-contract error text (%j)", value => {
    expect(documentActionError(new ApiError(502, JSON.stringify(value)), fallback)).toBe(fallback);
  });

  it.each([
    "The selected file is not a PDF.",
    "This document is being processed or changed. Refresh before trying again.",
    "Some selected pages already contain reviewed chunks. Reset their review before replacing them.",
    "Chunking was interrupted or the service failed. Completed pages were kept. Choose the remaining pages to retry.",
    "The embedding service returned invalid or mismatched vectors. No chunks from this request were marked embedded. Retry Embed.",
    "No extractable text was found or the parsing response was invalid. Check the PDF; scanned pages may need OCR before upload. Retry Parse.",
  ])("preserves fixed backend-owned guidance (%s)", error => {
    expect(documentActionError(new ApiError(400, JSON.stringify({ error })), fallback)).toBe(error);
  });

  it("replaces a strictly numeric invalid-page list with fixed actionable feedback", () => {
    expect(documentActionError(new ApiError(400, JSON.stringify({ error: "Invalid page numbers: -1, 0, 7" })), fallback))
      .toBe("Choose page numbers that exist in this document.");
  });

  it.each([
    [401, "Sign in again before trying this action."],
    [403, "Editor access to this workspace is required for this action."],
    [413, "Choose a PDF of at most 50 MB."],
  ])("keeps status-based feedback independent of the response body (%i)", (status, message) => {
    expect(documentActionError(new ApiError(status as number, '{"error":"PRIVATE_MARKER"}'), fallback)).toBe(message);
  });

  it("fails closed on malformed, oversized and non-API errors", () => {
    for (const error of [new ApiError(500, "PRIVATE_TEXT"), new ApiError(500, "x".repeat(2001)), new Error("PRIVATE_TEXT"), undefined])
      expect(documentActionError(error, fallback)).toBe(fallback);
  });
});
