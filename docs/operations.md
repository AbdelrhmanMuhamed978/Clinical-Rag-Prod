# Clinical RAG Platform — Operations

This document covers workspace/document administration, the Colab model service connection, health/readiness, metrics, and platform administration. It does not cover retrieval tuning, which is described in [retrieval-deployment.md](retrieval-deployment.md) and the [hybrid retrieval PR report](hybrid-retrieval-pr.md).

## Guideline governance

Each document retains society, title, version, publication year, final/draft/superseded status, effective date, review-by date, source URL and permission/licence note. Superseded documents are excluded from retrieval by default; an owner can opt in with `PATCH /api/workspaces/{id}/guideline-policy`. Superseded and overdue-review citations remain visibly labelled.

Rule approval snapshots the authenticated reviewer name, review timestamp and the approved source URL. Owners can export `GET /api/workspaces/{id}/governance/audit?format=csv` or `?format=pdf`. Clinicians can flag an answer with a closed reason category and an optional note; owners see the flag backlog at `GET /api/workspaces/{id}/flags`. Flags retain run ID, configuration version and the reason category/note; they never copy case text or generated answer text.

## Evaluation and load testing

The retrieval regression gate uses the offline fixed-vector harness and a stored baseline:

```powershell
python eval/run_eval.py --output eval/results/<new-artifact>.json
python eval/compare_baseline.py --baseline eval/baselines/synthetic-retrieval.v1.json --current eval/results/<new-artifact>.json --tolerances eval/baselines/synthetic-retrieval.tolerances.json
```

This gate is a regression check, not clinical evidence. CI runs it in the `eval-gate` job. Existing artifacts and the baseline are never overwritten; a deliberate re-baseline requires review.

Clinician-labelled evaluation uses `eval/clinical/`. The template is empty until a named clinician supplies labels and an attestation; the synthetic worked example is never presented as clinical evidence. A live run requires `EVAL_FIREBASE_TOKEN`, `EVAL_PIPELINE_KEY`, `EVAL_PIPELINE_URL`, `EVAL_BACKEND_URL` and `EVAL_WORKSPACE_ID`, and calls the authenticated pipeline service with text-free diagnostics. It writes a new dated directory under `eval/results/clinical/`; a missing prerequisite writes a **BLOCKED** report.

The staging load test lives in `scripts/loadtest/`. It runs k6 retrieve and end-to-end analyze measurements at 1, 3 and 10 concurrent users while real Colab chunk jobs run, samples GPU memory, and records error/429/503 rates. See [scripts/loadtest/README.md](../scripts/loadtest/README.md). A missing k6, staging stack, verified token, real Colab runtime or GPU evidence produces a dated **BLOCKED** report, not invented numbers.

Colab readiness now reports live CUDA memory fields (`totalMemoryBytes`, `usedMemoryBytes`, `allocatedMemoryBytes`, `peakAllocatedBytes`) alongside the model and encoder identity. The backend `/api/operations/colab` endpoint exposes the same non-secret metadata to a verified workspace user for load-test evidence; it never exposes the Colab URL or key.

## Deployment rehearsal — 16 November 2026

**Command**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/rehearse-deployment.ps1 -Server "localhost\MSSQLSERVER2025" -OutputDirectory docs/rehearsal-results
```

**Outcome:** **PASSED** against SQL Server 2025 Standard Developer Edition with Full-Text installed. The script used only disposable GUID-named databases, integrated authentication and synthetic rows; it dropped the rehearsal databases afterward. Final dated record: `docs/rehearsal-results/deployment-rehearsal-2026-11-16T08-59-36Z.json`; earlier passing runs are retained at `deployment-rehearsal-2026-11-16T08-38-05Z.json` and `deployment-rehearsal-2026-11-16T08-51-38Z.json`.

An earlier rehearsal attempt failed before creating databases because the PowerShell helper name recursively resolved to itself; that failed record is retained at `docs/rehearsal-results/deployment-rehearsal-2026-11-16T08-34-41Z.json`. The helper was renamed (`Run-DotNet`) and the passing run above is the corrected result. A subsequent pre-result failure was a client-side permission check on the SQL Server backup directory; cleanup no longer touches that server-owned path.

| Rehearsal check | Measured result |
| --- | --- |
| Migrate the legacy-state source to `DocumentProcessingState` | **4,751.6 ms** |
| Back up the legacy-state source | **216.7 ms** |
| Restore that backup to a second disposable copy | **695.5 ms** |
| Apply `db/schema.sql` to the restored copy first time | **1,688.2 ms** |
| Reapply the same idempotent script | **126.4 ms** |
| Legacy chunks inserted | **4**: valid JSON 1, empty string 1, malformed 1, NULL 1 |
| Archive preservation | `LegacyEmbeddingJson` preserved all 4 values; new binary `Embedding` NULL 4; status `accepted` 4 |
| Document state | indexed document -> `review`, revision 8; interrupted embedding -> `failed`, revision 10 |
| Search-generation trigger | **1** trigger (`TR_Chunks_SearchGeneration`) present after script |
| Re-embedding rehearsal | **passed** on a separate disposable copy: 4 chunks, batch size 2, explicit re-embed produced binary vectors and `indexed`; explicit re-embed cleared the archive as designed |
| Backup (`COPY_ONLY, CHECKSUM`) | **225.5 ms** |
| Restore to an isolated copy | **631.1 ms**; 4 chunks, valid archive 1, document status `review`; matches source |
| Full-Text schema setup | **1,869.1 ms** |
| `enable-chunk-fulltext.sql` first / second run | **342.3 ms / 101.7 ms** |
| Full-Text population | **1,986.1 ms** |
| Exact-term queries under `clinicalrag_rehearsal_app` | `cystatin monitoring` 1, `anti-Xa measurement` 1, `procalcitonin measurement` 1; all expected chunks matched |
| BM25 fallback test (no Full-Text index) | **passed** |
| Colab heartbeat/rotation/recovery rehearsal | **passed** at transport/API level; `/ready` returned 503 with `Retry-After: 30` while the old runtime was down and 200 after startup registration of the new runtime without an API restart. No live Colab runtime was restarted. |

The synthetic backup files were retained in the SQL Server backup directory because the client principal could not access that server path; they contain synthetic data only and are not a production retention decision. A DBA should remove them under the normal backup-retention procedure. The live Colab runtime was not available, so the rotation result is a transport/API simulation, not proof of a real runtime restart. The notebook procedure is in [live-test-setup.md](live-test-setup.md#notebook-restart--url-rotation-procedure).

Cache coherence was exercised separately: `ChunkVectorCacheTests.A_changed_database_generation_reloads_without_process_invalidation`, `Persisted_chunk_changes_bump_the_workspace_generation`, and `SqlRetrievalTests.Raw_chunk_changes_bump_the_shared_generation_and_reload_another_cache` prove generation reload, SQLite bumping and the SQL Server trigger respectively. Alerts and recovery procedures are in [alert-rules.yml](alert-rules.yml) and [runbook.md](runbook.md).

Verification commands from this rehearsal pass:

| Command | Actual result |
| --- | --- |
| `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/rehearse-deployment.ps1 -Server "localhost\MSSQLSERVER2025" -OutputDirectory docs/rehearsal-results` | **PASSED**; dated JSON above |
| `$env:CLINICALRAG_TEST_SQLSERVER='localhost\MSSQLSERVER2025'; $env:CLINICALRAG_TEST_REQUIRE_FULLTEXT='1'; dotnet test backend.Tests --filter "FullyQualifiedName~SqlRetrievalTests" --nologo --no-restore` | **9 passed** (live Full-Text and BM25 fallback) |
| `dotnet build backend/ClinicalRag.Api.csproj --nologo --no-restore` | **0 warnings / 0 errors** |
| `dotnet test backend.Tests --no-restore --nologo` | **875 passed / 0 failed / 0 skipped** |
| `python -m pytest tests -q` | **589 passed** |
| `npm run lint` (frontend) | **passed, 0 errors** |
| `npm test` (frontend) | **15 Node + 555 Vitest passed** |
| `npm run build` (frontend) | **passed** |

## Parser artifact/re-parse validation — 17 November 2026

The shared normalization and non-destructive re-parse change was validated locally on an uncommitted worktree:

| Command | Actual result |
| --- | --- |
| `python -m pytest tests -q` | **613 passed** |
| `dotnet test backend.Tests/ClinicalRag.Tests.csproj --nologo --no-restore` | **914 passed / 0 failed / 0 skipped** |
| `dotnet ef migrations has-pending-model-changes ...` | **No changes** since `ReparseMetadata` |
| `npm test` (frontend) | **19 Node + 582 Vitest passed** |
| `npx tsc --noEmit`, `npm run lint`, `npm run build` | **Passed** |
| `dotnet build backend/ClinicalRag.Api.csproj --configuration Release` | **Passed** |

Synthetic fixtures cover double-escaped entities, alphabetic superscript footnote markers, numeric superscripts that must stay visible, wrapped dashes and table-cell cleanup. Re-parse tests prove changed pages are marked `ReReviewRequired`, accepted/embedded chunks return to pending, stale vectors are cleared and unchanged reviews are retained. A local, non-committed real guideline spot check of p.11/p.27/p.40/p.41 layouts produced no stored entities, no fused `Screeningc` marker and no `antigen– positive` wrap; 46 alphabetic markers were separated into metadata. The p.41 “when cryptococcal antigen screening is not available…” statement verified against the real parsed page text through the new canonical verifier, with no live model or Colab call. Hosted CI was not rerun, and no real document was re-parsed in production.

The rebuilt Colab source archive is `clinical-rag-colab-source-20261117-citations-v10.zip`: **74 files, 170,550 bytes**, SHA-256 `cf9e606c5b4b47a31dc8835ec8f25714666b62a35a9f7240829da652ab0975e5`. Its manifest hashes, member list, ZIP integrity and inclusion of `evidence_text.py`, `colab/parsing.py`, the prompt, schema and verifier/relevance/citation tests were verified; it contains no appsettings, credentials, PDFs or uploads. Extracting the archive to a clean temporary directory and running its parser/normalization/answer-quality/relevance tests passed **40/40**.

## Quote canonicalization and table evidence

`evidence_text.canonical_text` is the single view used to locate a quote. It applies Unicode NFKC (so `mm³` equals `mm3`), decodes entities, removes parser-recorded footnote markers by their recorded context, unifies dashes and ignores whitespace/punctuation differences. Source and quote use the same function. The matched span must be an exact substring after canonicalization; a quote cannot skip, add or reorder words. Numbers, negation, strength, scope and topic keep their existing independent checks.

For a markdown table, the source also exposes structured rows (row label, column headers, cell values). A table quote may be one exact row label plus one exact cell; the verifier returns the two headers and the row for display. A paraphrased sentence with added words is rejected even if it describes the same row.

Rejected candidates keep the existing closed reason codes and add `formatting_mismatch` for failures that are formatting-only but not safely canonicalizable; a genuine absence remains `quote_not_found`. The drafting prompt now requires character-for-character copying without fixing typos, symbols or footnote letters, and asks for a row label plus one exact cell for table evidence.

## Citation display and grade derivation

Citation names are built from society, title, version and year, then shown as `Society, Title (Year, STATUS)`. Duplicate adjacent words, repeated society names, trailing status/year tokens in file-derived titles, ISBN-like digit prefixes and doubled spaces are removed. Section paths use `Treatment > Induction therapy`, not table-pipe syntax.

Strength and certainty are derived only from an explicit grade pattern in the same paragraph or table row as the cited quote, such as `(Strong recommendation, high-certainty evidence)`. The LLM's chunk-level `strength`/`certainty` fields are ignored for display, and a narrative with no grade shows `Strength not stated` / `Certainty not stated`.

The p.40 narrative investigation found that the old path trusted the chunk-level LLM grade before scanning text; a narrative chunk could therefore show `strong` even though its paragraph contained no recommendation grade. The citation now scopes grade extraction to the quote's paragraph/row and ignores the borrowed field. A real p.40 narrative spot check returns an empty strength/certainty and a clean label/section.

## Question mode, relevance and coverage

A request is a **question** when it is interrogative or when deterministic fact extraction finds no vitals, labs, drugs, age or sex, regardless of word count. A non-interrogative text with extracted facts is a case. This is the analysis mode only: privacy storage remains stricter and treats any explicit vitals/labs/drug dose/age/sex or a 40+ word message as case data, so it is not saved by default.

Question planning splits the user text into stated parts without rewriting them. Each problem carries a `covers` field that must be an exact substring of the user question; unmapped problems are rejected. Follow-up questions may use verified history to resolve the retrieval/drafting reference, while coverage remains tied to the stated part.

After deterministic verification, each verified item is scored against the resolved question with the Colab reranker plus deterministic term overlap. The default combined score is `0.5 * rerank + 0.5 * overlap`; items below `retrieval.relevanceThreshold` (default `0.55`) move to `related`, never `items`. On a broad population question, a verified item whose text names a narrow exception population (pregnancy, TB, children, renal failure, dialysis, contraindication, “only if”/“except”) receives a **0.10** penalty and is sorted behind general statements; it can remain verified under **Related**. When the reranker is unavailable, the gate falls back to term overlap with `retrieval.relevanceOverlapThreshold` (default `0.30`). The response includes a `coverage` row for every stated question part with status `answered`, `related_only` or `not_covered`. A related-only part has verified evidence but no lead answer.

## Search cache coherence

The `SearchGenerationCoherence` migration adds `Workspaces.SearchGeneration` and the SQL Server `TR_Chunks_SearchGeneration` trigger. Every chunk insert/update/delete advances the generation atomically, including changes from another API instance or direct SQL. Retrieval reads that primary-key value before serving a cached index and reloads on a mismatch. InMemory/SQLite test providers mirror the bump in `AppDbContext.SaveChanges`. `Chunk` uses EF `UseSqlOutputClause(false)` because SQL Server rejects the default OUTPUT clause on a trigger-bearing table. Keep the trigger enabled; do not replace the durable generation with process-local-only invalidation. See [retrieval-deployment.md](retrieval-deployment.md#cache-operations-and-scale-out) and [runbook.md](runbook.md#cache-coherence-and-scale-out).

## CORS, abuse limits and security headers

CORS is the named **App** policy. `Cors:AllowedOrigins` must list explicit origins; Production startup fails when the list is empty or contains `*`. Development uses `http://localhost:3000` from `appsettings.Development.json`. The policy keeps `X-Request-Id` and `Retry-After` exposed. Production also fails startup when `Firebase:ProjectId` is missing; the development authentication scheme is never registered in Production.

`/analyze`, `/analyze/stream` and `/tests/run` enforce per-user request and concurrency limits through `AbuseLimits` (`RequestsPerWindow`, `WindowSeconds`, `Concurrency`, `RequestTimeoutSeconds`). Rejections return **429** with `Retry-After`; the request-timeout policy returns the framework **504** timeout response. Set limits conservatively for the deployed GPU capacity and monitor 429 counts without logging prompts or case text.

API responses set `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, and a deny-all CSP suitable for JSON. The Next.js frontend sets `X-Content-Type-Options`, `Referrer-Policy`, and a CSP restricted to the app origin, the configured API origin, and Firebase Auth/API origins. Set `NEXT_PUBLIC_API_BASE` and the Firebase public variables before building the frontend so the CSP matches the deployed origins.

## Workspace administration

Workspace owners administer membership and workspace content through **Settings**. This never grants platform administration or permission to change the shared Colab connection.

| Role | Workspace capability |
|------|----------------------|
| Viewer | Read workspace resources and leave |
| Editor | Viewer plus document/chunk processing, analysis and draft-definition/configuration editing |
| Reviewer | Viewer plus clinical rule review/approval; **not** document editing or administration |
| Owner | Editing, rule review, membership/workspace administration and owner-only configuration publication |

At least one owner must remain. The server rejects removing/demoting/leaving as the last owner, including concurrent changes. Promote another member to owner first. When the canonical `OwnerId` owner leaves or is demoted, it moves to a remaining owner. All membership mutations, invitation acceptance, uploads and workspace deletion share the `MembershipRevision` concurrency token; conflicts require refresh, not automatic replay.

All routes below require Firebase bearer authentication. Non-members/deleted workspaces receive 404 without revealing existence; insufficient roles receive 403. `id`, `userId` and `invitationId` are database GUIDs, **not Firebase UIDs**.

| Method | Route | Access / body |
|--------|-------|---------------|
| GET | `/api/workspaces` | Signed-in user's non-deleted memberships |
| POST | `/api/workspaces` | Authenticated; `{ name }`, creates an owner membership |
| GET | `/api/workspaces/{id}` | Member |
| GET | `/api/workspaces/{id}/members` | Owner |
| POST | `/api/workspaces/{id}/invite` | Owner; `{ email, role }`, role defaults to viewer |
| GET | `/api/workspaces/{id}/invitations` | Owner; pending invitations only |
| DELETE | `/api/workspaces/{id}/invitations/{invitationId}` | Owner; cancel pending access |
| PATCH | `/api/workspaces/{id}/members/{userId}/role` | Owner; `{ role, expectedRole }` |
| DELETE | `/api/workspaces/{id}/members/{userId}?expectedRole=viewer` | Owner; expectedRole must match the saved role |
| POST | `/api/workspaces/{id}/leave` | Member; no body, last-owner guard applies |
| PATCH | `/api/workspaces/{id}` | Owner; `{ name, expectedName }`, name 1–128 characters without controls |
| DELETE | `/api/workspaces/{id}` | Owner; confirmed soft-delete |

Missing `expectedRole`/`expectedName` returns 428; stale values or shared revisions return 409. Member access changes are audited and do not change rule definitions or approvals. Owner promotion/invitation gives full administration and deletion capability; confirm the captured email/role, not a later edited selection.

### Invitation acceptance and delivery limitation

Invitations are normalized email-address records, **not outgoing emails or link tokens**. Share the ordinary app URL separately. There is no delivery status, expiry/resend route or automatic email service. To change pending access, cancel the old invitation and create a new one.

Acceptance occurs during current-user resolution on an authenticated request with the matching **verified Firebase email**. A user with another email, an unverified claim or development authentication cannot accept it. Acceptance removes the pending record and writes audit attribution; an existing member keeps their current role even if an old invitation offers higher access. If an invitation is no longer pending, inspect/refresh members rather than retrying cancellation. Invitations for deleted workspaces cannot be accepted.

### Deletion, retention and safe file cleanup

Workspace deletion is a soft-delete of the workspace, **not** of its document content. It is blocked while any document is being processed. The transaction explicitly removes chunks, chunk jobs, guidance, documents and pending invitations despite Restrict FKs, queues uploaded-file deletion and sets `DeletedAt`/`DeletedBy`. It retains the tombstone, memberships, accounts, configuration/rule/catalog/audit history, analyses and test history; normal workspace authorization denies all access afterwards. There is no restore or hard-purge API. Treat retained analyses/test inputs according to a separate approved retention/erasure policy; workspace deletion alone does not satisfy full data erasure.

Document deletion similarly removes the document, its chunks and its guidance, then attempts file cleanup after the database commit. Failed cleanup returns a warning and keeps `PendingUploadDeletions` records (`StoragePath`, `CreatedAt`, `LastAttemptAt`) without a FK to the deleted document. The warning is not a failed content deletion and must not be dismissed as proof the file was erased.

- Set `Documents__UploadsDirectory` to a persistent, service-owned directory (default: `uploads` under the API working directory). Restrict the entire path and its ancestors so untrusted users cannot replace directories during cleanup. Use the same root on restart and coordinate shared storage across API instances.
- `Documents__AutomaticFileCleanup` defaults to **true**. The worker attempts at most **20 records every 30 seconds**, oldest attempts first. Testing hosts disable it. An immediate post-delete attempt still runs when the background worker is disabled.
- Cleanup accepts only regular files lexically inside the configured root. It rejects path traversal/outside files, directories, file symlinks and directory symlinks/junctions anywhere in the path, including ancestors above the configured root. Missing in-root files count as already cleaned. It never broadens the root because a stored path asks it to.
- Cleanup logs only a fixed deferred-cleanup message, not document names, paths or source text. Operators should monitor the queue count/age using restricted database access; do not expose path records through browser status/logs.

If cleanup remains pending, first confirm the active root and service ACLs, then inspect the individual path through privileged operator tooling. Fix legitimate configuration/permissions or reconcile a known legacy path only after verifying file ownership. Never disable the path checks, follow a link, expand the root to a broad filesystem directory, or delete a queue record merely to hide a failure. For an outside-root legacy file, explicitly delete that exact approved file through operator tooling; document the outcome and reconcile only its cleanup record. Pending records survive API restart and retry safely.

### Management migration and rollback

Migration `20261115155603_WorkspaceAdministration` adds:

- `Workspaces.DeletedAt`, `DeletedBy`, and `MembershipRevision` (default 1; shared concurrency token)
- `WorkspaceInvitations`, with unique workspace/email index and Restrict workspace FK
- `PendingUploadDeletions`, deliberately independent of deleted-document FKs
- The document/created-at index for latest persisted chunker guidance

Before deployment, back up the database and upload storage, drain old API instances, review/apply the regenerated idempotent `db/schema.sql`, verify the migration list, then start the new API. Do not mix old instances with tombstones: they do not enforce the new access-denial rules. Production should keep `Database__AutoMigrate=false`.

Downgrade to `TestRunHarness` refuses with SQL error **51002** if any deleted-workspace tombstone, pending invitation or pending file-cleanup record exists. SQL tests verify each guard preserves schema/data and permit an empty-management-schema downgrade/upgrade. Resolve invitations/cleanup with the normal audited process; **do not clear tombstones to bypass the guard**, which could resurrect workspace access in old code. Use a separately reviewed retention-aware migration/recovery plan if a populated system must roll back. Guards 51000 (new binary vectors) and 51001 (operational Colab connection) remain independent.

### Chunk job migration and worker

Migration `20261116181710_ChunkJobs` adds the `ChunkJobs` table, status/lease indexes and range checks. Apply the regenerated idempotent `db/schema.sql` with `Database__AutoMigrate=false`, then start the new API. Keep `Documents__ChunkJobs=true` on the instance that should own the worker. Drain old API instances before enabling the worker; an old instance does not understand job rows and cannot make progress on queued jobs. The job table stores page arrays, settings and safe status/error codes, not chunk text. If the worker stops, queued/paused jobs remain; restarting it resumes queued work, while interrupted/pausing/stopping leases must be resumed, retried or cancelled explicitly. Deleting a document or workspace removes its job rows before the Restrict document FK is applied.

### Parser artifact normalization and re-parse

Migration `20261117102730_ReparseMetadata` adds `Documents.FootnoteMarkers` (JSON page/offset/context records for alphabetic superscript markers removed from visible text) and `Chunks.ReReviewRequired` (default `false`). It does not rewrite existing text; a document keeps its current parse until an editor explicitly re-parses it.

`evidence_text.py` is the single normalization path used by the Colab parser, chunker and Python verifier. It decodes HTML entities until stable, expands ligatures, normalizes soft hyphens, joins a dash at a wrapped line only when no space precedes it, and leaves visible clinical words unchanged. Numeric superscripts such as `cells/mm3` stay visible; a single alphabetic superscript is removed from visible text and recorded in `FootnoteMarkers` with page, character offset and bounded context. The .NET ingestion boundary applies the same entity/dash cleanup before storing chunk text and source metadata.

`POST /documents/{documentId}/reparse` re-reads the stored PDF without deleting chunks. It compares normalized text page by page. Chunks whose page range overlaps a changed page are marked `ReReviewRequired`; accepted/embedded chunks return to `pending` and their vectors are cleared. Chunks on unchanged pages keep their review state and vectors. Re-parse is editor/owner-only, revision-claimed and refuses while another document operation is active. If parsing fails, existing text, chunks and reviews are kept. A subsequent review clears `ReReviewRequired`.

## Document reset, re-chunk and review integrity

In **Chunk Review → Document reset controls**, reset, re-parse and re-chunk are separate confirmed/explicit actions. None of these actions automatically run an analysis, publish configuration or change the Colab connection.

| Method | Route suffix under `/api/workspaces/{id}` | Access / body |
|--------|--------------------------------------------|---------------|
| POST | `/documents/{documentId}/reset-pages` | Editor/owner; `{ pages: [1, 3] }`, 1–1000 parsed page numbers |
| POST | `/documents/{documentId}/reset-embeddings` | Editor/owner; no body |
| POST | `/documents/{documentId}/reparse` | Editor/owner; no body; non-destructive page comparison, re-review marking and stale-vector clearing |
| GET | `/documents/{documentId}/chunker-guidance` | Member; latest guidance or server default |
| GET | `/documents/{documentId}/processing-state` | Member; lightweight polling projection: scalar status, page/count evidence, latest chunk-job view; no text or vectors |
| POST | `/documents/{documentId}/chunk-jobs` | Editor/owner; `{ pages, guidance?, batchSize?, maxTokens?, batchTimeoutSeconds?, maxRetries?, failureMode? }`; returns 202 |
| GET | `/documents/{documentId}/chunk-jobs/active` | Member; active job view or `null` |
| GET | `/documents/{documentId}/chunk-jobs` | Member; latest 20 job views |
| GET | `/chunk-jobs/{jobId}` | Member; one job view used for polling |
| POST | `/chunk-jobs/{jobId}/pause` | Editor/owner; pauses after the current batch (queued jobs pause immediately) |
| POST | `/chunk-jobs/{jobId}/resume` | Editor/owner; returns a paused/interrupted/blocked job to the queue |
| POST | `/chunk-jobs/{jobId}/cancel` | Editor/owner; stops after the current batch; completed pages are kept |
| POST | `/chunk-jobs/{jobId}/retry` | Editor/owner; `{ scope: "failed" \| "remaining" \| "all" }`; creates a new linked job |
| POST | `/documents/{documentId}/chunk-portions` | Editor/owner; legacy synchronous bridge `{ pages, guidance?, maxTokens?, batchSize? }` |
| PATCH | `/chunks/{chunkId}` | Editor/owner; `{ status?, text?, section? }` |
| POST | `/chunks/bulk-accept` | Editor/owner; `{ documentId, minConfidence? }` |
| POST | `/chunks/merge` | Editor/owner; `{ ids: [chunkId1, chunkId2] }` |
| POST | `/chunks/bulk` | Editor/owner; content-only import array |

Reset pages deletes every chunk/vector whose source range overlaps a selected page, regardless of review status. It clears processed-page flags for the entire deleted range, not just the selected page, while preserving unrelated/gap chunks. PDF, parsed text and guidance remain. After success, verify the returned `resetPages` and refreshed counts, choose the desired reset pages, then explicitly start a background chunk job (or use the legacy **Chunk Selected** bridge). Reset cannot be undone; regenerate/review from the source PDF.

Embedding reset clears binary/model/legacy vector fields and embedding-progress counters for the document, returns embedded chunks to accepted and preserves source text, review attribution and processed-page flags. It is document-scoped; changing encoder identity still requires invalidating **all active workspace vectors**, not just one document or relabelling old bytes.

Durable chunk jobs accept 1–1000 pages, batch size 1–10 (default 2), 100–2000 tokens (default 400), per-batch timeout 60–600 seconds (default 300), 0–3 retries (default 1), failure mode `continue` or `stop` (default `continue`) and at most 4000 guidance characters. The legacy synchronous endpoint keeps its 1–20 pages-per-batch contract. Valid no-op, accepted and subsequently failed calls persist guidance; invalid/busy/reviewed-range-conflicting calls do not. Untouched UI fields follow saved guidance, while local edits survive refetch. Partial failures/cancellation retain committed checkpoints. Upstream chunk ranges must fit entirely inside the requested batch; malformed/out-of-batch results do not replace retained chunks or mark pages processed.

The background worker is enabled by `Documents:ChunkJobs` (default true). It leases a job per batch and recovers abandoned `running`, `pausing` or `stopping` leases as `interrupted`; startup recovery does not automatically resume them. The job view exposes only safe status/error codes, page arrays and progress, never chunk or upstream text. Pause, cancel and failure-mode `stop` take effect at the next batch boundary. Stopping browser polling or leaving the page does not cancel the job. If the API restarts mid-batch, completed batches remain committed and the job must be resumed or retried explicitly. Retry creates a new job and retains the original history.

Manual review allows pending, accepted or rejected, **never embedded**. Review, bulk acceptance and merge record `ReviewedBy`/`ReviewedAt` through `ICurrentUser`, not posted provenance. Reviewed text/section edits invalidate all vectors and return to accepted; edited/merged/imported text is not falsely labelled verbatim. Same-document merge requires 2–1000 distinct chunks, valid overlapping/adjacent page ranges without gaps, compatible years and combined section/hierarchy at most 256 characters. It preserves full ranges, sections/year and strongest strength/certainty, rejects originals and clears their vectors. Imports reject unknown fields (including vectors, status, identities and review attribution) and always begin pending.

Document revision/processing claims prevent concurrent resets/reviews from overwriting active or newly committed processing. Chunk Review gates writes on verified editor/owner access, polls access during owned work, dismisses reset confirmations on detected permission loss and aborts the request without applying late feedback. Workspace exit cancels reads before scoped cache removal; confirmed self-removal uses the internal user ID and cannot navigate a replacement user/workspace after late cleanup.

## Secrets and credentials

Provision these outside tracked configuration. Do not commit them to version control.

| Secret | Purpose | Scope |
|--------|---------|-------|
| `Colab__RegistrationSecret` | Shared secret for notebook startup registration (`X-Colab-Registration-Secret`) | ASP.NET backend |
| `Pipeline__ApiKey` | Service credential for Python → ASP.NET connection resolution (`X-Pipeline-Key`) | ASP.NET backend, Python pipeline |
| `Operations__MetricsKey` | Key for authenticated `/metrics` access (`X-Operations-Key`) | ASP.NET backend |
| `Operations__AdminFirebaseUids__0` (then `__1`, etc.) | Indexed environment entries for platform administrator Firebase UIDs; appsettings uses an array | ASP.NET backend |
| `DataProtection__KeyRingPath` | Directory for Data Protection key-ring persistence (default `backend/key-ring`) | ASP.NET backend |

Data Protection encrypts the Colab API key before database storage. Browser status responses never expose plaintext or protected keys. On Windows, keys are additionally protected with DPAPI. On Linux, key-ring file ACLs are the only protection; restrict the directory to the service account.

A Firebase bearer token — including an administrator's — is **not** sufficient for Python's `X-Pipeline-Key` endpoint. Python production resolution uses `PIPELINE_RETRIEVE_BASE_URL` and `/api/internal/colab-connection`, authenticated by `PIPELINE_SERVICE_KEY`.

## Data Protection deployment

Default key-ring path: `backend/key-ring` under the ASP.NET content root. On Windows, `ProtectKeysWithDpapi()` is used. For shared or persistent deployments:

1. Set `DataProtection__KeyRingPath` to a directory outside the application tree, backed up regularly.
2. Restrict directory ACLs to the service account (and, for key restoration, a DBA/admin principal).
3. Rotate keys by adding a new key to the ring; old keys remain for decryption of previously protected values.
4. Do not share key-ring directories between ASP.NET and Python; Python receives already-decrypted credentials over `X-Pipeline-Key`.

## Platform administration

Administration fails closed behind `Operations:AdminFirebaseUids`. Workspace ownership and development authentication **cannot** bootstrap an administrator. An admin identity requires:

- `Firebase__ProjectId` configured (not dev mode)
- Authenticated Firebase bearer with `email_verified = true`
- `ClaimTypes.NameIdentifier` (or `user_id` / `sub`) matching an entry in `Operations__AdminFirebaseUids`

Admin endpoints:

| Method | Route | Access | Description |
|--------|-------|--------|-------------|
| GET | `/api/admin/access` | Authenticated | Returns `isAdmin` |
| GET | `/api/admin/colab-settings` | Platform admin | Returns connection status including base URL (admin-only) |
| PUT | `/api/admin/colab-settings` | Platform admin | Manual connection update with expected-revision concurrency |
| POST | `/api/admin/colab-registration` | Shared secret | Notebook startup registration (no Firebase required) |

## Colab connection management

The Colab base URL and API key are stored in the database under admin-only control. There is **no** appsettings URL/key fallback for production Colab connections. Python resolves rotating credentials through ASP.NET's authenticated internal endpoint.

### Manual connection (admin)

Use `PUT /api/admin/colab-settings` with `baseUrl`, `apiKey` (optional), `manualOverride` and `expectedRevision`. The revision must match the current value or the request conflicts. Setting `manualOverride = true` blocks notebook registration and heartbeats; releasing it requires the notebook to re-register through the tunnel cell.

### Notebook registration

The Colab notebook registers through `POST /api/admin/colab-registration` with header `X-Colab-Registration-Secret` matching `Colab__RegistrationSecret`. The notebook sends `baseUrl`, `apiKey`, `instanceId` (32 hex chars) and `heartbeat`.

Validation:
- Base URL must be public HTTPS, no path/credentials/query/fragment, port 443 or default
- API key: 16–4096 printable ASCII characters without spaces
- Instance ID: exactly 32 lowercase hexadecimal characters
- Registration secret must match

Responses:
- `200 { registered: true }` — success; never reflects URL, credentials or payload
- `401` — registration secret mismatch
- `409 { code: "manual_override" }` — admin manual override blocks registration
- `409 { code: "superseded" }` — heartbeat from a different instance ID than the registered one
- `400` — invalid base URL or API key

### Heartbeats

The notebook re-registers with `heartbeat=true` approximately every 30 seconds. A heartbeat updates only `LastHeartbeatAt`; it never changes the URL, key or revision. Any heartbeat with a different instance ID, URL or key is rejected as superseded and the notebook stops heartbeating. Only explicit startup registration (`heartbeat=false`) can rotate the connection, and manual override still blocks it.

### Manual override

When `manualOverride` is active:
- Notebook registration and heartbeats are rejected with `manual_override`
- Settings UI shows "Active — notebook registration blocked"
- To release: set `manualOverride = false` via `PUT /api/admin/colab-settings`, then rerun the notebook tunnel/registration cell

Old heartbeats cannot restore the connection after override release; the notebook must re-register.

## Health, readiness and metrics

| Endpoint | Auth | Description |
|----------|------|-------------|
| `GET /health` | Anonymous | Liveness: `{ alive: true }` |
| `GET /ready` | Anonymous | Readiness: database, Colab model, Python pipeline. Returns 200 when all ready, 503 with `Retry-After: 30` otherwise. Includes Colab model name and GPU information. |
| `GET /metrics` | Metrics key or platform admin | Prometheus text format with fixed step/outcome labels |
| `GET /api/operations/colab` | Authenticated (email_verified) | Safe connection/model status: no base URL, plaintext key or protected key |

Readiness requires all three: database connectivity (plus the operations migration table), Colab `ready == true` with model name, and Python pipeline readiness. Colab readiness reports model, embedding model, dimensions and GPU info (available, name, device, memory).

Metrics use fixed labels only: `step` and `outcome`. Inputs never enter logs, spans or labels. Step names are restricted to a fixed set (e.g. `colab.ready`, `colab.generate`, `colab.retrieve`, `pipeline.analyze`, `http.server`). Outcomes are `success`, `error` or `cancelled`.

ASP.NET emits structured UTC JSON logs. `X-Request-Id` accepts only 32 hexadecimal characters (normalized to lowercase); otherwise ASP.NET creates a new ID and returns it to the browser. The same ID is forwarded to Python, its retrieval/connection callbacks and Colab. Logs contain fixed step summaries, SHA-256 input hashes/lengths, route templates and exception type names, not raw paths/queries, case text, prompts, credentials, upstream bodies or exception messages/stacks. The inactive Gemini/Jina provider error paths are sanitized too; this does not enable them or change the active Colab retrieval implementation. Disable body/header/tunnel inspection logging in the deployment as well.

## Circuit breaker and resilience

Colab uses `AddHttpClient<IColabClient, ColabClient>` and a bounded process-local circuit breaker separated by endpoint + credential identity:

- Default threshold: 5 failures; default break: 30 seconds; maximum 32 concurrent circuit keys (LRU eviction)
- Credential rejection (401/403) and redirects (3xx) count as circuit failures without retries
- Only explicitly replayable calls retry: retrieval, rerank, PDF parsing and job polling. Default `MaxRetries=2` permits up to **3 attempts**; readiness does not retry and embedding transport retries are disabled because the checkpointed batch client owns retries. Generation and chunk submissions/raw chunk operations are never replayed
- Each attempt has a deadline covering transport and response-body buffering; polling also has an overall job deadline. Caller cancellation propagates without counting as an upstream failure
- Jittered retry delay honors `Retry-After` subject to a 30-second delay cap
- Response bodies are capped at 32 MiB; non-cooperative handlers are disposed on deadline
- Stale in-flight completions cannot close a circuit opened by a newer failure (epoch-based)
- After cooldown only one logical recovery call is admitted. Concurrent probes receive sanitized 503 without reaching transport; success closes, failure reopens for a fresh cooldown, and cancellation/disposal releases the probe slot without claiming recovery

When the breaker is open or dependency is unavailable, ASP.NET returns sanitized 503 responses with `Retry-After` and `dependency: colab` (or `pipeline`/`database`).

## Operations database migration

Migration `20261115125916_ColabOperationalConnection` adds the `ColabConnections` table:

- `Id` (always 1, never auto-generated), `BaseUrl` (nvarchar 2048), `ProtectedApiKey` (nvarchar 8192, Data Protection encrypted), `Revision` (concurrency token), `ManualOverride`, `InstanceId` (32 hex), `UpdatedAt`, `LastHeartbeatAt`

### Downgrade protection

Automatic downgrade refuses with SQL error **51001** if any row exists in `ColabConnections`:
```
THROW 51001, 'Export and explicitly remove the operational Colab connection before downgrading this migration.', 1;
```

Export the connection (base URL and revision, never the API key) and explicitly remove the row before downgrading. This is separate from the binary embedding downgrade guard (SQL error 51000).

### Migration validation

- Fresh database: all current migrations apply (8 as of 15 November 2026); `ColabConnections` table is empty
- Idempotent schema script: repeat application preserves all data
- Downgrade without new binary vectors restores the legacy JSON column but never reasserts indexing
- Downgrade with new binary vectors refuses (51000) and leaves all data unchanged

## Frontend availability and offline banner

The frontend distinguishes an unreachable API from an offline model service:

- `GET /ready` probes the backend; dependency-specific 503 responses set a shared outage state
- A global banner shows the affected dependency (`backend`, `database`, `pipeline` or `colab`) and a suggested retry interval
- Requests are not automatically resubmitted; 401 triggers one token refresh then sign-out
- Only string bodies get `Content-Type: application/json`; `FormData` and binary bodies keep browser-generated types
- Timers and external abort listeners are released in `finally`. Token retrieval/refresh, sign-out and response-body reads share the five-minute deadline. Caller cancellation is not reported as an outage
- JSON error bodies are limited to **16 KiB while reading**, and the remaining stream is cancelled on overflow; readable messages are capped at 512 characters with control characters removed. HTML/plaintext/malformed JSON and all 5xx raw bodies/stack traces are not displayed
- Responses and SSE events from a replaced account are discarded; a non-cooperative late fetch response is cancelled instead of being returned to the new account
- A successful Colab status request reporting `model.ready=false` still activates the Colab banner. Unrelated successful API requests do not prove model recovery; a verified readiness/status probe clears the matching outage

## Operations settings UI

The workspace Settings page shows:

- **Live status**: Ready / Offline or not ready / Status unavailable (refreshed every 30 seconds)
- **Model**: Colab generation model name
- **Embedding model** and dimensions
- **GPU**: availability and name (device/memory are in the readiness payload)
- **Last heartbeat**: timestamp with stale/recent indicator
- **Manual override**: active/inactive

Only provisioned platform administrators can edit the connection. Workspace ownership does not grant platform administration. The admin form edits base URL (public HTTPS only) and optional replacement API key (16–4096 printable ASCII, no spaces). Changes are revision-conflict protected and affect every workspace.

## Operations validation — 15 November 2026

The audit retained the existing JSON telemetry, typed transport, readiness and protected database registration implementation rather than replacing it. Follow-up fixes enforce single half-open admission, remove raw failures from the inactive Gemini/Jina providers, and harden frontend streamed error bounds, auth deadlines, account ownership and status-driven outage detection. Startup examples now use database rotation/shared-secret registration, not ignored static URL/key environment variables.

| Check | Result |
|-------|--------|
| `dotnet test backend.Tests --nologo --no-restore` | **669 passed / 2 existing harness failures / 671 total** |
| Focused Colab, operations readiness and legacy-provider privacy regressions | **53 passed** |
| `python -m pytest tests -q` | **372 passed**, including **14 new operations tests** |
| `npm test` in `frontend/` | **543 Vitest + 15 Node passed**, including **35 API + 3 banner + 4 Settings tests** |
| TypeScript and changed-file ESLint | Passed |
| Backend Release and frontend production builds | Passed with existing dependency/native SWC fallback warnings |
| Full frontend lint | Unchanged **3 errors / 7 warnings**, outside the operations files |

The two backend failures remain `TestHarnessTests.Comparer_detects_missing_and_unexpected_problems` and `TestHarnessTests.Failing_expectation_makes_run_fail_with_diff`. This work does not unblock configuration publication or certify the unfinished draft-bound harness.

Coverage includes controlled-clock cooldown/probe concurrency, success/failure/cancellation recovery, stale completions rejected **before** HTTP, idempotent retries/deadlines, missing readiness schema, sanitized logging/failures, correlation forwarding, dynamic Python connection rotation, registration authentication/override/supersession and stopped old heartbeats. Frontend tests cover body-aware headers, one refresh/retry then sign-out, listener/timer cleanup, late response cancellation, byte-bounded errors, SSE ownership and explicit banner recovery. Providers/models, Firebase and GPU are stubbed; no deployment migration, live notebook registration or real clinical request was performed. Existing evaluation artifacts and retrieval semantics are unchanged.

### Operations live acceptance checklist (not yet verified)

1. Back up/apply the reviewed schema to a test deployment; persist and restrict the Data Protection key ring. Provision verified Firebase admin UIDs and strong registration/pipeline/metrics secrets. Serve the notebook registration origin over public HTTPS.
2. Confirm `/health` stays live while each dependency is stopped; `/ready` becomes 503/no-store with `Retry-After`, then returns real model/embedding/GPU metadata when all recover.
3. Run a synthetic request through ASP.NET → Python → retrieval/connection callbacks → Colab. Confirm one `X-Request-Id`, fixed-label latency metrics and hash/length-only input summaries; inspect proxy/tunnel logging for leaks too.
4. Restart Colab and verify registration changes the DB connection, Settings heartbeat/status and the next Python/model call without editing static configuration. Confirm old heartbeats cannot restore a superseded connection.
5. Enable manual override, verify notebook registration is blocked, then release and explicitly rerun registration. Verify ordinary workspace owners and unverified users cannot edit settings; Firebase tokens alone cannot call the internal connection endpoint.
6. Stop Colab during an idempotent request and during generation/chunk submission. Verify sanitized 503/retry hints and breaker recovery; confirm generation/submissions are not automatically replayed. A client timeout is not proof a mutation failed—refresh server state before any explicit retry.
7. Verify the global dependency banner, explicit connection check, token rejection/sign-out and account-switch cancellation with designated test accounts. Do not treat these smoke tests as clinical-quality evidence.

## Management validation — 15 November 2026

| Check | Result |
|-------|--------|
| `dotnet test backend.Tests --nologo --no-restore` | **650 passed, 2 failed, 652 total**; only the two pre-existing harness failures listed below |
| Focused management, migrations, document processing/chunk portions | **131 passed**, plus **2 passed** file-cleanup tests |
| `python -m pytest tests -q` | **347 passed** |
| `npm test` in `frontend/` | **501 Vitest + 15 Node passed** |
| `npx tsc --noEmit --incremental false` | Passed |
| ESLint on changed management/API/reset components and regressions | Passed |
| `npm run lint` | Same **3 errors / 7 warnings**, outside management files |
| `npm run build` | Passed with existing native SWC → WASM fallback warnings |
| `dotnet build backend/ClinicalRag.Api.csproj --configuration Release --no-restore --nologo` | Passed with existing dependency advisories |

The backend failures are `TestHarnessTests.Comparer_detects_missing_and_unexpected_problems` and `TestHarnessTests.Failing_expectation_makes_run_fail_with_diff`; both still lack the expected missing-problem diff. They are not new management regressions. Publication remains blocked by the registered `NoConfigTestRunProvider`; the unfinished draft-bound harness/publication integration is not certified by these management checks.

Regression coverage includes verified-email/stale invitation behavior, HTTP owner authorization and deleted-workspace denial, last-owner/concurrent membership changes, spanning/disjoint page resets, invalid source ranges, review attribution/vector invalidation, content-only imports, self-removal/late-callback cache ownership, and cleanup rejection of a junction **above** the configured root. Real LocalDB tests validate current model/schema equality, idempotent migration scripts, each SQL 51002 guard and an empty-management-schema downgrade/upgrade. No deployment migration or real uploaded guideline reset/deletion was performed.

The subsequent retrieval follow-up reran the full suites: **652 backend passed / the same 2 harness failures / 654 total**, **358 Python passed**; **104 focused retrieval tests** and **8 live-Full-Text-required SQL tests** passed. Debug/Release builds pass. No frontend changes/checks were added in that follow-up. The original management results above are retained as dated history; see the [current retrieval validation](hybrid-retrieval-pr.md#validation) and preserved measurement artifacts.

### Live acceptance checklist (not yet verified)

Use synthetic/de-identified documents and designated test accounts, not patient data or production owner accounts:

1. Apply the reviewed migration to a backed-up test deployment; configure the stable upload root/ACLs, Firebase, Python and a ready Colab GPU service.
2. Save/cancel an invitation; verify no mail-delivery claim. Sign in with unverified/wrong email (no access), then the matching verified email (exact pending role, once).
3. Promote a second owner, change/remove/leave membership, verify last-owner protection and permission-loss cancellation, and confirm self-removal exits without stale workspace data.
4. Chunk/review/embed a source fixture; reset a spanning range and a disjoint selection, verify exact deleted chunks/cleared flags and latest guidance, then explicitly re-chunk/review/embed. Confirm reset itself starts no Colab work.
5. Soft-delete the test workspace; confirm all resource routes deny former members, file cleanup completes or leaves durable records/warnings, and non-document history remains inaccessible through ordinary workspace routes.

Mocked UI/model tests and synthetic retrieval measurements do not establish live clinical-quality performance, genuine HIV-question provenance or a dated clinical evaluation. Those remain separate acceptance work.

## Remaining acceptance limits

| Item | Status |
|------|--------|
| Warm retrieval p95 target <100 ms | LocalDB/BM25 **34.370 ms** with stub models; original EF InMemory **122.799 ms**. Deployed model/network/concurrency latency still **unverified**; see [retrieval measurements](hybrid-retrieval-pr.md) |
| Live Colab GPU/Firebase end-to-end | **Unavailable** |
| Representative clinical-quality validation | **Unavailable** |
| Configuration publication | **Blocked** by `NoConfigTestRunProvider` pending Prompt 11 evidence |

Known dependency advisories: `System.Security.Cryptography.Pkcs` and test SQLite packages. Previous frontend lint reported three errors/seven warnings.
