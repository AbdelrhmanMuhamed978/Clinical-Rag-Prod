# Clinical RAG Platform

## Data flow
1. Firebase authenticates the user. ASP.NET validates the bearer token and authorizes workspace membership. Running analysis requires editor access; viewing retrieval and one's own saved runs requires viewer access.
2. Documents are uploaded and parsed through ASP.NET → secured Colab `/parse-pdf`. Manual chunking submits Colab `/chunk/jobs` and polls completion. ASP.NET saves chunks/page progress; accepted chunks are embedded using bounded Colab `/embed` batches with durable per-batch progress.
3. For analysis, ASP.NET takes the published configuration and current owner-approved database rules, writes audit metadata and calls the separate Python FastAPI service's `/analyze` or `/analyze/stream`. It forwards the user's bearer token plus `X-Pipeline-Key`.
4. Python constructs a request-scoped `Config`, extracts case facts/evaluates supplied rules, plans focused guideline questions and orchestrates retrieval. It calls only `/api/workspaces/{workspaceId}/analyze/retrieve` on the configured ASP.NET origin, using the bearer token without model/service keys.
5. ASP.NET embeds the focused query, searches accepted/embedded binary-indexed chunks in that workspace using cached SIMD dense search plus SQL Full-Text/BM25, fuses with RRF and calls Colab `/rerank` for at most 30 passages. There is no ASP.NET LLM query decomposition. Colab's legacy Mongo `/retrieve` is not part of this web path.
6. Python drafts through Colab `/generate` with `X-Colab-Key` only, rejects truncated output and runs deterministic quote/number/wording/scope/topic verification. It returns the pipeline schema, not a free-text `answer`.
7. ASP.NET validates/projects the response through `PipelineResultPolicy`, optionally stores its privacy-safe projection and relays JSON or real progress/result/error SSE. The frontend validates again and renders only verified recommendations as answers; excluded drafts expose reasons only.

Python runs independently of Colab. The frontend never receives the pipeline/Colab keys. See [startup and configuration](../README.md#configuration-and-startup) for matching secrets, tunnel URLs and reverse-proxy retrieval origins.

## Embedding and search index

- `DocumentEmbeddingService` claims a revision-bound document operation and reads at most one configured batch (default/hard maximum 32). `EmbeddingBatchClient` retries transient failures with attempt deadlines. Each successful batch commits vector bytes, exact model/dimension binding and progress together, then releases chunk tracking. Failure/cancellation keeps committed checkpoints and retries only unfinished chunks.
- `EmbeddingVectors` validates the response model, declared dimensions, finite float32 values and nonzero norm. SQL stores little-endian unit vectors in `Chunks.Embedding` (`varbinary(max)`), with `EmbeddingModel` on every vector. Workspace model/dimension concurrency tokens serialize competing encoders; SQL model identities use `Latin1_General_100_BIN2` collation.
- The binary migration preserves legacy JSON verbatim in `LegacyEmbeddingJson`, never treats UTF-16 JSON bytes as a vector, and requires real re-embedding. Malformed binary rows cannot become evidence; mixed/unlabelled model identities or valid dimension mismatches are refused rather than silently mixed.
- `ChunkVectorCache` is a singleton containing immutable guideline-only workspace snapshots, not scoped database contexts or patient queries. Defaults are 512 MiB estimated retained data and 120-second absolute expiry. Fixed lock stripes single-flight cold loads and make publication/invalidation atomic. Database transaction hooks invalidate only after commit and discard rollback/disposal invalidations.
- `ChunkSearchIndex` holds contiguous float32 vectors and streams top-k dot-product hits through `System.Numerics.Tensors`. `Bm25Index` indexes normalized `Text + Section`, precomputes immutable posting weights and uses cleared, per-request pooled score buffers. Both preserve score-descending, chunk-ID-ascending tie ordering.
- Full-Text capability requires the SQL component, an enabled chunk index keyed by `Id`, and both `Text`/`Section` columns. Its parameterized query applies workspace/review/model filtering before the limit. Expected unavailable-index errors fall back to BM25 and invalidate capability; unexpected database errors remain sanitized failures. Installation/population is a separate DBA operation, not a migration.
- Each search leg contributes up to 60 hits to RRF (`k=60`); the fused shortlist retains `min(configured candidates, 30)` (default 30). A fresh, untracked SQL projection rechecks eligible IDs/model/dimensions and reads current quotation/citation metadata without returning vector/archive columns. At most 30 rerank passages are truncated to 2,000 Unicode code points, including the section; original source text is preserved for Python verification.
- The cache is process-local: other instances/direct SQL writers require expiry or explicit coordinated refresh. Fresh metadata checks prevent serving removed/ineligible shortlisted rows, but do not guarantee immediately current recall across processes. The cache budget is not a bound on concurrent cold-load or total process memory.

The offline evaluation exercises this production retrieval path with fixed vectors and constant reranking. At 10,000 × 768, warm LocalDB/BM25 `DoRetrieve` measures **34.370 ms p95**, including SQL metadata reads; cached search alone measures **7.714 ms p95**. The original EF InMemory path remains above 100 ms (**122.799 ms p95**). These are separate provider measurements, not a claimed follow-up speedup. Deployed model/network/concurrency latency remains unverified, and these results provide neither clinical-quality nor publication evidence. See [deployment guidance](retrieval-deployment.md) and the [PR-ready report](hybrid-retrieval-pr.md).

## Configuration and privacy boundaries

- Published configuration is read per workspace (new workspaces bootstrap a default published version); drafts do not serve live runs. Python never mutates global workspace settings.
- Each request supplies its own approved rule list. The web pipeline does not load local `rules.json` as a fallback. Owners approve rules; edits invalidate review and editors cannot self-approve.
- `AnalysisRun` stores `UserId`, `WorkspaceId`, `ConfigVersion`, SHA-256 `InputHash` and timestamps, not the case text, extracted facts, patient summary, conversation ID or history.
- `Privacy:StoreResults` unset means short questions store results and cases (at least 40 words), including follow-ups retaining a case turn, do not. Explicit false disables storage; true opts in to evidence-only case projection; invalid values fail closed.
- Case storage replaces planner titles and generated patient-specific recommendations, removes queries/checks/draft text/patient-derived warnings, and retains guideline evidence, citation metadata and verifier issues. Live patient checks are transient cautions, not evidence.
- Saved-analysis list/open/delete predicates include both user and workspace. Legacy free-text content is withheld rather than automatically removed. Saved results do not become conversation turns.

## Conversation and streaming lifecycle

- The browser keeps at most six question/verified-summary turns in tab-local React state. Summaries exclude drafts, planner labels and patient checks. History only resolves references for retrieval/drafting; fresh guideline sources must still pass verification.
- Clear history cancels work and creates a new conversation identity. Workspace/user changes remount the Playground. Cancellation, replacement and unmount invalidate late callbacks as well as aborting transport.
- API requests are bounded to 1 MiB; service/transport deadlines cover SSE body reads. Disconnects cancel Python work. SSE buffering is disabled and errors omit sensitive bodies/prompts. Infrastructure must also disable sensitive logging/tunnel retention.

## Safety locks
- Quote, number, support, scope, negation, strength and topic checks stay in Python code and cannot be disabled by workspace prompts/configuration.
- Schema projection is fail-closed: missing verification metadata, contradictory status and free-text answers are rejected. Unknown private fields do not pass the contract boundary.
- Citation grade, guideline, page and DRAFT status come from retrieved sources, never the model. Saved DRAFT warnings and “not covered” wording remain explicit.
- “Not covered” is lack of a verifiable statement in the loaded guidelines, not evidence that a topic is safe or unimportant. This remains decision support requiring clinical review.
