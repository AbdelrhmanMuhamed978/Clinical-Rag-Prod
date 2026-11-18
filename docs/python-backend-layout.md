# Python backend layout

The deterministic local Python service is isolated in `python-backend/`. The
ASP.NET API and the Next.js frontend stay in `backend/` and `frontend/`; the
GPU model server and notebook stay in `colab/`.

## Layout

| Path | Purpose |
| --- | --- |
| `python-backend/pipeline.py` | Plan → retrieve → draft → verify pipeline. |
| `python-backend/config.py`, `facts.py`, `operations.py`, `chunker.py`, `clinical_text.py`, `evidence_text.py` | Core deterministic modules. |
| `python-backend/pipeline_service/` | FastAPI boundary and local service entrypoint. |
| `python-backend/config/` | Shared configuration schema, default prompts and retrieval limits. |
| `python-backend/*.json` | Rule, drug, clinical-text, vocabulary and query-synonym data used by the pipeline. |
| `python-backend/requirements.txt` | Local service dependencies. |
| `colab/` | GPU model server, notebook and Colab bootstrap. |
| `backend/` | ASP.NET API; embeds the shared JSON/config assets from `python-backend/`. |
| `frontend/` | Next.js UI. |
| `eval/`, `tests/`, `scripts/` | Evaluation, regression tests and tooling. |

## Running the local service

From the repository root:

```powershell
python -m pip install -r python-backend/requirements.txt
python -m uvicorn --app-dir python-backend pipeline_service.app:app `
  --host 127.0.0.1 --port 8100 --no-access-log
```

`--app-dir` puts `python-backend/` on `sys.path`, so the service's top-level
imports (`config`, `facts`, `pipeline`, `operations`) resolve without changing
the package names.

## Test and import resolution

- `conftest.py` adds `python-backend/` to `sys.path` for `python -m pytest tests`.
- `colab/__init__.py` adds the same path before the Colab server imports the
  shared chunker/operations modules.
- `colab/limits.py` reads the shared
  `python-backend/config/retrieval-limits.json`.
- The Colab archive includes `python-backend/` and `conftest.py`, so the
  extracted offline suite runs from the archive root.

## Verification

- `python -m pytest tests -q` — **626 passed**
- `dotnet test backend.Tests/ClinicalRag.Tests.csproj --nologo --no-restore` —
  **929 passed**
- Extracted Colab archive `clinical-rag-colab-source-20261117-semantic-v18.zip`
  (SHA-256
  `b84f60cbf0aa1bbdc7b17c8a6e16b789b0ec03a2a7a5d5f46f0005067da40b55`) —
  **626 passed**
- `python scripts/clinical_text_asset.py` — asset verified

The layout change is structural; the semantic ingestion/retrieval changes are
documented in [semantic ingestion and retrieval](semantic-ingestion-retrieval.md).
Neither establishes live model, retrieval, clinical or hosted-CI evidence.
