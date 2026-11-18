import json
import subprocess
import sys
import pytest

from eval import run_eval


def test_retrieval_eval_invokes_release_production_harness_and_defaults(tmp_path, monkeypatch):
    output = tmp_path / "result.json"
    calls = []

    def invoke(command, **options):
        calls.append((command, options))
        if command[1].endswith("ClinicalRag.Eval.dll"):
            output.write_text(json.dumps({"scope": "synthetic fixture"}), encoding="utf-8")
        return subprocess.CompletedProcess(command, 0)

    monkeypatch.setattr(run_eval.subprocess, "run", invoke)
    assert run_eval.main(["--output", str(output)]) == 0
    build, run = [call[0] for call in calls]
    assert build[:2] == ["dotnet", "build"]
    assert build[build.index("--configuration") + 1] == "Release"
    assert run[0] == "dotnet" and run[1].endswith("ClinicalRag.Eval.dll")
    assert run[run.index("--chunks") + 1] == "10000"
    assert run[run.index("--iterations") + 1] == "15"
    assert run[run.index("--fixture") + 1] == str(run_eval.ROOT / "eval/retrieval-fixture.json")
    assert run[run.index("--provider") + 1] == "inmemory"
    assert all(options["cwd"] == run_eval.ROOT for _, options in calls)


def test_retrieval_eval_passes_provider_search_and_sample_options(tmp_path, monkeypatch):
    output = tmp_path / "new/result.json"
    commands = []

    def invoke(command, **options):
        commands.append(command)
        if command[1].endswith("ClinicalRag.Eval.dll"):
            output.write_text("{}", encoding="utf-8")
        return subprocess.CompletedProcess(command, 0)

    monkeypatch.setattr(run_eval.subprocess, "run", invoke)
    assert run_eval.main(["--output", str(output), "--provider", "sqlserver", "--measure-search",
                          "--chunks", "1000", "--iterations", "100"]) == 0
    command = commands[-1]
    for flag, value in [("--provider", "sqlserver"), ("--measure-search", "true"),
                        ("--chunks", "1000"), ("--iterations", "100")]:
        assert command[command.index(flag) + 1] == value


def test_retrieval_eval_never_overwrites_existing_artifacts(tmp_path, monkeypatch):
    output = tmp_path / "before.json"
    output.write_text("preserved baseline", encoding="utf-8")
    monkeypatch.setattr(run_eval.subprocess, "run", lambda *_args, **_kwargs: pytest.fail("must not build or run"))
    with pytest.raises(SystemExit) as error:
        run_eval.main(["--output", str(output)])
    assert error.value.code == 2
    assert output.read_text(encoding="utf-8") == "preserved baseline"


@pytest.mark.parametrize("values", [
    ["--iterations", "0"], ["--iterations", "-1"], ["--chunks", "7"],
    ["--provider", "other"], ["--fixture", "missing-fixture.json"],
])
def test_retrieval_eval_rejects_invalid_arguments_before_running(tmp_path, monkeypatch, values):
    monkeypatch.setattr(run_eval.subprocess, "run", lambda *_args, **_kwargs: pytest.fail("must not build or run"))
    with pytest.raises(SystemExit) as error:
        run_eval.main(["--output", str(tmp_path / "result.json"), *values])
    assert error.value.code == 2


@pytest.mark.parametrize("failure_stage", ["build", "run"])
def test_retrieval_eval_propagates_harness_failures(tmp_path, monkeypatch, failure_stage):
    commands = []

    def invoke(command, **options):
        commands.append(command)
        failed = (command[1] == "build") == (failure_stage == "build")
        return subprocess.CompletedProcess(command, 9 if failed else 0)

    monkeypatch.setattr(run_eval.subprocess, "run", invoke)
    assert run_eval.main(["--output", str(tmp_path / "result.json")]) == 9
    assert len(commands) == (1 if failure_stage == "build" else 2)


def test_pipeline_eval_remains_separate_and_requires_explicit_mode(monkeypatch):
    commands = []
    monkeypatch.setattr(run_eval.subprocess, "call", lambda command: commands.append(command) or 4)
    assert run_eval.main(["--mode", "pipeline", "--questions", "pending.jsonl", "--output-dir", "experimental"]) == 4
    assert commands == [[sys.executable, str(run_eval.ROOT / "eval/run_pipeline_eval.py"),
                         "--questions", "pending.jsonl", "--output-dir", "experimental"]]


def test_clinical_eval_dispatches_to_the_dedicated_runner(monkeypatch):
    commands = []
    monkeypatch.setattr(run_eval.subprocess, "call", lambda command: commands.append(command) or 4)
    assert run_eval.main(["--mode", "clinical", "--clinical-set", "clinical.json",
                          "--allow-synthetic", "--output-dir", "results"]) == 4
    assert commands == [[sys.executable, str(run_eval.ROOT / "eval/run_clinical_eval.py"),
                         "--clinical-set", "clinical.json", "--allow-synthetic",
                         "--output-dir", "results"]]
