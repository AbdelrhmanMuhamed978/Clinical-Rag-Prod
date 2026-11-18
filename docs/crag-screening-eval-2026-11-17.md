# CrAg screening evaluation — 17 November 2026

**Status: engineering evidence only. Not a clinical evaluation, not clinician
sign-off and not a live model/retrieval result.**

This report records the real-guideline cryptococcal antigen (CrAg) case added to
the clinical evaluation set, the before/after offline verifier replay for the
G1–G3 guideline-fidelity work, and the question-mode “Not covered” export fix.
It deliberately separates what was measured from what remains blocked.

## Case added

`eval/clinical/real-who-crag-pending.json` adds one case:

| Field | Value |
| --- | --- |
| Case id | `who-crag-screening-before-art` |
| Kind | `question` |
| Expected mode | `question` |
| Expected pages | p.41 and p.11 of `9789240118164-eng.pdf` |
| Expected statement | “Screening for cryptococcal antigen followed by pre-emptive antifungal therapy is recommended before initiating ART for adults and adolescents living with HIV who have a CD4 count <100 cells/mm3.” |
| Forbidden lead | “For cryptococcal meningitis, induction therapy with amphotericin B is recommended.” |
| Pending clinician field | `screeningUnavailableStatement` |
| Label source | `synthetic-fixture` (engineering scaffold) |

The expected CrAg screening statement is the one supplied in the task. The
“screening unavailable” expectation is **not filled in**: it is listed in
`pendingClinicianFields` and must be extracted by a clinician from the source.
No clinician name, role, organisation, attestation or screening-unavailable
wording was invented.

The file carries an explicit synthetic notice and `labelSource:
"synthetic-fixture"`. Its run must never be reported as clinician-labelled
clinical evidence. `eval/clinical/README.md` documents the new
`expectedMode` and `pendingClinicianFields` fields and this pending scaffold.

The clinical runner was invoked with the new set and correctly refused to run
without live configuration:

```text
eval/results/clinical/2026-11-17T13-50-25Z-clinical-synthetic-blocked/
status=blocked, exitCode=4, caseCount=1, clinicalEvidence=false
```

The blocking reasons are the missing pipeline URL, backend URL, workspace ID,
Firebase token and pipeline key. No case text was sent.

## What the G1–G3 work changed for this case

The worktree contains the G1–G3 guideline-fidelity changes. For this case the
relevant effects are:

- real-PDF parsing no longer fuses the screening marker (`Screeningc` /
  `Screeninga`), leaves table `<br>` markup in the text, or splits a dashed term
  across a line;
- quote location uses the canonical exact-match view (entity/ligature, footnote
  marker, dash, whitespace and punctuation normalisation), and citation repair
  never replaces the quote's words;
- strength/certainty and the citation display are derived from the source text,
  so a narrative paragraph does not borrow a nearby recommendation's grade.

The export change is separate: question-mode “Not covered” entries keep the
human-readable problem topic label; case-mode exports still use generic
“Problem N” labels and omit patient text.

## Before/after offline replay

### Method

- **Before:** commit `a92f196a81ab2914dad68c4e42d56f887b37dede` extracted to a
  temporary directory.
- **After:** the current worktree.
- **Source:** `9789240118164-eng.pdf`, SHA-256
  `865ab98747ace555c3de012dca1b3f86713463fb78f67cd7fad704ca6966fac2`,
  pages 11 and 41.
- **Input:** a fixed policy draft, not model output. The expected screening
  statement is paired with its exact source quote; the forbidden induction
  statement is paired with a non-supporting caution quote for the main replay.
- **No retrieval, model, network or clinician review was used.** The two
  expected pages were supplied as fixed context.

Reproduce with:

```powershell
python eval/crag_offline_replay.py `
  --pdf <path-to-9789240118164-eng.pdf> `
  --case eval/clinical/real-who-crag-pending.json `
  --out eval/results/clinical-crag-offline-2026-11-17/after.json
```

The `before.json` artifact was produced by the same script with `--repo` pointing
at the extracted `a92f196` checkout. Both artifacts are in
`eval/results/clinical-crag-offline-2026-11-17/`.

### Counts

| Metric | Before (`a92f196`) | After (worktree) |
| --- | ---: | ---: |
| Cases with ≥1 verified item (verified-rate) | 1/1 (100%) | 1/1 (100%) |
| Expected statement verified (expected-statement verified-rate) | 1/1 (100%) | 1/1 (100%) |
| Forbidden statement verified in the main replay (non-supporting quote) | 0/1 | 0/1 |
| Forbidden statement verified with its exact supporting quote (guard probe) | 1/1 | 1/1 |
| Rejection reasons | `negation_conflict=1` | `negation_conflict=1` |
| Verified item required quote rewriting | yes (1) | **no (0)** |
| hit@k | not measured | not measured |
| Expected-mode accuracy | not measured | not measured |
| Pending clinician-label cases | 1 | 1 |

Parser artifacts on the same two pages:

| Artifact | Before | After |
| --- | ---: | ---: |
| Fused screening markers on p.11 | 2 | 0 |
| `<br>` tags on p.11 | 42 | 0 |
| Fused screening markers on p.41 | 1 | 0 |
| Wrapped `antigen– positive` on p.41 | 1 | 0 |

### Reading the counts

- Verified-rate did **not** improve because it was already 1/1 for this fixed
  draft. The material change is the evidence path: before, the verified item's
  quote was silently replaced by a fuzzy “best span” from the source; after,
  the exact canonical quote matches and the item verifies without rewriting any
  words. The before artifact records `verifiedItemQuoteRewritten=true`; the
  after artifact records `false`.
- The main replay rejects the induction candidate for `negation_conflict`
  because its fixed quote is a “not recommended” caution, not the induction
  recommendation. That is a rejection-counter exercise, **not** proof that a
  real model would not lead with induction.
- The guard probe pairs the forbidden induction statement with its **exact**
  supporting quote. The deterministic verifier accepts it in both revisions.
  Therefore the eval's forbidden-lead rule is only tested end-to-end, after
  lead selection/relevance has run. The offline replay did not exercise that
  stage, and the 0 violation count above must not be read as lead-selection
  safety.
- The p.41 source contains a sentence about screening being unavailable, and
  that sentence verifies exactly through the canonical verifier. This is an
  engineering source check only; it is not the clinician's expected wording and
  is not scored.

## What remains unmeasured or blocked

| Item | Status |
| --- | --- |
| hit@k / MRR | **Unavailable.** No retrieval run, embeddings, reranker or live service. The expected pages were supplied as fixed context; this is page coverage, not retrieval. |
| Expected-mode accuracy | **Unavailable.** The offline verifier does not produce `plan_source`. The case records `expectedMode: "question"` for the live runner. |
| Live verified-rate and rejection reasons | **Unavailable.** No model server and no completed clinician-labelled set. |
| Screening-unavailable expectation | **Pending clinician extraction from p.41.** Engineering did not fill it. |
| Forbidden-lead safety | **Unmeasured end-to-end.** The verifier alone accepts a correctly quoted induction statement. |
| Clinical sign-off | **None.** The set is an engineering scaffold. |
| Hosted CI | **Not rerun** for this uncommitted worktree. |

## Question-mode “Not covered” export fix

Question-mode saved results now keep each problem's topic label in the
`not_covered` list even when the pipeline's coverage list omitted it. The
frontend export does the same for Markdown/PDF. Case mode remains stricter:
titles become “Problem N”, queries/coverage are removed, warnings are rebuilt
from guideline-level cautions only, and patient text is not exported.

Regression coverage:

- `backend.Tests/AnswerDiagnosticsTests.cs` —
  `Question_storage_keeps_problem_topic_labels_in_not_covered` checks both the
  question-mode label and the case-mode generic label.
- `frontend/tests/analysis-result.test.tsx` — keeps a question topic label in
  “Not covered” and confirms a case topic marker never appears in a case
  export.

## Verification commands and actual results

| Command | Actual result |
| --- | --- |
| `python -m pytest tests/test_clinical_eval.py -q` | **29 passed** |
| `python -m pytest tests -q` | **615 passed** |
| `dotnet test backend.Tests/ClinicalRag.Tests.csproj --nologo --no-restore` | **915 passed, 0 failed** |
| `npm test` in `frontend/` | **19 Node + 584 Vitest passed** |
| `npx tsc --noEmit` in `frontend/` | Passed |
| `npm run lint` in `frontend/` | Passed |
| `npm run build` in `frontend/` | Passed (Next.js 16.4.6, webpack) |
| `python eval/run_clinical_eval.py --clinical-set eval/clinical/real-who-crag-pending.json --allow-synthetic --output-dir eval/results/clinical` | Blocked as expected, exit 4; no case text sent |

## Source archive

`clinical-rag-colab-source-20261117-python-backend-v16.zip` — 88 source files,
SHA-256
`6f63a091d208d9f9cede62eab262685f8d12e71a27e3c427908c1f0799ef9ab5`.

The archive builder now includes the clinical eval package and runner,
the baseline tolerances and checked-in synthetic retrieval artifact, the
loadtest scripts required by the offline suite, and the relocated
`python-backend/` service plus root `conftest.py`.
`tests/test_colab_bootstrap.py` asserts those assets are present.

The archive was extracted to a clean directory and its suite ran with
`python -m pytest tests -q`: **615 passed, 1 warning**. This was local Python
3.13.6, not Colab Python 3.12, and involved no GPU, model server, network or
clinical data. Passing the archive suite is not a hosted-CI or Colab-runtime
substitute.

The later semantic-ingestion/retrieval work rebuilt the source archive as
`clinical-rag-colab-source-20261117-semantic-v18.zip` (SHA-256
`b84f60cbf0aa1bbdc7b17c8a6e16b789b0ec03a2a7a5d5f46f0005067da40b55`) with
**626 Python tests**; see
[semantic ingestion and retrieval](semantic-ingestion-retrieval.md).

## Files changed for this task

- `eval/clinical/real-who-crag-pending.json` — pending real-guideline scaffold.
- `eval/clinical/loader.py` — `expectedMode` and `pendingClinicianFields`.
- `eval/clinical/metrics.py` — mode accuracy and pending-label metrics.
- `eval/run_clinical_eval.py` — CSV/report mode and pending-label fields.
- `eval/clinical/README.md` — eval contract and pending-scaffold warning.
- `eval/crag_offline_replay.py` — deterministic before/after replay.
- `eval/results/clinical-crag-offline-2026-11-17/` — before/after artifacts.
- `tests/test_clinical_eval.py` — scaffold and mode-metric tests.
- `scripts/prepare_colab_zip.py` — offline-suite assets for the clinical eval and loadtest.
- `tests/test_colab_bootstrap.py` — archive-asset regression.
- `clinical-rag-colab-source-20261117-python-backend-v16.zip` — rebuilt source archive after the `python-backend/` layout move.
- `backend/Services/PipelineResultPolicy.cs` — question-mode topic retention.
- `backend.Tests/AnswerDiagnosticsTests.cs` — backend export regression.
- `frontend/src/lib/analysis.ts` — question/case “Not covered” export behavior.
- `frontend/tests/analysis-result.test.tsx` — frontend export regressions.
- `docs/crag-screening-eval-2026-11-17.md` — this report.

## Privacy

The case is a guideline question, not a patient case. No patient text, model
recommendation or source excerpt is stored in the reports or replay artifacts;
the artifacts record counts, page numbers, parser-artifact counts and the
task-supplied label metadata only.
