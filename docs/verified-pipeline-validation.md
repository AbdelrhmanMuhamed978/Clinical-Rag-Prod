# Verified pipeline validation — 14 November 2026

The web analysis path now delegates planning, retrieval orchestration, drafting and verification to the separate Python service. Workspace configuration/rules are request-scoped; persistence is privacy-projected; conversation memory contains verified summaries only; saved analyses are scoped to the signed-in user and workspace.

## Command results

Commands below ran against the final implementation. Backend/Python commands ran from the repository root; frontend commands ran from `frontend/`.

| Command | Result |
| --- | --- |
| `dotnet test backend.Tests/ClinicalRag.Tests.csproj --no-restore` | **40 passed, 0 failed, 0 skipped** |
| `dotnet build backend/ClinicalRag.Api.csproj --no-restore` | **Passed**, 0 errors; 2 existing package-advisory warnings |
| `python -m pytest tests -q` | **34 passed** |
| `node --experimental-strip-types --test tests/analysis.test.mjs tests/sse.test.mjs tests/request-scope.test.mjs tests/saved-analyses.test.mjs` | **14 passed** |
| `npm run lint` | **Failed: 16 errors, 33 warnings** in existing layout, Chunk Review, workflow, auth and shared-type code |
| Focused ESLint on Playground, analysis-result, my-analyses, analysis, request-scope, sse and api files | **Passed**, no diagnostics |
| `npm run build` | **Passed**, including TypeScript checking and route generation |
| `node node_modules/typescript/bin/tsc --noEmit` | **Passed**; the later production build also passed TypeScript checking |

The contradictory-status and patient-warning regressions failed before the shared-policy fix. The full passing xUnit suite includes `Case_storage_rebuilds_draft_and_uncovered_warnings_without_patient_text`, including its storage-idempotence assertion.

## Actual-component browser checks

An isolated local fixture imported the actual Playground, result and saved-analysis components plus their API, parsing, memory, request-scope and SSE utilities. Only authentication/navigation context and API responses were synthetic. Its CSS reused the successful app build. No project dependency, real Firebase account, real analysis row or patient data was used.

Verified in Chromium:

- Saved verified content opens separately, suppresses patient checks and adds no conversation turn. Audit-only runs open with the no-stored-result explanation.
- Excluded recommendation/quote text and legacy free-text answers never appear as answers. Live patient checks remain separate cautions and do not enter verified summaries.
- The native deletion dialog is modal, starts focus on **Cancel** and restores focus after dismissal. Cancel and Escape send no DELETE; explicit confirmation sends exactly one DELETE for the selected synthetic row and refreshes the list.
- Reference follow-ups send the same conversation ID and only question/verified-summary history. Summaries omit planner labels, patient checks, source excerpts and excluded drafts.
- Invalid and uncovered outputs add no verified memory turn. The “not covered” safety wording remains visible. History retains only the last six verified turns.
- Enter sends; Shift+Enter inserts a line without sending. IME-safe Enter behavior is additionally covered by the utility tests.
- Cancelling a pending request and releasing its late response adds no answer. A cancelled response released while a replacement request is pending does not clear that replacement's loading state.
- Clearing history during a request aborts it, clears messages/input/memory and prevents its late response from appending. The next request has a new conversation ID and empty history.
- Workspace/user changes clear input, messages, opened saved results and memory, abort pending analysis work and suppress late responses. Saved-list requests use the new workspace/user scope rather than another identity's cached list.
- Collapsing My analyses during a saved-open request, or changing identity during it, cancels ownership and suppresses the late saved response.
- No browser console errors were recorded. Screenshots of the result view and native confirmation dialog were inspected.

Fixture files and build outputs are outside the project at `C:\Users\abdom\AppData\Local\Temp\opencode\clinical-rag-verified-ui-20261114\`. The QA server was stopped after validation; it is not a production service.

## Routing implementation file inventory

This workspace has no Git metadata. The list records the added/updated routing implementation files, not a generated Git diff or changes from unrelated earlier work.

### Python

- `config.py`
- `facts.py`
- `pipeline.py`
- `pipeline_service/__init__.py`
- `pipeline_service/app.py`
- `pipeline_service/middleware.py`
- `tests/test_v32.py`
- `tests/test_pipeline_service.py`
- `tests/test_pipeline_safety.py`

The full Python suite also runs the existing secured Colab API tests in `tests/test_colab_server.py`; it does not start real GPU models.

### ASP.NET

- `backend/Program.cs`
- `backend/Controllers/AnalysisController.cs`
- `backend/Controllers/AnalysesController.cs`
- `backend/Controllers/RulesController.cs`
- `backend/Services/ConfigService.cs`
- `backend/Services/PipelineClient.cs`
- `backend/Services/PipelineResultPolicy.cs`
- `backend.Tests/AnalysisControllerTests.cs`
- `backend.Tests/VerifiedPipelineControllerTests.cs`
- `backend.Tests/AnalysesControllerTests.cs`
- `backend.Tests/RuleApprovalTests.cs`
- `backend.Tests/PipelineClientTests.cs`

### Frontend

- `frontend/src/app/(app)/w/[workspaceId]/playground/page.tsx`
- `frontend/src/app/(app)/w/[workspaceId]/documents/[docId]/chunks/page.tsx` — explicit API response types after helpers began returning `unknown` by default
- `frontend/src/components/analysis-result.tsx`
- `frontend/src/components/my-analyses.tsx`
- `frontend/src/components/my-analyses.module.css`
- `frontend/src/lib/analysis.ts`
- `frontend/src/lib/api.ts`
- `frontend/src/lib/request-scope.ts`
- `frontend/src/lib/sse.ts`
- `frontend/tests/analysis.test.mjs`
- `frontend/tests/saved-analyses.test.mjs`
- `frontend/tests/request-scope.test.mjs`
- `frontend/tests/sse.test.mjs`

### Documentation

- `README.md`
- `.env.example`
- `docs/architecture.md`
- `docs/verified-pipeline-validation.md`

## Remaining work and warnings

1. **Live integration remains untested:** start the actual Colab GPU model runtime, configure matching pipeline/Colab secrets and the allowed retrieval origin, authenticate with Firebase, and validate ASP.NET → Python → ASP.NET retrieval / Colab generation using loaded guideline data. Mocked tests and the isolated UI fixture do not establish live model quality, startup or deployment connectivity. No real accounts/uploads were created by `test_e2e.ps1`; that script was not run.
2. **Full lint remains failing** as listed above. The verified-analysis files pass focused lint. Existing layout/Chunk Review/workflow/auth/shared-type lint issues were not expanded into this routing task.
3. **Existing dependency advisories remain:** NuGet reports moderate/high advisories for `System.Security.Cryptography.Pkcs` 9.0.4 and a high advisory for test dependency `SQLitePCLRaw.lib.e_sqlite3` 2.1.11. Review and authorize dependency remediation before production; no versions were upgraded here. Existing xUnit runner resolution (2.9.2 → 3.0.0) and test nullability warnings also remain.
4. **Existing frontend environment warning:** native Windows SWC failed to load as a valid Win32 application; Next successfully used WASM and completed the production build. Node utility tests emit experimental type-stripping/module-format warnings.

See [README configuration and startup](../README.md#configuration-and-startup) for service settings and the privacy defaults. Production must configure Firebase authentication and disable sensitive request/prompt/credential logging in all services, proxies and tunnel inspection.
