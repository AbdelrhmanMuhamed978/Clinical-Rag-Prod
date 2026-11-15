"""Authenticated, private, request-local canonical screening (no model or retrieval)."""
import asyncio
import copy
import json

import httpx
import pytest
from fastapi.testclient import TestClient

import facts as F
import pipeline as P
from pipeline_service.app import create_app


HEADERS = {"Authorization": "Bearer synthetic-user-token", "X-Pipeline-Key": "synthetic-service-key"}
CATALOG = {"syntheticdrug": {"classes": ["nsaid", "nephrotoxin"], "brands": ["Synthetic Brand"], "regional_names": ["اسم تجريبي"]}}


def candidate(**changes):
    return {
        "id": "synthetic", "title": "Synthetic screening fixture", "status": "draft", "enabled": False,
        "drug_classes": ["nsaid"], "case_level": False,
        "applies_when": {"any": [{"field": "creatinine", "op": ">=", "value": 1.5}]},
        "sources": [{"title": "Synthetic source", "url": "https://example.org/source", "quote": "Test only."}],
        "link_terms": r"kidney|\bAKI\b", **changes,
    }


def payload(**changes):
    return {"rule": candidate(), "text": "Synthetic Brand 25 mg; creat 176.8 µmol/L; k 2.9; K+ 4.2",
            "drugCatalog": copy.deepcopy(CATALOG), "catalogRevision": 7, "regexSample": "Possible AKI", **changes}


@pytest.fixture
def preview_app(monkeypatch):
    monkeypatch.setenv("PIPELINE_SERVICE_KEY", "synthetic-service-key")

    def forbidden(*args, **kwargs):
        raise AssertionError("A rule preview must not load disk rules, retrieve, generate or construct a backend.")

    monkeypatch.setattr(P, "analyze", forbidden)
    monkeypatch.setattr(F, "load_rules", forbidden)
    return create_app(backend_factory=forbidden)


def test_preview_uses_the_canonical_values_catalog_and_regex_without_activation(preview_app, caplog):
    request = payload()
    original = copy.deepcopy(request)
    with TestClient(preview_app) as client:
        response = client.post("/rules/preview", headers=HEADERS, json=request)
    assert response.status_code == 200
    assert response.headers["cache-control"] == "no-store"
    result = response.json()
    assert result["would_fire"] is True
    assert result["catalog_revision"] == 7
    assert result["values"]["creatinine"] == pytest.approx(2)
    assert result["values"]["potassium"] == pytest.approx(4.2)
    assert result["regex"] == {"matched": True, "timed_out": False}
    assert result["meds"][0]["classes"] == ["nsaid", "nephrotoxin"]
    assert result["meds"][0]["verify"] is False
    assert any("latest" in warning for warning in result["warnings"])
    assert "not stored" in result["notice"]
    assert request == original
    assert F.analyze_facts(request["text"], rules=[request["rule"]], drug_catalog=request["drugCatalog"])["med_flags"] == []
    assert "176.8" not in caplog.text and "Possible AKI" not in caplog.text


@pytest.mark.parametrize("headers", [
    {}, {"Authorization": "Bearer token"}, {"X-Pipeline-Key": "synthetic-service-key"},
    {**HEADERS, "X-Pipeline-Key": "wrong"}, {**HEADERS, "Authorization": "Basic token"},
    {**HEADERS, "Authorization": "Bearer token with spaces"},
])
def test_preview_requires_both_credentials(preview_app, headers):
    with TestClient(preview_app) as client:
        response = client.post("/rules/preview", headers=headers, json=payload())
    assert response.status_code == 401
    assert response.headers["cache-control"] == "no-store"


def test_preview_fails_closed_when_the_service_key_is_absent(monkeypatch):
    monkeypatch.delenv("PIPELINE_SERVICE_KEY", raising=False)
    with TestClient(create_app()) as client:
        response = client.post("/rules/preview", headers=HEADERS, json=payload())
    assert response.status_code == 503
    assert response.headers["cache-control"] == "no-store"


@pytest.mark.parametrize("definition", [
    candidate(link_terms="[PRIVATE_CASE_MARKER"), candidate(sources=[]),
    candidate(applies_when={"any": [{"field": "aki", "op": ">=", "value": 1}]}),
    candidate(applies_when={"any": [{"field": "creatinine", "op": ">=", "value": 10 ** 400}]}),
    candidate(enabled="true"), candidate(status="unknown"),
])
def test_rejected_definitions_are_private_and_never_call_the_pipeline(preview_app, definition):
    with TestClient(preview_app) as client:
        response = client.post("/rules/preview", headers=HEADERS, json=payload(rule=definition, text="PRIVATE_CASE_MARKER"))
    assert response.status_code == 422
    assert response.headers["cache-control"] == "no-store"
    assert "PRIVATE_CASE_MARKER" not in response.text


@pytest.mark.parametrize("changes", [
    {"text": "PRIVATE_CASE_MARKER " * 6000}, {"regexSample": "PRIVATE_CASE_MARKER " * 1000},
    {"drugCatalog": {"x": {"classes": ["unknown"], "brands": [], "regional_names": []}}},
    {"drugCatalog": {"x": {"classes": ["nsaid"], "brands": ["same"], "regional_names": []},
                     "y": {"classes": ["metformin"], "brands": ["ＳＡＭＥ"], "regional_names": []}}},
])
def test_preview_rejects_oversized_inputs_or_invalid_recognition_without_echo(preview_app, changes):
    with TestClient(preview_app) as client:
        response = client.post("/rules/preview", headers=HEADERS, json=payload(**changes))
    assert response.status_code == 422
    assert "PRIVATE_CASE_MARKER" not in response.text
    assert response.headers["cache-control"] == "no-store"


def test_preview_malformed_json_and_body_limit_are_private(preview_app):
    with TestClient(preview_app) as client:
        malformed = client.post("/rules/preview", headers={**HEADERS, "Content-Type": "application/json"},
                                content=b'{"text": PRIVATE_CASE_MARKER}')
        oversized = client.post("/rules/preview", headers=HEADERS, json=payload(text="PRIVATE_CASE_MARKER " * 100000))
    assert malformed.status_code == 422 and oversized.status_code == 413
    for response in (malformed, oversized):
        assert "PRIVATE_CASE_MARKER" not in response.text
        assert response.headers["cache-control"] == "no-store"


def test_preview_reports_bounded_regex_timeout_without_firing_or_approving(preview_app):
    with TestClient(preview_app) as client:
        response = client.post("/rules/preview", headers=HEADERS,
                               json=payload(rule=candidate(link_terms=r"(a+)+$"), text="creat 2", regexSample="a" * 8000 + "!"))
    assert response.status_code == 200
    assert response.json()["would_fire"] is False  # No recognized medication.
    assert response.json()["regex"] == {"matched": False, "timed_out": True}


def test_service_forwards_isolated_catalogs_and_explicit_request_rules(monkeypatch):
    monkeypatch.setenv("PIPELINE_SERVICE_KEY", "synthetic-service-key")
    monkeypatch.setenv("PIPELINE_RETRIEVE_BASE_URL", "http://backend")

    async def run_pair():
        ready = asyncio.Event()
        arrivals = 0

        async def analyze(text, backend, emit, *, config, rules, history, drug_catalog):
            nonlocal arrivals
            facts = F.analyze_facts(text, rules=rules, drug_catalog=drug_catalog)
            arrivals += 1
            if arrivals == 2:
                ready.set()
            await asyncio.wait_for(ready.wait(), timeout=3)
            return facts

        monkeypatch.setattr(P, "analyze", analyze)
        app = create_app(backend_factory=lambda request, token: object())
        common = {"text": "Synthetic Brand creat 2", "config": {}, "history": [], "workspaceId": "workspace",
                  "retrieveUrl": "http://backend/api/workspaces/workspace/analyze/retrieve",
                  "rules": [candidate(status="approved", enabled=True)], "catalogRevision": 1}
        first = {**common, "drugCatalog": copy.deepcopy(CATALOG)}
        second = {**common, "drugCatalog": {"syntheticdrug": {"classes": ["metformin"], "brands": ["Synthetic Brand"], "regional_names": []}}}
        async with httpx.AsyncClient(transport=httpx.ASGITransport(app), base_url="http://service") as client:
            responses = await asyncio.gather(client.post("/analyze", headers=HEADERS, json=first),
                                             client.post("/analyze", headers=HEADERS, json=second))
        return responses

    first, second = asyncio.run(run_pair())
    assert first.status_code == second.status_code == 200
    assert first.json()["med_flags"] and not second.json()["med_flags"]
    assert first.json()["meds"][0]["classes"] == ["nsaid", "nephrotoxin"]
    assert second.json()["meds"][0]["classes"] == ["metformin"]


def test_extreme_numeric_inputs_cannot_produce_nonfinite_facts_or_invalid_json():
    result = F.preview_rule("calcium " + "9" * 308 + " mmol/L; creat " + "9" * 400,
                            candidate(case_level=True, drug_classes=[]), drug_catalog={})
    assert "creatinine" not in result["values"] and "calcium" not in result["values"]
    json.dumps(result, allow_nan=False)
