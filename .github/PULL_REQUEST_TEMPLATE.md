## Change

Describe the problem, requested scope, and why this change is needed.

## Prompt 0 checklist

- [ ] Read every file before modifying it and searched its usages/callers.
- [ ] Added a failing regression test before the fix where possible; explain any exception.
- [ ] Changed only the requested scope; preserved unrelated work and historical evaluation evidence.
- [ ] Did not weaken verification, `PipelineResultPolicy`, owner-only publication, approved-rules-only execution, privacy projection, or **Not covered**.
- [ ] Used only synthetic/de-identified fixtures; no real case text, secrets, uploads, credentials, or protected-key material appear in source, commits, logs, screenshots, or reports.
- [ ] Ran `dotnet build backend/ClinicalRag.Api.csproj` and `dotnet test backend.Tests/ClinicalRag.Tests.csproj`.
- [ ] Ran `python -m pytest tests -q`.
- [ ] Ran frontend `npm run lint`, `npm test`, and `npm run build`.
- [ ] Reported actual command exit results, failing tests/warnings, and limitations; no fabricated passing runs or hidden red checks.
- [ ] Included a real test-branch **CI run** link and its current job results.
- [ ] Requested independent/code-owner review; no self-approval, administrator bypass, or merge while a required check is red/missing/skipped.

## Files changed

List paths and the purpose of each change.

## Tests added

List regressions and their observed red-to-green evidence, or explain why tests are not applicable.

## Commands and actual results

| Command / CI job | Actual result | Failures, warnings, or evidence link |
| --- | --- | --- |
| .NET build / tests | Not run | |
| Python tests | Not run | |
| Frontend lint / tests / build | Not run | |
| Secret scan / CI gate | Not run | |

## Unfinished work

State blockers, current red items, untested behavior, deployment limits, and any follow-up outside this PR. Unit/synthetic evidence is not clinical validation and does not authorize rule approval or configuration publication.
