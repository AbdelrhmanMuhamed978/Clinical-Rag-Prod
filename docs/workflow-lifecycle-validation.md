# Workflow configuration lifecycle validation — 15 November 2026

The Workflow editor now preserves complete configuration snapshots and unsaved typing, validates drafts on the server, exposes version history and settings, and gates publication and step completion on trusted, version-bound test evidence. The verified analysis path, privacy projections and mandatory verifier remain intact.

**Production publication intentionally remains blocked:** Prompt 11's real persisted, draft-bound test runner is not implemented. `NoConfigTestRunProvider` is still registered, and `/api/workspaces/{id}/tests/run` still returns `not_run`. Neither that stub nor client-written test results count as evidence.

## Implemented guarantees

### Editor state and ownership

- Published JSON parsing is memoized. The editor initializes once per published configuration ID; same-ID refetches do not replace input or reset typing.
- The editor retains its complete source snapshot, including untouched and unknown keys. Defaults come from `config/config.schema.json` and the existing `config/default_prompts.json`; edited fields are deep-merged rather than rebuilding only prompts/retrieval.
- Differences distinguish nested paths such as `future.a.b` from literal keys such as `future["a.b"]`. Empty keys, quotes and brackets are escaped unambiguously, preventing duplicate React keys. These labels are display-only, never evaluated.
- Dirty state compares the current fields with the saved snapshot. Configuration and rule-link editors register unsaved-change warnings; hidden Reconcile edits remain mounted and protected when switching settings.
- A clean editor initializes a newly published ID. A dirty editor keeps its original complete snapshot, blocks stale saves and requires confirmation before reloading. The frontend submits `publishedConfigId`; the server rejects a changed publication with HTTP 409 before allocating a draft version.
- Draft submission captures its payload and source fields. Typing remains possible during a save; a late success preserves newer typing as dirty instead of silently marking it saved. Version-changing controls stay disabled during mutations.
- Workspace/user changes replace the editor. Query keys include workspace and user ownership, and unmounted mutation callbacks cannot write a replacement editor's state or cache.

All six panels are available:

| Step | Settings |
| --- | --- |
| Read case | Read-only shared canonical parser units, explicit-unit handling and unitless count assumptions |
| Plan | Plan prompt |
| Retrieve | Five retrieval settings and rewrite prompt |
| Draft | Answer prompt |
| Verify | Read-only quote/citation, number, strength, scope, condition, negation, topic and truncation checks; no unlock control |
| Reconcile | Existing revision-bound rule link-term update and clinician-review APIs; no invented configuration keys |

Read case displays the 19 parser input fields from `clinical-vocabulary.json`. Unitless WBC/platelet counts ≥1000 are interpreted as /µL; displayed ×10⁹/L is equivalent to ×10³/µL. Supported explicit units are converted, while unsupported units are excluded rather than guessed.

Rule definition changes preserve omitted fields and clear prior approval; exact no-ops preserve review. The current rule lifecycle allows owners or reviewers to approve a saved revision and binds review to the recognition catalog where applicable. Editors cannot self-approve, and approval does not automatically enable a rule. This existing rule-review capability is separate from **owner-only configuration publication** and workflow-step completion.

### Shared validation and legacy retrieval

ASP.NET validates the schema keywords used by the shared contract without adding a runtime schema-library dependency. Python and the frontend use the same ranges/defaults. Known fields retain strict types; object defaults and published settings are merged before edits, and unknown object/array keys are retained unless explicitly replaced.

| Retrieval field | Inclusive range | Default |
| --- | --- | --- |
| `minRerank` | Number, 0–1 | 0.30 |
| `topK` | Integer, 1–20 | 8 |
| `candidates` | Integer, 5–100 | 30 |
| `queriesPerProblem` | Integer, 1–5 | 2 |
| `maxChunks` | Integer, 1–20 | 8 |

- Prompt 5's required variables remain literal tokens: Plan requires `{case}` and `{n}`; Answer requires `{problem}`, `{patient}`, `{k}` and `{context}`; Rewrite requires `{q}`. Other tokens and JSON braces are allowed, not interpreted as format-string expressions. Prompt length is 1–20,000 Unicode characters.
- `safety.verifierLocked` is forced to `true`. `safety.minQuoteMatch` cannot fall below 0.88. Other safety/chunking and unknown settings survive the merge.
- Draft JSON must be an object, have no duplicate keys at any depth and fit the 262,144-character limit. The controller also limits requests to 512 KiB. Invalid drafts create no row, including no accidental bootstrap row.
- Validation responses name known fields/constraints without echoing submitted prompts, parser exceptions or arbitrary upstream response bodies.
- `AnalysisController.DoRetrieve` safely falls back to shared defaults for malformed legacy JSON or invalid retrieval types/ranges. It continues to embed only the supplied focused query, filter workspace chunks and rerank them; it does not reintroduce backend LLM planning or legacy Mongo retrieval.
- Schema integers include whole-number decimal/exponent JSON forms: `10.0` and `1e1` both mean 10. Retrieval honors these forms and retains safe defaults for fractional, string, out-of-range and nonfinite values.

### Version lifecycle and authorization

All routes require Firebase bearer authentication and membership in the route's workspace. IDs cannot cross workspace boundaries.

| Method | Route | Minimum access / behavior |
| --- | --- | --- |
| GET | `/api/workspaces/{id}/config/published` | Viewer; existing default bootstrap for a new workspace |
| GET | `/api/workspaces/{id}/config/versions` | Viewer; `{ role, versions }`, latest bound run and publication reason per version |
| POST | `/api/workspaces/{id}/config/draft` | Editor; `{ configJson, publishedConfigId? }`; new normalized draft |
| POST | `/api/workspaces/{id}/config/{configId}/publish` | Owner; latest passing bound evidence required |
| POST | `/api/workspaces/{id}/config/{configId}/discard` | Editor; history-preserving, idempotent draft discard |
| POST | `/api/workspaces/{id}/config/rollback/{version}` | Owner; published/archived snapshot becomes a new draft |

Saved configuration artifacts are immutable: loading and editing a saved draft creates another version. Publication changes status/time and archives prior publications, but never normalizes or rewrites the tested `ConfigJson`. A noncanonical legacy draft must be saved as a normalized new draft and tested again.

Rollback restores the historical snapshot with schema defaults/validation, not a merge with the current publication. It leaves the publication and history intact and requires fresh testing. Published, archived and discarded versions cannot bypass draft publication restrictions.

Relational lifecycle operations use serializable transactions for bootstrap, version allocation, stale-save preconditions and publication. `Status` and `ConfigJson` are EF concurrency tokens; a final ordinal comparison checks the persisted artifact even on case-insensitive database collations. Publication rereads the latest bound run immediately before updating versions. Discard cannot overwrite a concurrently published draft. These EF metadata changes require no new database column or DDL migration; the model snapshot was updated.

### Evidence and completion

`IConfigTestRunProvider` is the trusted Prompt 11 seam. `ConfigTestRun` binds a nonempty run ID, workspace ID, configuration ID/version, SHA-256 of the exact persisted UTF-8 `ConfigJson`, status, start/completion times and genuinely completed step IDs 1–6. Invalid binding, a run predating the draft, duplicate/out-of-range steps or a mismatched hash is treated as no evidence.

The provider must return the **latest started** run, including failures and in-flight runs, not the last passing run. It must never derive evidence from client-written `TestCase.LastRunJson`, a request body or cached results.

Server publication reasons include `only drafts can be published`, `no test run`, `latest test run is still running`, `latest test run did not pass` and `latest test run is not complete`. The UI additionally explains owner permissions, unsaved changes and refreshing/unavailable test status, and fails closed while permissions/evidence load.

Step markers belong to the **loaded** saved version, not the version merely selected for inspection. Dirty configuration edits suppress evidence. Opening settings, saving, approving a rule or inspecting a passing draft completes no step. A latest failed run can show only its genuinely completed steps; it revokes publication eligibility even after an earlier pass.

## Latest follow-up validation — 15 November 2026

The substantial existing implementation was retained, including memoized initialization, typing/refetch/save persistence, complete merges/defaults, lifecycle authorization/concurrency and trusted-evidence completion. The follow-up changed only three production files:

- `frontend/src/lib/workflow-config.ts`: unambiguous difference paths for unknown literal keys.
- `frontend/src/components/workflow/step-settings.tsx`: shared canonical units/count assumptions and complete read-only verifier explanations.
- `backend/Controllers/AnalysisController.cs`: safe retrieval of whole-number decimal/exponent JSON integers.

Regressions were added **before** the corresponding fixes. Red runs reproduced **2 xUnit failures** for integer retrieval and **4 frontend failures** for ambiguous helper/rendered differences and incomplete unit/verifier explanations. Additional passing regressions cover integer normalization, unknown-key merges and duplicate-key rejection before a first draft/bootstrap can be persisted. The four test files changed were `backend.Tests/AnalysisControllerTests.cs`, `backend.Tests/ConfigServiceTests.cs`, `frontend/tests/workflow-config.test.ts` and `frontend/tests/workflow-lifecycle.test.tsx`.

### Current command results

Full suites ran after the production fixes. Fresh builds, TypeScript, focused/full lint and the refreshed browser fixture checked the same production source. Subsequent changes were isolated QA assets and documentation only; no dependencies were added in this follow-up. Backend/Python commands ran from the workspace root, and frontend commands from `frontend/`.

| Check | Current result |
| --- | --- |
| `dotnet test backend.Tests/ClinicalRag.Tests.csproj --no-restore --verbosity minimal` | **472 passed, 0 failed, 0 skipped** |
| `python -m pytest -q` | **312 passed** |
| `npm.cmd test -- --reporter=dot` | **14 Node utility tests + 452 Vitest tests passed** |
| Focused regression reruns | **47 xUnit + 58 Workflow/navigation Vitest tests passed** |
| `npx.cmd tsc --noEmit --incremental false` | **Passed**, no diagnostics |
| Focused ESLint command below | **Passed**, no diagnostics |
| `dotnet build backend/ClinicalRag.Api.csproj --no-restore` | **Passed**, 0 errors; 2 existing NuGet advisory warnings |
| `dotnet build backend/ClinicalRag.Api.csproj --no-restore --configuration Release` | **Passed**, 0 errors; the same 2 advisory warnings |
| `npm.cmd run build` | **Passed**, Next.js 16.4.6 production compilation, TypeScript and route generation; successful WASM fallback |
| `npm.cmd run lint` | **Failed: 3 existing errors, 7 warnings**, all outside the focused Workflow/navigation changes |

Current focused lint command:

```powershell
npx.cmd eslint "src/app/(app)/w/[workspaceId]/workflow/page.tsx" "src/app/(app)/layout.tsx" "src/components/navigation-guard.tsx" "src/components/workflow" "src/lib/workflow-config.ts" "tests/*.test.ts" "tests/*.test.tsx" "vitest.config.ts"
```

The fresh full lint diagnostics are:

| File (relative to `frontend/`) | Errors | Warnings |
| --- | --- | --- |
| `src/app/(auth)/verify-email/page.tsx` | 1 unescaped apostrophe | 1 missing hook dependency |
| `src/components/AuthForm.tsx` | 2 explicit `any` types | 2 unused bindings |
| `src/app/(app)/workspaces/page.tsx` | 0 | 3 unused bindings |
| `src/components/TiltCard.tsx` | 0 | 1 unused import |

These match the follow-up's pre-change full lint scan. The older **14-error/25-warning** lint report and **116/49/53** suite totals below are historical, not the current results. NuGet advisories, runner resolution/test nullability warnings, Windows native SWC loading, Node stripping/module-format warnings and existing jsdom/React `<search>` warnings remain visible; they were not hidden or treated as failures of passing checks. No fresh npm audit was run.

### Refreshed actual-component browser QA

A **new** isolated fixture compiled the current Workflow page, app layout and settings/version/rule/navigation components with existing dependencies and fresh production CSS. It supplies the current rule status/revision and recognition-catalog contracts. Authentication, navigation and all API records/responses remain synthetic; external fetches are refused. The unavailable `agent-browser` CLI was not installed; the existing in-app Chromium 152 browser was used instead.

**No passing-run generator was included.** Every version has `latestTestRun=null`, the synthetic publish endpoint refuses publication, and no publish request was sent. Simulated remote publication was solely a test of editor invalidation, not evidence of production publishing or a real runner. No real accounts, patient data, backend records, Firebase login or Colab connection were used.

Verified against the current components:

- All six panels render. Read case shows all 19 shared canonical unit rows and unitless WBC/platelet conversion. Verify explains number, wording/scope, condition/negation, topic and truncation checks; neither read-only panel exposes editable controls.
- Six difference rows distinguish nested, dotted, empty, bracketed and quoted keys and show the correct published/selected values. Inspecting differences does not load the selected draft, and no duplicate-key console warning occurs.
- Every character in a typed suffix survives, including after a same-ID refetch and panel switches. The saved full payload retains unknown literal/nested keys, arrays, chunking and safety settings.
- `topK=21` sends no mutation, sets `aria-invalid` and focuses the error summary; `topK=20` saves. Step switching focuses the settings heading, and Tab reaches the labeled Min rerank input. No unlabeled visible controls were found in the inspected states.
- Rule link terms survive hidden panel switches. Saving submits `{ linkTerms, expectedRevision }`, advances the synthetic saved revision and clears review. Native clinician confirmation opens with Cancel focused, uses the actual modal/backdrop CSS, closes with Esc without a request and restores focus. Confirmation submits the saved revision and leaves enablement unchanged.
- Typing during a deferred save remains dirty after success; the saved artifact contains the submitted text, not later keystrokes. A simulated new publication pins that dirty snapshot and disables stale saving. Refusing reload keeps it; accepting loads the new publication and clears dirty state.
- Dirty sidebar navigation and sign-out open native confirmations. Refusal preserves the route/text and sends no sign-out; same-page navigation does not prompt.
- Rollback refusal sends no request; confirmation restores the historical publication as a new untested draft and leaves publication unchanged. Discard refusal leaves that draft unchanged; confirmation preserves its discarded history and restores the loaded published editor.
- Viewer inputs/loading/saves are restricted. Editors can save/load but not publish. Owners still see disabled publication with `no test run`. **All six steps remain Not completed** through settings, configuration saves, rule approval, rollback and discard.
- Screenshots were inspected. No page-level horizontal overflow was observed in the inspected narrow Review-pane viewports (531 and 462 CSS pixels), and no browser console warning/error was recorded.

Fixture/build/evidence files are outside the project at `C:\Users\abdom\AppData\Local\Temp\opencode\clinical-rag-workflow-ui-refresh-20261115\`. Screenshots are `locked-verifier-and-differences.png` and `unit-assumptions.png`; `qa-results.md` records the fixture scope. The QA tab was closed, its server stopped, and port **8767 was verified not listening**.

This is synthetic UI validation, not a live authenticated end-to-end or clinical-quality test. Back/Forward traversal and the browser-native `beforeunload` dialog were not refreshed in this follow-up. Firefox/Safari/mobile-device, assistive-technology testing and a full accessibility audit remain unperformed; earlier browser findings below are retained as historical evidence only.

## Historical initial lifecycle validation

Tests were written and run before the corresponding fixes. Staged red runs reproduced 5 initial RTL failures, 16 expanded RTL failures, 37 focused xUnit failures with 2 passes, and 4 Python failures with 11 passes. Later additions exposed 4 race failures and 1 case-insensitive artifact-comparison failure before their fixes. These are separate staged runs, not totals to add together.

The **original** lifecycle rerun after its browser QA produced the following results. They describe that earlier source/test snapshot, not this follow-up or the current repository-wide totals above. Backend/Python commands ran from the repository root; frontend commands ran from `frontend/`.

| Check | Result |
| --- | --- |
| `dotnet test backend.Tests/ClinicalRag.Tests.csproj --no-restore --logger "console;verbosity=minimal"` | **116 passed, 0 failed, 0 skipped** |
| `python -m pytest tests -q` | **49 passed** |
| `npm.cmd test -- --reporter=dot` | **14 Node utility tests + 53 Vitest tests passed**; Vitest ran five files |
| `npx.cmd tsc --noEmit --incremental false` | **Passed**, no diagnostics |
| Focused ESLint command below | **Passed**, no diagnostics |
| Backend build: `dotnet build backend/ClinicalRag.Api.csproj --no-restore` | **Passed**, 0 errors |
| Frontend production build: `npm.cmd run build` | **Passed**, including TypeScript and route generation |
| Full frontend ESLint scan, saved JSON report | **Failed: 14 existing errors, 25 warnings** outside the focused Workflow/navigation implementation |

Historical focused lint command:

```powershell
npx.cmd eslint "src/app/(app)/w/[workspaceId]/workflow/page.tsx" "src/app/(app)/layout.tsx" "src/components/navigation-guard.tsx" "src/components/workflow" "src/lib/workflow-config.ts" "tests/*.test.ts" "tests/*.test.tsx" "vitest.config.ts"
```

The historical full lint report is `C:\Users\abdom\AppData\Local\Temp\opencode\workflow-lint-20261115.json`. At that stage, errors were in Chunk Review (10), Verify Email (1), AuthForm (2) and the former `Chunk.metadata` type (1). Warning-only files also included Documents, Rules, Settings, Workspaces and TiltCard. These are **not** the current lint totals or outstanding-file list.

Regression coverage includes complete/deep merges, literal placeholders, type/range/duplicate-key rejection, locked safety, legacy retrieval fallback, lifecycle restrictions, exact evidence binding, newer-run rechecks, stale publication preconditions, HTTP ownership/roles, SQLite stale-artifact/concurrency checks (including `NOCASE`), typing/refetch/save persistence, dirty publication snapshots, loaded-versus-selected completion, navigation cleanup and workspace/user replacement. Existing privacy, verified-result, request-scope and rule-approval suites remain passing.

### Historical actual-component browser QA

The original isolated fixture compiled the then-current Workflow page, app layout, settings/version/rule/navigation components and API/configuration helpers using existing dependencies and production CSS. Its rule/recognition contracts predate later changes and were not reused unchanged for the refreshed QA. Authentication, Next navigation/link context and API responses were synthetic. **Passing runs and publication in this historical fixture were synthetic only**, not proof of a working production runner or live backend connection. No real accounts, patient data or production records were used.

Verified in the in-app Chromium 152 browser (OpenCode 2.3.0 / Electron 44.9.1):

- Character-by-character typing survives same-ID refetches and switching settings. Saved payloads preserve unknown nested keys, arrays, chunking and safety settings.
- Invalid `topK=21` sends no API mutation, marks the field and focuses the error summary; `topK=20` saves. Step selection moves focus to the settings heading; Tab reaches labeled numeric inputs.
- All six panels, read-only verifier safeguards, complete JSON preview and version differences render. No unlabeled visible input/select/textarea was found in the inspected state; the 800-pixel viewport had no page-level horizontal overflow.
- A synthetic latest pass enables owner publication and marks only the loaded version's steps. A newer failure disables publication and retains only its completed steps. Editor/viewer controls remain restricted even with a passing selected draft.
- Publication preserves the submitted artifact. Rollback confirmation creates a new untested draft and leaves publication unchanged; discard cancellation makes no change, while confirmation preserves discarded history.
- Link-term edits survive hidden panel switches, use the existing rule update payload, clear approval and require owner re-approval. Approval does not mark a workflow step complete.
- Typing during a deferred save remains dirty after its late success. A changed publication leaves that complete source snapshot pinned and save-disabled; reload refusal preserves edits, acceptance replaces them. Clean publication initialization works without confirmation.
- Sidebar links and sign-out prompt while dirty; refusal preserves the editor and sends no sign-out. Same-page links do not prompt. Sidebar collapse/expand is keyboard-operable and preserves typing.
- Actual cancelable Back traversal to another SPA route prompts and preserves edits on refusal. Same-page traversal does not prompt.
- Dirty reload was intercepted. The tool did not expose its native `beforeunload` dialog, so acceptance/dismissal of that browser-native dialog was not verified. Dirty unload cancellation, clean reload, registration cleanup and confirmed sign-out cleanup were checked separately.
- Screenshots were inspected; no browser console warnings or errors were recorded.

Fixture/build/evidence files are outside the project at `C:\Users\abdom\AppData\Local\Temp\opencode\clinical-rag-workflow-ui-20261115\`. Screenshots are `evidence/retrieve-and-differences.png` and `evidence/locked-verifier.png`. The QA tab was closed and the synthetic server stopped; port 8766 was verified unbound. Its background exit code 1 resulted from intentional shutdown, not a QA/build defect.

Browser limitations: the Navigation API guard is feature-detected and handles only cancelable traversal. Unsupported/noncancelable SPA history traversal remains unprotected; history is not patched and no guard-created history entries are inserted. `beforeunload` remains the document-leave fallback and is subject to browser restrictions. Firefox/Safari/mobile, assistive-technology testing and a full accessibility audit were not performed.

## Lifecycle implementation file inventory

This workspace has no Git metadata. The following manually recorded inventory records the **original lifecycle implementation**, not files newly changed in this follow-up or a generated Git diff. The current follow-up's smaller change set is listed above; other areas have since changed independently. Existing `config/default_prompts.json`, Node utility tests and Next/TypeScript configuration were reused, not rewritten for the lifecycle task. Generated outputs and isolated synthetic QA fixtures are not project implementation changes.

### Shared configuration / Python

- `config/config.schema.json`
- `config.py`
- `tests/test_workflow_config.py`

### ASP.NET implementation

- `backend/ClinicalRag.Api.csproj`
- `backend/Program.cs`
- `backend/Controllers/ConfigController.cs`
- `backend/Controllers/AnalysisController.cs`
- `backend/Services/ConfigService.cs`
- `backend/Services/ConfigValidation.cs`
- `backend/Services/ConfigTestRuns.cs`
- `backend/Data/AppDbContext.cs`
- `backend/Data/Migrations/AppDbContextModelSnapshot.cs`

### xUnit regressions

- `backend.Tests/ConfigServiceTests.cs`
- `backend.Tests/ConfigLifecycleControllerTests.cs`
- `backend.Tests/ConfigTestRunProviderTests.cs`
- `backend.Tests/ConfigConcurrencyTests.cs`
- `backend.Tests/ConfigAuthorizationTests.cs`
- `backend.Tests/AnalysisControllerTests.cs`

### Frontend implementation

- `frontend/src/app/(app)/w/[workspaceId]/workflow/page.tsx`
- `frontend/src/app/(app)/layout.tsx`
- `frontend/src/components/navigation-guard.tsx`
- `frontend/src/components/workflow/step-settings.tsx`
- `frontend/src/components/workflow/config-versions.tsx`
- `frontend/src/components/workflow/rule-link-terms.tsx`
- `frontend/src/lib/workflow-config.ts`
- `frontend/src/lib/types.ts`

### Frontend test setup / regressions

- `frontend/package.json`
- `frontend/package-lock.json`
- `frontend/vitest.config.ts`
- `frontend/tests/setup.ts`
- `frontend/tests/workflow.test.tsx`
- `frontend/tests/workflow-lifecycle.test.tsx`
- `frontend/tests/workflow-ownership.test.tsx`
- `frontend/tests/workflow-config.test.ts`
- `frontend/tests/navigation-guard.test.tsx`

### Documentation

- `README.md`
- `docs/workflow-lifecycle-validation.md`

## Remaining integration work and warnings

1. **Implement Prompt 11 before enabling publication.** No real test-run entity, database set, migration, provider or runner was added. The runner must load a persisted normalized draft server-side, execute real tests through the verified pipeline and produce server-authored status/step evidence. Bind it to the exact workspace/configuration ID/version/hash and return the latest started run, including failures/in-flight runs. Do not trust client `LastRunJson` or substitute fixture/fake evidence in production.
2. **Integrate the evidence transaction/lock.** A persisted provider must share publication's DbContext/serializable transaction, or use an equivalent lock also held when starting runs, so a newer run cannot appear between the final evidence read and commit. Cached evidence is prohibited. Test this with the real database/provider, including concurrent run creation/publication, before replacing `NoConfigTestRunProvider`.
3. **Live integration remains untested.** Start the actual Colab GPU runtime, configure matching service secrets and retrieval origin, authenticate with Firebase and exercise ASP.NET → Python → ASP.NET retrieval / Colab generation with loaded guideline data. Unit/HTTP/fixture tests do not establish GPU startup, deployment connectivity or clinical output quality. Keep approved rules and configuration request-scoped and preserve the existing privacy/verifier safeguards.
4. **Current full frontend lint still fails: 3 errors, 7 warnings.** See the current file breakdown above, not the historical JSON report. Focused Workflow/layout/navigation/component/helper/test lint and TypeScript pass; unrelated lint cleanup was not expanded into this task.
5. **Dependency/environment warnings remain.** NuGet reports moderate/high advisories for `System.Security.Cryptography.Pkcs` 9.0.4 and a high advisory for test dependency `SQLitePCLRaw.lib.e_sqlite3` 2.1.11, plus xUnit runner resolution (2.9.2 → 3.0.0) and existing test nullability warnings. The earlier npm install reported 19 vulnerabilities; that is not a freshly rerun audit. No audit fixes or unrelated dependency upgrades were applied. RTL, user-event, Vitest and jsdom were approved development/test additions during the original lifecycle implementation; **no dependencies were added in this follow-up**.
6. Native Windows SWC still fails to load as a valid Win32 application; the fresh Next production build successfully used WASM and completed. Node utility tests emit experimental TypeScript-stripping/module-format warnings, and full RTL runs emit existing jsdom/React `<search>` warnings. These were not hidden or converted into claimed failures of passing checks.

No commit or push was made. See [README lifecycle guidance](../README.md#workflow-configuration-lifecycle) and the earlier [verified pipeline validation](verified-pipeline-validation.md) for routing/privacy details and its historical validation results.
