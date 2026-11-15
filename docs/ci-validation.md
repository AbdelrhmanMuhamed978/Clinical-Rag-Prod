# Version-control and CI validation

The sanitized baseline `f543ccc` was pushed to `main`; CI/review changes were tested on `ci/review-gates` at `9789cebbd3a10c183d9ad85e28e0977cf706ac05`. A later evidence-only commit adds this report, saved protection fields, README linkage and ownership of these reports. Application source and existing audit/evaluation artifacts were not repaired or rewritten.

## Hosted evidence

The push triggered [Actions run 43759956403](https://github.com/AbdelrhmanMuhamed978/Clinical-Rag-Prod/actions/runs/43759956403). The run completed on **2026-11-15 at 20:49:50 UTC** with conclusion **failure**, verified with `gh run watch ... --exit-status`, `gh run view --json status,conclusion,headSha,url,jobs` and bounded log summaries:

| Required job | Actual hosted result |
| --- | --- |
| `dotnet` | **Failure**. LocalDB startup and build passed (0 errors / 4 warnings); tests: **669 passed / the same 2 failed / 0 skipped / 671 total**. |
| `pytest` | **Success**. **475 passed / 9 warnings** with the existing requirements and Python 3.11. |
| `frontend` | **Failure**. Install passed; lint: **3 errors / 7 warnings**. Tests still ran and passed **15 Node + 543 Vitest**; production build also ran and passed. |
| `secret-scan` | **Success**. Checksum-pinned gitleaks scanned both fetched commits; no leaks found. |
| `ci-gate` | **Failure**. It observed `dotnet: failure`, `pytest: success`, `frontend: failure`, `secret-scan: success` and exited 1, as required. |

No job result was converted to success, filtered, or retried into a fabricated green record. Pinned actions emitted a Node 20 deprecation/forced Node 24 runtime annotation; this did not stop them running. The snapshot above belongs to the exact tested commit, not to future untested changes. Pushing the evidence-only commit triggers another run; consult GitHub for that run's independent results.

## Saved main protection

`gh api repos/AbdelrhmanMuhamed978/Clinical-Rag-Prod/branches/main/protection` successfully returned the saved settings after application. [Selected response fields](ci-main-protection.json) preserve the verification:

- Up-to-date branches and all five required checks: `dotnet`, `pytest`, `frontend`, `secret-scan`, `ci-gate`. Each is bound to the observed `github-actions` app ID **15368**, not an arbitrary status writer.
- One approving review, code-owner review, stale approval dismissal, approval of the latest push, and resolved conversations.
- Administrator enforcement enabled; force-push and deletion disabled. No bypass actors were configured. Organization-only restriction/bypass fields were rejected for this personal repository and omitted, not used to weaken administrator enforcement.
- GitHub's `codeowners/errors?ref=ci/review-gates` endpoint returned `{"errors":[]}`.

**Bootstrap limit:** the workflow, PR template and CODEOWNERS are on the test branch, not merged into `main`. GitHub matches file owners from the PR's **base branch**, so safety-path owner matching activates only after a reviewed, green merge. The owner cannot independently approve their own PR. No merge, reviewer invitation, administrator bypass or direct post-baseline push to `main` was performed.

## Local command results

| Command | Actual result |
| --- | --- |
| `dotnet build backend/ClinicalRag.Api.csproj --nologo` | Exit 0; 0 errors / 4 warnings. Reconfirmed in a clean sanitized checkout without local credentials. |
| `dotnet test backend.Tests/ClinicalRag.Tests.csproj --nologo` in the live workspace | Exit 1 before tests: the running API locked its Debug DLL. The service was not stopped. |
| Same unfiltered `dotnet test` in a clean baseline checkout | Exit 1; **669 passed / 2 failed / 0 skipped / 671 total**. |
| `dotnet test ... --artifacts-path <isolated temporary path>` | Exit 1; **662 passed / 9 failed / 671 total**. Seven additional failures came from existing test helpers locating SQL files by walking output-directory ancestors. The normal clean-checkout run above removes that relocation artifact, not any test. |
| `python -m pytest tests -q` | Exit 0; **475 passed**. |
| Frontend `npm run lint` | Exit 1; **3 errors / 7 warnings**, unchanged. |
| Frontend `npm test` | Exit 0; **15 Node + 543 Vitest passed**. |
| Frontend `npm run build` | Exit 0; existing native SWC/WASM fallback warnings remain. |
| `python -m pytest tests/test_repository_hygiene.py tests/test_ci_governance.py -q` | Exit 0; **92 passed**. |
| Checksum-verified `actionlint` 1.7.12 on `.github/workflows/ci.yml` | Exit 0; no findings. |
| `gitleaks git --pre-commit --staged --redact=100 --no-banner --ignore-gitleaks-allow` before baseline and CI commits | Exit 0; no leaks found. |
| `gitleaks git --redact=100 --no-banner --ignore-gitleaks-allow --log-opts="--all"` | Exit 0; 2 commits scanned, no leaks found. |

The clean .NET checkout was cloned from the baseline into an approved temporary directory; no source, local configured file or deployment database was modified. Build/test warnings include existing `System.Security.Cryptography.Pkcs` and SQLite native-package advisories, xUnit runner version resolution and nullable warnings.

The two reproducible backend failures remain:

- `TestHarnessTests.Comparer_detects_missing_and_unexpected_problems`
- `TestHarnessTests.Failing_expectation_makes_run_fail_with_diff`

Both expect `Missing expected problem` evidence that is absent. Full lint still reports an unescaped apostrophe in `frontend/src/app/(auth)/verify-email/page.tsx` and two explicit-`any` errors in `frontend/src/components/AuthForm.tsx`. None was fixed in this task.

## New regressions and safe baseline

- `tests/test_repository_hygiene.py`: **42** ignore/trackability checks. Initial ignore-policy run: **24 failed / 13 passed**; final 42 pass. Build/dependency/cache outputs, environment files, uploads, key rings, DPAPI material and authorized private local files stay excluded; evaluation artifacts remain trackable.
- `tests/test_ci_governance.py`: **50** checks. Observed **50 failures before the governance files existed**, then 50 pass. Subprocess tests reject every required job's failed/cancelled/skipped/non-success or missing status, invalid input and unexpected jobs; workflow/ownership/template checks preserve required commands and review scope.
- The baseline index had **350 files**, no forbidden staged paths, and all **seven `eval/results/*.json` files**. The targeted scanner read actual **index blobs**, found no credential-shaped values, and counted 0 tunnel-provider references, 55 X-key header references and 28 registration-secret references without printing values. Gitleaks independently scanned the staged content before publication.
- Existing configured appsettings, both legacy E2E scripts, the raw conversation export, uploads, protected keys, unreviewed captures and generated archives remain local and ignored. A credential-free `backend/appsettings.example.json` is tracked instead.

## Changed files and unfinished work

Baseline hygiene/configuration changes: `.gitignore`, `README.md`, `backend/appsettings.example.json`, `tests/test_repository_hygiene.py`. CI/review changes: `.github/workflows/ci.yml`, `.github/scripts/check_ci_gate.py`, `.github/PULL_REQUEST_TEMPLATE.md`, `.github/CODEOWNERS`, `tests/test_ci_governance.py`. This report and `docs/ci-main-protection.json` add evidence without editing historical reports.

Existing red checks need separate reviewed fixes; do not hide them or weaken the gate. A valid independent reviewer and a green reviewed bootstrap merge remain necessary before the governance files exist on `main`. Synthetic/unit/CI evidence is not clinical validation, approved clinical rules, trusted draft-test evidence or authorization to publish a configuration. Prior live clinical-quality/publication failures remain unresolved.
