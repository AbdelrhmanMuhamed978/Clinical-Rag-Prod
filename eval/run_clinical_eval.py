"""Run a clinician-labelled set through the real verified pipeline.

This is the full verified analysis path below the ASP.NET persistence wrapper:
the pipeline service plans, retrieves through the real workspace retrieval
endpoint (real encoder and reranker), generates, verifies, applies approved
rules to cases, and returns text-free diagnostics when the authenticated
evaluation call enables ``testRunDiagnostics``.

The runner refuses to present synthetic fixtures as clinician labels. It writes
only new timestamped result directories and records blocked runs explicitly.
Reports never contain case text, model recommendations, quotes or excerpts.
"""

from __future__ import annotations

import argparse
import csv
import json
import os
import re
import shlex
import sys
import time
import uuid
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any
from urllib.parse import urlsplit

import httpx

if __package__ in (None, ""):
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from eval.clinical.loader import ClinicalSet, ClinicalSetError, load_clinical_set
from eval.clinical.metrics import compute_metrics, evaluate_case


RESULT_LABEL = re.compile(r"^[a-z0-9-]{1,40}$")


@dataclass(frozen=True)
class Settings:
    clinical_set_path: Path
    pipeline_url: str
    backend_url: str
    workspace_id: str
    firebase_token: str
    pipeline_key: str
    top_k: int
    timeout: float
    match_threshold: float
    allow_synthetic: bool
    output_dir: Path
    config_file: Path | None
    rules_file: Path | None
    minimum_hit_at_k: float | None
    minimum_mrr: float | None
    minimum_verified_rate: float | None
    minimum_expected_statement_verified_rate: float | None
    maximum_not_covered_rate: float | None
    maximum_unsupported_claim_rate: float | None
    maximum_error_rate: float | None
    argv: tuple[str, ...]


def positive_int(value: str) -> int:
    number = int(value)
    if number < 1:
        raise argparse.ArgumentTypeError("must be positive")
    return number


def _bounded(value: str) -> float:
    number = float(value)
    if not 0.0 <= number <= 1.0:
        raise argparse.ArgumentTypeError("must be between 0 and 1")
    return number


def _url(value: str | None, label: str, errors: list[str]) -> str:
    if not value:
        errors.append(f"{label} is required (argument or environment variable).")
        return ""
    parsed = urlsplit(value)
    if (parsed.scheme not in {"http", "https"} or not parsed.hostname or parsed.username
            or parsed.password or parsed.query or parsed.fragment):
        errors.append(f"{label} must be an HTTP(S) URL without credentials, query or fragment.")
        return ""
    return value.rstrip("/")


def _token(path: Path | None, errors: list[str]) -> str:
    if path is not None:
        try:
            token = path.read_text(encoding="utf-8").strip()
        except (OSError, UnicodeError) as error:
            errors.append(f"Firebase token file could not be read ({type(error).__name__}).")
            return ""
    else:
        token = os.environ.get("EVAL_FIREBASE_TOKEN", "").strip()
    if not token:
        errors.append("A Firebase token is required via EVAL_FIREBASE_TOKEN or --firebase-token-file.")
    elif any(character.isspace() for character in token):
        errors.append("The Firebase token contains whitespace; provide a single ID token.")
    return token


def _workspace(value: str | None, errors: list[str]) -> str:
    if not value:
        errors.append("--workspace-id or EVAL_WORKSPACE_ID is required.")
        return ""
    try:
        return str(uuid.UUID(value))
    except ValueError:
        errors.append("workspace-id must be a UUID.")
        return ""


def parse_settings(argv: list[str]) -> tuple[Settings | None, list[str]]:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--clinical-set", type=Path, required=True)
    parser.add_argument("--pipeline-url", default=os.environ.get("EVAL_PIPELINE_URL"))
    parser.add_argument("--backend-url", default=os.environ.get("EVAL_BACKEND_URL"))
    parser.add_argument("--workspace-id", default=os.environ.get("EVAL_WORKSPACE_ID"))
    parser.add_argument("--firebase-token-file", type=Path)
    parser.add_argument("--config-file", type=Path)
    parser.add_argument("--rules-file", type=Path)
    parser.add_argument("--top-k", type=positive_int, default=8)
    parser.add_argument("--timeout", type=float, default=300.0)
    parser.add_argument("--match-threshold", type=_bounded, default=0.8)
    parser.add_argument("--allow-synthetic", action="store_true")
    parser.add_argument("--output-dir", type=Path, default=Path("eval/results/clinical"))
    parser.add_argument("--minimum-hit-at-k", type=_bounded)
    parser.add_argument("--minimum-mrr", type=_bounded)
    parser.add_argument("--minimum-verified-rate", type=_bounded)
    parser.add_argument("--minimum-expected-statement-verified-rate", type=_bounded)
    parser.add_argument("--maximum-not-covered-rate", type=_bounded)
    parser.add_argument("--maximum-unsupported-claim-rate", type=_bounded)
    parser.add_argument("--maximum-error-rate", type=_bounded, default=0.0)
    args = parser.parse_args(argv)

    errors: list[str] = []
    if not 0 < args.timeout <= 3600:
        errors.append("--timeout must be between 0 and 3600 seconds.")
    pipeline_url = _url(args.pipeline_url, "--pipeline-url / EVAL_PIPELINE_URL", errors)
    backend_url = _url(args.backend_url, "--backend-url / EVAL_BACKEND_URL", errors)
    workspace_id = _workspace(args.workspace_id, errors)
    token = _token(args.firebase_token_file, errors)
    pipeline_key = os.environ.get("EVAL_PIPELINE_KEY", "").strip()
    if not pipeline_key:
        errors.append("EVAL_PIPELINE_KEY is required for the authenticated pipeline service.")
    elif any(character.isspace() for character in pipeline_key):
        errors.append("EVAL_PIPELINE_KEY contains whitespace.")
    for path, label in ((args.config_file, "--config-file"), (args.rules_file, "--rules-file")):
        if path is not None and not path.is_file():
            errors.append(f"{label} not found: {path}")
    if errors:
        return None, errors
    return Settings(
        clinical_set_path=args.clinical_set,
        pipeline_url=pipeline_url,
        backend_url=backend_url,
        workspace_id=workspace_id,
        firebase_token=token,
        pipeline_key=pipeline_key,
        top_k=args.top_k,
        timeout=args.timeout,
        match_threshold=args.match_threshold,
        allow_synthetic=args.allow_synthetic,
        output_dir=args.output_dir,
        config_file=args.config_file,
        rules_file=args.rules_file,
        minimum_hit_at_k=args.minimum_hit_at_k,
        minimum_mrr=args.minimum_mrr,
        minimum_verified_rate=args.minimum_verified_rate,
        minimum_expected_statement_verified_rate=args.minimum_expected_statement_verified_rate,
        maximum_not_covered_rate=args.maximum_not_covered_rate,
        maximum_unsupported_claim_rate=args.maximum_unsupported_claim_rate,
        maximum_error_rate=args.maximum_error_rate,
        argv=tuple(argv),
    ), []


def create_run_dir(root: Path, label: str, now: datetime | None = None) -> Path:
    if not RESULT_LABEL.fullmatch(label):
        raise ValueError("Result label must use lowercase letters, digits and hyphens.")
    stamp = (now or datetime.now(timezone.utc)).astimezone(timezone.utc).strftime("%Y-%m-%dT%H-%M-%SZ")
    root.mkdir(parents=True, exist_ok=True)
    for attempt in range(1000):
        suffix = "" if attempt == 0 else f"-{attempt:02d}"
        candidate = root / f"{stamp}-{label}{suffix}"
        try:
            candidate.mkdir()
            return candidate
        except FileExistsError:
            continue
    raise RuntimeError("Could not allocate a new result directory without overwriting evidence.")


def _write_new(path: Path, text: str) -> None:
    with path.open("x", encoding="utf-8", newline="\n") as handle:
        handle.write(text)


def _commands(argv: tuple[str, ...]) -> list[str]:
    direct = shlex.join([sys.executable, "eval/run_clinical_eval.py", *argv])
    via = shlex.join([sys.executable, "eval/run_eval.py", "--mode", "clinical", *argv])
    return [
        direct,
        via,
        "# EVAL_FIREBASE_TOKEN and EVAL_PIPELINE_KEY are supplied only through the environment; values are never recorded.",
    ]


def _write_commands(run_dir: Path, argv: tuple[str, ...]) -> list[str]:
    lines = _commands(argv)
    _write_new(run_dir / "commands.txt", "\n".join(lines) + "\n")
    return lines


def _safe_error(error: Exception) -> str:
    return re.sub(r"[\x00-\x1f\x7f]", " ", f"{type(error).__name__}: {error}")[:300]


def _headers(settings: Settings) -> dict[str, str]:
    return {
        "Authorization": f"Bearer {settings.firebase_token}",
        "X-Pipeline-Key": settings.pipeline_key,
        "Cache-Control": "no-store",
    }


def _read_json_file(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


def _config_payload(settings: Settings, client: httpx.Client, preflight: dict[str, Any]) -> dict[str, Any]:
    if settings.config_file is not None:
        raw = _read_json_file(settings.config_file)
        if isinstance(raw, dict) and isinstance(raw.get("configJson"), str):
            raw = json.loads(raw["configJson"])
        if not isinstance(raw, dict):
            raise ClinicalSetError("--config-file must contain a JSON configuration object or a configJson export.")
        config = raw
        preflight["configSource"] = "file"
        preflight["configVersion"] = None
    else:
        response = client.get(
            f"{settings.backend_url}/api/workspaces/{settings.workspace_id}/config/published",
            headers={"Authorization": f"Bearer {settings.firebase_token}", "Cache-Control": "no-store"},
        )
        preflight["configStatus"] = response.status_code
        if response.status_code != 200:
            raise ClinicalSetError(f"The backend published-configuration endpoint returned HTTP {response.status_code}.")
        payload = response.json()
        text = payload.get("configJson") if isinstance(payload, dict) else None
        if not isinstance(text, str):
            raise ClinicalSetError("The backend published configuration did not contain configJson.")
        config = json.loads(text)
        if not isinstance(config, dict):
            raise ClinicalSetError("The published configuration is not a JSON object.")
        preflight["configSource"] = "backend"
        preflight["configVersion"] = payload.get("version")
    retrieval = config.get("retrieval")
    preflight["configuredRetrieval"] = (
        {key: retrieval.get(key) for key in ("topK", "candidates", "minRerank")}
        if isinstance(retrieval, dict) else None
    )
    return config


def _rules_payload(settings: Settings, client: httpx.Client,
                   preflight: dict[str, Any]) -> tuple[list[dict[str, Any]], dict[str, Any], int]:
    if settings.rules_file is not None:
        raw = _read_json_file(settings.rules_file)
        preflight["rulesSource"] = "file"
        if isinstance(raw, list):
            rules, catalog, revision = raw, {}, 0
        elif isinstance(raw, dict):
            rules = raw.get("rules", [])
            catalog = raw.get("drugCatalog", {})
            revision = raw.get("catalogRevision", 0)
        else:
            raise ClinicalSetError("--rules-file must contain an approved-rule snapshot or rule array.")
    else:
        response = client.get(
            f"{settings.backend_url}/api/workspaces/{settings.workspace_id}/rules/approved-snapshot",
            headers={"Authorization": f"Bearer {settings.firebase_token}", "Cache-Control": "no-store"},
        )
        preflight["rulesStatus"] = response.status_code
        if response.status_code != 200:
            raise ClinicalSetError(
                f"The backend approved-rule snapshot endpoint returned HTTP {response.status_code}; "
                "deploy the matching backend or supply --rules-file from the same configuration."
            )
        raw = response.json()
        rules = raw.get("rules", []) if isinstance(raw, dict) else raw
        catalog = raw.get("drugCatalog", {}) if isinstance(raw, dict) else {}
        revision = raw.get("catalogRevision", 0) if isinstance(raw, dict) else 0
        preflight["rulesSource"] = "backend"
    if not isinstance(rules, list) or not all(isinstance(rule, dict) for rule in rules):
        raise ClinicalSetError("Approved rules must be a JSON array of rule objects.")
    if not isinstance(catalog, dict):
        raise ClinicalSetError("The drug catalog must be a JSON object.")
    if type(revision) is not int or revision < 0:
        raise ClinicalSetError("The drug catalog revision must be a non-negative integer.")
    preflight["ruleCount"] = len(rules)
    preflight["catalogRevision"] = revision
    return rules, catalog, revision


def preflight(settings: Settings, clinical_set: ClinicalSet) -> tuple[dict[str, Any], list[str]]:
    """Check live dependencies/config without running any clinical case text."""
    details: dict[str, Any] = {}
    reasons: list[str] = []
    retrieve_url = f"{settings.backend_url}/api/workspaces/{settings.workspace_id}/analyze/retrieve"
    details["retrieveUrl"] = retrieve_url
    try:
        with httpx.Client(timeout=30.0, follow_redirects=False) as client:
            try:
                response = client.get(f"{settings.pipeline_url}/ready",
                                      headers={"X-Pipeline-Key": settings.pipeline_key})
                details["pipelineReadyStatus"] = response.status_code
                body = response.json() if response.status_code == 200 else {}
                details["pipelineReady"] = bool(body.get("ready"))
                if response.status_code != 200 or not details["pipelineReady"]:
                    reasons.append("The verified pipeline service is not ready; no clinical case was run.")
            except Exception as error:  # noqa: BLE001 - blocked-run evidence
                details["pipelineError"] = _safe_error(error)
                reasons.append("The verified pipeline service could not be reached; no clinical case was run.")

            try:
                response = client.get(f"{settings.backend_url}/health")
                details["backendHealthStatus"] = response.status_code
                if response.status_code != 200:
                    reasons.append("The backend health endpoint did not return HTTP 200.")
            except Exception as error:  # noqa: BLE001
                details["backendError"] = _safe_error(error)
                reasons.append("The backend could not be reached; no clinical case was run.")

            try:
                response = client.get(
                    f"{settings.backend_url}/api/operations/colab",
                    headers={"Authorization": f"Bearer {settings.firebase_token}", "Cache-Control": "no-store"},
                )
                details["modelEvidenceStatus"] = response.status_code
                if response.status_code == 200:
                    payload = response.json()
                    model = payload.get("model") if isinstance(payload, dict) else None
                    if isinstance(model, dict):
                        details["modelEvidence"] = {
                            "ready": model.get("ready"),
                            "model": model.get("model"),
                            "embeddingModel": model.get("embeddingModel"),
                            "embeddingDimensions": model.get("embeddingDimensions"),
                            "gpu": model.get("gpu"),
                        }
                        if not model.get("ready"):
                            reasons.append("The Colab model/encoder/reranker runtime is not ready.")
                else:
                    details["modelEvidence"] = {"unavailable": True, "status": response.status_code}
            except Exception as error:  # noqa: BLE001
                details["modelEvidence"] = {"unavailable": True, "error": _safe_error(error)}

            if details.get("pipelineReady"):
                try:
                    config = _config_payload(settings, client, details)
                    rules, catalog, revision = _rules_payload(settings, client, details)
                    details["config"] = config
                    details["rules"] = rules
                    details["drugCatalog"] = catalog
                    details["catalogRevision"] = revision
                except (ClinicalSetError, OSError, ValueError, json.JSONDecodeError) as error:
                    reasons.append(_safe_error(error))
    except Exception as error:  # noqa: BLE001
        reasons.append(_safe_error(error))
    if not clinical_set.cases:
        reasons.append("The clinical set contains no cases.")
    return details, reasons


def _thresholds(settings: Settings, metrics: dict[str, Any]) -> list[dict[str, Any]]:
    checks = [
        ("hitAtK", ">=", settings.minimum_hit_at_k, "minimumHitAtK"),
        ("mrr", ">=", settings.minimum_mrr, "minimumMrr"),
        ("verifiedRate", ">=", settings.minimum_verified_rate, "minimumVerifiedRate"),
        ("expectedStatementVerifiedRate", ">=", settings.minimum_expected_statement_verified_rate,
         "minimumExpectedStatementVerifiedRate"),
        ("notCoveredRate", "<=", settings.maximum_not_covered_rate, "maximumNotCoveredRate"),
        ("unsupportedClaimRate", "<=", settings.maximum_unsupported_claim_rate, "maximumUnsupportedClaimRate"),
        ("errorRate", "<=", settings.maximum_error_rate, "maximumErrorRate"),
    ]
    results: list[dict[str, Any]] = []
    for key, comparator, limit, label in checks:
        if limit is None:
            continue
        actual = metrics.get(key, 0.0)
        passed = actual >= limit if comparator == ">=" else actual <= limit
        results.append({"metric": key, "comparator": comparator, "limit": limit,
                        "actual": actual, "passed": bool(passed), "label": label})
    violations = metrics.get("forbiddenStatementViolations", 0)
    results.append({"metric": "forbiddenStatementViolations", "comparator": "<=", "limit": 0,
                    "actual": violations, "passed": violations == 0,
                    "label": "forbidden statements must never appear as verified guidance"})
    return results


def _csv_rows(cases: list[dict[str, Any]]) -> list[list[Any]]:
    rows = [[
        "case_id", "http_status", "error", "hit_at_k", "rank", "section_matched",
        "expected_mode", "actual_mode", "mode_matched", "pending_clinician_fields",
        "expected_statements", "matched_expected_statements", "forbidden_violations",
        "forbidden_rejected", "verified_items", "draft_count", "candidate_statements",
        "verified_statements", "diagnostics_available", "pipeline_not_covered_count",
        "duration_ms",
    ]]
    for case in cases:
        rows.append([
            case["caseId"], case["status"], case.get("error") or "",
            str(case["hitAtK"]).lower(), case["rank"] if case["rank"] is not None else "",
            str(case["sectionMatched"]).lower(),
            case.get("expectedMode") or "", case.get("actualMode") or "",
            str(case.get("modeMatched")).lower() if case.get("expectedMode") else "",
            case.get("pendingClinicianFields", 0),
            case["expectedStatements"],
            case["matchedExpectedStatements"], case["forbiddenViolations"],
            case["forbiddenRejected"], case["verifiedItems"], case["draftCount"], case["candidateStatements"],
            case["verifiedStatements"], str(case["diagnosticsAvailable"]).lower(),
            case["pipelineNotCoveredCount"], case["durationMs"] if case["durationMs"] is not None else "",
        ])
    return rows


def _write_csv(path: Path, cases: list[dict[str, Any]]) -> None:
    with path.open("x", encoding="utf-8", newline="") as handle:
        writer = csv.writer(handle)
        writer.writerows(_csv_rows(cases))


def _fmt(value: Any) -> str:
    if value is None:
        return "unavailable"
    if isinstance(value, float):
        return f"{value:.4f}"
    return str(value)


def _markdown(payload: dict[str, Any]) -> str:
    status = payload["status"]
    lines = [
        f"# Clinical evaluation — {payload['generatedAtUtc']}",
        "",
        f"**Status:** {status.upper()}",
        f"**Clinical evidence:** {'yes' if payload['clinicalEvidence'] else 'NO — not clinician-labelled evidence'}",
        f"**Label source:** `{payload['set']['labelSource'] or 'unavailable'}`",
        f"**Cases in set:** {payload['set']['caseCount']}",
    ]
    notice = payload["set"].get("syntheticNotice")
    if notice:
        lines.append(f"**Synthetic notice:** {notice}")
    clinician = payload["set"].get("clinician") or {}
    if clinician.get("name"):
        lines.append(f"**Clinician:** {clinician['name']} ({clinician.get('role', 'role not recorded')})")
        lines.append(f"**Attestation:** {clinician.get('attestation', 'not recorded')}")
    lines.append("")
    if status == "blocked":
        lines += ["## Blocked", ""]
        for reason in payload["blockedReasons"]:
            parts = []
            for line in str(reason).splitlines():
                part = line.strip()
                if part.startswith("- "):
                    part = part[2:].strip()
                if part:
                    parts.append(part)
            if not parts:
                continue
            lines.append(f"- {parts[0]}")
            lines += [f"  - {part}" for part in parts[1:]]
        lines += ["", "No clinical case text was sent. This is a blocked run, not a passing evaluation.", ""]
    else:
        metrics = payload["metrics"]
        model = payload.get("modelEvidence")
        model_verified = isinstance(model, dict) and model.get("model") and model.get("embeddingModel")
        retrieval_line = (
            "- Retrieval: workspace retrieval endpoint with reported encoder/reranker model metadata."
            if model_verified else
            "- Retrieval: workspace retrieval endpoint, but encoder/reranker identity was not independently verified in this report."
        )
        lines += [
            "## Scope",
            "",
            "- Endpoint: authenticated pipeline service `/analyze` with `testRunDiagnostics`.",
            retrieval_line,
            "- This is not evidence of clinical safety, effectiveness or guideline fidelity.",
            "",
            "## Metrics",
            "",
            "| Metric | Value |",
            "| --- | --- |",
            f"| hit@{metrics['topK']} | {_fmt(metrics['hitAtK'])} |",
            f"| MRR | {_fmt(metrics['mrr'])} |",
            f"| verified-rate (cases with ≥1 verified item) | {_fmt(metrics['verifiedRate'])} |",
            f"| expected-statement verified-rate | {_fmt(metrics['expectedStatementVerifiedRate'])} |",
            f"| not-covered-rate | {_fmt(metrics['notCoveredRate'])} |",
            f"| expected-mode accuracy | {_fmt(metrics.get('modeAccuracy'))} ({metrics.get('modeMatchedCases', 0)}/{metrics.get('modeExpectedCases', 0)}) |",
            f"| cases with pending clinician labels | {metrics.get('pendingClinicianLabelCases', 0)} |",
            f"| unsupported-claim rate | {_fmt(metrics['unsupportedClaimRate'])} |",
            f"| forbidden-statement violations | {metrics['forbiddenStatementViolations']} |",
            f"| error rate | {_fmt(metrics['errorRate'])} |",
            f"| answer drafts | {metrics['draftCount']} |",
            f"| statement candidates | {metrics['statementCandidates']} |",
            f"| verified statements | {metrics['verifiedStatements']} |",
            f"| rejected statements | {metrics['rejectedStatements']} |",
            f"| deduplicated statements | {metrics['deduplicatedStatements']} |",
            f"| analyze latency p50 / p95 / p99 (ms) | {metrics['latencyMilliseconds']['p50']} / "
            f"{metrics['latencyMilliseconds']['p95']} / {metrics['latencyMilliseconds']['p99']} |",
            "",
            "### Metric definitions",
            "",
            "- **hit@k**: an expected document/page appears at or above the best within-problem source rank, within the configured evaluation `k`.",
            "- **MRR**: reciprocal rank of the first expected source, averaged over scored cases.",
            "- **verified-rate**: scored cases with at least one verified item, divided by scored cases (the prior evaluator's case-level meaning).",
            "- **expected-statement verified-rate**: expected statements matched by a verified recommendation or verified quote, divided by all expected statements.",
            "- **not-covered-rate**: cases with no matched expected statement, divided by scored cases.",
            "- **unsupported-claim rate**: `(candidates - verified - deduplicated) / candidates` from the text-free diagnostics. A zero candidate count is shown explicitly.",
            "- **per-reason counts**: the pipeline's fixed verification rejection reasons; one candidate can carry more than one reason.",
            "",
            "## Per-reason rejection counts",
            "",
        ]
        reasons = metrics.get("perReasonRejections")
        if reasons is None:
            lines.append("Diagnostics were unavailable; no per-reason claim is made.")
        else:
            lines += ["| Reason | Count |", "| --- | --- |"]
            lines += [f"| `{key}` | {value} |" for key, value in sorted(reasons.items())]
        lines += [
            "",
            "## Thresholds",
            "",
        ]
        if not payload["thresholdResults"]:
            lines.append("No optional clinical thresholds were configured; errors and forbidden statements remain mandatory.")
        else:
            lines += ["| Check | Limit | Actual | Result |", "| --- | --- | --- | --- |"]
            for row in payload["thresholdResults"]:
                lines.append(f"| {row['metric']} {row['comparator']} | {row['limit']} | {row['actual']} | "
                             f"{'PASS' if row['passed'] else 'FAIL'} |")
        lines += [
            "",
            "## Per-case results",
            "",
            "| Case | HTTP | hit@k | Rank | Mode | Expected matched | Forbidden | Candidates / verified | Latency ms |",
            "| --- | --- | --- | --- | --- | --- | --- | --- | --- |",
        ]
        for case in payload["cases"]:
            mode = f"{case.get('actualMode') or '—'}"
            if case.get("expectedMode"):
                mode += f" / {case['expectedMode']}" + ("" if case.get("modeMatched") else " MISMATCH")
            lines.append(
                f"| {case['caseId']} | {case['status'] if case['status'] is not None else 'error'} | "
                f"{'yes' if case['hitAtK'] else 'no'} | {case['rank'] if case['rank'] is not None else '—'} | "
                f"{mode} | {case['matchedExpectedStatements']}/{case['expectedStatements']} | "
                f"{case['forbiddenViolations']} | {case['candidateStatements']}/{case['verifiedStatements']} | "
                f"{case['durationMs'] if case['durationMs'] is not None else '—'} |"
            )
        errors = [case for case in payload["cases"] if case.get("error")]
        if errors:
            lines += ["", "## Case errors", ""]
            lines += [f"- `{case['caseId']}`: {case['error']}" for case in errors]
        lines += [
            "",
            "## Configuration and model evidence",
            "",
            f"- Config version: {_fmt(payload.get('configVersion'))}",
            f"- Approved rules: {payload.get('ruleCount', 'unavailable')}",
            f"- Model evidence: `{json.dumps(payload.get('modelEvidence'), sort_keys=True)}`",
            f"- Pipeline preflight: `{json.dumps(payload.get('preflight'), sort_keys=True)}`",
        ]
    lines += [
        "",
        "## Commands used",
        "",
        "```text",
        *payload["commands"],
        "```",
        "",
        "## Report privacy",
        "",
        "This report contains no case input text, model recommendation, quote, source excerpt or patient identifier.",
        "",
        "## Limitations",
        "",
        "- The statement matcher is a lexical triage heuristic with negation consistency; it is not semantic entailment or clinician review of each answer.",
        "- A matched expected statement does not prove the recommendation is complete, correct or safe for a patient.",
        "- Not covered means no verifiable statement in the loaded guidelines; it does not mean the topic or patient is safe.",
        "- Live staging and real-model evidence are not a substitute for independent clinical review.",
    ]
    model = payload.get("modelEvidence")
    if not (isinstance(model, dict) and model.get("model") and model.get("embeddingModel")):
        lines.append(
            "- Model/encoder metadata was unavailable; this report cannot independently verify which encoder and reranker served retrieval."
        )
    lines.append("")
    return "\n".join(lines)


def write_result(run_dir: Path, payload: dict[str, Any]) -> None:
    payload = dict(payload)
    payload["commands"] = payload.get("commands", [])
    _write_new(run_dir / "report.md", _markdown(payload))
    _write_new(run_dir / "run.json", json.dumps(payload, indent=2, sort_keys=True) + "\n")
    _write_csv(run_dir / "per-case.csv", payload.get("cases", []))


def blocked_payload(*, status: str, reasons: list[str], settings: Settings | None,
                    clinical_set: ClinicalSet | None, preflight: dict[str, Any] | None,
                    argv: tuple[str, ...], exit_code: int) -> dict[str, Any]:
    set_meta = clinical_set.report_metadata() if clinical_set is not None else {
        "schemaVersion": None, "labelSource": None, "instructionsVersion": None,
        "clinician": {}, "guidelineCorpus": {}, "syntheticNotice": None,
        "caseCount": 0, "clinicalEvidence": False,
    }
    return {
        "schemaVersion": "clinical-eval/1.0",
        "generatedAtUtc": datetime.now(timezone.utc).isoformat(),
        "status": status,
        "exitCode": exit_code,
        "clinicalEvidence": bool(set_meta.get("clinicalEvidence")),
        "set": set_meta,
        "endpoint": {
            "pipelineUrl": settings.pipeline_url if settings else None,
            "backendUrl": settings.backend_url if settings else None,
            "workspaceId": settings.workspace_id if settings else None,
        },
        "preflight": preflight or {},
        "modelEvidence": (preflight or {}).get("modelEvidence"),
        "configVersion": (preflight or {}).get("configVersion"),
        "ruleCount": (preflight or {}).get("ruleCount"),
        "metrics": None,
        "thresholdResults": [],
        "cases": [],
        "blockedReasons": reasons,
        "commands": _commands(argv),
    }


def _write_blocked(*, settings: Settings | None, clinical_set: ClinicalSet | None,
                   reasons: list[str], preflight: dict[str, Any] | None,
                   argv: tuple[str, ...], output_dir: Path) -> Path:
    label = "clinical-synthetic-blocked" if clinical_set is not None and not clinical_set.is_clinician_labelled else "clinical-blocked"
    run_dir = create_run_dir(output_dir, label)
    _write_commands(run_dir, argv)
    payload = blocked_payload(status="blocked", reasons=reasons, settings=settings,
                              clinical_set=clinical_set, preflight=preflight,
                              argv=argv, exit_code=4)
    write_result(run_dir, payload)
    print(json.dumps({"status": "blocked", "run": str(run_dir), "reasons": reasons}, indent=2))
    return run_dir


def _post_case(client: httpx.Client, settings: Settings,
               case: Any, config: dict[str, Any], rules: list[dict[str, Any]],
               catalog: dict[str, Any], revision: int, run_id: str) -> dict[str, Any]:
    payload = {
        "text": case.input,
        "config": config,
        "rules": rules,
        "history": [],
        "workspaceId": settings.workspace_id,
        "retrieveUrl": f"{settings.backend_url}/api/workspaces/{settings.workspace_id}/analyze/retrieve",
        "conversationId": f"clinical-eval-{run_id}-{case.id}"[:128],
        "drugCatalog": catalog,
        "catalogRevision": revision,
        "testRunDiagnostics": True,
    }
    started = time.perf_counter()
    try:
        response = client.post(f"{settings.pipeline_url}/analyze", headers=_headers(settings), json=payload)
        elapsed = (time.perf_counter() - started) * 1000.0
        if response.status_code != 200:
            return evaluate_case(case, None, status=response.status_code, duration_ms=elapsed,
                                 top_k=settings.top_k, threshold=settings.match_threshold,
                                 error=f"HTTP {response.status_code}")
        try:
            body = response.json()
        except ValueError:
            return evaluate_case(case, None, status=response.status_code, duration_ms=elapsed,
                                 top_k=settings.top_k, threshold=settings.match_threshold,
                                 error="Invalid JSON response")
        return evaluate_case(case, body, status=response.status_code, duration_ms=elapsed,
                             top_k=settings.top_k, threshold=settings.match_threshold)
    except Exception as error:  # noqa: BLE001 - record the failed case and continue
        elapsed = (time.perf_counter() - started) * 1000.0
        return evaluate_case(case, None, status=None, duration_ms=elapsed,
                             top_k=settings.top_k, threshold=settings.match_threshold,
                             error=_safe_error(error))


def run(settings: Settings, clinical_set: ClinicalSet) -> tuple[Path, int]:
    run_id = uuid.uuid4().hex[:10]
    label = "clinical-synthetic" if not clinical_set.is_clinician_labelled else "clinical"
    run_dir = create_run_dir(settings.output_dir, label)
    commands = _write_commands(run_dir, settings.argv)

    if not clinical_set.is_clinician_labelled and not settings.allow_synthetic:
        reasons = ["The set is a synthetic worked example, not clinician labels. Pass --allow-synthetic only for a format dry run."]
        payload = blocked_payload(status="blocked", reasons=reasons, settings=settings,
                                  clinical_set=clinical_set, preflight=None, argv=settings.argv,
                                  exit_code=4)
        write_result(run_dir, payload)
        print(json.dumps({"status": "blocked", "run": str(run_dir), "reasons": reasons}, indent=2))
        return run_dir, 4

    details, reasons = preflight(settings, clinical_set)
    if reasons:
        payload = blocked_payload(status="blocked", reasons=reasons, settings=settings,
                                  clinical_set=clinical_set,
                                  preflight={key: value for key, value in details.items()
                                             if not key.startswith("_") and key not in {"config", "rules", "drugCatalog"}},
                                  argv=settings.argv, exit_code=4)
        write_result(run_dir, payload)
        print(json.dumps({"status": "blocked", "run": str(run_dir), "reasons": reasons}, indent=2))
        return run_dir, 4

    config = details.pop("config")
    rules = details.pop("rules")
    catalog = details.pop("drugCatalog")
    revision = details.pop("catalogRevision")
    cases: list[dict[str, Any]] = []
    with httpx.Client(timeout=settings.timeout, follow_redirects=False) as client:
        for case in clinical_set.cases:
            cases.append(_post_case(client, settings, case, config, rules,
                                    catalog, revision, run_id))

    metrics = compute_metrics(cases, top_k=settings.top_k, threshold=settings.match_threshold)
    threshold_results = _thresholds(settings, metrics)
    failed = any(not row["passed"] for row in threshold_results)
    status = "threshold-failure" if failed else "completed"
    exit_code = 1 if failed else 0
    payload = {
        "schemaVersion": "clinical-eval/1.0",
        "generatedAtUtc": datetime.now(timezone.utc).isoformat(),
        "status": status,
        "exitCode": exit_code,
        "clinicalEvidence": clinical_set.is_clinician_labelled,
        "set": clinical_set.report_metadata(),
        "endpoint": {
            "pipelineUrl": settings.pipeline_url,
            "backendUrl": settings.backend_url,
            "workspaceId": settings.workspace_id,
            "retrieveUrl": details.get("retrieveUrl"),
            "transport": "pipeline-service /analyze with testRunDiagnostics",
        },
        "preflight": details,
        "modelEvidence": details.get("modelEvidence"),
        "configVersion": details.get("configVersion"),
        "ruleCount": details.get("ruleCount"),
        "metrics": metrics,
        "thresholdResults": threshold_results,
        "cases": cases,
        "blockedReasons": [],
        "commands": commands,
    }
    write_result(run_dir, payload)
    print(json.dumps({
        "status": status,
        "run": str(run_dir),
        "metrics": metrics,
        "thresholdResults": threshold_results,
    }, indent=2))
    return run_dir, exit_code


def main(argv: list[str] | None = None) -> int:
    values = list(sys.argv[1:] if argv is None else argv)
    pre_parser = argparse.ArgumentParser(add_help=False)
    pre_parser.add_argument("--clinical-set", type=Path)
    pre_parser.add_argument("--output-dir", type=Path,
                            default=Path(os.environ.get("EVAL_OUTPUT_DIR", "eval/results/clinical")))
    known, _ = pre_parser.parse_known_args(values)
    output_dir = known.output_dir
    clinical_set: ClinicalSet | None = None
    load_error: str | None = None
    if known.clinical_set is None:
        load_error = "A clinical set is required via --clinical-set."
    else:
        try:
            clinical_set = load_clinical_set(known.clinical_set)
        except ClinicalSetError as error:
            load_error = str(error)

    settings, errors = parse_settings(values)
    reasons = list(errors)
    if load_error is not None:
        reasons.append(load_error)
    if reasons:
        _write_blocked(settings=settings, clinical_set=clinical_set, reasons=reasons,
                       preflight=None, argv=tuple(values), output_dir=output_dir)
        return 4
    assert settings is not None and clinical_set is not None
    _, exit_code = run(settings, clinical_set)
    return exit_code


if __name__ == "__main__":
    raise SystemExit(main())
