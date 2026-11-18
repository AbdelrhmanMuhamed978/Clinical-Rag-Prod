from pathlib import Path
import shutil
import subprocess

import pytest


ROOT = Path(__file__).resolve().parents[1]


@pytest.fixture(scope="module")
def ignore_repository(tmp_path_factory):
    repository = tmp_path_factory.mktemp("ignore-policy")
    shutil.copyfile(ROOT / ".gitignore", repository / ".gitignore")
    subprocess.run(
        ["git", "init", "--quiet", str(repository)], check=True, capture_output=True
    )
    return repository


@pytest.mark.parametrize("path", [
    "backend/bin/Debug/application.dll",
    "backend/obj/project.assets.json",
    "backend.Tests/bin/Debug/tests.dll",
    "frontend/node_modules/package/index.js",
    "frontend/.next/server/app.js",
    "nested/__pycache__/module.pyc",
    ".pytest_cache/cache/nodeids",
    ".venv/Scripts/python.exe",
    "uploads/synthetic.pdf",
    "backend/uploads/synthetic.pdf",
    ".env",
    "development.env",
    "frontend/.env.local",
    "frontend/.env.production",
    "nested/.env.test",
    "backend/key-ring/key.xml",
    "nested/key-rings/key.xml",
    "nested/DataProtection-Keys/key.xml",
    "nested/registration-secret.dpapi",
    "nested/service.log",
    "logs/service.txt",
    "frontend/tsconfig.tsbuildinfo",
    "backend/appsettings.Local.json",
    "nested/service-account.json",
    "nested/private.pfx",
    "nested/private.key",
    "clinical-rag-colab-source.zip",
    "backend/.vs/project/cache.bin",
    "backend/appsettings.json",
    "test_e2e.ps1",
    "clinical-rag/test_e2e.ps1",
    "docs/clinicalrag-system-context.json",
])
def test_runtime_and_secret_files_are_ignored(ignore_repository, path):
    result = subprocess.run(
        ["git", "-C", str(ignore_repository), "check-ignore", "--no-index", path],
        capture_output=True,
    )
    assert result.returncode == 0, f"Runtime/secret path is not ignored: {path}"


@pytest.mark.parametrize("path", [
    "eval/results/before.json",
    "eval/results/after.json",
    "eval/results/scaling-sql-confirmation-2026-11-15.json",
    "eval/results/synthetic-retrieval-2026-11-16.json",
    "eval/run_eval.py",
    "eval/clinical/template.json",
    "eval/clinical/synthetic-example.json",
    "eval/clinical/README.md",
    "python-backend/query-synonyms.json",
    "docs/semantic-ingestion-retrieval.md",
    "eval/baselines/synthetic-retrieval.v1.json",
    "eval/baselines/synthetic-retrieval.tolerances.json",
    "scripts/loadtest/k6-load.js",
    "scripts/loadtest/run_loadtest.py",
    "scripts/rehearse-deployment.ps1",
    "docs/runbook.md",
    "docs/alert-rules.yml",
    "docs/rehearsal-results/deployment-rehearsal-2026-11-16T08-38-05Z.json",
    "docs/rehearsal-results/deployment-rehearsal-2026-11-16T08-51-38Z.json",
    "docs/rehearsal-results/deployment-rehearsal-2026-11-16T08-59-36Z.json",
    ".env.example",
    "clinical-rag/.env.example",
    "frontend/package-lock.json",
    "backend/appsettings.example.json",
    "backend/Services/PipelineResultPolicy.cs",
    ".github/workflows/ci.yml",
])
def test_sources_templates_and_evaluation_results_remain_trackable(ignore_repository, path):
    result = subprocess.run(
        ["git", "-C", str(ignore_repository), "check-ignore", "--no-index", path],
        capture_output=True,
    )
    assert result.returncode == 1, f"Required reviewable path is ignored: {path}"
