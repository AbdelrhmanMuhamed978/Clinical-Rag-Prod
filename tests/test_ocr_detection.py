"""OCR detection must distinguish section dividers from genuinely image-only pages."""

from __future__ import annotations

import fitz

from colab.parsing import parse_pdf_bytes


def test_short_section_title_is_not_flagged_but_a_full_page_image_is() -> None:
    with fitz.open() as document:
        title = document.new_page(width=595, height=842)
        title.insert_text((50, 100), "Clinical management", fontsize=20)
        source = document.new_page(width=595, height=842)
        source.insert_text((50, 100), "Synthetic scanned page source", fontsize=20)
        image = source.get_pixmap(dpi=100).tobytes("png")
        scanned = document.new_page(width=595, height=842)
        scanned.insert_image(scanned.rect, stream=image)
        data = document.tobytes()

    parsed = parse_pdf_bytes(data)
    assert parsed["needsOcrPages"] == [3]
    assert parsed["pages"][0]["needsOcr"] is False
    assert parsed["pages"][1]["needsOcr"] is False
    assert parsed["pages"][2]["needsOcr"] is True
