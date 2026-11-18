import json
import os
from pathlib import Path
import re
import subprocess
import sys

import pytest


ROOT = Path(__file__).resolve().parents[1]
GATE = ROOT / ".github" / "scripts" / "check_ci_gate.py"
REQUIRED_JOBS = ("dotnet", "pytest", "frontend", "eval-gate", "secret-scan")


def passing_results():
    return {name: {"result": "success"} for name in REQUIRED_JOBS}


def run_gate(payload):
    environment = os.environ.copy()
    environment["CI_JOB_RESULTS"] = payload
    return subprocess.run(
        [sys.executable, str(GATE)], env=environment,
        capture_output=True, text=True, check=False,
    )


def test_gate_accepts_only_all_required_successes():
    result = run_gate(json.dumps(passing_results()))
    assert result.returncode == 0, result.stderr


@pytest.mark.parametrize("name", REQUIRED_JOBS)
@pytest.mark.parametrize("status", ["failure", "cancelled", "skipped", "neutral", "timed_out", ""])
def test_gate_rejects_every_non_success_result(name, status):
    results = passing_results()
    results[name]["result"] = status
    result = run_gate(json.dumps(results))
    assert result.returncode == 1
    assert name in result.stdout


@pytest.mark.parametrize("name", REQUIRED_JOBS)
def test_gate_rejects_missing_required_jobs(name):
    results = passing_results()
    del results[name]
    result = run_gate(json.dumps(results))
    assert result.returncode == 1
    assert name in result.stdout


@pytest.mark.parametrize("payload", ["not-json", "[]", "null", "{}"])
def test_gate_fails_closed_for_invalid_input(payload):
    result = run_gate(payload)
    assert result.returncode == 1


def test_gate_requires_explicit_review_of_new_required_jobs():
    results = passing_results()
    results["unexpected-job"] = {"result": "failure"}
    assert run_gate(json.dumps(results)).returncode == 1


def workflow_jobs():
    workflow = (ROOT / ".github" / "workflows" / "ci.yml").read_text(encoding="utf-8")
    # Deliberately inspect this workflow's job blocks, without a new YAML dependency.
    matches = list(re.finditer(r"(?m)^  ([a-z][a-z-]*):\s*$", workflow.split("\njobs:\n", 1)[1]))
    jobs = {
        match[1]: workflow.split("\njobs:\n", 1)[1][
            match.end():matches[index + 1].start() if index + 1 < len(matches) else None
        ]
        for index, match in enumerate(matches)
    }
    return workflow, jobs


def test_workflow_has_independent_required_jobs_and_fail_closed_gate():
    workflow, jobs = workflow_jobs()
    assert set(jobs) == set(REQUIRED_JOBS) | {"ci-gate"}
    assert "  push:" in workflow
    assert "  pull_request:" in workflow
    assert "branches: [main]" in workflow
    assert "pull_request_target" not in workflow
    assert "contents: read" in workflow
    assert "continue-on-error" not in workflow
    assert "|| true" not in workflow
    assert "if: ${{ always() }}" in jobs["ci-gate"]
    assert "needs: [dotnet, pytest, frontend, eval-gate, secret-scan]" in jobs["ci-gate"]
    assert "CI_JOB_RESULTS: ${{ toJSON(needs) }}" in jobs["ci-gate"]
    assert "python .github/scripts/check_ci_gate.py" in jobs["ci-gate"]
    for name in REQUIRED_JOBS:
        assert "    needs:" not in jobs[name]


def test_workflow_runs_existing_unfiltered_checks_and_localdb():
    _, jobs = workflow_jobs()
    assert "runs-on: windows-2025" in jobs["dotnet"]
    assert 'dotnet-version: "10.0.x"' in jobs["dotnet"]
    assert "sqllocaldb start MSSQLLocalDB" in jobs["dotnet"]
    assert "dotnet build backend/ClinicalRag.Api.csproj --nologo" in jobs["dotnet"]
    assert "dotnet test backend.Tests/ClinicalRag.Tests.csproj --nologo" in jobs["dotnet"]
    assert "--filter" not in jobs["dotnet"]
    assert "python -m pip install -r python-backend/requirements.txt" in jobs["pytest"]
    assert "python -m pytest tests -q" in jobs["pytest"]
    assert 'node-version: "22"' in jobs["frontend"]
    assert "npm ci" in jobs["frontend"]
    assert "npm run lint" in jobs["frontend"]
    assert "npm test" in jobs["frontend"]
    assert "npm run build" in jobs["frontend"]
    assert jobs["frontend"].count("if: ${{ !cancelled() }}") == 3
    assert "runs-on: windows-2025" in jobs["eval-gate"]
    assert 'dotnet-version: "10.0.x"' in jobs["eval-gate"]
    assert "python eval/run_eval.py --output eval/results/ci-eval-" in jobs["eval-gate"]
    assert "python eval/compare_baseline.py" in jobs["eval-gate"]
    assert "synthetic-retrieval.v1.json" in jobs["eval-gate"]


def test_secret_scan_is_pinned_redacted_and_scans_full_history():
    workflow, jobs = workflow_jobs()
    assert "fetch-depth: 0" in jobs["secret-scan"]
    assert "sha256sum --check" in jobs["secret-scan"]
    assert "gitleaks git --redact=100 --no-banner --ignore-gitleaks-allow" in jobs["secret-scan"]
    assert '--log-opts="--all"' in jobs["secret-scan"]
    actions = re.findall(r"uses: ([^\s]+)", workflow)
    assert actions
    assert all(re.fullmatch(r"actions/[a-z-]+@[a-f0-9]{40}", action) for action in actions)


@pytest.mark.parametrize("path", [
    "backend/Services/PipelineResultPolicy.cs",
    "python-backend/pipeline.py",
    "python-backend/facts.py",
    "clinical-rag/pipeline.py",
    "clinical-rag/facts.py",
    "backend/Services/RuleService.cs",
    "backend/Services/RuleValidation.cs",
    "backend/Controllers/RulesController.cs",
    "backend/Controllers/AnalysisController.cs",
    "backend/Models/RuleDtos.cs",
    ".github/workflows/ci.yml",
    ".github/CODEOWNERS",
])
def test_safety_and_ci_files_have_explicit_code_owners(path):
    owners = (ROOT / ".github" / "CODEOWNERS").read_text(encoding="utf-8")
    assert f"/{path} @AbdelrhmanMuhamed978" in owners


def test_pull_request_template_preserves_prompt_zero_safety_and_evidence_checklist():
    template = (ROOT / ".github" / "PULL_REQUEST_TEMPLATE.md").read_text(encoding="utf-8")
    for requirement in (
        "Prompt 0", "Read every", "usages", "failing regression", "requested scope",
        "PipelineResultPolicy", "owner-only", "approved", "privacy", "Not covered",
        "synthetic", "secrets", "dotnet build", "dotnet test", "python -m pytest tests -q",
        "npm run lint", "npm test", "npm run build", "Files changed", "Tests added",
        "Commands and actual results", "Unfinished work", "independent", "CI run",
    ):
        assert requirement in template
