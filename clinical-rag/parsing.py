"""PDF parsing for the Colab server."""

import collections
import math
import re
from typing import Any

PAGE_NUMBER_RE = re.compile(r"^(?:\d{1,4}|[ivxlcdm]{1,8})$", re.I)


def _normalise_line(line: str) -> str:
    return re.sub(r"\s+", " ", line).strip().lower()


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


def parse_pdf_bytes(contents: bytes, fitz_module: Any = None) -> dict[str, Any]:
    """Extract page text, strip running headers/page numbers and append tables.

    ``pageCount`` is captured before the document is closed.
    """
    if not contents:
        raise ValueError("empty file received")
    if fitz_module is None:
        import fitz as fitz_module

    document = fitz_module.open(stream=contents, filetype="pdf")
    try:
        page_count = len(document)
        raw_pages = [page.get_text().splitlines() for page in document]
        repeated = _repeated_header_footer_lines(raw_pages)
        output: list[str] = []

        for index, page in enumerate(document):
            cleaned_lines: list[str] = []
            for line in raw_pages[index]:
                stripped = line.strip()
                if not stripped or PAGE_NUMBER_RE.fullmatch(stripped):
                    continue
                if _normalise_line(stripped) in repeated:
                    continue
                cleaned_lines.append(line)
            text = "\n".join(cleaned_lines).strip()

            table_markdown: list[str] = []
            try:
                tables = page.find_tables()
                for table in getattr(tables, "tables", []):
                    try:
                        markdown = table.to_markdown()
                    except Exception:
                        continue
                    if markdown and markdown.strip():
                        table_markdown.append(markdown.strip())
            except Exception:
                pass
            if table_markdown:
                text += "\n\n[TABLES]\n" + "\n\n".join(table_markdown) + "\n[/TABLES]"
            if text:
                output.append(f"--- Page {index + 1} ---\n{text}")
        return {"text": "\n\n".join(output), "pageCount": page_count}
    finally:
        document.close()
