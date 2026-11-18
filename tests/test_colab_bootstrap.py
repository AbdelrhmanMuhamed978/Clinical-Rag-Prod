import hashlib
import json
from pathlib import Path
import sys
from types import ModuleType, SimpleNamespace
import zipfile

import pytest

from colab.bootstrap import extract_source, install_environment
from scripts.prepare_colab_zip import ROOT, ROOT_FILES, build_archive, source_files


def test_source_archive_excludes_credentials_data_and_build_directories(tmp_path):
    root = tmp_path / "source"
    for name in ROOT_FILES:
        path = root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("source", encoding="utf-8")
    for directory in ("colab", "pipeline_service", "tests"):
        (root / directory).mkdir(exist_ok=True)
        (root / directory / "module.py").write_text("pass", encoding="utf-8")
        (root / directory / "secrets.json").write_text("private", encoding="utf-8")
        cache = root / directory / "__pycache__"
        cache.mkdir()
        (cache / "cached.py").write_text("private", encoding="utf-8")
    for name in (".env", "frontend/.env.local", "backend/appsettings.json", "uploads/patient.pdf",
                 "key-ring/key.xml", "eval/questions.jsonl", "tests.json"):
        path = root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("private", encoding="utf-8")
    names = {p.relative_to(root).as_posix() for p in source_files(root)}
    assert "python-backend/requirements.txt" in names
    assert not any("private" in (root / name).read_text() for name in names)
    output = tmp_path / "source.zip"
    build_archive(output, root)
    extracted = tmp_path / "extracted"
    extract_source(output, extracted)
    assert (extracted / "python-backend/requirements.txt").read_text() == "source"


def test_archive_builder_never_overwrites_existing_zip(tmp_path):
    output = tmp_path / "existing.zip"
    output.write_bytes(b"preserved")
    with pytest.raises(FileExistsError):
        build_archive(output)
    assert output.read_bytes() == b"preserved"


@pytest.mark.parametrize("name", ["../escape.py", "/absolute.py", "C:/escape.py", "nested\\escape.py"])
def test_extractor_refuses_unsafe_paths_without_writing(tmp_path, name):
    data = b"pass"
    manifest = {"format": 1, "python": "3.12", "files": [
        {"path": name, "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}]}
    output = tmp_path / "unsafe.zip"
    with zipfile.ZipFile(output, "w") as archive:
        archive.writestr(name, data)
        archive.writestr("colab-source-manifest.json", json.dumps(manifest))
    target = tmp_path / "target"
    with pytest.raises(ValueError, match="unsafe member path"):
        extract_source(output, target)
    assert not target.exists()


def test_extractor_detects_changed_content_and_extra_members(tmp_path):
    manifest = {"format": 1, "python": "3.12", "files": [
        {"path": "source.py", "bytes": 4, "sha256": hashlib.sha256(b"pass").hexdigest()}]}
    output = tmp_path / "changed.zip"
    with zipfile.ZipFile(output, "w") as archive:
        archive.writestr("source.py", b"oops")
        archive.writestr("colab-source-manifest.json", json.dumps(manifest))
    with pytest.raises(ValueError, match="integrity"):
        extract_source(output, tmp_path / "target")


def test_gpu_installer_does_not_run_on_windows(monkeypatch, tmp_path):
    monkeypatch.setattr("colab.bootstrap.sys.platform", "win32")
    with pytest.raises(ValueError, match="Linux/Colab"):
        install_environment(tmp_path)


def test_real_archive_has_required_evaluator_and_clinical_assets(tmp_path):
    output = tmp_path / "clinical-rag.zip"
    build_archive(output)
    target = tmp_path / "app"
    extract_source(output, target)
    assert (target / "conftest.py").is_file()
    assert (target / "python-backend/pipeline.py").is_file()
    assert (target / "python-backend/pipeline_service/app.py").is_file()
    assert (target / "python-backend/config/default_prompts.json").is_file()
    assert (target / "python-backend/query-synonyms.json").is_file()
    assert (target / "eval/retrieval-fixture.json").is_file()
    assert (target / "eval/run_clinical_eval.py").is_file()
    assert (target / "eval/clinical/loader.py").is_file()
    assert (target / "eval/clinical/real-who-crag-pending.json").is_file()
    assert (target / "eval/baselines/synthetic-retrieval.tolerances.json").is_file()
    assert (target / "eval/results/synthetic-retrieval-2026-11-16.json").is_file()
    assert (target / "scripts/loadtest/run_loadtest.py").is_file()
    assert (target / "scripts/loadtest/k6-load.js").is_file()
    assert (target / "tests/fixtures/clinical-text-vectors.json").is_file()
    assert (target / "colab/bootstrap.py").is_file()
    assert (target / "python-backend/requirements.txt").read_bytes() == (ROOT / "python-backend/requirements.txt").read_bytes()


def test_real_archive_contains_governance_resources_required_by_offline_tests(tmp_path):
    output = tmp_path / "clinical-rag.zip"
    build_archive(output)
    target = tmp_path / "app"
    extract_source(output, target)
    # tests/test_repository_hygiene.py reads the root policy; tests/test_ci_governance.py
    # executes the gate and inspects the workflow, owners and pull-request template.
    for name in (
        ".gitignore",
        ".github/scripts/check_ci_gate.py",
        ".github/workflows/ci.yml",
        ".github/CODEOWNERS",
        ".github/PULL_REQUEST_TEMPLATE.md",
    ):
        assert (target / name).read_bytes() == (ROOT / name).read_bytes()


def test_notebook_cells_are_standard_python_and_upload_does_not_install():
    import ast
    notebook = json.loads((ROOT / "colab/clinical_rag_colab.ipynb").read_text())
    cells = {cell["id"]: "".join(cell["source"]) for cell in notebook["cells"] if cell["cell_type"] == "code"}
    for code in cells.values():
        ast.parse(code)
    assert "files.upload()" in cells["cell-2-upload"]
    assert "pip" not in cells["cell-2-upload"]
    assert "INSTALL COMPLETE" in cells["cell-3-packages"]
    assert "userdata.get" in cells["cell-5-server"]


def test_runtime_uses_sentence_transformers_27_reranker_keyword_and_disables_prompt_logs(monkeypatch):
    from colab.models import ModelRuntime
    captured = {}

    def module(name, **values):
        result = ModuleType(name)
        result.__dict__.update(values)
        monkeypatch.setitem(sys.modules, name, result)

    module("torch", cuda=SimpleNamespace(is_available=lambda: False), float16="half")
    module("transformers", AutoTokenizer=SimpleNamespace(from_pretrained=lambda _: object()))

    def reranker(model_name, *, device, max_length, automodel_args):
        captured["reranker"] = (model_name, device, max_length, automodel_args)
        return object()

    module("sentence_transformers", CrossEncoder=reranker,
           SentenceTransformer=lambda *args, **kwargs: SimpleNamespace(get_sentence_embedding_dimension=lambda: 768))

    def arguments(**kwargs):
        captured["engine"] = kwargs
        return kwargs

    module("vllm.engine.arg_utils", AsyncEngineArgs=arguments)
    module("vllm.engine.async_llm_engine", AsyncLLMEngine=SimpleNamespace(from_engine_args=lambda _: object()))
    module("vllm.sampling_params", SamplingParams=object)
    runtime = ModelRuntime()
    runtime._load_models()
    assert captured["reranker"] == ("BAAI/bge-reranker-large", "cpu", 512, {})
    assert captured["engine"]["disable_log_requests"] is True
    assert runtime.embedding_dimensions == 768


def test_larger_model_requires_explicit_flag_and_preserves_default(monkeypatch):
    from colab.models import ModelRuntime
    monkeypatch.delenv("COLAB_ENABLE_LARGER_MODEL", raising=False)
    assert ModelRuntime().model_name == "Qwen/Qwen3-4B-Instruct-2507"
    monkeypatch.setenv("COLAB_ENABLE_LARGER_MODEL", "1")
    larger = ModelRuntime()
    assert larger.model_name == "Qwen/Qwen3-8B-AWQ"
    assert larger.quantization == "awq"
    assert larger.embedding_model == "pritamdeka/S-PubMedBert-MS-MARCO"


def test_larger_model_flag_refuses_ambiguous_values(monkeypatch):
    from colab.models import ModelRuntime
    monkeypatch.setenv("COLAB_ENABLE_LARGER_MODEL", "maybe")
    with pytest.raises(ValueError):
        ModelRuntime()
