"""Offline replay for the pending real-guideline CrAg case.

This is NOT a clinical evaluation and NOT a retrieval or model run. It parses
two fixed pages from a locally held PDF, supplies fixed policy candidates for
the expected and forbidden statements, and records what the deterministic
verifier does with them.

Run it from the current worktree, and again from an extracted older revision,
to produce an honest before/after record:

    python eval/crag_offline_replay.py --pdf <path> --case <case.json> \
        --out eval/results/crag-before.json
    python <older-repo>/eval/crag_offline_replay.py --repo <older-repo> \
        --pdf <path> --case <case.json> --out eval/results/crag-after.json

The report contains no case input, patient text or model recommendation text.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SCREENING_QUOTE = (
    "Screening for cryptococcal antigen followed by pre-emptive antifungal therapy among cryptococcal "
    "antigen-positive people to prevent the development of invasive cryptococcal disease is recommended "
    "before initiating or reinitiating antiretroviral therapy (ART) for adults and adolescents living with HIV "
    "who have a CD4 cell count <100 cells/mm3."
)
INDUCTION_START = "A single high dose (10 mg/kg) of liposomal amphotericin B"
NON_SUPPORTING_QUOTE = (
    "Routine use of adjunctive corticosteroid therapy during the induction phase is not recommended in "
    "treating adults, adolescents and children who have HIV-associated cryptococcal meningitis."
)


def canonical(value: str) -> str:
    return re.sub(r"\s+", " ", value).strip().casefold()


def rejection_key(issue: str) -> str:
    if "quote not found" in issue:
        return "quote_not_found"
    if "negation" in issue:
        return "negation_conflict"
    if "number" in issue:
        return "number_missing"
    if "stronger" in issue:
        return "stronger_than_source"
    if "topic" in issue:
        return "topic_mismatch"
    if "support" in issue or "scope" in issue or "widen" in issue or "narrow" in issue:
        return "scope_mismatch"
    return "other"


def run_draft(P, sources, expected: str, forbidden: str, forbidden_quote: str) -> dict:
    draft = (
        "SOURCE: S1\n"
        f"RECOMMENDATION: {expected}\n"
        f"QUOTE: {SCREENING_QUOTE}\n\n"
        "SOURCE: S2\n"
        f"RECOMMENDATION: {forbidden}\n"
        f"QUOTE: {forbidden_quote}\n"
    )
    if hasattr(P, "verify_draft"):
        result = P.verify_draft(draft, sources, {"crypt", "antig", "screen"})
        return {
            "verified": result["items"],
            "rejected": result["unverified"],
            "reasons": {key: value for key, value in result["diagnostics"]["reasons"].items() if value},
        }
    verified, rejected, reasons = [], [], {}
    for item in P.parse_items(draft):
        item = P.repair_item(item, sources)
        issues = P.verify_item(item, sources)
        entry = {"recommendation": item["rec"], "quote": item["quote"]}
        if item.get("repaired"):
            entry["repaired"] = item["repaired"]
        if issues:
            entry["issues"] = issues
            rejected.append(entry)
            for issue in issues:
                key = rejection_key(issue)
                reasons[key] = reasons.get(key, 0) + 1
        else:
            verified.append(entry)
    return {"verified": verified, "rejected": rejected, "reasons": reasons}


def summarize(result: dict, expected: str, forbidden: str) -> dict:
    verified = result["verified"]
    expected_verified = any(canonical(item["recommendation"]) == canonical(expected) for item in verified)
    forbidden_violations = sum(
        1 for item in verified if canonical(item["recommendation"]) == canonical(forbidden))
    return {
        "verifiedRate": 1.0 if verified else 0.0,
        "expectedStatementVerified": expected_verified,
        "expectedStatementVerifiedRate": 1.0 if expected_verified else 0.0,
        "forbiddenStatementViolations": forbidden_violations,
        "rejectionReasons": result["reasons"],
        "quoteRewrites": sum(
            1 for item in verified + result["rejected"]
            if "source text" in str(item.get("repaired") or "").casefold()),
        "verifiedItemQuoteRewritten": any(
            "source text" in str(item.get("repaired") or "").casefold() for item in verified),
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=ROOT,
                        help="Repository root whose python-backend/pipeline.py and colab/parsing.py to import.")
    parser.add_argument("--pdf", type=Path, required=True)
    parser.add_argument("--case", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args(argv)
    if args.out.exists():
        raise SystemExit(f"Refusing to overwrite existing output: {args.out}")

    repo = args.repo.resolve()
    sys.path.insert(0, str(repo))
    backend = repo / "python-backend"
    if backend.is_dir():
        sys.path.insert(0, str(backend))
    import pipeline as P
    from colab.parsing import parse_pdf_bytes

    payload = json.loads(args.case.read_text(encoding="utf-8"))
    case = payload["cases"][0]
    expected = case["expectedStatements"][0]
    forbidden = case["forbiddenStatements"][0]
    pages = sorted({document["page"] for document in case["expectedDocuments"]})
    expected_mode = case.get("expectedMode")

    pdf_bytes = args.pdf.read_bytes()
    parsed = parse_pdf_bytes(pdf_bytes)
    page_text = {
        page: next(
            part.split("\n", 1)[1] for part in parsed["text"].split("--- Page ")
            if part.startswith(f"{page} ---")
        )
        for page in pages
    }
    screening_page = next(
        page for page, text in page_text.items()
        if "cryptococcal antigen followed by pre-emptive antifungal therapy" in text)
    induction_page = next(
        page for page, text in page_text.items() if INDUCTION_START in text)
    induction_start = page_text[induction_page].find(INDUCTION_START)
    induction_quote = page_text[induction_page][induction_start:induction_start + 520]
    induction_quote = induction_quote.split("Alternative induction regimes")[0].strip()
    other_page = next(page for page in pages if page != screening_page)

    chunks = [
        {
            "society": "WHO",
            "title": "WHO guidelines for managing advanced HIV disease and rapid initiation of antiretroviral therapy",
            "version": "2026",
            "year": 2026,
            "status": "final",
            "section": "TABLES|Prevention and screening",
            "page": screening_page,
            "text": page_text[screening_page],
            "rerank_score": 0.95,
        },
        {
            "society": "WHO",
            "title": "WHO guidelines for managing advanced HIV disease and rapid initiation of antiretroviral therapy",
            "version": "2026",
            "year": 2026,
            "status": "final",
            "section": "Cryptococcal disease" if other_page == 41 else "Prevention and screening",
            "page": other_page,
            "text": page_text[other_page],
            "rerank_score": 0.94,
        },
    ]
    sources = P.number_sources(P.sources_from_chunks(chunks), max_n=2)

    main_result = run_draft(P, sources, expected, forbidden, NON_SUPPORTING_QUOTE)
    guard_result = run_draft(P, sources, expected, forbidden, induction_quote)
    guard_forbidden_verified = any(
        canonical(item["recommendation"]) == canonical(forbidden)
        for item in guard_result["verified"])

    artifacts = {}
    for page in pages:
        text = page_text[page]
        artifacts[str(page)] = {
            "fused_screening_marker": text.count("Screeningc") + text.count("Screeninga"),
            "html_br": text.count("<br>"),
            "wrapped_dash": text.count("antigen\u2013 positive"),
        }

    output = {
        "schemaVersion": "crag-offline-replay/1.0",
        "generatedAtUtc": datetime.now(timezone.utc).isoformat(),
        "status": "offline-replay",
        "clinicalEvidence": False,
        "labelSource": payload.get("labelSource"),
        "scope": (
            "Deterministic verifier replay on two fixed parsed pages. No retrieval, "
            "model, network or clinician review. Not clinical evidence."
        ),
        "case": {
            "id": case["id"],
            "set": args.case.name,
            "expectedMode": expected_mode,
            "expectedPages": pages,
            "pendingClinicianFields": case.get("pendingClinicianFields", []),
        },
        "source": {
            "document": args.pdf.name,
            "sha256": hashlib.sha256(pdf_bytes).hexdigest(),
            "parser": ("python-backend/pipeline.py + colab/parsing.py"
                       if backend.is_dir() else "pipeline.py + colab/parsing.py"),
        },
        "parserArtifacts": artifacts,
        "metrics": summarize(main_result, expected, forbidden) | {
            "cases": 1,
            "hitAtK": None,
            "modeAccuracy": None,
        },
        "guardProbe": {
            "description": (
                "The forbidden induction statement with its exact supporting quote. "
                "The deterministic verifier accepts it, so this probe does not test "
                "lead selection or the eval's forbidden-lead rule."
            ),
            "forbiddenStatementVerifierAccepted": guard_forbidden_verified,
            "leadSelectionTested": False,
        },
        "unavailable": {
            "hitAtK": "No retrieval run; the expected pages were supplied as fixed context.",
            "modeAccuracy": "The offline verifier does not produce plan_source; live mode is unmeasured.",
            "screeningUnavailableStatement": (
                "Pending clinician extraction from the source. Engineering did not fill it."
            ),
        },
        "limitations": [
            "Not a clinical evaluation and not clinician-labelled evidence.",
            "The candidates are fixed policy replays, not model output.",
            "The expected pages were supplied; hit@k, MRR and retrieval latency were not measured.",
            "The forbidden statement is rejected in the main replay only because its fixed quote "
            "does not support it; the guard probe shows the verifier accepts a correctly quoted "
            "induction statement, so lead-selection safety is unmeasured.",
            "The screening-unavailable expectation remains pending clinician input.",
        ],
    }
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(output, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"out": str(args.out), "metrics": output["metrics"],
                      "guardProbe": output["guardProbe"], "parserArtifacts": artifacts}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
