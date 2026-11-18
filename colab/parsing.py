"""PDF parsing for the Colab server.

This is deliberately deterministic: no OCR and no model calls. Image-only or
near-empty pages are reported as ``needsOcrPages`` instead of being silently
discarded.
"""

import collections
import math
import re
from typing import Any

from evidence_text import normalize_evidence_text

PAGE_NUMBER_RE = re.compile(
    r"^(?:\d{1,4}|[ivxlcdm]{1,8}|page\s+\d{1,4}\s+(?:of|/)\s+\d{1,4})$", re.I
)
NEAR_EMPTY_CHARACTERS = 40
FOOTNOTE_PLACEHOLDER = "\ue000"
FOOTNOTE_END = "\ue001"
_FOOTNOTE_TOKEN = re.compile(r"\ue000(\d+)\ue001")
_FOOTNOTE_ANY = re.compile(r"\ue000\d*\ue001")


def _clean(value: str) -> str:
    return normalize_evidence_text(value)


def _normalise_line(line: str) -> str:
    return _clean(line).strip().lower()


def _repeated_header_footer_lines(raw_pages: list[list[str]]) -> set[str]:
    """Find repeated first/last lines that are likely running headers/footers."""
    if len(raw_pages) < 2:
        return set()
    counts: collections.Counter[str] = collections.Counter()
    for lines in raw_pages:
        non_empty = [line for line in lines if line.strip()]
        page_candidates = {
            _normalise_line(line) for line in non_empty[:2] + non_empty[-2:]
        }
        for normalised in page_candidates:
            if normalised and not PAGE_NUMBER_RE.fullmatch(normalised):
                counts[normalised] += 1
    threshold = max(2, math.ceil(len(raw_pages) * 0.5))
    return {line for line, count in counts.items() if count >= threshold}


def _tables(page: Any) -> tuple[list[Any], list[Any]]:
    try:
        tables = page.find_tables()
    except Exception:
        return [], []
    items = list(getattr(tables, "tables", []))
    boxes = [box for table in items if (box := getattr(table, "bbox", None)) is not None]
    return items, boxes


def _is_superscript(span: dict[str, Any], line: dict[str, Any]) -> bool:
    text = str(span.get("text", "")).strip()
    if not text:
        return False
    if int(span.get("flags", 0) or 0) & 1:
        return True
    sizes = [float(item.get("size", 0) or 0) for item in line.get("spans", [])
             if str(item.get("text", "")).strip()]
    size = float(span.get("size", 0) or 0)
    return bool(sizes and size and size < max(sizes) * 0.75)


def _footnote_marker(span: dict[str, Any], line: dict[str, Any]) -> str | None:
    text = str(span.get("text", "")).strip()
    # Only single alphabetic superscripts are footnote markers.  Digits such
    # as the 3 in mm3 are meaningful units and stay visible.
    if len(text) == 1 and text.isalpha() and _is_superscript(span, line):
        return text
    return None


def _line_text(line: dict[str, Any], markers: list[dict[str, Any]], source: str) -> str:
    parts: list[str] = []
    for span in line.get("spans", []):
        marker = _footnote_marker(span, line)
        if marker:
            markers.append({"marker": marker, "source": source})
            # A unique token keeps marker metadata aligned with the surviving
            # text even when a later repeated header/footer line is dropped.
            parts.append(f"{FOOTNOTE_PLACEHOLDER}{len(markers) - 1}{FOOTNOTE_END}")
        else:
            parts.append(str(span.get("text", "")))
    return "".join(parts)


def _overlaps_table(block: tuple[Any, ...], boxes: list[Any]) -> bool:
    if len(block) < 5 or not boxes:
        return False
    x0, y0, x1, y1 = (float(value) for value in block[:4])
    if x1 <= x0 or y1 <= y0:
        return False
    for box in boxes:
        try:
            bx0, by0, bx1, by1 = (float(value) for value in box)
        except (TypeError, ValueError):
            continue
        overlap = max(0.0, min(x1, bx1) - max(x0, bx0)) * max(0.0, min(y1, by1) - max(y0, by0))
        if overlap >= 0.2 * (x1 - x0) * (y1 - y0):
            return True
    return False


def _cell_text(page: Any, rect: Any, markers: list[dict[str, Any]]) -> str:
    try:
        data = page.get_text("dict", clip=rect)
    except (AttributeError, TypeError):
        return ""
    lines: list[str] = []
    for block in data.get("blocks", []):
        if block.get("type", 0) != 0:
            continue
        for line in block.get("lines", []):
            value = _line_text(line, markers, "table")
            if value.strip():
                lines.append(value.strip())
    return " ".join(lines)


def _table_markdown(page: Any, table: Any, markers: list[dict[str, Any]]) -> str:
    rows = list(getattr(table, "rows", []) or [])
    value = ""
    if rows:
        columns = max((len(getattr(row, "cells", []) or []) for row in rows), default=0)
        if columns:
            lines: list[str] = []
            for index, row in enumerate(rows):
                cells = []
                for column in range(columns):
                    rect = (getattr(row, "cells", []) or [])[column] if column < len(getattr(row, "cells", []) or []) else None
                    cell = _cell_text(page, rect, markers) if rect is not None else ""
                    cells.append(cell.replace("|", "\\|").strip())
                lines.append("|" + "|".join(cells) + "|")
                if index == 0:
                    lines.append("|" + "|".join("---" for _ in range(columns)) + "|")
            if any(line.replace("|", "").strip() for line in lines[1:]):
                value = "\n".join(lines)
    if not value:
        try:
            value = str(table.to_markdown())
        except Exception:
            return ""
    return _clean(value).strip()


def _blocks(page: Any, table_boxes: list[Any], markers: list[dict[str, Any]]) -> list[tuple[float, float, float, float, str]]:
    try:
        raw = page.get_text("blocks")
    except TypeError:
        # Older/simplified test doubles expose get_text() only.
        return [(0.0, 0.0, 0.0, 0.0, line) for line in page.get_text().splitlines()]
    out: list[tuple[float, float, float, float, str]] = []
    for block in raw:
        if len(block) < 5 or (len(block) > 6 and block[6] != 0):
            continue
        if _overlaps_table(block, table_boxes):
            continue
        text = str(block[4]).strip()
        if text:
            out.append((float(block[0]), float(block[1]), float(block[2]), float(block[3]), text))
    return out


def _blocks_with_markers(page: Any, table_boxes: list[Any],
                         markers: list[dict[str, Any]]) -> list[tuple[float, float, float, float, str]]:
    try:
        data = page.get_text("dict")
    except (AttributeError, TypeError):
        return _blocks(page, table_boxes, markers)
    out: list[tuple[float, float, float, float, str]] = []
    for block in data.get("blocks", []):
        if block.get("type", 0) != 0:
            continue
        bbox = block.get("bbox")
        if not bbox or _overlaps_table((*bbox, ""), table_boxes):
            continue
        lines: list[str] = []
        for line in block.get("lines", []):
            value = _line_text(line, markers, "body")
            if value.strip():
                lines.append(value)
        text = "\n".join(lines).strip()
        if text:
            out.append((float(bbox[0]), float(bbox[1]), float(bbox[2]), float(bbox[3]), text))
    return out


def _reading_order(blocks: list[tuple[float, float, float, float, str]], width: float,
                   rotation: int) -> list[tuple[float, float, float, float, str]]:
    if not blocks:
        return []
    if rotation:
        return sorted(blocks, key=lambda block: (block[1], block[0]))
    full = [block for block in blocks if block[2] - block[0] >= width * 0.6]
    narrow = [block for block in blocks if block not in full]
    if not full or not narrow:
        return sorted(blocks, key=lambda block: (block[1], block[0]))

    def flush(band: list[tuple[float, float, float, float, str]]) -> list[tuple[float, float, float, float, str]]:
        left = [block for block in band if (block[0] + block[2]) / 2 < width / 2]
        right = [block for block in band if (block[0] + block[2]) / 2 >= width / 2]
        if left and right:
            return sorted(left, key=lambda block: (block[1], block[0])) + sorted(right, key=lambda block: (block[1], block[0]))
        return sorted(band, key=lambda block: (block[1], block[0]))

    ordered: list[tuple[float, float, float, float, str]] = []
    band: list[tuple[float, float, float, float, str]] = []
    for block in sorted(full, key=lambda item: (item[1], item[0])):
        band = [item for item in narrow if item[1] < block[1]]
        narrow = [item for item in narrow if item[1] >= block[1]]
        ordered.extend(flush(band))
        ordered.append(block)
    ordered.extend(flush(narrow))
    return ordered


def _image_only(page: Any, fitz_module: Any) -> bool:
    """True for a mostly image-covered page, not merely a short section title."""
    try:
        area = float(page.rect.width) * float(page.rect.height)
        if area <= 0:
            return False
        covered = 0.0
        for info in page.get_image_info():
            bbox = info.get("bbox")
            if not bbox:
                continue
            rect = fitz_module.Rect(bbox)
            rect.intersect(page.rect)
            if rect.width > 0 and rect.height > 0:
                covered += rect.width * rect.height
        return covered / area >= 0.5
    except Exception:
        return False


def _strip_footnote_markers(text: str, markers: list[dict[str, Any]]) -> tuple[str, list[dict[str, Any]]]:
    out: list[str] = []
    found: list[dict[str, Any]] = []
    offset = 0
    last = 0
    for match in _FOOTNOTE_TOKEN.finditer(text):
        segment = text[last:match.start()]
        out.append(segment)
        offset += len(segment)
        index = int(match.group(1))
        if 0 <= index < len(markers):
            marker = dict(markers[index])
            marker["offset"] = offset
            found.append(marker)
        last = match.end()
    out.append(text[last:])
    visible = _FOOTNOTE_ANY.sub("", "".join(out))
    for marker in found:
        start = max(0, marker["offset"] - 40)
        end = min(len(visible), marker["offset"] + 40)
        marker["context"] = visible[start:end]
    return visible, found


def parse_pdf_bytes(contents: bytes, fitz_module: Any = None) -> dict[str, Any]:
    """Extract page text, strip running headers/footers and append tables.

    Multi-column pages are ordered left column then right column within each
    full-width section. Tables are excluded from the ordinary text layer and
    appended as markdown so they can be kept intact by the chunker.
    """
    if not contents:
        raise ValueError("empty file received")
    if fitz_module is None:
        try:
            import pymupdf as fitz_module
        except ImportError:
            import fitz as fitz_module

    document = fitz_module.open(stream=contents, filetype="pdf")
    try:
        page_count = len(document)
        raw_pages: list[list[str]] = []
        page_blocks: list[list[tuple[float, float, float, float, str]]] = []
        page_tables: list[list[Any]] = []
        page_markers: list[list[dict[str, Any]]] = []
        for page in document:
            tables, boxes = _tables(page)
            markers: list[dict[str, Any]] = []
            blocks = _blocks_with_markers(page, boxes, markers)
            page_tables.append(tables)
            page_blocks.append(blocks)
            page_markers.append(markers)
            raw_pages.append([_FOOTNOTE_ANY.sub("", line)
                              for block in blocks for line in block[4].splitlines()])

        repeated = _repeated_header_footer_lines(raw_pages)
        output: list[str] = []
        needs_ocr: list[int] = []
        pages: list[dict[str, Any]] = []
        all_markers: list[dict[str, Any]] = []

        for index, page in enumerate(document):
            width = float(getattr(getattr(page, "rect", None), "width", 595.0))
            rotation = int(getattr(page, "rotation", 0) or 0)
            ordered = _reading_order(page_blocks[index], width, rotation)
            cleaned_lines: list[str] = []
            for block in ordered:
                for line in block[4].splitlines():
                    stripped = line.strip()
                    comparable = _FOOTNOTE_ANY.sub("", stripped)
                    if not comparable or PAGE_NUMBER_RE.fullmatch(comparable):
                        continue
                    if _normalise_line(comparable) in repeated:
                        continue
                    cleaned_lines.append(line)
            text = _clean("\n".join(cleaned_lines)).strip()
            table_markdowns = [_table_markdown(page, table, page_markers[index])
                               for table in page_tables[index]]
            table_markdowns = [value for value in table_markdowns if value]
            if table_markdowns:
                text += "\n\n[TABLES]\n" + "\n\n".join(table_markdowns) + "\n[/TABLES]"
            text, markers = _strip_footnote_markers(text, page_markers[index])
            for marker in markers:
                marker["page"] = index + 1
            all_markers.extend(markers)
            is_ocr = len(re.sub(r"\s+", "", text)) < NEAR_EMPTY_CHARACTERS and _image_only(page, fitz_module)
            if is_ocr:
                needs_ocr.append(index + 1)
            pages.append({"page": index + 1, "characters": len(text), "needsOcr": is_ocr,
                          "footnoteMarkers": markers})
            if text:
                output.append(f"--- Page {index + 1} ---\n{text}")

        return {
            "text": "\n\n".join(output),
            "pageCount": page_count,
            "needsOcrPages": needs_ocr,
            "pages": pages,
            "footnoteMarkers": all_markers,
        }
    finally:
        document.close()
