import asyncio, sys, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).parent.parent))
import pipeline as P, facts as F, config as Cfg

CASE = ("a 86 years old female patient admitted to the ER, hypotensive blood pressure 80/50, feverish 38.5 degree, dyspnea, cough, "
        "desaturated on room air ER vitals: temperature: 38.5, blood pressure: 80/50, spo2: 85 %, HR: 110, RR: 30 "
        "past medication history: on concor 2.5 mg, Targ 80, aspirin 75 mg once daily, plavix 75 mg once daily "
        "CT chest: consolidation right lower lobe pneumonia. labs: Hb: 9.5, TLC: 22,000, platelet: 25,000, srcr: 2.5, k: 3, mg: 1.6, albumin: 2.5")


class Fake:
    def __init__(self, chunks, answer="NOT_FOUND"):
        self.chunks, self.answer, self.prompts = chunks, answer, []

    async def retrieve(self, q):
        return {"context": "x", "chunks": self.chunks}

    async def generate(self, prompt, n=400):
        self.prompts.append(prompt)
        return self.answer


def chunk(text, score=0.9, status="final", society="SCCM", year="2026"):
    return {"chunk_id": text[:10], "society": society, "year": year, "status": status, "section": "S", "page": "1", "text": text, "rerank_score": score}


async def _noop_emit(msg):
    """Async no-op emit callback (not a sync coroutine-runner)."""
    return None


def run(c):
    """One top-level asyncio.run for the whole test."""
    return asyncio.run(c)


def test_smalltalk():
    assert P.is_smalltalk("hello r u here ?")
    assert P.is_smalltalk("hi")
    assert not P.is_smalltalk("hi, what vasopressor in septic shock")


def test_draft_penalised():
    srcs = [{"text": "a draft", "score": 0.80, "draft": True, "label": "", "section": "", "page": "", "strength": ""},
            {"text": "a final", "score": 0.72, "draft": False, "label": "", "section": "", "page": "", "strength": ""}]
    assert P.number_sources(srcs)["S1"]["text"] == "a final"


def test_plan_is_multi_aspect_and_covers_case():
    fx = F.analyze_facts(CASE)
    plan, _ = run(P.make_plan(CASE, fx["flags"], Fake([], "{}"), 8, Cfg.Config.from_dict()))
    titles = [p["title"] for p in plan]
    assert "Septic shock and hypotension" in titles


def test_good_item_verified_end_to_end():
    text = "In adults with septic shock, we recommend using norepinephrine as the first-line agent over dopamine."
    ans = ("SOURCE: S1\n"
           "RECOMMENDATION: Norepinephrine is recommended as first-line agent in adults with septic shock.\n"
           "QUOTE: we recommend using norepinephrine as the first-line agent")
    out = run(P.analyze("vasopressor in septic shock", Fake([chunk(text)], ans), _noop_emit))
    assert len(out["problems"][0]["items"]) == 1


def test_not_found_when_sources_silent():
    out = run(P.analyze("vasopressor in septic shock", Fake([], "NOT_FOUND"), _noop_emit))
    pr = out["problems"][0]
    assert pr["status"] == "not_found"
    assert pr["title"] in out["not_covered"]


# ---------- verify_item tests ----------

def _src(text, tag="S1", score=0.9, strength="strong", draft=False):
    return {tag: {"id": tag, "text": text, "score": score, "strength": strength, "draft": draft,
            "label": tag, "section": "", "page": ""}}


def test_verify_item_quote_not_found():
    srcs = _src("we recommend drug X for condition Y")
    item = {"tag": "S1", "rec": "we recommend drug X", "quote": "completely different words not in source"}
    issues = P.verify_item(item, srcs)
    assert any("quote not found" in i for i in issues)


def test_verify_item_number_not_in_source():
    srcs = _src("the dose is 5 mg daily")
    item = {"tag": "S1", "rec": "give 10 mg twice daily", "quote": "the dose is 5 mg daily"}
    issues = P.verify_item(item, srcs)
    assert any("number(s) not in source" in i for i in issues)


def test_verify_item_stronger_than_source():
    srcs = _src("we suggest drug X for condition Y", strength="conditional")
    item = {"tag": "S1", "rec": "we MUST give drug X for condition Y", "quote": "we suggest drug X for condition Y"}
    issues = P.verify_item(item, srcs)
    assert any("stronger than the source" in i for i in issues)


def test_verify_item_negation_flip():
    srcs = _src("we do not recommend drug X", strength="weak")
    item = {"tag": "S1", "rec": "we recommend drug X", "quote": "we do not recommend drug X"}
    issues = P.verify_item(item, srcs)
    assert any("negation" in i.lower() for i in issues)


def test_verify_item_dropped_condition():
    srcs = _src("use drug X only if creatinine > 2", strength="conditional")
    item = {"tag": "S1", "rec": "use drug X for all patients", "quote": "use drug X only if creatinine > 2"}
    issues = P.verify_item(item, srcs)
    assert any("drops a condition" in i for i in issues)


def test_verify_item_clean_pass():
    srcs = _src("we recommend drug X for condition Y in adults", strength="strong")
    item = {"tag": "S1", "rec": "we recommend drug X for condition Y in adults", "quote": "we recommend drug X for condition Y"}
    issues = P.verify_item(item, srcs)
    assert issues == [], issues


# ---------- draft penalty ordering ----------

def test_draft_never_outranks_final():
    srcs = [{"text": "a draft recommendation", "score": 0.60, "draft": True, "label": "", "section": "", "page": "", "strength": ""},
            {"text": "a final recommendation", "score": 0.50, "draft": False, "label": "", "section": "", "page": "", "strength": ""}]
    numbered = P.number_sources(srcs)
    assert numbered["S1"]["text"] == "a final recommendation"


def test_config_get_returns_cur_not_default():
    config = Cfg.load_from_dict({"retrieval": {"topK": 5}})
    assert config.get("retrieval.topK") == 5
    assert config.get("retrieval.minRerank") == 0.30
    assert config.get("missing.key", "fallback") == "fallback"


def test_validate_prompt_missing_placeholder():
    err = Cfg.validate_prompt("plan", "Read the case: hello")
    assert err is not None and "case" in err and "n" in err


def test_validate_prompt_valid():
    assert Cfg.validate_prompt("plan", "Read the case: {case} (at most {n})") is None
    assert Cfg.validate_prompt("answer", "Problem: {problem}\nPatient: {patient}\n{k}\n{context}") is None
    assert Cfg.validate_prompt("rewrite", "Message: {q}") is None
