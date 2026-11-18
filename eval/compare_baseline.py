"""Fail a regression gate when synthetic-fixture metrics drop beyond tolerance.

The gate intentionally uses the offline fixed-vector retrieval harness because
hosted CI has no clinician labels, staging stack or GPU. It is a regression check
for retrieval behavior, not clinical-quality evidence.
"""

from __future__ import annotations

import argparse
import json
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_BASELINE = ROOT / "eval/baselines/synthetic-retrieval.v1.json"
DEFAULT_TOLERANCES = ROOT / "eval/baselines/synthetic-retrieval.tolerances.json"


def _read(path: Path) -> dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise ValueError(f"Cannot read {path}: {error}") from error
    if not isinstance(value, dict):
        raise ValueError(f"{path} must contain a JSON object.")
    return value


def _quality(document: dict[str, Any], label: str) -> dict[str, Any]:
    quality = document.get("quality")
    if not isinstance(quality, dict):
        raise ValueError(f"{label} has no quality metrics.")
    return quality


def compare(*, baseline: dict[str, Any], current: dict[str, Any], tolerances: dict[str, Any],
            allow_fixture_change: bool = False, allow_provider_change: bool = False) -> dict[str, Any]:
    base_quality = _quality(baseline, "baseline")
    current_quality = _quality(current, "current")
    gated = tolerances.get("gatedMetrics")
    if not isinstance(gated, dict) or not gated:
        raise ValueError("Tolerances must contain a non-empty gatedMetrics object.")

    checks: list[dict[str, Any]] = []
    for metric, allowed_drop in gated.items():
        if not isinstance(allowed_drop, (int, float)) or isinstance(allowed_drop, bool) or allowed_drop < 0:
            raise ValueError(f"Tolerance for {metric} must be a non-negative number.")
        if metric not in base_quality:
            raise ValueError(f"Baseline is missing gated metric {metric}.")
        if metric not in current_quality:
            raise ValueError(f"Current artifact is missing gated metric {metric}.")
        before = float(base_quality[metric])
        after = float(current_quality[metric])
        drop = before - after
        passed = drop <= float(allowed_drop) + 1e-12
        checks.append({
            "metric": metric, "baseline": before, "current": after,
            "drop": round(drop, 6), "allowedDrop": float(allowed_drop), "passed": passed,
        })

    fixture_ok = baseline.get("fixtureSha256") == current.get("fixtureSha256")
    fixture_check = {
        "metric": "fixtureSha256", "baseline": baseline.get("fixtureSha256"),
        "current": current.get("fixtureSha256"), "allowedDrop": None,
        "passed": fixture_ok or allow_fixture_change,
    }
    provider_ok = baseline.get("databaseProvider") == current.get("databaseProvider")
    provider_check = {
        "metric": "databaseProvider", "baseline": baseline.get("databaseProvider"),
        "current": current.get("databaseProvider"), "allowedDrop": None,
        "passed": provider_ok or allow_provider_change,
    }
    query_check = {
        "metric": "queries", "baseline": base_quality.get("queries"),
        "current": current_quality.get("queries"), "allowedDrop": None,
        "passed": base_quality.get("queries") == current_quality.get("queries"),
    }
    checks.extend([fixture_check, provider_check, query_check])
    return {
        "schemaVersion": "synthetic-regression-gate/1.0",
        "generatedAtUtc": datetime.now(timezone.utc).isoformat(),
        "command": " ".join([sys.executable, "eval/compare_baseline.py", *sys.argv[1:]]),
        "passed": all(check["passed"] for check in checks),
        "checks": checks,
        "baselineScope": baseline.get("scope"),
        "currentScope": current.get("scope"),
    }


def _table(report: dict[str, Any]) -> str:
    lines = ["| Check | Baseline | Current | Allowed drop | Result |", "| --- | --- | --- | --- | --- |"]
    for check in report["checks"]:
        allowed = check["allowedDrop"] if check["allowedDrop"] is not None else "must match"
        lines.append(
            f"| {check['metric']} | {check['baseline']} | {check['current']} | {allowed} | "
            f"{'PASS' if check['passed'] else 'FAIL'} |"
        )
    return "\n".join(lines)


def write_new(path: Path, report: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("x", encoding="utf-8", newline="\n") as handle:
        json.dump(report, handle, indent=2, sort_keys=True)
        handle.write("\n")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", type=Path, default=DEFAULT_BASELINE)
    parser.add_argument("--current", type=Path, required=True)
    parser.add_argument("--tolerances", type=Path, default=DEFAULT_TOLERANCES)
    parser.add_argument("--tolerance", type=float,
                        help="Override every gated metric's absolute allowed drop (e.g. 0.01)")
    parser.add_argument("--output", type=Path, help="New JSON gate report; existing files are never overwritten")
    parser.add_argument("--allow-fixture-change", action="store_true",
                        help="Only for a deliberate, reviewed fixture migration")
    parser.add_argument("--allow-provider-change", action="store_true",
                        help="Only when comparing a deliberately different database provider")
    args = parser.parse_args(argv)

    try:
        baseline = _read(args.baseline)
        current = _read(args.current)
        tolerances = _read(args.tolerances)
        if args.tolerance is not None:
            if args.tolerance < 0:
                raise ValueError("--tolerance must be non-negative.")
            gated = tolerances.get("gatedMetrics")
            if not isinstance(gated, dict):
                raise ValueError("Tolerances must contain a gatedMetrics object.")
            tolerances = {**tolerances, "gatedMetrics": {key: args.tolerance for key in gated}}
        report = compare(baseline=baseline, current=current, tolerances=tolerances,
                         allow_fixture_change=args.allow_fixture_change,
                         allow_provider_change=args.allow_provider_change)
    except ValueError as error:
        print(f"Regression gate failed: {error}", file=sys.stderr)
        return 2

    print("Synthetic-fixture regression gate")
    print(_table(report))
    if args.output is not None:
        if args.output.exists():
            print(f"Regression gate failed: output already exists: {args.output}", file=sys.stderr)
            return 2
        write_new(args.output, report)
    if not report["passed"]:
        print("Regression gate failed: at least one metric dropped beyond tolerance or the fixture/provider changed.",
              file=sys.stderr)
        return 1
    print("Regression gate passed: synthetic-fixture metrics are within tolerance.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
