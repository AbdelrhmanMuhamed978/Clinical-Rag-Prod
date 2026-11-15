"""The same identity/syntax vectors are exercised by xUnit and Vitest."""
import json
from pathlib import Path

import pytest
import regex as bounded_regex

import clinical_text as CT
import facts as F

VECTORS = json.loads((Path(__file__).parent / "fixtures" / "clinical-text-vectors.json").read_text(encoding="utf-8"))


@pytest.mark.parametrize("vector", VECTORS["aliases"])
def test_shared_alias_identity_and_collision(vector):
    assert CT.alias(vector["input"]) == vector["expected"]
    catalog = {"first": {"classes": ["nsaid"], "brands": [vector["input"]], "regional_names": []},
               "second": {"classes": ["metformin"], "brands": [vector["expected"]], "regional_names": []}}
    with pytest.raises(ValueError, match="two different drugs"):
        F.validate_drug_catalog(catalog)


@pytest.mark.parametrize("vector", VECTORS["names"])
def test_shared_name_validation_and_utf16_limits(vector):
    value = vector.get("input", "".join(chr(point) for point in vector.get("codepoints", []))) * vector.get("repeat", 1)
    assert CT.valid_name(value) is vector["valid"]
    catalog = {value: {"classes": ["nsaid"], "brands": [], "regional_names": []}}
    if vector["valid"]:
        F.validate_drug_catalog(catalog)
    else:
        with pytest.raises(ValueError):
            F.validate_drug_catalog(catalog)


@pytest.mark.parametrize("vector", VECTORS["recognition"])
def test_full_fold_recognition_preserves_displayed_input_and_boundaries(vector):
    catalog = {"syntheticdrug": {"classes": ["nsaid"], "brands": [vector["alias"]], "regional_names": []}}
    meds = F.find_meds(vector["text"], catalog)
    assert bool(meds) is vector["valid"]
    if meds:
        assert meds[0]["input"] == vector["display"]
        assert meds[0]["confidence"] == "exact"
        assert meds[0]["verify"] is False


@pytest.mark.parametrize("vector", VECTORS["urls"])
def test_shared_unambiguous_source_url_syntax(vector):
    assert CT.http_source_url(vector["input"]) is vector["valid"]
    candidate = F.load_rules()["rules"][0] | {"sources": [{"title": "Synthetic", "url": vector["input"]}]}
    if vector["valid"]:
        F.validate_rule_definition(candidate)
    else:
        with pytest.raises(ValueError):
            F.validate_rule_definition(candidate)


@pytest.mark.parametrize("vector", VECTORS["regex"])
def test_shared_portable_regex_subset_and_compilation(vector):
    assert (CT.portable_regex_problem(vector["input"]) is None) is vector["valid"]
    if vector["valid"]:
        F.validate_link_terms(vector["input"])
        if vector["input"]:
            F._compiled_link_terms(vector["input"])
    else:
        with pytest.raises(ValueError):
            F.validate_link_terms(vector["input"])


@pytest.mark.parametrize("vector", VECTORS["regex_compilation"])
def test_lexical_acceptance_never_substitutes_for_actual_regex_compilation(vector):
    assert CT.portable_regex_problem(vector["input"]) is None
    if vector["valid"]:
        F.validate_link_terms(vector["input"])
        F._compiled_link_terms(vector["input"])
    else:
        with pytest.raises(ValueError, match="compile"):
            F.validate_link_terms(vector["input"])
        with pytest.raises(bounded_regex.error):
            bounded_regex.compile(vector["input"], bounded_regex.I | bounded_regex.VERSION0)


def test_rule_and_preview_text_lengths_match_html_and_database_utf16_units():
    candidate = F.load_rules()["rules"][0] | {"title": "💊" * 128}
    F.validate_rule_definition(candidate)
    with pytest.raises(ValueError):
        F.validate_rule_definition(candidate | {"title": "💊" * 129})
    with pytest.raises(ValueError):
        F.preview_rule("💊" * 50001, candidate, {})
    with pytest.raises(ValueError):
        F.test_link_terms("renal", "💊" * 5001)


def test_pinned_casefold_matches_all_unicode_15_1_mappings():
    # Also catches a malformed or accidentally edited compression table.
    for start, end, step, _ in CT.ASSET["casefold_ranges"]:
        for point in range(start, end + 1, step):
            assert CT.casefold(chr(point)) == chr(point).casefold()
    for point in CT.ASSET["casefold_expansions"]:
        assert CT.casefold(chr(int(point))) == chr(int(point)).casefold()
