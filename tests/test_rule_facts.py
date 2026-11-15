"""Regression tests for canonical parsing and request-scoped clinical definitions."""

import copy
import json
from pathlib import Path

import pytest

import facts as F


@pytest.mark.parametrize("text,field,expected", [
    ("k 3.1", "potassium", 3.1),
    ("K+ 3.1", "potassium", 3.1),
    ("K+: 3.1", "potassium", 3.1),
    ("potassium = 3.1 mmol/L", "potassium", 3.1),
    ("creat 2.1", "creatinine", 2.1),
    ("sCr 2.1", "creatinine", 2.1),
    ("s.cr: 2.1", "creatinine", 2.1),
    ("srcr 2.1", "creatinine", 2.1),
    ("creatinine 2.1 mg/dL", "creatinine", 2.1),
    ("mg: 1.6", "magnesium", 1.6),
    ("Mg = 1.6", "magnesium", 1.6),
    ("magnesium 1.6 mg/dL", "magnesium", 1.6),
    ("SBP 88 DBP 52 MAP 64", "sbp", 88),
    ("SBP 88 DBP 52 MAP 64", "dbp", 52),
    ("SBP 88 DBP 52 MAP 64", "map", 64),
])
def test_parser_accepts_supported_forms(text, field, expected):
    assert F.extract_values(text)[field] == pytest.approx(expected)


@pytest.mark.parametrize("text", [
    "aspirin 75 mg once daily", "aspirin 75 mg 3 times daily",
    "concor 2.5 mg, plavix 75 mg", "mg 1.6", "magnesium 400 mg tablets",
])
def test_dose_units_are_not_magnesium_labs(text):
    assert "magnesium" not in F.extract_values(text)


@pytest.mark.parametrize("text,field,expected", [
    ("creat 176.8 umol/L", "creatinine", 2.0),
    ("sCr 176.8 µmol/L", "creatinine", 2.0),
    ("creatinine 176.8 μmol/L", "creatinine", 2.0),
    ("creatinine (umol/L): 176.8", "creatinine", 2.0),
    ("bilirubin 34.2 umol/L", "bilirubin", 2.0),
    ("total bilirubin 34.2 µmol/L", "bilirubin", 2.0),
    ("T.bil 34.2 μmol/L", "bilirubin", 2.0),
    ("Hb 95 g/L", "hb", 9.5),
    ("haemoglobin (g/L) 95", "hb", 9.5),
    ("Hgb 9.5 g/dL", "hb", 9.5),
    ("WBC 22 x10^9/L", "wbc", 22),
    ("WCC 22 ×10⁹/L", "wbc", 22),
    ("platelets 25 x 10^9 / L", "platelets", 25),
    ("plt 25 × 10⁹/L", "platelets", 25),
    ("WBC 22,000 /µL", "wbc", 22),
    ("platelets 800 /uL", "platelets", 0.8),
    ("platelets 25 x10^3/uL", "platelets", 25),
    ("platelets 25,000", "platelets", 25),
    ("albumin 25 g/L", "albumin", 2.5),
    ("magnesium 0.6 mmol/L", "magnesium", 1.4583),
    ("calcium 2 mmol/L", "calcium", 8.016),
    ("K+ 3.1 mEq/L", "potassium", 3.1),
])
def test_explicit_units_are_normalized_before_rules(text, field, expected):
    assert F.extract_values(text)[field] == pytest.approx(expected)


def test_converted_values_are_not_warned_as_unconverted_umol_values():
    fx = F.analyze_facts("creat 176.8 umol/L; bilirubin 34.2 µmol/L; Hb 95 g/L", rules=[])
    assert fx["values"]["creatinine"] == pytest.approx(2)
    assert fx["unit_details"]["creatinine"] == {
        "unit": "mg/dL", "source_unit": "umol/L", "converted": True, "assumed": False,
    }
    assert not any("creatinine" in warning and "assumed" in warning for warning in fx["warnings"])


@pytest.mark.parametrize("text,field,expected", [
    ("K 2.9, later K+ 4.2 mmol/L", "potassium", 4.2),
    ("creat 3 mg/dL, repeat sCr 88.4 umol/L", "creatinine", 1),
    ("Hb 80 g/L, repeat Hgb 110 g/L", "hb", 11),
    ("BP 80/50, repeat BP 110/70", "sbp", 110),
    ("BP 80/50, repeat SBP 110", "sbp", 110),
    ("WBC 25,000, repeat WBC 8 x10^9/L", "wbc", 8),
    ("mg: 1.2, repeat magnesium 2.1 mg/dL", "magnesium", 2.1),
])
def test_latest_textual_occurrence_wins_and_is_warned(text, field, expected):
    fx = F.analyze_facts(text, rules=[])
    assert fx["values"][field] == pytest.approx(expected)
    assert any(field in warning and "latest" in warning.lower() for warning in fx["warnings"])


def test_unsupported_latest_units_do_not_fall_back_to_an_older_lab():
    fx = F.analyze_facts("creat 2 mg/dL, repeat creat 200 bananas/L", rules=[])
    assert "creatinine" not in fx["values"]
    assert any("unsupported" in warning.lower() for warning in fx["warnings"])


def test_zero_systolic_pressure_does_not_crash_score_derivation():
    assert "shock_index" not in F.derive({"sbp": 0, "hr": 90})


def rule(**changes):
    return {
        "id": "synthetic-screen", "title": "Synthetic medication review",
        "status": "approved", "enabled": True, "drug_classes": ["nsaid"],
        "applies_when": {"any": [{"field": "creatinine", "op": ">=", "value": 1.5}]},
        "level": "individual_review", "message": "Synthetic test only.",
        "sources": [{"title": "Synthetic source", "url": "https://example.org/source"}],
        "link_terms": r"kidney|\bAKI\b", "case_level": False, **changes,
    }


def test_approved_request_rule_fires_after_conversion():
    fx = F.analyze_facts("ibuprofen 400 mg, creat 176.8 µmol/L", rules=[rule()])
    assert [flag["id"] for flag in fx["med_flags"]] == ["synthetic-screen"]
    assert fx["med_flags"][0]["drugs"][0]["generic"] == "ibuprofen"


@pytest.mark.parametrize("status", ["draft", "in_review", "retired", None])
def test_unapproved_rules_never_fire(status):
    candidate = rule(status=status)
    assert F.analyze_facts("ibuprofen, creat 2", rules=[candidate])["med_flags"] == []


def test_disabled_rule_and_explicit_empty_list_never_fire():
    assert F.analyze_facts("ibuprofen, creat 2", rules=[rule(enabled=False)])["med_flags"] == []
    assert F.analyze_facts("ibuprofen, creat 2", rules=[])["med_flags"] == []


def test_omitted_rules_do_not_activate_disk_seed(monkeypatch):
    monkeypatch.setattr(F, "load_rules", lambda: {"rules": [rule()]})
    assert F.analyze_facts("ibuprofen, creat 2")["med_flags"] == []


def test_all_conditions_require_all_values_and_use_latest_values():
    candidate = rule(applies_when={"all": [
        {"field": "creatinine", "op": ">=", "value": 1.5},
        {"field": "sbp", "op": "<", "value": 90},
    ]})
    assert F.analyze_facts("ibuprofen creat 2 BP 80/50", rules=[candidate])["med_flags"]
    assert not F.analyze_facts("ibuprofen creat 2", rules=[candidate])["med_flags"]
    assert not F.analyze_facts("ibuprofen creat 2 BP 80/50, later BP 120/80", rules=[candidate])["med_flags"]


def test_case_level_rule_needs_no_medication():
    candidate = rule(case_level=True, drug_classes=[])
    fx = F.analyze_facts("creat 176.8 umol/L", rules=[candidate])
    assert fx["case_flags"] and not fx["med_flags"]


def test_workspace_catalog_is_request_local_and_multi_class():
    catalog = {"syntheticdrug": {"classes": ["diuretic", "potassium_sparing"],
                                 "brands": ["Synthetic Brand"], "regional_names": ["اسم تجريبي"]}}
    original = copy.deepcopy(catalog)
    meds = F.find_meds("Synthetic Brand 25 mg; اسم تجريبي", drug_catalog=catalog)
    assert len(meds) == 1
    assert meds[0]["classes"] == ["diuretic", "potassium_sparing"]
    assert meds[0]["confidence"] == "exact" and not meds[0]["verify"]
    assert F.find_meds("Synthetic Brand 25 mg", drug_catalog={}) == []
    assert F.find_meds("Synthetic Brand 25 mg") == []
    assert catalog == original
    candidate = rule(drug_classes=["potassium_sparing"])
    assert F.analyze_facts("Synthetic Brand 25 mg, creat 2", rules=[candidate], drug_catalog=catalog)["med_flags"]


def test_fuzzy_matches_remain_explicitly_flagged_verify():
    meds = F.find_meds("Targ 80 mg")
    assert meds[0]["generic"] == "valsartan"
    assert meds[0]["confidence"] == "fuzzy" and meds[0]["verify"] is True


def test_default_catalog_covers_candidate_classes_and_brands():
    for class_name, name in [
        ("nsaid", "brufen"), ("anticoagulant", "eliquis"), ("metformin", "glucophage"),
        ("aminoglycoside", "gentamicin"), ("nephrotoxin", "vancomycin"),
        ("qt_prolonging", "ondansetron"), ("potassium_sparing", "aldactone"),
    ]:
        assert class_name in F.CLASSES
        assert class_name in F.find_meds(name)[0]["classes"]


def test_seed_is_sourced_draft_data_not_runtime_policy():
    seed = F.load_rules()
    assert len(seed["rules"]) >= 6
    for candidate in seed["rules"]:
        assert candidate["status"] == "draft"
        assert candidate["enabled"] is False
        assert candidate["sources"]
        for source in candidate["sources"]:
            assert source["title"] and source["url"].startswith("https://") and source["quote"]
        F.validate_rule_definition(candidate)


def test_draft_preview_uses_canonical_parser_but_never_approves_or_mutates():
    candidate = rule(status="draft", enabled=False)
    original = copy.deepcopy(candidate)
    result = F.preview_rule("ibuprofen creat 176.8 umol/L", candidate, regex_sample="Possible AKI")
    assert result["would_fire"] is True
    assert result["regex"]["matched"] is True
    assert result["values"]["creatinine"] == pytest.approx(2)
    assert candidate == original
    assert F.analyze_facts("ibuprofen creat 2", rules=[candidate])["med_flags"] == []


@pytest.mark.parametrize("pattern", ["[", "(?<name>x)", r"\p{L}", r"(x)\1", "(?=kidney)"])
def test_regex_policy_rejects_invalid_or_nonportable_patterns(pattern):
    with pytest.raises(ValueError):
        F.validate_link_terms(pattern)


def test_bounded_regex_tester_does_not_hang_on_nested_quantifiers():
    result = F.test_link_terms(r"(a+)+$", "a" * 8000 + "!")
    assert result["timed_out"] is True
    assert result["matched"] is False
