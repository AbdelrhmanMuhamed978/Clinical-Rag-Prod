# Synthetic retrieval regression baseline

`synthetic-retrieval.v1.json` is the stored baseline for the offline retrieval
harness in `eval/Program.cs`. It was produced by an actual run of:

```powershell
python eval/run_eval.py --output eval/results/synthetic-retrieval-2026-11-16.json
```

The harness uses the fixed-vector synthetic fixture in
`eval/retrieval-fixture.json`, stub embeddings/reranking, and EF InMemory. It is
a deterministic regression check. It does **not** measure real encoders,
rerankers, GPUs, network latency, clinical quality or publication evidence.

## Gate

`eval/compare_baseline.py` compares a new artifact to this baseline and fails
when a gated quality metric drops by more than the absolute tolerance in
`synthetic-retrieval.tolerances.json`, when the fixture SHA-256 changes, when the
database provider changes, or when the query count changes. CI runs this gate in
the `eval-gate` job.

```powershell
python eval/run_eval.py --output eval/results/ci-retrieval.json
python eval/compare_baseline.py `
  --baseline eval/baselines/synthetic-retrieval.v1.json `
  --current eval/results/ci-retrieval.json `
  --tolerances eval/baselines/synthetic-retrieval.tolerances.json
```

Run output paths must be new. The harness and the comparison report writer both
refuse to overwrite existing files.

## Re-baselining

Do not silently update this file to make a red gate green. A deliberate
re-baseline must:

1. Explain why the fixture, provider or expected behavior changed.
2. Generate a fresh artifact with the exact command above.
3. Get independent review of the artifact and the changed tolerances.
4. Store the old baseline under a new version name; never overwrite history.

The clinical evaluation runner is separate and does not use this baseline. A
clinical run against real models must write its own dated result and must not be
compared to these synthetic metrics as if they were the same measurement.
