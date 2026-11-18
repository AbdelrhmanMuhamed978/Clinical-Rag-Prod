# Deployed-scale staging load test

This harness measures the **real staging stack**, not mocks:

- real ASP.NET backend and workspace retrieval
- real verified Python pipeline service
- real Colab model, encoder and reranker
- real Colab chunk jobs running concurrently with retrieval/analysis
- live GPU memory samples

It uses [k6](https://k6.io/) for request generation and `run_loadtest.py` for
preflight, concurrent chunk work, GPU sampling, thresholds and dated reports.

## Why k6 plus an orchestrator

k6 provides stable p50/p95/p99 summaries and status counters. The Python
orchestrator runs each mode separately so “3 concurrent users” means three
users in that measured path, while a real Colab chunk-job worker runs alongside.
It records GPU memory and writes the report so a missing prerequisite or failed
threshold is documented instead of hidden.

## Prerequisites

1. A staging backend, pipeline service and real Colab runtime are running.
2. The staging workspace contains indexed, reviewed guideline chunks.
3. A verified-user Firebase ID token is available. The user must have editor
   (or owner) access to the workspace for `/analyze`, and must be able to read
   `/api/operations/colab` so model/GPU evidence can be recorded.
4. k6 is installed and on `PATH`, or set `LOADTEST_K6` to its executable.
5. A non-identifying, clinician-approved guideline question is available for
   the end-to-end analyze path. Do not use patient text.
6. For concurrent chunk jobs: the Colab base URL and `X-Colab-Key` value.

## Environment

Never put credentials in a command line, report or shell history.

| Variable | Required | Purpose |
| --- | --- | --- |
| `LOADTEST_BASE_URL` | yes | Staging backend origin, e.g. `https://api.staging.example`. |
| `LOADTEST_WORKSPACE_ID` | yes | Workspace UUID under test. |
| `EVAL_FIREBASE_TOKEN` or `LOADTEST_FIREBASE_TOKEN_FILE` | yes | Verified-user bearer token. Prefer the file. |
| `LOADTEST_QUESTION` | yes | Non-identifying clinician-approved question for analyze. |
| `LOADTEST_QUERY` | no | Retrieval query; defaults to `LOADTEST_QUESTION`. |
| `LOADTEST_DURATION` | no | Per-run k6 duration, default `2m`. |
| `LOADTEST_VUS` | no | Comma-separated levels, default `1,3,10`. |
| `LOADTEST_COLAB_URL` | with chunk jobs | Real Colab origin. |
| `LOADTEST_COLAB_KEY` | with chunk jobs | Colab API key (`X-Colab-Key`). |
| `LOADTEST_CHUNK_INTERVAL_SECONDS` | no | Delay between chunk jobs, default `15`. |
| `LOADTEST_GPU_POLL_SECONDS` | no | GPU sample interval, default `2`. |
| `LOADTEST_MAX_ERROR_RATE` | no | Default `0.01`. |
| `LOADTEST_MAX_429_RATE` | no | Default `0`; a rate limit is a finding. |
| `LOADTEST_MAX_503_RATE` | no | Default `0`; a dependency outage is a finding. |
| `LOADTEST_MAX_CHUNK_FAILURE_RATE` | no | Default `0`. |
| `LOADTEST_MAX_RETRIEVE_P95_MS` / `P99_MS` | no | Optional retrieve latency thresholds. |
| `LOADTEST_MAX_ANALYZE_P95_MS` / `P99_MS` | no | Optional analyze latency thresholds. |

If GPU memory is unavailable, the run is **blocked** unless
`--allow-missing-gpu-memory` is explicitly set; the report then states that GPU
memory was not recorded.

## Run

```powershell
$env:LOADTEST_BASE_URL = "https://api.staging.example"
$env:LOADTEST_WORKSPACE_ID = "<workspace UUID>"
$env:LOADTEST_FIREBASE_TOKEN_FILE = "C:\secure\eval-token.txt"
$env:LOADTEST_QUESTION = "<non-identifying clinician-approved question>"
$env:LOADTEST_COLAB_URL = "https://<colab-tunnel>"
$env:LOADTEST_COLAB_KEY = "<staging Colab key>"

python scripts/loadtest/run_loadtest.py `
  --duration 2m `
  --vus 1,3,10 `
  --output-dir scripts/loadtest/results
```

The orchestrator:

1. Preflights `/health`, `/api/operations/colab`, the published configuration,
   a real retrieval request, a real end-to-end analyze request, and the real
   Colab readiness endpoint.
2. Starts a GPU sampler and a concurrent real chunk-job worker.
3. Runs k6 `retrieve` at 1, 3 and 10 VUs, then `analyze` at 1, 3 and 10 VUs.
4. Stops the workers and writes a new timestamped directory with:
   - `report.md`
   - `summary.json`
   - `commands.txt`
   - `gpu-samples.json`
   - `chunk-jobs.json`
   - `k6-<mode>-<vus>vu.json` and `.log`
5. Exits `0` on pass, `1` on a threshold failure, `4` on a blocked run.

Existing result directories are never overwritten.

## What is recorded

- k6 request count, error rate, 429 count/rate, 503 count/rate and other error
  counts for each mode/concurrency. The overall error rate includes 429/503;
  the separate counters show how much of it came from each source.
- k6 p50/p95/p99 and max latency in milliseconds.
- Concurrent chunk-job submissions, completions, failures, 429/503 counts and
  p50/p95 job duration.
- GPU total, used, allocated and peak allocated memory samples. The report
  includes the baseline, maximum used and maximum peak.
- The command lines used, with tokens replaced by environment variable names.
- Question and query SHA-256 fingerprints only; the question text is not stored.

## Interpreting the report

- **BLOCKED** means no load was generated. The reason and preflight evidence are
  in the report. This is not a passing test.
- **THRESHOLD FAILURE** means the load ran but at least one configured threshold
  failed. The report lists each check, limit, actual value and result.
- 429/503 responses are counted, not retried away. A rate limiter or dependency
  outage is a finding, not noise.
- p99 on a short run is sensitive to one outlier; increase duration only with a
  deliberate plan and keep the run archived.
- Concurrent chunk jobs share the same GPU and model service. Their contention
  is included, not isolated.
- Staging results are not a production capacity guarantee and not clinical
  evidence.

## Local dry run

If k6, staging credentials or a real Colab runtime are unavailable, the
orchestrator writes a dated **BLOCKED** report with the missing prerequisite.
That is the honest result for an unavailable stack; do not substitute mocks or
invent numbers.
