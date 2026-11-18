import asyncio

import config as Cfg
import pipeline as P


QUESTION = ("When cryptococcal antigen screening is not available, what fluconazole primary "
            "prophylaxis is recommended?")


def chunk(text, page=1, section="Cryptococcal disease"):
    return {"chunk_id": text[:12], "society": "Synthetic Society", "year": 2026, "status": "final",
            "section": section, "page": page, "strength": "strong", "text": text, "rerank_score": 0.9}


SCREENING = ("When cryptococcal antigen screening is not available, fluconazole primary prophylaxis "
             "should be given to adults and adolescents with advanced HIV disease.")
INDUCTION = "For cryptococcal meningitis, induction therapy with amphotericin B is recommended."


class ScenarioBackend:
    def __init__(self):
        self.prompts = []

    async def retrieve(self, query):
        return {"context": "unused", "chunks": [chunk(SCREENING, 1), chunk(INDUCTION, 2, "Induction therapy")]}

    async def generate(self, prompt, max_new_tokens=400):
        self.prompts.append(prompt)
        if "Rewrite the clinician's message" in prompt:
            return "cryptococcal antigen screening unavailable fluconazole prophylaxis"
        return (
            "SOURCE: S1\n"
            "RECOMMENDATION: Fluconazole primary prophylaxis should be given when cryptococcal antigen screening is not available.\n"
            f"QUOTE: {SCREENING}\n\n"
            "SOURCE: S2\n"
            "RECOMMENDATION: Amphotericin B induction therapy is recommended for cryptococcal meningitis.\n"
            f"QUOTE: {INDUCTION}"
        )

    async def rerank(self, query, passages):
        return [0.9 if "screening" in passage.casefold() else 0.8 for passage in passages]


def test_screening_question_is_question_and_induction_is_secondary():
    async def scenario():
        events = []

        async def emit(event):
            events.append(event)

        result = await P.analyze(QUESTION, ScenarioBackend(), emit, config=Cfg.Config.from_dict())
        return events, result

    events, result = asyncio.run(scenario())
    assert {event["mode"] for event in events} == {"question"}
    assert result["plan_source"] == "single_question"
    problem = result["problems"][0]
    assert problem["covers"] == QUESTION
    assert problem["items"], problem
    assert all("induction" not in item["recommendation"].casefold() for item in problem["items"])
    assert any("screening" in item["recommendation"].casefold() for item in problem["items"])
    assert problem["related"] and all("induction" in item["recommendation"].casefold() for item in problem["related"])
    assert result["coverage"] == [{"part": QUESTION, "status": "answered"}]


def test_broad_question_demotes_a_narrow_exception_item():
    narrow = {
        "recommendation": "In patients with tuberculosis, delay ART.",
        "quote": "In patients with tuberculosis, delay ART.",
    }
    general = {
        "recommendation": "Start ART in all adults with HIV.",
        "quote": "Start ART in all adults with HIV.",
    }
    problem = {"title": "ART timing", "status": "ok", "items": [narrow, general], "related": [],
               "unverified": [], "sources": []}

    class Backend:
        async def rerank(self, query, passages):
            return [0.9, 0.9]

    asyncio.run(P.apply_relevance([problem], "When should ART be started in adults with HIV?",
                                  Backend(), Cfg.Config.from_dict()))
    assert problem["items"][0]["recommendation"] == general["recommendation"]
    assert any(item["recommendation"] == narrow["recommendation"] for item in problem["items"] + problem["related"])


def test_hyde_adds_one_optional_query_only_when_enabled():
    class Backend:
        def __init__(self):
            self.retrieved = []
            self.generated = []

        async def generate(self, prompt, max_new_tokens=400):
            self.generated.append(prompt)
            if "hypothetical guideline excerpt" in prompt:
                return "Hypothetical guideline answer about synthetic monitoring."
            return "synthetic"

        async def retrieve(self, query):
            self.retrieved.append(query)
            return {"context": "unused", "chunks": []}

    problem = {"title": "Synthetic", "query": "synthetic query", "queries": ["synthetic query"]}
    enabled = Backend()
    asyncio.run(P.retrieve_problem(problem, enabled, 2, 8, 0.0,
                                   config=Cfg.Config.from_dict({"retrieval": {"hydeEnabled": True}})))
    assert any("Hypothetical guideline" in query for query in enabled.retrieved)

    disabled = Backend()
    asyncio.run(P.retrieve_problem(problem, disabled, 2, 8, 0.0, config=Cfg.Config.from_dict()))
    assert disabled.generated == []
    assert not any("Hypothetical guideline" in query for query in disabled.retrieved)


def test_failed_problem_is_analysis_incomplete_not_not_covered():
    question = "Which synthetic regimen is preferred?"
    failed = {"title": "Synthetic", "status": "error", "covers": question, "items": [], "related": []}
    assert P.coverage_report(question, [failed]) == []
    answered = {"title": "Synthetic", "status": "ok", "covers": question,
                "items": [{"recommendation": "Synthetic", "quote": "Synthetic"}], "related": []}
    assert P.coverage_report(question, [failed, answered]) == [{"part": question, "status": "answered"}]


def test_multi_part_question_plan_maps_each_stated_part():
    question = "When is screening recommended and what prophylaxis is used?"

    class Backend:
        async def generate(self, prompt, max_new_tokens=400):
            return "synthetic query"

    plan = asyncio.run(P.question_plan(question, Backend(), Cfg.Config.from_dict()))
    assert len(plan) == 2
    assert all(problem["covers"] in question for problem in plan)
    assert [problem["covers"] for problem in plan] == [
        "When is screening recommended", "what prophylaxis is used?"]
    report = P.coverage_report(question, [])
    assert [row["status"] for row in report] == ["not_covered", "not_covered"]


def test_question_mode_is_not_word_count_bound():
    long_question = " ".join(["synthetic"] * 60) + " — which guideline applies?"
    long_non_question = " ".join(["synthetic"] * 60)
    assert P.is_question(long_question)
    assert not P.is_question(long_non_question)
    assert not P.has_clinical_facts({"values": {"altered_mentation": False, "mentation_term": None},
                                     "derived": {}, "meds": []})
