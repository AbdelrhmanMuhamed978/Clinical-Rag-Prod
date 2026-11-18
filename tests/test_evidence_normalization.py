import pipeline as P


def _source(text, strength="strong", markers=None):
    return {"S1": {"id": "S1", "text": text, "strength": strength, "draft": False,
                   "label": "Synthetic", "section": "Test", "page": "11",
                   "footnote_markers": markers or []}}


def test_quote_matching_decodes_entities_and_ignores_known_footnote_markers():
    markers = [{"marker": "c", "offset": 9, "context": "Screening for cryptococcal antigen"}]
    source = _source(
        "Screeningc for cryptococcal antigen followed by pre&amp;#45;emptive antifungal therapy "
        "among cryptococcal antigen–positive people is recommended before initiating ART. "
        "Use 100 cells/mm3 in the synthetic example. "
        "(Strong recommendation, moderate&amp;#45;certainty evidence)",
        markers=markers,
    )
    quote = (
        "Screening for cryptococcal antigen followed by pre-emptive antifungal therapy "
        "among cryptococcal antigen–positive people is recommended before initiating ART. "
        "Use 100 cells/mm³ in the synthetic example. "
        "(Strong recommendation; moderate-certainty evidence)"
    )
    issues = P.verify_item({"tag": "S1", "rec": quote, "quote": quote}, source)
    assert not issues, issues


def test_canonical_text_equates_superscript_units_and_ignores_dash_spacing():
    assert P.canonical_text("100 cells/mm³") == P.canonical_text("100 cells/mm3")
    assert P.canonical_text("antigen– positive") == P.canonical_text("antigen-positive")
    assert P._quote_in_source("not", "The synthetic patient cannot use it.") is False


def test_canonical_text_equates_a_hyphen_lost_at_a_pdf_line_wrap():
    fused = "A single highdose liposomal amphotericin B is the preferred induction regimen."
    wrapped = "A single high-\ndose liposomal amphotericin B is the preferred induction regimen."
    quoted = "A single high-dose liposomal amphotericin B is the preferred induction regimen."
    assert P.canonical_text(quoted) == P.canonical_text(fused)
    assert P.canonical_text(quoted) == P.canonical_text(wrapped)
    assert P._quote_in_source(quoted, fused)
    assert not P.scope_issues(quoted, quoted)
    # End-to-end verifier acceptance, including support-ratio and grade checks.
    source = _source(fused)
    issues = P.verify_item({"tag": "S1", "rec": quoted, "quote": quoted}, source)
    assert not issues, issues
    # A hyphen inside a number/unit or a range is not removed.
    assert P.canonical_text("24-hour infusion") != P.canonical_text("24 hour infusion")
    assert P.canonical_text("100-200 cells") != P.canonical_text("100200 cells")


def test_table_row_label_and_one_cell_verify_and_return_headers():
    source = _source(
        "[TABLES]\n|Intervention|CD4 cell count|\n|---|---|\n"
        "|Cryptococcal antigen screening|Recommended for both &lt;200 cells/mm3 and &lt;100 cells/mm3|\n[/TABLES]",
        strength="conditional",
    )
    quote = "Cryptococcal antigen screening: Recommended for both <200 cells/mm3 and <100 cells/mm3"
    rec = "Cryptococcal antigen screening is recommended at both CD4 thresholds."
    item = {"tag": "S1", "rec": rec, "quote": quote}
    issues = P.verify_item(item, source)
    assert not issues, issues
    assert item["table"] == {
        "headers": ["Intervention", "CD4 cell count"],
        "row": ["Cryptococcal antigen screening", "Recommended for both <200 cells/mm3 and <100 cells/mm3"],
    }


def test_paraphrased_table_row_is_rejected():
    source = _source(
        "[TABLES]\n|Intervention|CD4 cell count|\n|---|---|\n"
        "|Cryptococcal antigen screening|Recommended for both &lt;200 cells/mm3 and &lt;100 cells/mm3|\n[/TABLES]",
        strength="conditional",
    )
    quote = "Cryptococcal antigen screening is recommended for both <200 cells/mm3 and <100 cells/mm3"
    issues = P.verify_item({"tag": "S1", "rec": quote, "quote": quote}, source)
    assert any("quote not found" in issue for issue in issues), issues


def test_negation_flip_is_still_rejected():
    source = _source("Fluconazole prophylaxis should not be used for this synthetic population.")
    rec = "Fluconazole prophylaxis should be used for this synthetic population."
    quote = "Fluconazole prophylaxis should not be used for this synthetic population."
    codes = []
    issues = P.verify_item({"tag": "S1", "rec": rec, "quote": quote}, source, codes)
    assert issues and "negation_conflict" in codes, (issues, codes)


def test_altered_number_is_still_rejected():
    source = _source("Use a synthetic threshold of 100 cells/mm3.")
    rec = "Use a synthetic threshold of 200 cells/mm3."
    codes = []
    issues = P.verify_item({"tag": "S1", "rec": rec, "quote": rec}, source, codes)
    assert issues and ("number_missing" in codes or "quote_not_found" in codes), (issues, codes)


def test_changed_comparison_symbol_is_still_rejected():
    source = _source("Use the synthetic threshold of <100 cells/mm3.")
    quote = "Use the synthetic threshold of >100 cells/mm3."
    codes = []
    issues = P.verify_item({"tag": "S1", "rec": quote, "quote": quote}, source, codes)
    assert issues and "quote_not_found" in codes, (issues, codes)


def test_missing_marker_metadata_reports_formatting_mismatch_not_not_found():
    source = _source("Screeningc for synthetic review.", markers=[])
    quote = "Screening for synthetic review."
    codes = []
    issues = P.verify_item({"tag": "S1", "rec": quote, "quote": quote}, source, codes)
    assert issues and "formatting_mismatch" in codes, (issues, codes)


def test_verified_draft_includes_table_display_metadata():
    source = _source(
        "[TABLES]\n|Intervention|CD4 cell count|\n|---|---|\n"
        "|Synthetic screening|Recommended for both &lt;200 cells/mm3 and &lt;100 cells/mm3|\n[/TABLES]",
        strength="conditional",
    )
    out = "SOURCE: S1\nRECOMMENDATION: Synthetic screening is recommended at both CD4 thresholds.\n" \
          "QUOTE: Synthetic screening: Recommended for both <200 cells/mm3 and <100 cells/mm3"
    result = P.verify_draft(out, source, stems=[])
    assert result["items"] and result["items"][0]["table"]["headers"] == ["Intervention", "CD4 cell count"]


def test_source_metadata_is_cleaned_for_citations():
    source = P.sources_from_chunks([{
        "society": "Who",
        "title": "WHO guidelines on the management of advanced HIV disease",
        "version": "1",
        "year": 2026,
        "section": "TABLES|Treatment|Induction therapy",
        "strength": "none",
        "status": "final",
        "page": 11,
        "text": "Use the preferred regimen. (Strong recommendation, moderate-certainty evidence)",
    }])[0]
    assert source["label"] == "WHO guidelines on the management of advanced HIV disease v1 (2026, FINAL)"
    assert source["section"] == "Treatment > Induction therapy"
    assert source["strength"] == "strong"
    assert source["certainty"] == "moderate"


def test_structured_kind_and_population_are_carried_into_the_model_context():
    sources = P.sources_from_chunks([{
        "society": "WHO", "title": "Synthetic guideline", "year": 2026, "status": "final",
        "page": 11, "section": "Definitions", "kind": "definition", "population": "adults",
        "text": "Synthetic definition.",
    }])[0] | {"id": "S1"}
    context = P.context_text({"S1": sources})
    assert "kind:definition" in context
    assert "population:adults" in context


def test_citation_label_deduplicates_society_title_version_year_and_file_artifacts():
    assert P._display_label("WHO", "Synthetic guideline", "", "2026", "final") == \
        "WHO, Synthetic guideline (2026, FINAL)"
    source = P.sources_from_chunks([{
        "society": "WHO", "title": "WHO WHO guideline 2026", "version": "v2", "year": 2026,
        "status": "final", "page": 1, "section": "Treatment|Induction therapy",
        "text": "Synthetic narrative only.",
    }])[0]
    assert source["label"] == "WHO guideline v2 (2026, FINAL)"
    assert "  " not in source["label"]
    assert source["section"] == "Treatment > Induction therapy"

    fallback = P.sources_from_chunks([{
        "society": "WHO", "title": None, "file_name": "9789240118164-eng.pdf", "year": 2026,
        "status": "final", "page": 1, "section": "Synthetic|Table",
        "text": "Synthetic narrative only.",
    }])[0]
    assert "9789240118164" not in fallback["label"]
    assert fallback["label"] == "WHO, Untitled guideline (2026, FINAL)"


def test_narrative_does_not_inherit_llm_strength_from_another_paragraph():
    text = ("Cryptococcal disease is an important opportunistic infection in advanced HIV disease.\n\n"
            "[TABLES]\n|Recommendation|Grade|\n|---|---|\n"
            "|Use synthetic drug X|(Strong recommendation, high-certainty evidence)|\n[/TABLES]")
    source = P.sources_from_chunks([{
        "society": "Synthetic", "title": "Synthetic guideline", "year": 2026, "status": "final",
        "page": 40, "section": "Cryptococcal disease", "strength": "strong", "certainty": "high",
        "text": text,
    }])[0] | {"id": "S1"}
    citation = P._cite(source, "Cryptococcal disease is an important opportunistic infection in advanced HIV disease.")
    assert citation["strength"] == ""
    assert "certainty" not in citation

    graded = P.sources_from_chunks([{
        "society": "Synthetic", "title": "Synthetic guideline", "year": 2026, "status": "final",
        "page": 40, "section": "Cryptococcal disease", "strength": "strong", "certainty": "high",
        "text": "Use synthetic drug X. (Strong recommendation, high-certainty evidence)",
    }])[0] | {"id": "S1"}
    citation = P._cite(graded, "Use synthetic drug X.")
    assert citation["strength"] == "strong"
    assert citation["certainty"] == "high"


def test_table_grade_is_read_from_the_same_row_only():
    text = ("[TABLES]\n|Recommendation|Grade|\n|---|---|\n"
            "|Use drug X|(Strong recommendation, high-certainty evidence)|\n"
            "|Consider drug Y|(Conditional recommendation, low-certainty evidence)|\n[/TABLES]")
    source = P.sources_from_chunks([{
        "society": "Synthetic", "title": "Synthetic table guideline", "year": 2026, "status": "final",
        "page": 1, "section": "Treatment|Induction therapy", "text": text,
    }])[0] | {"id": "S1"}
    first = P._cite(source, "Use drug X: (Strong recommendation, high-certainty evidence)")
    second = P._cite(source, "Consider drug Y: (Conditional recommendation, low-certainty evidence)")
    assert (first["strength"], first["certainty"]) == ("strong", "high")
    assert (second["strength"], second["certainty"]) == ("conditional", "low")
