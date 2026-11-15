"""Plan -> retrieve -> draft -> verify. Only planning and drafting use the LLM; every check is code."""
import asyncio, difflib, json, re, os, random, time
from datetime import datetime, timezone
from email.utils import parsedate_to_datetime
import httpx
import facts as F
import config as Cfg
from operations import Step, record_retry, record_step, request_id, timed

VERSION = "3.2"

DISCLAIMER = ("Decision support only. Not a substitute for clinical judgement. Medication flags come from a "
              "rule table compiled from public sources and are pending clinician review.")

# Default prompts live in config/default_prompts.json (one source of truth, shared with the .NET API).
DEFAULT_PROMPTS = Cfg._load_default_prompts()

PLAN_PROMPT = DEFAULT_PROMPTS.get("plan", "")
ANSWER_PROMPT = DEFAULT_PROMPTS.get("answer", "")
REWRITE_PROMPT = DEFAULT_PROMPTS.get("rewrite", "")

_GREET = set("hi hii hello hey hallo salam thanks thank ok okay test testing good how are r is anyone".split())
_SMALL = _GREET | set("you u here there working morning evening afternoon again all everyone claude much so a lot".split())


def is_smalltalk(text):
    w = re.findall(r"[a-z']+", (text or "").lower())
    return bool(w) and len(w) <= 8 and w[0] in _GREET and all(x in _SMALL for x in w)


class BackendError(Exception):
    pass


class ServiceUnavailable(BackendError):
    def __init__(self, service="colab", retry_after_seconds=30):
        self.service = service if service in {"colab", "pipeline"} else "pipeline"
        self.retry_after_seconds = max(1, min(300, retry_after_seconds))
        super().__init__("The model or verified pipeline service is temporarily unavailable.")


def _retry_after(response):
    try:
        value = response.headers.get("Retry-After", "30")
        seconds = float(value) if value.isdigit() else (parsedate_to_datetime(value) - datetime.now(timezone.utc)).total_seconds()
        return max(1, min(300, int(seconds + 0.999)))
    except (ValueError, TypeError, OverflowError):
        return 30


class Backend:
    """Talks to the ASP.NET backend (/retrieve) and Colab server (/generate)."""
    def __init__(self, backend_url, workspace_id, firebase_token=None, timeout=120.0,
                 generate_url=None, colab_key=None, retrieve_url=None, connection_resolver=None):
        self.backend_url = backend_url.rstrip("/")
        self.workspace_id = workspace_id
        self.retrieve_url = retrieve_url or f"{self.backend_url}/api/workspaces/{workspace_id}/analyze/retrieve"
        self.firebase_token = firebase_token
        self.timeout = timeout
        self.connection_resolver = connection_resolver
        self.generate_url = generate_url if connection_resolver else generate_url or os.getenv("COLAB_GENERATE_URL", "http://localhost:8000/generate")
        self.colab_key = colab_key if connection_resolver else colab_key or os.getenv("COLAB_API_KEY") or os.getenv("COLAB_KEY")

    def _headers(self, what, colab_key=None):
        h = {"Content-Type": "application/json", "X-Request-Id": request_id()}
        if what == "retrieval" and self.firebase_token:
            h["Authorization"] = f"Bearer {self.firebase_token}"
        key = colab_key if colab_key is not None else self.colab_key
        if what == "generation" and key:
            h["X-Colab-Key"] = key
        return h

    async def _post(self, url, payload, what, colab_key=None):
        # Only retrieval is replayable. Generation must never be submitted twice after a lost response.
        attempts = 2 if what == "retrieval" else 1
        for attempt in range(attempts):
            try:
                with Step("pipeline", "pipeline.http.retrieve" if what == "retrieval" else "pipeline.http.generate",
                          payload.get("query") if what == "retrieval" else payload.get("prompt")):
                    async with httpx.AsyncClient(timeout=httpx.Timeout(self.timeout, connect=min(5.0, self.timeout)),
                                                 follow_redirects=False, trust_env=False) as c:
                        r = await c.post(url, json=payload, headers=self._headers(what, colab_key))
                    r.raise_for_status()
                    result = r.json()
                    if not isinstance(result, dict):
                        raise ValueError("invalid backend response")
                    return result
            except httpx.HTTPStatusError as error:
                transient = error.response.status_code in {408, 429} or error.response.status_code >= 500
                if transient and attempt + 1 < attempts:
                    record_retry()
                    await asyncio.sleep(0.25 * (0.5 + random.random()))
                    continue
                if transient or what == "generation" and (error.response.status_code in {401, 403} or 300 <= error.response.status_code < 400):
                    service = "colab" if what == "generation" else "pipeline"
                    if what == "retrieval" and len(error.response.content) <= 16384:
                        try:
                            if error.response.json().get("dependency") == "colab":
                                service = "colab"
                        except (ValueError, TypeError, AttributeError):
                            pass
                    raise ServiceUnavailable(service, _retry_after(error.response)) from None
                raise BackendError(f"The {what} step refused the request.") from None
            except httpx.TransportError:
                if attempt + 1 < attempts:
                    record_retry()
                    await asyncio.sleep(0.25 * (0.5 + random.random()))
                    continue
                raise ServiceUnavailable("colab" if what == "generation" else "pipeline") from None
            except (httpx.HTTPError, ValueError):
                raise BackendError(f"The {what} step returned an invalid response.") from None

    async def prepare(self):
        if self.connection_resolver is None:
            return
        url, key = await self.connection_resolver()
        try:
            async with httpx.AsyncClient(timeout=2.0, follow_redirects=False, trust_env=False) as client:
                response = await client.get(url.removesuffix("generate") + "ready", headers={"X-Colab-Key": key, "X-Request-Id": request_id()})
                response.raise_for_status()
                body = response.json()
                if body.get("ready") is not True:
                    raise ValueError("Model not ready")
        except (httpx.HTTPError, ValueError, TypeError, AttributeError):
            raise ServiceUnavailable("colab") from None

    async def retrieve(self, query):
        d = await self._post(self.retrieve_url, {"query": query}, "retrieval")
        return {"context": d.get("context", "NOT_FOUND"), "chunks": d.get("chunks")}

    async def generate(self, prompt, max_new_tokens=400):
        # Generation goes directly to the secured Colab endpoint.
        url, key = await self.connection_resolver() if self.connection_resolver is not None else (self.generate_url, self.colab_key)
        d = await self._post(url, {"prompt": prompt, "max_new_tokens": max_new_tokens,
                                  "repetition_penalty": 1.0, "temperature": 0.0}, "generation", key)
        if d.get("truncated") or d.get("finish_reason") in {"length", "max_tokens"}:
            raise BackendError("Generation was truncated; no incomplete draft can be verified.")
        r = d.get("response") or ""
        if not isinstance(r, str):
            raise BackendError("The generation service returned an invalid response.")
        if r.startswith("COLAB SERVER ERROR"):
            raise BackendError("The Colab server reported an error during generation.")
        return r


# ---------- sources ----------
def _norm(s):
    s = s.lower()
    s = re.sub(r"[\u2010-\u2015]", "-", s)
    s = re.sub(r"[\"'\u2018\u2019\u201c\u201d`*]", "", s)
    s = re.sub(r"(?<=\d),(?=\d)", "", s)
    return re.sub(r"\s+", " ", s).strip(" .…")


def sources_from_text(context):
    """Parse the '[S1] label | section | p.N | strength' text format into source dicts (no ids yet)."""
    out = []
    for part in re.split(r"(?m)^(?=\[S\d+\] )", context or ""):
        m = re.match(r"\[S\d+\]\s*([^\n]*)\n?(.*)", part, re.S)
        if not m: continue
        segs = [s.strip() for s in m[1].split("|")]
        page, strength = "", ""
        for s in segs[2:]:
            if s.lower().startswith("p."): page = s[2:].strip()
            elif s: strength = s
        out.append({"label": segs[0], "section": segs[1] if len(segs) > 1 else "", "page": page,
                    "strength": strength, "draft": "DRAFT" in segs[0].upper(), "text": m[2].strip(), "score": None})
    return out


def sources_from_chunks(chunks):
    out = []
    for c in chunks:
        draft = str(c.get("status", "")).lower() != "final"
        label = f"{c.get('society', '')} {c.get('year', '')}".strip() + (" (DRAFT)" if draft else " (FINAL)")
        out.append({"label": label, "section": c.get("section") or "General", "page": str(c.get("page", "")),
                    "strength": c.get("strength") or "", "draft": draft, "text": (c.get("text") or "").strip(),
                    "score": c.get("rerank_score")})
    return out


def number_sources(srcs, max_n=8, min_score=0.0, keep_order=False):
    seen, uniq = set(), []
    for s in srcs:
        k = _norm(s["text"])[:200]
        if k and k not in seen:
            seen.add(k); uniq.append(s)
    if min_score:
        uniq = [s for s in uniq if s.get("score") is None or s["score"] >= min_score]
    if not keep_order and uniq and all(s.get("score") is not None for s in uniq):
        uniq.sort(key=lambda s: s["score"] - (0.15 if s["draft"] else 0.0), reverse=True)
    return {f"S{i}": s | {"id": f"S{i}"} for i, s in enumerate(uniq[:max_n], 1)}


def _interleave(lists):
    out = []
    for i in range(max((len(l) for l in lists), default=0)):
        out += [l[i] for l in lists if i < len(l)]
    return out


def context_text(sources):
    return "\n".join(f"[{s['id']}] {s['label']} | {s['section']} | p.{s['page']}" + (f" | {s['strength']}" if s["strength"] else "")
                     + f"\n{s['text']}\n" for s in sources.values())


def _cite(s):
    c = {k: s[k] for k in ("id", "label", "section", "page", "strength", "draft")}
    if s.get("score") is not None: c["score"] = round(float(s["score"]), 2)
    return c


# ---------- parsing and verification ----------
def parse_items(text):
    items = []
    for block in re.split(r"(?im)^\s*SOURCE\s*:\s*", text or "")[1:]:
        tag = re.match(r"\[?\s*S?(\d+)", block)
        rec = re.search(r"(?is)RECOMMENDATION\s*:\s*(.*?)(?:\n\s*QUOTE\s*:|\Z)", block)
        quote = re.search(r"(?is)QUOTE\s*:\s*(.*)", block)
        if tag and rec:
            items.append({"tag": f"S{tag[1]}", "rec": rec[1].strip(), "quote": (quote[1] if quote else "").strip().strip("\"“”")})
    return items


STOP = set("the and for with that this which should would could into over under than then also not can may are was were been being from have has had their there these those such only about between during after before".split())
SOFT = re.compile(r"\b(suggest\w*|weak|conditional|consider\w*|may|could)\b|\b2[A-D]\b", re.I)
STRONG = re.compile(r"\b(must|should|strongly|always|mandatory)\b", re.I)


def support_ratio(rec, text):
    words = [w for w in re.findall(r"[a-z]{4,}", rec.lower()) if w not in STOP]
    if not words: return 1.0
    stems = {w[:5] for w in re.findall(r"[a-z]{4,}", text.lower())}
    return sum(w[:5] in stems for w in words) / len(words)


QUALIFIERS = ["severe", "moderate", "mild", "children", "pediatric", "paediatric", "neonat", "infant", "surgical", "postoperative",
              "pregnan", "cirrho", "dialysis", "cancer", "elderly", "older"]
NEG = re.compile(r"\b(no recommendation|not recommend\w*|recommend\w* against|suggest\w* against|against|should not|do not|not be used|not routinely|avoid\w*)\b")
COND_SRC = re.compile(r"\b(unless|except|only if|only when|provided that|in the absence of|contraindication)\b")
COND_REC = re.compile(r"\b(unless|except|only|if|when|provided|absence|contraindication|without)\b")


def scope_issues(rec, quote):
    """The recommendation must not widen, narrow or flip what the quoted words say."""
    r, q, out = _norm(rec), _norm(quote), []
    for w in QUALIFIERS:
        if re.search(rf"\b{w}", r) and not re.search(rf"\b{w}", q): out.append(f"“{w}” is not in the quoted text")
    if bool(NEG.search(q)) != bool(NEG.search(r)): out.append("negation or no-recommendation wording differs from the quote")
    if COND_SRC.search(q) and not COND_REC.search(r): out.append("drops a condition stated in the quote (unless/except/only if)")
    return out


def verify_item(item, sources):
    src = sources.get(item["tag"])
    if not src: return ["cited source is not among the retrieved sources"]
    text, issues = _norm(src["text"]), []
    frags = [f for f in re.split(r"\.\.\.|…", item["quote"]) if _norm(f)]
    if not frags or any(_norm(f) not in text for f in frags): issues.append("quote not found in the cited source")
    missing = [n for n in re.findall(r"\d+(?:\.\d+)?", _norm(item["rec"]))
               if not re.search(rf"(?<![\d.]){re.escape(n)}(?!\d)", text)]
    if missing: issues.append("number(s) not in source: " + ", ".join(missing))
    if support_ratio(item["rec"], src["text"]) < 0.5: issues.append("recommendation wording is not supported by the source text")
    if STRONG.search(item["rec"]) and SOFT.search(src["strength"] + " " + item["quote"]):
        issues.append("wording is stronger than the source")
    issues += scope_issues(item["rec"], item["quote"])
    return issues


def _best_span(quote, text):
    qn, words = _norm(quote), text.split()
    n, best = len(quote.split()), (0.0, "")
    sm = difflib.SequenceMatcher(None, autojunk=False); sm.set_seq2(qn)
    for size in range(max(3, n - 2), n + 3):
        for i in range(0, max(0, len(words) - size) + 1):
            span = " ".join(words[i:i + size]); sm.set_seq1(_norm(span))
            if sm.real_quick_ratio() > best[0] and sm.quick_ratio() > best[0]:
                r = sm.ratio()
                if r > best[0]: best = (r, span)
    return best


def repair_item(item, sources, threshold=0.88):
    q = item["quote"]
    if not q.strip() or re.search(r"\.\.\.|…", q): return item
    src = sources.get(item["tag"])
    if src and _norm(q) in _norm(src["text"]): return item
    for s_ in sources.values():
        if _norm(q) in _norm(s_["text"]): return item | {"tag": s_["id"], "repaired": "citation corrected"}
    best = max((( _best_span(q, s_["text"]), s_) for s_ in ([src] if src else sources.values())), key=lambda x: x[0][0], default=None)
    if best and best[0][0] >= threshold and best[0][1]:
        return item | {"tag": best[1]["id"], "quote": best[0][1], "repaired": "quote corrected to the source text"}
    return item


# ---------- planning ----------
FAMILIES = {"shock": r"shock|hypotens|vasopress|\bmap\b", "pneumonia": r"pneumon", "ards": r"\bards\b|ventilat|hypox|respiratory (?:failure|distress)",
            "antiplatelet": r"antiplatelet|antithrombotic|anticoag|stent|\bdapt\b|clopidogrel",
            "platelets": r"thrombocytopen|platelet", "aki": r"kidney|\baki\b|renal", "anemia": r"an(a)?emia|transfusion",
            "electrolytes": r"hypokal|potassium|magnesium",
            "antimicrobial": r"antimicrob|antibiot|infection", "vte": r"thromboembol|\bvte\b|prophylaxis"}
EVIDENCE = {
    "shock": lambda i, t: "hypotension" in i or "shock" in t,
    "pneumonia": lambda i, t: bool(re.search(r"pneumon|consolidation|infiltrat", t)),
    "ards": lambda i, t: "hypoxemia" in i or bool(re.search(r"\bards\b|hypox|desaturat", t)),
    "platelets": lambda i, t: any(x.startswith("thrombocytopenia") for x in i) or "platelet" in t,
    "aki": lambda i, t: "aki_possible" in i or bool(re.search(r"kidney|\baki\b|renal|creatinine", t)),
    "anemia": lambda i, t: "anemia" in i,
    "electrolytes": lambda i, t: "hypokalemia" in i or "magnesium_low" in i or bool(re.search(r"potassium|hypokal|magnes", t)),
    "antiplatelet": lambda i, t: bool(re.search(r"stent|antiplatelet|clopidogrel|plavix|dapt|ticagrelor|aspirin|anticoag|warfarin", t)),
    "antimicrobial": lambda i, t: "fever" in i or "leukocytosis" in i or bool(re.search(r"pneumon|infect|sepsis|septic|fever|antibiot", t)),
    "vte": lambda i, t: "hypotension" in i or bool(re.search(r"shock|sepsis|septic", t)),
}
GENERIC = {"elder", "patie", "criti", "adult", "manag", "treat", "thera", "acute", "sever", "chron", "syndr", "disea", "injur"}


def _family(s):
    return next((k for k, p in FAMILIES.items() if re.search(p, s.lower())), None)


def _primary(title):
    return re.split(r"\b(?:in|with|due to|and|of|during)\b", title, maxsplit=1, flags=re.I)[0]


def _stems(s): return {w[:5] for w in re.findall(r"[a-z]{4,}", s.lower())}


def _grounded(p, text, ids):
    prim = _primary(p["title"]); fam = _family(prim)
    if fam: return bool(EVIDENCE[fam](ids, text.lower()))
    return bool((_stems(prim) - GENERIC) & _stems(text))


def rule_problems(text, flags):
    ids, t, P = {f["id"] for f in flags}, text.lower(), []
    low_plt = any(i.startswith("thrombocytopenia") for i in ids)
    infection = bool(re.search(r"pneumon|consolidation|infiltrat|sepsis|septic|infection|fever", t)) or "fever" in ids or "leukocytosis" in ids
    def add(title, queries, max_items=3, max_chunks=None):
        P.append({"title": title, "query": queries[0], "queries": queries, "max_items": max_items, "max_chunks": max_chunks})
    if "hypotension" in ids or "shock" in t:
        add("Septic shock and hypotension", [
            "vasopressor first-line agent and MAP target in septic shock in adults",
            "initial fluid resuscitation crystalloid in sepsis-induced hypotension",
            "corticosteroids hydrocortisone for septic shock with ongoing vasopressor requirement",
            "lactate-guided resuscitation and capillary refill in sepsis",
            "blood transfusion threshold in sepsis"], max_items=5, max_chunks=10)
    if infection:
        add("Antimicrobial therapy in sepsis", [
            "timing of antimicrobial administration in sepsis and septic shock",
            "antimicrobial dose optimisation and renal impairment in sepsis",
            "empiric antibiotic choice sepsis procalcitonin duration of therapy"], max_items=4, max_chunks=8)
    if re.search(r"pneumonia|consolidation|infiltrat", t):
        add("Pneumonia", ["antibiotic treatment of severe community-acquired pneumonia in ICU adults",
                          "severe community-acquired pneumonia corticosteroids and diagnostic testing"])
    if re.search(r"\bards\b|hypox|desaturat", t) or "hypoxemia" in ids:
        add("ARDS and hypoxaemic respiratory failure", [
            "lung-protective mechanical ventilation tidal volume plateau pressure ARDS",
            "prone positioning neuromuscular blockade corticosteroids severe ARDS",
            "high-flow nasal oxygen versus non-invasive ventilation acute hypoxaemic respiratory failure"], max_items=4, max_chunks=8)
    if low_plt:
        add("Thrombocytopenia", ["platelet transfusion threshold prophylactic and before invasive procedures in critically ill adults",
                                 "thrombocytopenia in sepsis disseminated intravascular coagulation bleeding risk"])
    if "aki_possible" in ids:
        add("Acute kidney injury in sepsis", ["sepsis-associated acute kidney injury fluids vasopressors nephrotoxin avoidance",
                                              "timing of renal replacement therapy in acute kidney injury in critically ill adults"], max_items=4)
    if "anemia" in ids: add("Anaemia", ["red blood cell transfusion threshold in critically ill adults with ischemic heart disease"])
    if "hypokalemia" in ids or "magnesium_low" in ids:
        add("Hypokalaemia and low magnesium", ["potassium and magnesium replacement for hypokalemia in cardiac patients"])
    if low_plt and re.search(r"stent|antiplatelet|plavix|clopidogrel|dapt", t):
        add("Antiplatelet therapy with a recent stent", ["dual antiplatelet therapy after stent bleeding risk thrombocytopenia"])
    if "hypotension" in ids or "shock" in t or "sepsis" in t:
        add("VTE prophylaxis in sepsis", ["pharmacologic venous thromboembolism prophylaxis in sepsis and contraindications",
                                          "mechanical thromboprophylaxis when anticoagulants are contraindicated"], max_items=3)
    return P


def fallback_plan(text, flags):
    return rule_problems(text, flags) or [{"title": "Case question", "query": text[:300]}]


async def make_plan(text, flags, backend, n, config):
    ids, llm = {f["id"] for f in flags}, []
    try:
        _plan_prompt = config.prompt("plan", PLAN_PROMPT)
        raw = await backend.generate(Cfg.render_prompt("plan", _plan_prompt, n=n, case=text), 500)
        data = json.loads(raw[raw.index("{"): raw.rindex("}") + 1])
        llm = [{"title": str(p["title"])[:80], "query": str(p["query"])[:300]} for p in data["problems"] if p.get("title") and p.get("query")]
    except BackendError:
        raise
    except Exception:
        pass
    rules = rule_problems(text, flags)
    rfam = {_family(_primary(p["title"])) for p in rules} - {None}
    ok, seen = [], set()
    for p in llm:
        if not _grounded(p, text, ids): continue
        fam = _family(_primary(p["title"]))
        if fam and (fam in seen or fam in rfam): continue
        if fam: seen.add(fam)
        ok.append(p)
    ok.sort(key=lambda p: _family(_primary(p["title"])) is None)
    plan = (rules + ok)[:max(n, len(rules))]
    return plan or fallback_plan(text, flags), ("llm+rules" if llm else "rules")


# ---------- one problem ----------
async def retrieve_problem(p, backend, n_queries, max_chunks, min_score):
    queries = list(p.get("queries") or [p["query"], f"{p['title']} recommendation"][:n_queries])
    rets = await asyncio.gather(*[backend.retrieve(q) for q in queries])
    lists, errors = [], 0
    for r in rets:
        ctx = r["context"]
        if ctx.startswith("ERROR"): errors += 1; continue
        lists.append(sources_from_chunks(r["chunks"]) if r.get("chunks") else sources_from_text(ctx))
    multi = len(queries) > 2
    srcs = number_sources(_interleave(lists), p.get("max_chunks") or max_chunks, min_score, keep_order=multi)
    return {"queries": queries, "sources": srcs, "errored": errors == len(rets)}


SYN = {"septi": ["sepsis", "septic"], "kidne": ["aki", "renal"], "throm": ["platelet"], "ards": ["respiratory distress"],
       "anaem": ["anemia", "transfusion", "hemoglobin"], "anemi": ["anaemia", "transfusion", "hemoglobin"],
       "antib": ["antimicrobial", "antibiotic"], "antim": ["antibiotic"], "venou": ["vte"], "pneum": ["cap"]}
TOPIC_STOP = STOP | GENERIC | set("guide lines guid guidelines recommend recommendation question tell what about management therapy".split())


def topic_stems(title, whole=False):
    s = title if whole else _primary(title)
    return {w[:5] for w in re.findall(r"[a-z]{4,}", s.lower()) if w not in TOPIC_STOP and w[:5] not in TOPIC_STOP}


def on_topic(stems, text):
    if not stems: return True
    t = text.lower()
    for s in stems:
        if s in t or any(a in t for a in SYN.get(s, [])): return True
    return False


async def answer_problem(p, ret, backend, patient, config):
    sources = ret["sources"]
    res = {"title": p["title"], "queries": ret["queries"], "status": "not_found", "items": [], "unverified": [],
           "sources": [_cite(s) | {"excerpt": s["text"][:400], "score": s["score"]} for s in sources.values()]}
    if ret["errored"]: return res | {"status": "error", "detail": "Retrieval returned an error."}
    if not sources: return res
    k = p.get("max_items", 3)
    _ans_prompt = config.prompt("answer", ANSWER_PROMPT)
    out = await backend.generate(Cfg.render_prompt("answer", _ans_prompt, problem=p.get("resolved_question", p["title"]), patient=patient, k=k, context=context_text(sources)), 450 + 60 * k)
    stems = p.get("topic") if "topic" in p else topic_stems(p["title"])
    items = parse_items(out)
    for it in items:
        it = repair_item(it, sources, threshold=config.get("safety.minQuoteMatch", 0.88))
        issues = verify_item(it, sources)
        src_ = sources.get(it["tag"])
        if src_ and not on_topic(stems, src_["text"] + " " + src_["section"]): issues.append("source does not mention the question topic")
        entry = {"recommendation": it["rec"], "quote": it["quote"]} | ({"repaired": it["repaired"]} if it.get("repaired") else {})
        if it["tag"] in sources: entry["citation"] = _cite(sources[it["tag"]])
        (res["unverified"] if issues else res["items"]).append(entry | ({"issues": issues} if issues else {}))
    res["status"] = "ok" if res["items"] else ("unverified" if items else "not_found")
    return res


# ---------- whole request ----------
def reconcile(problems, fx):
    if not fx: return
    flags = [f for f in fx["med_flags"] + fx["case_flags"] if f.get("link_terms")]
    for pr in problems:
        for it in pr["items"]:
            hay = f"{it['recommendation']} {it['quote']}"
            notes = [{"id": f["id"], "title": f["title"], "level": f["level"], "reasons": f["reasons"]}
                     for f in flags if F.test_link_terms(f["link_terms"], hay[:10000])["matched"]]
            if notes: it["patient_notes"] = notes


def clean_question(text):
    t = re.sub(r"(?i)\b(guide\s?lines?|guidelines?|recommendations?\s+for|tell me about|please)\b", " ", text)
    return re.sub(r"\s+", " ", t).strip(" ?.,") or text.strip()


def _history_for_rewrite(history):
    turns = (history or [])[-6:]
    if not turns:
        return ""
    lines = []
    for turn in turns:
        question = str(turn.get("question", ""))[:500]
        summary = str(turn.get("verifiedSummary", turn.get("verified_summary", "")))[:1000]
        if question and summary:
            lines.append(f"Previous question: {question}\nVerified summary: {summary}")
    if not lines:
        return ""
    return (
        "Verified conversation context (use only to resolve references; this is not guideline evidence):\n"
        + "\n".join(lines)
        + "\nCurrent question: "
    )


async def question_plan(text, backend, config, history=None):
    cleaned = clean_question(text)
    q = cleaned
    _rw_prompt = config.prompt("rewrite", REWRITE_PROMPT)
    try:
        rewrite_input = _history_for_rewrite(history) + text[:500]
        r = (await backend.generate(Cfg.render_prompt("rewrite", _rw_prompt, q=rewrite_input), 60)).strip().splitlines()[0].strip(" \"'")
        if 2 <= len(r.split()) <= 24 and ":" not in r and "NOT_FOUND" not in r: q = r
    except BackendError: raise
    except Exception: pass
    queries = [q] + ([cleaned] if _norm(cleaned) != _norm(q) else [f"{q} recommendations"])
    queries = queries[:config.get("retrieval.queriesPerProblem", 2)]
    return [{"title": text.strip()[:80], "resolved_question": q, "query": q, "queries": queries,
             "topic": topic_stems(q, whole=True), "max_items": 4}]


@timed("pipeline", "pipeline.analyze", input_argument=0)
async def analyze(text, backend, emit, config=None, rules=None, history=None, drug_catalog=None,
                  max_problems=8, queries_per_problem=2, max_chunks=8, min_score=0.30):
    # Always create a request-local default; never share a mutable config object.
    config = config or Cfg.Config.from_dict()
    max_problems = config.get("retrieval.topK", max_problems)
    queries_per_problem = config.get("retrieval.queriesPerProblem", queries_per_problem)
    max_chunks = config.get("retrieval.maxChunks", max_chunks)
    min_score = config.get("retrieval.minRerank", min_score)

    smalltalk = is_smalltalk(text)
    case_mode = len(text.split()) >= 40
    mode = "chat" if smalltalk else "case" if case_mode else "question"
    step_starts = {}

    async def progress(stage, state, message, step_id=None):
        # These events describe work actually reached. In particular, "completed"
        # means the stage ran, not that a generated recommendation was verified.
        identity = step_id or stage
        if state == "started":
            step_starts[identity] = time.perf_counter()
        elif identity in step_starts:
            record_step("pipeline", "pipeline." + stage, "success" if state == "completed" else "error",
                        (time.perf_counter() - step_starts.pop(identity)) * 1000)
        await emit({"message": message, "stage": stage, "state": state,
                    "stepId": step_id or stage, "mode": mode})

    if smalltalk:
        await progress("routing", "completed", "Greeting only; no guideline analysis performed")
        return {"plan_source": "chat", "chat": "I'm here. Ask a guideline question (for example \"vasopressors in septic shock\") "
                "or paste a full case with vitals, labs and medications.", "problems": [], "not_covered": [],
                "warnings": [], "disclaimer": DISCLAIMER, "version": VERSION}
    fx = None
    if case_mode:
        await progress("facts", "started", "Reading values and medications")
        try:
            fx = F.analyze_facts(text, rules=rules, drug_catalog=drug_catalog)
        except Exception:
            await progress("facts", "failed", "Reading values and medications failed")
            raise
        await progress("facts", "completed", "Values and medications read")
    flags = fx["flags"] if fx else []
    patient = fx["summary"] if fx else "not given"
    await progress("planning", "started", "Planning the guideline questions" if case_mode else "Preparing the search")
    try:
        if case_mode:
            plan, plan_source = await make_plan(text, flags, backend, max_problems, config)
        else:
            plan, plan_source = await question_plan(text, backend, config, history=history), "single_question"
        if not plan:
            raise ValueError("No guideline questions planned.")
    except Exception:
        await progress("planning", "failed", "Guideline question planning failed")
        raise
    await progress("planning", "completed", f"Prepared {len(plan)} guideline question(s)")

    async def tracked_retrieval(index, problem):
        step_id = f"retrieval-{index}"
        await progress("retrieval", "started", f"Retrieving guideline passages {index} of {len(plan)}", step_id)
        try:
            result = await retrieve_problem(problem, backend, queries_per_problem, max_chunks, min_score)
        except Exception:
            await progress("retrieval", "failed", f"Guideline retrieval {index} failed", step_id)
            raise
        await progress("retrieval", "failed" if result["errored"] else "completed",
                       f"Guideline retrieval {index} failed" if result["errored"] else
                       f"Retrieved {len(result['sources'])} passage(s) for question {index}", step_id)
        return result

    rets = await asyncio.gather(*[tracked_retrieval(i, p) for i, p in enumerate(plan, 1)], return_exceptions=True)
    failures = [r for r in rets if isinstance(r, Exception)]
    unavailable = next((failure for failure in failures if isinstance(failure, ServiceUnavailable)), None)
    if unavailable is not None: raise unavailable
    if len(failures) == len(rets): raise failures[0]
    problems = []
    for i, (p, ret) in enumerate(zip(plan, rets), 1):
        if isinstance(ret, Exception):
            problems.append({"title": p["title"], "queries": [], "status": "error", "detail": "Guideline retrieval failed.", "items": [], "unverified": [], "sources": []})
            continue
        if ret["errored"] or not ret["sources"]:
            # No generation/verification ran when retrieval yielded no evidence.
            problems.append(await answer_problem(p, ret, backend, patient, config))
            continue
        step_id = f"verification-{i}"
        await progress("verification", "started", f"Drafting and checking answer {i} of {len(plan)}", step_id)
        try:
            problem = await answer_problem(p, ret, backend, patient, config)
            problems.append(problem)
            await progress("verification", "completed", f"Answer {i} checked: {len(problem['items'])} verified, {len(problem['unverified'])} excluded", step_id)
        except BackendError as error:
            await progress("verification", "failed", f"Answer {i} generation or checking failed", step_id)
            if i == 1 or isinstance(error, ServiceUnavailable): raise
            problems.append({"title": p["title"], "queries": ret["queries"], "status": "error", "detail": "Answer generation failed; no recommendation was verified.", "items": [], "unverified": [], "sources": []})
        except Exception:
            await progress("verification", "failed", f"Answer {i} generation or checking failed", step_id)
            raise
    if fx:
        await progress("patient_checks", "started", "Checking verified items against patient rule flags")
        reconcile(problems, fx)
        await progress("patient_checks", "completed", "Patient rule checks completed; these are not guideline evidence")
    warnings = []
    if any(it.get("citation", {}).get("draft") for pr in problems for it in pr["items"]):
        warnings.append("Some citations come from DRAFT guidelines (marked DRAFT). Do not treat them as final.")
    if any(it.get("patient_notes") for pr in problems for it in pr["items"]):
        warnings.append("Some guideline statements are marked “Check against this patient”: a flag from the case data applies to them. Read the flag before using the statement.")
    if any(pr["status"] in {"not_found", "unverified"} for pr in problems):
        warnings.append("“Not covered” means the loaded guidelines gave no verifiable answer. It does not mean the topic is unimportant or safe.")
    if any(pr["status"] == "error" for pr in problems):
        warnings.append("Some problems could not be analyzed. Retry them; their guideline coverage is unknown.")
    if fx:
        warnings.append(fx["assumptions"][0])
        if fx["values"].get("mentation_term"):
            warnings.append(f"“{fx['values']['mentation_term']}” was read as altered consciousness (used for qSOFA).")
        if any(m["confidence"] == "fuzzy" for m in fx["meds"]):
            warnings.append("A drug name was matched approximately (marked “verify”). Confirm the actual drug.")
        warnings.extend(fx["warnings"])
    return {"plan_source": plan_source, "problems": problems,
            "not_covered": [p["title"] for p in problems if p["status"] in {"not_found", "unverified"}],
            "warnings": warnings, "disclaimer": DISCLAIMER, "version": VERSION}
