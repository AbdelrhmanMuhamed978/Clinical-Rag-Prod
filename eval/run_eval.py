"""Evaluation entry points.

``--mode retrieval`` (default) benchmarks production retrieval using a fixed
synthetic fixture with stub embeddings/reranking. It measures EF InMemory or a
disposable SQL Server database, not live model/GPU/service-network latency,
clinical quality or publication evidence.

``--mode clinical`` runs a clinician-labelled set through the real verified
pipeline (real encoder and reranker, authenticated diagnostics). It refuses the
synthetic worked example unless ``--allow-synthetic`` is explicitly supplied.

``--mode pipeline`` remains an experimental legacy question evaluator; prefer
``--mode clinical`` for a clinical-quality run.
"""

import argparse
import json
import subprocess
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def positive_int(value: str) -> int:
    number = int(value)
    if number < 1:
        raise argparse.ArgumentTypeError("must be positive")
    return number


def main(argv: list[str] | None = None) -> int:
    values = list(sys.argv[1:] if argv is None else argv)
    mode_parser = argparse.ArgumentParser(add_help=False)
    mode_parser.add_argument("--mode", choices=("retrieval", "pipeline", "clinical"), default="retrieval")
    mode, remaining = mode_parser.parse_known_args(values)
    if mode.mode == "pipeline":
        return subprocess.call([sys.executable, str(ROOT / "eval/run_pipeline_eval.py"), *remaining])
    if mode.mode == "clinical":
        return subprocess.call([sys.executable, str(ROOT / "eval/run_clinical_eval.py"), *remaining])

    parser = argparse.ArgumentParser(description=__doc__, parents=[mode_parser])
    parser.add_argument("--output", type=Path, required=True, help="New JSON artifact; existing files are never overwritten")
    parser.add_argument("--fixture", type=Path, default=ROOT / "eval/retrieval-fixture.json")
    parser.add_argument("--chunks", type=positive_int, default=10000)
    parser.add_argument("--iterations", type=positive_int, default=15)
    parser.add_argument("--provider", choices=("inmemory", "sqlserver"), default="inmemory")
    parser.add_argument("--measure-search", action="store_true", help="Also time warm CandidatesAsync separately after DoRetrieve")
    args = parser.parse_args(values)
    output = args.output.resolve()
    if output.exists():
        parser.error(f"output already exists: {output}; choose a new artifact path")
    if args.chunks < 8:
        parser.error("--chunks must be at least 8 (the fixed query is chunk 8)")
    fixture = args.fixture.resolve()
    if not fixture.is_file():
        parser.error(f"fixture not found: {fixture}")
    output.parent.mkdir(parents=True, exist_ok=True)

    build = subprocess.run([
        "dotnet", "build", str(ROOT / "eval/ClinicalRag.Eval.csproj"),
        "--configuration", "Release", "--verbosity", "minimal",
    ], cwd=ROOT, check=False)
    if build.returncode:
        return build.returncode
    run = subprocess.run([
        "dotnet", str(ROOT / "eval/bin/Release/net10.0/ClinicalRag.Eval.dll"),
        "--fixture", str(fixture), "--output", str(output),
        "--chunks", str(args.chunks), "--iterations", str(args.iterations),
        "--provider", args.provider, "--measure-search", str(args.measure_search).lower(),
    ], cwd=ROOT, check=False)
    if run.returncode:
        return run.returncode
    print(json.dumps(json.loads(output.read_text(encoding="utf-8")), indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
