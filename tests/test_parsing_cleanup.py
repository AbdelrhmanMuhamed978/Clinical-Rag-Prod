from __future__ import annotations

import pymupdf

from colab.parsing import parse_pdf_bytes
from evidence_text import normalize_evidence_text


def _artifact_pdf() -> bytes:
    with pymupdf.open() as document:
        page = document.new_page(width=595, height=842)
        page.insert_text((50, 80), "Synthetic pre&amp;#45;emptive therapy and antigen- positive marker", fontsize=12)

        page.insert_text((50, 120), "Screening", fontsize=12)
        width = pymupdf.get_text_length("Screening", fontname="helv", fontsize=12)
        page.insert_text((50 + width, 120), "c", fontsize=6)
        page.insert_text((50 + width + 3, 120), " for synthetic review", fontsize=12)

        # A small ruled table with the same artifacts in a cell.
        page.draw_rect(pymupdf.Rect(50, 200, 500, 280), color=(0.3, 0.3, 0.3), width=0.8)
        page.draw_line((150, 200), (150, 280), color=(0.3, 0.3, 0.3), width=0.8)
        page.draw_line((50, 240), (500, 240), color=(0.3, 0.3, 0.3), width=0.8)
        page.insert_text((55, 225), "Year", fontsize=9)
        page.insert_text((155, 225), "Recommendations", fontsize=9)
        page.insert_text((55, 265), "2018", fontsize=9)
        page.insert_text((155, 265), "Screening", fontsize=9)
        table_width = pymupdf.get_text_length("Screening", fontname="helv", fontsize=9)
        page.insert_text((155 + table_width, 265), "c", fontsize=4.5)
        page.insert_text((155 + table_width + 2, 265), " pre&amp;#45;emptive antigen- positive", fontsize=9)
        return document.tobytes()


def test_parser_removes_entities_footnotes_and_wrapped_dash_artifacts():
    parsed = parse_pdf_bytes(_artifact_pdf())
    text = parsed["text"]
    assert "&amp;" not in text and "&lt;" not in text and "&gt;" not in text and "&#" not in text
    assert "Screeningc" not in text
    assert "Screening for synthetic review" in text
    assert "pre-emptive" in text
    assert "antigen- positive" not in text and "antigen-positive" in text
    assert "[TABLES]" in text
    markers = parsed["footnoteMarkers"]
    assert any(marker["page"] == 1 and marker["marker"] == "c" and marker["offset"] >= 0 for marker in markers)


def test_shared_normalizer_is_idempotent_and_joins_dash_wraps():
    value = "antigen–\n positive pre&amp;#45;emptive"
    cleaned = normalize_evidence_text(value)
    assert cleaned == "antigen–positive pre-emptive"
    assert normalize_evidence_text(cleaned) == cleaned


def _header_marker_pdf() -> bytes:
    with pymupdf.open() as document:
        for page_number in (1, 2):
            page = document.new_page(width=595, height=842)
            page.insert_text((50, 70), "Synthetic running header", fontsize=10)
            header_width = pymupdf.get_text_length("Synthetic running header", fontname="helv", fontsize=10)
            page.insert_text((50 + header_width, 70), "a", fontsize=6)
            page.insert_text((50, 120), f"Body page {page_number}", fontsize=12)
            body_width = pymupdf.get_text_length(f"Body page {page_number}", fontname="helv", fontsize=12)
            page.insert_text((50 + body_width, 120), "b", fontsize=6)
        return document.tobytes()


def test_dropped_header_marker_does_not_shift_body_marker_offsets():
    parsed = parse_pdf_bytes(_header_marker_pdf())
    page_one = [marker for marker in parsed["footnoteMarkers"] if marker["page"] == 1]
    assert [marker["marker"] for marker in page_one] == ["b"]
    assert page_one[0]["offset"] == len("Body page 1")
    assert page_one[0]["context"].startswith("Body page 1")
    assert "Synthetic running header" not in page_one[0]["context"]
    assert "Synthetic running header" not in parsed["text"]
