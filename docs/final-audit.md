# Final audit — 15 November 2026

**Not a clean sign-off.** All requested build/test commands were rerun. CORS, backend harness failures, frontend lint errors, a frontend test timeout, genuine live acceptance and trusted draft publication remain unresolved. The subsequent user-authorized Colab setup corrections are recorded below; historical evaluation artifacts were not overwritten.

## Fresh checks

| Command | Actual result |
| --- | --- |
| `dotnet build backend/ClinicalRag.Api.csproj --nologo --no-restore` | Passed: 0 errors, 2 package-advisory warnings |
| `dotnet test backend.Tests --nologo --no-restore` | **669 passed / 2 failed / 671 total**, no skips |
| `python -m pytest tests -q` | Original final-audit rerun: **372 passed**; after Colab setup regressions: **383 passed in 4.70 s** |
| `npm.cmd run lint` in `frontend/` | Failed: **3 errors / 7 warnings** |
| `npm.cmd run build` in `frontend/` | Passed, including its TypeScript stage; native Windows SWC failed to load and WASM fallback was used |
| `npm.cmd test` in `frontend/` | **15 Node tests passed; 542 Vitest passed / 1 timed out / 543 total** |
| `npx.cmd tsc --noEmit --incremental false` in `frontend/` | Passed |

Backend failures:

- `TestHarnessTests.Comparer_detects_missing_and_unexpected_problems` (`backend.Tests/TestHarnessTests.cs:65`).
- `TestHarnessTests.Failing_expectation_makes_run_fail_with_diff` (`backend.Tests/TestHarnessTests.cs:115`).

Both fail because the expected `Missing expected problem` diff is absent. These are the same failures recorded before this final rerun.

Frontend lint errors:

- `frontend/src/app/(auth)/verify-email/page.tsx:24`: unescaped apostrophe.
- `frontend/src/components/AuthForm.tsx:47,70`: explicit `any`.

The failed Vitest test is `keeps enablement separate from approval and uses new revisions for retirement and reopening`, at `frontend/tests/rules-editor.test.tsx:223`. It exceeded the 5-second timeout during the full suite. An unchanged isolated rerun passed in **4.8 seconds**, with 24 other tests skipped. That is evidence of a timing-sensitive test, not a green full-suite result or proof of stability. The production build and tests ran concurrently during the initial rerun.

Existing advisories include `System.Security.Cryptography.Pkcs` and test `SQLitePCLRaw.lib.e_sqlite3`. The xUnit runner fallback, Node TypeScript/module warnings and React/jsdom `<search>` warning also remain.

## Original signatures

This directory has **no Git repository**. Searches used `git grep --no-index -n -I -E` with individually scoped production source paths. Generated `bin`/`obj`, dependencies, test fixtures, reports and measurements were excluded. A zero-match search exits 1; an actual search error would not count as zero hits.

| Original bug | Literal evidence and semantic conclusion |
| --- | --- |
| `seenKeys` shared across subqueries | **0 `seenKeys` / `seen_keys` hits.** `AnalysisController.cs:334–336` retrieves one focused query. `pipeline.py:407–417` collects each problem's retrieval results independently and merges them afterwards. The `seen` sets at lines 183 and 394 are invocation-local source/planning deduplication, not shared cross-query state. |
| Terminal `return default` in `config.get` | The literal still has **1 legitimate hit**, `config.py:135`, for a missing key. Successful traversal ends with `return copy.deepcopy(current)` at line 137. **0 occurrences of the original terminal-return bug.** |
| `os.getenv` without an import | **19 matching lines** across 7 production modules. An AST scan parsed all 18 production Python files, found **0 missing module-level `import os` bindings** and no parse errors. The notebook imports `os` before its environment accesses and contains no `os.getenv` calls. |
| Hardcoded `'FINAL'` source status | **2 quoted uppercase `FINAL` matches**, both conditional on source metadata: `colab/models.py:274` and the Documents badge. Backend retrieval derives status from `Document.GuidelineStatus` at `AnalysisController.cs:383–396`; Python preserves it at `pipeline.py:174–178`. Unknown/missing status becomes DRAFT. **No unconditional FINAL retrieval assignment found.** |
| ChunkPortions using min/max pages | **No page-selection min/max expansion.** `DocumentsController.cs:287–291,347–351,418` uses exact selected/batch/replacement membership. `ValidChunkRange`, lines 662–673, rejects a range spanning an unselected gap. Remaining `Math.Min` calls inside chunking cap section metadata, not pages. Automated regressions and genuine FINAL/DRAFT model runs preserve exact `[1,3,5]`, with no gap-page text. |
| `AllowAnyOrigin` | **1 unresolved hit**, `backend/Program.cs:83`. CORS is still unrestricted. A zero-hit claim would be false. |
| `EnsureCreated` | **0 production hits.** Startup uses `db.Database.Migrate()` subject to configuration and provider checks. |
| `mongodb_uri` | **0 case-sensitive lowercase hits.** This does **not** mean Mongo support was removed: `colab/models.py:218` still supports `COLAB_MONGODB_URI` / `MONGODB_URI` for the legacy retrieval route. The web pipeline uses ASP.NET retrieval instead. |
| Playground text `verified sources` | **0 hits** in the Playground source; a separate case-insensitive search of all `frontend/src` also returned none. |

## Genuine local smoke checks

Started a dedicated **SQL LocalDB** database, the real ASP.NET API, the actual Python service and the production Next.js server. Existing application database settings were not used for audit data. Firebase project settings remain enabled and match the frontend; no development authentication, verification bypass, mock model or invented test evidence was introduced. Result storage is disabled for this stack.

| Observed check | Result |
| --- | --- |
| Browser opens `http://localhost:3000/signup` | Passed: actual signup form rendered |
| API `GET /health` | 200, `alive: true` |
| Python `GET /health` | 200, `alive: true` |
| API `GET /ready` before Colab startup | **503** with `Retry-After: 30`; database ready, Colab and model-dependent pipeline readiness false |
| API `GET /ready` after genuine Colab registration | **200**, `ready: true`; database, Colab and pipeline ready. Reports `Qwen/Qwen3-4B-Instruct-2507`, `pritamdeka/S-PubMedBert-MS-MARCO`, 768 dimensions, CUDA and Tesla T4. |
| Unauthenticated `GET /api/workspaces` | 401 |
| Unauthenticated `GET /metrics` | 403 |

These smoke checks do not establish two-user isolation or clinical/model behavior.

## Requested manual acceptance

| Step | Current status |
| --- | --- |
| Sign up second user, verify email, deny another workspace's list/direct access | **Existing-account authentication/isolation passed; signup flow not exercised**: A and B signed in through private handoffs, matched their designated Firebase identities, and verified-claim-only `/api/operations/colab` returned 200 for each. Before switching, A remained the sole owner with no invitations. Uninvited B received an empty workspace list (200) and 404 on all 12 scoped reads; the direct documents UI showed an access-failure alert without fixture data. No new signup or verification-email flow is claimed. |
| Upload PDF | **Passed for synthetic fixtures**: FINAL `992c9139-2b2e-45a4-a98a-00ee28f721be` and DRAFT `0249f9d1-a7ea-4d2a-a268-18256557f12c`, through the real upload dialog with explicit synthetic organization/year/status. Not clinical provenance. |
| Parse PDF | **Passed** automatic parsing: both show five pages and review links. FINAL has 3,198 and DRAFT 3,222 parsed characters, matching local parser output; all five page markers and the known quote survive. |
| Chunk non-contiguous pages | **Passed on both** through genuine model/UI: `[1,3,5]` saved as the exact processed-page list; three single-page verbatim chunks per document, no gap-page markers, no source-text mismatch. Both correctly remain partial/60%. |
| Review chunks | **Passed on both**: all six expanded passages compared against source, accepted individually, and server review attribution confirmed. No confidence-based auto-accept. |
| Embed | **Passed on both** through real Embed: three embedded chunks each, zero pending/accepted remainder, documents indexed; processed-page coverage remains partial/60%. The review DTO does not expose vector metadata, so no per-vector dimension assertion is claimed from it. |
| Ask question and full case | **Mixed / failing**: FINAL page-3 marker answer failed despite retrieval containing `GREEN`; FINAL known-quote question produced one UI-verified item with an exact page-1 quote. DRAFT question returned four UI-verified, source-matching items. Full fictional case returned three planned problems, all Not covered, zero verified items: nonempty-answer/coverage expectation failed. |
| Only verified answers, truthful DRAFT labels, no truncated output | **Partial integration evidence**: DRAFT question has two DRAFT citations with badges; all three retrieved DRAFT sources have badges. Both FINAL and DRAFT sources were used despite the question mentioning DRAFT, with additional medication statements and one corrected citation. Full case has nine DRAFT source occurrences with consistent badges. No rejected/truncated diagnostic was reported in these runs; this does not exercise truncation rejection or establish semantic safety. |
| Edit prompt, save and confirm after reload | **Passed storage check**: harmless answer-prompt persistence marker saved as draft v2 `55220319-67a0-49a5-98a9-8b418c84aa86`; selected/loaded after browser reload with exact text, all other configuration unchanged, and published v1 unchanged. No attempt to force answer success by publishing prompt changes. |
| Create, approve, enable and observe rule firing | **Partial / approval blocked**: test-only rule `4c924e6b-4e96-4854-bed4-7022db9fc9a8` created disabled, submitted (revision 2), enabled while still unapproved (revision 3), then disabled (revision 4). Canonical Python preview reports would-fire simulation, matches the manifest fact and regex. The approval dialog explicitly requires clinician review; its confirmation was cancelled rather than fabricating review. Rule remains unapproved/disabled; live approved-rule firing is not established. |
| Run in-app tests | **Run live / both failed before results**: two synthetic cases created with exact persisted expectations and run through the real Tests UI. Both completed with execution-failure diffs and no pipeline output, using published v1 rather than draft v2. Neither answer expectations nor the deliberate missing-problem comparison were exercised. Failed histories are preserved; no trusted draft run was created. |
| Publish a draft | **Protective denial passed; successful publication blocked**: authenticated POST for the exact untested draft v2 returned **409**. Published v1 and its JSON remain unchanged, v2 remains draft without a trusted run, and its publish control remains disabled. `NoConfigTestRunProvider` remains registered at `backend/Program.cs:72`; the enabled draft-test button created no run, and Workflow's `onRunTests` is a no-op at `frontend/src/app/(app)/w/[workspaceId]/workflow/page.tsx:222`. No publication bypass was attempted. |

The user elected to prepare dedicated accounts and a real Colab runtime rather than end live acceptance at these blockers. Setup requirements are in [live-test-setup.md](live-test-setup.md). After a stopped-stack/API-tunnel-502 observation, the user restarted the updated launcher: API/Python/frontend and public API-tunnel health returned HTTP 200. Genuine registration subsequently completed, and independent local `/ready` returned 200 with all dependency checks ready. Account A used an existing Firebase account; no account or patient data was created by the assistant. The real create-workspace UI created `3ad97b32-3ee1-4e0e-aab5-fb783de7ac6b` and redirected to documents. An automation timeout waiting for a now-absent list link was a locator error, not a failed creation; the existing workspace was inspected and reused without duplication.

## Additional genuine-startup blocker discovered

The old notebook installed `requirements.txt` and `requirements-colab.txt` together. Its original pins conflicted:

- `requirements-colab.txt` pins `vllm==0.8.3` and `torch==2.5.1`.
- vLLM 0.8.3 declares **`torch==2.6.0`** and **`transformers>=4.51.0`**, while `requirements.txt` pins Transformers 4.46.3.
- The selected `Qwen/Qwen3-4B-Instruct-2507` model card requires **vLLM >=0.8.5** and Transformers >=4.51.0.

Evidence: [vLLM 0.8.3 package metadata](https://pypi.org/pypi/vllm/0.8.3/json), [Qwen model card](https://huggingface.co/Qwen/Qwen3-4B-Instruct-2507), [vLLM 0.8.5 metadata](https://pypi.org/pypi/vllm/0.8.5/json). With user authorization, requirements were corrected to **vLLM 0.8.5 / Torch 2.6.0 / Transformers 4.51.3**. The observed Colab host uses Python 3.13; vLLM 0.8.5 requires Python <3.13, so the replacement six-cell notebook installs binary packages into isolated Python 3.12 using uv 0.9.12. Genuine installation and GPU/model readiness now passed as recorded below; clinical behavior and load headroom are not established.

Additional corrections: sentence-transformers 2.7.0 uses `automodel_args` for the reranker, and vLLM request logging is disabled. A manifest/hash-validated, source-only ZIP was generated and inspected: **45 source files, 111,723 bytes**, SHA-256 `434d10726c80e0cf5b16327f9e442944c635610205f1eb568dc27e79033b6a09`. It excludes secrets, databases, uploads and unestablished evaluation question inputs. Local Python tests passed **383/383**; all six notebook cells parse as Python. These checks are offline evidence only. The updated local launcher starts all three services successfully and created its DPAPI ciphertext entry; ciphertext/value were not read. Encrypted-entry reuse and the later optional-admin-field cleanup still need runtime validation.

The missing extension driver was restored as the same browse 0.0.16 using the official checksum-validating installer, without replacing browser pairing/settings. The user's **existing signed-in Chrome tab**, not the separate in-app browser, is controlled through that extension. The replacement notebook was imported as a new Drive copy, preserving the old notebook. A genuine T4 runtime is connected. Tunnel-binary setup and the source ZIP upload completed with their printed markers. The isolated Python 3.12/GPU installation cell finished with **`INSTALL COMPLETE — run the offline-test cell next.`**, which executes only after the checked installer subprocess (dependency/GPU validation included) succeeds. The subprocess's detailed package/version output was not displayed, so no captured package-log evidence is claimed. Colab's offline-test cell subsequently printed **`OFFLINE TESTS PASSED`** after pytest exited successfully; its exact test count was not captured. The last cell then printed **`COLAB REGISTERED`** and **`STARTUP COMPLETE`**. Independent API readiness returned 200 with real model/embedding/GPU metadata. A prior status check interrupted by Chrome-debugger detachment was discarded as model evidence; the user re-shared the same tab. This establishes genuine startup/registration/readiness, not clinical quality or a completed authenticated lifecycle.

With user approval, `scripts/prepare_acceptance_fixtures.py` generated two visibly synthetic five-page PDFs and a manifest in the isolated temp fixture directory. FINAL/DRAFT means finalized/unfinished **synthetic test artifacts**, not genuine clinical publication. Checks covered five pages per PDF, hashes, exact quotations, unique selected/excluded-page markers and no-overwrite behavior. Representative rendered pages were visually inspected; native PDF input is unsupported. Both were uploaded and parsed through the real stack. Both documents' pages 1/3/5 were chunked with explicit synthetic-text guidance, inspected, accepted individually and embedded; exact source-text matches and excluded-page absence were checked independently through authenticated reads. These are integration passes, not clinical or publication evidence. Original evaluation artifacts and the reviewed source ZIP remain untouched.

The first genuine FINAL marker question retrieved pages 3/1/5, including the expected `GREEN` text on page 3, but returned **Not covered / zero verified items**. This answer expectation failed; an empty-array quote check is vacuous and is **not** a verification pass. A separate known-quotation question returned one UI-verified recommendation, an exact page-1 source quotation and a FINAL citation without DRAFT badges or excluded-page markers. Recommendation wording changed “does not establish” to “does not indicate”; the citation displayed `weak`, although the synthetic fixture supplies no clinical grade. This establishes a nonempty source-quote/citation integration result, not semantic or clinical-metadata adequacy.

The genuine DRAFT creatinine question returned four UI-verified quotations, matching indexed source text: two kidney cautions and two medication-exposure cautions, split across FINAL/DRAFT citations. Both DRAFT citations and all three retrieved DRAFT sources retain DRAFT badges; source list pages are 3/3/1/5/1/5 with no excluded markers. One medication citation was corrected. The full fictional case subsequently planned three problems and retrieved 18 source occurrences, all from pages 1/3/5 (nine DRAFT, each badged), but returned **zero verified items, all three Not covered**, with no request alert or incomplete-run notice. This is a failed useful-answer/coverage check, not a clinical success. No automatic-stop recommendation appeared because there were no recommendations; that absence is vacuous safety evidence. Inputs are intentionally omitted from this report.

Rule-control acceptance uses only an explicitly synthetic local source and the manifest's arbitrary test threshold, with caveats denying clinical evidence/public-guideline provenance. No public source or clinician assessment is claimed. The genuine deterministic preview matched the synthetic scenario and reported **Would fire (simulation only)**, not an approved analysis result. Enablement did not change `in_review` or create reviewer attribution. Four append-only audit actions (`created`, `submit`, `enabled`, `disabled`) were independently read; no approval action exists. A script timeout expecting the wrong post-submit status label was a locator error: authenticated reads confirmed the existing submitted rule, and continuation reused it without duplicate creation. A separate incorrect helper URL returned 404; the app's actual `/drugs` URL returned 200 after correcting the helper. Neither tooling mistake is treated as an application failure or a passing clinical check.

The two genuine Tests-UI runs are execution failures, not expectation failures:

| Synthetic test case | Preserved case ID | Result | Duration | Pipeline output |
| --- | --- | --- | --- | --- |
| Known-quote integration | `9d68c4da-fabd-472b-8221-0b87a8449d56` | Failed, execution-failure diff | 6.0829 ms | Absent |
| Deliberate unmet expectation | `bcad7ee9-ef15-4719-ba95-003dfb404c3e` | Failed, execution-failure diff | 10.2136 ms | Absent |

Both persisted runs completed using published configuration v1. The negative sentinel's expected missing-problem diff was absent because no result reached comparison; this does **not** establish a live comparer defect or a successful negative test. Draft v2 still has no trusted test run. Case inputs, exception details and upstream response bodies are withheld. Static investigation finds a concrete authentication mismatch: `TestHarnessService.cs:241` passes null authorization, `PipelineClient.cs:98–102` forwards bearer authorization only when provided, and `pipeline_service/app.py:135–141,211–212` requires it before analysis. This is an established source-level blocker, not yet a correlated live-failure diagnosis; no security requirement was weakened to test around it.

The subsequent authenticated publication request targeted only the exact untested audit draft and returned **409**. Independent reads confirmed published v1 `8321324c-78e5-487b-a432-08be38d272c4` and its configuration unchanged, draft v2 still `draft`, and no trusted run. The UI publish control stayed disabled. This proves rejection of this untested artifact, not successful publication or certification of the unfinished runner.

Account B completed private sign-in to the existing designated Firebase account and reached `/workspaces`. Browser identity and client verification matched; `/api/operations/colab` returned **200**, independently accepting the verified bearer claim before the denials were interpreted. Owner-side checks immediately before sign-out confirmed one designated owner and no invitations. B's workspace list returned **200 with zero workspaces**, with no A workspace shown in the UI. All **12** authenticated scoped reads returned **404**: workspace, members, document list, each FINAL/DRAFT document, each chunking-progress endpoint, each document's chunks, published configuration, rules and tests. Direct navigation to A's Documents page rendered “Could not refresh documents.” without fixture filenames, selected-page source markers or review links. This is a genuine uninvited-account isolation pass for the tested reads, not certification of every route/write or a new-account/email-verification flow. The browser returned to B's `/workspaces`; no invitation or new workspace was created.

After isolation, a final independent `/ready` recheck still returned **200**, with database/Colab/pipeline ready and the same genuine Qwen/PubMedBERT/768-dimension/Tesla-T4 metadata. SHA-256 rechecks confirmed the source ZIP and both synthetic PDFs unchanged. Services, tunnels and Colab were left running; failed histories and original evaluation artifacts were not replaced. This acceptance attempt remains **not a clean sign-off**.

## Remaining gaps

- Unrestricted CORS, two backend harness failures, three lint errors, seven lint warnings and the frontend timing-sensitive timeout.
- Corrected Colab installation, offline tests, genuine GPU/model readiness and registration passed; clinical behavior and load headroom remain unverified.
- Verified owner A and uninvited B's tested read isolation, both synthetic PDF lifecycles, prompt-draft persistence, rule create/submit/enablement separation, canonical simulation and untested-draft publication denial established. New-account/verification-email flows were not exercised. Both in-app tests failed before pipeline results; clinician approval and live approved-rule firing remain unestablished. FINAL marker and full-case useful-answer expectations failed; DRAFT/FINAL quote and badge results do not establish clinical correctness.
- Trusted draft-bound testing/publication is unfinished and must not be bypassed.
- Live notebook rotation/heartbeat, deployment migration, real provider outages and deployment-scale behavior are not certified by mocks.
- Clinical-quality evaluation and question provenance remain unestablished. Original evaluation artifacts are preserved; synthetic fixed-vector measurements are not publication or clinical evidence.
- The original EF InMemory warm retrieval p95 still exceeds 100 ms; separate SQL/stub-model measurements do not prove deployed GPU/network/concurrent latency or a follow-up speedup.
- Known dependency advisories and native SWC fallback remain.
- No Git metadata, commit, push or PR exists here.
