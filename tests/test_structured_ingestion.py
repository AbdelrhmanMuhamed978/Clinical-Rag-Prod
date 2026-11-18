import asyncio
import json
import re

from chunker import apply_decisions, chunk_document, classify_chunk, structured_metadata

GRADE = re.compile(r"\b(strong|conditional|weak)\s+recommendation\b", re.I)


async def _llm(prompt: str) -> str:
    if "profiling" in prompt:
        return json.dumps({"kind": "guideline", "structure": "numbered_recommendations",
                           "strategy": "split by recommendation grade", "notes": "Synthetic."})
    if "chunk critic" in prompt:
        return json.dumps({"decisions": []})
    if "Extract metadata" in prompt:
        return json.dumps({"section_path": "", "strength": "none", "population": "",
                           "status": "FINAL", "topic": "synthetic"})
    return "{}"


def test_adjacent_recommendations_are_atomic_and_never_share_a_grade():
    text = (
        "--- Page 1 ---\n"
        "Summary of recommendations\n"
        "Use synthetic drug A for adults. (Strong recommendation, high-certainty evidence)\n"
        "Use synthetic drug B for adults. (Conditional recommendation, low-certainty evidence)\n"
    )
    result = asyncio.run(chunk_document(_llm, text, max_tokens=400))
    graded = [chunk for chunk in result["chunks"] if GRADE.search(chunk["text"])]
    assert len(graded) == 2
    assert all(len(GRADE.findall(chunk["text"])) == 1 for chunk in graded)
    assert {chunk["metadata"]["strength"] for chunk in graded} == {"strong", "conditional"}
    assert len({chunk["metadata"]["recommendation_id"] for chunk in graded}) == 2
    for chunk in graded:
        assert chunk["metadata"]["body"] == chunk["text"]
        assert chunk["metadata"]["title"]
        assert chunk["metadata"]["kind"] in {"summary", "recommendation"}
    assert result["stats"]["structuredKinds"]


def test_definition_summary_and_table_objects_are_classified():
    definition = "Advanced HIV disease is defined as a CD4 count below 200 cells/mm3."
    summary = "Summary of recommendations\nUse synthetic drug A. (Strong recommendation, low-certainty evidence)"
    table = "[TABLES]\n|Intervention|Grade|\n|---|---|\n|Drug A|Strong|\n[/TABLES]"
    assert classify_chunk(definition) == "definition"
    assert classify_chunk(summary) == "summary"
    assert classify_chunk(table) == "table_row"
    metadata = structured_metadata({"page": 1, "text": definition})
    assert metadata["kind"] == "definition"
    assert metadata["body"] == definition
    assert metadata["title"] == "Advanced HIV disease is defined as a CD4 count below 200 cells/mm3."


def test_recommendation_metadata_binds_title_year_body_and_grade():
    text = "2022 recommendations\nUse synthetic drug A. (Strong recommendation, moderate-certainty evidence)"
    metadata = structured_metadata({"page": 11, "text": text}, {"section_path": "Treatment > Induction"})
    assert metadata["kind"] == "recommendation"
    assert metadata["title"] == "Treatment > Induction"
    assert metadata["year"] == "2022"
    assert metadata["body"] == text
    assert metadata["strength"] == "strong"
    assert metadata["certainty"] == "moderate"
    assert metadata["recommendation_id"]


def test_graded_recommendation_table_rows_become_atomic_objects():
    text = (
        "--- Page 1 ---\n"
        "[TABLES]\n"
        "|Year|Recommendation|Grade|\n"
        "|---|---|---|\n"
        "|2018|Use synthetic drug A.|Strong recommendation, high-certainty evidence|\n"
        "|2022|Use synthetic drug B.|Conditional recommendation, low-certainty evidence|\n"
        "[/TABLES]\n"
    )
    result = asyncio.run(chunk_document(_llm, text, max_tokens=400))
    chunks = result["chunks"]
    assert len(chunks) == 2
    assert all(chunk["metadata"]["kind"] == "recommendation" for chunk in chunks)
    assert all(chunk["metadata"]["table"] is True for chunk in chunks)
    assert {chunk["metadata"]["strength"] for chunk in chunks} == {"strong", "conditional"}
    assert len({chunk["metadata"]["recommendation_id"] for chunk in chunks}) == 2
    assert all(chunk["verbatim"] for chunk in chunks)
    assert all(len(GRADE.findall(chunk["text"])) == 1 for chunk in chunks)
    assert all("|2018|" in chunk["text"] or "|2022|" in chunk["text"] for chunk in chunks)


def test_critic_cannot_merge_across_recommendation_boundaries():
    chunks = [
        {"page": 1, "text": "Use drug A. (Strong recommendation, high-certainty evidence)", "index": 0},
        {"page": 1, "text": "Use drug B. (Conditional recommendation, low-certainty evidence)", "index": 1},
    ]
    merged = apply_decisions(chunks, [
        {"global": 0, "action": "KEEP", "note": ""},
        {"global": 1, "action": "MERGE_WITH_PREVIOUS", "note": "test merge"},
    ])
    assert len(merged) == 2
    assert all(len(GRADE.findall(chunk["text"])) == 1 for chunk in merged)
