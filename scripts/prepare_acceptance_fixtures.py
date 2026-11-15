"""Create visibly synthetic five-page PDFs for integration acceptance, not clinical evaluation."""

import argparse
import hashlib
import json
from pathlib import Path

import pymupdf as fitz


PROVENANCE = "Locally authored synthetic acceptance artifact; not a medical society guideline or clinical evidence."
PAGE_MARKERS = {
    1: "SELECTED-PAGE-ONE",
    2: "GAP-PAGE-TWO-DO-NOT-INDEX",
    3: "SELECTED-PAGE-THREE",
    4: "GAP-PAGE-FOUR-DO-NOT-INDEX",
    5: "SELECTED-PAGE-FIVE",
}


def page_body(number: int, status: str) -> str:
    prefix = "PENDING-" if status == "DRAFT" else ""
    bodies = {
        1: (
            "1. Low-pressure review - synthetic test protocol\n\n"
            "An isolated low systolic blood pressure does not establish shock. "
            "The dummy low-pressure review marker is " + prefix + "BLUE. "
            "This label is arbitrary integration-test data, not a diagnostic or treatment threshold.\n\n"
            "For this fixture, a medication mention and a low-pressure value may be used to test a "
            "manually created review rule. This must not automatically stop any medicine. "
            "Rule approval, enablement and source status are separate application controls."
        ),
        2: (
            "2. Deliberately excluded appendix\n\n"
            "This page must not be chunked when the selection is pages 1, 3 and 5. "
            "Its unique exclusion marker appears nowhere else in this document. "
            "It contains no patient information and no clinical recommendations.\n\n"
            "The dummy appendix label is CIRCULAR-PAPERCLIP. This arbitrary label must be absent "
            "from the chunks produced for the non-contiguous selection."
        ),
        3: (
            "3. Kidney-function review - synthetic test protocol\n\n"
            "An isolated creatinine value does not establish acute kidney injury. "
            "The dummy kidney-function review marker is " + prefix + "GREEN. "
            "This label is arbitrary integration-test data, not a diagnostic or dosing instruction.\n\n"
            "Baseline measurements, changes over time and relevant clinical assessment are not "
            "supplied by this document. The synthetic case below must not be used as real "
            "patient data or to certify clinical performance."
        ),
        4: (
            "4. Second deliberately excluded appendix\n\n"
            "This page must not be chunked when the selection is pages 1, 3 and 5. "
            "Its unique exclusion marker appears nowhere else in this document. "
            "It contains no patient information and no clinical recommendations.\n\n"
            "The dummy appendix label is HEXAGONAL-STAPLER. This arbitrary label must be absent "
            "from the chunks produced for the non-contiguous selection."
        ),
        5: (
            "5. Medication review - synthetic test protocol\n\n"
            "Medication mentions do not confirm current exposure, dose, timing or indication. "
            "The dummy medication review marker is " + prefix + "AMBER. "
            "This label is arbitrary integration-test data, not medication advice.\n\n"
            "Review the dummy medication list with a clinician rather than automatically stopping "
            "a medicine based on this test protocol. Source quotations may establish exact text "
            "matches for an integration test; they do not establish clinical correctness."
        ),
    }
    return bodies[number]


def create_pdf(path: Path, status: str) -> dict:
    title = f"SYNTHETIC RAG acceptance protocol - {status} test artifact"
    with fitz.open() as document:
        document.set_metadata({"title": title, "author": "Local synthetic acceptance fixture generator",
                               "subject": PROVENANCE, "keywords": f"SYNTHETIC, TEST ONLY, {status}, NO PATIENT DATA"})
        for number in range(1, 6):
            page = document.new_page(width=595, height=842)
            text = (f"{title}\n\nSYNTHETIC - TEST ONLY - NOT CLINICAL GUIDANCE\n"
                    f"Artifact status: {status}. Year: 2026.\n"
                    f"Page {number} of 5. Marker: {PAGE_MARKERS[number]}\n\n"
                    f"{PROVENANCE}\n\n{page_body(number, status)}\n\n"
                    "No real patient, personal identifier, credential or uploaded source is used here.")
            available = page.insert_textbox(fitz.Rect(45, 45, 550, 795), text, fontname="helv", fontsize=11)
            if available < 0:
                raise ValueError("Synthetic fixture text did not fit the page.")
        document.save(path, garbage=4, deflate=True)
    with fitz.open(path) as document:
        assert document.page_count == 5
        for number, page in enumerate(document, 1):
            extracted = page.get_text()
            assert PAGE_MARKERS[number] in extracted
            assert "SYNTHETIC - TEST ONLY - NOT CLINICAL GUIDANCE" in extracted
            assert f"Artifact status: {status}" in extracted
            assert all(marker not in extracted for other, marker in PAGE_MARKERS.items() if other != number)
    return {"path": path.name, "status": status.lower(), "bytes": path.stat().st_size,
            "sha256": hashlib.sha256(path.read_bytes()).hexdigest(), "pages": 5,
            "provenance": PROVENANCE}


def build_fixtures(destination: Path) -> dict:
    # Refuse to replace previous fixtures or original evaluation artifacts.
    destination.mkdir(parents=True, exist_ok=False)
    files = [create_pdf(destination / f"synthetic-{status.lower()}-five-pages.pdf", status)
             for status in ("FINAL", "DRAFT")]
    manifest = {
        "format": 1, "scope": "Synthetic integration acceptance only; not clinical/publication evidence",
        "publisher": "Local synthetic acceptance fixture generator - NOT a medical society", "year": 2026,
        "sourceStatusMeaning": "FINAL means this synthetic fixture is finalized; DRAFT is its explicitly unfinished test variant.",
        "documents": files, "selectedPages": [1, 3, 5], "excludedPages": [2, 4],
        "selectedPageMarkers": [PAGE_MARKERS[n] for n in (1, 3, 5)],
        "excludedPageMarkers": [PAGE_MARKERS[n] for n in (2, 4)],
        "questions": [
            {"question": "According to the synthetic test protocol, does an isolated low systolic blood pressure establish shock?",
             "page": 1, "requiredQuote": "An isolated low systolic blood pressure does not establish shock."},
            {"question": "What is the dummy kidney-function review marker in this synthetic protocol?",
             "page": 3, "finalAnswer": "GREEN", "draftAnswer": "PENDING-GREEN"},
        ],
        "syntheticCase": (
            "SYNTHETIC ACCEPTANCE CASE - not a real patient and not a request for clinical certification. "
            "Dummy adult record: SBP 82 mmHg, DBP 50 mmHg, HR 100 bpm, creatinine 1.8 mg/dL. "
            "The dummy medication list mentions lisinopril and metformin. Baseline kidney function, "
            "measurement trend, urine output, exposure and indication are unknown. "
            "Using only the synthetic protocol, list review considerations without diagnosing shock or AKI "
            "from isolated values and without automatically stopping medication."
        ),
        "ruleScenario": {"scope": "Manually create/approve/enable a test-only review rule through the app; no seed or publication bypass",
                         "drugClass": "acei_arb", "condition": {"field": "sbp", "op": "<", "value": 90},
                         "expectedFact": {"field": "sbp", "value": 82},
                         "seedRulesRemainDisabled": True},
    }
    (destination / "acceptance-fixture-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    return {"directory": str(destination.resolve()), "documents": files, "selectedPages": [1, 3, 5]}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-directory", type=Path, required=True, help="New directory; previous artifacts are never overwritten")
    args = parser.parse_args()
    print(json.dumps(build_fixtures(args.output_directory), indent=2))


if __name__ == "__main__":
    main()
