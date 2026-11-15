import asyncio
import json

import httpx
import pytest
from fastapi.testclient import TestClient

import config as Cfg
import pipeline as P
from pipeline_service.app import create_app


HEADERS = {"Authorization": "Bearer user-token", "X-Pipeline-Key": "pipeline-test-key"}


@pytest.fixture(autouse=True)
def pipeline_settings(monkeypatch):
    monkeypatch.setenv("PIPELINE_SERVICE_KEY", "pipeline-test-key")
    monkeypatch.setenv("PIPELINE_RETRIEVE_BASE_URL", "http://backend")


def source_chunk(text):
    return {
        "chunk_id": "chunk-1",
        "society": "SCCM",
        "year": "2024",
        "status": "final",
        "section": "Shock",
        "page": 12,
        "strength": "strong",
        "text": text,
        "rerank_score": 0.95,
    }


class FakeBackend:
    def __init__(self):
        self.prompts = []
        self.retrieve_queries = []

    async def retrieve(self, query):
        self.retrieve_queries.append(query)
        text = "In adults with septic shock, we recommend norepinephrine as the first-line agent."
        return {"context": "unused", "chunks": [source_chunk(text)]}

    async def generate(self, prompt, max_new_tokens=400):
        self.prompts.append(prompt)
        if "Rewrite the clinician's message" in prompt:
            return "vasopressor septic shock adults"
        return (
            "SOURCE: S1\n"
            "RECOMMENDATION: Norepinephrine is recommended as first-line agent in adults with septic shock.\n"
            "QUOTE: we recommend norepinephrine as the first-line agent"
        )


def request_payload(config=None):
    return {
        "text": "Which vasopressor should be used in septic shock?",
        "config": config or {},
        "rules": [],
        "history": [],
        "workspaceId": "11111111-1111-1111-1111-111111111111",
        "retrieveUrl": "http://backend/api/workspaces/11111111-1111-1111-1111-111111111111/analyze/retrieve",
        "conversationId": "conversation-1",
    }


def stream_events(body):
    events = []
    for record in body.replace("\r\n", "\n").split("\n\n"):
        lines = record.splitlines()
        name = next((line[6:].strip() for line in lines if line.startswith("event:")), None)
        data = "\n".join(line[5:].lstrip() for line in lines if line.startswith("data:"))
        if name and data:
            events.append((name, json.loads(data)))
    return events


def test_pipeline_service_returns_verified_contract():
    backend = FakeBackend()
    app = create_app(backend_factory=lambda request, token: backend)

    with TestClient(app) as client:
        response = client.post(
            "/analyze",
            headers=HEADERS,
            json=request_payload(),
        )

    assert response.status_code == 200
    result = response.json()
    assert set((
        "problems", "not_covered", "warnings", "disclaimer", "version", "plan_source"
    )).issubset(result)
    assert result["problems"][0]["items"]
    item = result["problems"][0]["items"][0]
    assert item["quote"] == "we recommend norepinephrine as the first-line agent"
    assert item["citation"]["id"] == "S1"
    assert result["problems"][0]["unverified"] == []


def test_pipeline_service_streams_real_progress_and_result():
    backend = FakeBackend()
    app = create_app(backend_factory=lambda request, token: backend)

    with TestClient(app) as client:
        with client.stream(
            "POST",
            "/analyze/stream",
            headers=HEADERS,
            json=request_payload(),
        ) as response:
            body = "".join(response.iter_text())

    assert response.status_code == 200
    assert "event: progress" in body
    assert "event: result" in body
    assert "Drafting and checking answer" in body
    assert response.headers["cache-control"] == "no-store"
    assert response.headers["x-accel-buffering"] == "no"
    events = stream_events(body)
    assert [name for name, _ in events] == ["progress"] * 6 + ["result"]
    progress = [data for name, data in events if name == "progress"]
    assert [(item["stage"], item["state"], item["stepId"]) for item in progress] == [
        ("planning", "started", "planning"), ("planning", "completed", "planning"),
        ("retrieval", "started", "retrieval-1"), ("retrieval", "completed", "retrieval-1"),
        ("verification", "started", "verification-1"), ("verification", "completed", "verification-1"),
    ]
    assert {item["mode"] for item in progress} == {"question"}
    assert events[-1][1]["plan_source"] == "single_question"


def test_service_rejects_missing_key_and_untrusted_retrieve_url():
    backend = FakeBackend()
    app = create_app(backend_factory=lambda request, token: backend)
    payload = request_payload()
    with TestClient(app) as client:
        missing_key = client.post("/analyze", headers={"Authorization": "Bearer user-token"}, json=payload)
        assert missing_key.status_code == 401
        payload["retrieveUrl"] = "http://untrusted.invalid/collect-token"
        untrusted = client.post("/analyze", headers=HEADERS, json=payload)
        assert untrusted.status_code == 422
    assert backend.prompts == []
    assert backend.retrieve_queries == []


def test_service_fails_closed_without_service_key(monkeypatch):
    monkeypatch.delenv("PIPELINE_SERVICE_KEY", raising=False)
    app = create_app(backend_factory=lambda request, token: FakeBackend())
    with TestClient(app) as client:
        response = client.post("/analyze", headers=HEADERS, json=request_payload())
    assert response.status_code == 503


def test_validation_errors_do_not_echo_case_text():
    payload = request_payload()
    payload["text"] = "PRIVATE_CASE_MARKER " * 6000
    app = create_app(backend_factory=lambda request, token: FakeBackend())
    with TestClient(app) as client:
        response = client.post("/analyze", headers=HEADERS, json=payload)
    assert response.status_code == 422
    assert "PRIVATE_CASE_MARKER" not in response.text


def test_invalid_config_is_rejected_before_streaming():
    payload = request_payload({"prompts": {"answer": "invalid PRIVATE_PROMPT_MARKER"}})
    app = create_app(backend_factory=lambda request, token: FakeBackend())
    with TestClient(app) as client:
        for endpoint in ("/analyze", "/analyze/stream"):
            response = client.post(endpoint, headers=HEADERS, json=payload)
            assert response.status_code == 422
            assert "PRIVATE_PROMPT_MARKER" not in response.text


def test_chunked_request_body_has_a_hard_size_limit():
    app = create_app(backend_factory=lambda request, token: FakeBackend())
    body = json.dumps({**request_payload(), "text": "x" * (2 * 1024 * 1024)}).encode()
    with TestClient(app) as client:
        response = client.post("/analyze", headers={**HEADERS, "Content-Type": "application/json"},
                               content=iter([body[:1024], body[1024:]]))
    assert response.status_code == 413


def test_stream_errors_do_not_echo_backend_exception_text():
    class BrokenBackend(FakeBackend):
        async def generate(self, prompt, max_new_tokens=400):
            raise RuntimeError("PRIVATE_CASE_MARKER")

    app = create_app(backend_factory=lambda request, token: BrokenBackend())
    with TestClient(app) as client:
        response = client.post("/analyze/stream", headers=HEADERS, json=request_payload())
    assert "event: error" in response.text
    assert "PRIVATE_CASE_MARKER" not in response.text


def test_concurrent_service_requests_keep_prompts_and_retrieval_settings_isolated():
    async def run_pair():
        ready = asyncio.Event()
        arrivals = 0
        backends = {}

        class InterleavedBackend(FakeBackend):
            async def generate(self, prompt, max_new_tokens=400):
                nonlocal arrivals
                self.prompts.append(prompt)
                if prompt.startswith(("REWRITE_A", "REWRITE_B")):
                    arrivals += 1
                    if arrivals == 2:
                        ready.set()
                    await asyncio.wait_for(ready.wait(), timeout=2)
                    return "septic shock vasopressor"
                return await super().generate(prompt, max_new_tokens)

        def factory(request, token):
            backend = InterleavedBackend()
            backends[request.conversation_id] = backend
            return backend

        app = create_app(backend_factory=factory)
        payload_a = request_payload({
            "prompts": {"rewrite": "REWRITE_A {q}", "answer": "ANSWER_A {problem} {patient} {k} {context}"},
            "retrieval": {"minRerank": 0.5, "queriesPerProblem": 1},
        })
        payload_b = request_payload({
            "prompts": {"rewrite": "REWRITE_B {q}", "answer": "ANSWER_B {problem} {patient} {k} {context}"},
            "retrieval": {"minRerank": 0.99, "queriesPerProblem": 2},
        })
        payload_a["conversationId"] = "a"
        payload_b["conversationId"] = "b"
        async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://service") as client:
            responses = await asyncio.gather(
                client.post("/analyze", headers=HEADERS, json=payload_a),
                client.post("/analyze", headers=HEADERS, json=payload_b),
            )
        return responses, backends

    responses, backends = asyncio.run(run_pair())
    assert all(response.status_code == 200 for response in responses)
    assert responses[0].json()["problems"][0]["items"]
    assert responses[1].json()["problems"][0]["items"] == []
    assert len(backends["a"].retrieve_queries) == 1
    assert len(backends["b"].retrieve_queries) == 2
    assert any("ANSWER_A" in prompt for prompt in backends["a"].prompts)
    assert all("ANSWER_B" not in prompt for prompt in backends["a"].prompts)
    assert all("ANSWER_A" not in prompt for prompt in backends["b"].prompts)


def test_concurrent_requests_do_not_leak_config():
    class PromptBackend:
        def __init__(self):
            self.prompts = []

        async def generate(self, prompt, max_new_tokens=400):
            self.prompts.append(prompt)
            return "query alpha" if "MARKER_A" in prompt else "query beta"

        async def retrieve(self, query):
            return {"context": "NOT_FOUND", "chunks": []}

    async def run_pair():
        backend_a = PromptBackend()
        backend_b = PromptBackend()
        config_a = Cfg.Config.from_dict({
            "prompts": {"rewrite": "MARKER_A {q}"},
            "retrieval": {"queriesPerProblem": 1},
        })
        config_b = Cfg.Config.from_dict({
            "prompts": {"rewrite": "MARKER_B {q}"},
            "retrieval": {"queriesPerProblem": 1},
        })

        async def emit(_):
            return None

        result_a, result_b = await asyncio.gather(
            P.analyze("follow-up question", backend_a, emit, config=config_a),
            P.analyze("follow-up question", backend_b, emit, config=config_b),
        )
        return backend_a, backend_b, result_a, result_b

    backend_a, backend_b, result_a, result_b = asyncio.run(run_pair())
    assert all("MARKER_B" not in prompt for prompt in backend_a.prompts)
    assert all("MARKER_A" not in prompt for prompt in backend_b.prompts)
    assert result_a["problems"][0]["queries"][0] == "query alpha"
    assert result_b["problems"][0]["queries"][0] == "query beta"


def test_pipeline_completion_events_wait_for_the_actual_stage_to_finish():
    async def scenario():
        reached = {stage: asyncio.Event() for stage in ("planning", "retrieval", "verification")}
        release = {stage: asyncio.Event() for stage in reached}
        progress = []

        class GatedBackend(FakeBackend):
            async def generate(self, prompt, max_new_tokens=400):
                stage = "planning" if "Rewrite the clinician's message" in prompt else "verification"
                reached[stage].set()
                await release[stage].wait()
                return await super().generate(prompt, max_new_tokens)

            async def retrieve(self, query):
                reached["retrieval"].set()
                await release["retrieval"].wait()
                return await super().retrieve(query)

        async def emit(item):
            progress.append(item)

        task = asyncio.create_task(P.analyze(request_payload()["text"], GatedBackend(), emit,
            config=Cfg.Config.from_dict({"retrieval": {"queriesPerProblem": 1}}), rules=[]))
        for index, stage in enumerate(reached):
            await asyncio.wait_for(reached[stage].wait(), timeout=2)
            assert [(item["stage"], item["state"]) for item in progress] == [
                (previous, state) for previous in list(reached)[:index] for state in ("started", "completed")
            ] + [(stage, "started")]
            release[stage].set()
        result = await asyncio.wait_for(task, timeout=2)
        assert progress[-1]["state"] == "completed"
        assert result["problems"][0]["items"]
        assert {item["mode"] for item in progress} == {"question"}

    asyncio.run(scenario())


@pytest.mark.parametrize("word_count,mode", [(39, "question"), (40, "case")])
def test_stream_mode_uses_the_pipeline_word_boundary_and_only_reached_case_stages(word_count, mode):
    class NoEvidenceBackend(FakeBackend):
        async def retrieve(self, query):
            return {"context": "NOT_FOUND", "chunks": []}

    payload = request_payload({"retrieval": {"queriesPerProblem": 1}})
    payload["text"] = " ".join(["synthetic"] * (word_count - 1) + ["query"])
    app = create_app(backend_factory=lambda request, token: NoEvidenceBackend())
    with TestClient(app) as client:
        response = client.post("/analyze/stream", headers=HEADERS, json=payload)
    events = stream_events(response.text)
    progress = [data for name, data in events if name == "progress"]
    assert {item["mode"] for item in progress} == {mode}
    stages = {item["stage"] for item in progress}
    assert stages == ({"planning", "retrieval"} if mode == "question" else {"facts", "planning", "retrieval", "patient_checks"})
    assert "verification" not in stages
    assert events[-1][0] == "result"
    assert events[-1][1]["plan_source"] == ("single_question" if mode == "question" else "rules")
    assert all(not problem["items"] for problem in events[-1][1]["problems"])


def test_a_short_case_follow_up_stays_question_mode_despite_verified_case_history():
    payload = request_payload()
    payload["text"] = "What about the mother?"
    payload["history"] = [{"question": " ".join(["synthetic"] * 45), "verifiedSummary": "Verified source statement only."}]
    app = create_app(backend_factory=lambda request, token: FakeBackend())
    with TestClient(app) as client:
        response = client.post("/analyze/stream", headers=HEADERS, json=payload)
    events = stream_events(response.text)
    progress = [data for name, data in events if name == "progress"]
    assert {item["mode"] for item in progress} == {"question"}
    assert not {"facts", "patient_checks"}.intersection(item["stage"] for item in progress)
    assert events[-1][1]["plan_source"] == "single_question"


@pytest.mark.parametrize("retrieval_error", [False, True])
def test_no_evidence_streams_do_not_claim_that_generation_or_verification_ran(retrieval_error):
    class NoEvidenceBackend(FakeBackend):
        async def retrieve(self, query):
            return {"context": "ERROR: PRIVATE_BACKEND_BODY" if retrieval_error else "NOT_FOUND", "chunks": []}

    backend = NoEvidenceBackend()
    app = create_app(backend_factory=lambda request, token: backend)
    with TestClient(app) as client:
        response = client.post("/analyze/stream", headers=HEADERS, json=request_payload())
    events = stream_events(response.text)
    progress = [data for name, data in events if name == "progress"]
    assert [item["stage"] for item in progress] == ["planning", "planning", "retrieval", "retrieval"]
    assert progress[-1]["state"] == ("failed" if retrieval_error else "completed")
    result = events[-1][1]
    assert result["problems"][0]["status"] == ("error" if retrieval_error else "not_found")
    assert result["problems"][0]["items"] == []
    assert result["not_covered"] == ([] if retrieval_error else [result["problems"][0]["title"]])
    assert any("coverage is unknown" in warning for warning in result["warnings"]) is retrieval_error
    assert any("Not covered" in warning for warning in result["warnings"]) is not retrieval_error
    assert len(backend.prompts) == 1  # Only the question rewrite ran, never answer generation.
    assert "PRIVATE_BACKEND_BODY" not in response.text


def test_completed_verification_with_zero_verified_items_keeps_rejected_drafts_excluded():
    class RejectedBackend(FakeBackend):
        async def generate(self, prompt, max_new_tokens=400):
            if "Rewrite the clinician's message" in prompt:
                return await super().generate(prompt, max_new_tokens)
            return "SOURCE: S1\nRECOMMENDATION: UNVERIFIED_CLAIM_MARKER\nQUOTE: These invented words do not occur in the source."

    app = create_app(backend_factory=lambda request, token: RejectedBackend())
    with TestClient(app) as client:
        response = client.post("/analyze/stream", headers=HEADERS, json=request_payload())
    events = stream_events(response.text)
    checked = [data for name, data in events if name == "progress" and data["stage"] == "verification"]
    assert [item["state"] for item in checked] == ["started", "completed"]
    assert "0 verified, 1 excluded" in checked[-1]["message"]
    problem = events[-1][1]["problems"][0]
    assert problem["items"] == [] and problem["status"] == "unverified"
    assert problem["unverified"][0]["recommendation"] == "UNVERIFIED_CLAIM_MARKER"
    assert problem["unverified"][0]["issues"]
    assert "UNVERIFIED_CLAIM_MARKER" not in json.dumps(checked)


@pytest.mark.parametrize("stage", ["facts", "planning", "retrieval", "verification"])
def test_stage_failures_report_failed_not_completed_and_end_with_a_safe_terminal_error(monkeypatch, stage):
    class BrokenBackend(FakeBackend):
        async def retrieve(self, query):
            if stage == "retrieval":
                raise P.BackendError("PRIVATE_RETRIEVAL_ERROR")
            return await super().retrieve(query)

        async def generate(self, prompt, max_new_tokens=400):
            if stage == "verification" and "Rewrite the clinician's message" not in prompt:
                raise P.BackendError("PRIVATE_GENERATION_ERROR")
            return await super().generate(prompt, max_new_tokens)

    payload = request_payload({"retrieval": {"queriesPerProblem": 1}})
    if stage == "facts":
        payload["text"] = " ".join(["synthetic"] * 40)
        def broken_facts(*args, **kwargs):
            raise RuntimeError("PRIVATE_FACTS_ERROR")
        monkeypatch.setattr(P.F, "analyze_facts", broken_facts)
    elif stage == "planning":
        async def broken_plan(*args, **kwargs):
            raise P.BackendError("PRIVATE_PLANNER_ERROR")
        monkeypatch.setattr(P, "question_plan", broken_plan)
    app = create_app(backend_factory=lambda request, token: BrokenBackend())
    with TestClient(app) as client:
        response = client.post("/analyze/stream", headers=HEADERS, json=payload)
    events = stream_events(response.text)
    reached = [data["state"] for name, data in events if name == "progress" and data["stage"] == stage]
    assert reached == ["started", "failed"]
    assert [name for name, _ in events].count("error") == 1
    assert events[-1] == ("error", {"message": "Verified analysis failed; no unverified answer was returned."})
    assert "result" not in [name for name, _ in events]
    assert "PRIVATE_" not in response.text


def test_service_forwards_legacy_messages_without_manufacturing_stage_completion(monkeypatch):
    async def legacy_pipeline(text, backend, emit, **kwargs):
        await emit("Legacy pipeline reported a step")
        return {"plan_source": "single_question", "problems": [], "not_covered": [], "warnings": [],
                "disclaimer": P.DISCLAIMER, "version": P.VERSION}

    monkeypatch.setattr(P, "analyze", legacy_pipeline)
    app = create_app(backend_factory=lambda request, token: FakeBackend())
    with TestClient(app) as client:
        response = client.post("/analyze/stream", headers=HEADERS, json=request_payload())
    events = stream_events(response.text)
    assert events[0] == ("progress", {"message": "Legacy pipeline reported a step"})
    assert [name for name, _ in events] == ["progress", "result"]


def test_service_timeout_cancels_pending_work_without_claiming_completed_stages(monkeypatch):
    monkeypatch.setenv("PIPELINE_TIMEOUT_SECONDS", "0.05")
    class PendingBackend(FakeBackend):
        cancelled = False
        async def generate(self, prompt, max_new_tokens=400):
            try:
                await asyncio.Event().wait()
            finally:
                self.cancelled = True

    backend = PendingBackend()
    app = create_app(backend_factory=lambda request, token: backend)
    with TestClient(app) as client:
        response = client.post("/analyze/stream", headers=HEADERS, json=request_payload())
    events = stream_events(response.text)
    assert backend.cancelled
    assert [(name, data.get("state")) for name, data in events] == [("progress", "started"), ("error", None)]


def test_a_real_asgi_disconnect_cancels_pipeline_work_and_does_not_emit_a_late_result():
    async def scenario():
        disconnect = asyncio.Event()
        sent_progress = asyncio.Event()
        frames = []
        received_body = False
        class PendingBackend(FakeBackend):
            cancelled = False
            async def generate(self, prompt, max_new_tokens=400):
                try:
                    await asyncio.Event().wait()
                finally:
                    self.cancelled = True

        backend = PendingBackend()
        app = create_app(backend_factory=lambda request, token: backend)
        async def receive():
            nonlocal received_body
            if not received_body:
                received_body = True
                return {"type": "http.request", "body": json.dumps(request_payload()).encode(), "more_body": False}
            await disconnect.wait()
            return {"type": "http.disconnect"}

        async def send(message):
            frames.append(message)
            if message["type"] == "http.response.body" and b"event: progress" in message.get("body", b""):
                sent_progress.set()

        scope = {"type": "http", "asgi": {"version": "3.0", "spec_version": "2.3"}, "http_version": "1.1",
                 "method": "POST", "scheme": "http", "path": "/analyze/stream", "raw_path": b"/analyze/stream",
                 "root_path": "", "query_string": b"", "client": ("127.0.0.1", 1234), "server": ("service", 80),
                 "headers": [(key.lower().encode(), value.encode()) for key, value in HEADERS.items()] + [(b"content-type", b"application/json")]}
        task = asyncio.create_task(app(scope, receive, send))
        await asyncio.wait_for(sent_progress.wait(), timeout=2)
        disconnect.set()
        await asyncio.wait_for(task, timeout=2)
        body = b"".join(message.get("body", b"") for message in frames).decode()
        assert backend.cancelled
        assert [name for name, _ in stream_events(body)] == ["progress"]
        assert "result" not in body and "event: error" not in body

    asyncio.run(scenario())
