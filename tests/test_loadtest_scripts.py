import json
import threading
from pathlib import Path

import pytest

from scripts.loadtest import run_loadtest as loadtest


ROOT = Path(__file__).resolve().parents[1]


def config(tmp_path, **overrides):
    values = dict(
        base_url="https://staging.example",
        workspace_id="11111111-1111-1111-1111-111111111111",
        token="secret-token-never-recorded",
        question="synthetic guideline question",
        query="synthetic retrieval query",
        duration="60s",
        vus_levels=(1, 3, 10),
        k6="k6",
        output_dir=tmp_path,
        chunk_jobs=True,
        colab_url="https://colab.example",
        colab_key="colab-secret-never-recorded",
        chunk_interval=1.0,
        chunk_timeout=10.0,
        gpu_poll=1.0,
        max_error_rate=0.01,
        max_429_rate=0.0,
        max_503_rate=0.0,
        max_chunk_failure_rate=0.0,
        max_retrieve_p95_ms=500.0,
        max_retrieve_p99_ms=900.0,
        max_analyze_p95_ms=30000.0,
        max_analyze_p99_ms=60000.0,
        allow_missing_gpu_memory=False,
        argv=(),
    )
    values.update(overrides)
    return loadtest.LoadConfig(**values)


def test_parse_config_requires_explicit_staging_and_credentials(monkeypatch, tmp_path):
    for name in ("LOADTEST_BASE_URL", "LOADTEST_WORKSPACE_ID", "EVAL_FIREBASE_TOKEN",
                 "LOADTEST_FIREBASE_TOKEN_FILE", "LOADTEST_QUESTION", "LOADTEST_COLAB_URL",
                 "LOADTEST_COLAB_KEY"):
        monkeypatch.delenv(name, raising=False)
    with pytest.raises(loadtest.Blocked, match="LOADTEST_BASE_URL"):
        loadtest.parse_config(["--output-dir", str(tmp_path)])


def test_parse_config_accepts_explicit_non_identifying_inputs(monkeypatch, tmp_path):
    monkeypatch.delenv("LOADTEST_FIREBASE_TOKEN_FILE", raising=False)
    monkeypatch.setenv("LOADTEST_BASE_URL", "https://staging.example")
    monkeypatch.setenv("LOADTEST_WORKSPACE_ID", "11111111-1111-1111-1111-111111111111")
    monkeypatch.setenv("EVAL_FIREBASE_TOKEN", "synthetic-token")
    monkeypatch.setenv("LOADTEST_QUESTION", "synthetic guideline question")
    monkeypatch.setenv("LOADTEST_COLAB_URL", "https://colab.example")
    monkeypatch.setenv("LOADTEST_COLAB_KEY", "synthetic-colab-key")
    monkeypatch.setenv("LOADTEST_VUS", "1,3,10")
    parsed = loadtest.parse_config(["--duration", "60s", "--output-dir", str(tmp_path)])
    assert parsed.vus_levels == (1, 3, 10)
    assert parsed.token == "synthetic-token"
    assert "secret" not in " ".join(loadtest._commands(parsed, parsed.argv))
    with pytest.raises(loadtest.Blocked, match="query or fragment"):
        loadtest._url("https://staging.example?token=leak", "LOADTEST_BASE_URL")


def test_parse_k6_summary_extracts_percentiles_and_status_rates():
    summary = {
        "metrics": {
            "http_reqs": {"values": {"count": 10}},
            "http_req_failed": {"values": {"rate": 0.2}},
            "retrieve_duration": {"values": {"med": 5.0, "p(95)": 9.0, "p(99)": 12.0, "max": 15.0}},
            "status_429": {"values": {"count": 1}},
            "status_503": {"values": {"count": 2}},
            "status_other_error": {"values": {"count": 3}},
        }
    }
    row = loadtest.parse_k6_summary(summary, "retrieve")
    assert row["requests"] == 10
    assert row["errorRate"] == 0.2
    assert row["status429"] == 1
    assert row["status503"] == 2
    assert row["otherErrors"] == 3
    assert row["p50Milliseconds"] == 5.0
    assert row["p95Milliseconds"] == 9.0
    assert row["p99Milliseconds"] == 12.0


def test_thresholds_fail_on_429_503_and_missing_gpu_memory(tmp_path):
    cfg = config(tmp_path)
    rows = [{
        "mode": "retrieve", "vus": 3, "requests": 10, "errorRate": 0.0,
        "status429": 1, "status503": 1, "otherErrors": 0,
        "p50Milliseconds": 1.0, "p95Milliseconds": 2.0, "p99Milliseconds": 3.0,
    }]
    chunk = {"submitted": 1, "completed": 1, "failed": 0, "failureRate": 0.0}
    gpu = {"unavailable": True}
    checks = loadtest.evaluate_thresholds(cfg, rows, chunk, gpu)
    failed = {check["metric"] for check in checks if not check["passed"]}
    assert "retrieve@3vu.status429Rate" in failed
    assert "retrieve@3vu.status503Rate" in failed
    assert "gpuMemory" in failed


def test_chunk_job_summary_counts_failures_and_durations(tmp_path):
    driver = loadtest.ChunkDriver(config(tmp_path), threading.Event())
    driver.jobs = [
        {"status": "completed", "durationMs": 100.0},
        {"status": "completed", "durationMs": 200.0},
        {"status": "failed", "durationMs": 50.0},
        {"status": "submit-failed", "durationMs": 10.0, "status429": True},
    ]
    summary = driver.summary()
    assert summary["submitted"] == 4
    assert summary["completed"] == 2
    assert summary["failed"] == 2
    assert summary["failureRate"] == 0.5
    assert summary["status429"] == 1
    assert summary["p50DurationMs"] == 100.0
    assert summary["p95DurationMs"] == 200.0


def test_gpu_summary_records_baseline_max_and_peak(tmp_path):
    sampler = loadtest.GpuSampler(config(tmp_path), threading.Event())
    sampler.samples = [
        {"gpu": {"totalMemoryBytes": 1000, "usedMemoryBytes": 100, "peakAllocatedBytes": 80}},
        {"gpu": {"totalMemoryBytes": 1000, "usedMemoryBytes": 700, "peakAllocatedBytes": 600}},
        {"error": "transient"},
    ]
    summary = sampler.summary()
    assert summary["samples"] == 3
    assert summary["samplesWithGpu"] == 2
    assert summary["baselineUsedMemoryBytes"] == 100
    assert summary["maxUsedMemoryBytes"] == 700
    assert summary["maxPeakAllocatedBytes"] == 600
    assert summary["totalMemoryBytes"] == 1000
    assert summary["unavailable"] is False


def test_missing_k6_writes_a_blocked_report_without_running_load(tmp_path):
    cfg = config(tmp_path, k6=str(tmp_path / "missing-k6"))
    run_dir, code = loadtest.run(cfg)
    assert code == 4
    report = (run_dir / "report.md").read_text(encoding="utf-8")
    payload = json.loads((run_dir / "summary.json").read_text(encoding="utf-8"))
    assert "BLOCKED" in report
    assert "k6 is not installed" in report
    assert "secret-token-never-recorded" not in report
    assert payload["status"] == "blocked"
    assert payload["commands"]


def test_missing_environment_writes_a_blocked_report(tmp_path, monkeypatch):
    for name in ("LOADTEST_BASE_URL", "LOADTEST_WORKSPACE_ID", "EVAL_FIREBASE_TOKEN",
                 "LOADTEST_FIREBASE_TOKEN_FILE", "LOADTEST_QUESTION", "LOADTEST_COLAB_URL",
                 "LOADTEST_COLAB_KEY"):
        monkeypatch.delenv(name, raising=False)
    code = loadtest.main(["--output-dir", str(tmp_path)])
    assert code == 4
    run_dir = next(tmp_path.iterdir())
    report = (run_dir / "report.md").read_text(encoding="utf-8")
    assert "BLOCKED" in report
    assert "LOADTEST_BASE_URL" in report
    assert (run_dir / "commands.txt").is_file()
