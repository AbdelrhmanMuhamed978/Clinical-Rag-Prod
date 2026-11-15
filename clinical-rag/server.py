"""FastAPI routes for the secured Colab model server.

This module contains HTTP concerns only. Model loading lives in ``models.py``,
PDF parsing in ``parsing.py`` and all chunking logic in the root ``chunker.py``.
"""

import asyncio
import logging
import math
import os
import secrets
import time
import uuid
from contextlib import asynccontextmanager
from typing import Any

from fastapi import APIRouter, Depends, FastAPI, File, Header, HTTPException, UploadFile, status
from fastapi.responses import JSONResponse
from fastapi.responses import PlainTextResponse
from fastapi.exceptions import RequestValidationError

from chunker import chunk_document, parse_pages, profile_document
from .models import (
    ChunkRequest,
    EmbedRequest,
    GenerateRequest,
    ModelRuntime,
    RerankRequest,
    RetrieveRequest,
)
from .parsing import parse_pdf_bytes
from .limits import MAX_EMBEDDING_DIMENSIONS
from operations import RequestObservabilityMiddleware, Step, prometheus

LOGGER = logging.getLogger("clinical_rag.colab")
MAX_BODY_BYTES = int(os.getenv("COLAB_MAX_BODY_BYTES", str(16 * 1024 * 1024)))
MAX_PDF_BYTES = int(os.getenv("COLAB_MAX_PDF_BYTES", str(15 * 1024 * 1024)))
MAX_JOB_RECORDS = max(1, min(1000, int(os.getenv("COLAB_MAX_JOB_RECORDS", "100"))))


class BodySizeLimitMiddleware:
    """Buffer and reject HTTP request bodies larger than the configured limit."""

    def __init__(self, app: Any, max_bytes: int) -> None:
        self.app = app
        self.max_bytes = max_bytes

    async def __call__(self, scope: dict[str, Any], receive: Any, send: Any) -> None:
        if scope["type"] != "http":
            await self.app(scope, receive, send)
            return

        headers = {key.lower(): value for key, value in scope.get("headers", [])}
        content_length = headers.get(b"content-length")
        if content_length:
            try:
                if int(content_length) > self.max_bytes:
                    await JSONResponse(
                        {"detail": "request body too large"},
                        status_code=status.HTTP_413_REQUEST_ENTITY_TOO_LARGE,
                    )(scope, receive, send)
                    return
            except ValueError:
                await JSONResponse({"detail": "invalid content length"}, status_code=400)(scope, receive, send)
                return

        body = bytearray()
        more_body = True
        while more_body:
            message = await receive()
            body.extend(message.get("body", b""))
            if len(body) > self.max_bytes:
                await JSONResponse(
                    {"detail": "request body too large"},
                    status_code=status.HTTP_413_REQUEST_ENTITY_TOO_LARGE,
                )(scope, receive, send)
                return
            if message["type"] == "http.disconnect":
                return
            more_body = message.get("more_body", False)

        sent = False

        async def replay() -> dict[str, Any]:
            nonlocal sent
            if sent:
                return {"type": "http.request", "body": b"", "more_body": False}
            sent = True
            return {"type": "http.request", "body": bytes(body), "more_body": False}

        await self.app(scope, replay, send)


def create_app(runtime: Any | None = None, api_key: str | None = None) -> FastAPI:
    model_runtime = runtime or ModelRuntime()
    configured_key = api_key if api_key is not None else os.getenv("COLAB_API_KEY")
    jobs: dict[str, dict[str, Any]] = {}
    chunk_semaphore = asyncio.Semaphore(1)
    job_tasks: set[asyncio.Task] = set()
    models_ready = False

    @asynccontextmanager
    async def lifespan(_: FastAPI):
        nonlocal models_ready
        await model_runtime.startup()
        models_ready = True
        try:
            yield
        finally:
            models_ready = False
            pending = list(job_tasks)
            for task in pending:
                task.cancel()
            await asyncio.gather(*pending, return_exceptions=True)
            await model_runtime.shutdown()

    application = FastAPI(
        title="Clinical RAG Colab Server",
        docs_url=None,
        redoc_url=None,
        openapi_url=None,
        lifespan=lifespan,
    )
    application.add_middleware(BodySizeLimitMiddleware, max_bytes=MAX_BODY_BYTES)
    application.add_middleware(RequestObservabilityMiddleware, service="colab")
    application.state.runtime = model_runtime
    application.state.jobs = jobs

    @application.exception_handler(RequestValidationError)
    async def invalid_request(_: Any, exception: RequestValidationError) -> JSONResponse:
        return JSONResponse({"detail": "Invalid model request."}, status_code=422)

    async def require_api_key(x_colab_key: str | None = Header(default=None, alias="X-Colab-Key")) -> None:
        if not configured_key:
            raise HTTPException(status_code=503, detail="Colab API key is not configured", headers={"Retry-After": "30"})
        if not x_colab_key or not secrets.compare_digest(x_colab_key.encode(), configured_key.encode()):
            raise HTTPException(status_code=401, detail="unauthorized")

    protected = APIRouter(dependencies=[Depends(require_api_key)])

    @application.get("/health")
    async def health() -> dict[str, bool]:
        # Deliberately minimal and unauthenticated for tunnel/liveness checks.
        return {"ok": True}

    @protected.get("/ready")
    async def ready() -> JSONResponse:
        with Step("colab", "colab.ready"):
            summary = model_runtime.readiness() if hasattr(model_runtime, "readiness") else {
                "ready": models_ready, "model": getattr(model_runtime, "model_name", None),
                "embeddingModel": getattr(model_runtime, "embedding_model", None),
                "embeddingDimensions": getattr(model_runtime, "embedding_dimensions", None),
                "gpu": getattr(model_runtime, "gpu_info", {"available": False, "name": None, "device": "unknown", "totalMemoryBytes": None}),
            }
            summary = {**summary, "ready": models_ready and summary.get("ready") is True}
        return JSONResponse(summary, status_code=200 if summary["ready"] else 503,
                            headers={} if summary["ready"] else {"Retry-After": "30"})

    @protected.get("/metrics")
    async def metrics() -> PlainTextResponse:
        return PlainTextResponse(prometheus("colab"), media_type="text/plain; version=0.0.4")

    @protected.post("/generate")
    async def generate(request: GenerateRequest) -> dict[str, Any]:
        try:
            with Step("colab", "colab.generate", request.prompt):
                return await model_runtime.generate(
                    request.prompt,
                    max_new_tokens=request.max_new_tokens,
                    temperature=request.temperature,
                    repetition_penalty=request.repetition_penalty,
                )
        except Exception as exc:
            LOGGER.error("Generation failed (%s)", type(exc).__name__)
            raise HTTPException(status_code=502, detail="generation failed") from None

    @protected.post("/embed")
    async def embed(request: EmbedRequest) -> dict[str, Any]:
        try:
            with Step("colab", "colab.embed", "\n".join(request.texts)):
                vectors = await model_runtime.embed(request.texts)
            model = model_runtime.embedding_model
            dimensions = len(vectors[0]) if vectors else 0
            if (not isinstance(model, str) or not model.strip() or model != model.strip() or len(model) > 256
                    or any(ord(character) < 32 or 127 <= ord(character) <= 159 for character in model)
                    or len(vectors) != len(request.texts) or not 1 <= dimensions <= MAX_EMBEDDING_DIMENSIONS
                    or any(len(vector) != dimensions
                           or any(isinstance(value, bool) or not math.isfinite(value) or abs(value) > 3.4028234663852886e38 for value in vector)
                           or math.hypot(*vector) == 0 for vector in vectors)):
                raise ValueError("invalid embedding model output")
            return {"embeddings": vectors, "model": model, "dimensions": dimensions}
        except Exception as exc:
            LOGGER.error("Embedding failed (%s)", type(exc).__name__)
            raise HTTPException(status_code=502, detail="embedding failed") from None

    @protected.post("/rerank")
    async def rerank(request: RerankRequest) -> dict[str, Any]:
        try:
            with Step("colab", "colab.rerank", request.query + "\n" + "\n".join(request.passages)):
                scores = await model_runtime.rerank(request.query, request.passages)
            if len(scores) != len(request.passages) or any(isinstance(score, bool) or not math.isfinite(score) or not 0 <= score <= 1 for score in scores):
                raise ValueError("invalid rerank model output")
            return {"scores": scores}
        except Exception as exc:
            LOGGER.error("Reranking failed (%s)", type(exc).__name__)
            raise HTTPException(status_code=502, detail="reranking failed") from None

    @protected.post("/retrieve")
    async def retrieve(request: RetrieveRequest) -> dict[str, Any]:
        try:
            with Step("colab", "colab.retrieve", request.prompt):
                return await model_runtime.retrieve(request.prompt, request.top_k)
        except Exception as exc:
            LOGGER.error("Legacy retrieval failed (%s)", type(exc).__name__)
            raise HTTPException(status_code=502, detail="retrieval failed") from None

    @protected.post("/parse-pdf")
    async def parse_pdf(file: UploadFile = File(...)) -> dict[str, Any]:
        contents = await file.read(MAX_PDF_BYTES + 1)
        if len(contents) > MAX_PDF_BYTES:
            raise HTTPException(status_code=413, detail="PDF is too large")
        try:
            with Step("colab", "colab.parse_pdf", contents):
                return await asyncio.to_thread(parse_pdf_bytes, contents)
        except ValueError as exc:
            raise HTTPException(status_code=400, detail="PDF could not be parsed") from None
        except Exception as exc:
            LOGGER.error("PDF parsing failed (%s)", type(exc).__name__)
            raise HTTPException(status_code=422, detail="PDF parsing failed") from None

    async def chunk_llm(prompt: str) -> str:
        with Step("colab", "colab.chunk.generate", prompt):
            result = await model_runtime.generate(
                prompt,
                max_new_tokens=400,
                temperature=0.0,
                repetition_penalty=1.0,
            )
        return result.get("response", "")

    async def execute_chunk(request: ChunkRequest, preview_pages: int = 0) -> dict[str, Any]:
        # Only one chunking pipeline can submit model work at a time. /generate,
        # /embed, /rerank and /retrieve never wait on this semaphore.
        async with chunk_semaphore:
            return await chunk_document(
                chunk_llm,
                request.text,
                request.guidance,
                request.max_tokens,
                preview_pages=preview_pages,
            )

    @protected.post("/chunk/profile")
    async def chunk_profile(request: ChunkRequest) -> dict[str, Any]:
        with Step("colab", "colab.chunk.profile", request.text):
            pages = parse_pages(request.text)
            async with chunk_semaphore:
                profile = await profile_document(chunk_llm, pages, request.guidance)
        return {"pages": len(pages), "profile": profile}

    @protected.post("/chunk/preview")
    async def chunk_preview(request: ChunkRequest) -> dict[str, Any]:
        with Step("colab", "colab.chunk.preview", request.text):
            return await execute_chunk(request, preview_pages=request.pages or 3)

    @protected.post("/chunk/run")
    async def chunk_run(request: ChunkRequest) -> dict[str, Any]:
        # Kept for compatibility; long-running callers should use /chunk/jobs.
        with Step("colab", "colab.chunk.run", request.text):
            return await execute_chunk(request)

    def prune_jobs() -> None:
        if len(jobs) < MAX_JOB_RECORDS:
            return
        finished = sorted(
            (
                (job_id, job.get("finished_at", float("inf")))
                for job_id, job in jobs.items()
                if job["status"] in {"completed", "failed"}
            ),
            key=lambda item: item[1],
        )
        for job_id, _ in finished[:max(1, len(jobs) - MAX_JOB_RECORDS + 1)]:
            jobs.pop(job_id, None)

    async def run_job(job_id: str, request: ChunkRequest) -> None:
        jobs[job_id]["status"] = "running"
        try:
            with Step("colab", "colab.chunk.job", request.text):
                jobs[job_id]["result"] = await execute_chunk(request)
            jobs[job_id]["status"] = "completed"
        except asyncio.CancelledError:
            jobs[job_id]["status"] = "failed"
            jobs[job_id]["error"] = "chunking interrupted"
            raise
        except Exception as exc:
            jobs[job_id]["status"] = "failed"
            jobs[job_id]["error"] = "chunking failed"
            LOGGER.error("Chunk job %s failed (%s)", job_id, type(exc).__name__)
        finally:
            jobs[job_id]["finished_at"] = time.time()

    @protected.post("/chunk/jobs", status_code=status.HTTP_202_ACCEPTED)
    async def create_chunk_job(request: ChunkRequest) -> dict[str, str]:
        prune_jobs()
        if len(jobs) >= MAX_JOB_RECORDS:
            raise HTTPException(status_code=503, detail="The chunk job queue is full. Retry shortly.", headers={"Retry-After": "30"})
        job_id = uuid.uuid4().hex
        jobs[job_id] = {"id": job_id, "status": "queued", "created_at": time.time()}
        task = asyncio.create_task(run_job(job_id, request), name=f"chunk-job-{job_id}")
        job_tasks.add(task)
        task.add_done_callback(job_tasks.discard)
        return {"id": job_id}

    @protected.get("/chunk/jobs/{job_id}")
    async def get_chunk_job(job_id: str) -> dict[str, Any]:
        job = jobs.get(job_id)
        if job is None:
            raise HTTPException(status_code=404, detail="job not found")
        response: dict[str, Any] = {"id": job_id, "status": job["status"]}
        if job["status"] == "completed":
            response["result"] = job["result"]
        elif job["status"] == "failed":
            response["error"] = job["error"]
        return response

    application.include_router(protected)
    return application


app = create_app()
