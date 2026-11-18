"""Run an exact synthetic prompt bundle on the already-running local Colab server.

This never reloads models, modifies configuration, submits test evidence or prints
prompts/drafts/credentials. The operator must first select a model via the default-off
runtime flag. Keep the bundle/results outside the source tree.
"""
import argparse
import asyncio
import hashlib
import json
import os
import sys
from pathlib import Path

if __package__ in (None, ""):
    sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python-backend"))

import httpx

import pipeline as P
from colab.models import GenerateRequest

MODELS = {"Qwen/Qwen3-4B-Instruct-2507", "Qwen/Qwen3-8B-AWQ"}


async def run_bundle(bundle, client, key, expected_model, expected_gpu):
    if expected_model not in MODELS or not key or bundle.get("synthetic") is not True:
        raise ValueError("Explicit synthetic bundle, credentials and supported model required.")
    pairs = bundle.get("pairs")
    if not isinstance(pairs, list) or not 1 <= len(pairs) <= 10:
        raise ValueError("Comparison requires 1..10 synthetic pairs.")
    # Validate the full bundle before any expensive work. No partial malformed run.
    for pair in pairs:
        GenerateRequest.model_validate(pair["payload"])
        if pair.get("id") not in {"a-1", "b-1", "b-2", "b-3", "c-1"} or not isinstance(pair.get("sources"), dict):
            raise ValueError("Invalid synthetic pair.")
    if len({pair["id"] for pair in pairs}) != len(pairs):
        raise ValueError("Duplicate synthetic pairs.")
    headers = {"X-Colab-Key":key}
    ready = await client.get("/ready", headers=headers)
    ready.raise_for_status()
    status = ready.json()
    if status.get("ready") is not True or status.get("model") != expected_model or status.get("gpu", {}).get("name") != expected_gpu:
        raise ValueError("Genuine model/GPU readiness does not match this comparison.")
    result = {"format":1, "synthetic":True, "model":expected_model, "gpu":expected_gpu,
              "embeddingModel":status.get("embeddingModel"), "pairs":[]}
    for pair in pairs:
        # One submission only; an unknown/lost response must never be replayed automatically.
        response = await client.post("/generate", json=pair["payload"], headers=headers)
        response.raise_for_status()
        output = response.json()
        if not isinstance(output.get("response"), str):
            raise ValueError("Invalid generation response.")
        if output.get("truncated") or output.get("finish_reason") in {"length", "max_tokens"}:
            counters = P.empty_diagnostics()
            counters["drafts"] = 1; counters["reasons"]["truncated"] = 1
        else:
            counters = P.verify_draft(output["response"], pair["sources"], set(pair["topic"]))["diagnostics"]
        payload_hash = hashlib.sha256(json.dumps(pair["payload"], sort_keys=True, separators=(",", ":")).encode()).hexdigest()
        result["pairs"].append({"id":pair["id"], "payloadSha256":payload_hash, "output":output, "diagnostics":counters})
    return result


async def main(args):
    if args.output.exists():
        raise ValueError("Comparison output already exists; choose a new path.")
    bundle = json.loads(args.bundle.read_text(encoding="utf-8"))
    async with httpx.AsyncClient(base_url="http://127.0.0.1:8000", timeout=240, trust_env=False, follow_redirects=False) as client:
        result = await run_bundle(bundle, client, os.getenv("COLAB_API_KEY", ""), args.model, args.gpu)
    with args.output.open("x", encoding="utf-8") as file:
        json.dump(result, file, ensure_ascii=True, indent=2)
    print(json.dumps({"model":result["model"], "gpu":result["gpu"], "pairs":[
        {"id":pair["id"], "payloadSha256":pair["payloadSha256"], "diagnostics":pair["diagnostics"]} for pair in result["pairs"]]}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bundle", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--model", choices=sorted(MODELS), required=True)
    parser.add_argument("--gpu", required=True)
    try:
        asyncio.run(main(parser.parse_args()))
    except Exception:
        # HTTP errors/validation errors may embed upstream URLs, submitted values or bodies.
        raise SystemExit("Synthetic comparison failed; no result or passing evidence was submitted.") from None
