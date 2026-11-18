import asyncio
import json

import httpx
import pytest

import config as Cfg
import facts as F
import pipeline as P


async def noop_emit(message):
    return None


def test_retrieval_and_generation_use_separate_credentials(monkeypatch):
    requests = []

    def handler(request):
        requests.append(request)
        if request.url.path.endswith("retrieve"):
            return httpx.Response(200, json={"context": "NOT_FOUND", "chunks": []})
        return httpx.Response(200, json={"response": "complete", "finish_reason": "stop", "truncated": False})

    client_type = httpx.AsyncClient
    monkeypatch.setattr(P.httpx, "AsyncClient", lambda **kwargs: client_type(
        transport=httpx.MockTransport(handler), **kwargs))
    backend = P.Backend("http://backend", "workspace", firebase_token="forwarded-user-token",
                        generate_url="http://colab/generate", colab_key="colab-test-key")

    async def run():
        await backend.retrieve("focused question")
        await backend.generate("safe prompt")

    asyncio.run(run())
    retrieve, generate = requests
    assert retrieve.headers["Authorization"] == "Bearer forwarded-user-token"
    assert "X-Colab-Key" not in retrieve.headers
    assert generate.headers["X-Colab-Key"] == "colab-test-key"
    assert "Authorization" not in generate.headers


def test_rerank_uses_the_colab_key_and_rejects_invalid_scores(monkeypatch):
    requests = []

    def handler(request):
        requests.append(request)
        return httpx.Response(200, json={"scores": [0.75]})

    client_type = httpx.AsyncClient
    monkeypatch.setattr(P.httpx, "AsyncClient", lambda **kwargs: client_type(
        transport=httpx.MockTransport(handler), **kwargs))
    backend = P.Backend("http://backend", "workspace", generate_url="http://colab/generate",
                        colab_key="colab-test-key")

    assert asyncio.run(backend.rerank("user question", ["verified statement"])) == [0.75]
    request = requests[0]
    assert request.url.path == "/rerank"
    assert request.headers["X-Colab-Key"] == "colab-test-key"
    assert json.loads(request.content) == {"query": "user question", "passages": ["verified statement"]}

    def invalid(request):
        return httpx.Response(200, json={"scores": [1.5]})

    monkeypatch.setattr(P.httpx, "AsyncClient", lambda **kwargs: client_type(
        transport=httpx.MockTransport(invalid), **kwargs))
    with pytest.raises(P.BackendError):
        asyncio.run(backend.rerank("user question", ["verified statement"]))


@pytest.mark.parametrize("metadata", [
    {"truncated": True, "finish_reason": "stop"},
    {"truncated": False, "finish_reason": "length"},
])
def test_truncated_generation_is_never_returned_as_a_complete_draft(monkeypatch, metadata):
    def handler(request):
        return httpx.Response(200, json={"response": "SOURCE: S1\nRECOMMENDATION: unfinished", **metadata})

    client_type = httpx.AsyncClient
    monkeypatch.setattr(P.httpx, "AsyncClient", lambda **kwargs: client_type(
        transport=httpx.MockTransport(handler), **kwargs))
    backend = P.Backend("http://backend", "workspace", generate_url="http://colab/generate")
    with pytest.raises(P.BackendError, match="truncat"):
        asyncio.run(backend.generate("safe prompt"))


def test_request_rules_override_defaults_including_an_empty_approved_rule_list(monkeypatch):
    def forbidden_default_rules():
        raise AssertionError("Default rules must not replace request rules")

    monkeypatch.setattr(F, "load_rules", forbidden_default_rules)
    rule = {
        "id": "workspace-rule", "title": "Workspace medication check", "drug_classes": ["antiplatelet"],
        "status": "approved", "enabled": True,
        "applies_when": {"any": [{"field": "sbp", "op": "<", "value": 90}]},
        "level": "caution", "message": "Review medication", "caveat": "Clinician review",
        "sources": [{"title": "Synthetic test source", "url": "https://example.org/source"}],
    }
    with_rule = F.analyze_facts("BP 80/50 aspirin 75 mg", rules=[rule])
    without_rule = F.analyze_facts("BP 80/50 aspirin 75 mg", rules=[])
    assert [flag["id"] for flag in with_rule["med_flags"]] == ["workspace-rule"]
    assert without_rule["med_flags"] == []


def test_follow_up_uses_resolved_reference_for_drafting_and_topic_verification():
    quote = "Screen pregnant women for hepatitis B during pregnancy."

    class HistoryBackend:
        def __init__(self):
            self.prompts = []

        async def generate(self, prompt, max_new_tokens=400):
            self.prompts.append(prompt)
            if "Rewrite the clinician's message" in prompt:
                return "hepatitis B screening in pregnant women"
            return f"SOURCE: S1\nRECOMMENDATION: {quote}\nQUOTE: {quote}"

        async def retrieve(self, query):
            return {"context": "unused", "chunks": [{
                "society": "WHO", "year": "2024", "status": "final", "section": "Pregnancy screening",
                "page": 1, "text": quote, "rerank_score": 0.99,
            }]}

    history = [{"question": f"OLD_CONTEXT_{i}", "verifiedSummary": "Older verified context"} for i in range(2)]
    history += [{"question": "The mother is pregnant. What screening is recommended?",
                 "verifiedSummary": "Hepatitis B screening in pregnancy", "unverified": "UNVERIFIED_MEMORY_MARKER"}] * 6
    backend = HistoryBackend()
    result = asyncio.run(P.analyze("What about the mother?", backend, noop_emit,
                                  config=Cfg.Config.from_dict(), rules=[], history=history))
    assert result["problems"][0]["items"]
    assert "hepatitis B screening in pregnant women" in backend.prompts[-1]
    assert "Verified conversation context" not in backend.prompts[-1]
    assert all("OLD_CONTEXT_" not in prompt and "UNVERIFIED_MEMORY_MARKER" not in prompt for prompt in backend.prompts)


def test_invalid_draft_is_excluded_in_the_pipeline_contract():
    quote = "Use norepinephrine as the first-line vasopressor in septic shock."

    class UnsafeBackend:
        async def retrieve(self, query):
            return {"context": "unused", "chunks": [{
                "society": "SCCM", "year": "2024", "status": "final", "section": "Shock", "page": 12,
                "text": quote, "rerank_score": 0.95,
            }]}

        async def generate(self, prompt, max_new_tokens=400):
            if "Rewrite the clinician's message" in prompt:
                return "septic shock vasopressor"
            return f"SOURCE: S1\nRECOMMENDATION: Use norepinephrine 999 mg in septic shock.\nQUOTE: {quote}"

    result = asyncio.run(P.analyze("Which vasopressor in septic shock?", UnsafeBackend(), noop_emit))
    problem = result["problems"][0]
    assert problem["items"] == []
    assert problem["unverified"][0]["issues"]
    assert problem["title"] in result["not_covered"]
    assert any("Not covered" in warning for warning in result["warnings"])
