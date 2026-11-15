"""Search/GPU limits shared with ASP.NET; environment overrides can only lower caps."""

import json
import os
from pathlib import Path

_LIMITS = json.loads((Path(__file__).resolve().parents[1] / "config" / "retrieval-limits.json").read_text(encoding="utf-8"))
MAX_EMBED_TEXTS = min(_LIMITS["maxEmbeddingBatchSize"], max(1, int(os.getenv("COLAB_MAX_EMBED_TEXTS", str(_LIMITS["maxEmbeddingBatchSize"])))))
MAX_PASSAGES = min(_LIMITS["maxRerankPassages"], max(1, int(os.getenv("COLAB_MAX_RERANK_PASSAGES", str(_LIMITS["maxRerankPassages"])))))
MAX_EMBEDDING_DIMENSIONS = _LIMITS["maxEmbeddingDimensions"]
MAX_PASSAGE_CHARACTERS = _LIMITS["maxRerankPassageCharacters"]
RRF_K = _LIMITS["rrfK"]


def rerank_passage(text: str, section: str | None = None) -> str:
    return (f"{section}\n{text}" if section else text)[:MAX_PASSAGE_CHARACTERS]
