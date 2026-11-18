# PR description: batched embeddings and cached SQL hybrid retrieval

## Summary

Implement bounded, checkpointed embedding generation and labelled binary-vector hybrid retrieval while preserving the separate verified Python pipeline, Workflow lifecycle and privacy boundary. At **10,000 × 768**, the fresh warm LocalDB/BM25 benchmark measures **34.370 ms p95 for production `DoRetrieve`** and **7.714 ms p95 for cached hybrid search**, with stub models. The original EF InMemory path still misses 100 ms (**122.799 ms p95**). These are separate measurements, not a provider switch presented as an optimization. Deployed Colab/Firebase end-to-end and representative clinical-quality validation remain required.

### Changes

- Batch accepted-chunk embeddings at 32 by default; cap `/embed` on both services, retry transient failures with cancellation/deadlines, commit each batch's progress and detach successful chunk entities. Failure/cancellation keeps checkpoints; retry reads only remaining chunks. Add viewer-scoped durable `embedding-progress`.
- Require exact model/dimension metadata, normalize finite nonzero vectors into little-endian float32 `varbinary(max)`, and record `EmbeddingModel` per chunk. Transactional workspace concurrency tokens and SQL `Latin1_General_100_BIN2` model collation prevent mixed spaces, including case-only model changes.
- Archive old JSON verbatim as `LegacyEmbeddingJson`, never search it, reset legacy indexing claims and require re-embedding. Explicit edits/re-embeds clear superseded archives. Downgrade refuses to discard new binary data. Regenerate `db/schema.sql` and verify generated-schema/snapshot consistency.
- Cache immutable workspace vectors/BM25 data with estimated 512 MiB admission, 120-second absolute expiry, fixed lock stripes and commit-aware invalidation. Do not retain scoped contexts, per-workspace lock metadata indefinitely or patient query content.
- Stream SIMD dot-product top-k hits using `System.Numerics.Tensors`; use SQL Full-Text on `Text + Section` when configured, otherwise immutable BM25. Precompute BM25 posting weights and clear pooled score buffers after each request. Reference-sort/formula tests preserve ranking and deterministic ID ties.
- Fuse up to 60 dense and 60 lexical hits with RRF `k=60`, retaining configured candidates (default 30) with an effective cap of 30. Parameterized Full-Text search applies workspace filtering before the limit. Optional DBA setup preserves existing catalogs/keys and repairs a Text-only index.
- Load only shortlisted current source/citation metadata on warm SQL queries; do not select vector/archive columns or trust stale tracked metadata. Rerank at most 30 passages, consistently truncating section-plus-text at 2,000 Unicode code points without truncating returned quotations. Refuse malformed embedding/rerank responses with sanitized errors.
- Follow-up: recheck shortlisted model labels ordinally in .NET (SQL ignores trailing spaces even with BIN2), use a bounded HashSet for shortlist membership and read the rerank character limit once per passage. Add warm-cache current-source/eligibility and SQL model-label regressions.
- Restore `eval/run_eval.py` as a production retrieval benchmark wrapper, update the fake Colab interface, add isolated SQL and optional search-only measurements, and refuse output overwrite in both Python and C#. Preserve the unfinished pipeline-question evaluator separately, with its provenance/authentication limitations explicit.

## Fresh follow-up before / after — 2026-11-15

Baselines were captured **before the follow-up production edits**. Both already contain the earlier batched/binary/cached hybrid implementation. Original `before.json`/`after.json` are untouched.

| Warm metric (10,000 × 768; 15 samples / 3 warmups) | Follow-up before | Follow-up after |
| --- | ---: | ---: |
| EF InMemory `DoRetrieve` p50 | 91.456 ms | 97.630 ms |
| EF InMemory `DoRetrieve` p95 / max | 126.887 ms | 122.799 ms |
| LocalDB `DoRetrieve` p50 | 12.179 ms | 12.108 ms |
| LocalDB `DoRetrieve` p95 / max | 19.110 ms | 34.370 ms |
| LocalDB cached `CandidatesAsync` p50 | 4.941 ms | 4.517 ms |
| LocalDB cached `CandidatesAsync` p95 / max | 6.028 ms | 7.714 ms |
| Vector payload bytes, either provider | 30,720,000 | 30,720,000 |
| Synthetic Recall@8 / candidate Recall@30, either provider | 1.000000 / 1.000000 | 1.000000 / 1.000000 |
| Synthetic MRR@8 / nDCG@8, either provider | 0.791667 / 0.846221 | 0.791667 / 0.846221 |

Artifacts: [InMemory before](../eval/results/scaling-before-2026-11-15.json), [InMemory after](../eval/results/scaling-after-2026-11-15.json), [SQL before](../eval/results/scaling-sql-before-2026-11-15.json), [SQL after](../eval/results/scaling-sql-after-2026-11-15.json). InMemory search-only after: **5.457 ms p50 / 9.056 ms p95**; no before search-only sample was captured for that provider.

The follow-up does **not** establish a latency improvement: InMemory p50 and SQL p95 increased, while ranking stayed identical. LocalDB was already below 100 ms before these small correctness/allocation changes. Do not attribute the InMemory-versus-SQL difference to these edits or omit the slower after samples.

A separate **100-sample / 3-warmup SQL confirmation** uses the same after code/fixture: `DoRetrieve` **10.850 ms p50 / 14.997 ms p95 / 23.004 ms max**; cached search **4.798 / 5.783 / 6.226 ms**. [Confirmation artifact](../eval/results/scaling-sql-confirmation-2026-11-15.json). This supports the local warm target, but does not replace the matched 15-sample comparison, supply a 100-sample before baseline, or measure production concurrency/tails. Quality results are identical.

### Comparable measurement boundaries

- Same fixture SHA-256, seed, 10,000 × 768 corpus, query, topK 8, candidates 30, constant reranker and timed `DoRetrieve` loop as the historical benchmark. No provider-only production bypass, metadata cache or weaker current-evidence checks.
- Release .NET **10.0.12**; sequential warm requests on this Windows development host. Fifteen nearest-rank samples make p95 equal max; they are not a robust production tail/concurrency study.
- SQL runs use `(localdb)\MSSQLLocalDB`, integrated authentication, current migrations and separate GUID-named disposable databases. The benchmark creates/drops only those databases and does not change the application database. It does not install Full-Text: quality/performance here characterize **BM25 fallback**.
- SQL `DoRetrieve` includes actual SQL configuration and shortlisted source/citation queries. Stub embedding/reranking still exclude live Colab/GPU/service-network latency; HTTP routing, cold loads, concurrent load and representative clinical documents are not measured.
- Optional `CandidatesAsync` timing runs **after** the `DoRetrieve` samples with an already validated query vector. It measures cached SIMD + BM25 + RRF only, not embedding, reranking or current source/citation reads, and is labelled separately.
- The restored wrapper defaults to retrieval mode and requires a **new** `--output` path. `--mode pipeline` delegates to `eval/run_pipeline_eval.py`; that unfinished evaluator's placeholder workspace/authentication and question provenance have not been validated. Its question labels/results are not clinical or publication evidence.

Reproduce into new artifact names (never overwrite the archived measurements):

```powershell
python eval/run_eval.py --measure-search --output eval/results/new-inmemory.json
python eval/run_eval.py --provider sqlserver --measure-search --output eval/results/new-sql.json
python eval/run_eval.py --provider sqlserver --measure-search --iterations 100 --output eval/results/new-sql-100.json
```

## Historical implementation before / after

Preserved original artifacts: [`eval/results/before.json`](../eval/results/before.json) and [`eval/results/after.json`](../eval/results/after.json). These compare the original JSON/uncached implementation with the earlier binary/cached hybrid implementation, not the follow-up audit above.

| Metric | Before | After |
| --- | ---: | ---: |
| Warm production `DoRetrieve` p50 | 1,873.831 ms | 88.158 ms |
| Warm production `DoRetrieve` p95 / max | 1,928.226 ms | 115.244 ms |
| Vector payload bytes | 189,331,284 | 30,720,000 |
| Synthetic Recall@8 | 0.500000 | 1.000000 |
| Synthetic MRR@8 | 0.500000 | 0.791667 |
| Synthetic nDCG@8 | 0.500000 | 0.846221 |
| Synthetic candidate Recall@30 | 1.000000 | 1.000000 |
| p95 <100 ms | No | **No** |

Measured p50 improves **21.3×** and p95 **16.7×**. Vector payload is **83.8% smaller** (180.560 → 29.297 MiB). This is not a measurement of total SQL database/disk size: archived JSON, labels, cache/postings, row/index/log/backups and physical allocation are excluded.

### Historical method and ranking tradeoff

- The original run used the same harness and fixture on both implementations. The entrypoint/harness have since been repaired and extended as described above; the fixture remains unchanged. SHA-256: `e8410b5d6cdaa20561ebb596377d7e03f8b31cdce311c298890cd16f7ff7df77`.
- .NET 10.0.12, **10,000 chunks × 768 dimensions**, **15 iterations / 3 warmups**, fixed seed/vectors, topK 8, candidates 30 and minRerank 0 for the evaluation. With 15 samples, the reported nearest-rank p95 is also the maximum; this is not a robust production tail-latency study.
- Exercises the real `AnalysisController.DoRetrieve` with EF InMemory and stub embedding/constant reranking. It excludes SQL execution, network and GPU latency, cold index construction and concurrent load. Lexical quality numbers characterize **BM25 fallback**, not live Full-Text quality.
- All six exact-term cases move from outside the top eight to first place. Five semantic cases move from first to second; the sixth stays first. This is a deliberate synthetic lexical stress fixture, **not uniformly improved semantic ranking or clinical-quality evidence**.
- Profiling found the current metadata query dominated by EF InMemory's deep row snapshots (roughly 33 MiB per warm query despite the narrow returned projection). Streaming top-k/precomputed BM25 reduced corpus-sized search allocations without changing ranks. No provider-only bypass, weaker snapshot isolation or fixture/cap change was used to obtain the reported result. This diagnosis does not satisfy the outstanding latency target or replace a representative SQL benchmark.

## Preserved boundaries

- Python still owns planning, retrieval orchestration, drafting and deterministic quotation/number/wording/scope/topic verification. ASP.NET retrieves exactly its focused query; no LLM decomposition, legacy Mongo retrieval or unverified `answer` replaces the web pipeline.
- Owner-only configuration publication still requires the latest real persisted workspace/config ID/version/hash-bound test evidence. `NoConfigTestRunProvider` remains registered; the partially implemented `/tests/run` harness is not integrated as trusted publication evidence and its two existing regressions still fail. **These offline results are not Prompt 11 evidence and do not authorize publication or step completion.**
- The source-only cache contains no patient query/history/fact data. Bearer tokens stay on the ASP.NET callback boundary; service keys remain server-side. `PipelineResultPolicy`, evidence-only case storage, audit-only defaults for cases, privacy-safe error logging and original source quotation/citation handling remain intact.

## Validation

### Current follow-up checks

| Check | Result |
| --- | --- |
| Full backend xUnit suite | **652 passed / 2 existing failures / 654 total**, no skipped tests |
| Focused retrieval/cache/embedding/RRF/search/SQL tests | **104 passed** |
| SQL retrieval on Full-Text-enabled SQL Server, live Full-Text required | **8 passed** on `localhost\MSSQLSERVER2025`, separate disposable databases |
| Full Python suite | **358 passed**, including **38 Colab contract + 11 evaluation-wrapper tests** |
| Backend Debug build / Release evaluation build | Passed; existing dependency/runner/nullability warnings remain |
| Fresh evaluations | Before/after for InMemory and LocalDB, plus 100-sample SQL confirmation; all artifacts retained |
| Frontend | No frontend code changed or checks rerun in this retrieval follow-up; preceding management validation: **501 Vitest + 15 Node**, TypeScript/build/changed-file lint passed |

The unchanged backend failures are `TestHarnessTests.Comparer_detects_missing_and_unexpected_problems` and `TestHarnessTests.Failing_expectation_makes_run_fail_with_diff`. They were present before this audit; no harness/publication fixes or clinical evaluation claims are included here.

New regressions prove that a warm shortlist excludes externally edited/vector-cleared, deleted and rejected chunks while reading current citation metadata, and that a trailing-space model label is excluded before reranking despite SQL's equality semantics (then refused on cache reload). Evaluation-wrapper tests cover arguments, failure propagation, separate pipeline mode and baseline overwrite refusal.

### Historical implementation checks

The original validation is retained for historical context, not represented as a clean result for the current checkout.

| Check | Result |
| --- | --- |
| Full backend xUnit suite | **572 passed**, 0 failed/skipped |
| Full Python suite | **347 passed** |
| Colab cap/output/Unicode/cancellation subset | **38 passed**, included in the Python total |
| Frontend UI / utility suites | **452 Vitest + 14 Node tests passed** |
| TypeScript | Passed |
| Backend Debug / Release builds | Passed |
| Frontend production build | Passed; existing native SWC/WASM fallback warnings |
| SQL retrieval regressions | **7 passed** on LocalDB and separately on a Full-Text-enabled SQL Server with live Full-Text required |
| Full frontend lint | **Not clean:** 3 existing errors and 7 warnings outside this work |

Backend regressions cover transaction commit/rollback/disposal cache invalidation, concurrent cold loads/publication, memory admission, review/edit/delete invalidation, strict model/dimension refusal, bounded batches/retries/checkpoints/tracking, RRF contributions/ties, SIMD top-k versus a full sort, BM25 versus the original formula, pooled-buffer isolation/cancellation, archival/binary round-trip/downgrade safety and migration/schema consistency. SQL tests exercise tenant isolation, parameterization, current source metadata, warm corpus loading, unavailable-index fallback, case-sensitive binding, Full-Text installation refusal, idempotence and Text-only repair without catalog replacement.

Commands from the repository root (build the test project before using `--no-build`):

```powershell
dotnet test backend.Tests/ClinicalRag.Tests.csproj --no-restore --no-build --verbosity minimal
python -m pytest tests -q
python -m pytest tests/test_colab_server.py -q
dotnet build backend/ClinicalRag.Api.csproj --no-restore --configuration Debug --verbosity minimal
dotnet build backend/ClinicalRag.Api.csproj --no-restore --configuration Release --verbosity minimal
python eval/run_eval.py --measure-search --output eval/results/new-measurement.json
```

Required live Full-Text coverage, using GUID-named disposable test databases rather than the application database:

```powershell
$env:CLINICALRAG_TEST_SQLSERVER = 'localhost\MSSQLSERVER2025'
$env:CLINICALRAG_TEST_REQUIRE_FULLTEXT = '1'
dotnet test backend.Tests/ClinicalRag.Tests.csproj --no-restore --no-build --filter "FullyQualifiedName~SqlRetrievalTests" --verbosity minimal
```

From `frontend/`: `npm test`, `npx tsc --noEmit --incremental false`, `npm run build`, `npm run lint`. Remaining lint errors are `src/app/(auth)/verify-email/page.tsx:24` and `src/components/AuthForm.tsx:47,70`. Existing NuGet vulnerability advisories, xUnit runner resolution/nullability warnings and Node/jsdom warnings are not resolved by this retrieval change.

## Rollout and remaining acceptance gaps

Follow the [deployment runbook](retrieval-deployment.md): backup/drain, reviewed binary migration, matching backend/Colab deployment, real re-embedding and optional Full-Text population checks. The cache now checks a durable per-workspace `SearchGeneration`; the SQL Server chunk trigger advances it for other API instances and direct SQL writers, so those changes reload on the next retrieval instead of waiting for expiry. Its budget bounds estimated retained entries, not total process/cold-load memory. Full-Text language, stoplist and eventual population can differ from BM25 and need corpus-specific evaluation.

- [x] Bounded embeddings, durable progress, labelled float32 binary storage and mixed-model refusal.
- [x] Cached SIMD + Full-Text/BM25 + RRF retrieval, current shortlisted metadata and shared rerank caps.
- [x] Cache/model/RRF/migration/SQL regressions and preserved original before/after evaluation artifacts.
- [x] **Warm local SQL/BM25 synthetic 10,000-chunk p95 <100 ms**, including current metadata reads with stub models: 34.370 ms; cached search-only: 7.714 ms.
- [ ] **Original EF InMemory `DoRetrieve` p95 <100 ms:** still 122.799 ms. No stale-metadata/provider-only shortcut was used to hide it.
- [ ] Representative deployed cold/warm/concurrent latency, including real Colab/GPU/network costs; local synthetic measurements do not satisfy this rollout requirement.
- [ ] Live Colab GPU + Firebase + Python/ASP.NET end-to-end validation and representative labelled clinical-quality evaluation.
- [ ] Trusted Prompt 11 publication evidence: intentionally unavailable, not implemented or simulated in this work.

This is a PR-ready description only. No commit, push or PR was created; this checkout has no Git metadata.
