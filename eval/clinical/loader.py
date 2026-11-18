"""Load and validate clinician-labelled evaluation sets without inventing labels.

The loader is deliberately strict: unknown keys, unresolved template fields,
missing attestations and contradictory labels fail closed. It distinguishes a
real clinician set from the synthetic worked example so a run cannot silently
present engineering-written examples as clinical evidence.
"""

from __future__ import annotations

import json
import re
from dataclasses import dataclass
from datetime import date
from pathlib import Path
from typing import Any


SCHEMA_VERSION = "1.0"
LABEL_SOURCES = frozenset({"clinician", "synthetic-fixture"})
CASE_KINDS = frozenset({"question", "deidentified_case"})
EXPECTED_MODES = frozenset({"question", "case"})
CASE_ID = re.compile(r"^[a-z0-9][a-z0-9-]{1,63}$")
TOP_LEVEL_KEYS = frozenset({
    "schemaVersion", "labelSource", "instructionsVersion", "clinician",
    "guidelineCorpus", "syntheticNotice", "cases",
})
CLINICIAN_KEYS = frozenset({"name", "role", "organisation", "labelledAt", "attestation"})
CORPUS_KEYS = frozenset({"description", "documentNames"})
CASE_KEYS = frozenset({
    "id", "kind", "input", "expectedDocuments", "expectedStatements",
    "forbiddenStatements", "expectedMode", "pendingClinicianFields", "notes",
})
DOCUMENT_KEYS = frozenset({"fileName", "page", "section"})


class ClinicalSetError(ValueError):
    """A clinical set is unusable; the message is safe to write in a run report."""


@dataclass(frozen=True)
class ExpectedDocument:
    file_name: str
    page: int
    section: str | None = None


@dataclass(frozen=True)
class ClinicalCase:
    id: str
    kind: str
    input: str
    expected_documents: tuple[ExpectedDocument, ...]
    expected_statements: tuple[str, ...]
    forbidden_statements: tuple[str, ...]
    expected_mode: str | None = None
    pending_clinician_fields: tuple[str, ...] = ()
    notes: str | None = None


@dataclass(frozen=True)
class ClinicalSet:
    path: Path
    schema_version: str
    label_source: str
    instructions_version: str
    clinician: dict[str, str]
    guideline_corpus: dict[str, Any]
    cases: tuple[ClinicalCase, ...]
    synthetic_notice: str | None = None

    @property
    def is_clinician_labelled(self) -> bool:
        return self.label_source == "clinician"

    def report_metadata(self) -> dict[str, Any]:
        """Return set metadata that is safe to write without case text."""
        return {
            "schemaVersion": self.schema_version,
            "labelSource": self.label_source,
            "instructionsVersion": self.instructions_version,
            "clinician": dict(self.clinician),
            "guidelineCorpus": {
                "description": self.guideline_corpus.get("description", ""),
                "documentNames": list(self.guideline_corpus.get("documentNames", [])),
            },
            "syntheticNotice": self.synthetic_notice,
            "caseCount": len(self.cases),
            "clinicalEvidence": self.is_clinician_labelled,
        }


class _Errors:
    def __init__(self) -> None:
        self.items: list[str] = []

    def add(self, message: str) -> None:
        self.items.append(message)

    def raise_if_any(self) -> None:
        if self.items:
            unique = list(dict.fromkeys(self.items))
            raise ClinicalSetError("Clinical set is not runnable:\n- " + "\n- ".join(unique))


def _object(value: Any, label: str, errors: _Errors) -> dict[str, Any] | None:
    if not isinstance(value, dict):
        errors.add(f"{label} must be a JSON object.")
        return None
    return value


def _unknown_keys(value: dict[str, Any], allowed: frozenset[str], label: str, errors: _Errors) -> None:
    unknown = sorted(set(value) - allowed)
    if unknown:
        errors.add(f"{label} contains unknown field(s): {', '.join(unknown)}.")


def _text(value: Any, label: str, maximum: int, errors: _Errors, minimum: int = 1) -> str | None:
    if not isinstance(value, str):
        errors.add(f"{label} must be a string.")
        return None
    text = value.strip()
    if not minimum <= len(text) <= maximum:
        errors.add(f"{label} must contain {minimum}–{maximum} characters.")
        return None
    if any(character in text for character in ("\x00", "\r")):
        errors.add(f"{label} contains a forbidden control character.")
        return None
    return text


def _string_list(value: Any, label: str, errors: _Errors, maximum_items: int,
                 maximum_length: int, minimum_items: int = 1) -> list[str]:
    if not isinstance(value, list):
        errors.add(f"{label} must be a JSON array.")
        return []
    if not minimum_items <= len(value) <= maximum_items:
        errors.add(f"{label} must contain {minimum_items}–{maximum_items} items.")
    result: list[str] = []
    for index, item in enumerate(value):
        text = _text(item, f"{label}[{index}]", maximum_length, errors)
        if text is not None:
            result.append(text)
    return result


def _parse_expected_documents(value: Any, label: str, errors: _Errors) -> tuple[ExpectedDocument, ...]:
    if not isinstance(value, list) or not value:
        errors.add(f"{label} must be a non-empty JSON array.")
        return ()
    if len(value) > 8:
        errors.add(f"{label} must contain at most 8 expected documents.")
    documents: list[ExpectedDocument] = []
    seen: set[tuple[str, int]] = set()
    for index, item in enumerate(value):
        item_label = f"{label}[{index}]"
        document = _object(item, item_label, errors)
        if document is None:
            continue
        _unknown_keys(document, DOCUMENT_KEYS, item_label, errors)
        file_name = _text(document.get("fileName"), f"{item_label}.fileName", 256, errors)
        page = document.get("page")
        if type(page) is not int or not 1 <= page <= 100000:
            errors.add(f"{item_label}.page must be a positive integer.")
            page = None
        section = None
        if "section" in document:
            section = _text(document.get("section"), f"{item_label}.section", 512, errors)
        if file_name and page is not None:
            key = (file_name.casefold(), page)
            if key in seen:
                errors.add(f"{item_label} duplicates expected document {file_name!r} page {page}.")
            seen.add(key)
            documents.append(ExpectedDocument(file_name, page, section))
    return tuple(documents)


def _parse_case(value: Any, index: int, errors: _Errors) -> ClinicalCase | None:
    label = f"cases[{index}]"
    case = _object(value, label, errors)
    if case is None:
        return None
    _unknown_keys(case, CASE_KEYS, label, errors)
    case_id = _text(case.get("id"), f"{label}.id", 64, errors)
    if case_id is not None and not CASE_ID.fullmatch(case_id):
        errors.add(f"{label}.id must use lowercase letters, digits and hyphens (2–64 characters).")
    kind = _text(case.get("kind"), f"{label}.kind", 32, errors)
    if kind is not None and kind not in CASE_KINDS:
        errors.add(f"{label}.kind must be one of: {', '.join(sorted(CASE_KINDS))}.")
    input_text = _text(case.get("input"), f"{label}.input", 100000, errors)
    documents = _parse_expected_documents(case.get("expectedDocuments"), f"{label}.expectedDocuments", errors)
    expected = _string_list(case.get("expectedStatements"), f"{label}.expectedStatements", errors, 20, 2000)
    forbidden = _string_list(case.get("forbiddenStatements"), f"{label}.forbiddenStatements", errors, 20, 2000,
                             minimum_items=0) if "forbiddenStatements" in case else []
    expected_mode = None
    if "expectedMode" in case:
        expected_mode = _text(case.get("expectedMode"), f"{label}.expectedMode", 16, errors)
        if expected_mode is not None and expected_mode not in EXPECTED_MODES:
            errors.add(f"{label}.expectedMode must be one of: {', '.join(sorted(EXPECTED_MODES))}.")
    pending = _string_list(case.get("pendingClinicianFields"), f"{label}.pendingClinicianFields",
                           errors, 20, 128, minimum_items=0) if "pendingClinicianFields" in case else []
    notes = None
    if "notes" in case:
        notes = _text(case.get("notes"), f"{label}.notes", 2000, errors)
    if not input_text or not case_id or not kind or not documents or not expected:
        return None
    normalized_expected = {statement.casefold() for statement in expected}
    normalized_forbidden = {statement.casefold() for statement in forbidden}
    overlap = sorted(normalized_expected & normalized_forbidden)
    if overlap:
        errors.add(f"{label} lists the same statement as both expected and forbidden.")
    if len(normalized_expected) != len(expected):
        errors.add(f"{label}.expectedStatements contains duplicates.")
    if len(normalized_forbidden) != len(forbidden):
        errors.add(f"{label}.forbiddenStatements contains duplicates.")
    return ClinicalCase(case_id, kind, input_text, documents, tuple(expected), tuple(forbidden),
                        expected_mode=expected_mode, pending_clinician_fields=tuple(pending), notes=notes)


def _parse_clinician(value: Any, errors: _Errors) -> dict[str, str]:
    clinician = _object(value, "clinician", errors)
    if clinician is None:
        return {}
    _unknown_keys(clinician, CLINICIAN_KEYS, "clinician", errors)
    result: dict[str, str] = {}
    for key, maximum in (("name", 128), ("role", 128), ("organisation", 256)):
        text = _text(clinician.get(key), f"clinician.{key}", maximum, errors)
        if text:
            result[key] = text
    labelled_at = clinician.get("labelledAt")
    if not isinstance(labelled_at, str) or not re.fullmatch(r"\d{4}-\d{2}-\d{2}", labelled_at.strip()):
        errors.add("clinician.labelledAt must be an ISO date (YYYY-MM-DD).")
    else:
        try:
            parsed = date.fromisoformat(labelled_at.strip())
        except ValueError:
            errors.add("clinician.labelledAt must be a real calendar date.")
        else:
            if parsed > date.today():
                errors.add("clinician.labelledAt cannot be in the future.")
            result["labelledAt"] = parsed.isoformat()
    attestation = _text(clinician.get("attestation"), "clinician.attestation", 2000, errors, minimum=20)
    if attestation:
        result["attestation"] = attestation
    return result


def _parse_corpus(value: Any, errors: _Errors, require_documents: bool) -> dict[str, Any]:
    corpus = _object(value, "guidelineCorpus", errors)
    if corpus is None:
        return {"description": "", "documentNames": []}
    _unknown_keys(corpus, CORPUS_KEYS, "guidelineCorpus", errors)
    description = _text(corpus.get("description"), "guidelineCorpus.description", 2000, errors,
                        minimum=1 if require_documents else 0) or ""
    documents = _string_list(corpus.get("documentNames"), "guidelineCorpus.documentNames", errors,
                             32, 256, minimum_items=1 if require_documents else 0)
    return {"description": description, "documentNames": documents}


def load_clinical_set(path: Path, *, require_cases: bool = True) -> ClinicalSet:
    """Load one JSON clinical set, failing closed on missing or contradictory labels."""
    path = path.resolve()
    if not path.is_file():
        raise ClinicalSetError(f"Clinical set not found: {path}")
    if path.suffix.casefold() != ".json":
        raise ClinicalSetError("Clinical sets must be JSON files; no other label format is accepted.")
    try:
        raw = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise ClinicalSetError(f"Clinical set is not readable JSON: {error}") from error

    errors = _Errors()
    root = _object(raw, "Clinical set", errors)
    if root is None:
        errors.raise_if_any()
        raise ClinicalSetError("Clinical set must be a JSON object.")
    _unknown_keys(root, TOP_LEVEL_KEYS, "Clinical set", errors)

    schema_version = _text(root.get("schemaVersion"), "schemaVersion", 16, errors)
    if schema_version != SCHEMA_VERSION:
        errors.add(f"schemaVersion must be exactly {SCHEMA_VERSION!r}.")
    label_source = _text(root.get("labelSource"), "labelSource", 32, errors)
    if label_source not in LABEL_SOURCES:
        errors.add(f"labelSource must be one of: {', '.join(sorted(LABEL_SOURCES))}.")
    instructions_version = _text(root.get("instructionsVersion"), "instructionsVersion", 32, errors)

    clinician: dict[str, str] = {}
    synthetic_notice = None
    if label_source == "clinician":
        clinician = _parse_clinician(root.get("clinician"), errors)
        if "syntheticNotice" in root:
            errors.add("A clinician-labelled set must not contain syntheticNotice.")
    elif label_source == "synthetic-fixture":
        synthetic_notice = _text(root.get("syntheticNotice"), "syntheticNotice", 2000, errors, minimum=40)
        if synthetic_notice is not None:
            notice = synthetic_notice.casefold()
            if "synthetic" not in notice or "not" not in notice or "clinician" not in notice:
                errors.add("syntheticNotice must state that the cases are synthetic and not clinician labels.")
        if "clinician" in root:
            errors.add("A synthetic set must not include a clinician attestation; it is not clinician-labelled.")
    else:
        # Keep a clear error even when labelSource itself was invalid.
        clinician = {}

    corpus = _parse_corpus(root.get("guidelineCorpus"), errors, require_documents=label_source == "clinician")

    cases_value = root.get("cases")
    cases: list[ClinicalCase] = []
    if not isinstance(cases_value, list):
        errors.add("cases must be a JSON array.")
    else:
        if require_cases and not cases_value:
            errors.add(
                "No clinician-labelled cases are present. Fill eval/clinical/template.json or use a completed "
                "clinician set; the synthetic worked example is not a substitute."
            )
        if len(cases_value) > 500:
            errors.add("cases must contain at most 500 entries.")
        seen_ids: set[str] = set()
        for index, value in enumerate(cases_value):
            case = _parse_case(value, index, errors)
            if case is None:
                continue
            if case.id in seen_ids:
                errors.add(f"cases[{index}].id duplicates {case.id!r}.")
            seen_ids.add(case.id)
            cases.append(case)

    errors.raise_if_any()
    # Keep type checkers and readers honest: these branches are unreachable after
    # errors.raise_if_any(), but return concrete values.
    assert label_source in LABEL_SOURCES
    assert instructions_version is not None
    return ClinicalSet(
        path=path,
        schema_version=SCHEMA_VERSION,
        label_source=label_source,
        instructions_version=instructions_version,
        clinician=clinician,
        guideline_corpus=corpus,
        cases=tuple(cases),
        synthetic_notice=synthetic_notice,
    )


def load_template(path: Path) -> dict[str, Any]:
    """Validate only the template shape; never treats the empty template as runnable."""
    path = path.resolve()
    try:
        raw = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise ClinicalSetError(f"Template is not readable JSON: {error}") from error
    errors = _Errors()
    root = _object(raw, "Clinical set", errors)
    if root is None:
        errors.raise_if_any()
        raise ClinicalSetError("Template must be a JSON object.")
    _unknown_keys(root, TOP_LEVEL_KEYS, "Clinical set", errors)
    if root.get("schemaVersion") != SCHEMA_VERSION:
        errors.add(f"schemaVersion must be exactly {SCHEMA_VERSION!r}.")
    if root.get("labelSource") != "clinician":
        errors.add("The template must use labelSource 'clinician'.")
    cases = root.get("cases")
    if not isinstance(cases, list) or cases:
        errors.add("The template must have an empty cases array until a clinician fills it.")
    clinician = _object(root.get("clinician"), "clinician", errors)
    if clinician is not None:
        _unknown_keys(clinician, CLINICIAN_KEYS, "clinician", errors)
    corpus = _object(root.get("guidelineCorpus"), "guidelineCorpus", errors)
    if corpus is not None:
        _unknown_keys(corpus, CORPUS_KEYS, "guidelineCorpus", errors)
    errors.raise_if_any()
    return raw


__all__ = [
    "ClinicalCase",
    "ClinicalSet",
    "ClinicalSetError",
    "ExpectedDocument",
    "load_clinical_set",
    "load_template",
]
