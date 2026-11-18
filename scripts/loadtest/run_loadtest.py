"""Run the staging load test with real Colab work and write a dated report.

The measured stack must be a real staging deployment: real backend, real pipeline
service, real Colab models/encoder/reranker. The orchestrator runs k6 separately
for retrieve and end-to-end analyze at each configured concurrency, while a
concurrent worker submits real Colab chunk jobs and GPU memory is sampled.

No token, case text or model output is written to the report. Missing
prerequisites or failed thresholds produce a dated BLOCKED/FAILED report rather
than an invented passing result.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import threading
import time
import uuid
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any
from urllib.parse import urlsplit

import httpx


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = Path(__file__).with_name("k6-load.js")
DEFAULT_OUTPUT = Path(__file__).with_name("results")
DURATION = re.compile(r"^(?:\d+[smh])+$")
RESULT_LABEL = re.compile(r"^[a-z0-9-]{1,40}$")


@dataclass(frozen=True)
class LoadConfig:
    base_url: str
    workspace_id: str
    token: str
    question: str
    query: str
    duration: str
    vus_levels: tuple[int, ...]
    k6: str
    output_dir: Path
    chunk_jobs: bool
    colab_url: str | None
    colab_key: str | None
    chunk_interval: float
    chunk_timeout: float
    gpu_poll: float
    max_error_rate: float
    max_429_rate: float
    max_503_rate: float
    max_chunk_failure_rate: float
    max_retrieve_p95_ms: float | None
    max_retrieve_p99_ms: float | None
    max_analyze_p95_ms: float | None
    max_analyze_p99_ms: float | None
    allow_missing_gpu_memory: bool
    argv: tuple[str, ...]


class Blocked(RuntimeError):
    pass


def _bounded(value: str) -> float:
    number = float(value)
    if not 0.0 <= number <= 1.0:
        raise argparse.ArgumentTypeError("must be between 0 and 1")
    return number


def _positive(value: str) -> float:
    number = float(value)
    if number <= 0:
        raise argparse.ArgumentTypeError("must be positive")
    return number


def _positive_int_list(value: str) -> tuple[int, ...]:
    try:
        levels = tuple(dict.fromkeys(int(item) for item in value.split(",")))
    except ValueError as error:
        raise argparse.ArgumentTypeError("must be comma-separated positive integers") from error
    if not levels or any(level < 1 or level > 1000 for level in levels):
        raise argparse.ArgumentTypeError("concurrency levels must be 1–1000")
    return levels


def _url(value: str | None, label: str) -> str:
    if not value:
        raise Blocked(f"{label} is required.")
    parsed = urlsplit(value)
    if (parsed.scheme not in {"http", "https"} or not parsed.hostname or parsed.username
            or parsed.password or parsed.query or parsed.fragment):
        raise Blocked(f"{label} must be an HTTP(S) URL without credentials, query or fragment.")
    return value.rstrip("/")


def _token() -> str:
    path = os.environ.get("LOADTEST_FIREBASE_TOKEN_FILE")
    if path:
        try:
            token = Path(path).read_text(encoding="utf-8").strip()
        except (OSError, UnicodeError) as error:
            raise Blocked(f"Firebase token file could not be read ({type(error).__name__}).") from error
    else:
        token = os.environ.get("EVAL_FIREBASE_TOKEN", "").strip()
    if not token:
        raise Blocked("A verified-user Firebase token is required via EVAL_FIREBASE_TOKEN or LOADTEST_FIREBASE_TOKEN_FILE.")
    if any(character.isspace() for character in token):
        raise Blocked("The Firebase token contains whitespace.")
    return token


def _workspace() -> str:
    value = os.environ.get("LOADTEST_WORKSPACE_ID", os.environ.get("EVAL_WORKSPACE_ID", "")).strip()
    if not value:
        raise Blocked("LOADTEST_WORKSPACE_ID is required.")
    try:
        return str(uuid.UUID(value))
    except ValueError as error:
        raise Blocked("LOADTEST_WORKSPACE_ID must be a UUID.") from error


def parse_config(argv: list[str]) -> LoadConfig:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--duration", default=os.environ.get("LOADTEST_DURATION", "2m"))
    parser.add_argument("--vus", type=_positive_int_list,
                        default=os.environ.get("LOADTEST_VUS", "1,3,10"))
    parser.add_argument("--k6", default=os.environ.get("LOADTEST_K6", "k6"))
    parser.add_argument("--output-dir", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--no-chunk-jobs", action="store_true")
    parser.add_argument("--chunk-interval", type=_positive,
                        default=float(os.environ.get("LOADTEST_CHUNK_INTERVAL_SECONDS", "15")))
    parser.add_argument("--chunk-timeout", type=_positive,
                        default=float(os.environ.get("LOADTEST_CHUNK_TIMEOUT_SECONDS", "120")))
    parser.add_argument("--gpu-poll", type=_positive,
                        default=float(os.environ.get("LOADTEST_GPU_POLL_SECONDS", "2")))
    parser.add_argument("--max-error-rate", type=_bounded,
                        default=float(os.environ.get("LOADTEST_MAX_ERROR_RATE", "0.01")))
    parser.add_argument("--max-429-rate", type=_bounded,
                        default=float(os.environ.get("LOADTEST_MAX_429_RATE", "0")))
    parser.add_argument("--max-503-rate", type=_bounded,
                        default=float(os.environ.get("LOADTEST_MAX_503_RATE", "0")))
    parser.add_argument("--max-chunk-failure-rate", type=_bounded,
                        default=float(os.environ.get("LOADTEST_MAX_CHUNK_FAILURE_RATE", "0")))
    parser.add_argument("--max-retrieve-p95-ms", type=float,
                        default=float(os.environ["LOADTEST_MAX_RETRIEVE_P95_MS"])
                        if os.environ.get("LOADTEST_MAX_RETRIEVE_P95_MS") else None)
    parser.add_argument("--max-retrieve-p99-ms", type=float,
                        default=float(os.environ["LOADTEST_MAX_RETRIEVE_P99_MS"])
                        if os.environ.get("LOADTEST_MAX_RETRIEVE_P99_MS") else None)
    parser.add_argument("--max-analyze-p95-ms", type=float,
                        default=float(os.environ["LOADTEST_MAX_ANALYZE_P95_MS"])
                        if os.environ.get("LOADTEST_MAX_ANALYZE_P95_MS") else None)
    parser.add_argument("--max-analyze-p99-ms", type=float,
                        default=float(os.environ["LOADTEST_MAX_ANALYZE_P99_MS"])
                        if os.environ.get("LOADTEST_MAX_ANALYZE_P99_MS") else None)
    parser.add_argument("--allow-missing-gpu-memory", action="store_true")
    args = parser.parse_args(argv)

    if not DURATION.fullmatch(args.duration):
        raise Blocked("--duration must be a k6 duration such as 60s, 2m or 1h.")
    base_url = _url(os.environ.get("LOADTEST_BASE_URL"), "LOADTEST_BASE_URL")
    question = os.environ.get("LOADTEST_QUESTION", "").strip()
    if not question:
        raise Blocked("LOADTEST_QUESTION is required (a non-identifying, clinician-approved guideline question).")
    query = os.environ.get("LOADTEST_QUERY", question).strip()
    if len(question) > 100000:
        raise Blocked("LOADTEST_QUESTION exceeds the analysis limit.")
    if not 1 <= len(query) <= 4000:
        raise Blocked("LOADTEST_QUERY must contain 1–4000 characters.")
    token = _token()
    workspace = _workspace()
    chunk_jobs = not args.no_chunk_jobs
    colab_url = None
    colab_key = None
    if chunk_jobs:
        colab_url = _url(os.environ.get("LOADTEST_COLAB_URL"), "LOADTEST_COLAB_URL")
        colab_key = os.environ.get("LOADTEST_COLAB_KEY", "").strip()
        if not colab_key:
            raise Blocked("LOADTEST_COLAB_KEY is required for concurrent real chunk jobs.")
        if any(character.isspace() for character in colab_key):
            raise Blocked("LOADTEST_COLAB_KEY contains whitespace.")
    levels = args.vus if isinstance(args.vus, tuple) else _positive_int_list(args.vus)
    k6 = args.k6
    return LoadConfig(
        base_url=base_url, workspace_id=workspace, token=token,
        question=question, query=query, duration=args.duration,
        vus_levels=levels, k6=k6, output_dir=args.output_dir,
        chunk_jobs=chunk_jobs, colab_url=colab_url, colab_key=colab_key,
        chunk_interval=args.chunk_interval, chunk_timeout=args.chunk_timeout,
        gpu_poll=args.gpu_poll,
        max_error_rate=args.max_error_rate, max_429_rate=args.max_429_rate,
        max_503_rate=args.max_503_rate, max_chunk_failure_rate=args.max_chunk_failure_rate,
        max_retrieve_p95_ms=args.max_retrieve_p95_ms,
        max_retrieve_p99_ms=args.max_retrieve_p99_ms,
        max_analyze_p95_ms=args.max_analyze_p95_ms,
        max_analyze_p99_ms=args.max_analyze_p99_ms,
        allow_missing_gpu_memory=args.allow_missing_gpu_memory,
        argv=tuple(argv),
    )


def create_run_dir(root: Path, label: str) -> Path:
    if not RESULT_LABEL.fullmatch(label):
        raise ValueError("Result label must use lowercase letters, digits and hyphens.")
    stamp = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H-%M-%SZ")
    root.mkdir(parents=True, exist_ok=True)
    for attempt in range(1000):
        name = f"{stamp}-{label}" + ("" if attempt == 0 else f"-{attempt:02d}")
        candidate = root / name
        try:
            candidate.mkdir()
            return candidate
        except FileExistsError:
            continue
    raise RuntimeError("Could not allocate a new load-test result directory.")


def _write_new(path: Path, text: str) -> None:
    with path.open("x", encoding="utf-8", newline="\n") as handle:
        handle.write(text)


def _commands(config: LoadConfig | None, argv: tuple[str, ...]) -> list[str]:
    lines = [" ".join([sys.executable, "scripts/loadtest/run_loadtest.py", *argv])]
    levels = config.vus_levels if config else (1, 3, 10)
    for mode in ("retrieve", "analyze"):
        for vus in levels:
            lines.append(
                f"MODE={mode} VUS={vus} DURATION={config.duration if config else '2m'} "
                f"BASE_URL={config.base_url if config else '<staging>'} "
                f"WORKSPACE_ID={config.workspace_id if config else '<workspace>'} "
                f"k6 run scripts/loadtest/k6-load.js"
            )
    lines.append("# EVAL_FIREBASE_TOKEN and LOADTEST_COLAB_KEY are supplied only through the environment; values are never recorded.")
    if config and config.chunk_jobs:
        lines.append("# A concurrent in-process worker submits real Colab /chunk/jobs and polls them while k6 runs.")
    return lines


def _write_commands(run_dir: Path, config: LoadConfig | None, argv: tuple[str, ...]) -> list[str]:
    lines = _commands(config, argv)
    _write_new(run_dir / "commands.txt", "\n".join(lines) + "\n")
    return lines


def _fingerprint(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def _safe_error(error: Exception) -> str:
    return re.sub(r"[\x00-\x1f\x7f]", " ", f"{type(error).__name__}: {error}")[:300]


def _auth(config: LoadConfig) -> dict[str, str]:
    return {"Authorization": f"Bearer {config.token}", "Cache-Control": "no-store"}


def _gpu_evidence(model: Any) -> dict[str, Any] | None:
    if not isinstance(model, dict):
        return None
    gpu = model.get("gpu")
    if not isinstance(gpu, dict):
        return None
    evidence = {
        key: gpu.get(key) for key in (
            "available", "name", "device", "totalMemoryBytes", "usedMemoryBytes",
            "allocatedMemoryBytes", "peakAllocatedBytes",
        ) if key in gpu
    }
    return evidence or None


def preflight(config: LoadConfig) -> tuple[dict[str, Any], list[str]]:
    details: dict[str, Any] = {
        "questionSha256": _fingerprint(config.question),
        "questionLength": len(config.question),
        "querySha256": _fingerprint(config.query),
        "queryLength": len(config.query),
    }
    reasons: list[str] = []
    with httpx.Client(timeout=30.0, follow_redirects=False) as client:
        try:
            response = client.get(f"{config.base_url}/health")
            details["backendHealthStatus"] = response.status_code
            if response.status_code != 200:
                reasons.append("Backend /health did not return HTTP 200.")
                return details, reasons
        except Exception as error:  # noqa: BLE001
            reasons.append("Backend /health could not be reached: " + _safe_error(error))
            return details, reasons

        try:
            response = client.get(f"{config.base_url}/api/operations/colab", headers=_auth(config))
            details["modelEvidenceStatus"] = response.status_code
            if response.status_code == 200:
                payload = response.json()
                model = payload.get("model") if isinstance(payload, dict) else None
                model_evidence = model if isinstance(model, dict) else None
                details["model"] = {
                    "ready": model_evidence.get("ready") if model_evidence else None,
                    "model": model_evidence.get("model") if model_evidence else None,
                    "embeddingModel": model_evidence.get("embeddingModel") if model_evidence else None,
                    "embeddingDimensions": model_evidence.get("embeddingDimensions") if model_evidence else None,
                    "gpu": _gpu_evidence(model_evidence),
                }
                if not (model_evidence and model_evidence.get("ready")):
                    details["modelWarning"] = "The operations probe did not report a ready model runtime; the analyze preflight below is authoritative."
            else:
                details["modelWarning"] = f"The operations probe returned HTTP {response.status_code}; direct Colab readiness or the analyze preflight must establish model evidence."
        except Exception as error:  # noqa: BLE001
            details["modelError"] = _safe_error(error)
            details["modelWarning"] = "The operations probe could not be read; direct Colab readiness or the analyze preflight must establish model evidence."

        try:
            response = client.get(f"{config.base_url}/api/workspaces/{config.workspace_id}/config/published",
                                  headers=_auth(config))
            details["publishedConfigStatus"] = response.status_code
            if response.status_code != 200:
                reasons.append("The workspace published configuration is unavailable.")
                return details, reasons
        except Exception as error:  # noqa: BLE001
            reasons.append("The workspace published configuration could not be read: " + _safe_error(error))
            return details, reasons

        try:
            response = client.post(
                f"{config.base_url}/api/workspaces/{config.workspace_id}/analyze/retrieve",
                headers=_auth(config), json={"query": config.query}, timeout=120.0,
            )
            details["retrievePreflightStatus"] = response.status_code
            if response.status_code != 200:
                reasons.append(f"The retrieval preflight returned HTTP {response.status_code}; no load was generated.")
                return details, reasons
            body = response.json()
            chunks = body.get("chunks") if isinstance(body, dict) else None
            if not isinstance(chunks, list) or not chunks:
                reasons.append("The retrieval preflight returned no indexed chunks for LOADTEST_QUERY.")
                return details, reasons
            details["retrievePreflightChunks"] = len(chunks)
            details["retrievePreflightScored"] = sum(
                1 for chunk in chunks
                if isinstance(chunk, dict) and isinstance(chunk.get("rerank_score"), (int, float))
            )
        except Exception as error:  # noqa: BLE001
            reasons.append("The retrieval preflight failed: " + _safe_error(error))
            return details, reasons

        try:
            started = time.perf_counter()
            response = client.post(
                f"{config.base_url}/api/workspaces/{config.workspace_id}/analyze",
                headers=_auth(config), json={"message": config.question}, timeout=300.0,
            )
            details["warmupAnalyzeStatus"] = response.status_code
            details["warmupAnalyzeMilliseconds"] = round((time.perf_counter() - started) * 1000.0, 3)
            if response.status_code != 200:
                reasons.append(f"The end-to-end analyze preflight returned HTTP {response.status_code}.")
                return details, reasons
            body = response.json()
            if not isinstance(body, dict) or not isinstance(body.get("problems"), list):
                reasons.append("The end-to-end analyze preflight returned an invalid verified-result shape.")
                return details, reasons
        except Exception as error:  # noqa: BLE001
            reasons.append("The end-to-end analyze preflight failed: " + _safe_error(error))
            return details, reasons

    if config.chunk_jobs:
        with httpx.Client(timeout=20.0, follow_redirects=False) as client:
            try:
                response = client.get(f"{config.colab_url}/ready", headers={"X-Colab-Key": config.colab_key})
                details["colabReadyStatus"] = response.status_code
                if response.status_code != 200:
                    reasons.append("The real Colab runtime is not ready for chunk jobs.")
                    return details, reasons
                body = response.json()
                details["colab"] = {
                    "ready": body.get("ready"),
                    "model": body.get("model"),
                    "embeddingModel": body.get("embeddingModel"),
                    "gpu": _gpu_evidence(body),
                }
                if not details["colab"].get("gpu"):
                    details["gpuWarning"] = "Colab readiness did not return GPU memory fields."
            except Exception as error:  # noqa: BLE001
                reasons.append("The real Colab chunk service could not be verified: " + _safe_error(error))
                return details, reasons

    gpu = (details.get("colab") or {}).get("gpu") or (details.get("model") or {}).get("gpu")
    if not gpu and not config.allow_missing_gpu_memory:
        reasons.append(
            "GPU memory evidence is unavailable. Use a verified-user token for /api/operations/colab or enable "
            "chunk jobs so the direct Colab readiness endpoint supplies it; otherwise pass --allow-missing-gpu-memory."
        )
    details["gpuEvidence"] = gpu
    return details, reasons


class GpuSampler(threading.Thread):
    def __init__(self, config: LoadConfig, stop: threading.Event) -> None:
        super().__init__(name="gpu-sampler", daemon=True)
        self.config = config
        self.stop = stop
        self.samples: list[dict[str, Any]] = []

    def _sample(self) -> None:
        timestamp = datetime.now(timezone.utc).isoformat()
        gpu = None
        source = None
        try:
            with httpx.Client(timeout=10.0, follow_redirects=False) as client:
                response = client.get(f"{self.config.base_url}/api/operations/colab", headers=_auth(self.config))
                if response.status_code == 200:
                    body = response.json()
                    gpu = _gpu_evidence(body.get("model") if isinstance(body, dict) else None)
                    source = "operations"
                if not gpu and self.config.chunk_jobs:
                    response = client.get(f"{self.config.colab_url}/ready",
                                          headers={"X-Colab-Key": self.config.colab_key or ""})
                    if response.status_code == 200:
                        gpu = _gpu_evidence(response.json())
                        source = "colab"
        except Exception as error:  # noqa: BLE001
            self.samples.append({"at": timestamp, "source": source, "gpu": None, "error": _safe_error(error)})
            return
        self.samples.append({"at": timestamp, "source": source, "gpu": gpu})

    def run(self) -> None:
        while not self.stop.is_set():
            self._sample()
            self.stop.wait(self.config.gpu_poll)

    def summary(self) -> dict[str, Any]:
        valid = [sample for sample in self.samples if isinstance(sample.get("gpu"), dict)]
        used = [sample["gpu"].get("usedMemoryBytes") for sample in valid
                if isinstance(sample["gpu"].get("usedMemoryBytes"), int)]
        peak = [sample["gpu"].get("peakAllocatedBytes") for sample in valid
                if isinstance(sample["gpu"].get("peakAllocatedBytes"), int)]
        allocated = [sample["gpu"].get("allocatedMemoryBytes") for sample in valid
                     if isinstance(sample["gpu"].get("allocatedMemoryBytes"), int)]
        total = next((sample["gpu"].get("totalMemoryBytes") for sample in valid
                      if isinstance(sample["gpu"].get("totalMemoryBytes"), int)), None)
        return {
            "samples": len(self.samples),
            "samplesWithGpu": len(valid),
            "baselineUsedMemoryBytes": used[0] if used else None,
            "maxUsedMemoryBytes": max(used) if used else None,
            "maxAllocatedMemoryBytes": max(allocated) if allocated else None,
            "maxPeakAllocatedBytes": max(peak) if peak else None,
            "totalMemoryBytes": total,
            "unavailable": not valid or not used,
        }


CHUNK_TEXT = (
    "--- Page 1 ---\n"
    "SYNTHETIC LOAD TEST ONLY. This page contains no patient data. "
    "A synthetic monitoring recommendation is repeated with neutral filler text for GPU chunking load. "
    "The synthetic recommendation asks for a review interval and a follow-up assessment. "
    "Additional synthetic sentences describe generic monitoring without clinical instructions.\n"
    "--- Page 2 ---\n"
    "SYNTHETIC LOAD TEST ONLY. This page contains no patient data. "
    "A second synthetic recommendation asks for a documented review after the synthetic assessment. "
    "The text is intentionally generic and must not be used as clinical guidance. "
    "More neutral filler keeps the chunking workload realistic without adding patient information.\n"
)


class ChunkDriver(threading.Thread):
    def __init__(self, config: LoadConfig, stop: threading.Event) -> None:
        super().__init__(name="chunk-jobs", daemon=True)
        self.config = config
        self.stop = stop
        self.jobs: list[dict[str, Any]] = []

    def _submit(self, client: httpx.Client) -> None:
        started = time.perf_counter()
        record: dict[str, Any] = {"startedAt": datetime.now(timezone.utc).isoformat()}
        try:
            response = client.post(
                f"{self.config.colab_url}/chunk/jobs",
                headers={"X-Colab-Key": self.config.colab_key or "", "Cache-Control": "no-store"},
                json={"text": CHUNK_TEXT, "guidance": "Synthetic load test; keep text verbatim.",
                      "max_tokens": 400, "pages": 2},
            )
            record["submitStatus"] = response.status_code
            if response.status_code == 429:
                record["status429"] = True
            if response.status_code == 503:
                record["status503"] = True
            if response.status_code not in (200, 202):
                record["status"] = "submit-failed"
                record["durationMs"] = round((time.perf_counter() - started) * 1000.0, 3)
                self.jobs.append(record)
                return
            job_id = response.json().get("id")
            if not job_id:
                record["status"] = "submit-failed"
                record["durationMs"] = round((time.perf_counter() - started) * 1000.0, 3)
                self.jobs.append(record)
                return
            deadline = time.monotonic() + self.config.chunk_timeout
            final = None
            while time.monotonic() < deadline and not self.stop.is_set():
                polled = client.get(f"{self.config.colab_url}/chunk/jobs/{job_id}",
                                    headers={"X-Colab-Key": self.config.colab_key or ""})
                if polled.status_code != 200:
                    final = {"status": f"poll-http-{polled.status_code}"}
                    break
                final = polled.json()
                if final.get("status") in ("completed", "failed"):
                    break
                time.sleep(0.25)
            record["status"] = (final or {}).get("status", "timeout")
            result = (final or {}).get("result")
            if isinstance(result, dict):
                chunks = result.get("chunks")
                record["chunks"] = len(chunks) if isinstance(chunks, list) else None
        except Exception as error:  # noqa: BLE001
            record["status"] = "error"
            record["error"] = _safe_error(error)
        record["durationMs"] = round((time.perf_counter() - started) * 1000.0, 3)
        self.jobs.append(record)

    def run(self) -> None:
        with httpx.Client(timeout=30.0, follow_redirects=False) as client:
            while not self.stop.is_set():
                self._submit(client)
                self.stop.wait(self.config.chunk_interval)

    def summary(self) -> dict[str, Any]:
        completed = [job for job in self.jobs if job.get("status") == "completed"]
        failed = [job for job in self.jobs if job.get("status") != "completed"]
        durations = [job["durationMs"] for job in completed if isinstance(job.get("durationMs"), (int, float))]
        total = len(self.jobs)
        return {
            "submitted": total,
            "completed": len(completed),
            "failed": len(failed),
            "failureRate": round(len(failed) / total, 6) if total else 0.0,
            "status429": sum(1 for job in self.jobs if job.get("status429")),
            "status503": sum(1 for job in self.jobs if job.get("status503")),
            "p50DurationMs": _percentile(durations, 0.50),
            "p95DurationMs": _percentile(durations, 0.95),
            "jobs": self.jobs,
        }


def _percentile(values: list[float], fraction: float) -> float | None:
    if not values:
        return None
    ordered = sorted(values)
    index = max(0, min(len(ordered) - 1, int(len(ordered) * fraction + 0.999999) - 1))
    return round(ordered[index], 3)


def _duration_seconds(value: str) -> int:
    units = {"s": 1, "m": 60, "h": 3600}
    total = 0
    for number, unit in re.findall(r"(\d+)([smh])", value):
        total += int(number) * units[unit]
    return total


def parse_k6_summary(summary: dict[str, Any], mode: str) -> dict[str, Any]:
    metrics = summary.get("metrics", {}) if isinstance(summary, dict) else {}

    def values(name: str) -> dict[str, Any]:
        metric = metrics.get(name)
        value = metric.get("values") if isinstance(metric, dict) else None
        return value if isinstance(value, dict) else {}

    trend = values("retrieve_duration" if mode == "retrieve" else "analyze_duration")
    http = values("http_reqs")
    failed = values("http_req_failed")
    requests = int(http.get("count", 0) or 0)
    status429 = int(values("status_429").get("count", 0) or 0)
    status503 = int(values("status_503").get("count", 0) or 0)
    return {
        "requests": requests,
        "errorRate": float(failed.get("rate", 0.0) or 0.0),
        "status429": status429,
        "status503": status503,
        "status429Rate": round(status429 / requests, 6) if requests else 0.0,
        "status503Rate": round(status503 / requests, 6) if requests else 0.0,
        "otherErrors": int(values("status_other_error").get("count", 0) or 0),
        "p50Milliseconds": trend.get("med"),
        "p95Milliseconds": trend.get("p(95)"),
        "p99Milliseconds": trend.get("p(99)"),
        "maxMilliseconds": trend.get("max"),
    }


def run_k6(config: LoadConfig, mode: str, vus: int, run_dir: Path) -> dict[str, Any]:
    summary_path = run_dir / f"k6-{mode}-{vus}vu.json"
    log_path = run_dir / f"k6-{mode}-{vus}vu.log"
    environment = os.environ.copy()
    environment.update({
        "MODE": mode,
        "VUS": str(vus),
        "DURATION": config.duration,
        "BASE_URL": config.base_url,
        "WORKSPACE_ID": config.workspace_id,
        "EVAL_FIREBASE_TOKEN": config.token,
        "EVAL_QUERY": config.query,
        "EVAL_QUESTION": config.question,
        "SUMMARY_PATH": str(summary_path),
        "REQUEST_TIMEOUT": "300s",
    })
    command = [config.k6, "run", "--quiet", str(SCRIPT)]
    try:
        completed = subprocess.run(
            command, cwd=ROOT, env=environment, capture_output=True, text=True,
            timeout=_duration_seconds(config.duration) + 180, check=False,
        )
        output = (completed.stdout or "") + (completed.stderr or "")
        _write_new(log_path, output)
        return_code = completed.returncode
        error = None if return_code == 0 else f"k6 exited with code {return_code}"
    except Exception as failure:  # noqa: BLE001
        _write_new(log_path, _safe_error(failure) + "\n")
        return_code = None
        error = _safe_error(failure)
    row: dict[str, Any] = {
        "mode": mode, "vus": vus, "returnCode": return_code, "error": error,
        "requests": 0, "errorRate": 0.0, "status429": 0, "status503": 0,
        "status429Rate": 0.0, "status503Rate": 0.0,
        "otherErrors": 0, "p50Milliseconds": None, "p95Milliseconds": None,
        "p99Milliseconds": None, "maxMilliseconds": None,
    }
    if not summary_path.is_file():
        row["error"] = error or "k6 produced no summary artifact"
        return row
    try:
        row.update(parse_k6_summary(json.loads(summary_path.read_text(encoding="utf-8")), mode))
    except (OSError, ValueError, TypeError) as failure:
        row["error"] = _safe_error(failure)
    return row


def evaluate_thresholds(config: LoadConfig, rows: list[dict[str, Any]],
                        chunk_summary: dict[str, Any], gpu_summary: dict[str, Any]) -> list[dict[str, Any]]:
    checks: list[dict[str, Any]] = []
    def add(name: str, comparator: str, limit: Any, actual: Any) -> None:
        if limit is None:
            return
        passed = actual >= limit if comparator == ">=" else actual <= limit
        checks.append({"metric": name, "comparator": comparator, "limit": limit,
                       "actual": actual, "passed": bool(passed)})
    for row in rows:
        label = f"{row['mode']}@{row['vus']}vu"
        if row.get("error"):
            checks.append({"metric": f"{label}.run", "comparator": "==", "limit": "completed",
                           "actual": row["error"], "passed": False})
            continue
        requests = row.get("requests") or 0
        add(f"{label}.errorRate", "<=", config.max_error_rate, row.get("errorRate", 0.0))
        if requests == 0:
            checks.append({"metric": f"{label}.requests", "comparator": ">", "limit": 0,
                           "actual": 0, "passed": False})
        add(f"{label}.status429Rate", "<=", config.max_429_rate, (row["status429"] / requests) if requests else 1.0)
        add(f"{label}.status503Rate", "<=", config.max_503_rate, (row["status503"] / requests) if requests else 1.0)
        if row["mode"] == "retrieve":
            add(f"{label}.p95Milliseconds", "<=", config.max_retrieve_p95_ms, row.get("p95Milliseconds"))
            add(f"{label}.p99Milliseconds", "<=", config.max_retrieve_p99_ms, row.get("p99Milliseconds"))
        else:
            add(f"{label}.p95Milliseconds", "<=", config.max_analyze_p95_ms, row.get("p95Milliseconds"))
            add(f"{label}.p99Milliseconds", "<=", config.max_analyze_p99_ms, row.get("p99Milliseconds"))
    if config.chunk_jobs:
        if chunk_summary.get("submitted", 0) == 0:
            checks.append({"metric": "chunkJobs.submitted", "comparator": ">", "limit": 0,
                           "actual": 0, "passed": False})
        add("chunkJobs.failureRate", "<=", config.max_chunk_failure_rate,
            chunk_summary.get("failureRate", 0.0))
    if gpu_summary.get("unavailable") and not config.allow_missing_gpu_memory:
        checks.append({"metric": "gpuMemory", "comparator": "==", "limit": "available",
                       "actual": "unavailable", "passed": False})
    return checks


def _markdown(payload: dict[str, Any]) -> str:
    lines = [
        f"# Staging load test — {payload['generatedAtUtc']}",
        "",
        f"**Status:** {payload['status'].upper()}",
        f"**Measured path:** real staging backend → real pipeline service → real Colab models/encoder/reranker",
        "",
        "## Staging evidence",
        "",
        f"- Backend: `{payload['target'].get('backendUrl')}`",
        f"- Workspace: `{payload['target'].get('workspaceId')}`",
        f"- Question SHA-256: `{payload['target'].get('questionSha256')}` (text not recorded)",
        f"- Query SHA-256: `{payload['target'].get('querySha256')}` (text not recorded)",
        f"- Duration per run: {payload['target'].get('duration')}",
        f"- Concurrency levels: {', '.join(str(v) for v in payload['target'].get('vusLevels', []))}",
        f"- Chunk jobs concurrent: {'yes' if payload['target'].get('chunkJobs') else 'no'}",
        "",
        "## Preflight",
        "",
        f"```json\n{json.dumps(payload.get('preflight'), indent=2, sort_keys=True)}\n```",
        "",
    ]
    if payload["status"] == "blocked":
        lines += ["## Blocked", ""]
        lines += [f"- {reason}" for reason in payload.get("blockedReasons", [])]
        lines += ["", "No load was generated. This is a blocked run, not a passing capacity test.", ""]
    else:
        lines += [
            "## Results",
            "",
            "| Mode | VUs | Requests | Error rate | 429 count (rate) | 503 count (rate) | Other errors | p50 ms | p95 ms | p99 ms | Max ms |",
            "| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |",
        ]
        for row in payload["results"]:
            lines.append(
                f"| {row['mode']} | {row['vus']} | {row['requests']} | {row.get('errorRate', 0.0):.4f} | "
                f"{row.get('status429', 0)} ({row.get('status429Rate', 0.0):.2%}) | "
                f"{row.get('status503', 0)} ({row.get('status503Rate', 0.0):.2%}) | {row.get('otherErrors', 0)} | "
                f"{_fmt(row.get('p50Milliseconds'))} | {_fmt(row.get('p95Milliseconds'))} | "
                f"{_fmt(row.get('p99Milliseconds'))} | {_fmt(row.get('maxMilliseconds'))} |"
            )
        lines += [
            "",
            "## Concurrent chunk jobs",
            "",
            f"```json\n{json.dumps(payload.get('chunkJobs'), indent=2, sort_keys=True)}\n```",
            "",
            "## GPU memory",
            "",
            f"```json\n{json.dumps(payload.get('gpuMemory'), indent=2, sort_keys=True)}\n```",
            "",
            "## Thresholds",
            "",
            "| Check | Limit | Actual | Result |",
            "| --- | --- | --- | --- |",
        ]
        for check in payload.get("thresholdResults", []):
            lines.append(f"| {check['metric']} {check['comparator']} | {check['limit']} | {check['actual']} | "
                         f"{'PASS' if check['passed'] else 'FAIL'} |")
    lines += [
        "",
        "## Commands used",
        "",
        "```text",
        *payload["commands"],
        "```",
        "",
        "## Limitations",
        "",
        "- This is staging behavior, not a production capacity guarantee.",
        "- p99 with a short run or small request count is sensitive to single outliers.",
        "- Chunk jobs share the same GPU and model service; their effect is included, not isolated.",
        "- 429/503 rates are recorded, not retried away; a rate limiter or dependency outage is a finding.",
        "- Reports contain no question text, case text, generated answer text or token.",
        "",
    ]
    return "\n".join(lines)


def _fmt(value: Any) -> str:
    if value is None:
        return "—"
    return f"{value:.1f}" if isinstance(value, float) else str(value)


def write_report(run_dir: Path, payload: dict[str, Any]) -> None:
    _write_new(run_dir / "report.md", _markdown(payload))
    _write_new(run_dir / "summary.json", json.dumps(payload, indent=2, sort_keys=True) + "\n")


def _blocked(config: LoadConfig | None, reasons: list[str], preflight_details: dict[str, Any] | None,
             argv: tuple[str, ...], output_dir: Path) -> Path:
    run_dir = create_run_dir(output_dir, "loadtest-blocked")
    commands = _write_commands(run_dir, config, argv)
    payload = {
        "schemaVersion": "loadtest/1.0",
        "generatedAtUtc": datetime.now(timezone.utc).isoformat(),
        "status": "blocked",
        "exitCode": 4,
        "target": {
            "backendUrl": config.base_url if config else None,
            "workspaceId": config.workspace_id if config else None,
            "questionSha256": _fingerprint(config.question) if config else None,
            "querySha256": _fingerprint(config.query) if config else None,
            "duration": config.duration if config else None,
            "vusLevels": list(config.vus_levels) if config else [],
            "chunkJobs": config.chunk_jobs if config else None,
        },
        "preflight": preflight_details or {},
        "results": [],
        "chunkJobs": None,
        "gpuMemory": None,
        "thresholdResults": [],
        "blockedReasons": reasons,
        "commands": commands,
    }
    write_report(run_dir, payload)
    print(json.dumps({"status": "blocked", "run": str(run_dir), "reasons": reasons}, indent=2))
    return run_dir


def _blocked_in(run_dir: Path, config: LoadConfig, reasons: list[str],
                preflight_details: dict[str, Any] | None, commands: list[str]) -> tuple[Path, int]:
    payload = {
        "schemaVersion": "loadtest/1.0",
        "generatedAtUtc": datetime.now(timezone.utc).isoformat(),
        "status": "blocked",
        "exitCode": 4,
        "target": {
            "backendUrl": config.base_url,
            "workspaceId": config.workspace_id,
            "questionSha256": _fingerprint(config.question),
            "querySha256": _fingerprint(config.query),
            "duration": config.duration,
            "vusLevels": list(config.vus_levels),
            "chunkJobs": config.chunk_jobs,
        },
        "preflight": preflight_details or {},
        "results": [],
        "chunkJobs": None,
        "gpuMemory": None,
        "thresholdResults": [],
        "blockedReasons": reasons,
        "commands": commands,
    }
    write_report(run_dir, payload)
    print(json.dumps({"status": "blocked", "run": str(run_dir), "reasons": reasons}, indent=2))
    return run_dir, 4


def run(config: LoadConfig) -> tuple[Path, int]:
    if not (Path(config.k6).is_file() or shutil.which(config.k6)):
        reason = (
            "k6 is not installed or not on PATH. Install k6 from https://k6.io/docs/get-started/installation/ "
            "or set LOADTEST_K6 to its executable; no substitute load generator was run."
        )
        return _blocked(config, [reason], None, config.argv, config.output_dir), 4
    run_dir = create_run_dir(config.output_dir, "loadtest")
    commands = _write_commands(run_dir, config, config.argv)
    preflight_details, reasons = preflight(config)
    if reasons:
        return _blocked_in(run_dir, config, reasons, preflight_details, commands)
    stop = threading.Event()
    gpu_sampler = GpuSampler(config, stop) if config.chunk_jobs or (preflight_details.get("model") or {}).get("gpu") else None
    chunk_driver = ChunkDriver(config, stop) if config.chunk_jobs else None
    if gpu_sampler is not None:
        gpu_sampler.start()
    if chunk_driver is not None:
        chunk_driver.start()
    try:
        results = []
        for mode in ("retrieve", "analyze"):
            for vus in config.vus_levels:
                results.append(run_k6(config, mode, vus, run_dir))
    finally:
        stop.set()
        if chunk_driver is not None:
            chunk_driver.join(timeout=config.chunk_timeout + 30)
        if gpu_sampler is not None:
            gpu_sampler.join(timeout=30)
    chunk_summary = chunk_driver.summary() if chunk_driver is not None else {
        "submitted": 0, "completed": 0, "failed": 0, "failureRate": 0.0,
        "status429": 0, "status503": 0, "p50DurationMs": None, "p95DurationMs": None, "jobs": [],
    }
    gpu_summary = gpu_sampler.summary() if gpu_sampler is not None else {
        "samples": 0, "samplesWithGpu": 0, "baselineUsedMemoryBytes": None,
        "maxUsedMemoryBytes": None, "maxAllocatedMemoryBytes": None,
        "maxPeakAllocatedBytes": None, "totalMemoryBytes": None, "unavailable": True,
    }
    _write_new(run_dir / "gpu-samples.json", json.dumps(gpu_sampler.samples if gpu_sampler else [], indent=2) + "\n")
    _write_new(run_dir / "chunk-jobs.json", json.dumps(chunk_driver.jobs if chunk_driver else [], indent=2) + "\n")
    threshold_results = evaluate_thresholds(config, results, chunk_summary, gpu_summary)
    failed = any(not check["passed"] for check in threshold_results)
    status = "threshold-failure" if failed else "completed"
    exit_code = 1 if failed else 0
    payload = {
        "schemaVersion": "loadtest/1.0",
        "generatedAtUtc": datetime.now(timezone.utc).isoformat(),
        "status": status,
        "exitCode": exit_code,
        "target": {
            "backendUrl": config.base_url,
            "workspaceId": config.workspace_id,
            "questionSha256": _fingerprint(config.question),
            "querySha256": _fingerprint(config.query),
            "duration": config.duration,
            "vusLevels": list(config.vus_levels),
            "chunkJobs": config.chunk_jobs,
        },
        "preflight": preflight_details,
        "results": results,
        "chunkJobs": chunk_summary,
        "gpuMemory": gpu_summary,
        "thresholdResults": threshold_results,
        "blockedReasons": [],
        "commands": commands,
    }
    write_report(run_dir, payload)
    print(json.dumps({
        "status": status,
        "run": str(run_dir),
        "results": results,
        "chunkJobs": chunk_summary,
        "gpuMemory": gpu_summary,
        "thresholdResults": threshold_results,
    }, indent=2))
    return run_dir, exit_code


def main(argv: list[str] | None = None) -> int:
    values = list(sys.argv[1:] if argv is None else argv)
    try:
        config = parse_config(values)
    except (Blocked, ValueError, TypeError) as error:
        output_dir = DEFAULT_OUTPUT
        if "--output-dir" in values:
            try:
                output_dir = Path(values[values.index("--output-dir") + 1])
            except IndexError:
                pass
        reason = str(error) if isinstance(error, Blocked) else f"Invalid load-test configuration: {error}"
        _blocked(None, [reason], None, tuple(values), output_dir)
        return 4
    _, exit_code = run(config)
    return exit_code


if __name__ == "__main__":
    raise SystemExit(main())
