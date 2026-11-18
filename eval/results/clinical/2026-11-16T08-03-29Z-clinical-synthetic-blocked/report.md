# Clinical evaluation — 2026-11-16T08:03:29.351524+00:00

**Status:** BLOCKED
**Clinical evidence:** NO — not clinician-labelled evidence
**Label source:** `synthetic-fixture`
**Cases in set:** 3
**Synthetic notice:** SYNTHETIC WORKED EXAMPLE ONLY. These cases were derived by engineering from eval/retrieval-fixture.json to demonstrate the file format. They are not clinician labels, were not reviewed by a clinician, and must never be reported as clinical evidence.

## Blocked

- --pipeline-url / EVAL_PIPELINE_URL is required (argument or environment variable).
- --backend-url / EVAL_BACKEND_URL is required (argument or environment variable).
- --workspace-id or EVAL_WORKSPACE_ID is required.
- A Firebase token is required via EVAL_FIREBASE_TOKEN or --firebase-token-file.
- EVAL_PIPELINE_KEY is required for the authenticated pipeline service.

No clinical case text was sent. This is a blocked run, not a passing evaluation.


## Commands used

```text
'C:\Users\abdom\AppData\Local\Programs\Python\Python313\python.exe' eval/run_clinical_eval.py --clinical-set eval/clinical/synthetic-example.json --allow-synthetic --output-dir eval/results/clinical
'C:\Users\abdom\AppData\Local\Programs\Python\Python313\python.exe' eval/run_eval.py --mode clinical --clinical-set eval/clinical/synthetic-example.json --allow-synthetic --output-dir eval/results/clinical
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
