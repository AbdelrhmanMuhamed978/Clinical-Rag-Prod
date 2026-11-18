# Clinical RAG Platform

Clinical guideline decision support (pipeline v3.2). Only recommendations that pass deterministic quote, number, wording and scope checks appear as answers. This is not a substitute for clinical judgement; use de-identified cases only.

New to the application? Start with the [user guide](docs/user-guide.md) for upload, chunking, review, Playground, tests, publishing, rules and troubleshooting.

## Services and data flow

| Service | Local URL | Responsibility |
| --- | --- | --- |
| Next.js frontend | `http://localhost:3000` | Firebase sign-in, verified results, tab-local conversation and My analyses |
| ASP.NET API | `http://localhost:5100` | Workspace authorization, published configuration, SQL retrieval and privacy-safe audit/storage |
| Python FastAPI pipeline | `http://127.0.0.1:8100` | Planning, retrieval orchestration, drafting and deterministic verification |
| Colab model server | Tunnel to port `8000` | Generation, embeddings, reranking, PDF parsing and chunking |

```text
Browser -- Firebase bearer --> ASP.NET -- bearer + X-Pipeline-Key --> Python
Python  -- bearer -----------> ASP.NET /api/workspaces/{id}/analyze/retrieve
Python  -- X-Colab-Key ------> Colab /generate
ASP.NET -- X-Colab-Key ------> Colab /embed and /rerank
```

The verified pipeline runs as its own Python service, **not on Colab**. The web analysis path does not use Colab's legacy Mongo `/retrieve` route and does not perform LLM query decomposition inside ASP.NET.

1. ASP.NET requires workspace **editor** access for analysis and selects the published configuration (a default published version is bootstrapped for a new workspace). Draft versions never serve live analyses.
2. Every Python request constructs an isolated `config.Config`; only that workspace's currently reviewed, approved and enabled database rules are supplied. Web requests never fall back to the local `python-backend/rules.json` table.
3. For cases, `python-backend/facts.py` extracts values and evaluates rules in code. A question is any interrogative input or any input with no extracted vitals/labs/drugs/age/sex, regardless of length; case planning uses facts. Question plans map each problem to a stated `covers` substring and reject unmapped problems. Short questions use reference-aware rewriting.
4. Python retrieves focused queries through ASP.NET. ASP.NET embeds each query, searches a cached float32 workspace index plus SQL Full-Text or BM25, fuses ranks with RRF and reranks at most 30 candidates; Python merges sources per problem.
5. Colab drafts `SOURCE / RECOMMENDATION / QUOTE` items. Python verifies quotations against retrieved text through one canonical formatting view (Unicode NFKC, entities, whitespace/dash/punctuation and parser-recorded footnote markers), requires an exact canonical substring, allows one exact table row label plus one exact cell, and still checks numbers, wording strength, scope, conditions, negation and topic. Truncated generations are rejected. Verified items are then scored against the question with the Colab reranker plus deterministic term overlap; below-threshold items move to a non-lead **Related, may not answer your question** group.
6. The response is the pipeline schema: `plan_source`, `problems`, `related`, `coverage`, `not_covered`, `warnings`, `disclaimer` and `version`. Each question part has an answered / related-only / not-covered coverage row. Verified items include quotations and source-derived citation metadata. Excluded drafts and verifier reasons stay inside a collapsed **Unverified (not shown as answers)** disclosure; they never enter copy/export or verified memory.
7. **Not covered** means no verifiable answer was found in the loaded guidelines, not that a topic is unimportant or safe. Processing failures appear separately as **Analysis incomplete**, with coverage unknown. Verifier locks and these distinctions cannot be disabled by workspace settings.

## Repository layout

- `python-backend/` — local deterministic Python service, core pipeline modules, shared config/JSON assets and its `requirements.txt`.
- `colab/` — GPU model server, notebook and Colab bootstrap.
- `backend/` — ASP.NET API.
- `frontend/` — Next.js UI.
- `eval/`, `tests/`, `scripts/` — evaluation, regression tests and tooling.

See [Python backend layout](docs/python-backend-layout.md) for the service command and import-resolution details.

## Configuration and startup

`.env.example` documents the settings; it is **not** an automatic environment loader for ASP.NET or Python. Set server variables in the service environment before startup. Only the `NEXT_PUBLIC_*` settings belong in `frontend/.env.local`; never place server keys in frontend variables or commit real credentials.

`backend/appsettings.example.json` is the credential-free backend template. For local use, copy it to the ignored `backend/appsettings.json` and configure the SQL connection and matching Firebase project through private environment settings **before startup**. Never run/deploy with a missing Firebase project. The existing configured appsettings file, legacy `test_e2e.ps1` copies, raw conversation export and browser captures are preserved locally but excluded from the baseline because they contain credentials or unreviewed private data. Generated archives are also local-only; their source remains reviewable. `eval/results/` is not ignored: preserve successful and failed evaluation artifacts without treating them as clinical validation.

## Version control and required review

The baseline starts on `main`. Later work belongs on a short-lived branch and must be reviewed through a pull request; do not fix existing red checks by hiding failures or weakening clinical safeguards.

Repository administrators must protect `main` with these rules:

- Require a pull request, at least one independent approving review and **code-owner review**; dismiss stale approvals and require approval of the latest push by someone other than its author.
- Require resolved review conversations and branches up to date before merging.
- Require the checks **dotnet**, **pytest**, **frontend**, **secret-scan** and **ci-gate**; a failed, cancelled, missing or skipped required job blocks merging.
- Apply the rule to administrators; do not allow bypass actors, direct pushes, force pushes or branch deletion.
- Keep `.github/CODEOWNERS`, the workflow and this rule under code-owner review. A sole author cannot approve their own PR: provision an independent reviewer with write access before merging.

These are **server-side GitHub settings**, not protections that `git init`, CODEOWNERS or this README can enforce locally. Confirm the saved branch-protection settings and an actual test-branch Actions run before claiming CI is active. No secrets, uploads, key rings or real case text belong in commits or CI logs. Original audit reports describe the pre-Git workspace; keep that historical evidence unchanged.

See [CI validation and current red items](docs/ci-validation.md) and the [saved main-protection verification](docs/ci-main-protection.json). The workflow/PR template/CODEOWNERS are initially on `ci/review-gates`; they are not on `main` until a green, independently reviewed merge. GitHub uses the **base branch's CODEOWNERS** for file-owner matching; do not bypass the required checks to bootstrap it.

### 1. Colab

Open `colab/clinical_rag_colab.ipynb` with a GPU runtime and run the cells in order. Configure `COLAB_API_KEY` (16–4096 printable ASCII characters without spaces), `ASPNET_BACKEND_URL` (public HTTPS backend origin) and `COLAB_REGISTRATION_SECRET` in Colab Secrets **before** starting the public tunnel. The registration secret must match backend `Colab__RegistrationSecret`; the backend must already be running with the operations migration applied. All model, legacy retrieval, parsing and chunking routes require `X-Colab-Key`; only minimal `/health` is public. Bounded chunking batches use `/chunk/jobs`; the ASP.NET background worker owns Colab polling and persists progress per batch. The browser polls a lightweight workspace-scoped processing-state view and refreshes the full chunk evidence only when state changes or on demand.

The tunnel cell waits for local model readiness, registers the rotating URL/key in ASP.NET's protected database setting, then sends approximately 30-second heartbeats. Neither ASP.NET nor the production Python service uses an appsettings/environment Colab URL/key fallback. Settings shows model/GPU/heartbeat status; only a provisioned platform administrator can edit the connection or enable manual override. After releasing override, rerun the tunnel/registration cell.

The user's bearer token is sent only to ASP.NET retrieval, never to Colab. The Colab key is sent only to Colab, never to the frontend or retrieval callback.

### 2. Python pipeline

The local Python service and its deterministic pipeline modules live in
[`python-backend/`](python-backend/). Use a separate environment with
`python-backend/requirements.txt`. Example PowerShell commands from the
repository root:

```powershell
python -m venv .venv
.\.venv\Scripts\Activate.ps1
python -m pip install -r python-backend/requirements.txt

# Supply secrets securely in this service's environment, not in source files.
$env:PIPELINE_SERVICE_KEY = "<pipeline shared secret>"
$env:PIPELINE_RETRIEVE_BASE_URL = "http://localhost:5100"
$env:PIPELINE_TIMEOUT_SECONDS = "240"
python -m uvicorn --app-dir python-backend pipeline_service.app:app --host 127.0.0.1 --port 8100 --no-access-log
```

`POST /analyze` returns JSON; `POST /analyze/stream` emits real `progress`, then `result` or `error` SSE events. Both require `X-Pipeline-Key` **and** a bearer token. ASP.NET supplies `text`, `config`, `rules`, `history`, `workspaceId`, `retrieveUrl` and optional `conversationId`; the browser must not supply trusted configuration or rules directly to this service.

`retrieveUrl` must match `PIPELINE_RETRIEVE_BASE_URL` exactly in origin and base path, followed by `/api/workspaces/{workspaceId}/analyze/retrieve`. Other origins, workspace paths, query strings, fragments and credentials in URLs are rejected. Set backend `Backend__PublicUrl` to the same base URL, especially behind a reverse proxy. Python captures authentication and retrieval-origin settings when `create_app()` runs: restart after changing them.

Requests have a 1 MiB body limit and a deadline. Streaming errors omit request text and upstream bodies; disconnects cancel pipeline work. Keep proxy buffering disabled for SSE and disable request-body, prompt and credential logging throughout the deployment, including tunnel inspection/retention. Keep the pipeline on a private interface/network where possible.

### 3. ASP.NET API

Configure the SQL Server connection and Firebase project, then set these variables in the backend's shell:

```powershell
$env:Firebase__ProjectId = "<Firebase project ID>"
$env:Backend__PublicUrl = "http://localhost:5100"
$env:Pipeline__BaseUrl = "http://127.0.0.1:8100"
$env:Pipeline__ApiKey = "<same PIPELINE_SERVICE_KEY>"
$env:Colab__RegistrationSecret = "<same notebook COLAB_REGISTRATION_SECRET>"
$env:Operations__AdminFirebaseUids__0 = "<verified platform admin Firebase UID>"
$env:Operations__MetricsKey = "<separate metrics access secret>"
dotnet run --project backend/ClinicalRag.Api.csproj --no-launch-profile --urls http://localhost:5100
```

Use matching Firebase projects in the frontend and backend. Do not deploy without `Firebase:ProjectId`: the existing no-project authentication fallback is for local development only. Set `ASPNETCORE_ENVIRONMENT=Development` explicitly when local automatic migrations are wanted; production deployments should apply reviewed migrations separately.

`GET /health` is dependency-independent liveness. `GET /ready` checks the database schema, Colab model/GPU and Python, returning sanitized 503 with `Retry-After` when unavailable. JSON logs use correlation IDs and input hashes/lengths, never case text, prompts or credentials; fixed-label latency metrics are available through protected `/metrics`. See [operations and rollout](docs/operations.md#health-readiness-and-metrics), including key-ring persistence and admin/shared-secret boundaries.

### 4. Frontend

Copy only the public API/Firebase settings from `.env.example` to `frontend/.env.local`, then run `npm ci` and `npm run dev` from `frontend/`. Sign in, open a workspace, load/review/embed guideline chunks and open Playground. Analysis requires editor or owner membership.

## Embedding and hybrid retrieval

- Guideline embeddings are generated in batches of **32** by default, with bounded transient retries and a durable transaction per batch. Cancellation/failure retains successful batches; retry processes only the remaining accepted chunks. Committed chunk entities are detached to bound tracking memory.
- Every vector requires a valid `model` and exact `dimensions`, and is normalized into little-endian float32 `varbinary(max)`. Mixed model identities/dimensions are refused, including differently cased model names. Changing encoders requires invalidating all active workspace vectors first, not relabelling old bytes.
- Ingestion binds each chunk to deterministic structure metadata: `kind` (`recommendation`, `definition`, `table_row`, `clinical_consideration` or `summary`), title, year, verbatim body, strength, certainty, population and a stable recommendation ID. Graded recommendation-table rows are split into one atomic object per row with the header repeated; ungraded tables remain atomic. Adjacent recommendations cannot share a grade. See [semantic ingestion and retrieval](docs/semantic-ingestion-retrieval.md).
- Retrieval uses a contiguous `System.Numerics.Tensors` dense index and `Text + Section` lexical search. SQL Server Full-Text is optional; otherwise an immutable BM25 inverted index is used. RRF uses **k=60**. The focused query is expanded with deterministic domain synonyms from `python-backend/query-synonyms.json` (up to four variants); each variant contributes a dense and a lexical leg. Definition and summary sections receive a deterministic boost for definitional or broad questions. Configured candidates default to 30 and are effectively capped at 30 without changing the Workflow schema's 5–100 range. Optional HyDE (`retrieval.hydeEnabled`, default false) adds one hypothetical-answer query; model failure is non-fatal.
- Reranking receives at most **30 passages**, each limited to **2,000 Unicode code points** including its section. The Colab cross-encoder scores the exact question; a deterministic intent-alignment penalty then demotes narrow exception passages (for example TB-specific timing) on broad population questions. Returned source text is not truncated. The process-local cache defaults to **512 MiB / 120 seconds**; application commits invalidate it on embedding, review, edit and deletion. Other API instances/direct SQL writers are not distributed invalidation participants.
- Quote verification uses one canonical view that removes a hyphen lost at a PDF line wrap (`highdose` matches `high-dose`) while preserving digit ranges (`100-200`) and number-unit pairs (`24-hour`). Negation, number, strength and scope checks are unchanged.
- Fresh synthetic **10,000 × 768** measurements: warm LocalDB/BM25 production retrieval **12.108 ms p50 / 34.370 ms p95**; cached hybrid search alone **4.517 / 7.714 ms**. The original EF InMemory path still misses 100 ms (**97.630 / 122.799 ms**). Both use stub models; deployed SQL/GPU/network/concurrency and clinical quality remain unverified. Original JSON-to-binary before/after artifacts are preserved separately; none of these results authorize Workflow publication.

See [deployment and cache guidance](docs/retrieval-deployment.md) before upgrading an existing index, and the [PR-ready description and validation results](docs/hybrid-retrieval-pr.md) for measured tradeoffs and remaining acceptance gaps.

## Privacy, memory and saved analyses

- Every accepted analysis writes audit metadata: `AnalysisRun.UserId`, `WorkspaceId`, published `ConfigVersion`, SHA-256 `InputHash` and `CreatedAt`. Input text, extracted facts, patient summaries, conversation IDs and conversation history are not persisted by the analysis flow. A failed/cancelled run can remain audit-only.
- If `Privacy:StoreResults` is **unset**, results are stored for short questions but **not** for cases (at least 40 words), or follow-ups whose last six history turns include a case. `Privacy__StoreResults=false` disables all result storage; `true` explicitly opts in. Malformed explicit values fail closed (storage off).
- Even with case storage enabled, only guideline quotations/citations, source excerpts and verifier issues are retained: planner titles are replaced, queries and patient checks are removed, generated recommendations become source quotations, unverified draft text is excluded, and patient-derived warnings are not saved. Saved case statements must be rerun against current patient data before use.
- Conversation memory is in React state only: the last **six question/verified-summary turns**. Summaries contain verified recommendations and citation metadata, not drafts, planner labels or patient checks. History resolves references such as “what about the mother?” but is **not guideline evidence**. Clearing history, changing workspace/user, leaving the Playground or reloading discards it; cancellation/stale callbacks cannot append late answers.
- **My analyses** lists only the signed-in user's runs in the current workspace. Opening a saved result does not add it to conversation memory. Deletion requires confirmation and removes that user's audit row and stored result. Legacy free-text results are withheld, not automatically deleted.

Browser API routes (Firebase bearer required):

| Method | Route | Access |
| --- | --- | --- |
| POST | `/api/workspaces/{id}/analyze` | Editor; `{ message, conversationId?, history?: [{ question, verifiedSummary }] }` |
| POST | `/api/workspaces/{id}/analyze/stream` | Editor; same body, SSE response |
| POST | `/api/workspaces/{id}/analyze/retrieve` | Viewer; focused query callback |
| GET | `/api/workspaces/{id}/analyses?mine=true` | Viewer; own runs only (`mine=false` is rejected) |
| GET / DELETE | `/api/workspaces/{id}/analyses/{runId}` | Viewer; own workspace run only |

### Rule approval

Editors can create/edit rule definitions but cannot self-approve by posting review fields. An owner or reviewer approves a saved revision with `POST /api/workspaces/{id}/rules/{ruleId}/approve` and `{ expectedRevision }`, recording reviewer identity/time and recognition-catalog binding. Definition changes clear approval; exact no-ops preserve it and partial edits preserve omitted fields. Only current, approved and enabled rules enter the pipeline; non-case-level rules must also match the current catalog revision. Approval does not enable a rule or complete a Workflow step, and is separate from **owner-only configuration publishing**.

## Workspace and document administration

- **Settings** lets owners invite by email, cancel pending invitations, change/remove members, rename and soft-delete a workspace. All members can leave, but the last owner cannot leave, be removed or be demoted; assign another owner first. Reviewer is a separate **rule-review** capability, not editor or administrator access.
- An invitation saves **pending access**, not an email delivery. Share the app URL with the invitee. Access is accepted only when the same verified Firebase email next makes an authenticated request; other emails, unverified users and development identities cannot accept it. A stale invitation never upgrades an existing member. Owner invitations require an explicit full-administration warning and confirmation.
- Workspace deletion denies access and removes the workspace from lists. Documents, chunks, chunk jobs, guidance and uploaded files are deleted; the workspace tombstone, memberships and non-document history remain. Failed file cleanup is durably queued, not silently forgotten. This is **not** complete account/data erasure or a recoverable archive.
- **Chunk Review → Document reset controls** separates confirmed reset from re-chunking: resetting 1–1000 parsed pages deletes overlapping chunks/vectors and clears their processed-page flags. A spanning chunk also resets its other covered pages; disjoint selections do not delete gap chunks. The PDF and saved guidance remain. Afterwards explicitly start a background chunk job (or use the legacy **Chunk Selected** bridge); reset never starts Colab automatically.
- **Background chunk jobs** persist page checkpoints and settings, survive page reload, and are owned by a hosted worker rather than the HTTP request. The UI can pause after the current batch, resume, cancel, retry failed/remaining pages, and control polling for the whole page (auto-poll, 2/5/10/30-second interval, poll now, stop polling; preference saved per document). Closing the page or stopping polling does not cancel the job; only pause/cancel do. Per-batch failures honour `continue` or `stop`, and retry creates a new linked job while retaining history.
- **Reset embeddings** keeps source text, review attribution and processed-page flags, clears all vector fields/progress, and returns embedded chunks to accepted for explicit re-embedding. Processing claims/concurrency conflicts block unsafe resets and reviews.
- Guidance from valid chunk requests is persisted and prefills the latest saved value without overwriting local edits. Chunk review/bulk acceptance/merge record the authenticated reviewer. Edited reviewed text/sections return to accepted with vectors cleared; clients cannot mark chunks embedded. Merges require same-document adjacent/overlapping, valid page ranges and compatible source years, preserving sections and strongest grades; content-only imports always start pending.
- Workspace exit cancels scoped reads before dropping caches. Owned mutations are aborted and late callbacks ignored after user/workspace replacement or detected permission loss. Chunk Review requires verified editor/owner access and checks it while requests are in flight.

See the [administration routes, cleanup and migration runbook](docs/operations.md#workspace-administration) for request bodies, deletion retention and operator recovery.

## Workflow configuration lifecycle

Workflow exposes Read case, Plan, Retrieve, Draft, Verify and Reconcile settings, complete JSON preview and version differences. Read case displays shared canonical units and unitless count assumptions. Verifier explanations cover quote/citation, number, strength, scope, condition, negation, topic and truncation checks. Both panels are read-only; Reconcile edits existing rule link terms and uses the separate clinician-review flow above.

- Parsing/initialization does not reset typing on refetch. Complete source snapshots retain untouched and unknown keys; defaults and validation come from `python-backend/config/config.schema.json` and the existing default prompts.
- Version differences distinguish nested `future.a.b` from literal `future["a.b"]` and safely escape quotes, brackets and empty keys.
- Retrieval limits are `minRerank` 0–1, `topK`/`maxChunks` 1–20, `candidates` 5–100 and `queriesPerProblem` 1–5. Required prompt variables are literal tokens; JSON braces and other tokens remain allowed. The server forces `safety.verifierLocked=true` and never permits `minQuoteMatch` below 0.88.
- Focused ASP.NET retrieval uses safe shared defaults for incomplete/invalid legacy settings and honors whole-number JSON such as `10.0` and `1e1`; it never performs LLM query planning.
- Saving creates an immutable new draft, including when editing a loaded draft. Discard preserves history. Owner rollback restores a published/archived snapshot as a **new draft**, leaves publication unchanged and requires fresh tests.
- Dirty editors warn before guarded navigation, sign-out or document unload. A changed publication keeps dirty snapshots pinned and blocks saving until confirmed reload; clean editors initialize the new publication. Cancelable Navigation API Back/Forward traversal is guarded where supported; unsupported/noncancelable SPA traversal is not.
- Only an owner can publish, and only after the **latest real test** passes for that exact persisted workspace/configuration ID/version/SHA-256 artifact. A newer failure or in-flight run blocks publication. Step completion belongs to the loaded version's real evidence, never to opening settings, saving or merely selecting a passing version.

Browser lifecycle routes (Firebase bearer required):

| Method | Route | Access / body |
| --- | --- | --- |
| GET | `/api/workspaces/{id}/config/published` | Viewer; default published configuration bootstraps for a new workspace |
| GET | `/api/workspaces/{id}/config/versions` | Viewer; returns `{ role, versions }` with test status and blocking reasons |
| POST | `/api/workspaces/{id}/config/draft` | Editor; `{ configJson: "<JSON object string>", publishedConfigId? }` |
| POST | `/api/workspaces/{id}/config/{configId}/publish` | Owner; latest passing bound test required |
| POST | `/api/workspaces/{id}/config/{configId}/discard` | Editor; history-preserving, idempotent draft discard |
| POST | `/api/workspaces/{id}/config/rollback/{version}` | Owner; published/archived integer version restored as a draft |

**Publication is intentionally unavailable until Prompt 11 supplies trusted persisted, draft-bound evidence.** `NoConfigTestRunProvider` remains registered; the partially implemented `/tests/run` harness is not connected to trusted configuration publication, and client-written `TestCase.LastRunJson` is not trusted. Drafts display `no test run` and do not serve live analyses. A future provider must share publication's serializable transaction or an equivalent run-creation lock and must not return cached evidence. Publication never rewrites the tested artifact.

See [Workflow lifecycle validation](docs/workflow-lifecycle-validation.md) for guarantees, regression results, browser limitations, the changed-file inventory and the runner integration contract.

## SQL Server migrations

Migrations live in `backend/Data/Migrations/`. `Initial` includes the analysis audit fields; `ChunkMetadata` widens section hierarchy, adds document/chunk metadata and adds retrieval indexes. `20261115104518_BinaryChunkEmbeddings` archives the old JSON column as `LegacyEmbeddingJson`, adds labelled binary vectors/workspace binding and durable embedding progress, and resets old indexing claims. **Existing guideline chunks must be re-embedded; archived JSON is never searched.** Downgrade refuses to discard newly generated binary vectors. No new analysis-audit migration is needed.

Workflow's `Status`/`ConfigJson` concurrency tokens are EF metadata changes, not new columns, so they require no new DDL migration. The `TestRunHarness` migration adds harness run storage, but this does not provide trusted publication evidence while `NoConfigTestRunProvider` remains registered.

`20261115155603_WorkspaceAdministration` adds workspace deletion fields/shared membership revision, pending invitations, durable file-cleanup records and the latest-guidance index. Apply the reviewed `db/schema.sql` before starting this API version and drain old instances: old code does not enforce deleted-workspace denial. Management downgrade refuses with SQL error **51002** while any tombstone, invitation or cleanup record exists; never remove tombstones simply to bypass it. See [management migration and rollback](docs/operations.md#management-migration-and-rollback).

`20261117102730_ReparseMetadata` adds `Documents.FootnoteMarkers` and `Chunks.ReReviewRequired` for non-destructive re-parsing. It does not rewrite existing text. The parser, chunker and verifier share `evidence_text.py` normalization; re-parsing compares pages and only marks chunks overlapping changed pages for re-review, clearing their stale vectors. See [parser artifact normalization and re-parse](docs/operations.md#parser-artifact-normalization-and-re-parse).

`Program.cs` runs migrations only when `Database:AutoMigrate` is enabled (default true in Development). Production should set it to false and apply a reviewed idempotent script:

```powershell
dotnet ef migrations script --project backend/ClinicalRag.Api.csproj --startup-project backend/ClinicalRag.Api.csproj --idempotent -o db/schema.sql
```

Apply the generated script to SQL Server after review/backup and drain old API instances before the binary upgrade. Optional `db/enable-chunk-fulltext.sql` runs separately, outside a transaction, on a Full-Text-enabled SQL Server; LocalDB uses BM25. The script adds missing `Text`/`Section` fields without replacing an existing DBA-managed catalog/key. See the [upgrade runbook](docs/retrieval-deployment.md#upgrade-an-existing-deployment) for population readiness and cache refresh requirements. Migration tests use `MSSQLLocalDB`; most API tests use isolated in-memory databases.

## Checks

From the repository root:

```powershell
dotnet test backend.Tests/ClinicalRag.Tests.csproj
dotnet build backend/ClinicalRag.Api.csproj
python -m pytest tests -q
```

From `frontend/` (the utility tests use Node's TypeScript stripping support; UI regressions use Vitest/RTL/jsdom):

```powershell
npm test
npx tsc --noEmit --incremental false
npm run lint
npm run build
```

`npm test` runs both `test:unit` (Node utilities) and `test:ui` (Vitest); either script can also run separately. The original retrieval implementation passed **572 xUnit, 347 Python, 452 Vitest and 14 Node utility tests**, TypeScript, backend Debug/Release builds and the frontend production build. The **latest retrieval audit** has **652 backend passed / 2 existing harness failures / 654 total**, **358 Python passed**, **104 focused retrieval tests passed** and **8 SQL retrieval regressions passed** separately with live Full-Text required. Debug/Release builds pass; no frontend code/checks changed in this follow-up. Full frontend lint still reports **3 existing errors and 7 warnings** outside this work; known dependency/runner/native SWC fallback warnings remain. Commands, failures and measurement limitations are recorded in the [PR-ready validation report](docs/hybrid-retrieval-pr.md#validation).

The subsequent management audit passed **501 Vitest, 15 Node and 347 Python tests**, TypeScript, changed-file ESLint and the frontend production/backend Release builds. Full backend validation has **650 passed / 2 failed / 652 total**: the unchanged failures are `TestHarnessTests.Comparer_detects_missing_and_unexpected_problems` and `TestHarnessTests.Failing_expectation_makes_run_fail_with_diff`. Full lint retains the same 3 errors/7 warnings. Fresh/idempotent SQL migration and 51002 downgrade-guard tests passed on isolated databases; **no deployment database was migrated**. See [management validation and live checklist](docs/operations.md#management-validation--15-november-2026).

The latest **operations follow-up** passed **669 backend tests / the same 2 harness failures / 671 total**, **372 Python**, **543 Vitest + 15 Node**, TypeScript, changed-file ESLint and backend Release/frontend production builds. Full lint retains the same 3 errors/7 warnings. See [operations validation and rollout checklist](docs/operations.md#operations-validation--15-november-2026); live notebook/GPU/Firebase acceptance remains unverified.

The latest **ingestion artifact/re-parse change** passed **914 backend**, **613 Python**, **582 Vitest + 19 Node**, `npx tsc --noEmit`, full ESLint, the frontend production build and backend Debug/Release builds. Synthetic fixtures cover double-escaped entities, superscript footnote markers, wrapped dashes, table cells, formatting-only quote matching, table row/cell evidence, negation/number/comparison-symbol rejection, question/case classification, mapped question parts, reranker-plus-overlap relevance, coverage status, citation-name deduplication, readable section hierarchy and text-derived grade handling. A local real-PDF spot check of p.11/p.27/p.40/p.41 layouts produced no stored entities, no fused `Screeningc` marker and no `antigen– positive` wrap; the p.40 narrative returns no borrowed strength, and the p.41 unavailable-screening statement verified against the real parsed page text through the canonical verifier. Hosted CI was not rerun for this uncommitted worktree; these local results are not clinical validation.

The 17 November **CrAg evaluation/export follow-up** passed **915 backend**, **615 Python**, **584 Vitest + 19 Node**, `npx tsc --noEmit`, full ESLint and the frontend production build. It adds a pending real-guideline CrAg case (`expectedMode: question`, p.41/p.11) without inventing the clinician's screening-unavailable expectation, mode/pending-label metrics, question-mode topic labels in “Not covered” exports, and a deterministic before/after verifier replay. Verified-rate stayed **1/1**, the rejection count stayed `negation_conflict=1`, and the verified item no longer requires fuzzy quote rewriting. hit@k and mode accuracy remain unmeasured because no retrieval/model run was available; the screening-unavailable label remains pending clinician extraction. The rebuilt source archive `clinical-rag-colab-source-20261117-python-backend-v16.zip` (SHA-256 `6f63a091d208d9f9cede62eab262685f8d12e71a27e3c427908c1f0799ef9ab5`) includes the relocated `python-backend/`, clinical eval and loadtest assets; its extracted offline suite passed **615 Python tests** on local Python 3.13.6, not Colab Python 3.12. See [CrAg screening evaluation](docs/crag-screening-eval-2026-11-17.md).

The 17 November **semantic ingestion/retrieval** work passed **929 backend**, **626 Python**, **584 Vitest + 19 Node**, `npx tsc --noEmit`, full ESLint and the frontend production build. It adds atomic recommendation metadata, graded table-row splitting, deterministic synonym expansion, definition/summary boosting, cross-encoder intent alignment, optional HyDE and hyphenation-tolerant quote matching. The rebuilt source archive `clinical-rag-colab-source-20261117-semantic-v18.zip` (SHA-256 `b84f60cbf0aa1bbdc7b17c8a6e16b789b0ec03a2a7a5d5f46f0005067da40b55`) passed **626 Python tests** when extracted locally. The synthetic retrieval gate passed with `mrrAt8` **0.7917 → 0.8333** and `ndcgAt8` **0.8462 → 0.8770**; the InMemory p95 was **137.436 ms**, so this is a quality improvement, not a latency claim. See [semantic ingestion and retrieval](docs/semantic-ingestion-retrieval.md).

Live deployment validation additionally requires a running Colab GPU model server, matching service secrets, Firebase authentication and loaded guideline data; mocked/unit tests do not prove model startup or live clinical output quality.

See [Workflow lifecycle validation](docs/workflow-lifecycle-validation.md) for actual-component UI checks, file inventory and the runner integration contract. [Verified pipeline validation](docs/verified-pipeline-validation.md) records the earlier routing/privacy validation; [hybrid retrieval validation](docs/hybrid-retrieval-pr.md) records the latest retrieval results.
