"""Build an allowlisted source-only Colab ZIP, never a workspace/database backup."""

import argparse
import hashlib
import json
from pathlib import Path
import sys
import zipfile


ROOT = Path(__file__).resolve().parents[1]
ROOT_FILES = (
    "chunker.py", "clinical_text.py", "config.py", "facts.py", "operations.py", "pipeline.py",
    "clinical-text.json", "clinical-vocabulary.json", "drugs.json", "rules.json",
    "requirements.txt", "requirements-colab.txt",
    "eval/run_eval.py", "eval/run_pipeline_eval.py", "eval/retrieval-fixture.json",
    "colab/clinical_rag_colab.ipynb", "scripts/prepare_colab_zip.py",
    "config/config.schema.json", "config/default_prompts.json", "config/retrieval-limits.json",
    "tests/fixtures/clinical-text-vectors.json",
)
SOURCE_DIRECTORIES = ("colab", "pipeline_service", "tests")
SOURCE_SUFFIXES = {".py"}
EXCLUDED_PARTS = {"__pycache__", ".pytest_cache", ".venv", "node_modules", "bin", "obj"}


def source_files(root: Path = ROOT) -> list[Path]:
    selected = {root / relative for relative in ROOT_FILES}
    for directory in SOURCE_DIRECTORIES:
        selected.update(path for path in (root / directory).rglob("*")
                        if path.is_file() and path.suffix in SOURCE_SUFFIXES
                        and not EXCLUDED_PARTS.intersection(path.relative_to(root).parts)
                        and not path.name.startswith("."))
    for path in selected:
        if not path.is_file():
            raise ValueError(f"Missing required source file: {path.relative_to(root)}")
        if path.is_symlink() or not path.resolve().is_relative_to(root.resolve()):
            raise ValueError("Source archive refuses linked/outside-root files.")
        if any(parent.is_symlink() for parent in path.parents if parent != root.parent):
            raise ValueError("Source archive refuses linked source directories.")
    return sorted(selected, key=lambda path: path.relative_to(root).as_posix())


def build_archive(output: Path, root: Path = ROOT) -> dict:
    selected = source_files(root)
    output = output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    entries = []
    # Exclusive creation preserves earlier ZIPs and historical evaluation artifacts.
    with zipfile.ZipFile(output, "x", compression=zipfile.ZIP_DEFLATED) as archive:
        for path in selected:
            name = path.relative_to(root).as_posix()
            data = path.read_bytes()
            archive.writestr(name, data)
            entries.append({"path": name, "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()})
        manifest = {"format": 1, "scope": "source-only; synthetic offline tests; no credentials or patient/evaluation inputs",
                    "python": "3.12", "files": entries}
        archive.writestr("colab-source-manifest.json", json.dumps(manifest, indent=2) + "\n")
    with zipfile.ZipFile(output) as archive:
        if archive.testzip() is not None:
            raise ValueError("Archive integrity check failed.")
    return {"path": str(output), "bytes": output.stat().st_size,
            "sha256": hashlib.sha256(output.read_bytes()).hexdigest(), "sourceFiles": len(entries)}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True, help="New ZIP path; existing files are never overwritten")
    args = parser.parse_args(argv)
    try:
        result = build_archive(args.output)
    except (ValueError, FileExistsError) as error:
        parser.error(str(error))
    print(json.dumps(result, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
