"""Synthetic verifier/diagnostics regressions; no model or patient record required."""
import asyncio
import json
import logging

import pytest

import config as Cfg
import operations as O
import pipeline as P


def source(text, strength="conditional"):
    return {"S1": {"id": "S1", "text": text, "strength": strength, "draft": False,
                   "label": "Synthetic", "section": "Test", "page": "3", "score": 0.9}}


def check(output, text="Synthetic review marker is GREEN.", **kwargs):
    class Backend:
        async def generate(self, prompt, max_new_tokens=400):
            return output
    return asyncio.run(P.answer_problem({"title": "Synthetic review marker", "topic": set()},
        {"sources": source(text), "queries": [], "errored": False}, Backend(), "not given", Cfg.Config.from_dict(), **kwargs))


def test_candidate_counters_are_closed_and_do_not_contain_draft_text():
    result = check("SOURCE: S1\nRECOMMENDATION: PRIVATE_DRAFT_MARKER 999\nQUOTE: altered words")
    counters = result["diagnostics"]
    assert counters["candidates"] == 1 and counters["verified"] == 0
    assert counters["reasons"]["quote_not_found"] == 1
    assert counters["reasons"]["number_missing"] == 1
    assert set(counters["reasons"]) == set(O.REJECTION_REASONS)
    assert "PRIVATE" not in json.dumps(counters)


@pytest.mark.parametrize("output,reason", [("NOT_FOUND", "empty_draft"), ("{broken json", "json_parse_error")])
def test_empty_and_malformed_drafts_are_distinguished(output, reason):
    assert check(output)["diagnostics"]["reasons"][reason] >= 1


def test_structured_logs_whitelist_counters_and_correlate_request():
    record = logging.LogRecord("clinical_rag.pipeline", logging.INFO, "private", 1, "PRIVATE", (), None)
    record.event = "answer_diagnostics"
    record.problemIndex = 1
    record.diagnostics = {"drafts": 1, "candidates": 1, "verified": 0,
                          "reasons": {"empty_draft": 1, "PRIVATE": "secret"}, "quote": "PRIVATE"}
    marker = O.REQUEST_ID.set("a" * 32)
    try:
        body = json.loads(O.PrivacySafeJsonFormatter().format(record))
    finally:
        O.REQUEST_ID.reset(marker)
    assert body["event"] == "answer_diagnostics" and body["requestId"] == "a" * 32
    assert body["diagnostics"]["reasons"] == {"empty_draft": 1}
    assert "PRIVATE" not in json.dumps(body)


def test_unicode_ligature_and_nonbreaking_space_match_exact_normalized_source():
    text = "Synthetic ﬁnal\u00a0review marker is GREEN."
    result = check("SOURCE: S1\nRECOMMENDATION: Synthetic final review marker is GREEN.\nQUOTE: Synthetic final review marker is GREEN.", text)
    assert len(result["items"]) == 1


def test_fenced_json_items_are_parsed_without_weakening_verification():
    output = 'Here is the answer:\n```json\n{"items":[{"source":"S1","recommendation":"Synthetic review marker is GREEN.","quote":"Synthetic review marker is GREEN."}]}\n```'
    assert len(check(output)["items"]) == 1


@pytest.mark.parametrize("rec,quote,text,reason", [
    ("Synthetic review marker is GREEN.", "Synthetic review marker is GREENER.", "Synthetic review marker is GREEN.", "quote_not_found"),
    ("Synthetic dose is 99 mg.", "Synthetic dose is 5 mg.", "Synthetic dose is 5 mg.", "number_missing"),
    ("We recommend synthetic drug X.", "We do not recommend synthetic drug X.", "We do not recommend synthetic drug X.", "negation_conflict"),
    ("Consider synthetic drug X in children.", "Consider synthetic drug X in adults.", "Consider synthetic drug X in adults.", "scope_mismatch"),
    ("Synthetic drug X must be used.", "Consider synthetic drug X.", "Consider synthetic drug X.", "stronger_than_source"),
])
def test_unsafe_candidates_remain_excluded(rec, quote, text, reason):
    result = check(f"SOURCE: S1\nRECOMMENDATION: {rec}\nQUOTE: {quote}", text)
    assert result["items"] == []
    assert result["diagnostics"]["reasons"][reason] == 1


@pytest.mark.parametrize("output", [
    '{"items":[{"source":"S1","recommendation":"Synthetic review marker is GREEN.","quote":"Synthetic review marker is GREEN."}]',
    '{broken "items":[{"source":"S1","recommendation":"Synthetic review marker is GREEN.","quote":"Synthetic review marker is GREEN."}]}',
])
def test_json_extraction_never_salvages_nested_items_from_invalid_envelope(output):
    result = check(output)
    assert result["items"] == []
    assert result["diagnostics"]["reasons"]["json_parse_error"] == 1


def test_search_rewrite_is_not_substituted_for_the_actual_question():
    class Backend:
        async def generate(self, prompt, max_new_tokens=400):
            return "Synthetic kidney function marker in dialysis protocol"
    question = "What is the FINAL dummy kidney-function review marker?"
    plan = asyncio.run(P.question_plan(question, Backend(), Cfg.Config.from_dict()))[0]
    assert plan["resolved_question"] == question
    assert "dialy" not in plan["topic"]


def test_truncated_draft_has_counters_but_never_verified_content():
    class Backend:
        async def generate(self, prompt, max_new_tokens=400):
            raise P.TruncatedGeneration("PRIVATE_OUTPUT")
    result = asyncio.run(P.answer_problem({"title":"Synthetic", "topic":set()},
        {"sources":source("Synthetic marker."),"queries":[],"errored":False}, Backend(), "not given", Cfg.Config.from_dict()))
    assert result["status"] == "error" and result["items"] == []
    assert result["diagnostics"]["reasons"]["truncated"] == 1
    assert "PRIVATE" not in result["detail"]


def test_duplicate_draft_candidates_are_counted_once():
    block = "SOURCE: S1\nRECOMMENDATION: Synthetic review marker is GREEN.\nQUOTE: Synthetic review marker is GREEN."
    result = check(block + "\n\n" + block)
    assert len(result["items"]) == 1
    assert result["diagnostics"]["reasons"]["deduplicated"] == 1


def test_off_topic_source_is_still_rejected():
    result = P.verify_draft("SOURCE: S1\nRECOMMENDATION: Synthetic marker is GREEN.\nQUOTE: Synthetic marker is GREEN.",
                            source("Synthetic marker is GREEN."), {"vasop"})
    assert result["items"] == []
    assert result["diagnostics"]["reasons"]["topic_mismatch"] == 1


def test_isolated_value_flags_do_not_invent_sepsis_or_vte():
    plan = P.rule_problems("Synthetic blood-pressure and creatinine review.", [{"id":"hypotension"}, {"id":"aki_possible"}])
    assert plan
    assert not any("sepsis" in p["title"].lower() or "septic" in p["title"].lower() or "vte" in p["title"].lower() for p in plan)
    assert not any("sepsis" in q.lower() or "septic" in q.lower() for p in plan for q in p["queries"])


def test_not_covered_reason_categories_are_fixed_and_text_free():
    assert P.not_covered_reason(
        {"status": "not_found", "sources": []}, {"verified": 0, "drafts": 0, "candidates": 0}) == "nothing_retrieved"
    assert P.not_covered_reason(
        {"status": "not_found", "sources": [{"draft": True}, {"draft": True}]},
        {"verified": 0, "drafts": 1, "candidates": 2}) == "only_draft_sources"
    assert P.not_covered_reason(
        {"status": "unverified", "sources": [{"draft": False}]},
        {"verified": 0, "drafts": 1, "candidates": 2}) == "retrieved_unverified"
    assert P.not_covered_reason(
        {"status": "ok", "sources": [{"draft": False}]},
        {"verified": 1, "drafts": 1, "candidates": 1}) is None
    assert P.not_covered_reason(
        {"status": "error", "sources": []}, {"verified": 0, "drafts": 0, "candidates": 0}) is None


def test_conflicts_show_both_opposite_verified_guidelines_without_merging():
    citation_a = {"id": "S1", "label": "Synthetic Society A", "section": "Synthetic", "page": "1",
                  "strength": "strong", "draft": False}
    citation_b = {"id": "S2", "label": "Synthetic Society B", "section": "Synthetic", "page": "2",
                  "strength": "conditional", "draft": False}
    conflicts = P.detect_conflicts([{"items": [
        {"recommendation": "We recommend anticoagulation for this synthetic condition.", "citation": citation_a},
        {"recommendation": "We do not recommend anticoagulation for this synthetic condition.", "citation": citation_b},
    ]}])
    assert len(conflicts) == 1
    assert conflicts[0]["point"] == "Potential guideline disagreement"
    assert [item["citation"]["label"] for item in conflicts[0]["items"]] == [
        "Synthetic Society A", "Synthetic Society B"]
    assert conflicts[0]["items"][0]["recommendation"] != conflicts[0]["items"][1]["recommendation"]


def test_default_answer_prompt_requests_minimal_complete_support_not_whole_paragraphs():
    prompt = Cfg.render_prompt("answer", P.ANSWER_PROMPT, problem="Synthetic", patient="not given", k=4, context="Synthetic")
    assert "shortest complete supporting span" in prompt
    assert "character-for-character" in prompt
    assert "exact row label followed by one exact cell" in prompt
    assert "Do not append unrelated neighboring sentences" in prompt
    assert "Never append NOT_FOUND after items" in prompt
