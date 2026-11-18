# Clinician-labelled clinical evaluation set

This directory contains the **contract** for a clinician-labelled evaluation set,
an empty template, and one clearly marked synthetic worked example. It does not
contain real clinician labels. Engineering must not invent labels, copy model
outputs into `expectedStatements`, or present the synthetic example as clinical
evidence.

## Who may label cases

A named clinician who is responsible for the relevant guideline content must:

1. Read the loaded guideline documents and confirm their version/status.
2. Write each case in `eval/clinical/<your-set>.json`.
3. Fill `clinician.name`, `clinician.role`, `clinician.organisation`,
   `clinician.labelledAt` (ISO `YYYY-MM-DD`) and a short `attestation` confirming
   the labels are their clinical judgement.
4. Keep the completed file under review; do not edit a completed set after it has
   been used for a dated run. Create a new version instead.

`eval/clinical/template.json` is intentionally empty. The loader and runner
reject it until a clinician fills it in.

## Privacy rules

- Use a **question** or a **de-identified case** only. Do not include names,
  initials, record numbers, dates of birth, contact details, addresses, exact
  dates of care, or any other direct identifier.
- A de-identified case may contain the minimum clinical detail needed to ask the
  question. If in doubt, use a standalone question instead of a case.
- Evaluation reports deliberately do **not** write case input text, model
  recommendations, quotes, or document excerpts. They write case IDs, counts,
  matched statement indexes, source ranks and metric values only.
- The clinical set file itself is the source of truth and must be handled under
  the same policy as other clinical evaluation material.

## File format

```json
{
  "schemaVersion": "1.0",
  "labelSource": "clinician",
  "instructionsVersion": "1.0",
  "clinician": {
    "name": "Clinician name",
    "role": "Role / specialty",
    "organisation": "Organisation",
    "labelledAt": "2026-11-16",
    "attestation": "I confirm these labels are my clinical judgement and were checked against the named guideline versions."
  },
  "guidelineCorpus": {
    "description": "Guideline set and version loaded in the evaluated workspace.",
    "documentNames": ["guideline-a.pdf", "guideline-b.pdf"]
  },
  "cases": [
    {
      "id": "lowercase-unique-id",
      "kind": "question",
      "input": "A guideline question or de-identified case.",
      "expectedDocuments": [
        {
          "fileName": "guideline-a.pdf",
          "page": 12,
          "section": "Optional section label for reviewer context"
        }
      ],
      "expectedStatements": [
        "A key statement the verified answer should contain."
      ],
      "forbiddenStatements": [
        "A statement that must not appear as verified guidance."
      ],
      "notes": "Optional reviewer note."
    }
  ]
}
```

### Field rules

| Field | Requirement |
| --- | --- |
| `schemaVersion` | Exactly `"1.0"`. |
| `labelSource` | `"clinician"` for a real set; `"synthetic-fixture"` only for engineering scaffolds/worked examples. |
| `instructionsVersion` | Version of these instructions used by the clinician. |
| `clinician` | Required for clinician sets. All five fields must be non-empty. `labelledAt` must be a real ISO date not in the future. |
| `syntheticNotice` | Required only for `synthetic-fixture`; must say the labels are synthetic and not clinician labels. |
| `guidelineCorpus.documentNames` | The document titles/file names loaded in the evaluation workspace. |
| `cases[].id` | Unique, lowercase letters/digits/hyphens, 2–64 characters. |
| `cases[].kind` | `"question"` or `"deidentified_case"`. |
| `cases[].input` | Non-empty, at most 100,000 characters. |
| `cases[].expectedDocuments` | At least one; each needs a file name/title and a positive page number. `section` is optional reviewer context and is not used for scoring. |
| `cases[].expectedStatements` | At least one statement the verified result should contain. These are labels, not model output. |
| `cases[].forbiddenStatements` | Statements that must never appear as verified guidance. May be empty, but should be used for known unsafe or contradicted claims. |
| `cases[].expectedMode` | Optional `"question"` or `"case"`; scored against `plan_source`. |
| `cases[].pendingClinicianFields` | Optional list of label names still awaiting clinician extraction. Pending fields are counted in reports and must not be filled by engineering. |

A statement may not appear in both `expectedStatements` and
`forbiddenStatements`. Duplicate document/page pairs and duplicate statements are
rejected.

`eval/clinical/real-who-crag-pending.json` is an engineering scaffold for the
real WHO CrAg question. It is explicitly **not clinician-labelled**: the
screening-unavailable expectation is listed in `pendingClinicianFields` and must
be filled in by a clinician from the source. Do not present its run as clinical
evidence.

## How to run

The full-path evaluator calls the real verified pipeline service with a real
Firebase token, a real workspace retrieval endpoint, and real encoder/reranker
models. It enables `testRunDiagnostics` to collect text-free rejection counters.

```powershell
$env:EVAL_FIREBASE_TOKEN = "<verified-user Firebase ID token>"
$env:EVAL_PIPELINE_KEY = "<pipeline service key>"

python eval/run_eval.py --mode clinical `
  --clinical-set eval/clinical/<your-set>.json `
  --pipeline-url https://pipeline.staging.example `
  --backend-url https://api.staging.example `
  --workspace-id <workspace UUID> `
  --top-k 8 `
  --output-dir eval/results/clinical
```

The evaluator fetches the workspace's published configuration and approved rule
snapshot from the backend using the Firebase token. Use `--config-file` and
`--rules-file` only when those artifacts were exported from the same evaluated
configuration; record why in the run notes.

`--backend-url` must be the same retrieval origin the pipeline service is
configured to call (`PIPELINE_RETRIEVE_BASE_URL`), because the service rejects a
`retrieveUrl` that targets a different origin or workspace path.

`--allow-synthetic` is required to run the synthetic example. The report will
state `clinicalEvidence: false`; that is a format/dry-run check, not a clinical
evaluation.

The runner writes a new timestamped directory under `eval/results/clinical/`
with `report.md`, `run.json`, `per-case.csv` and `commands.txt`. It never
overwrites an existing result. If prerequisites are missing (staging stack,
token, clinician labels), it writes a **BLOCKED** report with the reason and the
command used, and exits non-zero.

Optional thresholds are available for `hit@k`, MRR, case-level verified-rate,
expected-statement verified-rate, not-covered-rate, unsupported-claim rate and
error rate. A forbidden-statement violation always fails the run. Any configured
threshold failure is recorded in the report and returns a non-zero exit code.

## Metrics

The report contains:

- **hit@k** — the expected document/page appears within the best within-problem
  source rank, using `k` (default 8). Matching uses the document title/file name
  and page number.
- **MRR** — reciprocal rank of the first matching expected source, averaged over
  cases; zero when no expected source is found.
- **verified-rate** — the fraction of scored cases with at least one verified
  item (the prior evaluator's case-level meaning).
- **expected-statement verified-rate** — the fraction of clinician expected
  statements matched by at least one verified recommendation or verified quote.
- **not-covered-rate** — the fraction of cases where no expected statement was
  matched and the pipeline therefore did not cover the labelled point. This is
  not a claim that the patient or topic is safe.
- **unsupported-claim rate** — `(candidate statements - verified statements -
  deduplicated statements) / candidate statements`, using the pipeline's
  text-free diagnostics. This measures statements rejected by verification.
- **per-reason rejection counts** — aggregated pipeline verification reason
  counters (`quote_not_found`, `number_missing`, `negation_conflict`,
  `topic_mismatch`, and the other fixed diagnostics codes).
- **forbidden-statement violations** — any forbidden statement matched in a
  verified recommendation or quote. A violation is always a run failure.

Statement matching is a **lexical triage heuristic**: normalized tokens, a
five-character prefix stem, negation consistency, and a configurable coverage
threshold (default 0.8). It is not semantic entailment, not clinician review of
each answer, and not evidence of clinical safety or effectiveness. The per-case
report exposes the coverage values so a clinician can review disagreements.

## Turning answer flags into cases

Owner-only **Flagged answers / evaluation backlog** entries store the run ID,
configuration version and reason category, never case text. A clinician can use
those flags to decide which topics need a new labelled case. Do not copy the
original case text into the evaluation set unless it is independently
de-identified and the clinician supplies the labels.

## Synthetic worked example

`eval/clinical/synthetic-example.json` is derived from
`eval/retrieval-fixture.json`. It exists to test the loader and report format. It
is not clinician-labelled, does not use a real guideline, and cannot support any
clinical-quality claim. The runner refuses it unless `--allow-synthetic` is
explicitly supplied, and the resulting report is marked synthetic.
