"""Canonical, request-local facts and rule screening. Disk rules are import-only seeds.

No isolated creatinine or blood-pressure value diagnoses AKI or shock. Medication
recognition is a screening aid, not a reconciliation of prescriptions or exposure.
"""
import copy
import difflib
import json
import math
import operator
import re
import unicodedata
from functools import lru_cache
from pathlib import Path

import clinical_text as CT

import regex as bounded_regex

RULES_PATH = Path(__file__).parent / "rules.json"
DRUGS_PATH = Path(__file__).parent / "drugs.json"
VOCABULARY = json.loads((Path(__file__).parent / "clinical-vocabulary.json").read_text(encoding="utf-8"))
OPS = {"<": operator.lt, "<=": operator.le, ">": operator.gt, ">=": operator.ge}
NUMBER = r"[+-]?(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d+)?"
LABELS = {
    "sbp": r"sbp|systolic(?: blood pressure)?",
    "dbp": r"dbp|diastolic(?: blood pressure)?",
    "map": r"map|mean arterial pressure",
    "hr": r"hr|heart rate|pulse",
    "rr": r"rr|resp(?:iratory)? rate",
    "spo2": r"spo[2₂]",
    "temp": r"temp(?:erature)?",
    "hb": r"hb|hgb|hemoglobin|haemoglobin",
    "wbc": r"tlc|wbc|wcc",
    "platelets": r"plt|platelets?",
    "creatinine": r"srcr|s\.?\s?cr|creatinine|creat",
    "potassium": r"potassium|k\+?",
    "magnesium": r"magnesium|mg(?=\s*[:=])",  # Bare 'mg' is a dose unit, never a lab label.
    "calcium": r"calcium|ca",
    "albumin": r"albumin",
    "alt": r"alt",
    "ast": r"ast",
    "bilirubin": r"total bilirubin|bilirubin|t\.?\s?bil(?:irubin)?",
    "age": r"age",
}
MENTATION = r"\b(dcl|depressed (?:level of )?consciousness|altered (?:mental|consciousness)|confus\w*|drows\w*|unresponsive)\b"
ASSUMPTIONS = ("Values without explicit units use the displayed canonical units: creatinine, calcium, magnesium and "
               "bilirubin mg/dL; potassium mmol/L; haemoglobin and albumin g/dL; WBC and platelets ×10⁹/L "
               "(unitless counts ≥1000 are interpreted as /µL); temperature °C. Confirm units and medication exposure.")
UNITS = {field: detail["unit"] for field, detail in VOCABULARY["fields"].items()}
UNIT_SUFFIX = re.compile(
    r"\s*(?P<unit>(?:[x×]\s*)?10\s*(?:\^|\*\*)?\s*[⁹³93]\s*/\s*[a-zµμ]+"
    r"|(?:[a-zµμ]+\s*)?/\s*[a-zµμ]+|°\s*[cf]|%|mmhg\b|bpm\b|(?:mg|mcg|g)\b)", re.I)
UNIT_FACTORS = {
    "creatinine": {"mg/dl": 1, "umol/l": 1 / 88.4},
    "bilirubin": {"mg/dl": 1, "umol/l": 1 / 17.1},
    "hb": {"g/dl": 1, "g/l": .1},
    "albumin": {"g/dl": 1, "g/l": .1},
    "potassium": {"mmol/l": 1, "meq/l": 1},
    "magnesium": {"mg/dl": 1, "mmol/l": 2.4305},
    "calcium": {"mg/dl": 1, "mmol/l": 4.008},
    "wbc": {"x10^9/l": 1, "x10^3/ul": 1, "/ul": .001},
    "platelets": {"x10^9/l": 1, "x10^3/ul": 1, "/ul": .001},
    "alt": {"u/l": 1, "iu/l": 1}, "ast": {"u/l": 1, "iu/l": 1},
    "sbp": {"mmhg": 1}, "dbp": {"mmhg": 1}, "map": {"mmhg": 1},
    "hr": {"bpm": 1, "/min": 1}, "rr": {"/min": 1}, "spo2": {"%": 1},
    "temp": {"°c": 1}, "age": {},
}


def _num(s): return float(s.replace(",", ""))


def _finite_number(value):
    if type(value) not in (int, float):
        return False
    try:
        return math.isfinite(value)
    except OverflowError:
        return False


def _unit_key(unit):
    key = re.sub(r"\s+", "", unit.lower()).replace("µ", "u").replace("μ", "u")
    key = key.replace("×", "x").replace("⁹", "^9").replace("³", "^3").replace("**", "^")
    key = re.sub(r"^(?:x)?10\^?([93])/", r"x10^\1/", key)
    return key


def _parse_values(text):
    occurrences = {field: [] for field in LABELS}
    # Merge paired BP and individual SBP/DBP into the same chronological stream.
    for match in re.finditer(rf"\b(?:blood pressure|bp)\s*[:=]?\s*({NUMBER})\s*/\s*({NUMBER})", text, re.I):
        for field, group in (("sbp", 1), ("dbp", 2)):
            suffix = UNIT_SUFFIX.match(text, match.end())
            occurrences[field].append((match.start(), _num(match[group]), suffix["unit"] if suffix else None))
    for field, label in LABELS.items():
        pattern = rf"(?<!\w)(?:{label})(?!\w)\s*(?:\((?P<before>[^)\n]{{1,32}})\))?\s*[:=]?\s*(?P<value>{NUMBER})(?![\d.])"
        for match in re.finditer(pattern, text, re.I):
            suffix = UNIT_SUFFIX.match(text, match.end())
            occurrences[field].append((match.start(), _num(match["value"]), match["before"] or (suffix["unit"] if suffix else None)))
    for match in re.finditer(r"\b(\d{1,3})\s*[- ]?\s*years?\s*[- ]?old", text, re.I):
        occurrences["age"].append((match.start(), _num(match[1]), None))

    v, details, warnings = {}, {}, []
    for field, observations in occurrences.items():
        if not observations:
            continue
        _, value, raw_unit = max(observations, key=lambda observation: observation[0])
        if len(observations) > 1:
            warnings.append(f"{field}: repeated values; the latest textual occurrence was used (not timestamp ordering).")
        if not math.isfinite(value) or (value < 0 and field != "temp"):
            warnings.append(f"{field}: invalid numeric value; excluded from screening.")
            continue
        unit_key = _unit_key(raw_unit) if raw_unit else None
        if unit_key and unit_key not in UNIT_FACTORS[field]:
            warnings.append(f"{field}: unsupported explicit unit; the latest observation was excluded, without falling back to an older value.")
            continue
        factor = UNIT_FACTORS[field].get(unit_key, 1)
        if not raw_unit and field in ("wbc", "platelets") and value >= 1000:
            factor = .001
        normalized = value * factor
        if not _finite_number(normalized):
            warnings.append(f"{field}: conversion exceeded the finite numeric range; excluded from screening.")
            continue
        v[field] = normalized
        details[field] = {"unit": UNITS[field], "source_unit":
                          ("umol/L" if unit_key == "umol/l" else raw_unit.strip()) if raw_unit else None,
                          "converted": factor != 1, "assumed": not bool(raw_unit)}
        if not raw_unit:
            warnings.append(f"{field}: unit assumed as {UNITS[field]}; confirm the original unit.")
    if details.get("creatinine", {}).get("assumed") and v.get("creatinine", 0) > 20:
        warnings.append("Creatinine looks like µmol/L, but mg/dL was assumed. Check the unit; no conversion was guessed.")
    m = re.search(r"\b(female|woman|male|man)\b", text, re.I)
    if m: v["sex"] = "F" if m[1].lower() in ("female", "woman") else "M"
    m = re.search(MENTATION, text, re.I)
    v["altered_mentation"] = bool(m)
    v["mentation_term"] = m[1] if m else None
    return v, details, warnings


def extract_values(text):
    return _parse_values(text)[0]


def derive(v):
    d = {}
    if "map" not in v and "sbp" in v and "dbp" in v: d["map"] = round((v["sbp"] + 2 * v["dbp"]) / 3, 1)
    if "hr" in v and v.get("sbp", 0) > 0: d["shock_index"] = round(v["hr"] / v["sbp"], 2)
    if "calcium" in v and "albumin" in v: d["corrected_ca"] = round(v["calcium"] + 0.8 * (4.0 - v["albumin"]), 1)
    if "rr" in v or "sbp" in v:
        d["qsofa"] = sum([v.get("rr", 0) >= 22, "sbp" in v and v["sbp"] <= 100, bool(v.get("altered_mentation"))])
    return {field: value for field, value in d.items() if _finite_number(value)}


def flag_list(v, d):
    out = []
    def add(i, label, value, note=""): out.append({"id": i, "label": label, "value": value, "note": note})
    g = v.get
    map_value = v.get("map", d.get("map"))
    if ("sbp" in v and v["sbp"] < 90) or (map_value is not None and map_value < 65):
        add("hypotension", "Low blood-pressure screen (not a shock diagnosis)",
            f"SBP {v.get('sbp', '?')}, DBP {v.get('dbp', '?')}, MAP {map_value if map_value is not None else '?'}")
    if g("temp", 0) >= 38.0: add("fever", "Fever", f"{v['temp']:g} °C")
    if g("hr", 0) > 100: add("tachycardia", "Tachycardia", f"HR {v['hr']:g}")
    if g("rr", 0) >= 22: add("tachypnea", "Tachypnoea", f"RR {v['rr']:g}")
    if "spo2" in v and v["spo2"] < 90: add("hypoxemia", "Hypoxaemia", f"SpO₂ {v['spo2']:g} %")
    if g("wbc", 0) > 12: add("leukocytosis", "Leukocytosis", f"{v['wbc']:g} ×10³/µL")
    if "hb" in v and v["hb"] < (13 if g("sex") == "M" else 12): add("anemia", "Anaemia", f"Hb {v['hb']:g} g/dL")
    if "platelets" in v:
        p = v["platelets"]
        if p < 50: add("thrombocytopenia_severe", "Severe thrombocytopenia", f"{p:g} ×10³/µL")
        elif p < 100: add("thrombocytopenia_moderate", "Moderate thrombocytopenia", f"{p:g} ×10³/µL")
        elif p < 150: add("thrombocytopenia_mild", "Mild thrombocytopenia", f"{p:g} ×10³/µL")
    if g("creatinine", 0) >= 1.5: add("aki_possible", "Raised-creatinine screen (not an AKI diagnosis)", f"{v['creatinine']:g} mg/dL", "baseline, trend and urine output unknown; screening cutoff only")
    if "potassium" in v and v["potassium"] < 3.5: add("hypokalemia", "Hypokalaemia", f"K {v['potassium']:g} mmol/L")
    if "magnesium" in v and v["magnesium"] < 1.8: add("magnesium_low", "Low magnesium", f"Mg {v['magnesium']:g} mg/dL")
    if g("potassium", 0) > 5.5: add("hyperkalemia", "Hyperkalaemia", f"K {v['potassium']:g} mmol/L")
    if "albumin" in v and v["albumin"] < 3.5: add("hypoalbuminemia", "Hypoalbuminaemia", f"{v['albumin']:g} g/dL")
    if g("alt", 0) > 41: add("alt_raised", "Mildly raised ALT", f"{v['alt']:g} U/L")
    return out


def _alias(name):
    return CT.alias(name)


def validate_drug_catalog(catalog):
    if not isinstance(catalog, dict) or len(catalog) > 512 or CT.utf16_length(json.dumps(catalog, ensure_ascii=False, separators=(",", ":"))) > 200000:
        raise ValueError("Drug catalog must be an object with at most 512 generics and 200000 characters.")
    aliases = {}
    for generic, entry in catalog.items():
        if not CT.valid_name(generic):
            raise ValueError("Each generic needs a nonempty name of at most 128 characters, without control characters.")
        if not isinstance(entry, dict) or set(entry) != {"classes", "brands", "regional_names"}:
            raise ValueError("Each drug requires classes, brands and regional_names arrays, without unknown fields.")
        classes = entry["classes"]
        if (not isinstance(classes, list) or not classes or len(classes) > len(VOCABULARY["drug_classes"])
                or any(not isinstance(c, str) or c not in VOCABULARY["drug_classes"] for c in classes)
                or len(set(classes)) != len(classes)):
            raise ValueError("Drug classes must be distinct known classes, with at least one class per drug.")
        names = [generic]
        for kind in ("brands", "regional_names"):
            if (not isinstance(entry[kind], list) or len(entry[kind]) > 64
                    or any(not CT.valid_name(name) for name in entry[kind])):
                raise ValueError("Drug aliases must be nonempty names of at most 128 characters, without control characters.")
            names += entry[kind]
        for name in names:
            normalized = _alias(name)
            if normalized in aliases and aliases[normalized] != generic:
                raise ValueError("A normalized generic, brand or regional name cannot identify two different drugs.")
            aliases[normalized] = generic
    return aliases


def load_drugs():
    return json.loads(DRUGS_PATH.read_text(encoding="utf-8"))


DEFAULT_DRUGS = load_drugs()
CLASSES = {c: [g for g, entry in DEFAULT_DRUGS.items() if c in entry["classes"]] for c in VOCABULARY["drug_classes"]}


def find_meds(text, drug_catalog=None):
    catalog = DEFAULT_DRUGS if drug_catalog is None else drug_catalog
    names = validate_drug_catalog(catalog)
    normalized_text = unicodedata.normalize("NFKC", text)
    folded_text, input_spans = CT.fold_with_spans(normalized_text)
    found, exact_spans = {}, []
    # Longest aliases first, so 'unfractionated heparin' is one recognition rather than two.
    for name in sorted(names, key=len, reverse=True):
        pattern = r"(?<!\w)" + re.escape(name).replace(r"\ ", r"\s+") + r"(?!\w)"
        for match in re.finditer(pattern, folded_text):
            # Never recognize only part of a full-fold expansion (e.g. i inside İ).
            if (match.start() > 0 and input_spans[match.start()] == input_spans[match.start() - 1]
                    or match.end() < len(input_spans) and input_spans[match.end()] == input_spans[match.end() - 1]):
                continue
            if any(start <= match.start() and match.end() <= end for start, end in exact_spans):
                continue
            generic = names[name]
            displayed = normalized_text[input_spans[match.start()][0]:input_spans[match.end() - 1][1]]
            found.setdefault(generic, {"input": displayed, "confidence": "exact", "verify": False})
            exact_spans.append(match.span())
    fuzzy_names = [name for name in names if re.fullmatch(r"[a-z][a-z-]{3,}", name)]
    for match in re.finditer(r"\b([A-Za-z][A-Za-z\-]{3,})\s+\d+(?:\.\d+)?\b", text):
        name = match[1].lower()
        if name in names:
            continue
        close = difflib.get_close_matches(name, fuzzy_names, n=1, cutoff=0.8)
        if close:
            found.setdefault(names[close[0]], {"input": match[1], "confidence": "fuzzy", "verify": True})
    return [{"generic": g, "class": catalog[g]["classes"][0], "classes": list(catalog[g]["classes"]), **info}
            for g, info in found.items()]


def load_rules(): return json.loads(RULES_PATH.read_text(encoding="utf-8"))


def validate_link_terms(pattern):
    problem = CT.portable_regex_problem(pattern)
    if problem:
        raise ValueError(problem)
    if not pattern:
        return
    try:
        re.compile(pattern, re.I)
    except re.error:
        raise ValueError("Link terms must compile as a portable regular expression.") from None


@lru_cache(maxsize=256)
def _compiled_link_terms(pattern):
    validate_link_terms(pattern)
    return bounded_regex.compile(pattern, bounded_regex.I | bounded_regex.VERSION0)


def test_link_terms(pattern, sample):
    validate_link_terms(pattern)
    if not isinstance(sample, str) or CT.utf16_length(sample) > 10000:
        raise ValueError("Regex sample text must be at most 10000 characters.")
    if not pattern:
        return {"matched": False, "timed_out": False}
    try:
        return {"matched": bool(_compiled_link_terms(pattern).search(sample, timeout=.05)), "timed_out": False}
    except TimeoutError:
        return {"matched": False, "timed_out": True}


def validate_rule_definition(rule):
    if not isinstance(rule, dict):
        raise ValueError("Rule must be an object.")
    if not isinstance(rule.get("id"), str) or not re.fullmatch(r"[A-Za-z0-9_-]{1,64}", rule["id"]):
        raise ValueError("Rule id must use 1–64 letters, digits, underscores or hyphens.")
    if not isinstance(rule.get("title"), str) or not CT.has_text(rule["title"]) or CT.utf16_length(rule["title"]) > 256:
        raise ValueError("Rule title is required (at most 256 characters).")
    for key, limit in (("message", 10000), ("caveat", 10000), ("level", 64)):
        if rule.get(key) is not None and (not isinstance(rule[key], str) or CT.utf16_length(rule[key]) > limit):
            raise ValueError(f"Rule {key} is too long or is not text.")
    if rule.get("status") not in (None, *VOCABULARY["statuses"]):
        raise ValueError("Unknown rule status.")
    if not isinstance(rule.get("case_level", False), bool) or not isinstance(rule.get("enabled", True), bool):
        raise ValueError("Rule case_level and enabled must be booleans.")
    group = rule.get("applies_when")
    if not isinstance(group, dict) or set(group) not in ({"any"}, {"all"}):
        raise ValueError("Conditions require exactly one 'any' or 'all' group.")
    conditions = next(iter(group.values()))
    if not isinstance(conditions, list) or not 1 <= len(conditions) <= 64:
        raise ValueError("Provide 1–64 condition rows.")
    for condition in conditions:
        if (not isinstance(condition, dict) or set(condition) != {"field", "op", "value"}
                or not isinstance(condition["field"], str) or condition["field"] not in UNITS
                or not isinstance(condition["op"], str) or condition["op"] not in OPS
                or not _finite_number(condition["value"])):
            raise ValueError("Each condition requires a known field, a supported comparison and a finite numeric value, without extra keys.")
    classes = rule.get("drug_classes")
    if (not isinstance(classes, list) or any(not isinstance(c, str) or c not in CLASSES for c in classes)
            or len(set(classes)) != len(classes) or (not rule.get("case_level", False) and not classes)):
        raise ValueError("Use distinct known drug classes; medication rules require at least one class.")
    sources = rule.get("sources")
    if not isinstance(sources, list) or not 1 <= len(sources) <= 32:
        raise ValueError("At least one source with title and URL is required (up to 32).")
    for source in sources:
        if (not isinstance(source, dict) or not isinstance(source.get("title"), str)
                or not CT.has_text(source["title"]) or CT.utf16_length(source["title"]) > 512
                or not isinstance(source.get("url"), str)):
            raise ValueError("Each source requires a title and HTTP(S) URL.")
        if not CT.http_source_url(source["url"]):
            raise ValueError("Source URLs must use HTTP(S), without embedded credentials.") from None
        if source.get("quote") is not None and (not isinstance(source["quote"], str) or CT.utf16_length(source["quote"]) > 10000):
            raise ValueError("Source quotes must be text of at most 10000 characters.")
    validate_link_terms(rule.get("link_terms"))


def _met(cond, ctx):
    value = ctx.get(cond["field"])
    return _finite_number(value) and _finite_number(cond["value"]) and OPS[cond["op"]](value, cond["value"])


def evaluate_rules(v, d, meds, rules=None):
    ctx = {**v, **d}
    med_flags, case_flags = [], []
    configured_rules = [] if rules is None else (
        rules.get("rules", []) if isinstance(rules, dict) else rules
    )
    for r in configured_rules:
        if not isinstance(r, dict) or r.get("status") != "approved" or r.get("enabled") is not True:
            continue
        try:
            validate_rule_definition(r)
        except (ValueError, TypeError, OverflowError):
            continue  # Invalid legacy/malicious definitions are never active policy.
        applies_when = r["applies_when"]
        conditions = next(iter(applies_when.values()))
        reasons = [
            f"{c['field']} {ctx[c['field']]:g} {c['op']} {c['value']:g}"
            for c in conditions
            if isinstance(c, dict) and all(key in c for key in ("field", "op", "value"))
            and isinstance(c["field"], str) and c.get("op") in OPS and _met(c, ctx)
        ]
        if not reasons or ("all" in applies_when and len(reasons) != len(conditions)): continue
        base = {k: r.get(k) for k in ("id", "title", "level", "message", "caveat", "sources")} | {"reasons": reasons, "link_terms": r.get("link_terms")}
        if r.get("case_level"): case_flags.append(base)
        else:
            drugs = [m for m in meds if set(m.get("classes", [m.get("class")])) & set(r["drug_classes"])]
            if drugs: med_flags.append(base | {"drugs": drugs})
    return med_flags, case_flags


def patient_summary(v, d):
    parts = []
    if "age" in v: parts.append(f"{v['age']:g}-year-old" + (" woman" if v.get("sex") == "F" else " man" if v.get("sex") == "M" else ""))
    if "sbp" in v: parts.append(f"SBP {v['sbp']:g}, DBP {v.get('dbp', '?')} (MAP {v.get('map', d.get('map', '?'))})")
    for k, lab in (("hr", "HR"), ("rr", "RR"), ("spo2", "SpO2"), ("temp", "T")):
        if k in v: parts.append(f"{lab} {v[k]:g}")
    for k, lab in (("hb", "Hb"), ("wbc", "WBC"), ("platelets", "platelets"), ("creatinine", "creatinine"), ("potassium", "K")):
        if k in v: parts.append(f"{lab} {v[k]:g}")
    return "; ".join(parts) or "not given"


def analyze_facts(text, rules=None, drug_catalog=None):
    v, units, warnings = _parse_values(text)
    d = derive(v)
    meds = find_meds(text, drug_catalog=drug_catalog)
    if "qsofa" in d and not all(field in v for field in ("rr", "sbp")):
        warnings.append("qsofa: missing RR or SBP; this is a partial screening score, not a diagnosis.")
    mf, cf = evaluate_rules(v, d, meds, rules=rules)
    return {"values": v, "derived": d, "flags": flag_list(v, d), "meds": meds, "med_flags": mf, "case_flags": cf,
            "assumptions": [ASSUMPTIONS], "warnings": warnings, "unit_details": units, "summary": patient_summary(v, d)}


def preview_rule(text, rule, drug_catalog=None, regex_sample=""):
    if not isinstance(text, str) or CT.utf16_length(text) > 100000:
        raise ValueError("Case preview text must be at most 100000 characters.")
    validate_rule_definition(rule)
    candidate = copy.deepcopy(rule) | {"status": "approved", "enabled": True}
    fx = analyze_facts(text, rules=[candidate], drug_catalog=drug_catalog)
    matches = fx["med_flags"] + fx["case_flags"]
    return {"would_fire": bool(matches), "reasons": matches[0]["reasons"] if matches else [],
            "values": fx["values"], "derived": fx["derived"], "meds": fx["meds"],
            "unit_details": fx["unit_details"], "warnings": fx["warnings"],
            "regex": test_link_terms(rule.get("link_terms"), regex_sample),
            "notice": "Preview ignores status/enablement and is not approval or a clinical recommendation. Inputs are not stored."}
