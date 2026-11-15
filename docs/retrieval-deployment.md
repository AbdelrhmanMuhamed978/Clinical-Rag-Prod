# Embedding and hybrid retrieval deployment

This upgrade changes vector storage and retrieval, not the verified Python pipeline or Workflow publication rules. Use only de-identified cases. At 10,000 × 768, warm LocalDB/BM25 `DoRetrieve` measures **34.370 ms p95** with stub models; the original EF InMemory path remains **122.799 ms**. Cached search is below 100 ms locally, but **deployed model/network/concurrency latency and live clinical validation are not complete**. See [measured results](hybrid-retrieval-pr.md).

## Upgrade an existing deployment

1. **Back up and drain old API instances.** Pause embedding/review writes and take a recoverable database backup. Old code expects JSON in `Chunks.Embedding` and must not run against the new binary schema. Review the generated `db/schema.sql`; use a migration/DBA principal, not the normal application account. Set `Database__AutoMigrate=false` for production.
2. Apply the reviewed idempotent schema script. `20261115104518_BinaryChunkEmbeddings`:
   - Renames the old column to `LegacyEmbeddingJson`, retaining null, empty, malformed and valid JSON verbatim. It does **not** cast or search that archive.
   - Adds normalized float32 `varbinary(max)` storage, workspace model/dimension binding and document embedding progress/check constraints. Model columns use case-sensitive `Latin1_General_100_BIN2` collation.
   - Changes old `embedded` chunks to `accepted`, moves `indexed` documents back to review and releases interrupted embedding claims as failed operations. Source/review metadata is retained; indexing must be earned again by real embeddings.
3. Deploy the matching backend and Colab files, including `config/retrieval-limits.json`. ASP.NET embeds that file as a resource; Colab reads it on import. Changing it requires rebuilding/redeploying ASP.NET and restarting Colab, not editing a published Workflow configuration.
4. Start the secured Colab server with matching `X-Colab-Key` credentials and the current tunnel URL. `/embed` must return `model`, exact `dimensions`, and one finite, nonzero vector per input. `/health` alone is not proof that embedding/reranking works. Validate a synthetic request with the actual encoder before resuming ingestion.
5. Re-embed accepted guideline chunks through the existing editor-authorized endpoint below. A committed explicit re-embed clears that chunk's superseded archive. Do not delete archives in bulk or relabel old bytes to bypass model checks. Other archives/backups and physical database allocation are outside the reported vector-payload savings.
6. Optionally configure Full-Text as described below. Confirm population with representative workspace-scoped exact-term queries before depending on its lexical results. Restart/refresh every API process, or wait for cache expiry, after installing/changing an index that was previously detected as unavailable.
7. Validate authorized retrieval, edit/delete invalidation and verified analysis on the deployed SQL/GPU/Firebase stack. Unit tests and synthetic evaluation are not a substitute for this rollout check.

Generate, review and apply migrations separately from application startup:

```powershell
dotnet ef migrations script --project backend/ClinicalRag.Api.csproj --startup-project backend/ClinicalRag.Api.csproj --idempotent -o db/schema.sql
```

**Rollback:** automatic downgrade refuses with SQL error 51000 while any new binary vector exists. Export/review new vectors and follow an approved restore/migration plan; do not remove the guard. If no binary vectors exist, downgrade restores the archived JSON column but does not manufacture `embedded`/`indexed` state.

## Batches, progress and model changes

Firebase bearer authentication and workspace authorization apply to both routes:

| Method | Route | Access |
| --- | --- | --- |
| POST | `/api/workspaces/{workspaceId}/documents/{id}/generate-embeddings` | Editor/owner; no request body |
| GET | `/api/workspaces/{workspaceId}/documents/{id}/embedding-progress` | Viewer or higher; current durable checkpoint |

Progress includes `totalChunks`, `completedChunks`, `batchSize`, `status`, `progressPercent` and a sanitized failure reason, not chunk text or vector data. Counts describe the **current invocation**: retry resets its total to the remaining pending chunks, rather than redoing previous successful batches. The POST response's `embedded` count is also that invocation's committed count. A failed/cancelled request may have committed successful batches; refresh progress and retry only after the operation is released.

The document revision claim prevents concurrent review/delete/embedding writes from overriding a newer owner. Keep the automatic parsing worker enabled (`Documents__AutomaticParsing`, default true) for stale-operation recovery: it marks abandoned operations failed after at least 660 seconds without a checkpoint, or `2 × Colab:TimeoutSeconds + 60` if larger. It preserves completed embedding batches but does not automatically embed the remainder. Keep an attempt/retry budget below the recovery window, or coordinate the service timeout; do not force client-written document status.

The backend validates nonempty, trimmed, control-free model identities of at most 256 characters, declared dimensions of 1–16,384 and finite float32 values. It normalizes vectors and rejects zero norms. Every persistence batch checks the workspace's existing labelled binary vectors inside the same transaction as its binding/progress write. The cached index also refuses incompatible query models/dimensions. Shortlisted current model labels are rechecked ordinally in .NET because SQL equality ignores trailing spaces even with BIN2 collation. A model mismatch returns 409; malformed/upstream retrieval failures return sanitized 502 responses.

To change encoders, invalidate **all active accepted/embedded workspace vectors** using the normal review/reset workflow before re-embedding with one model. A real review-status transition or text/section edit clears the old vector; an exact no-op review does not. Reset the entire active workspace before starting batches from the new encoder, not one document at a time while other documents still use the old space. Neither case-only renaming nor manually changing `EmbeddingModel` is a conversion. The label is a contract, not a hash of model weights: pin deployed artifacts/revisions and assign a distinct identity when the embedding space changes.

## Resource settings

Set these in the ASP.NET process environment; `.env.example` is a reference, not an automatic loader:

| Environment variable | Default | Effective range |
| --- | ---: | --- |
| `Embedding__BatchSize` | 32 | 1–32 |
| `Embedding__MaxRetries` | 2 | 0–5 retries after the initial attempt |
| `Embedding__RetryBaseMilliseconds` | 250 | 0–5,000; exponential delay capped at 10,000 ms |
| `Embedding__TimeoutSeconds` | 120 | 1–300 per attempt; coordinate the HTTP/worker timeout |
| `Retrieval__CacheSizeMegabytes` | 512 | 16–4,096 MiB estimated retained index data |
| `Retrieval__CacheSeconds` | 120 | 1–3,600 seconds, absolute expiry |

Only transient network/HTTP 408, 429, 5xx and attempt timeouts retry. Invalid embedding output, authentication errors, oversized requests and caller cancellation do not retry as transient failures. Successful chunk entities are detached after each commit; neither requests nor EF tracking grow with the document's full chunk count.

Workflow still permits candidates 5–100 (default 30); the effective rerank shortlist is capped at 30. `topK` remains 1–20 (default 8), with the configured `minRerank` threshold (default 0.30). Each dense/lexical leg retains up to 60 hits; RRF uses one-based ranks and `k=60`, not raw cosine/BM25/Full-Text score addition.

The shared hard caps are 32 embedding texts, 16,384 dimensions, 30 rerank passages and 2,000 Unicode code points per passage. Rerank text is `Section + newline + Text` when a section exists; the budget includes the section. Both services truncate consistently, while returned evidence retains the full original text. Colab's optional `COLAB_MAX_EMBED_TEXTS` and `COLAB_MAX_RERANK_PASSAGES` can **lower**, never raise, the limits. Keep lower caps compatible with the backend batch size and published effective candidate setting; otherwise Colab rejects the request. Workflow's minimum candidate setting is 5, so a Colab rerank cap below 5 cannot be matched for general retrieval through that setting. Publication remains gated on real bound test evidence, so these caps do not authorize a bypass of the publication lifecycle.

## Optional SQL Server Full-Text

Full-Text Search must be installed in the SQL Server instance; **LocalDB uses BM25**. Run reviewed `db/enable-chunk-fulltext.sql` in the target database, outside an explicit transaction, using an authorized DBA principal. Normal schema migrations do not install a catalog/index.

- A new index uses a single-column unique `Chunks.Id` key, `Text` and `Section`, English language 1033, automatic change tracking and no stoplist. A newly created `ClinicalRagChunks` catalog is accent-sensitive; a pre-existing catalog keeps its own setting. Review language, accent sensitivity and stoplist choices for the actual corpus.
- An existing index/catalog is not dropped or replaced. The script verifies the key, adds missing `Text`/`Section`, enables the index and sets automatic change tracking; it preserves its existing catalog and stoplist. Review those alterations with the DBA.
- Missing components/schema/key or an incompatible existing key cause explicit installation refusal. A Text-only index uses BM25 until Section is added and the API's capability cache is refreshed.
- Population is asynchronous. An enabled index and an installed component do **not** prove that rows are searchable; automatic updates can also lag. Test actual term-to-chunk matches with the application principal. Dense search continues during lexical lag, but exact-term recall is not guaranteed.
- Retrieval parameterizes queries/model/workspace/limits and filters workspace/review/model eligibility **before** taking lexical candidates. There is no global `top_n_by_rank` that lets another tenant crowd out this workspace.
- Known unavailable-index failures trigger BM25 and capability invalidation. Connection, permission and unexpected SQL failures are not silently swallowed; investigate the sanitized 502 without enabling body/prompt logging.

## Cache operations and scale-out

The singleton cache holds immutable guideline vectors and a BM25 inverted index, never patient queries, bearer/service keys or a scoped `AppDbContext`. Dot products stream into a bounded top-k selector; BM25's immutable posting weights are precomputed and per-query pooled score buffers are cleared before reuse.

Application saves invalidate affected workspace entries on embed, accept/reject, text/section edits and delete, plus document/workspace changes. Explicit transactions defer invalidation until commit; rollback/disposal cannot publish stale invalidations. Cold loads use fixed lock stripes, and a commit racing a load forces a reload before publication. Entries above the configured budget are refused rather than retained without a bound.

The budget limits **estimated retained cache data**, not total process memory, transient query allocations, decoded/contiguous cold-load copies or simultaneous cold loads. Size each API worker for those peaks. The 10,000 × 768 float32 vector payload alone is 30,720,000 bytes (29.297 MiB), before IDs, lexical postings, text and runtime/database overhead.

There is **no distributed invalidation**. Changes in another API process or direct SQL can leave this process's ranking snapshot stale until expiry/refresh. The final untracked SQL projection rechecks current eligible IDs/model/dimensions and reads current citation/source metadata without returning vectors/archives, but new matches and current ranking are not guaranteed immediately. Coordinate refreshes after bulk maintenance/model changes; multi-instance strong-consistency/recall requirements need a separately designed shared generation/invalidation mechanism.

## Operational acceptance

- Run the regression commands in the [validation report](hybrid-retrieval-pr.md#validation), including a Full-Text-required SQL run if deploying that path.
- Preserve `eval/results/before.json`, `after.json` and subsequent dated artifacts. Run `python eval/run_eval.py --measure-search --output eval/results/new-measurement.json`; the wrapper and harness refuse existing output paths. Do not overwrite baselines or alter the fixture/candidate caps to make the target pass.
- To include SQL execution, add `--provider sqlserver`. This creates/migrates/drops GUID-named **evaluation databases**, never the application database. Default server: `(localdb)\MSSQLLocalDB`; optionally select a test server via `CLINICALRAG_TEST_SQLSERVER`. Integrated authentication requires permission to create/drop these isolated databases. No Full-Text setup is performed by the benchmark, so it measures BM25 fallback.
- The unrelated unfinished question evaluator is preserved as `eval/run_pipeline_eval.py` (`eval/run_eval.py --mode pipeline ...`). Its source provenance and placeholder authentication/workspace setup are not established. It must not be treated as clinical-quality or publication evidence.
- Measure cold/warm retrieval on the actual deployment at the intended corpus size and concurrency, including SQL, GPU and network latency. Warm synthetic LocalDB/BM25 is below 100 ms; EF InMemory remains above it. Neither measures deployed model/network costs or concurrent load.
- Evaluate a representative, labelled clinical corpus with real encoders/reranking. The fixed-vector/constant-reranker evaluation characterizes regressions only, including lexical-versus-semantic ranking tradeoffs.
- Keep pipeline/Colab keys server-side, user bearer tokens off Colab, sensitive logging/tunnel retention disabled, and `PipelineResultPolicy`/the mandatory Python verifier intact. Owner-only publishing remains unavailable until Prompt 11 supplies trusted persisted evidence; this evaluation cannot complete Workflow steps or publish a draft.
