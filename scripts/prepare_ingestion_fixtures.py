"""Generate synthetic, structurally realistic guideline PDFs for ingestion tests.

No copyrighted or patient content is used. The files exercise layout and text
normalisation only; they are not clinical evidence and contain no OCR engine.
"""

import argparse
import hashlib
import json
from pathlib import Path

import pymupdf as fitz


HEADER = "SYNTHETIC INGESTION GUIDELINE — TEST ONLY"
PROVENANCE = "Locally authored synthetic ingestion fixture; not clinical guidance."
FONT_CANDIDATES = (
    Path("C:/Windows/Fonts/arial.ttf"),
    Path("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"),
    Path("/usr/share/fonts/truetype/liberation2/LiberationSans-Regular.ttf"),
)


def _font() -> tuple[str, str | None]:
    for path in FONT_CANDIDATES:
        if path.is_file():
            return "FixtureFont", str(path)
    # PyMuPDF's built-in Japanese font includes Latin ligature glyphs and works
    # when the host has no writable system font path.
    return "japan", None


FONT_NAME, FONT_FILE = _font()


def _text(page, rect, value: str, *, size: float = 10.5) -> None:
    kwargs = {"fontname": FONT_NAME, "fontsize": size}
    if FONT_FILE:
        page.insert_font(fontname=FONT_NAME, fontfile=FONT_FILE)
    remaining = page.insert_textbox(fitz.Rect(rect), value, **kwargs)
    if remaining < 0:
        raise ValueError("Synthetic fixture text did not fit its intended box.")


def _frame(page, number: int, total: int) -> None:
    page.insert_text((45, 25), HEADER, fontsize=7.5, color=(0.35, 0.35, 0.35))
    page.insert_text((45, 825), f"Page {number} of {total}", fontsize=7.5, color=(0.35, 0.35, 0.35))


def _cover(page, number: int, total: int) -> None:
    _frame(page, number, total)
    _text(page, (55, 80, 540, 270),
          "SYNTHETIC GUIDELINE INGESTION FIXTURE\n"
          "A locally authored test document\n\n"
          "Table of Contents\n"
          "1. Synthetic recommendations\n"
          "2. Synthetic evidence table\n"
          "3. Synthetic figure\n\n"
          "Copyright 2026 Synthetic Fixture Authors\n"
          "ISBN 000-0-000000-0\n"
          "Publisher: Synthetic Test Press\n"
          "Edition: test-only")


def _two_column(page, number: int, total: int) -> None:
    _frame(page, number, total)
    _text(page, (50, 55, 545, 125),
          "2. Synthetic recommendations\n"
          "This section is a structural fixture, not clinical advice.")
    # Insert the right column first deliberately; the parser must use geometry,
    # not PDF content-stream order.
    _text(page, (305, 140, 545, 300),
          "RIGHT COLUMN SECOND\n"
          "The synthetic right-column recommendation says that the dummy review "
          "marker is RIGHT-SIDE-AMBER. This is test text.")
    _text(page, (50, 140, 285, 300),
          "LEFT COLUMN FIRST\n"
          "We recommend the synthetic left-column review when the dummy value "
          "LEFT-SIDE-GREEN is present. This is test text.")
    page.draw_rect(fitz.Rect(50, 330, 545, 430), color=(0.2, 0.2, 0.2), width=0.8)
    page.draw_line((80, 410), (240, 350), color=(0.2, 0.2, 0.2), width=1)
    page.draw_line((240, 350), (400, 390), color=(0.2, 0.2, 0.2), width=1)
    page.draw_line((400, 390), (515, 345), color=(0.2, 0.2, 0.2), width=1)
    _text(page, (50, 440, 545, 490),
          "Figure 1. Synthetic flow diagram. This caption is test text, not a clinical figure.")


def _table(page, number: int, total: int, rows: list[tuple[str, str, str]], title: str) -> None:
    _frame(page, number, total)
    _text(page, (50, 55, 545, 105), title)
    x = (50, 245, 345, 545)
    y = 125
    height = 26
    for row in rows:
        page.draw_rect(fitz.Rect(x[0], y, x[-1], y + height), color=(0.25, 0.25, 0.25), width=0.7)
        for column in x[1:-1]:
            page.draw_line((column, y), (column, y + height), color=(0.25, 0.25, 0.25), width=0.7)
        for index, value in enumerate(row):
            _text(page, (x[index] + 5, y + 7, x[index + 1] - 5, y + height - 2), value, size=9)
        y += height
    _text(page, (50, y + 20, 545, y + 70),
          "Table rows continue on the following page. The table below is synthetic.")


def _ligatures(page, number: int, total: int) -> None:
    _frame(page, number, total)
    _text(page, (50, 70, 545, 260),
          "5. Synthetic typography checks\n\n"
          "Ligature check: ﬁbrosis, ﬂuid and eﬀect remain readable words.\n"
          "A creati-\nng rise is synthetic and uses a soft line break.\n"
          "Follow-up remains an explicit hyphenated word.")


def _rotated(page, number: int, total: int) -> None:
    page.set_rotation(90)
    _text(page, (50, 50, 545, 220),
          "6. Rotated synthetic page\n"
          "ROTATED-PAGE-MARKER is present. We recommend synthetic rotation review.")


def _scanned_image(total: int) -> bytes:
    with fitz.open() as source:
        page = source.new_page(width=595, height=842)
        page.insert_text((45, 25), HEADER, fontsize=7.5, color=(0.35, 0.35, 0.35))
        page.insert_text((45, 825), f"Page 7 of {total}", fontsize=7.5, color=(0.35, 0.35, 0.35))
        page.insert_text((55, 180), "SCANNED-PAGE-TEXT", fontsize=26, color=(0.1, 0.1, 0.1))
        page.insert_text((55, 230), "Image-only synthetic page. OCR is not provided.", fontsize=11,
                         color=(0.25, 0.25, 0.25))
        page.draw_rect(fitz.Rect(55, 300, 540, 520), color=(0.5, 0.5, 0.5), width=1)
        page.insert_text((70, 340), "Synthetic scan artifact", fontsize=13, color=(0.3, 0.3, 0.3))
        pixmap = page.get_pixmap(dpi=150)
        return pixmap.tobytes("png")


def _structural_pdf(path: Path) -> dict:
    total = 7
    with fitz.open() as document:
        document.set_metadata({"title": "Synthetic ingestion fixture", "author": "Local test generator",
                               "subject": PROVENANCE, "keywords": "SYNTHETIC, TEST ONLY, NO PATIENT DATA",
                               "creationDate": "D:20260101000000Z", "modDate": "D:20260101000000Z"})
        _cover(document.new_page(width=595, height=842), 1, total)
        _two_column(document.new_page(width=595, height=842), 2, total)
        _table(document.new_page(width=595, height=842), 3, total,
               [("Marker", "Value", "Synthetic row"),
                ("TABLE-ROW-1", "10", "first continued row"),
                ("TABLE-ROW-2", "20", "second continued row")],
               "3. Synthetic multi-page evidence table, part 1")
        _table(document.new_page(width=595, height=842), 4, total,
               [("Marker", "Value", "Synthetic row"),
                ("TABLE-ROW-3", "30", "third continued row"),
                ("TABLE-ROW-4", "40", "fourth continued row")],
               "4. Synthetic multi-page evidence table, part 2")
        _ligatures(document.new_page(width=595, height=842), 5, total)
        _rotated(document.new_page(width=595, height=842), 6, total)
        scanned = document.new_page(width=595, height=842)
        scanned.insert_image(scanned.rect, stream=_scanned_image(total))
        document.save(path, garbage=4, deflate=True)
    return {
        "file": path.name,
        "pages": total,
        "frontMatterPages": [1],
        "clinicalPages": [2, 3, 4, 5, 6],
        "needsOcrPages": [7],
        "twoColumnMarkers": ["LEFT COLUMN FIRST", "RIGHT COLUMN SECOND"],
        "tableRows": ["TABLE-ROW-1", "TABLE-ROW-2", "TABLE-ROW-3", "TABLE-ROW-4"],
        "ligatureWords": ["fibrosis", "fluid", "effect", "creating"],
        "rotatedMarker": "ROTATED-PAGE-MARKER",
        "imageOnlyMarker": "SCANNED-PAGE-TEXT",
    }


def _frontmatter_pdf(path: Path) -> dict:
    total = 4
    with fitz.open() as document:
        document.set_metadata({"title": "Synthetic front-matter precision fixture", "author": "Local test generator",
                               "subject": PROVENANCE, "keywords": "SYNTHETIC, TEST ONLY",
                               "creationDate": "D:20260101000000Z", "modDate": "D:20260101000000Z"})
        _cover(document.new_page(width=595, height=842), 1, total)
        _frame(document.new_page(width=595, height=842), 2, total)
        page = document[-1]
        _text(page, (50, 70, 545, 260),
              "CLINICAL-CONTENTS-SAFE\n"
              "The contents of this synthetic section must not be mistaken for front matter. "
              "We recommend clinician review and explicitly mention patient treatment.")
        _frame(document.new_page(width=595, height=842), 3, total)
        page = document[-1]
        _text(page, (50, 70, 545, 260),
              "Synthetic administrative insert\nCopyright 2026 Synthetic Test Press\n"
              "ISBN 000-0-000000-0\nPublisher: Synthetic Test Press\nEdition: test-only")
        _frame(document.new_page(width=595, height=842), 4, total)
        page = document[-1]
        _text(page, (50, 70, 545, 260),
              "CLINICAL-PAGE-FOUR\nWe recommend the synthetic follow-up review for this patient.")
        document.save(path, garbage=4, deflate=True)
    return {
        "file": path.name,
        "pages": total,
        "frontMatterPages": [1, 3],
        "clinicalPages": [2, 4],
        "needsOcrPages": [],
    }


def build_fixtures(destination: Path) -> dict:
    """Build both PDFs into a new directory; existing files are never replaced."""
    destination.mkdir(parents=True, exist_ok=False)
    fixtures = [_structural_pdf(destination / "synthetic-ingestion-guideline.pdf"),
                _frontmatter_pdf(destination / "synthetic-ingestion-frontmatter.pdf")]
    for fixture in fixtures:
        path = destination / fixture["file"]
        fixture["bytes"] = path.stat().st_size
        fixture["sha256"] = hashlib.sha256(path.read_bytes()).hexdigest()
    manifest = {
        "format": 1,
        "synthetic": True,
        "provenance": PROVENANCE,
        "scope": "Structural ingestion tests only; no OCR and no clinical evidence.",
        "fixtures": fixtures,
    }
    (destination / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    return manifest


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--destination", type=Path, required=True)
    args = parser.parse_args()
    print(json.dumps(build_fixtures(args.destination.resolve()), indent=2))


if __name__ == "__main__":
    main()
