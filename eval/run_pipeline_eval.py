"""Legacy experimental question evaluator.

Prefer ``python eval/run_eval.py --mode clinical`` for a clinician-labelled,
diagnostics-bearing full-path evaluation. This legacy tool is retained only for
the existing question file. It requires explicit live credentials and workspace
settings; it never falls back to a placeholder bearer token or workspace.

Results are written to a new timestamped directory under ``eval/results`` and
never overwrite an existing artifact.
"""

import argparse
import csv
import json
import os
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import httpx


def load_questions(path: Path) -> list[dict[str, Any]]:
    questions: list[dict[str, Any]] = []
    with path.open(encoding="utf-8") as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            questions.append(json.loads(line))
    return questions


def run_pipeline(question: str, base_url: str, service_key: str, firebase_token: str,
                 workspace_id: str) -> dict[str, Any]:
    """Run one question through the verified pipeline /analyze endpoint."""
    payload = {
        "text": question,
        "config": {},
        "rules": [],
        "history": [],
        "workspaceId": workspace_id,
        "retrieveUrl": f"{base_url.rstrip('/')}/api/workspaces/{workspace_id}/analyze/retrieve",
        "conversationId": "eval-conversation",
        "drugCatalog": {},
        "catalogRevision": 0,
    }
    headers = {
        "Authorization": f"Bearer {firebase_token}",
        "X-Pipeline-Key": service_key,
    }
    with httpx.Client(timeout=120.0) as client:
        response = client.post(f"{base_url.rstrip('/')}/analyze", headers=headers, json=payload)
        response.raise_for_status()
        return response.json()


def compute_metrics(results: list[dict[str, Any]]) -> dict[str, Any]:
    grounded = [r for r in results if r.get("grounded")]
    total = len(results)
    grounded_count = len(grounded)

    hit_at_k = 0
    mrr_sum = 0.0
    verified_items = 0
    not_covered_count = 0
    unsupported_claims = 0
    problems_with_items = 0

    for r in grounded:
        result = r.get("result", {})
        problems = result.get("problems", [])

        # hit@k: at least one problem with status "ok" and items
        for p in problems:
            if p.get("status") == "ok" and p.get("items"):
                hit_at_k += 1
                problems_with_items += 1
                break

        # MRR: reciprocal rank of first problem with verified items
        for idx, p in enumerate(problems):
            if p.get("status") == "ok" and p.get("items"):
                mrr_sum += 1.0 / (idx + 1)
                break

        # verified items count
        for p in problems:
            verified_items += len(p.get("items", []))

        # not-covered rate
        if result.get("not_covered"):
            not_covered_count += 1

        # unsupported-claim rate: problems with status "unverified" or items with issues
        for p in problems:
            if p.get("status") == "unverified":
                unsupported_claims += 1
            for item in p.get("items", []):
                if item.get("issues"):
                    unsupported_claims += 1

    metrics = {
        "total_questions": total,
        "grounded_questions": grounded_count,
        "todo_questions": total - grounded_count,
        "hit_at_k": hit_at_k / grounded_count if grounded_count else 0.0,
        "mrr": mrr_sum / grounded_count if grounded_count else 0.0,
        "verified_rate": problems_with_items / grounded_count if grounded_count else 0.0,
        "avg_verified_items_per_problem": verified_items / max(problems_with_items, 1),
        "not_covered_rate": not_covered_count / grounded_count if grounded_count else 0.0,
        "unsupported_claim_rate": unsupported_claims / grounded_count if grounded_count else 0.0,
    }
    return metrics


def write_markdown(path: Path, questions: list[dict[str, Any]], metrics: dict[str, Any]) -> None:
    lines: list[str] = []
    today = datetime.now(timezone.utc).strftime("%Y-%m-%d")
    lines.append(f"# Pipeline Evaluation Results — {today}")
    lines.append("")
    lines.append("## Scope")
    lines.append("Experimental pipeline evaluation; source provenance and live authentication are not established.")
    lines.append("Not Prompt 11 publication evidence. Not a clinical-quality claim.")
    lines.append("")
    lines.append("## Metrics")
    lines.append("| Metric | Value |")
    lines.append("|--------|-------|")
    for key, value in metrics.items():
        if isinstance(value, float):
            lines.append(f"| {key} | {value:.4f} |")
        else:
            lines.append(f"| {key} | {value} |")
    lines.append("")
    lines.append("## Per-question results")
    lines.append("| ID | Question | Grounded | Expected Problem | Status |")
    lines.append("|----|----------|----------|------------------|--------|")
    for q in questions:
        result = q.get("result", {})
        status = result.get("status", "not_run") if result else "not_run"
        expected = q.get("expected_problem") or "—"
        lines.append(f"| {q['id']} | {q['question'][:60]} | {'yes' if q.get('grounded') else 'no'} | {expected} | {status} |")
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def write_csv(path: Path, questions: list[dict[str, Any]], metrics: dict[str, Any]) -> None:
    with path.open("w", newline="", encoding="utf-8") as fh:
        writer = csv.writer(fh)
        writer.writerow(["metric", "value"])
        for key, value in metrics.items():
            writer.writerow([key, value])
        writer.writerow([])
        writer.writerow(["id", "question", "grounded", "expected_problem", "status"])
        for q in questions:
            result = q.get("result", {})
            status = result.get("status", "not_run") if result else "not_run"
            expected = q.get("expected_problem") or ""
            writer.writerow([q["id"], q["question"], q.get("grounded", False), expected, status])


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--questions", type=Path, default=Path("eval/questions.jsonl"))
    parser.add_argument("--base-url", type=str, required=True,
                        help="Pipeline service origin, e.g. https://pipeline.staging.example")
    parser.add_argument("--workspace-id", type=str, required=True)
    parser.add_argument("--pipeline-key-env", default="EVAL_PIPELINE_KEY")
    parser.add_argument("--firebase-token-env", default="EVAL_FIREBASE_TOKEN")
    parser.add_argument("--output-dir", type=Path, default=Path("eval/results"))
    args = parser.parse_args()

    service_key = os.environ.get(args.pipeline_key_env, "").strip()
    firebase_token = os.environ.get(args.firebase_token_env, "").strip()
    if not service_key or not firebase_token:
        parser.error(
            f"Explicit live credentials are required in {args.pipeline_key_env} and "
            f"{args.firebase_token_env}; no placeholder token or workspace is used."
        )

    questions = load_questions(args.questions)

    # Run grounded questions through the pipeline.
    for q in questions:
        if not q.get("grounded"):
            continue
        try:
            result = run_pipeline(q["question"], args.base_url, service_key, firebase_token,
                                  args.workspace_id)
            q["result"] = result
        except Exception as exc:  # noqa: BLE001 - record failure, continue
            q["result"] = {"error": type(exc).__name__}

    metrics = compute_metrics(questions)

    args.output_dir.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H-%M-%SZ")
    run_dir = args.output_dir / f"{stamp}-pipeline-legacy"
    suffix = 1
    while run_dir.exists():
        run_dir = args.output_dir / f"{stamp}-pipeline-legacy-{suffix:02d}"
        suffix += 1
    run_dir.mkdir()
    md_path = run_dir / "report.md"
    csv_path = run_dir / "metrics.csv"
    write_markdown(md_path, questions, metrics)
    write_csv(csv_path, questions, metrics)

    print(json.dumps(metrics, indent=2))
    print(f"Results written to {md_path} and {csv_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
