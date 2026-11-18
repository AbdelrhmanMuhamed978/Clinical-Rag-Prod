"""Dependency-free, privacy-safe correlation, JSON summaries and bounded step metrics."""

import asyncio
import contextvars
import functools
import hashlib
import json
import logging
import math
import re
import sys
import threading
import time
import uuid
from datetime import datetime, timezone
from typing import Any

from starlette.responses import JSONResponse

REQUEST_ID = contextvars.ContextVar("clinical_rag_request_id", default=None)
REJECTION_REASONS = ("empty_draft", "json_parse_error", "quote_not_found", "formatting_mismatch", "number_missing",
                     "stronger_than_source", "negation_conflict", "scope_mismatch", "topic_mismatch",
                     "truncated", "deduplicated", "below_threshold")
BUCKETS = (5, 25, 50, 100, 250, 500, 1000, 5000, 30000, 120000, 300000)
STEPS = frozenset({
    "http.server", "pipeline.analyze", "pipeline.facts", "pipeline.planning", "pipeline.retrieval",
    "pipeline.verification", "pipeline.patient_checks", "pipeline.routing", "pipeline.preview", "pipeline.ready",
    "pipeline.colab_configuration", "pipeline.http.retrieve", "pipeline.http.rerank", "pipeline.http.generate",
    "colab.ready", "colab.generate", "colab.embed", "colab.rerank", "colab.retrieve", "colab.parse_pdf",
    "colab.chunk.profile", "colab.chunk.preview", "colab.chunk.run", "colab.chunk.job", "colab.chunk.generate",
})
_metrics: dict[tuple[str, str, str], dict[str, Any]] = {}
_retry_count = 0
_lock = threading.Lock()


def request_id() -> str:
    return REQUEST_ID.get() or uuid.uuid4().hex


class PrivacySafeJsonFormatter(logging.Formatter):
    def format(self, record: logging.LogRecord) -> str:
        # Never format arbitrary messages, args, exc_info, stack traces or SDK bodies.
        event = getattr(record, "event", "operation_failed" if record.levelno >= logging.ERROR else "operation")
        if event not in {"input_summary", "step_finished", "request_finished", "operation_failed", "registration_failed", "answer_diagnostics"}:
            event = "operation"
        result: dict[str, Any] = {
            "timestamp": datetime.fromtimestamp(record.created, timezone.utc).isoformat(),
            "level": record.levelname, "service": record.name.rsplit(".", 1)[-1],
            "event": event, "requestId": REQUEST_ID.get(),
        }
        step_name = getattr(record, "step", None)
        if step_name in STEPS:
            result["step"] = step_name
        for name in ("durationMilliseconds", "inputLength", "statusCode"):
            value = getattr(record, name, None)
            if isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value) and value >= 0:
                result[name] = value
        fingerprint = getattr(record, "inputHash", None)
        if isinstance(fingerprint, str) and re.fullmatch(r"[a-f0-9]{64}", fingerprint):
            result["inputHash"] = fingerprint
        outcome = getattr(record, "outcome", None)
        if outcome in {"success", "error", "cancelled"}:
            result["outcome"] = outcome
        # Only route templates and exception type identifiers, never raw paths or messages.
        route = getattr(record, "route", None)
        if route in {"/health", "/ready", "/metrics", "/analyze", "/analyze/stream", "/rules/preview", "/generate",
                     "/embed", "/rerank", "/retrieve", "/parse-pdf", "/chunk/profile", "/chunk/preview", "/chunk/run",
                     "/chunk/jobs", "/chunk/jobs/{job_id}", "unmatched"}:
            result["route"] = route
        exception_type = getattr(record, "exceptionType", None)
        if isinstance(exception_type, str) and re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]{0,63}", exception_type):
            result["exceptionType"] = exception_type
        if event == "answer_diagnostics":
            counters = getattr(record, "diagnostics", {})
            if isinstance(counters, dict):
                safe = {key: value for key in ("drafts", "candidates", "verified")
                        if type(value := counters.get(key)) is int and 0 <= value <= 100000}
                reasons = counters.get("reasons", {})
                safe["reasons"] = {key: value for key in REJECTION_REASONS
                                   if isinstance(reasons, dict) and type(value := reasons.get(key)) is int and 0 <= value <= 100000}
                result["diagnostics"] = safe
            index = getattr(record, "problemIndex", None)
            if type(index) is int and 1 <= index <= 100:
                result["problemIndex"] = index
        return json.dumps(result, ensure_ascii=True, separators=(",", ":"))


def record_answer_diagnostics(index: int, counters: dict) -> None:
    logging.getLogger("clinical_rag.pipeline").info("answer_diagnostics", extra={
        "event": "answer_diagnostics", "problemIndex": index, "diagnostics": counters,
    })


def configure_logging(service: str) -> logging.Logger:
    logger = logging.getLogger(f"clinical_rag.{service}")
    logger.setLevel(logging.INFO)
    if not any(isinstance(handler.formatter, PrivacySafeJsonFormatter) for handler in logger.handlers):
        handler = logging.StreamHandler(sys.stdout)
        handler.setFormatter(PrivacySafeJsonFormatter())
        logger.addHandler(handler)
    logger.propagate = False
    logging.getLogger("uvicorn.access").disabled = True
    logging.getLogger("httpx").setLevel(logging.CRITICAL)
    logging.getLogger("httpcore").setLevel(logging.CRITICAL)
    return logger


def record_step(service: str, step_name: str, outcome: str, milliseconds: float) -> None:
    if service not in {"pipeline", "colab"} or step_name not in STEPS or outcome not in {"success", "error", "cancelled"}:
        raise ValueError("Operation metric labels must be fixed labels")
    if not math.isfinite(milliseconds) or milliseconds < 0:
        raise ValueError("Operation durations must be finite and nonnegative")
    with _lock:
        sample = _metrics.setdefault((service, step_name, outcome), {"count": 0, "sum": 0.0, "buckets": [0] * len(BUCKETS)})
        sample["count"] += 1
        sample["sum"] += milliseconds
        for index, boundary in enumerate(BUCKETS):
            if milliseconds <= boundary:
                sample["buckets"][index] += 1
    logging.getLogger(f"clinical_rag.{service}").info("step_finished", extra={
        "event": "step_finished", "step": step_name, "outcome": outcome, "durationMilliseconds": round(milliseconds, 3),
    })


def record_retry() -> None:
    global _retry_count
    with _lock:
        _retry_count += 1


def prometheus(service: str) -> str:
    lines = ["# TYPE clinicalrag_step_duration_milliseconds histogram"]
    with _lock:
        for (owner, step_name, outcome), sample in sorted(_metrics.items()):
            if owner != service:
                continue
            labels = f'service="{owner}",step="{step_name}",outcome="{outcome}"'
            for boundary, count in zip(BUCKETS, sample["buckets"]):
                lines.append(f'clinicalrag_step_duration_milliseconds_bucket{{{labels},le="{boundary}"}} {count}')
            lines.extend([
                f'clinicalrag_step_duration_milliseconds_bucket{{{labels},le="+Inf"}} {sample["count"]}',
                f'clinicalrag_step_duration_milliseconds_count{{{labels}}} {sample["count"]}',
                f'clinicalrag_step_duration_milliseconds_sum{{{labels}}} {sample["sum"]}',
            ])
        if service == "pipeline":
            lines.extend(["# TYPE clinicalrag_pipeline_http_retries_total counter", f"clinicalrag_pipeline_http_retries_total {_retry_count}"])
    return "\n".join(lines) + "\n"


class Step:
    def __init__(self, service: str, name: str, input_value: str | bytes | None = None) -> None:
        self.service, self.name, self.input_value = service, name, input_value

    def __enter__(self):
        self.started = time.perf_counter()
        if self.input_value is not None:
            data = self.input_value.encode("utf-8") if isinstance(self.input_value, str) else self.input_value
            logging.getLogger(f"clinical_rag.{self.service}").info("input_summary", extra={
                "event": "input_summary", "step": self.name, "inputHash": hashlib.sha256(data).hexdigest(),
                "inputLength": len(self.input_value),
            })
        return self

    def __exit__(self, exception_type, exception, traceback):
        outcome = "cancelled" if exception_type and issubclass(exception_type, asyncio.CancelledError) else "error" if exception_type else "success"
        record_step(self.service, self.name, outcome, (time.perf_counter() - self.started) * 1000)


def timed(service: str, name: str, input_argument: int | None = None):
    def decorate(function):
        @functools.wraps(function)
        async def measured(*args, **kwargs):
            value = args[input_argument] if input_argument is not None and len(args) > input_argument else None
            with Step(service, name, value if isinstance(value, (str, bytes)) else None):
                return await function(*args, **kwargs)
        return measured
    return decorate


class RequestObservabilityMiddleware:
    def __init__(self, app: Any, service: str) -> None:
        self.app, self.service = app, service
        self.logger = configure_logging(service)

    async def __call__(self, scope: dict[str, Any], receive: Any, send: Any) -> None:
        if scope["type"] != "http":
            await self.app(scope, receive, send)
            return
        supplied = dict(scope.get("headers", [])).get(b"x-request-id", b"").decode("ascii", errors="replace")
        identifier = supplied.lower() if re.fullmatch(r"[A-Fa-f0-9]{32}", supplied) else uuid.uuid4().hex
        marker = REQUEST_ID.set(identifier)
        started, status, sent_headers, finished = time.perf_counter(), 500, False, False

        async def observed_send(message):
            nonlocal status, sent_headers, finished
            if message["type"] == "http.response.start":
                status, sent_headers = message["status"], True
                headers = [(key, value) for key, value in message.get("headers", []) if key.lower() not in {b"x-request-id", b"cache-control"}]
                message = {**message, "headers": headers + [(b"x-request-id", identifier.encode()), (b"cache-control", b"no-store")]}
            elif message["type"] == "http.response.body" and not message.get("more_body", False):
                finished = True
            await send(message)

        try:
            await self.app(scope, receive, observed_send)
        except asyncio.CancelledError:
            status = 499
            raise
        except Exception as exception:
            self.logger.error("operation_failed", extra={"event": "operation_failed", "exceptionType": type(exception).__name__})
            if not sent_headers:
                await JSONResponse({"detail": "The service could not complete the request."}, status_code=500)(scope, receive, observed_send)
            elif not finished:
                await observed_send({"type": "http.response.body", "body": b"", "more_body": False})
            # Do not re-raise SDK exceptions into Uvicorn's raw exception/stack logger.
        finally:
            duration = (time.perf_counter() - started) * 1000
            record_step(self.service, "http.server", "cancelled" if status == 499 else "error" if status >= 500 else "success", duration)
            route = getattr(scope.get("route"), "path", "unmatched")
            self.logger.info("request_finished", extra={"event": "request_finished", "route": route, "statusCode": status,
                                                        "durationMilliseconds": round(duration, 3)})
            REQUEST_ID.reset(marker)
