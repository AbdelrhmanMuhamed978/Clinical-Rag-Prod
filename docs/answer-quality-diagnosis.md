# Answer-quality diagnosis — 16 November 2026

**Not a clinical validation and not a clean sign-off.** This report separates verifier
rejections from model abstention and from answer fidelity. Verified item counts do not
establish that the requested question was answered, that the requested source status was
respected, or that the content is clinically correct.

All evidence is synthetic or de-identified. Prompts containing the fictional case are not
stored in the repository; only source-free counts, synthetic fixture identifiers and
request IDs are recorded here.

## What was observed

The original observation was three synthetic cases on the real Colab Qwen3-4B/PubMedBERT
stack:

- (a) a FINAL page-3 marker question retrieved page 3 containing the expected text but
  returned **Not covered**;
- (b) a full fictional case planned three problems, retrieved 18 passages and verified
  nothing;
- (c) a DRAFT creatinine question verified four items, so the path can work.

The original drafts for (a) were unavailable, so the historical zero count could not be
reproduced from the retained artifacts. The (a) fixture below is a **new reproduction**
against the same fixed source context, not the original recording. Its historical cause is
therefore not established.

## Recorded cases and counters

The replay harness is `tests/replay/`. `contexts.json` stores each exact synthetic source
once; `a.json`, `b.json`, `c.json`, `postfix-4b.json` and `comparison.json` store the
recorded outputs and counts. Retrieval is not rerun.

| Case | Recorded run | Candidates | Verified | Non-zero rejection reasons | Reading |
| --- | --- | ---: | ---: | --- | --- |
| (a) marker, FINAL page 3 | baseline reproduction, 4B | 4 | 4 | none | Exact quotes accepted, but all four statements were unrelated to the requested marker. This is a draft-selection/fidelity failure, not a verifier rejection. |
| (b.1) septic shock/hypotension | baseline, 4B | 0 | 0 | `empty_draft=1` | Literal `NOT_FOUND`; verification never ran. |
| (b.2) acute kidney injury in sepsis | baseline, 4B | 0 | 0 | `empty_draft=1` | Literal `NOT_FOUND`; verification never ran. |
| (b.3) VTE prophylaxis in sepsis | baseline, 4B | 0 | 0 | `empty_draft=1` | Literal `NOT_FOUND`; verification never ran. |
| (c) DRAFT creatinine review | baseline, 4B | 4 | 4 | none | The verifier accepts exact quotes; the path works. |
| (a) revised prompt | post-fix, 4B | 2 | 1 | `quote_not_found=1` | The FINAL marker verified. The second item appended `NOT_FOUND` to its quote and was rejected. |
| (c) revised prompt | post-fix, 4B | 2 | 0 | `negation_conflict=2` | Full-paragraph quotes included an unrelated `must not be used` warning; the unchanged negation check rejected them. |

Dominant reason for the original (b): **`empty_draft`**, one per problem. A literal
`NOT_FOUND` is deliberately counted as `empty_draft` so that the absence of candidates is
visible; it is not a verifier rejection. This is model abstention, not a quote-matching
failure. The recorded sources are synthetic test markers, not sepsis management
recommendations, so abstaining was the safe outcome for that fixture. The pipeline does not
retry a genuine `NOT_FOUND` merely to force coverage.

For (a), the dominant finding is not a closed rejection reason at all: the verifier
accepted four exact but off-question statements. The reproduction therefore does not prove
a verifier false negative. The corrected prompt asks for the requested fact, the requested
source status/page and the shortest complete supporting span; under that prompt the marker
verifies. Whether the original historical run failed for the same reason remains unknown.

## Fixes applied

Only defects with evidence were changed; the safety checks were not weakened.

1. **Closed, text-free diagnostics.** Every parsed candidate gets one or more reasons from
   `empty_draft`, `json_parse_error`, `quote_not_found`, `formatting_mismatch`, `number_missing`,
   `stronger_than_source`, `negation_conflict`, `scope_mismatch`, `topic_mismatch`,
   `truncated`, `deduplicated`, `below_threshold`. Counters are aggregated per problem and
   logged by request ID. They contain no source text, quotes, prompts or patient data.
   They are returned only inside authenticated test execution, only to owners/editors, and
   are excluded from saved history, publication evidence and comparison evidence.

2. **Exact-only quote handling.** Previously a low-threshold fuzzy quote replacement could
   turn altered words into a “match”. That was removed. The verifier now requires the
   matched span to be an exact substring after NFC normalisation, typographic-ligature
   expansion, whitespace/punctuation normalisation and digit-comma removal. Citation
   repair only corrects which retrieved source an already exact quote belongs to; it never
   replaces the quote's words.

3. **Complete JSON extraction.** Fenced JSON and complete objects/arrays are parsed without
   repairing quotes, commas or truncated values. A malformed envelope is not searched for
   salvageable nested items; it is counted as `json_parse_error`.

4. **Prompt improvements.** The answer prompt now directs the model to answer the actual
   factual source question, respect an explicitly requested FINAL/DRAFT status and page,
   treat synthetic test labels as non-clinical, avoid inventing management from a caution,
   copy the shortest complete supporting span, avoid unrelated neighbouring sentences, and
   never append `NOT_FOUND` after items.

5. **Planning corrections.** Standalone questions keep their real wording for drafting and
   topic checks instead of being replaced by a search rewrite. Verified conversation
   history still resolves follow-ups. Isolated hypotension or kidney flags no longer
   introduce sepsis-specific queries or VTE prophylaxis by themselves.

No number, strength, negation, scope, topic or support-ratio check was relaxed. The
existing support threshold remains in force.

## Post-fix results

| Run | (a) verified | (c) verified | Rejections |
| --- | ---: | ---: | --- |
| 4B, revised no-patient-context bundle | 2 | 1 | (c): `negation_conflict=1` |
| 8B-AWQ, same revised bundle | 2 | 4 | none |

The prompt fix recovered a verified marker item and reduced paragraph-quote negation
rejections, but it did not fully solve source-status fidelity. Both models still returned a
verified DRAFT marker inside the revised question that requested FINAL. The 4B (c) run also
still had one candidate rejected for excess neighbouring negation.

## Same-GPU larger-model comparison

Both exact-payload bundles ran on the same Tesla T4 with PubMedBERT embeddings, without a
retrieval rerun. `tests/replay/comparison.json` records all fourteen pairs, output
references and payload hashes; private prompts are omitted. The comparison flag
`COLAB_ENABLE_LARGER_MODEL=1` selects `Qwen/Qwen3-8B-AWQ`; the default remains
`Qwen/Qwen3-4B-Instruct-2507`.

| Bundle | Model | (a) | (b.1) | (b.2) | (b.3) | (c) |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| Captured baseline | 4B | 4 | 0 | 0 | 0 | 4 |
| Captured baseline | 8B-AWQ | 4 | 0 | 2 | 0 | 4 |
| Revised | 4B | 2 | — | — | — | 1 |
| Revised | 8B-AWQ | 2 | — | — | — | 4 |

The 8B baseline (b.1) produced five candidates and rejected all of them
(`stronger_than_source=2`, `negation_conflict=2`, `topic_mismatch=3`; reasons may repeat per
candidate). Its (b.2) verified two items, but they were cautions that a finding does not
establish a diagnosis, not sepsis management; two further candidates were rejected for
`topic_mismatch`. Its (b.3) remained literal `NOT_FOUND` (`empty_draft=1`). A larger model
is therefore not a uniform fix for useful coverage: it drafted more, but not all of it was
on-question or safe.

## Limits

- Counts show verifier acceptance, not answer fidelity, requested source-status compliance,
  management coverage or clinical correctness.
- The comparison used frozen synthetic source contexts and did not rerun retrieval or the
  full web request path. It does not certify end-to-end useful answers.
- The original (a) draft is unavailable, so its historical zero count and cause cannot be
  established.
- No new useful-answer acceptance, trusted-test publication or hosted CI run was performed
  as part of this comparison.
- Existing unrelated frontend lint errors and dependency advisories remain.

## Validation commands and actual results

| Command | Actual result |
| --- | --- |
| `dotnet build backend/ClinicalRag.Api.csproj --nologo --no-restore` | Passed; 0 errors, 2 package-advisory warnings |
| `dotnet build backend/ClinicalRag.Api.csproj -c Release` | Also passed; 0 errors, 4 package-advisory warnings |
| `dotnet test backend.Tests --no-restore` | **723 passed, 0 failed, 0 skipped** |
| `python -m pytest tests -q` | **524 passed** |
| `python -m pytest tests/replay -q` | **21 passed** |
| Full suite in the extracted source archive on Colab Python 3.12 | **524 passed, 9 warnings**, exit 0 |
| `npm test` in `frontend/` | **15 Node tests passed; 548 Vitest passed** |
| `npm run lint` in `frontend/` | **Failed: 3 errors, 7 warnings** in existing unrelated files |
| `npm run build` in `frontend/` | Passed; native SWC failed to load and the WASM fallback was used |

The first frontend test run was started concurrently with `npm run build` and hit one
5-second Vitest timeout in `rules-editor.test.tsx`; an isolated rerun passed **548/548**. The
timeout is recorded here rather than presented as a clean result. The corrected source
archive used for the Colab run has SHA-256
`4a49ec07f344467b8cade29754c55f9c2550ff7e330e6b6b3d6e6f8b4268db92`.

The Colab archive run initially failed because the source-only archive omitted governance
resources required by the offline tests. The builder now includes only `.gitignore`,
`.github/scripts/check_ci_gate.py`, `.github/workflows/ci.yml`, `.github/CODEOWNERS` and
`.github/PULL_REQUEST_TEMPLATE.md` for that purpose; the regression
`test_real_archive_contains_governance_resources_required_by_offline_tests` fails if they
are missing.

## Files changed for this diagnosis

- `pipeline.py` — exact-only quote verification, complete JSON parsing, diagnostics,
  question handling, planning corrections, prompt text.
- `operations.py` — closed rejection reasons and privacy-safe diagnostic logging.
- `config/default_prompts.json` — factual-answer, source-status/page, shortest-quote and
  rewrite instructions.
- `pipeline_service/app.py` — service-authenticated test-only diagnostics.
- `backend/Services/PipelineAnswerDiagnostics.cs` — closed, bounded, numeric-only projection.
- `backend/Controllers/TestsController.cs`, `backend/Services/TestHarnessService.cs`,
  `backend/Services/PipelineClient.cs` — diagnostics transport restricted to authenticated
  test execution.
- `backend.Tests/*` — diagnostics/privacy/trusted-evidence regressions.
- `tests/replay/*` — recorded fixtures, comparison data, harness and offline tests.
- `tests/test_answer_quality.py` — verifier, parser, rewrite, planning and prompt regressions.
- `scripts/prepare_colab_zip.py`, `tests/test_colab_bootstrap.py` — source-archive
  governance resources and regression.
- `colab/models.py` — default-off larger-model selection.
- `colab/answer_quality_comparison.py` — exact-payload, declared-model/GPU-checked
  comparison runner.
- `docs/answer-quality-diagnosis.md` — this report.

## Unfinished work

- FINAL-only fidelity is still unresolved: both models include a verified DRAFT item when
  the revised question requests FINAL.
- The revised 4B (c) style still rejects one candidate for excess neighbouring negation.
- Original (a) historical zero-verified reproduction is unavailable.
- Case (b) remains `empty_draft` on the captured 4B output; no forced retry was added, and
  the 8B alternatives were not uniformly useful.
- The retained local stack is alive (`/health` 200, frontend 200, database ready), but
  `/ready` returned 503 because the Colab model service was offline after the earlier
  runtime was replaced. The replacement runtime was used only for the offline archive
  suite and was disconnected after it; no model server was started there.
- Independent authorized review and a fresh hosted CI run remain outstanding.
- No commit, push or merge was performed.
