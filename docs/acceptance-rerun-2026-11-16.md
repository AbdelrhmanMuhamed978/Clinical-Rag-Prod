# Acceptance re-run — 16 November 2026

**Outcome: the initial run failed because Colab was offline. After the T4 runtime was reconnected, the real-stack walk passed** for parse, non-contiguous chunking, chunk review, embedding, question answering, full-case answering, flagging, a draft test, and publication of v5. Hosted CI remains red; viewer/editor limits and named-clinician approval were still not run.

No real patient data was used. The uploaded fixture is a locally authored synthetic PDF.

## Environment

| Item | Observed |
| --- | --- |
| Run window | 2026-11-16 10:04–10:23 UTC |
| Branch / revision | `ci/review-gates` / `a92f196` (worktree contains uncommitted changes) |
| Frontend | `http://localhost:3000`, HTTP 200 |
| API | `http://localhost:5100`, `/health` HTTP 200 |
| Python pipeline | `http://127.0.0.1:8100`, `/health` HTTP 200 |
| Database | Ready |
| API `/ready` | **503**: database ready, Colab **not ready**, pipeline **not ready** |
| Colab connection | Configured, `heartbeatStale: true`, model `null`; offline. Last heartbeat `2026-11-15T23:59:18Z`. |
| Synthetic fixture | `synthetic-final-five-pages.pdf`, 5 pages, SHA-256 `e120778eed401ddc74584d953ad9ae37f907c88f62d6149820f2736eb47ca846` |
| Workspace used | `856737ef-7ac7-4150-9c02-1cf96e33fe54` (existing test workspace named `test`; it also contains a previously parsed public WHO PDF, not used for a clinical claim) |
| Account used | `timothy901@10minutes.email` (verified, workspace owner) |

The readiness response was rechecked at the end of the run and remained 503:

```json
{
  "ready": false,
  "checks": {
    "database": { "ready": true },
    "colab": { "ready": false, "message": "The Colab model service is offline or not ready..." },
    "pipeline": { "ready": false, "message": "The verified pipeline service is temporarily unavailable..." }
  }
}
```

## Resumed real-stack run — passed

After the user reconnected the T4 runtime, `/ready` returned **200** with real Qwen3-4B-Instruct, PubMedBERT, 768 dimensions and an active Tesla T4. The six canonical notebook cells matched the repo; the source-only offline suite reported **436 passed** after excluding repo-only, CI-governance, clinical-eval/loadtest and environment-sensitive ingestion-fixture tests. The existing model server and registration process were already alive, so cell 5/6 duplicate guards were expected and no duplicate server was started.

| Step | Result | Evidence |
| --- | --- | --- |
| Notebook and source archive | **Pass** | The six canonical notebook cells matched the repo exactly. Cell 4 was corrected to the source-only suite and produced **436 passed**; the repo notebook was updated and the final archive rebuilt: `clinical-rag-colab-source-20261116-final.zip`, 69 files, 155,951 bytes, SHA-256 `22de29fa8fd03a31ec806c5083fdfd74a9b54b59d680296af4562d616915b411`. The live runtime used the earlier 155,738-byte archive with the same cell 4 fix applied in the running notebook. |
| Parse | **Pass** | `synthetic-final-five-pages.pdf` (`5739b5ef-fac2-468f-b875-7727faac218f`) parsed to 5 pages / 3,198 characters. |
| Non-contiguous chunk | **Pass** | Selected pages `[1,3,5]`; server reported 3 chunks created, 3 pages chunked, 0 skipped, 60% document coverage. Pages 2/4 remained unprocessed. |
| Review | **Pass** | Three pending verbatim chunks (pages 1/3/5) reviewed and accepted individually; pending count 0, reviewed count 3. |
| Embed | **Pass** | Embedded 3 accepted chunks; document status `indexed`; embedded count 3, pending/accepted/rejected 0. Chunk IDs: page 1 `af724598-f018-4c05-961a-885e4114cce9`, page 3 `1a3cac18-a82c-4f58-860a-ce05597d7a17`, page 5 `36819ec8-b51b-4704-b189-f5d8fd5e8101`. |
| Ask a question | **Pass** | Real Playground question returned **1 verified recommendation**, citation `S1`, page 1, FINAL label, and exact quote “An isolated low systolic blood pressure does not establish shock.” |
| Ask a full case | **Pass** | Case mode returned **3 verified recommendations** (citations S1 p.1, S3 p.3, S2 p.5) and one `Possible acute kidney injury review` group marked **Not covered** with the safe reason “Sources were retrieved, but no statement could be verified.” |
| Flag an answer | **Pass** | Flag `b49e1594-9dcd-41b3-a555-7c686effee51`, run `e35e1075-ca41-4f74-99bf-05a0a7b5bca9`, config v1; reason `other`; note records that no patient or case data was stored. |
| Run in-app tests on a draft | **Pass after correcting the synthetic expectation** | First v5 run `9562c8e6-27ae-49e2-ad46-5f88b34d12e2` failed only because the synthetic FINAL fixture test required a DRAFT warning. Removing that incorrect expectation produced passing run `03c1b0f7-0b86-4b2d-849c-5af72b04d6ab`, config v5, 14.0 s. |
| Publish only after they pass | **Pass** | Published v5 `78d30bf0-a2eb-4ac3-92d5-6680ca9c1971`; API showed `published: v5`, passing v5 test run, and v1 archived. UI: “Published v5” and “No configuration differences.” A fresh question after publication still returned one verified recommendation, and its flag form showed **Configuration version: 5**. |
| Export a result | **Partial** | The real result displayed the Export Markdown/PDF controls and Markdown recorded “Download requested: guideline-result.md.” The browse extension cannot retain Chromium downloads, so the downloaded bytes were not independently inspected in this run; export content remains covered by component tests. |
| CI is green | **Fail** | See the CI table below. Hosted `dotnet`, `frontend`, and `ci-gate` jobs are still red. |
| Viewer limits | **Not run** | No authenticated viewer account was available. |
| Editor limits | **Not run** | No authenticated editor account was available. |
| Rule approval with a named clinician | **Not run** | The test-only rule remains `in_review`, disabled, `reviewedBy: null`; approval was not executed because no named clinician was available. |

The source-only Colab test command intentionally excludes `tests/test_repository_hygiene.py`, `tests/test_ci_governance.py`, `tests/test_clinical_eval.py`, `tests/test_loadtest_scripts.py` and `tests/test_ingestion_fixtures.py` because those require the full repository, optional tooling, or a Colab-compatible PDF fixture. This is not a full hosted-CI substitute.

## Post-run corrective changes — durable chunk jobs and OCR detection

The real-stack table above used the legacy synchronous `chunk-portions` path. Chunking was then redesigned, and the durable job path was exercised against the live Colab runtime in a separate test workspace.

- `POST /documents/{documentId}/chunk-jobs` now persists a text-free `ChunkJob` and returns **202**. A hosted `ChunkJobWorker` claims queued jobs, renews a per-batch lease, commits each batch, and recovers abandoned `running`/`pausing`/`stopping` leases as `interrupted`. Pages that the service neither chunks nor explicitly skips are recorded as failed rather than retried forever. Migration `20261116181710_ChunkJobs` and the regenerated idempotent `db/schema.sql` add the table and constraints.
- Pause/cancel take effect at the next batch boundary; resume, cancel, retry-failed, retry-remaining and retry-all are explicit endpoints. Retry creates a new linked job and preserves the original page history. Retry is refused while the document is busy or when selected pages overlap reviewed chunks; document/workspace deletion removes terminal jobs first so the restrict FK cannot block deletion.
- The Chunk Review UI adds a background-job panel with start, pause after current batch, resume, cancel, retry failed/remaining, batch size, max tokens, per-batch timeout, retries and continue/stop failure mode. It reattaches to an active job after reload. Auto-poll on/off, interval, poll now, stop polling and last/next update are browser controls only: closing the page or stopping polling does not cancel the worker.
- The legacy synchronous `POST /documents/{documentId}/chunk-portions` remains as a compatibility bridge.
- OCR detection now requires both fewer than 40 non-whitespace characters **and** at least 50% displayed-image coverage. Near-empty chapter dividers and short text pages are no longer labelled image-only; a genuinely image-covered page still is.

Local verification on 16 November 2026 — **not** hosted CI:

| Check | Result |
| --- | --- |
| Backend full suite | **895/895 passed**, including 15 `ChunkJobTests`/processing-state tests and migration/schema tests |
| Frontend lint | **Pass**, no warnings |
| Frontend `npm test` | **16/16 Node tests + 578/578 Vitest tests**; the Chunk Review file is 57/57, including shared polling controls, per-document persistence, start/reattach, pause/resume/cancel and both retry scopes |
| Frontend production build | **Pass** (`next build --webpack`) |
| Python full suite | **591/591 passed**, including `tests/test_ocr_detection.py` (short divider not flagged, full-page image flagged) |
| Rebuilt source ZIP | `clinical-rag-colab-source-20261116-ocr-fix.zip`, 70 files, 156,787 bytes, SHA-256 `993e63cba9f1474e9e2ebaa84c2f6eb30aa56f53ec2abc7b0b166009a61d4923`; manifest, integrity, `colab/parsing.py` hash and OCR test verified |

### Live durable chunk-job verification — 16 November 2026

A fresh test workspace (`1fed6092-69ae-49dd-9708-e46337f14073`) was created through the UI and the public WHO guideline was uploaded and parsed into document `5b2990d5-e941-42c4-912e-b3590c3444de` (83 pages). The real Colab runtime was ready with Qwen3-4B, PubMedBERT 768 and a Tesla T4. The pipeline readiness check remained false during this chunk test because the restarted API no longer had the pipeline key; chunking itself depends only on Colab.

| Step | Result | Evidence |
| --- | --- | --- |
| Legacy failure retained for context | **Failed/interrupted** | The old synchronous button committed pages 7–20 (14 pages, 55 chunks), then stopped with the interrupted banner. No `ChunkJobs` row existed, confirming this was the legacy path. |
| Start job | **Pass** | Job `e4b90f55-e5bb-4215-9e24-e8546152c8aa`, pages `[21,23]`, batch size 1, timeout 120 s, retries 0, continue on failure; returned 202 and entered `running`. |
| Pause after current batch | **Pass** | Pause was requested while page 21 was in flight. Page 21 committed, page 23 stayed untouched, job became `paused` with `[21]` completed and 71 total chunks. |
| Reattach after reload | **Pass** | Reloading the Chunk Review page restored the paused job panel (`Paused · 1/2 pages`) and its Resume/Cancel controls. |
| Resume and complete | **Pass** | Resume completed page 23 and the job ended `completed` at 2/2. |
| Completion checkpoint merge | **Pass after fix** | The first completion exposed a real bug: the document checkpoint was replaced with only `[21,23]`, hiding the pre-existing 14 pages. `FinishCompletedAsync`/`FinishWithFailuresAsync` now merge with `Document.ChunkedPageNumbers`; a regression test was added. The test document checkpoint was repaired from the actual chunk pages (`[7…21,23]`) rather than fabricated. |
| Second completion job | **Pass** | Job `eeba764a-03be-49af-ab79-d04a3914259b`, pages `[25,27]`, completed 2/2; the checkpoint retained all 18 pages and the UI showed 65 remaining, not 81. |
| Partial cancel | **Pass** | Job `63656a33-c222-41ce-99d5-50ac92e2bf9e`, pages `[35,37,39,41,43,45]`. Cancel was requested after page 35; the next batch committed page 37, then the job ended `cancelled` at 2/6 with **Retry remaining (4)**. |
| Retry remaining | **Pass** | Retry created job `2f7eeeff-8b8d-4b52-a63d-803620927151` for `[39,41,43,45]` with `RetryOfJobId` set to the cancelled job. It completed 4/4; the original cancelled job and its `[35,37]` history remained. |
| Stop polling does not cancel | **Pass** | Job `21003fa0-782b-4b61-bc21-e5df5a6527e3` was started for `[47,49]` and auto-poll was stopped immediately while the UI still showed `Queued · 0/2`. The worker completed both pages in the background (checkpoint advanced to 29 pages); **Poll now** then revealed `Completed · 2/2`. |
| Final state | **Pass** | Document checkpoint `[7…21,23,25,27,29,31,33,35,37,39,41,43,45,47,49]` (29 pages), 177 chunks, `ChunkingStatus: partial`, no `ProcessingOperation`, no active job. The completed/cancelled/retry job rows remain for inspection. |

The OCR coverage heuristic remains verified by local Python tests only; it was not exercised against a real scanned guideline in this live run. Forced-failure paths (timeout, malformed batch, `stop` failure mode, retry-failed) remain covered by local tests rather than live Colab outages.

### Shared polling control and lightweight state endpoint — 17 November 2026

The Chunk Review page now has one shared polling control for the whole page: auto-poll on/off, an interval of 2/5/10/30 seconds, Poll now, Stop polling, and last/next update. The preference is stored per document in browser localStorage. Auto-poll calls the lightweight `GET /documents/{id}/processing-state` endpoint; the full document, chunk list and progress reads are refreshed only when the processing state changes, on manual Poll now/Refresh, or on a terminal job transition. Stopping polling stops every periodic page read without cancelling the worker.

Live verification against the running stack:

| Check | Result |
| --- | --- |
| Lightweight endpoint | **Pass** — `GET /api/workspaces/.../documents/5b2990d5.../processing-state` returned 200 with scalar status, page/count evidence and the latest job view; it contains no chunk text or vectors. |
| New UI | **Pass** — the live page showed “Auto-poll all page reads”, 2/5/10/30-second options, Poll now, Stop polling, last update and next poll. |
| Per-document persistence | **Pass** — set interval to 10 s, stopped polling, reloaded the page, and the controls restored Auto-poll **off**, interval **10 s**, Next poll **manual**. |
| Full reads are not periodic | **Pass by component tests** — 57/57 Chunk Review tests assert that an unchanged processing state does not re-download the chunk list, manual mode stops all reads, and Poll now refreshes evidence. |

The live run verified the endpoint and browser persistence; the 10-second periodic cadence itself is covered by component tests rather than a timed live batch.

## CI and repository history

| Check | Result | Evidence |
| --- | --- | --- |
| Repository has history | **Pass** | 3 commits; `main` at `f543ccc`, `ci/review-gates` at `a92f196`; both branches present locally and on `origin` |
| CI is green | **Fail** | The only hosted runs are failures: [43760578072](https://github.com/AbdelrhmanMuhamed978/Clinical-Rag-Prod/actions/runs/43760578072) and [43759956403](https://github.com/AbdelrhmanMuhamed978/Clinical-Rag-Prod/actions/runs/43759956403) |

Latest hosted run `43760578072` on `ci/review-gates`:

| Job | Conclusion | Failed step |
| --- | --- | --- |
| `pytest` | success | — |
| `secret-scan` | success | — |
| `dotnet` | failure | Test backend (no filtered or suppressed failures) |
| `frontend` | failure | Lint frontend |
| `ci-gate` | failure | Require every job to succeed |

There is no green hosted run to cite. Local worktree results are not a substitute for
the requested CI confirmation.

## Acceptance script status

The repository's root `test_e2e.ps1` is the only file presented as an end-to-end script. It is
**stale and incomplete** for this acceptance walk: it creates a random unverified Firebase user,
targets a non-existent local PDF path (`C:\Users\pc\Documents\Default Project\...`), and covers
only workspace creation, upload/parse and Colab health. It was **not used as acceptance evidence**
and was not treated as a pass. The real stack was exercised through the application and
authenticated API as recorded below.

## Initial attempt — model offline (retained for history)

These are the responses observed before the T4 runtime was reconnected. They are retained so the earlier failures are not hidden. The second-user denials and owner checks remain valid; the parse/chunk/review/embed/question/case/test/publish failures were superseded by the resumed run above.

| Step | Result | Evidence |
| --- | --- | --- |
| Second user denied reads **and** writes | **Pass** (authorization response; existing second account) | Signed-in account `timothy901@10minutes.email` against the other workspace `3ad97b32-3ee1-4e0e-aab5-fb783de7ac6b`. **12/12 scoped reads and 9/9 representative writes returned HTTP 404.** The account's own workspace list did not contain the other workspace. See endpoint table below. No new signup or email-verification flow was exercised. |
| Owner limits | **Pass** (for this workspace) | `/api/workspaces/856737ef-.../rules/access` returned `role: owner`, `canEdit: true`, `canReview: true`, `canDelete: true`; owner-only configuration and rule actions were available. |
| Viewer limits | **Not run** | No authenticated viewer account was available in this re-run. Source/UI controls were not used to infer a real viewer denial. |
| Editor limits | **Not run** | No authenticated editor account was available. Owner-only evidence is not evidence for the editor role. |
| Upload PDF | **Pass** | Synthetic final PDF uploaded through the real Documents UI with explicit synthetic publisher/year/FINAL metadata. Document ID `5739b5ef-fac2-468f-b875-7727faac218f`; initial status `uploaded`, queued for server parsing. |
| Parse PDF | **Fail** | The same document ended `status: failed`, `failureStage: parsing`, `parsedTextLength: 0`, reason: “The Colab model service is offline or not ready. Retry shortly...” No page count or parsed text was produced. |
| Non-contiguous chunk | **Not run** | The synthetic document never reached `parsed`, so pages 1/3/5 could not be selected or chunked. The model was also offline. The pre-existing WHO document in this workspace was not substituted for the synthetic fixture. |
| Review chunks | **Not run** | No chunks exist for the failed synthetic document. No chunk review action was executed. |
| Embed | **Not run** | No accepted chunks exist. No embedding request was executed. |
| Ask a question | **Fail** | Real Playground submission returned HTTP 503 from `/analyze/stream`. UI: “The Colab model service is offline or not ready... No answer was added.” No result was created. |
| Ask a full case | **Fail** | Real Playground submission of the manifest's synthetic full case returned the same HTTP 503 and no answer. |
| Run in-app tests on a draft | **Fail** | Test case `e5dea0b6-1f8d-4025-993a-c537cc10d579` run against draft v5 `78d30bf0-a2eb-4ac3-92d5-6680ca9c1971`; run `6e7f77e1-f9b7-4cf4-9b4f-e7d3076907a2`; `status: failed`, `failureCode: pipeline_unavailable`, duration 1,996.9 ms. A separate Tests-page run on published v1 also failed (`2a3eae7d-8b0f-47f1-9cce-19f96ad1d3ec`, 2,179.8 ms). |
| Publish only after they pass | **Protective denial pass; successful publication not run** | Draft v5 remains `draft` with `publishBlockingCode: case_failed`. A direct authenticated POST to `/config/78d30bf0-a2eb-4ac3-92d5-6680ca9c1971/publish` returned **HTTP 409** `{ "code": "case_failed" }`. No passing test run and no successful publication were produced. |
| Rule submit/approve with a named clinician | **Partial: submit pass; approval not run** | Test-only rule `e7e92f2a-a6b3-47ce-bf75-ee5bf440802f` (`synthetic-acceptance-20261116`) saved and submitted: revision 4, `status: in_review`, `enabled: false`, `reviewedBy: null`. The approval dialog appeared and required “Confirm clinician review...”; it was cancelled. No named clinician was available, so approval was not executed. |
| Flag an answer | **Not run** | `/analyses?mine=true` contains two failed runs, both `hasResult: false`. There was no verified answer/run to flag; no flag request was made. |
| Export a result | **Not run** | No verified result exists. The Export Markdown/PDF controls were not exercised on the real stack; component-level export tests are not a substitute. |
| CI is green | **Fail** | See the CI table above. Hosted `dotnet`, `frontend`, and `ci-gate` jobs failed. |

## Uninvited-account denial detail

All requests were made with the second account's verified Firebase bearer token against
workspace `3ad97b32-3ee1-4e0e-aab5-fb783de7ac6b`, which was absent from that account's
workspace list.

| Read | Status |
| --- | --- |
| workspace | 404 |
| members | 404 |
| documents list | 404 |
| final document | 404 |
| final chunking-progress | 404 |
| final chunks | 404 |
| draft document | 404 |
| draft chunking-progress | 404 |
| draft chunks | 404 |
| published configuration | 404 |
| rules | 404 |
| tests | 404 |

| Write probe | Status |
| --- | --- |
| upload document (real multipart body) | 404 |
| parse document | 404 |
| chunk pages `[1]` | 404 |
| review a chunk | 404 |
| save configuration draft | 404 |
| create rule | 404 |
| invite member | 404 |
| change guideline policy | 404 |
| delete document | 404 |

The probes used a non-existent chunk/document where applicable and a denied upload body.
Every request was rejected with 404; no success status was observed. An independent
owner-side re-read of the other workspace was not available in this run, so absence of
side effects is not separately claimed.

## Artifacts left for inspection

- Synthetic document: `5739b5ef-fac2-468f-b875-7727faac218f`, now `indexed`, 5 pages, 3 embedded chunks, 60% coverage.
- Test case: `e5dea0b6-1f8d-4025-993a-c537cc10d579`.
- Passing draft run: `03c1b0f7-0b86-4b2d-849c-5af72b04d6ab` (v5, 14.0 s).
- Comparison-failure run retained: `9562c8e6-27ae-49e2-ad46-5f88b34d12e2` (the corrected expectation produced the passing run).
- Pipeline-unavailable runs retained: `6e7f77e1-f9b7-4cf4-9b4f-e7d3076907a2` (v5), `2a3eae7d-8b0f-47f1-9cce-19f96ad1d3ec` (v1).
- Published version: v5 `78d30bf0-a2eb-4ac3-92d5-6680ca9c1971`; v1 archived.
- Earlier source archive: `clinical-rag-colab-source-20261116-final.zip`, 69 files, SHA-256 `22de29fa8fd03a31ec806c5083fdfd74a9b54b59d680296af4562d616915b411`. It predates the OCR detection fix and is superseded by the rebuilt archive.
- Current source archive: `clinical-rag-colab-source-20261116-ocr-fix.zip`, 70 files, 156,787 bytes, SHA-256 `993e63cba9f1474e9e2ebaa84c2f6eb30aa56f53ec2abc7b0b166009a61d4923`, including `colab/parsing.py` and `tests/test_ocr_detection.py`.
- Governance flag: `b49e1594-9dcd-41b3-a555-7c686effee51` (reason `other`, no case text).
- Test-only rule: `e7e92f2a-a6b3-47ce-bf75-ee5bf440802f` (`in_review`, disabled, revision 4).
- Live chunk-job test workspace: `1fed6092-69ae-49dd-9708-e46337f14073` (`new test`).
- Live chunk-job document: `5b2990d5-e941-42c4-912e-b3590c3444de`, 83 pages, 29 processed for chunking, 177 chunks.
- Live chunk jobs: `e4b90f55-e5bb-4215-9e24-e8546152c8aa` (completed `[21,23]`), `eeba764a-03be-49af-ab79-d04a3914259b` (completed `[25,27]`), `ca81ba41-7c45-4796-83b3-95d88652d675` (cancelled `[29,31,33]`), `63656a33-c222-41ce-99d5-50ac92e2bf9e` (cancelled 2/6 on `[35,37,39,41,43,45]`), `2f7eeeff-8b8d-4b52-a63d-803620927151` (retry-remaining completed `[39,41,43,45]`), `21003fa0-782b-4b61-bc21-e5df5a6527e3` (background completion `[47,49]`, revealed with Poll now after auto-poll was stopped).

No synthetic document or chunk was manually inserted to force a pass, and the synthetic test expectation was corrected to match the FINAL fixture before the earlier passing run. The live test workspace/document were created through the UI. After the live run exposed the checkpoint-loss bug, the affected document checkpoint was repaired to the actual distinct chunk pages (`[7…21,23]`), not to a fabricated completion count; the bug fix and regression test are recorded above.

## Remaining gaps

- Hosted CI is still red on backend tests, frontend lint, and the aggregate gate; the worktree remains uncommitted and unpushed. Local/source-only passes are not a hosted-CI substitute.
- No authenticated viewer or editor account was available, so those role limits remain untested.
- The test-only rule remains `in_review`, disabled, with `reviewedBy: null`; named-clinician approval was not executed.
- The governance flag remains for owner review; no owner disposition is claimed.
- The Markdown/PDF export controls were exercised, and the Markdown download was requested, but the browse extension could not retain the Chromium download for independent byte inspection. Export content is covered by component tests, not a real-stack file inspection.
- The Colab source-only suite excludes repository-hygiene, CI-governance, clinical-eval, loadtest-script, and ingestion-fixture tests; the full repository suite is still the separate verification target.
- The live app run reused the already-running model/registration processes from the reconnected kernel; a fresh model-server restart from the final ZIP was not performed, so model-server startup code changes are not independently revalidated.
- The durable chunk-job start/pause/reattach/resume/cancel/retry-remaining path is now live-verified. Forced-failure paths (timeout, malformed batch, `stop` failure mode, retry-failed) and the OCR coverage heuristic remain local-test-only.
- Overall `/ready` was false during the live job test because pipeline readiness was unavailable after the API restart; Colab readiness itself was true. The chunk-job path was not blocked by the pipeline.
- Clinical correctness, guideline fidelity, and patient safety remain unverified.

## What is not claimed

- No clinical correctness, guideline fidelity, or patient safety claim.
- No pass for viewer/editor authorization, named-clinician rule approval, owner disposition of the flag, independently inspected export bytes, or hosted CI.
- No live-stack pass is claimed for forced chunk failures, retry-failed, the OCR coverage heuristic, or overall pipeline readiness.
- No claim that the earlier offline failures did not happen; they are retained above as historical evidence.
