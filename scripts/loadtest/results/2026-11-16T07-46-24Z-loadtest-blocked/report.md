# Staging load test — 2026-11-16T07:46:25.000558+00:00

**Status:** BLOCKED
**Measured path:** real staging backend → real pipeline service → real Colab models/encoder/reranker

## Staging evidence

- Backend: `https://staging.invalid`
- Workspace: `11111111-1111-1111-1111-111111111111`
- Question SHA-256: `46b58fdbcee03e6a206b69a4164b1bdeeb4749bfc7d4e84da15c61e916d88dc2` (text not recorded)
- Query SHA-256: `46b58fdbcee03e6a206b69a4164b1bdeeb4749bfc7d4e84da15c61e916d88dc2` (text not recorded)
- Duration per run: 60s
- Concurrency levels: 1, 3, 10
- Chunk jobs concurrent: yes

## Preflight

```json
{}
```

## Blocked

- k6 is not installed or not on PATH. Install k6 from https://k6.io/docs/get-started/installation/ or set LOADTEST_K6 to its executable; no substitute load generator was run.

No load was generated. This is a blocked run, not a passing capacity test.


## Commands used

```text
C:\Users\abdom\AppData\Local\Programs\Python\Python313\python.exe scripts/loadtest/run_loadtest.py --duration 60s --vus 1,3,10 --output-dir scripts/loadtest/results
MODE=retrieve VUS=1 DURATION=60s BASE_URL=https://staging.invalid WORKSPACE_ID=11111111-1111-1111-1111-111111111111 k6 run scripts/loadtest/k6-load.js
MODE=retrieve VUS=3 DURATION=60s BASE_URL=https://staging.invalid WORKSPACE_ID=11111111-1111-1111-1111-111111111111 k6 run scripts/loadtest/k6-load.js
MODE=retrieve VUS=10 DURATION=60s BASE_URL=https://staging.invalid WORKSPACE_ID=11111111-1111-1111-1111-111111111111 k6 run scripts/loadtest/k6-load.js
MODE=analyze VUS=1 DURATION=60s BASE_URL=https://staging.invalid WORKSPACE_ID=11111111-1111-1111-1111-111111111111 k6 run scripts/loadtest/k6-load.js
MODE=analyze VUS=3 DURATION=60s BASE_URL=https://staging.invalid WORKSPACE_ID=11111111-1111-1111-1111-111111111111 k6 run scripts/loadtest/k6-load.js
MODE=analyze VUS=10 DURATION=60s BASE_URL=https://staging.invalid WORKSPACE_ID=11111111-1111-1111-1111-111111111111 k6 run scripts/loadtest/k6-load.js
# EVAL_FIREBASE_TOKEN and LOADTEST_COLAB_KEY are supplied only through the environment; values are never recorded.
# A concurrent in-process worker submits real Colab /chunk/jobs and polls them while k6 runs.
```

## Limitations

- This is staging behavior, not a production capacity guarantee.
- p99 with a short run or small request count is sensitive to single outliers.
- Chunk jobs share the same GPU and model service; their effect is included, not isolated.
- 429/503 rates are recorded, not retried away; a rate limiter or dependency outage is a finding.
- Reports contain no question text, case text, generated answer text or token.
