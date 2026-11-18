"""Deterministic, privacy-safe metrics for a clinical evaluation run.

Reports never contain case text, generated recommendations or source excerpts.
The statement matcher is a lexical triage heuristic, not semantic entailment.
"""

from __future__ import annotations

import math
import re
import unicodedata
from typing import Any, Iterable

from .loader import ClinicalCase


TOKEN = re.compile(r"[a-z0-9]+")
NEGATION = re.compile(
    r"\b(?:not|no|never|avoid|without|withhold|contraindicat\w*|cannot|can't|isn't|aren't|"
    r"doesn't|don't|shouldn't|do not|does not|should not)\b",
    re.IGNORECASE,
)
STOPWORDS = frozenset({
    "a", "an", "the", "and", "or", "of", "to", "in", "on", "for", "with", "at",
    "by", "from", "as", "is", "are", "was", "were", "be", "been", "being",
    "should", "could", "would", "may", "might", "can", "must", "recommend",
    "recommended", "recommends", "suggest", "suggests", "suggested", "consider",
    "considered", "offer", "offered", "use", "used", "using", "patient",
    "patients", "adult", "adults", "clinical", "clinician", "guideline",
    "guidelines", "evidence", "during", "if", "when",
    "than", "that", "this", "these", "those", "it", "its", "their", "they",
    "them", "we", "our", "you", "your", "all", "any", "each", "every", "both",
    "also", "only", "such", "into", "over", "under", "about", "after", "before",
    "more", "most", "less", "least", "very", "same", "other", "another",
})
REASON_KEYS = (
    "empty_draft", "json_parse_error", "quote_not_found", "formatting_mismatch", "number_missing",
    "stronger_than_source", "negation_conflict", "scope_mismatch",
    "topic_mismatch", "truncated", "deduplicated", "below_threshold",
)


def _stem(token: str) -> str:
    # Mirror the existing pipeline's bounded prefix matching for lexical triage.
    return token if token.isdigit() else token[:5]


def _tokens(text: str) -> list[str]:
    normalized = unicodedata.normalize("NFKC", text).casefold().replace("’", "'")
    return TOKEN.findall(normalized)


def content_tokens(text: str) -> set[str]:
    tokens = _tokens(text)
    result: set[str] = set()
    for token in tokens:
        if token in STOPWORDS:
            continue
        if len(token) < 2 and not token.isdigit():
            continue
        result.add(_stem(token))
    return result


def has_negation(text: str) -> bool:
    return bool(NEGATION.search(unicodedata.normalize("NFKC", text)))


def statement_coverage(expected: str, actual: str) -> float:
    expected_tokens = content_tokens(expected)
    if not expected_tokens:
        return 0.0
    return len(expected_tokens & content_tokens(actual)) / len(expected_tokens)


def statement_matches(expected: str, actual: str, threshold: float) -> bool:
    # A statement with an explicit negation must not match an affirmative claim,
    # and an affirmative expected statement must not match a negated claim.
    if has_negation(expected) != has_negation(actual):
        return False
    return statement_coverage(expected, actual) >= threshold


def page_numbers(value: Any) -> list[int]:
    return [int(number) for number in re.findall(r"\d+", str(value or ""))]


def page_matches(expected: int, value: Any) -> bool:
    numbers = page_numbers(value)
    if not numbers:
        return False
    if len(numbers) >= 2 and numbers[0] <= expected <= numbers[-1]:
        return True
    return expected in numbers


def _file_name(value: Any) -> str:
    name = str(value or "").strip().replace("\\", "/").rsplit("/", 1)[-1].casefold()
    return name[:-4] if name.endswith(".pdf") else name


def source_matches(expected_file_name: str, expected_page: int, source: dict[str, Any]) -> bool:
    if not page_matches(expected_page, source.get("page")):
        return False
    target = _file_name(expected_file_name)
    if not target:
        return False
    candidates = [_file_name(source.get("file_name")), _file_name(source.get("title")),
                  _file_name(source.get("label")), _file_name(source.get("id"))]
    for candidate in candidates:
        if not candidate:
            continue
        if candidate == target:
            return True
        # Titles often include society/year/version around the file name. Require
        # a meaningful substring in either direction to avoid a one-letter match.
        if len(target) >= 4 and target in candidate:
            return True
        if len(candidate) >= 4 and candidate in target:
            return True
    return False


def _verified_texts(response: dict[str, Any]) -> list[str]:
    texts: list[str] = []
    for problem in response.get("problems") or []:
        if not isinstance(problem, dict):
            continue
        for item in problem.get("items") or []:
            if not isinstance(item, dict):
                continue
            for name in ("recommendation", "quote"):
                value = item.get(name)
                if isinstance(value, str) and value.strip():
                    texts.append(value)
    return texts


def _unverified_texts(response: dict[str, Any]) -> list[str]:
    texts: list[str] = []
    for problem in response.get("problems") or []:
        if not isinstance(problem, dict):
            continue
        for item in problem.get("unverified") or []:
            if not isinstance(item, dict):
                continue
            for name in ("recommendation", "quote"):
                value = item.get(name)
                if isinstance(value, str) and value.strip():
                    texts.append(value)
    return texts


def _source_rank(case: ClinicalCase, response: dict[str, Any]) -> tuple[int | None, bool]:
    """Best within-problem rank and whether the matching section was present."""
    best: int | None = None
    section_matched = False
    for problem in response.get("problems") or []:
        if not isinstance(problem, dict):
            continue
        seen: set[str] = set()
        rank = 0
        for source in problem.get("sources") or []:
            if not isinstance(source, dict):
                continue
            identity = str(source.get("id") or source.get("chunk_id") or (source.get("label"), source.get("page")))
            if identity in seen:
                continue
            seen.add(identity)
            rank += 1
            for expected in case.expected_documents:
                if source_matches(expected.file_name, expected.page, source):
                    if best is None or rank < best:
                        best = rank
                    if expected.section and isinstance(source.get("section"), str):
                        if expected.section.casefold() == source["section"].casefold():
                            section_matched = True
    return best, section_matched


def _diagnostics(response: dict[str, Any]) -> tuple[dict[str, Any] | None, bool]:
    raw = response.get("diagnostics")
    if not isinstance(raw, list):
        return None, False
    totals = {"drafts": 0, "candidates": 0, "verified": 0,
              "reasons": {key: 0 for key in REASON_KEYS}}
    for entry in raw:
        if not isinstance(entry, dict):
            continue
        for name in ("drafts", "candidates", "verified"):
            value = entry.get(name)
            if type(value) is int and value >= 0:
                totals[name] += value
        reasons = entry.get("reasons")
        if isinstance(reasons, dict):
            for key, value in reasons.items():
                if isinstance(key, str) and type(value) is int and value >= 0:
                    totals["reasons"][key] = totals["reasons"].get(key, 0) + value
    return totals, True


def evaluate_case(case: ClinicalCase, response: dict[str, Any] | None, *, status: int | None,
                  duration_ms: float | None, top_k: int, threshold: float,
                  error: str | None = None) -> dict[str, Any]:
    """Evaluate one case and return only metrics/identifiers, never generated text."""
    result: dict[str, Any] = {
        "caseId": case.id,
        "status": status,
        "durationMs": None if duration_ms is None else round(duration_ms, 3),
        "error": error,
        "hitAtK": False,
        "rank": None,
        "sectionMatched": False,
        "expectedStatements": len(case.expected_statements),
        "matchedExpectedStatements": 0,
        "expectedStatementCoverage": [],
        "expectedMode": case.expected_mode,
        "actualMode": None,
        "modeMatched": case.expected_mode is None,
        "pendingClinicianFields": len(case.pending_clinician_fields),
        "forbiddenViolations": 0,
        "forbiddenRejected": 0,
        "verifiedItems": 0,
        "draftCount": 0,
        "candidateStatements": 0,
        "verifiedStatements": 0,
        "deduplicatedStatements": 0,
        "pipelineNotCoveredCount": 0,
        "diagnosticsAvailable": False,
        "diagnostics": None,
    }
    if response is None or error is not None or status is None or not 200 <= status < 300:
        return result
    if not isinstance(response.get("problems"), list):
        result["error"] = "Invalid verified-result shape"
        return result

    plan_source = response.get("plan_source")
    actual_mode = ("question" if plan_source == "single_question"
                   else "case" if plan_source in ("rules", "llm+rules")
                   else "chat" if plan_source == "chat" else None)
    result["actualMode"] = actual_mode
    result["modeMatched"] = case.expected_mode is None or case.expected_mode == actual_mode

    rank, section_matched = _source_rank(case, response)
    result["rank"] = rank
    result["hitAtK"] = rank is not None and rank <= top_k
    result["sectionMatched"] = section_matched

    verified = _verified_texts(response)
    unverified = _unverified_texts(response)
    result["verifiedItems"] = len(verified)

    coverages: list[float] = []
    matched = 0
    for expected in case.expected_statements:
        best = max((statement_coverage(expected, actual) for actual in verified), default=0.0)
        is_match = any(statement_matches(expected, actual, threshold) for actual in verified)
        coverages.append(round(best, 4))
        matched += int(is_match)
    result["matchedExpectedStatements"] = matched
    result["expectedStatementCoverage"] = coverages

    violations = 0
    for forbidden in case.forbidden_statements:
        if any(statement_matches(forbidden, actual, threshold) for actual in verified):
            violations += 1
    result["forbiddenViolations"] = violations
    result["forbiddenRejected"] = sum(
        int(any(statement_matches(forbidden, actual, threshold) for actual in unverified))
        for forbidden in case.forbidden_statements
    )

    diagnostics, available = _diagnostics(response)
    result["diagnosticsAvailable"] = available
    result["diagnostics"] = diagnostics
    if diagnostics is not None:
        result["draftCount"] = diagnostics["drafts"]
        result["candidateStatements"] = diagnostics["candidates"]
        result["verifiedStatements"] = diagnostics["verified"]
        result["deduplicatedStatements"] = diagnostics["reasons"].get("deduplicated", 0)
    else:
        # Fallback only; the required full-path runner reports diagnostics as
        # unavailable when it cannot use the authenticated diagnostics path.
        result["candidateStatements"] = len(verified) + len(unverified)
        result["verifiedStatements"] = len(verified)

    not_covered = response.get("not_covered")
    result["pipelineNotCoveredCount"] = len(not_covered) if isinstance(not_covered, list) else 0
    return result


def percentile(values: Iterable[float], fraction: float) -> float:
    ordered = sorted(value for value in values if math.isfinite(value))
    if not ordered:
        return 0.0
    index = max(0, min(len(ordered) - 1, math.ceil(len(ordered) * fraction) - 1))
    return ordered[index]


def compute_metrics(cases: list[dict[str, Any]], *, top_k: int, threshold: float) -> dict[str, Any]:
    total = len(cases)
    scored = [case for case in cases if case.get("error") is None and case.get("status") is not None
              and 200 <= case["status"] < 300]
    errors = total - len(scored)
    expected_total = sum(case["expectedStatements"] for case in scored)
    expected_matched = sum(case["matchedExpectedStatements"] for case in scored)
    draft_total = sum(case["draftCount"] for case in scored)
    candidate_total = sum(case["candidateStatements"] for case in scored)
    verified_total = sum(case["verifiedStatements"] for case in scored)
    deduplicated_total = sum(case["deduplicatedStatements"] for case in scored)
    rejected_total = max(candidate_total - verified_total - deduplicated_total, 0)

    reasons: dict[str, int] = {key: 0 for key in REASON_KEYS}
    diagnostics_seen = False
    for case in scored:
        diagnostics = case.get("diagnostics")
        if not isinstance(diagnostics, dict):
            continue
        diagnostics_seen = True
        for key, value in diagnostics.get("reasons", {}).items():
            if isinstance(key, str) and type(value) is int and value >= 0:
                reasons[key] = reasons.get(key, 0) + value

    durations = [case["durationMs"] for case in cases if case.get("durationMs") is not None]
    hit_count = sum(1 for case in scored if case["hitAtK"])
    reciprocal_sum = sum((1.0 / case["rank"]) for case in scored if case["rank"] and case["hitAtK"])
    not_covered_count = sum(1 for case in scored if case["matchedExpectedStatements"] == 0)
    cases_with_verified = sum(1 for case in scored if case["verifiedItems"] > 0)
    forbidden_violations = sum(case["forbiddenViolations"] for case in scored)
    mode_cases = [case for case in scored if case.get("expectedMode")]
    mode_matched = sum(1 for case in mode_cases if case.get("modeMatched"))
    pending_label_cases = sum(1 for case in scored if case.get("pendingClinicianFields", 0) > 0)

    metrics: dict[str, Any] = {
        "totalCases": total,
        "scoredCases": len(scored),
        "errorCases": errors,
        "errorRate": round(errors / total, 6) if total else 0.0,
        "hitAtK": round(hit_count / len(scored), 6) if scored else 0.0,
        "mrr": round(reciprocal_sum / len(scored), 6) if scored else 0.0,
        "verifiedRate": round(cases_with_verified / len(scored), 6) if scored else 0.0,
        "expectedStatementVerifiedRate": round(expected_matched / expected_total, 6) if expected_total else 0.0,
        "notCoveredRate": round(not_covered_count / len(scored), 6) if scored else 0.0,
        "modeAccuracy": round(mode_matched / len(mode_cases), 6) if mode_cases else None,
        "modeExpectedCases": len(mode_cases),
        "modeMatchedCases": mode_matched,
        "pendingClinicianLabelCases": pending_label_cases,
        "unsupportedClaimRate": round(rejected_total / candidate_total, 6) if candidate_total else 0.0,
        "forbiddenStatementViolations": forbidden_violations,
        "draftCount": draft_total,
        "statementCandidates": candidate_total,
        "verifiedStatements": verified_total,
        "rejectedStatements": rejected_total,
        "deduplicatedStatements": deduplicated_total,
        "expectedStatements": expected_total,
        "matchedExpectedStatements": expected_matched,
        "perReasonRejections": reasons if diagnostics_seen else None,
        "diagnosticsAvailable": diagnostics_seen,
        "latencyMilliseconds": {
            "count": len(durations),
            "p50": round(percentile(durations, 0.50), 3),
            "p95": round(percentile(durations, 0.95), 3),
            "p99": round(percentile(durations, 0.99), 3),
            "mean": round(sum(durations) / len(durations), 3) if durations else 0.0,
        },
        "topK": top_k,
        "statementMatchThreshold": threshold,
    }
    return metrics


__all__ = [
    "REASON_KEYS",
    "compute_metrics",
    "content_tokens",
    "evaluate_case",
    "has_negation",
    "page_matches",
    "percentile",
    "source_matches",
    "statement_coverage",
    "statement_matches",
]
