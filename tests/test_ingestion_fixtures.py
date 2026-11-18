"""Per-fixture ingestion checks; synthetic PDFs only, no OCR or clinical content."""
from __future__ import annotations

import asyncio
import json

import pytest

from chunker import chunk_document, detect_front_matter, is_verbatim, parse_pages
from colab.parsing import parse_pdf_bytes
from scripts.prepare_ingestion_fixtures import build_fixtures


async def _clean_llm(prompt: str) -> str:
    if "profiling" in prompt:
        return json.dumps({"kind": "guideline", "structure": "mixed",
                           "strategy": "keep tables atomic", "notes": "Synthetic fixture."})
    if "chunk critic" in prompt:
        return json.dumps({"decisions": []})
    if "Extract metadata" in prompt:
        return json.dumps({"section_path": "Synthetic section", "strength": "none",
                           "population": "", "status": "FINAL", "topic": "synthetic"})
    return "{}"


@pytest.fixture(scope="module")
def ingestion_fixtures(tmp_path_factory):
    destination = tmp_path_factory.mktemp("ingestion") / "files"
    manifest = build_fixtures(destination)
    parsed = {}
    for fixture in manifest["fixtures"]:
        parsed[fixture["file"]] = parse_pdf_bytes((destination / fixture["file"]).read_bytes())
    return manifest, parsed


def _fixture(manifest: dict, name: str) -> dict:
    return next(item for item in manifest["fixtures"] if item["file"] == name)


def _front_matter_precision(expected: list[int], detected: list[dict]) -> float:
    actual = {item["page"] for item in detected}
    return len(actual & set(expected)) / len(actual) if actual else 0.0


def test_fixtures_are_synthetic_and_have_expected_files(ingestion_fixtures):
    manifest, parsed = ingestion_fixtures
    assert manifest["synthetic"] is True
    assert len(manifest["fixtures"]) == 2
    assert all(item["pages"] == parsed[item["file"]]["pageCount"] for item in manifest["fixtures"])


def test_two_column_order_headers_footers_figure_and_rotated_page(ingestion_fixtures):
    manifest, parsed = ingestion_fixtures
    fixture = _fixture(manifest, "synthetic-ingestion-guideline.pdf")
    data = parsed[fixture["file"]]
    text = data["text"]

    assert data["pageCount"] == 7
    assert [item["page"] for item in data["pages"] if item["needsOcr"]] == [7]
    assert fixture["needsOcrPages"] == [7]
    assert "SYNTHETIC INGESTION GUIDELINE" not in text
    assert "Page 1 of 7" not in text

    assert text.index("LEFT COLUMN FIRST") < text.index("RIGHT COLUMN SECOND")
    assert "Figure 1. Synthetic flow diagram." in text
    assert "ROTATED-PAGE-MARKER" in text
    assert "SCANNED-PAGE-TEXT" not in text


def test_ligatures_and_hyphenated_line_breaks_are_normalised(ingestion_fixtures):
    manifest, parsed = ingestion_fixtures
    fixture = _fixture(manifest, "synthetic-ingestion-guideline.pdf")
    text = parsed[fixture["file"]]["text"]

    for word in ("fibrosis", "fluid", "effect", "creating", "Follow-up"):
        assert word in text
    assert "\xad" not in text
    assert "creati-\n" not in text


def test_table_markdown_is_present_and_rows_are_not_split(ingestion_fixtures):
    manifest, parsed = ingestion_fixtures
    data = parsed[_fixture(manifest, "synthetic-ingestion-guideline.pdf")["file"]]
    pages = parse_pages(data["text"])
    chunks = asyncio.run(chunk_document(_clean_llm, data["text"], max_tokens=400))["chunks"]

    table_chunks = [chunk["text"] for chunk in chunks if "[TABLES]" in chunk["text"]]
    assert len(table_chunks) == 2
    for marker in ("TABLE-ROW-1", "TABLE-ROW-2"):
        assert any(marker in chunk for chunk in table_chunks)
    for marker in ("TABLE-ROW-3", "TABLE-ROW-4"):
        assert any(marker in chunk for chunk in table_chunks)
    assert all(chunk.count("[TABLES]") == 1 and chunk.count("[/TABLES]") == 1 for chunk in table_chunks)
    assert all("|---|---|---|" in chunk for chunk in table_chunks)
    assert all("TABLE-ROW-1" in chunk or "TABLE-ROW-3" in chunk for chunk in table_chunks)

    page_text = {page["page"]: page["text"] for page in pages}
    assert all(is_verbatim(chunk["text"], page_text[chunk["page"]]) for chunk in chunks)
    assert all(chunk["verbatim"] for chunk in chunks)
    assert not any(chunk.get("validation_error") for chunk in chunks)


def test_front_matter_precision_and_no_clinical_page_is_dropped(ingestion_fixtures):
    manifest, parsed = ingestion_fixtures
    for fixture in manifest["fixtures"]:
        pages = parse_pages(parsed[fixture["file"]]["text"])
        detected = detect_front_matter(pages)
        assert _front_matter_precision(fixture["frontMatterPages"], detected) == 1.0
        assert {item["page"] for item in detected} == set(fixture["frontMatterPages"])
        assert not ({item["page"] for item in detected} & set(fixture["clinicalPages"]))
        result = asyncio.run(chunk_document(_clean_llm, parsed[fixture["file"]]["text"]))
        assert {item["page"] for item in result["skipped_pages"]} == set(fixture["frontMatterPages"])
        assert all(chunk["page"] not in fixture["frontMatterPages"] for chunk in result["chunks"])


def test_all_chunks_are_verbatim_slices_for_both_fixtures(ingestion_fixtures):
    manifest, parsed = ingestion_fixtures
    for fixture in manifest["fixtures"]:
        pages = parse_pages(parsed[fixture["file"]]["text"])
        page_text = {page["page"]: page["text"] for page in pages}
        result = asyncio.run(chunk_document(_clean_llm, parsed[fixture["file"]]["text"]))
        assert result["failed_chunks"] == []
        assert result["chunks"]
        for chunk in result["chunks"]:
            assert chunk["verbatim"] is True
            assert is_verbatim(chunk["text"], page_text[chunk["page"]])
