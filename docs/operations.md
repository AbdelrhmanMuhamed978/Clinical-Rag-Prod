# Clinical RAG Platform — Operations

This document covers workspace/document administration, the Colab model service connection, health/readiness, metrics, and platform administration. It does not cover retrieval tuning, which is described in [retrieval-deployment.md](retrieval-deployment.md) and the [hybrid retrieval PR report](hybrid-retrieval-pr.md).

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

Workspace deletion is a soft-delete of the workspace, **not** of its document content. It is blocked while any document is being processed. The transaction explicitly removes chunks, guidance, documents and pending invitations despite Restrict FKs, queues uploaded-file deletion and sets `DeletedAt`/`DeletedBy`. It retains the tombstone, memberships, accounts, configuration/rule/catalog/audit history, analyses and test history; normal workspace authorization denies all access afterwards. There is no restore or hard-purge API. Treat retained analyses/test inputs according to a separate approved retention/erasure policy; workspace deletion alone does not satisfy full data erasure.

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

## Document reset, re-chunk and review integrity

In **Chunk Review → Document reset controls**, reset and re-chunk are separate confirmed/explicit actions. None of these actions automatically run an analysis, publish configuration or change the Colab connection.

| Method | Route suffix under `/api/workspaces/{id}` | Access / body |
|--------|--------------------------------------------|---------------|
| POST | `/documents/{documentId}/reset-pages` | Editor/owner; `{ pages: [1, 3] }`, 1–1000 parsed page numbers |
| POST | `/documents/{documentId}/reset-embeddings` | Editor/owner; no body |
| GET | `/documents/{documentId}/chunker-guidance` | Member; latest guidance or server default |
| POST | `/documents/{documentId}/chunk-portions` | Editor/owner; `{ pages, guidance?, maxTokens?, batchSize? }` |
| PATCH | `/chunks/{chunkId}` | Editor/owner; `{ status?, text?, section? }` |
| POST | `/chunks/bulk-accept` | Editor/owner; `{ documentId, minConfidence? }` |
| POST | `/chunks/merge` | Editor/owner; `{ ids: [chunkId1, chunkId2] }` |
| POST | `/chunks/bulk` | Editor/owner; content-only import array |

Reset pages deletes every chunk/vector whose source range overlaps a selected page, regardless of review status. It clears processed-page flags for the entire deleted range, not just the selected page, while preserving unrelated/gap chunks. PDF, parsed text and guidance remain. After success, verify the returned `resetPages` and refreshed counts, choose the desired reset pages, then explicitly press **Chunk Selected**. Reset cannot be undone; regenerate/review from the source PDF.

Embedding reset clears binary/model/legacy vector fields and embedding-progress counters for the document, returns embedded chunks to accepted and preserves source text, review attribution and processed-page flags. It is document-scoped; changing encoder identity still requires invalidating **all active workspace vectors**, not just one document or relabelling old bytes.

Chunk requests accept 1–1000 pages, 1–20 pages per batch (default 2), 100–2000 tokens (default 400) and at most 4000 guidance characters. Valid no-op, accepted and subsequently failed calls persist guidance; invalid/busy/reviewed-range-conflicting calls do not. Untouched UI fields follow saved guidance, while local edits survive refetch. Partial failures/cancellation retain committed checkpoints. Upstream chunk ranges must fit entirely inside the requested batch; malformed/out-of-batch results do not replace retained chunks or mark pages processed.

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
