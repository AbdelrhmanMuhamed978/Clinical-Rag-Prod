import asyncio
import json
import math
import os
from pathlib import Path
import subprocess
import sys
import threading
import time

from fastapi.testclient import TestClient
import pytest

from colab.limits import MAX_EMBED_TEXTS
from colab.models import ModelRuntime, _sigmoid
from colab.parsing import parse_pdf_bytes
from colab.server import create_app


class FakeRuntime:
    model_name = "fake-model"
    embedding_model = "synthetic-embedding-model"

    def __init__(self):
        self.chunk_started = threading.Event()
        self.last_embed_texts = None
        self.last_rerank_passages = None

    async def startup(self):
        return None

    async def shutdown(self):
        return None

    async def generate(self, prompt, max_new_tokens=750, temperature=0.0,
                       repetition_penalty=1.0):
        if "profiling a clinical document" in prompt:
            self.chunk_started.set()
            await asyncio.sleep(0.35)
            return {
                "response": json.dumps({
                    "kind": "guideline",
                    "structure": "headings",
                    "strategy": "keep recommendations intact",
                    "notes": "test",
                }),
                "finish_reason": "stop",
                "truncated": False,
            }
        if "chunk critic" in prompt:
            await asyncio.sleep(0.05)
            return {
                "response": json.dumps({"decisions": [{"i": 0, "action": "KEEP"}]}),
                "finish_reason": "stop",
                "truncated": False,
            }
        if "Extract metadata" in prompt:
            return {
                "response": json.dumps({
                    "section_path": "Test",
                    "strength": "none",
                    "population": "",
                    "status": "FINAL",
                    "topic": "test",
                }),
                "finish_reason": "stop",
                "truncated": False,
            }
        return {
            "response": "direct answer",
            "finish_reason": "stop",
            "truncated": False,
        }

    async def embed(self, texts):
        self.last_embed_texts = texts
        return [[1.0, 0.0] for _ in texts]

    async def rerank(self, query, passages):
        self.last_rerank_passages = passages
        return [0.9 for _ in passages]

    async def retrieve(self, prompt, top_k=6):
        return {"context": "NOT_FOUND", "chunks": []}


def test_missing_colab_key_is_unauthorized():
    app = create_app(runtime=FakeRuntime(), api_key="test-secret")
    with TestClient(app) as client:
        assert client.get("/health").status_code == 200
        response = client.post("/generate", json={"prompt": "hello"})
        assert response.status_code == 401


def test_embed_caps_t4_batch_size_and_returns_the_actual_embedding_identity():
    runtime = FakeRuntime()
    with TestClient(create_app(runtime=runtime, api_key="test-secret")) as client:
        headers = {"X-Colab-Key": "test-secret"}
        rejected = client.post("/embed", headers=headers, json={"texts": ["source"] * 33})
        assert rejected.status_code == 422
        assert runtime.last_embed_texts is None
        accepted = client.post("/embed", headers=headers, json={"texts": ["source"] * 32})
        assert accepted.status_code == 200
        assert len(accepted.json()["embeddings"]) == 32
        assert accepted.json()["model"] == runtime.embedding_model
        assert accepted.json()["dimensions"] == 2


def test_rerank_caps_passages_and_truncates_unicode_consistently_before_runtime():
    runtime = FakeRuntime()
    with TestClient(create_app(runtime=runtime, api_key="test-secret")) as client:
        headers = {"X-Colab-Key": "test-secret"}
        rejected = client.post("/rerank", headers=headers, json={"query": "topic", "passages": ["source"] * 31})
        assert rejected.status_code == 422
        assert runtime.last_rerank_passages is None
        passage = "Section\n" + "😀" * 2100
        accepted = client.post("/rerank", headers=headers, json={"query": "topic", "passages": [passage]})
        assert accepted.status_code == 200
        assert runtime.last_rerank_passages == [passage[:2000]]


@pytest.mark.parametrize("model,vectors", [
    (None, [[1.0, 0.0]]),
    ("", [[1.0, 0.0]]),
    (" model", [[1.0, 0.0]]),
    ("model\n", [[1.0, 0.0]]),
    ("model\u0085", [[1.0, 0.0]]),
    ("m" * 257, [[1.0, 0.0]]),
    ("model", []),
    ("model", [[]]),
    ("model", [[0.0, 0.0]]),
    ("model", [[True, 0.0]]),
    ("model", [[math.nan, 0.0]]),
    ("model", [[math.inf, 0.0]]),
    ("model", [[1e100, 0.0]]),
    ("model", [[1.0, 0.0], [1.0]]),
    ("model", [[1.0] * 16385]),
])
def test_invalid_embedding_output_is_rejected_with_a_sanitized_error(model, vectors):
    class InvalidRuntime(FakeRuntime):
        embedding_model = model

        async def embed(self, texts):
            return vectors

    with TestClient(create_app(runtime=InvalidRuntime(), api_key="test-secret")) as client:
        response = client.post("/embed", headers={"X-Colab-Key": "test-secret"},
                               json={"texts": ["PRIVATE_INPUT"] * max(1, len(vectors))})
        assert response.status_code == 502
        assert response.json() == {"detail": "embedding failed"}


@pytest.mark.parametrize("scores", [[], [0.9, 0.8], [True], [math.nan], [math.inf], [-0.1], [1.1]])
def test_invalid_rerank_output_is_rejected_with_a_sanitized_error(scores):
    class InvalidRuntime(FakeRuntime):
        async def rerank(self, query, passages):
            return scores

    with TestClient(create_app(runtime=InvalidRuntime(), api_key="test-secret")) as client:
        response = client.post("/rerank", headers={"X-Colab-Key": "test-secret"},
                               json={"query": "PRIVATE_QUERY", "passages": ["PRIVATE_SOURCE"]})
        assert response.status_code == 502
        assert response.json() == {"detail": "reranking failed"}


@pytest.mark.parametrize("variable,value,expected", [
    ("COLAB_MAX_EMBED_TEXTS", "999", 32),
    ("COLAB_MAX_RERANK_PASSAGES", "999", 30),
    ("COLAB_MAX_EMBED_TEXTS", "2", 2),
    ("COLAB_MAX_RERANK_PASSAGES", "2", 2),
])
def test_environment_overrides_cannot_raise_the_shared_gpu_caps(variable, value, expected):
    environment = dict(os.environ, COLAB_MAX_EMBED_TEXTS="32", COLAB_MAX_RERANK_PASSAGES="30")
    environment[variable] = value
    completed = subprocess.run(
        [sys.executable, "-c", "import json; from colab.limits import MAX_EMBED_TEXTS, MAX_PASSAGES; print(json.dumps([MAX_EMBED_TEXTS, MAX_PASSAGES]))"],
        cwd=Path(__file__).resolve().parents[1], env=environment, check=True, capture_output=True, text=True,
    )
    limits = json.loads(completed.stdout)
    assert limits[0 if variable == "COLAB_MAX_EMBED_TEXTS" else 1] == expected


@pytest.mark.parametrize("score,expected", [(-10000.0, 0.0), (0.0, 0.5), (10000.0, 1.0)])
def test_sigmoid_does_not_overflow_on_finite_extreme_logits(score, expected):
    assert _sigmoid(score) == expected


@pytest.mark.parametrize("score", [math.nan, math.inf, -math.inf])
def test_sigmoid_refuses_nonfinite_logits(score):
    with pytest.raises(ValueError, match="invalid rerank score"):
        _sigmoid(score)


def test_cancelled_encoder_work_remains_serialized_with_reranking_and_returns_float32():
    runtime = ModelRuntime()
    runtime._started = True  # No GPU or heavyweight package imports in this regression.
    started = threading.Event()
    release = threading.Event()
    predicted = threading.Event()

    class ObservedLock:
        def __init__(self):
            self.lock = threading.Lock()
            self.guard = threading.Lock()
            self.attempts = 0
            self.second_attempt = threading.Event()

        def __enter__(self):
            with self.guard:
                self.attempts += 1
                if self.attempts == 2:
                    self.second_attempt.set()
            self.lock.acquire()

        def __exit__(self, *_):
            self.lock.release()

    class Vector:
        def astype(self, dtype):
            assert dtype == "float32"
            return self

        def tolist(self):
            return [1.0, 0.0]

    class Encoder:
        def encode(self, texts, *, batch_size, normalize_embeddings):
            assert batch_size == MAX_EMBED_TEXTS
            assert normalize_embeddings is True
            started.set()
            assert release.wait(timeout=5)
            return [Vector() for _ in texts]

    class Reranker:
        def predict(self, pairs, *, batch_size):
            predicted.set()
            assert pairs == [("query", "😀" * 2000)]
            assert batch_size == 8
            return [-10000.0]

    lock = ObservedLock()
    runtime._encoder_lock = lock
    runtime.embedder = Encoder()
    runtime.reranker = Reranker()

    async def exercise():
        embedding = asyncio.create_task(runtime.embed(["source"]))
        reranking = None
        try:
            assert await asyncio.to_thread(started.wait, 2)
            embedding.cancel()
            with pytest.raises(asyncio.CancelledError):
                await embedding
            reranking = asyncio.create_task(runtime.rerank("query", ["😀" * 2100]))
            assert await asyncio.to_thread(lock.second_attempt.wait, 2)
            assert not predicted.is_set()  # Cancellation cannot release running GPU work's lock.
            release.set()
            assert await reranking == [0.0]
            assert await runtime.embed(["source"]) == [[1.0, 0.0]]
        finally:
            release.set()
            await asyncio.gather(embedding, *([reranking] if reranking is not None else []), return_exceptions=True)

    asyncio.run(exercise())


def test_generate_answers_while_chunk_job_runs():
    runtime = FakeRuntime()
    app = create_app(runtime=runtime, api_key="test-secret")
    headers = {"X-Colab-Key": "test-secret"}

    with TestClient(app) as client:
        submitted = client.post(
            "/chunk/jobs",
            headers=headers,
            json={
                "text": "--- Page 1 ---\nClinical recommendation text.",
                "guidance": "keep recommendations intact",
                "max_tokens": 400,
            },
        )
        assert submitted.status_code == 202
        job_id = submitted.json()["id"]
        assert runtime.chunk_started.wait(timeout=1.0)

        started = time.perf_counter()
        generated = client.post(
            "/generate",
            headers=headers,
            json={"prompt": "answer now", "max_new_tokens": 128},
        )
        elapsed = time.perf_counter() - started

        assert generated.status_code == 200
        assert generated.json()["response"] == "direct answer"
        assert elapsed < 0.25

        deadline = time.time() + 2.0
        status = None
        while time.time() < deadline:
            status = client.get(f"/chunk/jobs/{job_id}", headers=headers).json()
            if status["status"] == "completed":
                break
            time.sleep(0.02)

        assert status is not None
        assert status["status"] == "completed"
        assert status["result"]["chunks"]


def test_pdf_page_count_is_captured_before_close_and_tables_are_kept():
    class FakeTable:
        def to_markdown(self):
            return "| A | B |\n| - | - |"

    class FakeTables:
        tables = [FakeTable()]

    class FakePage:
        def __init__(self, text):
            self.text = text

        def get_text(self):
            return self.text

        def find_tables(self):
            return FakeTables()

    class FakeDocument:
        def __init__(self):
            self.pages = [
                FakePage("RUNNING HEADER\nClinical page one\n1"),
                FakePage("RUNNING HEADER\nClinical page two\n2"),
            ]
            self.closed = False

        def __len__(self):
            if self.closed:
                raise RuntimeError("len() called after close")
            return len(self.pages)

        def __iter__(self):
            return iter(self.pages)

        def close(self):
            self.closed = True

    document = FakeDocument()

    class FakeFitz:
        @staticmethod
        def open(**_):
            return document

    parsed = parse_pdf_bytes(b"fake pdf", fitz_module=FakeFitz)

    assert parsed["pageCount"] == 2
    assert document.closed is True
    assert "RUNNING HEADER" not in parsed["text"]
    assert "Clinical page one" in parsed["text"]
    assert "[TABLES]" in parsed["text"]
