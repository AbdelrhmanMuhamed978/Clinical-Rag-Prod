# Documents / Chunk Review / Playground validation

| Field | Value |
| --- | --- |
| Date | 2026-11-15 |
| URL | `http://127.0.0.1:8768` (isolated synthetic fixture) |
| Browser session | `clinical-docs-playground-qa-73ea5ebdf175` |
| Scope | Actual Documents, Chunk Review, Playground, result/history/actions, and application shell components |
| Status | Browser validation in progress |

## Evidence boundary

The fixture compiles the actual frontend components using the installed dependencies and production CSS. Router/auth adapters and API state are synthetic. The API fixture uses in-memory document checkpoints, review/embedding transitions, privacy markers, saved-history scenarios, and split-byte SSE streams consumed by the actual transport code.

This is **not** proof of live Firebase authentication, ASP.NET persistence, PDF parsing, vector indexing, retrieval, generation, or Colab/GPU integration. The generated PDF and all questions/results are synthetic and are not clinical guidance.

Fixture sources: `C:\Users\abdom\AppData\Local\Temp\opencode\clinical-rag-documents-playground-ui-20261115\` (with shared loaders/link adapter in the adjacent `clinical-rag-rules-ui-20261115\` directory). Launch: `node build.cjs`, `python make-pdf.py`, `node server.cjs`.

## Automated checks completed before browser QA

| Check | Result |
| --- | --- |
| Complete frontend Vitest run | 424/424 tests, 18 files passed |
| Complete frontend Node run | 14/14 passed |
| TypeScript and scoped ESLint | Passed |
| Frontend production build | Passed, including all routes and TypeScript; native SWC could not load, WASM fallback succeeded |
| Backend build, `--no-restore` | Passed; 0 errors, 2 dependency-advisory warnings |
| Earlier complete backend/Python runs | 461 xUnit and 312 pytest tests passed (not rerun during this browser pass) |
| Focused backend processing/cancellation run | 105/105 passed |
| Migration regressions | 8/8 passed |

Full frontend lint remains non-passing outside this scope: 3 errors (unescaped apostrophe in `frontend/src/app/(auth)/verify-email/page.tsx:24`, explicit `any` in `frontend/src/components/AuthForm.tsx:47,70`) and 7 warnings. Backend dependency warnings affect `System.Security.Cryptography.Pkcs` 9.0.4: `GHSA-2fmv-rv2c-59x7` and `GHSA-846f-f764-76mx`.

The corrected polling regression now verifies the request/cache update at 2000 ms and advances 1 ms for the separately scheduled UI notification; production polling and test timeouts were not weakened.

## Browser observations

### Initial document lifecycle

- All seven lifecycle states are distinct; parse, chunking, and embedding failures retain actionable stage-specific feedback.
- Required society/year/FINAL-DRAFT metadata, parsed-character/page/checkpoint counts, and pending/accepted/rejected/embedded counts are visible.
- Only the failed unparsed document offers Retry Parse. Parsed/review/indexed documents offer Choose pages / Review / Embed instead.
- Parsing and chunking disable deletion; uploaded documents state they are queued for automatic parsing.
- No page-load JavaScript exception; console initially contains only the React development-tools informational message.

### Upload, retry and deletion

- The rebuilt source rejects the synthetic `502 {"error":"PRIVATE_UPSTREAM_ERROR_MARKER"}` and shows only the safe fallback, retaining PDF/society/year/FINAL metadata: [fixed upload error](issue-001-upload-fixed.png).
- A held upload disables duplicate submission. Cancel upload closes the native modal and aborts the request. A synthetic transport deliberately allowed to finish after abort saves a document, which Refresh discovers in Parsing without a late success callback or duplicate client `/parse` request; worker release/polling then shows Parsed. Cancellation is not promised as server rollback.
- Only the failed unparsed document offers Retry Parse; one held retry disables mutations, then a confirmed parse response removes the retry control and offers Choose pages / Review / Embed.
- Native deletion confirmation starts on Cancel and permits keyboard Tab/Shift+Tab and Escape. Escape and Cancel send no deletion request. Confirming sends one DELETE and removes the synthetic document plus all its chunks; no other document is removed.
- Post-rebuild Documents axe-core 4.12.1: **0 violations**; generic-card ARIA concern is gone. Gradient-background contrast still requires manual review.

## Issues

### ISSUE-001: Upload accepts an arbitrary structured error as user-facing feedback

| Field | Value |
| --- | --- |
| Severity | High (privacy defense-in-depth) |
| Category | Functional / privacy |
| URL | `/w/00000000-0000-4000-8000-000000000001/documents` |
| Repro video | [Upload repro](issue-001-upload-repro.webm) |
| Status | Fixed; 24 privacy regressions and rebuilt-browser reproduction passed |

The synthetic upload response `502 {"error":"PRIVATE_UPSTREAM_ERROR_MARKER"}` is displayed verbatim in the upload dialog instead of an actionable safe fallback. The fixture marker is not real private data; this demonstrates that arbitrary structured transport-error text can cross the UI error boundary. Native file/required/year validation correctly blocks invalid uploads, and valid metadata is retained after the failure.

1. Open Upload guideline; choose the synthetic PDF, society `Synthetic Test Society`, year `2024`, and FINAL.
2. Inject `window.clinicalQa.failNext("upload", 502)` (synthetic response only).
3. Click Upload and parse; repeat the injected failure once to verify reproducibility.
4. Observe the raw marker in the dialog alert: [before repeat](issue-001-upload-before.png), [after repeat](issue-001-upload-after.png).

`documentActionError` now displays only exact fixed backend-owned messages or status-based/caller fallbacks. Numeric invalid-page lists become fixed guidance. Arbitrary structured text, including near-matches to legitimate messages, is rejected. This shared boundary also protects parse, chunking, review, embedding and deletion errors.

### ISSUE-002: Destructive button text fails default contrast

| Field | Value |
| --- | --- |
| Severity | Medium |
| Category | Accessibility |
| URL | Documents; shared destructive buttons also appear in confirmation dialogs |
| Repro video | N/A (static on load) |
| Evidence | [Delete text contrast](issue-002-delete-contrast.png) |
| Status | Text contrast fixed; rebuilt axe audit has 0 violations. Follow-up keyboard finding is ISSUE-003. |

Two axe-core 4.12.1 audits report 7 enabled Delete buttons at **3.98:1**, below the normal-text minimum of 4.5:1 (foreground `#e7000b`, background `#fde5e7`, 14 px text). The shared destructive variant now uses darker light-mode text and lighter dark-mode text, with a matching outline-based keyboard focus indicator; background/confirmation behavior is unchanged.

The audit's manual-review list also identified named document cards using generic `div` semantics. They now expose a named `group`, preserving card contents and existing interactions without adding a landmark per document.

### ISSUE-003: Destructive keyboard focus outline remains suppressed

| Field | Value |
| --- | --- |
| Severity | Medium |
| Category | Accessibility |
| URL | Documents deletion confirmation; shared destructive buttons |
| Repro video | [Focus repro](issue-003-focus-repro.webm) |
| Status | Reproduced twice; explicit solid-outline fix and regression added, rebuilt-browser verification pending |

The intended outline from ISSUE-002 still computes to `outline-style: none` while the destructive control is actually focused and matches `:focus-visible`. Its width/color/offset are correct, but Tailwind's shared `outline-none` sets the style variable to `none`. The remaining translucent shadow ring does not provide the intended high-contrast/forced-colors-safe outline.

1. Open Delete for a synthetic document. Native focus starts on Cancel: [before Tab](issue-003-focus-before.png).
2. Press Tab to Delete document: [after Tab](issue-003-focus-after.png). Browser inspection reports `:focus-visible`, `outline-width: 2px`, `outline-offset: 2px`, but `outline-style: none`.
3. Repeat Shift+Tab then Tab; the defect persists. No deletion is confirmed during this reproduction.

The shared destructive variant now explicitly sets `focus-visible:outline-solid`; the confirmation regression requires style, width, offset and light/dark outline colors. A class-presence assertion alone is not treated as browser rendering proof.

## Remaining work

- Upload, native validation, upload failures/cancellation, deletion confirmation and failed-only parse retry.
- Page selection, checkpoint polling, partial/skipped results, review/embedding, cancellation, stale-read handling.
- SSE progress/modes, verified evidence/diagnostics, copy/export privacy, errors/cancellation/history, identity isolation.
- Responsive, keyboard/modal and accessibility/console checks.
- Persisted harness/evaluation and paused Rules QA remain subsequent work; the current `/tests/run` stub and `NoConfigTestRunProvider` do not provide publication evidence.
