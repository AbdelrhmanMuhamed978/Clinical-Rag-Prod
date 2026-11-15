import assert from "node:assert/strict";
import test from "node:test";
import { parseAnalysisList, parseSavedAnalysis } from "../src/lib/analysis.ts";

const workspaceId = "a90a7a40-bef2-4f15-9e7c-a5e4115b0bba";
const id = "06339c5a-20de-4e42-bc06-df157dd23f35";
const metadata = {
  id, workspaceId, configVersion: 7, createdAt: "2026-11-14T10:00:00Z", inputHash: "a".repeat(64), hasResult: false,
};

test("saved lists validate workspace identity and keep only audit metadata", () => {
  const [item] = parseAnalysisList([{ ...metadata, text: "PRIVATE_CASE_MARKER" }], workspaceId);
  assert.equal(item.id, id);
  assert.equal(item.text, undefined);
  assert.throws(() => parseAnalysisList([{ ...metadata, workspaceId: "another-workspace" }], workspaceId));
  assert.throws(() => parseAnalysisList([{ ...metadata, id: "../foreign" }], workspaceId));
});

test("audit-only analyses can open but legacy free text cannot become an answer", () => {
  const opened = parseSavedAnalysis({ ...metadata, result: null }, workspaceId, id);
  assert.equal(opened.result, null);
  assert.throws(() => parseSavedAnalysis({ ...metadata, result: { answer: "UNVERIFIED_LEGACY_MARKER" } }, workspaceId, id));
  assert.throws(() => parseSavedAnalysis({ ...metadata, id: "ae5aa7a2-5071-4704-b4d2-875ca8f44b29", result: null }, workspaceId, id));
});
