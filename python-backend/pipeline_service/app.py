"""FastAPI boundary for the deterministic, verified Python pipeline."""

import asyncio
import contextlib
import json
import os
import secrets
import sys
from collections.abc import Awaitable, Callable
from pathlib import Path
from typing import Any
from urllib.parse import urlsplit

if __package__ in (None, ""):
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from fastapi import Depends, FastAPI, Header, HTTPException
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse, PlainTextResponse, StreamingResponse
from pydantic import BaseModel, ConfigDict, Field, field_validator

import config as Cfg
import facts as F
import pipeline as P
from pipeline_service.middleware import BodySizeLimitMiddleware, NoStorePreviewMiddleware
from pipeline_service.colab_connection import ColabConnectionResolver
from operations import RequestObservabilityMiddleware, Step, prometheus


class HistoryTurn(BaseModel):
    model_config = ConfigDict(populate_by_name=True, extra="forbid")
    question: str = Field(min_length=1, max_length=2000)
    verified_summary: str = Field(alias="verifiedSummary", min_length=1, max_length=4000)


class AnalyzeRequest(BaseModel):
    model_config = ConfigDict(populate_by_name=True, extra="forbid")
    text: str = Field(min_length=1, max_length=100000)
    config: dict[str, Any] = Field(default_factory=dict)
    rules: list[dict[str, Any]] = Field(default_factory=list, max_length=512)
    history: list[HistoryTurn] = Field(default_factory=list, max_length=64)
    workspace_id: str = Field(alias="workspaceId", min_length=1, max_length=128)
    retrieve_url: str = Field(alias="retrieveUrl", min_length=1, max_length=2048)
    conversation_id: str | None = Field(default=None, alias="conversationId", max_length=128)
    drug_catalog: dict[str, Any] = Field(default_factory=dict, alias="drugCatalog", max_length=512)
    catalog_revision: int = Field(default=0, alias="catalogRevision", ge=0)
    # Only the authenticated ASP.NET test runner sets this; never a public analysis DTO.
    test_run_diagnostics: bool = Field(default=False, alias="testRunDiagnostics", strict=True)

    @field_validator("drug_catalog")
    @classmethod
    def require_catalog(cls, value: dict[str, Any]) -> dict[str, Any]:
        F.validate_drug_catalog(value)
        return value

    @field_validator("history")
    @classmethod
    def keep_last_six_turns(cls, value: list[HistoryTurn]) -> list[HistoryTurn]:
        return value[-6:]

    @field_validator("retrieve_url")
    @classmethod
    def require_http_url(cls, value: str) -> str:
        parsed = urlsplit(value)
        if parsed.scheme not in {"http", "https"} or not parsed.hostname or parsed.username or parsed.password:
            raise ValueError("retrieveUrl must be an HTTP(S) URL")
        return value


class RulePreviewRequest(BaseModel):
    model_config = ConfigDict(populate_by_name=True, extra="forbid")
    text: str = Field(default="", max_length=100000)
    rule: dict[str, Any]
    drug_catalog: dict[str, Any] = Field(alias="drugCatalog", max_length=512)
    regex_sample: str = Field(default="", alias="regexSample", max_length=10000)
    catalog_revision: int = Field(default=0, alias="catalogRevision", ge=0)

    @field_validator("rule")
    @classmethod
    def require_valid_rule(cls, value: dict[str, Any]) -> dict[str, Any]:
        F.validate_rule_definition(value)
        return value

    @field_validator("drug_catalog")
    @classmethod
    def require_valid_catalog(cls, value: dict[str, Any]) -> dict[str, Any]:
        F.validate_drug_catalog(value)
        return value


BackendFactory = Callable[[AnalyzeRequest, str], Any]


def _bearer_token(authorization: str) -> str:
    scheme, separator, token = authorization.partition(" ")
    if (not separator or scheme.lower() != "bearer" or not token.strip()
            or len(token) > 8192 or any(character.isspace() for character in token.strip())):
        raise HTTPException(status_code=401, detail="Bearer token required")
    return token.strip()


def _default_backend(request: AnalyzeRequest, token: str) -> P.Backend:
    return P.Backend(
        backend_url="",
        workspace_id=request.workspace_id,
        firebase_token=token,
        retrieve_url=request.retrieve_url,
        connection_resolver=ColabConnectionResolver(os.getenv("PIPELINE_RETRIEVE_BASE_URL", "http://localhost:5100"),
                                                    os.getenv("PIPELINE_SERVICE_KEY")),
        timeout=float(os.getenv("PIPELINE_TIMEOUT_SECONDS", "180")),
    )


def create_app(backend_factory: BackendFactory | None = None, readiness_probe: Callable[[], Awaitable[bool]] | None = None) -> FastAPI:
    factory = backend_factory or _default_backend
    service_key = os.getenv("PIPELINE_SERVICE_KEY")
    retrieve_base_url = os.getenv("PIPELINE_RETRIEVE_BASE_URL", "http://localhost:5100").rstrip("/")
    timeout_seconds = float(os.getenv("PIPELINE_TIMEOUT_SECONDS", "240"))
    application = FastAPI(
        title="Verified Clinical Pipeline",
        docs_url=None,
        redoc_url=None,
        openapi_url=None,
    )
    application.add_middleware(BodySizeLimitMiddleware)
    application.add_middleware(NoStorePreviewMiddleware)
    application.add_middleware(RequestObservabilityMiddleware, service="pipeline")

    @application.exception_handler(RequestValidationError)
    async def validation_error_handler(request: Any, exc: RequestValidationError) -> JSONResponse:
        # Pydantic's default response echoes rejected input, including entire cases.
        return JSONResponse({"detail": "Invalid request."}, status_code=422, headers={"Cache-Control": "no-store"})

    async def authenticate_service(
        pipeline_key: str | None = Header(default=None, alias="X-Pipeline-Key"),
    ) -> None:
        if not service_key:
            raise HTTPException(status_code=503, detail="Pipeline service key is not configured.", headers={"Retry-After": "30"})
        if not pipeline_key or not secrets.compare_digest(service_key.encode(), pipeline_key.encode()):
            raise HTTPException(status_code=401, detail="unauthorized")

    async def authenticate(
        authorization: str | None = Header(default=None, alias="Authorization"),
        _: None = Depends(authenticate_service),
    ) -> str:
        if not authorization:
            raise HTTPException(status_code=401, detail="Bearer token required")
        return _bearer_token(authorization)

    @application.get("/health")
    async def health() -> dict[str, bool]:
        return {"alive": True}

    @application.get("/ready", dependencies=[Depends(authenticate_service)])
    async def ready() -> JSONResponse:
        available = False
        try:
            with Step("pipeline", "pipeline.ready"):
                async with asyncio.timeout(4):
                    if readiness_probe is not None:
                        available = await readiness_probe()
                    else:
                        backend = P.Backend("", "", connection_resolver=ColabConnectionResolver(retrieve_base_url, service_key))
                        await backend.prepare()
                        available = True
        except (P.ServiceUnavailable, TimeoutError):
            pass
        return JSONResponse({"ready": available}, status_code=200 if available else 503,
                            headers={} if available else {"Retry-After": "30"})

    @application.get("/metrics", dependencies=[Depends(authenticate_service)])
    async def metrics() -> PlainTextResponse:
        return PlainTextResponse(prometheus("pipeline"), media_type="text/plain; version=0.0.4")

    def prepare_config(request: AnalyzeRequest) -> Cfg.Config:
        try:
            expected = urlsplit(retrieve_base_url)
            actual = urlsplit(request.retrieve_url)

            def origin(url: Any) -> tuple[str, str | None, int]:
                return url.scheme, url.hostname, url.port or (443 if url.scheme == "https" else 80)

            expected_path = expected.path.rstrip("/") + f"/api/workspaces/{request.workspace_id}/analyze/retrieve"
            if (expected.scheme not in {"http", "https"} or not expected.hostname
                    or expected.username or expected.password or expected.query or expected.fragment):
                raise HTTPException(status_code=503, detail="Pipeline retrieval origin is not configured correctly.")
            if origin(actual) != origin(expected) or actual.path != expected_path or actual.query or actual.fragment:
                raise HTTPException(status_code=422, detail="retrieveUrl must target the configured workspace retrieval endpoint.")
            return Cfg.Config.from_dict(request.config)
        except (ValueError, TypeError, AttributeError, KeyError):
            raise HTTPException(status_code=422, detail="Invalid analysis configuration or retrieval URL.") from None

    async def prepare_backend(request: AnalyzeRequest, token: str) -> Any:
        backend = factory(request, token)
        if hasattr(backend, "prepare") and not P.is_smalltalk(request.text):
            async with asyncio.timeout(min(10, timeout_seconds)):
                await backend.prepare()
        return backend

    def unavailable_response(exception: P.ServiceUnavailable) -> JSONResponse:
        return JSONResponse({"detail": "The Colab model service is offline or not ready. Retry shortly." if exception.service == "colab"
                             else "The verified pipeline service is temporarily unavailable; no unverified answer was returned.",
                             "dependency": exception.service}, status_code=503, headers={"Retry-After": str(exception.retry_after_seconds)})

    async def execute(request: AnalyzeRequest, backend: Any, request_config: Cfg.Config, emit: Any) -> dict[str, Any]:
        history = [turn.model_dump(by_alias=True) for turn in request.history]
        async with asyncio.timeout(timeout_seconds):
            return await P.analyze(
                request.text,
                backend,
                emit,
                config=request_config,
                rules=request.rules,
                history=history,
                drug_catalog=request.drug_catalog,
                **({"include_diagnostics": True} if request.test_run_diagnostics else {}),
            )

    @application.post("/analyze")
    async def analyze(request: AnalyzeRequest, token: str = Depends(authenticate)) -> dict[str, Any]:
        request_config = prepare_config(request)
        async def emit(_: Any) -> None:
            return None

        try:
            backend = await prepare_backend(request, token)
            return await execute(request, backend, request_config, emit)
        except P.ServiceUnavailable as exception:
            return unavailable_response(exception)
        except TimeoutError:
            return unavailable_response(P.ServiceUnavailable("pipeline"))
        except Exception:
            raise HTTPException(status_code=502, detail="Verified analysis failed; no unverified answer was returned.") from None

    @application.post("/analyze/stream")
    async def analyze_stream(request: AnalyzeRequest, token: str = Depends(authenticate)) -> StreamingResponse:
        request_config = prepare_config(request)
        try:
            backend = await prepare_backend(request, token)
        except P.ServiceUnavailable as exception:
            return unavailable_response(exception)
        except TimeoutError:
            return unavailable_response(P.ServiceUnavailable("pipeline"))
        async def events():
            queue: asyncio.Queue[tuple[str, Any]] = asyncio.Queue()

            async def emit(progress: dict[str, Any] | str) -> None:
                # Structured stage evidence from the pipeline; string-only emitters
                # remain explicitly legacy messages, not completion assertions.
                await queue.put(("progress", progress if isinstance(progress, dict) else {"message": progress}))

            async def run() -> None:
                try:
                    result = await execute(request, backend, request_config, emit)
                    await queue.put(("result", result))
                except P.ServiceUnavailable as exception:
                    await queue.put(("error", {"message": "The model or verified pipeline service is temporarily unavailable; no unverified answer was returned.",
                                               "dependency": exception.service, "retryAfterSeconds": exception.retry_after_seconds}))
                except TimeoutError:
                    await queue.put(("error", {"message": "The verified pipeline service timed out; no unverified answer was returned.",
                                               "dependency": "pipeline", "retryAfterSeconds": 30}))
                except Exception:
                    # Never include request text, prompts or backend response bodies.
                    await queue.put(("error", {"message": "Verified analysis failed; no unverified answer was returned."}))

            task = asyncio.create_task(run(), name="verified-analysis")
            try:
                while True:
                    event, payload = await queue.get()
                    yield f"event: {event}\ndata: {json.dumps(payload, ensure_ascii=False)}\n\n"
                    if event in {"result", "error"}:
                        break
            finally:
                if not task.done():
                    task.cancel()
                with contextlib.suppress(asyncio.CancelledError):
                    await task

        return StreamingResponse(
            events(),
            media_type="text/event-stream",
            headers={"Cache-Control": "no-store", "X-Accel-Buffering": "no"},
        )

    @application.post("/rules/preview")
    async def rule_preview(request: RulePreviewRequest, token: str = Depends(authenticate)) -> JSONResponse:
        # No backend/model construction, retrieval, mutation or persistence. Regex matching
        # is bounded by the canonical facts implementation; inputs never enter diagnostics.
        try:
            with Step("pipeline", "pipeline.preview", request.text):
                async with asyncio.timeout(5):
                    result = await asyncio.to_thread(F.preview_rule, request.text, request.rule,
                                                    request.drug_catalog, request.regex_sample)
            return JSONResponse(result | {"catalog_revision": request.catalog_revision}, headers={"Cache-Control": "no-store"})
        except (ValueError, TypeError, OverflowError):
            raise HTTPException(status_code=422, detail="Invalid preview definition.") from None
        except Exception:
            raise HTTPException(status_code=502, detail="Canonical preview failed; no approval or result was stored.") from None

    return application


app = create_app()
