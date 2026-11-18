import asyncio
import copy
import json

import httpx
import pytest

from colab.answer_quality_comparison import run_bundle

MODEL = "Qwen/Qwen3-4B-Instruct-2507"
BUNDLE = {"synthetic":True, "pairs":[{"id":"a-1", "payload":{"prompt":"Synthetic test only.", "temperature":0.0},
    "topic":[], "sources":{"S1":{"id":"S1", "text":"Synthetic marker is GREEN.", "strength":"weak",
    "section":"Synthetic", "label":"Synthetic", "page":"3", "draft":False}}}]}


def run(bundle, ready_model=MODEL, gpu="Tesla T4", output=None):
    calls = []
    def handler(request):
        calls.append(request)
        if request.url.path == "/ready":
            return httpx.Response(200, json={"ready":True,"model":ready_model,"gpu":{"name":gpu}})
        return httpx.Response(200, json=output or {"response":"SOURCE: S1\nRECOMMENDATION: Synthetic marker is GREEN.\nQUOTE: Synthetic marker is GREEN.", "finish_reason":"stop", "truncated":False})
    async def execute():
        async with httpx.AsyncClient(transport=httpx.MockTransport(handler), base_url="http://localhost") as client:
            return await run_bundle(bundle, client, "synthetic-test-key", MODEL, "Tesla T4")
    return asyncio.run(execute()), calls


def test_comparison_keeps_exact_payload_and_only_offline_verification():
    result, calls = run(copy.deepcopy(BUNDLE))
    assert json.loads(calls[1].content) == BUNDLE["pairs"][0]["payload"]
    assert len(calls) == 2 and result["pairs"][0]["diagnostics"]["verified"] == 1
    assert "synthetic-test-key" not in json.dumps(result)


@pytest.mark.parametrize("model,gpu", [("wrong-model","Tesla T4"),(MODEL,"wrong-gpu")])
def test_comparison_requires_same_declared_gpu_and_real_model(model,gpu):
    with pytest.raises(ValueError):
        run(copy.deepcopy(BUNDLE), ready_model=model, gpu=gpu)


def test_comparison_never_verifies_truncated_generation():
    result, _ = run(copy.deepcopy(BUNDLE), output={"response":"PRIVATE_INCOMPLETE", "finish_reason":"length", "truncated":True})
    assert result["pairs"][0]["diagnostics"]["verified"] == 0
    assert result["pairs"][0]["diagnostics"]["reasons"]["truncated"] == 1


def test_comparison_refuses_non_synthetic_bundle():
    with pytest.raises(ValueError):
        run({**copy.deepcopy(BUNDLE), "synthetic":False})
