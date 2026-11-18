# Clinical evaluation — 2026-11-16T07:57:05.133455+00:00

**Status:** BLOCKED
**Clinical evidence:** NO — not clinician-labelled evidence
**Label source:** `unavailable`
**Cases in set:** 0

## Blocked

- pipeline-url / EVAL_PIPELINE_URL is required (argument or environment variable).
- backend-url / EVAL_BACKEND_URL is required (argument or environment variable).
- workspace-id or EVAL_WORKSPACE_ID is required.
- A Firebase token is required via EVAL_FIREBASE_TOKEN or --firebase-token-file.
- EVAL_PIPELINE_KEY is required for the authenticated pipeline service.
- Clinical set is not runnable:
  - clinician.name must contain 1–128 characters.
  - clinician.role must contain 1–128 characters.
  - clinician.organisation must contain 1–256 characters.
  - clinician.labelledAt must be an ISO date (YYYY-MM-DD).
  - clinician.attestation must contain 20–2000 characters.
  - guidelineCorpus.description must contain 1–2000 characters.
  - guidelineCorpus.documentNames must contain 1–32 items.
  - No clinician-labelled cases are present. Fill eval/clinical/template.json or use a completed clinician set; the synthetic worked example is not a substitute.

No clinical case text was sent. This is a blocked run, not a passing evaluation.


## Commands used

```text
'C:\Users\abdom\AppData\Local\Programs\Python\Python313\python.exe' eval/run_clinical_eval.py --clinical-set eval/clinical/template.json --output-dir eval/results/clinical
'C:\Users\abdom\AppData\Local\Programs\Python\Python313\python.exe' eval/run_eval.py --mode clinical --clinical-set eval/clinical/template.json --output-dir eval/results/clinical
# EVAL_FIREBASE_TOKEN and EVAL_PIPELINE_KEY are supplied only through the environment; values are never recorded.
```

## Report privacy

This report contains no case input text, model recommendation, quote, source excerpt or patient identifier.

## Limitations

- The statement matcher is a lexical triage heuristic with negation consistency; it is not semantic entailment or clinician review of each answer.
- A matched expected statement does not prove the recommendation is complete, correct or safe for a patient.
- Not covered means no verifiable statement in the loaded guidelines; it does not mean the topic or patient is safe.
- Live staging and real-model evidence are not a substitute for independent clinical review.
- Model/encoder metadata was unavailable; this report cannot independently verify which encoder and reranker served retrieval.
