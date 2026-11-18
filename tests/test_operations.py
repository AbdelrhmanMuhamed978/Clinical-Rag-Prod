"""Offline operations contracts; no Firebase, model downloads or GPU required."""
import asyncio
import hashlib
import json
import logging
from types import SimpleNamespace

import httpx
import pytest
from fastapi import FastAPI
from fastapi.testclient import TestClient

import operations as O
import pipeline as P
import colab.registration as R
from colab.server import create_app as colab_app
from pipeline_service.app import create_app as pipeline_app
from pipeline_service.colab_connection import ColabConnectionResolver


def test_json_formatter_discards_arbitrary_messages_exceptions_and_inputs():
    record = logging.LogRecord("clinical_rag.pipeline", logging.ERROR, "private-path", 1,
                               "PRIVATE_CASE %s", ("PRIVATE_KEY",), (RuntimeError, RuntimeError("PRIVATE_PROMPT"), None))
    record.event = "step_finished"
    record.step = "pipeline.analyze"
    record.inputHash = hashlib.sha256(b"PRIVATE_CASE").hexdigest()
    record.inputLength = 12
    record.route = "/PRIVATE_URL"
    result = O.PrivacySafeJsonFormatter().format(record)
    assert "PRIVATE" not in result
    body = json.loads(result)
    assert body["inputHash"] == record.inputHash and body["inputLength"] == 12
    assert "route" not in body
    with pytest.raises(ValueError):
        O.record_step("pipeline", "PRIVATE_LABEL", "success", 1)
    O.record_step("pipeline", "pipeline.ready", "success", 1)
    assert "PRIVATE" not in O.prometheus("pipeline")


def test_request_correlation_and_sanitized_exception_response():
    app = FastAPI()
    app.add_middleware(O.RequestObservabilityMiddleware, service="pipeline")

    @app.get("/boom")
    async def boom():
        raise RuntimeError("PRIVATE_CASE_AND_CREDENTIAL")

    with TestClient(app) as client:
        response = client.get("/boom", headers={"X-Request-Id": "DEADBEEF" * 4})
        assert response.status_code == 500
        assert response.headers["X-Request-Id"] == "deadbeef" * 4
        assert response.headers["Cache-Control"] == "no-store"
        assert "PRIVATE" not in response.text
        generated = client.get("/boom", headers={"X-Request-Id": "PRIVATE_INPUT"}).headers["X-Request-Id"]
        assert len(generated) == 32 and all(character in "0123456789abcdef" for character in generated)
    assert O.REQUEST_ID.get() is None


@pytest.mark.parametrize("ready", [True, False])
def test_pipeline_liveness_readiness_and_metrics_authentication(monkeypatch, ready):
    monkeypatch.setenv("PIPELINE_SERVICE_KEY", "synthetic-pipeline-key")

    async def probe():
        return ready

    with TestClient(pipeline_app(readiness_probe=probe)) as client:
        assert client.get("/health").json() == {"alive": True}
        assert client.get("/ready").status_code == 401
        assert client.get("/metrics").status_code == 401
        response = client.get("/ready", headers={"X-Pipeline-Key": "synthetic-pipeline-key"})
        assert response.status_code == (200 if ready else 503)
        assert response.json() == {"ready": ready}
        if not ready:
            assert response.headers["Retry-After"] == "30"


def test_colab_readiness_reports_model_gpu_and_requires_model_key():
    class Runtime:
        async def startup(self):
            pass

        async def shutdown(self):
            pass

        def readiness(self):
            return {"ready": True, "model": "synthetic-model", "embeddingModel": "synthetic-encoder",
                    "embeddingDimensions": 2, "gpu": {"available": True, "name": "synthetic-GPU", "device": "cuda", "totalMemoryBytes": 1024}}

    with TestClient(colab_app(runtime=Runtime(), api_key="synthetic-model-key")) as client:
        assert client.get("/health").status_code == 200
        assert client.get("/ready").status_code == 401
        response = client.get("/ready", headers={"X-Colab-Key": "synthetic-model-key", "X-Request-Id": "a" * 32})
        assert response.status_code == 200 and response.json()["ready"] is True
        assert response.json()["gpu"]["name"] == "synthetic-GPU"
        assert response.headers["X-Request-Id"] == "a" * 32


def test_model_readiness_exposes_live_gpu_memory_without_loading_models(monkeypatch):
    from colab.models import ModelRuntime

    runtime = ModelRuntime()
    runtime._started = True
    runtime.device = "cuda"
    runtime.gpu_info = {"available": True, "name": "synthetic-GPU", "device": "cuda", "totalMemoryBytes": 1024}
    runtime.engine = runtime.embedder = runtime.reranker = object()
    monkeypatch.setattr(runtime, "gpu_memory", lambda: {
        "totalMemoryBytes": 1024, "usedMemoryBytes": 512,
        "allocatedMemoryBytes": 256, "peakAllocatedBytes": 384,
    })
    summary = runtime.readiness()
    assert summary["ready"] is True
    assert summary["gpu"]["name"] == "synthetic-GPU"
    assert summary["gpu"]["usedMemoryBytes"] == 512
    assert summary["gpu"]["peakAllocatedBytes"] == 384


def test_dynamic_connection_resolution_forwards_correlation_and_uses_new_runtime_on_each_generation(monkeypatch):
    calls = []
    revision = 0

    def respond(request):
        nonlocal revision
        calls.append(request)
        assert request.headers["X-Request-Id"] == "b" * 32
        if request.url.path == "/api/internal/colab-connection":
            revision += 1
            assert request.headers["X-Pipeline-Key"] == "synthetic-service-key"
            assert "Authorization" not in request.headers
            return httpx.Response(200, json={"baseUrl": f"https://runtime-{revision}.example.com/", "apiKey": f"synthetic-model-key-{revision}"})
        assert request.headers["X-Colab-Key"] == f"synthetic-model-key-{revision}"
        assert "Authorization" not in request.headers and "X-Pipeline-Key" not in request.headers
        return httpx.Response(200, json={"response": "synthetic draft"})

    original = httpx.AsyncClient
    monkeypatch.setattr(httpx, "AsyncClient", lambda **kwargs: original(transport=httpx.MockTransport(respond), **kwargs))

    async def run():
        marker = O.REQUEST_ID.set("b" * 32)
        try:
            backend = P.Backend("", "workspace", firebase_token="PRIVATE_BEARER", connection_resolver=ColabConnectionResolver("https://backend.example.com", "synthetic-service-key"))
            assert await backend.generate("PRIVATE_PROMPT") == "synthetic draft"
            assert await backend.generate("PRIVATE_PROMPT") == "synthetic draft"
        finally:
            O.REQUEST_ID.reset(marker)

    asyncio.run(run())
    assert [str(call.url) for call in calls if call.url.path == "/generate"] == ["https://runtime-1.example.com/generate", "https://runtime-2.example.com/generate"]


@pytest.mark.parametrize("status,body,code", [(401, {}, "unauthorized"), (409, {"code": "manual_override"}, "manual_override"),
                                             (409, {"code": "superseded"}, "superseded"), (503, {}, "offline"),
                                             (302, {}, "invalid"), (200, {"registered": False}, "invalid")])
def test_registration_failure_contract_never_echoes_response_body(monkeypatch, status, body, code):
    original = httpx.Client
    monkeypatch.setattr(httpx, "Client", lambda **kwargs: original(transport=httpx.MockTransport(
        lambda _: httpx.Response(status, json={**body, "error": "PRIVATE_UPSTREAM"})), **kwargs))
    with pytest.raises(R.RegistrationError) as error:
        R.registration_request("https://backend.example.com", "https://colab.example.com", "synthetic-model-key", "synthetic-registration-secret", "c" * 32)
    assert error.value.code == code and "PRIVATE" not in str(error.value)


def test_registration_sends_shared_secret_and_heartbeat_contract_without_redirects(monkeypatch):
    calls = []

    def respond(request):
        calls.append(request)
        assert request.headers["X-Colab-Registration-Secret"] == "synthetic-registration-secret"
        assert "Authorization" not in request.headers
        assert json.loads(request.content) == {"baseUrl": "https://colab.example.com", "apiKey": "synthetic-model-key", "instanceId": "c" * 32, "heartbeat": True}
        return httpx.Response(200, json={"registered": True})

    original = httpx.Client

    def client(**kwargs):
        assert kwargs["follow_redirects"] is False and kwargs["trust_env"] is False
        return original(transport=httpx.MockTransport(respond), **kwargs)

    monkeypatch.setattr(httpx, "Client", client)
    R.registration_request("https://backend.example.com", "https://colab.example.com", "synthetic-model-key", "synthetic-registration-secret", "c" * 32, heartbeat=True)
    assert len(calls) == 1


def test_superseded_heartbeat_stops_old_notebook_runtime(monkeypatch):
    calls = []

    class Event:
        stopped = False

        def wait(self, _):
            return self.stopped

        def set(self):
            self.stopped = True

    class Thread:
        def __init__(self, target, **kwargs):
            self.target = target

        def start(self):
            self.target()

        def join(self, **kwargs):
            pass

    def register(*args, **kwargs):
        calls.append(kwargs.get("heartbeat", False))
        if kwargs.get("heartbeat"):
            raise R.RegistrationError("superseded")

    monkeypatch.setattr(R, "threading", SimpleNamespace(Event=Event, Thread=Thread))
    monkeypatch.setattr(R, "registration_request", register)
    handle = R.start_registration("https://backend.example.com", "https://colab.example.com", "synthetic-model-key", "synthetic-registration-secret")
    handle.stop()
    assert calls == [False, True]
