import json
import threading
from datetime import date
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import pytest

from eval.clinical.loader import ClinicalSetError, load_clinical_set, load_template
from eval.clinical.metrics import (
    compute_metrics,
    evaluate_case,
    page_matches,
    percentile,
    statement_coverage,
    statement_matches,
)
from eval import compare_baseline, run_clinical_eval


ROOT = Path(__file__).resolve().parents[1]
CLINICAL = ROOT / "eval" / "clinical"


def clinician_set(**overrides):
    payload = {
        "schemaVersion": "1.0",
        "labelSource": "clinician",
        "instructionsVersion": "1.0",
        "clinician": {
            "name": "Dr Synthetic Reviewer",
            "role": "Synthetic specialty",
            "organisation": "Synthetic organisation",
            "labelledAt": date.today().isoformat(),
            "attestation": "I confirm these synthetic labels are a test fixture for loader logic only.",
        },
        "guidelineCorpus": {
            "description": "Synthetic test corpus only",
            "documentNames": ["synthetic-only.pdf"],
        },
        "cases": [{
            "id": "synthetic-case",
            "kind": "question",
            "input": "synthetic question",
            "expectedDocuments": [{"fileName": "synthetic-only.pdf", "page": 1}],
            "expectedStatements": ["Synthetic expected statement"],
            "forbiddenStatements": ["Synthetic forbidden statement"],
        }],
    }
    payload.update(overrides)
    return payload


def write_set(tmp_path, payload):
    path = tmp_path / "set.json"
    path.write_text(json.dumps(payload), encoding="utf-8")
    return path


def test_template_has_shape_but_is_not_runnable():
    template = load_template(CLINICAL / "template.json")
    assert template["labelSource"] == "clinician"
    assert template["cases"] == []
    with pytest.raises(ClinicalSetError, match="No clinician-labelled cases"):
        load_clinical_set(CLINICAL / "template.json")


def test_synthetic_worked_example_is_explicitly_not_clinician_labelled():
    clinical = load_clinical_set(CLINICAL / "synthetic-example.json")
    assert clinical.label_source == "synthetic-fixture"
    assert clinical.is_clinician_labelled is False
    assert len(clinical.cases) == 3
    assert "not clinician labels" in clinical.synthetic_notice.casefold()
    metadata = clinical.report_metadata()
    assert metadata["clinicalEvidence"] is False
    assert "input" not in json.dumps(metadata)


def test_real_guideline_crag_case_is_a_pending_engineering_scaffold():
    clinical = load_clinical_set(CLINICAL / "real-who-crag-pending.json")
    assert clinical.is_clinician_labelled is False
    assert clinical.label_source == "synthetic-fixture"
    case = clinical.cases[0]
    assert case.id == "who-crag-screening-before-art"
    assert case.expected_mode == "question"
    assert {document.page for document in case.expected_documents} == {41, 11}
    assert "cryptococcal antigen" in case.expected_statements[0].casefold()
    assert "induction therapy" in case.forbidden_statements[0].casefold()
    assert case.pending_clinician_fields == ("screeningUnavailableStatement",)
    assert "screening unavailable" in (case.notes or "").casefold()
    assert "not clinician-labelled" in (clinical.synthetic_notice or "").casefold()


def test_expected_mode_is_scored_and_missing_mode_is_not_invented(tmp_path):
    payload = clinician_set()
    payload["cases"][0]["expectedMode"] = "question"
    clinical = load_clinical_set(write_set(tmp_path, payload))
    case = clinical.cases[0]
    response = {"plan_source": "single_question", "problems": []}
    result = evaluate_case(case, response, status=200, duration_ms=1.0, top_k=8, threshold=0.8)
    assert result["expectedMode"] == "question"
    assert result["actualMode"] == "question"
    assert result["modeMatched"] is True
    wrong = evaluate_case(case, {"plan_source": "rules", "problems": []},
                          status=200, duration_ms=1.0, top_k=8, threshold=0.8)
    assert wrong["modeMatched"] is False
    metrics = compute_metrics([result, wrong], top_k=8, threshold=0.8)
    assert metrics["modeExpectedCases"] == 2
    assert metrics["modeMatchedCases"] == 1
    assert metrics["modeAccuracy"] == 0.5

    invalid = clinician_set()
    invalid["cases"][0]["expectedMode"] = "PRIVATE_MODE"
    with pytest.raises(ClinicalSetError, match="expectedMode"):
        load_clinical_set(write_set(tmp_path, invalid))


def test_completed_clinician_set_loads_with_attestation(tmp_path):
    clinical = load_clinical_set(write_set(tmp_path, clinician_set()))
    assert clinical.is_clinician_labelled is True
    assert clinical.clinician["name"] == "Dr Synthetic Reviewer"
    assert clinical.cases[0].expected_documents[0].page == 1


@pytest.mark.parametrize("mutate, message", [
    (lambda value: value["clinician"].pop("attestation"), "attestation"),
    (lambda value: value["clinician"].update(name=""), "clinician.name"),
    (lambda value: value.update(labelSource="other"), "labelSource"),
    (lambda value: value.update(instructionsVersion=""), "instructionsVersion"),
    (lambda value: value["cases"][0].update(expectedStatements=[]), "expectedStatements"),
    (lambda value: value["cases"][0].update(expectedDocuments=[]), "expectedDocuments"),
    (lambda value: value["cases"][0].update(id="UPPER CASE"), "lowercase letters"),
    (lambda value: value["cases"][0].update(forbiddenStatements=["Synthetic expected statement"]),
     "both expected and forbidden"),
    (lambda value: value["cases"][0]["expectedDocuments"][0].update(page=0), "page"),
    (lambda value: value["cases"][0].update(extraField=True), "unknown field"),
])
def test_invalid_clinical_sets_fail_closed(tmp_path, mutate, message):
    payload = clinician_set()
    mutate(payload)
    with pytest.raises(ClinicalSetError, match=message):
        load_clinical_set(write_set(tmp_path, payload))


def test_duplicate_case_ids_are_rejected(tmp_path):
    payload = clinician_set()
    payload["cases"].append(dict(payload["cases"][0]))
    with pytest.raises(ClinicalSetError, match="duplicates"):
        load_clinical_set(write_set(tmp_path, payload))


def test_synthetic_set_requires_an_explicit_notice(tmp_path):
    payload = clinician_set(labelSource="synthetic-fixture")
    payload.pop("clinician")
    payload["syntheticNotice"] = "A short notice."
    with pytest.raises(ClinicalSetError, match="syntheticNotice"):
        load_clinical_set(write_set(tmp_path, payload))


def test_page_matching_supports_single_pages_and_ranges():
    assert page_matches(12, "12") is True
    assert page_matches(12, "p.12") is True
    assert page_matches(13, "12-14") is True
    assert page_matches(15, "12-14") is False
    assert page_matches(12, "") is False


def test_statement_matching_is_lexical_and_preserves_negation():
    assert statement_coverage(
        "Cystatin monitoring and a kidney function review.",
        "Cystatin monitoring and kidney function review.",
    ) == 1.0
    assert statement_matches("Offer anticoagulation", "Do not offer anticoagulation", 0.8) is False
    assert statement_matches("Do not offer anticoagulation", "Do not offer anticoagulation", 0.8) is True


def synthetic_case():
    return load_clinical_set(CLINICAL / "synthetic-example.json").cases[0]


def synthetic_response(include_forbidden=True):
    source = {
        "id": "S1",
        "label": "Synthetic fixture 2026",
        "title": "synthetic-only.pdf",
        "page": "1",
        "section": "Renal monitoring: cystatin",
        "score": 0.91,
    }
    items = [{
        "recommendation": "Cystatin monitoring and kidney function review.",
        "quote": "Synthetic source: cystatin monitoring and a kidney function review.",
        "citation": source,
    }]
    if include_forbidden:
        items.append({
            "recommendation": "No kidney function review is ever needed during cystatin monitoring.",
            "quote": "No kidney function review is ever needed during cystatin monitoring.",
            "citation": source,
        })
    return {
        "problems": [{
            "title": "Synthetic problem",
            "status": "ok",
            "items": items,
            "unverified": [{
                "recommendation": "Unsupported synthetic statement",
                "quote": "Unsupported synthetic quote",
                "issues": ["quote not found"],
            }],
            "sources": [source],
        }],
        "not_covered": [],
        "diagnostics": [{
            "problemIndex": 1,
            "drafts": 1,
            "candidates": 4,
            "verified": 2,
            "reasons": {"quote_not_found": 1, "deduplicated": 1},
        }],
    }


def test_case_metrics_report_hits_verified_statements_forbidden_and_reasons():
    case = synthetic_case()
    result = evaluate_case(case, synthetic_response(), status=200, duration_ms=12.5,
                           top_k=8, threshold=0.8)
    assert result["hitAtK"] is True
    assert result["rank"] == 1
    assert result["matchedExpectedStatements"] == 1
    assert result["forbiddenViolations"] == 1
    assert result["candidateStatements"] == 4
    assert result["verifiedStatements"] == 2
    assert result["deduplicatedStatements"] == 1
    assert result["diagnosticsAvailable"] is True
    assert "recommendation" not in json.dumps(result)


def test_aggregate_metrics_count_reasons_and_not_covered():
    clinical = load_clinical_set(CLINICAL / "synthetic-example.json")
    hit = evaluate_case(clinical.cases[0], synthetic_response(include_forbidden=False),
                        status=200, duration_ms=10.0, top_k=8, threshold=0.8)
    missed = evaluate_case(clinical.cases[1], {
        "problems": [{"title": "Synthetic", "status": "not_found", "items": [], "unverified": [],
                      "sources": []}],
        "not_covered": ["Synthetic"],
        "diagnostics": [{"problemIndex": 1, "drafts": 0, "candidates": 0, "verified": 0,
                         "reasons": {}}],
    }, status=200, duration_ms=30.0, top_k=8, threshold=0.8)
    metrics = compute_metrics([hit, missed], top_k=8, threshold=0.8)
    assert metrics["totalCases"] == 2
    assert metrics["errorCases"] == 0
    assert metrics["hitAtK"] == 0.5
    assert metrics["verifiedRate"] == 0.5
    assert metrics["expectedStatementVerifiedRate"] == 0.5
    assert metrics["notCoveredRate"] == 0.5
    assert metrics["forbiddenStatementViolations"] == 0
    assert metrics["perReasonRejections"]["quote_not_found"] == 1
    assert metrics["perReasonRejections"]["deduplicated"] == 1
    assert metrics["unsupportedClaimRate"] == 0.25
    assert metrics["latencyMilliseconds"]["p50"] == 10.0
    assert metrics["latencyMilliseconds"]["p99"] == 30.0


def test_diagnostics_unavailable_is_explicit():
    case = synthetic_case()
    response = synthetic_response()
    response.pop("diagnostics")
    result = evaluate_case(case, response, status=200, duration_ms=1.0, top_k=8, threshold=0.8)
    assert result["diagnosticsAvailable"] is False
    assert result["diagnostics"] is None


def test_percentile_uses_nearest_rank_without_hiding_small_samples():
    assert percentile([], 0.95) == 0.0
    assert percentile([1.0], 0.99) == 1.0
    assert percentile([1.0, 2.0, 3.0, 4.0], 0.50) == 2.0
    assert percentile([1.0, 2.0, 3.0, 4.0], 0.95) == 4.0


def test_result_directories_are_new_even_with_the_same_timestamp(tmp_path):
    from datetime import datetime, timezone
    fixed = datetime(2026, 11, 16, 12, 0, tzinfo=timezone.utc)
    first = run_clinical_eval.create_run_dir(tmp_path, "clinical", fixed)
    second = run_clinical_eval.create_run_dir(tmp_path, "clinical", fixed)
    assert first != second
    assert first.name.endswith("clinical")
    assert second.name.endswith("clinical-01")


def test_clinical_runner_writes_a_blocked_report_without_live_credentials(tmp_path, monkeypatch):
    for name in ("EVAL_FIREBASE_TOKEN", "EVAL_PIPELINE_KEY", "EVAL_PIPELINE_URL",
                 "EVAL_BACKEND_URL", "EVAL_WORKSPACE_ID"):
        monkeypatch.delenv(name, raising=False)
    output = tmp_path / "results"
    code = run_clinical_eval.main([
        "--clinical-set", str(CLINICAL / "synthetic-example.json"),
        "--allow-synthetic",
        "--output-dir", str(output),
    ])
    assert code == 4
    run_dir = next(output.iterdir())
    report = (run_dir / "report.md").read_text(encoding="utf-8")
    payload = json.loads((run_dir / "run.json").read_text(encoding="utf-8"))
    assert "BLOCKED" in report
    assert "No clinical case text was sent" in report
    assert "cystatin monitoring" not in report.casefold()
    assert payload["status"] == "blocked"
    assert payload["clinicalEvidence"] is False
    assert payload["set"]["labelSource"] == "synthetic-fixture"
    assert payload["commands"]
    assert "EVAL_FIREBASE_TOKEN" in (run_dir / "commands.txt").read_text(encoding="utf-8")


def test_clinical_runner_completes_against_a_local_verified_service_without_writing_case_text(tmp_path, monkeypatch):
    workspace = "11111111-1111-1111-1111-111111111111"
    result = {
        "problems": [{
            "title": "Synthetic problem",
            "status": "ok",
            "items": [{
                "recommendation": "Synthetic expected statement",
                "quote": "Synthetic expected statement",
                "citation": {
                    "id": "S1", "label": "Synthetic source", "title": "synthetic-only.pdf",
                    "file_name": "synthetic-only.pdf", "section": "Synthetic", "page": "1",
                    "strength": "", "draft": False, "score": 0.9,
                },
            }],
            "unverified": [],
            "sources": [{
                "id": "S1", "label": "Synthetic source", "title": "synthetic-only.pdf",
                "file_name": "synthetic-only.pdf", "section": "Synthetic", "page": "1",
                "strength": "", "draft": False, "score": 0.9,
            }],
        }],
        "not_covered": [],
        "diagnostics": [{"problemIndex": 1, "drafts": 1, "candidates": 1,
                         "verified": 1, "reasons": {}}],
        "warnings": [],
        "disclaimer": "Decision support only.",
        "version": "synthetic",
    }

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_args):
            pass

        def _json(self, status, payload):
            body = json.dumps(payload).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self):
            if self.path == "/ready":
                return self._json(200, {"ready": True})
            if self.path == "/health":
                return self._json(200, {"alive": True})
            if self.path == "/api/operations/colab":
                return self._json(200, {"model": {
                    "ready": True, "model": "synthetic-model", "embeddingModel": "synthetic-encoder",
                    "embeddingDimensions": 768,
                    "gpu": {"available": True, "name": "synthetic-GPU", "device": "cuda",
                            "totalMemoryBytes": 1024, "usedMemoryBytes": 512},
                }})
            if self.path.endswith("/config/published"):
                return self._json(200, {"version": 1, "configJson": "{\"retrieval\":{\"topK\":8}}"})
            if self.path.endswith("/rules/approved-snapshot"):
                return self._json(200, {"rules": [], "drugCatalog": {}, "catalogRevision": 0})
            return self._json(404, {"error": "not found"})

        def do_POST(self):
            if self.path != "/analyze":
                return self._json(404, {"error": "not found"})
            length = int(self.headers.get("Content-Length", "0"))
            self.rfile.read(length)
            return self._json(200, result)

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        base = f"http://127.0.0.1:{server.server_address[1]}"
        monkeypatch.setenv("EVAL_FIREBASE_TOKEN", "synthetic-token")
        monkeypatch.setenv("EVAL_PIPELINE_KEY", "synthetic-pipeline-key")
        clinical_path = write_set(tmp_path, clinician_set())
        output = tmp_path / "results"
        code = run_clinical_eval.main([
            "--clinical-set", str(clinical_path),
            "--pipeline-url", base,
            "--backend-url", base,
            "--workspace-id", workspace,
            "--output-dir", str(output),
        ])
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=5)

    assert code == 0
    run_dir = next(output.iterdir())
    report = (run_dir / "report.md").read_text(encoding="utf-8")
    payload = json.loads((run_dir / "run.json").read_text(encoding="utf-8"))
    assert "COMPLETED" in report
    assert payload["status"] == "completed"
    assert payload["clinicalEvidence"] is True
    assert payload["metrics"]["hitAtK"] == 1.0
    assert payload["metrics"]["mrr"] == 1.0
    assert payload["metrics"]["verifiedRate"] == 1.0
    assert payload["metrics"]["expectedStatementVerifiedRate"] == 1.0
    assert payload["metrics"]["forbiddenStatementViolations"] == 0
    assert payload["metrics"]["perReasonRejections"]["quote_not_found"] == 0
    assert payload["configVersion"] == 1
    assert payload["set"]["caseCount"] == 1
    assert "synthetic question" not in report.casefold()
    assert "Synthetic expected statement" not in report
    assert "synthetic-token" not in report
    assert "synthetic-pipeline-key" not in report
    assert (run_dir / "per-case.csv").is_file()
    assert "synthetic-case" in (run_dir / "per-case.csv").read_text(encoding="utf-8")


def baseline_artifact():
    return json.loads((ROOT / "eval/results/synthetic-retrieval-2026-11-16.json").read_text(encoding="utf-8"))


def test_baseline_gate_passes_the_checked_in_artifact():
    baseline = baseline_artifact()
    tolerances = json.loads((ROOT / "eval/baselines/synthetic-retrieval.tolerances.json").read_text(encoding="utf-8"))
    report = compare_baseline.compare(baseline=baseline, current=baseline, tolerances=tolerances)
    assert report["passed"] is True


def test_baseline_gate_fails_a_metric_drop_and_fixture_change():
    baseline = baseline_artifact()
    current = json.loads(json.dumps(baseline))
    current["quality"]["mrrAt8"] -= 0.5
    current["fixtureSha256"] = "0" * 64
    tolerances = json.loads((ROOT / "eval/baselines/synthetic-retrieval.tolerances.json").read_text(encoding="utf-8"))
    report = compare_baseline.compare(baseline=baseline, current=current, tolerances=tolerances)
    assert report["passed"] is False
    failed = {check["metric"] for check in report["checks"] if not check["passed"]}
    assert "mrrAt8" in failed
    assert "fixtureSha256" in failed


def test_baseline_gate_report_never_overwrites(tmp_path):
    output = tmp_path / "gate.json"
    output.write_text("preserved", encoding="utf-8")
    assert compare_baseline.write_new  # keep the import explicit
    with pytest.raises(FileExistsError):
        compare_baseline.write_new(output, {"passed": True})
    assert output.read_text(encoding="utf-8") == "preserved"
