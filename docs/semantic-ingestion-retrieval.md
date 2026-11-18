# Semantic ingestion and retrieval

**Status: implemented and offline-verified. Not clinical validation.**

This change adds four production-oriented improvements to the guideline pipeline:

1. structure-aware ingestion with atomic recommendation metadata;
2. hybrid dense/BM25 search with deterministic query expansion and section boosts;
3. cross-encoder reranking followed by intent alignment;
4. hyphenation/whitespace-tolerant quote verification.

Existing chunks can still be searched through deterministic fallbacks, but the
new metadata is complete only after the affected documents are re-ingested.
There is no automatic backfill because chunk text and structure must come from
the source document, not from an inference over an old chunk.

## 1. Structure-aware ingestion

`python-backend/chunker.py` now emits one deterministic metadata payload per
chunk:

| Field | Meaning |
| --- | --- |
| `kind` | `recommendation`, `definition`, `table_row`, `clinical_consideration` or `summary`. |
| `title` | Section/recommendation title, or a deterministic fallback. |
| `year` | First source year in the atomic object, if present. |
| `body` | The verbatim chunk text. |
| `table` | Whether the object came from a markdown table. |
| `strength` / `certainty` | Grade derived from the same atomic object. |
| `population` | Narrow population marker when present (for example TB, pregnancy). |
| `recommendation_id` | Stable hash of page/title/body for recommendations, definitions, summaries and graded table rows. |
| `section_path`, `status`, `topic` | LLM-assisted context, bounded; never authoritative for grade. |

Key safeguards:

- A block containing multiple graded recommendations is split at the next
  graded sentence, so one recommendation cannot inherit another's grade.
- A graded recommendation **table** is split into one object per row. Each
  row keeps the exact header and separator, the row text and its own grade.
  Ungraded tables remain one atomic `table_row` object.
- The critic may not merge across recommendation, summary or table boundaries.
- Every object remains verbatim: split table rows are accepted only when every
  non-separator line exists in the same page's table.
- The backend stores the raw metadata JSON on `Chunk.Metadata` and exposes
  `kind`, `population` and `recommendation_id` in the retrieval response.
  `ChunkMetadata.Kind`/`Population` read the structured field first and fall
  back to deterministic text/section classification for legacy rows.

The model context includes `kind:` and `population:` when they are meaningful,
so drafting can distinguish a definition or summary from a recommendation.

## 2. Hybrid search and query expansion

The existing dense + BM25/Full-Text RRF path is retained. It now runs each
query variant through both legs:

- `python-backend/query-synonyms.json` is the shared synonym source (for
  example `viral load` → `viral suppression`, `hiv rna`, `copies/ml`).
- Up to four variants are used: the original plus deterministic whole-word
  substitutions. Expansion never invents a diagnosis, treatment or population.
- Definitional questions boost `definition` and summary sections.
- Broad population questions boost summary/overview sections and demote
  narrow exception passages after cross-encoder scoring.
- `retrieval.hydeEnabled` (default `false`) optionally generates one
  hypothetical-answer query before retrieval. A model failure is non-fatal;
  the explicit queries still run. Enable it in the workspace configuration
  JSON after measuring latency and answer quality on the target deployment;
  the Workflow editor intentionally exposes only the five numeric retrieval
  controls, so this advanced flag is not a visible checkbox.

The synonym file is embedded into the ASP.NET assembly and included in the
Colab source archive. Changing it requires restarting the API and rebuilding
the archive; it does not require re-embedding chunks because it expands the
query, not the stored documents.

## 3. Cross-encoder reranking and intent alignment

The existing Colab cross-encoder (`/rerank`) remains the reranking stage before
drafting and verification. It receives the exact user question and at most 30
candidate passages. After its scores return, the API applies deterministic
intent alignment:

- `Narrow` when the question itself names a narrow population/exception
  (pregnancy, TB, children, renal failure, dialysis, contraindication,
  “only if”/“except”); no penalty is applied.
- `Broad` for general adult/patient/starting/management questions.
- A narrow-population passage receives a **0.10** score penalty on a broad
  question. This is enough to demote an edge-case exception below a general
  statement with an equal cross-encoder score; it does not remove verified
  evidence from the response.
- The response records `kind`/`population` and the existing rerank score for
  observability.

The Python relevance stage applies the same 0.10 narrow-exception penalty to
verified items and sorts lead items by the adjusted score. A narrow statement
can remain verified and appear under **Related**, but cannot lead a broad
answer solely because it scored well in isolation.

## 4. Hyphenation/whitespace-normalized quote verification

`evidence_text.canonical_text` now removes a hyphen between two letters for
matching only. This handles the PDF line-wrap case:

```text
source: A single highdose liposomal amphotericin B...
quote:  A single high-dose liposomal amphotericin B...
```

The quote verifies end-to-end, including the support, number, negation and
scope checks. The same normalization is applied in `pipeline._norm` for
recommendation/quote word comparisons. It deliberately does **not** remove:

- digit ranges (`100-200` remains distinct from `100200`),
- number-unit pairs (`24-hour` remains distinct from `24 hour`),
- comparison symbols or changed numbers.

Displayed/source text is unchanged; only the canonical comparison view is
normalized.

## Configuration and operations

| Setting | Default | Effect |
| --- | --- | --- |
| `retrieval.hydeEnabled` | `false` | Adds one optional hypothetical-answer query per problem. |
| `python-backend/query-synonyms.json` | checked in | Shared domain synonym map for query expansion and the embedded API resource. |
| `python-backend/config/config.schema.json` | checked in | Schema/defaults shared by ASP.NET and Python. |

After deploying these changes:

1. Rebuild/restart the ASP.NET API and the Python service so the schema and
   embedded synonym resource are loaded.
2. Re-ingest guideline documents to populate structured metadata and split
   graded table rows. Until then, legacy chunks use deterministic fallbacks.
3. Re-embed only if chunk text changed. The row split changes chunk boundaries
   for graded recommendation tables, so those rows need new vectors.
4. Keep `pubmedbert`/embedding model consistency; do not mix old and new
   vectors for the same workspace.

## Verification

| Check | Result |
| --- | --- |
| `dotnet test` (local, DB fixtures copied beside the temp test output) | **929 passed** |
| `python -m pytest tests -q` | **626 passed** |
| `npm test` | **584 Vitest + 19 Node passed** |
| `npx tsc --noEmit` | Passed |
| `npm run lint` | Passed |
| `npm run build` | Passed |
| Extracted `clinical-rag-colab-source-20261117-semantic-v18.zip` suite | **626 passed** |
| `eval/compare_baseline.py` synthetic retrieval gate | **PASS** |

The offline synthetic retrieval gate improved on the checked-in baseline:
`recallAt8` **1.0 → 1.0**, `mrrAt8` **0.7917 → 0.8333**, `ndcgAt8`
**0.8462 → 0.8770**, candidate recall **1.0 → 1.0**, same fixture SHA-256.
Artifacts: `eval/results/synthetic-retrieval-semantic-2026-11-17.json` and
`eval/results/synthetic-baseline-gate-semantic-2026-11-17.json`. This is a
fixed-vector regression, not live-model quality.

The InMemory performance path measured **137.436 ms p95** in this run versus
the earlier **122.799 ms** artifact. Query expansion adds legs and the index
now retains metadata; this is not a latency win. Re-measure the SQL/deployed
path with the real encoder/reranker before enabling broad expansion or HyDE
in production.

A real-PDF spot check of WHO pages 11, 41 and 43 produced 33 atomic chunks:
**7** graded recommendation objects from the p.11 table, **7** ungraded
`table_row` objects, **2** summary objects and the remaining
considerations. The p.43 quote “A single high-dose liposomal amphotericin
B–based regimen is the preferred option to treat individuals” now verifies
against the extracted `highdose` text, and the p.41 screening-unavailable
source sentence still verifies. This is an ingestion/verifier check, not a
clinical label.

New regressions cover:

- atomic adjacent recommendations and graded table rows;
- definition/summary/table classification and metadata binding;
- merge refusal across recommendation boundaries;
- synonym expansion, intent detection and narrow-passage penalties;
- optional HyDE query generation and fail-open behavior;
- exact canonical matching for `high-dose`/`highdose`;
- structured kind/population flowing into the model context.

## Limits

- These are offline/unit and local integration results. No live retrieval,
  hit@k, model-quality or clinical validation was performed.
- The synonym map is a small deterministic aid, not a clinical ontology.
- HyDE is off by default and adds a generation call; measure latency and
  quality before enabling it in production.
- Existing data requires re-ingestion for full structure metadata; the
  fallback path is safe but less precise.
- Hosted CI and independent review remain outstanding for this uncommitted
  worktree.
