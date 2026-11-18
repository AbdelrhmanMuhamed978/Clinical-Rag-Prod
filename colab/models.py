"""Request schemas and lazy model runtime for the Colab server.

Heavy GPU packages are imported only during ``startup`` so API tests can inject a
fake runtime without importing vLLM, torch or sentence-transformers.
"""

import asyncio
import collections
import math
import os
import re
import threading
import uuid
from typing import Any

from pydantic import BaseModel, ConfigDict, Field, field_validator
from .limits import MAX_EMBED_TEXTS, MAX_PASSAGES, RRF_K, rerank_passage

MAX_PROMPT_CHARS = int(os.getenv("COLAB_MAX_PROMPT_CHARS", "50000"))
MAX_CHUNK_TEXT_CHARS = int(os.getenv("COLAB_MAX_CHUNK_TEXT_CHARS", "12000000"))


class StrictModel(BaseModel):
    model_config = ConfigDict(extra="forbid")


class GenerateRequest(StrictModel):
    prompt: str = Field(min_length=1, max_length=MAX_PROMPT_CHARS)
    max_new_tokens: int = Field(default=750, ge=32, le=2400)
    repetition_penalty: float = Field(default=1.0, ge=0.5, le=2.0)
    temperature: float = Field(default=0.1, ge=0.0, le=2.0)


class RetrieveRequest(StrictModel):
    prompt: str = Field(min_length=1, max_length=MAX_PROMPT_CHARS)
    top_k: int = Field(default=6, ge=1, le=8)


class EmbedRequest(StrictModel):
    texts: list[str]

    @field_validator("texts")
    @classmethod
    def validate_texts(cls, values: list[str]) -> list[str]:
        if not values or len(values) > MAX_EMBED_TEXTS:
            raise ValueError(f"texts must contain 1..{MAX_EMBED_TEXTS} items")
        if any(not value.strip() or len(value) > MAX_PROMPT_CHARS for value in values):
            raise ValueError("each text must be non-empty and within the prompt limit")
        return values


class RerankRequest(StrictModel):
    query: str = Field(min_length=1, max_length=MAX_PROMPT_CHARS)
    passages: list[str]

    @field_validator("passages")
    @classmethod
    def validate_passages(cls, values: list[str]) -> list[str]:
        if not values or len(values) > MAX_PASSAGES:
            raise ValueError(f"passages must contain 1..{MAX_PASSAGES} items")
        if any(not value.strip() or len(value) > MAX_PROMPT_CHARS for value in values):
            raise ValueError("each passage must be non-empty and within the prompt limit")
        return [rerank_passage(value) for value in values]


class ChunkRequest(StrictModel):
    text: str = Field(min_length=1, max_length=MAX_CHUNK_TEXT_CHARS)
    guidance: str = Field(default="", max_length=10000)
    max_tokens: int = Field(default=400, ge=32, le=2000)
    pages: int | None = Field(default=None, ge=1, le=100)


class ModelRuntime:
    """Lazy vLLM + PubMedBERT + BGE runtime."""

    model_name = "Qwen/Qwen3-4B-Instruct-2507"
    embedding_model = "pritamdeka/S-PubMedBert-MS-MARCO"

    def __init__(self) -> None:
        flag = os.getenv("COLAB_ENABLE_LARGER_MODEL", "0").strip().lower()
        if flag not in {"0", "false", "1", "true"}:
            raise ValueError("COLAB_ENABLE_LARGER_MODEL must be 0/false or 1/true.")
        self.larger_model = flag in {"1", "true"}
        self.model_name = "Qwen/Qwen3-8B-AWQ" if self.larger_model else type(self).model_name
        self.quantization = "awq" if self.larger_model else None
        self.device = "unknown"
        self.gpu_info: dict[str, Any] = {"available": False, "name": None, "device": "unknown", "totalMemoryBytes": None}
        self.embedding_dimensions: int | None = None
        self.tokenizer: Any = None
        self.engine: Any = None
        self.embedder: Any = None
        self.reranker: Any = None
        self._sampling_params: Any = None
        self._mongo_clients: dict[str, Any] = {}
        self._retrieve_lock = threading.Lock()
        self._encoder_lock = threading.Lock()
        self._startup_lock = asyncio.Lock()
        self._started = False

    async def startup(self) -> None:
        if self._started:
            return
        async with self._startup_lock:
            if self._started:
                return
            # vLLM's async engine must be created on the server event-loop thread.
            # Startup is intentionally blocking; no requests are accepted yet.
            self._load_models()
            self._started = True

    async def shutdown(self) -> None:
        self._mongo_clients.clear()

    def _load_models(self) -> None:
        import torch
        from sentence_transformers import CrossEncoder, SentenceTransformer
        from transformers import AutoTokenizer
        from vllm.engine.arg_utils import AsyncEngineArgs
        from vllm.engine.async_llm_engine import AsyncLLMEngine
        from vllm.sampling_params import SamplingParams

        self.device = "cuda" if torch.cuda.is_available() else "cpu"
        self.gpu_info = {"available": self.device == "cuda", "device": self.device,
                         "name": torch.cuda.get_device_name(0) if self.device == "cuda" else None,
                         "totalMemoryBytes": torch.cuda.get_device_properties(0).total_memory if self.device == "cuda" else None}
        self.tokenizer = AutoTokenizer.from_pretrained(self.model_name)
        engine_args = AsyncEngineArgs(
            model=self.model_name,
            tensor_parallel_size=1,
            gpu_memory_utilization=0.70,
            max_model_len=8192,
            max_num_seqs=8,
            dtype="half",
            enforce_eager=True,
            disable_log_requests=True,
            trust_remote_code=True,
            **({"quantization": self.quantization} if self.quantization else {}),
        )
        self.engine = AsyncLLMEngine.from_engine_args(engine_args)
        self._sampling_params = SamplingParams
        self.embedder = SentenceTransformer(self.embedding_model, device=self.device)
        self.embedding_dimensions = self.embedder.get_sentence_embedding_dimension()
        if self.device == "cuda":
            self.embedder.half()
        self.reranker = CrossEncoder(
            "BAAI/bge-reranker-large",
            device=self.device,
            max_length=512,
            automodel_args={"torch_dtype": torch.float16} if self.device == "cuda" else {},
        )

    def gpu_memory(self) -> dict[str, int]:
        """Live CUDA memory evidence for load-test reports; never loads models."""
        if self.device != "cuda":
            return {}
        try:
            import torch

            free, total = torch.cuda.mem_get_info()
            return {
                "totalMemoryBytes": int(total),
                "usedMemoryBytes": int(total - free),
                "allocatedMemoryBytes": int(torch.cuda.memory_allocated()),
                "peakAllocatedBytes": int(torch.cuda.max_memory_allocated()),
            }
        except Exception:
            # A driver/runtime failure must not make readiness itself fail.
            return {}

    def readiness(self) -> dict[str, Any]:
        gpu = dict(self.gpu_info)
        gpu.update(self.gpu_memory())
        return {"ready": self._started and all(model is not None for model in (self.engine, self.embedder, self.reranker)),
                "model": self.model_name, "embeddingModel": self.embedding_model,
                "embeddingDimensions": self.embedding_dimensions, "gpu": gpu}

    async def generate(self, prompt: str, max_new_tokens: int = 750,
                       temperature: float = 0.1,
                       repetition_penalty: float = 1.0) -> dict[str, Any]:
        await self.startup()
        rendered = self.tokenizer.apply_chat_template(
            [{"role": "user", "content": prompt}],
            tokenize=False,
            add_generation_prompt=True,
            **({"enable_thinking": False} if self.larger_model else {}),
        )
        prompt_tokens = len(self.tokenizer(rendered).input_ids)
        budget = 8192 - prompt_tokens - 16
        if budget < 64:
            return {
                "response": f"PROMPT_TOO_LONG: {prompt_tokens} tokens",
                "finish_reason": "length",
                "truncated": True,
            }
        sampling = self._sampling_params(
            max_tokens=max(32, min(max_new_tokens, 2400, budget)),
            temperature=temperature,
            top_p=0.95,
            repetition_penalty=repetition_penalty,
        )
        final = None
        async for output in self.engine.generate(rendered, sampling, str(uuid.uuid4())):
            final = output
        if final is None:
            return {"response": "", "finish_reason": "error", "truncated": True}
        item = final.outputs[0]
        finish_reason = item.finish_reason or "stop"
        return {
            "response": item.text.strip(),
            "finish_reason": finish_reason,
            "truncated": finish_reason == "length",
        }

    async def embed(self, texts: list[str]) -> list[list[float]]:
        if not texts or len(texts) > MAX_EMBED_TEXTS:
            raise ValueError("embedding batch exceeds the server limit")
        await self.startup()
        return await asyncio.to_thread(self._embed_sync, texts)

    def _embed_sync(self, texts: list[str]) -> list[list[float]]:
        # A cancelled to_thread caller does not stop GPU work. A threading lock
        # keeps late work and a retry from running encoder batches concurrently.
        with self._encoder_lock:
            vectors = self.embedder.encode(texts, batch_size=MAX_EMBED_TEXTS, normalize_embeddings=True)
        return [vector.astype("float32").tolist() for vector in vectors]

    async def rerank(self, query: str, passages: list[str]) -> list[float]:
        if not passages or len(passages) > MAX_PASSAGES:
            raise ValueError("rerank batch exceeds the server limit")
        await self.startup()
        return await asyncio.to_thread(self._rerank_sync, query, passages)

    def _rerank_sync(self, query: str, passages: list[str]) -> list[float]:
        pairs = [(query, rerank_passage(passage)) for passage in passages]
        with self._encoder_lock:
            raw_scores = self.reranker.predict(pairs, batch_size=8)
        return [round(_sigmoid(float(score)), 4) for score in raw_scores]

    async def retrieve(self, prompt: str, top_k: int = 6) -> dict[str, Any]:
        await self.startup()
        return await asyncio.to_thread(self._retrieve_sync, prompt, top_k)

    def _retrieve_sync(self, prompt: str, top_k: int) -> dict[str, Any]:
        """Legacy Mongo retrieval. URI is server-side only, never caller supplied."""
        from pymongo import MongoClient

        uri = os.getenv("COLAB_MONGODB_URI") or os.getenv("MONGODB_URI")
        if not uri:
            return {"context": "ERROR: legacy retrieval is not configured", "chunks": []}
        query = re.sub(
            r"\b(guide\s?lines?|guidelines?|recommendations?\s+for|what\s+(is|are)|tell me about|please)\b",
            " ",
            prompt,
            flags=re.I,
        )
        query = re.sub(r"\s+", " ", query).strip(" ?.,") or prompt
        if uri not in self._mongo_clients:
            self._mongo_clients[uri] = MongoClient(uri, serverSelectionTimeoutMS=15000)
        collection = self._mongo_clients[uri]["Rag_Db"]["Guidlines_Embbdding"]

        with self._retrieve_lock, self._encoder_lock:
            query_vector = self.embedder.encode([query], normalize_embeddings=True)[0].tolist()
            vector_results = list(collection.aggregate([{"$vectorSearch": {
                "index": "vector_index",
                "path": "embedding",
                "queryVector": query_vector,
                "numCandidates": 200,
                "limit": 40,
            }}]))
            text_results = list(collection.aggregate([
                {"$search": {"index": "text_index", "compound": {"must": [
                    {"text": {"query": query, "path": ["text", "section", "title"]}}
                ]}}},
                {"$limit": 40},
            ]))
            scores: dict[str, float] = collections.defaultdict(float)
            documents: dict[str, dict[str, Any]] = {}
            for result_list in (vector_results, text_results):
                for rank, document in enumerate(result_list):
                    chunk_id = str(document["chunk_id"])
                    scores[chunk_id] += 1.0 / (RRF_K + 1 + rank)
                    documents.setdefault(chunk_id, document)
            merged = [documents[chunk_id] for chunk_id, _ in sorted(
                scores.items(), key=lambda item: item[1], reverse=True
            )[:MAX_PASSAGES]]
            if not merged:
                return {"context": "NOT_FOUND", "chunks": []}
            pairs = [(query, rerank_passage(item["text"], item.get("section"))) for item in merged]
            rerank_scores = self.reranker.predict(pairs, batch_size=8)

        normalised = [_sigmoid(float(score)) for score in rerank_scores]
        for document, score in zip(merged, normalised):
            document["rerank_score"] = score
        selected = [document for document in sorted(
            merged, key=lambda item: item["rerank_score"], reverse=True
        )[:max(1, min(top_k, 8))] if document["rerank_score"] >= 0.30]
        if not selected:
            return {"context": "NOT_FOUND", "chunks": []}

        parts: list[str] = []
        chunks: list[dict[str, Any]] = []
        for index, document in enumerate(selected, 1):
            status = "DRAFT" if str(document.get("status", "")).lower() != "final" else "FINAL"
            section = document.get("section", "General")
            strength = f" | {document['strength']}" if document.get("strength") else ""
            parts.append(
                f"[S{index}] {document.get('society', '')} {document.get('year', '')} ({status}) | "
                f"{section} | p.{document.get('page', '')}{strength}\n{document['text']}\n"
            )
            chunks.append({
                "chunk_id": str(document.get("chunk_id", "")),
                "society": document.get("society", ""),
                "year": document.get("year", ""),
                "status": str(document.get("status", "unknown")).lower(),
                "section": section,
                "page": document.get("page", ""),
                "strength": document.get("strength", ""),
                "text": document["text"],
                "rerank_score": round(document["rerank_score"], 4),
            })
        return {"context": "\n".join(parts), "chunks": chunks}


def _sigmoid(score: float) -> float:
    if not math.isfinite(score):
        raise ValueError("invalid rerank score")
    if score >= 0:
        return 1 / (1 + math.exp(-score))
    exp_score = math.exp(score)
    return exp_score / (1 + exp_score)
