"""Plan -> retrieve -> draft -> verify. Only planning and drafting use the LLM; every check is code."""
import asyncio, json, math, re, os, random, time, unicodedata
from evidence_text import canonical_text, decode_entities, formatting_view, normalize_evidence_text
from datetime import datetime, timezone
from email.utils import parsedate_to_datetime
import httpx
import facts as F
import config as Cfg
from operations import Step, record_retry, record_step, request_id, timed, REJECTION_REASONS, record_answer_diagnostics

VERSION = "3.2"

DISCLAIMER = ("Decision support only. Not a substitute for clinical judgement. Medication flags come from a "
              "rule table compiled from public sources and are pending clinician review.")

# Default prompts live in config/default_prompts.json (one source of truth, shared with the .NET API).
DEFAULT_PROMPTS = Cfg._load_default_prompts()

PLAN_PROMPT = DEFAULT_PROMPTS.get("plan", "")
ANSWER_PROMPT = DEFAULT_PROMPTS.get("answer", "")
REWRITE_PROMPT = DEFAULT_PROMPTS.get("rewrite", "")
HYDE_PROMPT = (
    "Write a short hypothetical guideline excerpt (at most 120 words) that would answer the "
    "question. Use generic clinical wording; do not invent a patient, a dose or a "
    "recommendation that the question does not imply. Question: {question}"
)

_GREET = set("hi hii hello hey hallo salam thanks thank ok okay test testing good how are r is anyone".split())
_SMALL = _GREET | set("you u here there working morning evening afternoon again all everyone claude much so a lot".split())


def is_smalltalk(text):
    w = re.findall(r"[a-z']+", (text or "").lower())
    return bool(w) and len(w) <= 8 and w[0] in _GREET and all(x in _SMALL for x in w)


_QUESTION_WORD = (r"(?:what|when|where|which|who|whom|whose|why|how|is|are|was|were|do|does|did|"
                  r"can|could|should|would|will|may|might|must|has|have|had)")
_QUESTION_START = re.compile(rf"^\s*{_QUESTION_WORD}\b", re.I)
_QUESTION_SPLIT = re.compile(rf"(?<=[?])\s+|;\s+|\s+(?:and|or)\s+(?={_QUESTION_WORD}\b)", re.I)


def is_question(text):
    """Interrogative text wins regardless of length or extracted values."""
    value = (text or "").strip()
    return bool(value) and ("?" in value or bool(_QUESTION_START.match(value)))


def question_parts(text):
    """Split a question into stated parts without inventing or rewriting them."""
    value = (text or "").strip()
    if not value:
        return []
    parts = [part.strip() for part in _QUESTION_SPLIT.split(value) if part.strip()]
    parts = [part for part in parts if re.search(r"[A-Za-z0-9]", part)]
    return parts or [value]


def has_clinical_facts(fx):
    """True when extraction found vitals/labs/age/sex or drug facts."""
    if not fx:
        return False
    values = {key: value for key, value in (fx.get("values") or {}).items()
              if value not in (None, False) and key != "mentation_term"}
    return bool(values or fx.get("derived") or fx.get("meds"))


class BackendError(Exception):
    pass


class TruncatedGeneration(BackendError):
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
        if what in ("generation", "rerank") and key:
            h["X-Colab-Key"] = key
        return h

    async def _post(self, url, payload, what, colab_key=None):
        # Retrieval and reranking are replayable read-only calls. Generation
        # must never be submitted twice after a lost response.
        attempts = 2 if what in ("retrieval", "rerank") else 1
        step_name = {"retrieval": "pipeline.http.retrieve", "rerank": "pipeline.http.rerank"}.get(what, "pipeline.http.generate")
        for attempt in range(attempts):
            try:
                with Step("pipeline", step_name,
                          payload.get("query") if what in ("retrieval", "rerank") else payload.get("prompt")):
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
                    service = "colab" if what in ("generation", "rerank") else "pipeline"
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
                raise ServiceUnavailable("colab" if what in ("generation", "rerank") else "pipeline") from None
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
            raise TruncatedGeneration("Generation was truncated; no incomplete draft can be verified.")
        r = d.get("response") or ""
        if not isinstance(r, str):
            raise BackendError("The generation service returned an invalid response.")
        if r.startswith("COLAB SERVER ERROR"):
            raise BackendError("The Colab server reported an error during generation.")
        return r

    async def rerank(self, query, passages):
        """Score verified statements against the user's question on Colab."""
        if not passages:
            return []
        if self.connection_resolver is not None:
            url, key = await self.connection_resolver()
        else:
            url, key = self.generate_url, self.colab_key
        url = url.removesuffix("generate") + "rerank"
        d = await self._post(url, {"query": query, "passages": passages}, "rerank", key)
        scores = d.get("scores")
        if (not isinstance(scores, list) or len(scores) != len(passages)
                or any(isinstance(score, bool) or not isinstance(score, (int, float))
                       or not math.isfinite(score) or not 0 <= score <= 1 for score in scores)):
            raise BackendError("The reranker returned an invalid response.")
        return [float(score) for score in scores]


# ---------- sources ----------
def _norm(s):
    # Compatibility/case/punctuation view for checks that compare words rather
    # than locate an exact quote. NFKC keeps mm³ and mm3 in the same number view.
    s = unicodedata.normalize("NFKC", normalize_evidence_text(str(s))).lower()
    s = re.sub(r"[\u2010-\u2015]", "-", s)
    # Match a hyphenated word to the fused form produced by a PDF line wrap.
    s = re.sub(r"(?<=[a-z])-(?=[a-z])", "", s)
    s = re.sub(r"[\"'\u2018\u2019\u201c\u201d`*]", "", s)
    s = re.sub(r"(?<=\d),(?=\d)", "", s)
    return re.sub(r"\s+", " ", s).strip(" .…")


def _source_markers(src):
    markers = src.get("footnote_markers") or src.get("footnoteMarkers") or []
    return markers if isinstance(markers, list) else []


def _split_markdown_row(line):
    stripped = line.strip()
    if len(stripped) < 2 or not stripped.startswith("|") or not stripped.endswith("|"):
        return []
    cells, current, escaped = [], [], False
    for char in stripped[1:-1]:
        if escaped:
            current.append(char); escaped = False
        elif char == "\\":
            escaped = True
        elif char == "|":
            cells.append("".join(current).strip()); current = []
        else:
            current.append(char)
    cells.append("".join(current).strip())
    return cells


def _table_separator(cells):
    return bool(cells) and all(re.fullmatch(r":?-{3,}:?", cell.strip()) for cell in cells)


def table_rows(text):
    """Parse page-scoped markdown tables into deterministic structured rows."""
    blocks, block = [], []
    for line in str(text or "").splitlines():
        if line.strip().startswith("|") and line.strip().endswith("|"):
            block.append(line)
        elif block:
            blocks.append(block); block = []
    if block:
        blocks.append(block)
    rows = []
    for lines in blocks:
        parsed = [_split_markdown_row(line) for line in lines]
        parsed = [cells for cells in parsed if cells]
        if len(parsed) < 2:
            continue
        headers = parsed[0]
        for cells in parsed[1:]:
            if _table_separator(cells) or len(cells) < 2:
                continue
            values = (cells + [""] * len(headers))[:len(headers)]
            values = [re.sub(r"\s+", " ", decode_entities(value)).strip() for value in values]
            headers = [re.sub(r"\s+", " ", decode_entities(value)).strip() for value in parsed[0]]
            label = values[0].strip()
            if label:
                rows.append({"headers": headers, "label": label, "values": values})
    return rows


def _table_quote(quote, tables, markers):
    """Accept only a row label plus one exact cell, never a paraphrased row."""
    q = canonical_text(quote, markers)
    if not q:
        return None
    for table in tables or []:
        headers, label, values = table.get("headers") or [], table.get("label") or "", table.get("values") or []
        if not label.strip():
            continue
        for index in range(1, min(len(headers), len(values))):
            cell = values[index]
            if not cell.strip():
                continue
            if q == canonical_text(f"{label} {cell}", markers):
                return {"headers": [headers[0] or "Row", headers[index] or f"Column {index + 1}"],
                        "row": [label, cell]}
    return None


def _canonical_contains(quote, source, footnote_markers=None):
    q = canonical_text(quote, footnote_markers)
    s = canonical_text(source, footnote_markers)
    return bool(q and s and f" {q} " in f" {s} ")


def _quote_match(quote, src):
    """Exact canonical substring, or one exact table row-label/cell pair."""
    markers = _source_markers(src)
    source = src.get("text") or ""
    tables = src.get("tables")
    if tables is None:
        tables = table_rows(source)
    table = _table_quote(quote, tables, markers)
    if table:
        return True, table
    return _canonical_contains(quote, source, markers), None


def _quote_in_source(quote, source_text, footnote_markers=None):
    src = {"text": source_text, "footnote_markers": footnote_markers or [], "tables": table_rows(source_text)}
    return _quote_match(quote, src)[0]


def _formatting_mismatch(quote, src):
    markers = _source_markers(src)
    q, s = formatting_view(quote, markers), formatting_view(src.get("text") or "", markers)
    return bool(q and s and q in s)


def _display_section(value):
    text = decode_entities(str(value or ""))
    text = re.sub(r"\[/?TABLES\]", " ", text, flags=re.I)
    parts = [part.strip() for part in re.split(r"[|·]", text)
             if part.strip() and part.strip().upper() != "TABLES"]
    return " > ".join(parts) or "General"


def _squash_name(value):
    text = re.sub(r"\s+", " ", decode_entities(str(value or ""))).strip(" ,;")
    words = []
    for word in text.split(" "):
        if word and (not words or _norm(word) != _norm(words[-1])):
            words.append(word)
    return " ".join(words)


def _clean_title(value, year="", version=""):
    text = _squash_name(value)
    text = re.sub(r"\.pdf$", "", text, flags=re.I)
    text = re.sub(r"^\d{6,}[-_ ]*", "", text)
    text = re.sub(r"(?:^|[-_ ])(?:eng|en)$", "", text, flags=re.I)
    text = re.sub(r"[-_ ](?:final|draft|superseded)$", "", text, flags=re.I)
    text = text.replace("_", " ")
    text = re.sub(r"\b\d{7,}\b", " ", text)
    if year:
        text = re.sub(rf"\b{re.escape(year)}\b", " ", text)
        text = re.sub(rf"(?:[\s,;_-]*{re.escape(year)})+\s*$", "", text).strip(" ,;-_")
    if version:
        version_label = version if version.lower().startswith("v") else f"v{version}"
        text = re.sub(rf"\b{re.escape(version_label)}\b", " ", text, flags=re.I)
        text = re.sub(rf"(?:[\s,;_-]*{re.escape(version_label)})+\s*$", "", text, flags=re.I).strip(" ,;-_")
    text = re.sub(r"\s+", " ", text).strip(" ,;-_")
    return text or "Untitled guideline"


def _display_label(society, title, version, year, status=None):
    society = _squash_name(society)
    title = _clean_title(title, str(year or "").strip(), str(version or "").strip())
    year = str(year or "").strip()
    version = str(version or "").strip()
    if version and version.lower().lstrip("v") != year:
        version_label = version if version.lower().startswith("v") else f"v{version}"
        if _norm(version_label) not in _norm(title) and _norm(version_label) not in _norm(society):
            title = f"{title} {version_label}" if title else version_label
    if society and _norm(society) not in _norm(title):
        label = f"{society}, {title}" if title else society
    else:
        label = title or society or "Untitled guideline"
    suffix = [part for part in (year, str(status or "").upper()) if part]
    if suffix:
        label += f" ({', '.join(suffix)})"
    return re.sub(r"\s+", " ", label).strip()


def _resolve_strength(provided, text):
    # The provided field may come from an LLM; never trust it for display.
    return _derive_grade(text)[0]


def _resolve_certainty(provided, text):
    return _derive_grade(text)[1]


def _grade_scope(text, quote=""):
    """Paragraph or table row containing the quote; never grade a whole chunk blindly."""
    source = normalize_evidence_text(str(text or ""))
    quote = str(quote or "").strip()
    if not quote:
        return source
    fragments = [fragment for fragment in re.split(r"\.\.\.|…", quote) if _norm(fragment)]
    if not fragments:
        return ""
    for fragment in fragments:
        target = canonical_text(fragment)
        if not target:
            continue
        for row in table_rows(source):
            row_text = " | ".join(row.get("values") or [])
            if target in canonical_text(row_text):
                return row_text
        paragraphs = [part.strip() for part in re.split(r"\n\s*\n|\[/?TABLES\]", source) if part.strip()]
        matches = [part for part in paragraphs if target in canonical_text(part)]
        if matches:
            return min(matches, key=len)
    return ""


def _derive_grade(text):
    # Grade phrases use a spaced or hyphenated separator ("moderate-certainty");
    # keep it for this view. The quote-matching view removes intra-word hyphens.
    value = re.sub(r"\s+", " ", normalize_evidence_text(str(text))).lower()
    strength = ""
    match = re.search(r"\b(strong|conditional|weak)\s+recommendation\b", value)
    if match:
        strength = match.group(1)
    certainty = ""
    match = re.search(r"\b(very\s+low|high|moderate|low)[\s-]certainty\s+evidence\b", value)
    if match:
        certainty = re.sub(r"\s+", "_", match.group(1))
    return strength, certainty


def sources_from_text(context):
    """Parse the '[S1] label | section | p.N | strength' text format into source dicts (no ids yet)."""
    out = []
    for part in re.split(r"(?m)^(?=\[S\d+\] )", context or ""):
        m = re.match(r"\[S\d+\]\s*([^\n]*)\n?(.*)", part, re.S)
        if not m: continue
        segs = [s.strip() for s in m[1].split("|")]
        page = ""
        for s in segs[2:]:
            if s.lower().startswith("p."): page = s[2:].strip()
        text = m[2].strip()
        strength, certainty = _derive_grade(text)
        kind = "table_row" if "[TABLES]" in text else "recommendation" if strength else "clinical_consideration"
        out.append({"label": segs[0], "section": _display_section(segs[1] if len(segs) > 1 else ""), "page": page,
                    "strength": strength, "certainty": certainty, "kind": kind, "population": "",
                    "draft": "DRAFT" in segs[0].upper(), "superseded": "SUPERSEDED" in segs[0].upper(),
                    "text": text, "score": None, "footnote_markers": [], "tables": table_rows(text)})
    return out


def sources_from_chunks(chunks):
    out = []
    for c in chunks:
        status = str(c.get("status", "")).lower()
        draft = status not in ("final", "superseded")
        superseded = status == "superseded"
        text = (c.get("text") or "").strip()
        label = _display_label(c.get("society"), c.get("title") or c.get("file_name"),
                               c.get("version"), c.get("year"), "SUPERSEDED" if superseded else "DRAFT" if draft else "FINAL")
        strength, certainty = _derive_grade(text)
        out.append({"label": label, "section": _display_section(c.get("section")), "page": str(c.get("page", "")),
                    "strength": strength, "certainty": certainty,
                    "kind": c.get("kind") or "", "population": c.get("population") or "",
                    "recommendation_id": c.get("recommendation_id") or "",
                    "draft": draft, "superseded": superseded,
                    "title": c.get("title") or "", "file_name": c.get("file_name") or "", "version": c.get("version") or "",
                    "effective_date": c.get("effective_date") or "", "review_by": c.get("review_by") or "",
                    "source_url": c.get("source_url") or "", "permission_note": c.get("permission_note") or "",
                    "text": text, "score": c.get("rerank_score"),
                    "footnote_markers": c.get("footnote_markers") or [], "tables": table_rows(text)})
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
    lines = []
    for source in sources.values():
        structure = ""
        if source.get("kind") and source["kind"] != "clinical_consideration":
            structure += f" | kind:{source['kind']}"
        if source.get("population"):
            structure += f" | population:{source['population']}"
        lines.append(f"[{source['id']}] {source['label']} | {source['section']}{structure} | p.{source['page']}"
                     + (f" | {source['strength']}" if source["strength"] else "")
                     + f"\n{source['text']}\n")
    return "\n".join(lines)


def _cite(s, quote=None):
    strength, certainty = _derive_grade(_grade_scope(s.get("text", ""), quote or "")) if quote else (s.get("strength", ""), s.get("certainty", ""))
    c = {k: s[k] for k in ("id", "label", "section", "page", "draft")}
    c["strength"] = strength
    if certainty: c["certainty"] = certainty
    for key in ("superseded", "title", "file_name", "version", "effective_date", "review_by", "source_url", "permission_note"):
        if s.get(key): c[key] = s[key]
    if s.get("score") is not None: c["score"] = round(float(s["score"]), 2)
    return c


# ---------- parsing and verification ----------
def extract_json(text):
    """Extract a complete JSON value; never fix quotes, commas, values or truncation."""
    decoder = json.JSONDecoder()
    text, cursor = (text or "")[:100000], 0
    while match := re.search(r"[\[{]", text[cursor:]):
        start = cursor + match.start()
        stack, quoted, escaped = [], False, False
        for end in range(start, len(text)):
            char = text[end]
            if quoted:
                if escaped: escaped = False
                elif char == "\\": escaped = True
                elif char == '"': quoted = False
            elif char == '"': quoted = True
            elif char in "[{": stack.append(char)
            elif char in "]}":
                if not stack or stack.pop() != ("[" if char == "]" else "{"):
                    raise ValueError("Invalid JSON envelope")
                if not stack: break
        else:
            raise ValueError("Incomplete JSON envelope")
        # Never search inside a malformed/truncated envelope for otherwise valid items.
        cursor = end + 1
        try:
            value = decoder.decode(text[start:cursor])
            if isinstance(value, (dict, list)):
                return value
        except ValueError:
            continue
    raise ValueError("No complete JSON value")


def parse_draft(text):
    items = []
    if not (text or "").strip() or (text or "").strip() == "NOT_FOUND":
        return [], {"empty_draft": 1}
    if re.search(r"(?m)^\s*(?:```json\s*$|[\[{])", text) or not re.search(r"(?im)^\s*SOURCE\s*:", text):
        try:
            data = extract_json(text)
            records = data.get("items") if isinstance(data, dict) else data
            if not isinstance(records, list) or len(records) > 100:
                raise ValueError("Invalid item array")
            malformed = 0
            for value in records:
                if not isinstance(value, dict):
                    malformed += 1; continue
                tag, rec, quote = value.get("source", value.get("tag")), value.get("recommendation", value.get("rec")), value.get("quote")
                if not all(isinstance(v, str) and v.strip() for v in (tag, rec, quote)) or not re.fullmatch(r"\[?S\d+\]?", tag.strip()):
                    malformed += 1; continue
                items.append({"tag": tag.strip().strip("[]"), "rec": rec.strip(), "quote": quote.strip()})
            return items, ({"json_parse_error": malformed} if malformed else {} if records else {"empty_draft": 1})
        except (ValueError, TypeError):
            return [], {"json_parse_error": 1}
    malformed = 0
    for block in re.split(r"(?im)^\s*SOURCE\s*:\s*", text or "")[1:]:
        tag = re.match(r"\[?\s*S?(\d+)", block)
        rec = re.search(r"(?is)RECOMMENDATION\s*:\s*(.*?)(?:\n\s*QUOTE\s*:|\Z)", block)
        quote = re.search(r"(?is)QUOTE\s*:\s*(.*)", block)
        if tag and rec:
            items.append({"tag": f"S{tag[1]}", "rec": rec[1].strip(), "quote": (quote[1] if quote else "").strip().strip("\"“”")})
        else:
            malformed += 1
    return items[:100], ({"json_parse_error": malformed} if malformed else {})


def parse_items(text):
    return parse_draft(text)[0]


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


def scope_issues(rec, quote, reasons=None):
    """The recommendation must not widen, narrow or flip what the quoted words say."""
    r, q, out = _norm(rec), _norm(quote), []
    for w in QUALIFIERS:
        if re.search(rf"\b{w}", r) and not re.search(rf"\b{w}", q):
            out.append(f"“{w}” is not in the quoted text")
            if reasons is not None: reasons.append("scope_mismatch")
    if bool(NEG.search(q)) != bool(NEG.search(r)):
        out.append("negation or no-recommendation wording differs from the quote")
        if reasons is not None: reasons.append("negation_conflict")
    if COND_SRC.search(q) and not COND_REC.search(r):
        out.append("drops a condition stated in the quote (unless/except/only if)")
        if reasons is not None: reasons.append("scope_mismatch")
    return out


def verify_item(item, sources, reasons=None):
    codes = reasons if reasons is not None else []
    src = sources.get(item["tag"])
    if not src:
        codes.append("quote_not_found")
        return ["cited source is not among the retrieved sources"]
    text, issues = _norm(src["text"]), []
    frags = [f for f in re.split(r"\.\.\.|…", item["quote"]) if _norm(f)]
    table = None
    failed = None
    for fragment in frags:
        found, table_match = _quote_match(fragment, src)
        if not found:
            failed = fragment
            break
        if table_match and len(frags) == 1:
            table = table_match
    if not frags or failed is not None:
        if failed is not None and _formatting_mismatch(failed, src):
            issues.append("quote differs from the source only in formatting that could not be canonicalized")
            codes.append("formatting_mismatch")
        else:
            issues.append("quote not found in the cited source")
            codes.append("quote_not_found")
    elif table:
        item["table"] = table
    missing = [n for n in re.findall(r"\d+(?:\.\d+)?", _norm(item["rec"]))
               if not re.search(rf"(?<![\d.]){re.escape(n)}(?!\d)", text)]
    if missing:
        issues.append("number(s) not in source: " + ", ".join(missing)); codes.append("number_missing")
    if support_ratio(item["rec"], src["text"]) < 0.5:
        issues.append("recommendation wording is not supported by the source text"); codes.append("below_threshold")
    item_strength, _ = _derive_grade(_grade_scope(src["text"], item["quote"]))
    safety_strength = src.get("strength") or item_strength
    if STRONG.search(item["rec"]) and SOFT.search(safety_strength + " " + item["quote"]):
        issues.append("wording is stronger than the source")
        codes.append("stronger_than_source")
    issues += scope_issues(item["rec"], item["quote"], codes)
    return issues


def repair_item(item, sources, threshold=0.88):
    """Only correct a citation for an already exact canonical quote; never replace its words."""
    q = item["quote"]
    if not q.strip() or re.search(r"\.\.\.|…", q): return item
    src = sources.get(item["tag"])
    if src and _quote_match(q, src)[0]: return item
    for s_ in sources.values():
        if _quote_match(q, s_)[0]: return item | {"tag": s_["id"], "repaired": "citation corrected"}
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
    if infection and ("hypotension" in ids or "shock" in t):
        add("Septic shock and hypotension", [
            "vasopressor first-line agent and MAP target in septic shock in adults",
            "initial fluid resuscitation crystalloid in sepsis-induced hypotension",
            "corticosteroids hydrocortisone for septic shock with ongoing vasopressor requirement",
            "lactate-guided resuscitation and capillary refill in sepsis",
             "blood transfusion threshold in sepsis"], max_items=5, max_chunks=10)
    elif "hypotension" in ids or "shock" in t:
        add("Hypotension review", ["assessment of low blood pressure and possible shock in adults",
                                   "hypotension evaluation and medication review in adults"])
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
        if infection:
            add("Acute kidney injury in sepsis", ["sepsis-associated acute kidney injury fluids vasopressors nephrotoxin avoidance",
                                                  "timing of renal replacement therapy in acute kidney injury in critically ill adults"], max_items=4)
        else:
            add("Possible acute kidney injury review", ["acute kidney injury assessment creatinine trends and baseline kidney function",
                                                       "renal impairment medication review and nephrotoxin avoidance"], max_items=4)
    if "anemia" in ids: add("Anaemia", ["red blood cell transfusion threshold in critically ill adults with ischemic heart disease"])
    if "hypokalemia" in ids or "magnesium_low" in ids:
        add("Hypokalaemia and low magnesium", ["potassium and magnesium replacement for hypokalemia in cardiac patients"])
    if low_plt and re.search(r"stent|antiplatelet|plavix|clopidogrel|dapt", t):
        add("Antiplatelet therapy with a recent stent", ["dual antiplatelet therapy after stent bleeding risk thrombocytopenia"])
    if infection and ("hypotension" in ids or "shock" in t or "sepsis" in t):
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
        data = extract_json(raw)
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
async def retrieve_problem(p, backend, n_queries, max_chunks, min_score, config=None):
    queries = list(p.get("queries") or [p["query"], f"{p['title']} recommendation"][:n_queries])
    if config is not None and config.get("retrieval.hydeEnabled", False):
        # HyDE is optional and fail-open: a model failure must not turn an
        # otherwise answerable guideline question into a retrieval failure.
        try:
            question = p.get("resolved_question") or p.get("query") or p["title"]
            hypothetical = (await backend.generate(HYDE_PROMPT.replace("{question}", question), 160)).strip()
            hypothetical = " ".join(hypothetical.split())[:800]
            if hypothetical and "NOT_FOUND" not in hypothetical and hypothetical not in queries:
                queries.append(hypothetical)
        except BackendError:
            pass
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


RELEVANCE_STOP = TOPIC_STOP | set(
    "which what when where who whom whose why how should could would can does did is are was were "
    "has have had use used using give given recommendation recommended patient patients".split()
)
_NARROW_SCOPE = re.compile(
    r"\b(?:pregnan\w*|tubercul\w*|\btb\b|children|infants?|neonat\w*|renal\s+failure|dialysis|"
    r"contraindicat\w*|only\s+if|except)\b",
    re.I,
)
_BROAD_QUESTION = re.compile(
    r"\b(?:adults?|patients?|people|persons?|all|initial|first[- ]line|starting|management|treatment)\b",
    re.I,
)


def _broad_question(text):
    value = str(text or "")
    return bool(_BROAD_QUESTION.search(value)) and not bool(_NARROW_SCOPE.search(value))


def _narrow_penalty(text):
    return 0.10 if _NARROW_SCOPE.search(str(text or "")) else 0.0


def question_terms(text):
    value = _norm(text)
    tokens = re.findall(r"[a-z0-9]{3,}", value)
    return {token[:5] for token in tokens if token not in RELEVANCE_STOP and token[:5] not in RELEVANCE_STOP}


def term_overlap(question, text):
    wanted = question_terms(question)
    if not wanted:
        return 0.0
    present = {token[:5] for token in re.findall(r"[a-z0-9]{3,}", _norm(text))}
    return len(wanted & present) / len(wanted)


async def apply_relevance(problems, question, backend, config):
    """Demote verified but off-question items; never let them lead the answer."""
    threshold = float(config.get("retrieval.relevanceThreshold", 0.55))
    overlap_threshold = float(config.get("retrieval.relevanceOverlapThreshold", 0.30))
    for problem in problems:
        items = problem.get("items") or []
        if not items or problem.get("status") == "error":
            problem.setdefault("related", [])
            continue
        passages = [f"{item['recommendation']}\n{item['quote']}" for item in items]
        query = problem.get("resolved_question") or question
        scores = None
        rerank = getattr(backend, "rerank", None)
        if callable(rerank):
            try:
                scores = await rerank(query, passages)
            except Exception:
                scores = None
        if not isinstance(scores, list) or len(scores) != len(items):
            scores = None
        broad = _broad_question(query)
        scored = []
        for index, item in enumerate(items):
            part = problem.get("resolved_question") or problem.get("covers") or question
            overlap = term_overlap(part, passages[index])
            if scores is None:
                score = overlap
                keep = score >= overlap_threshold
            else:
                score = 0.5 * float(scores[index]) + 0.5 * overlap
                keep = score >= threshold
            if broad:
                score = max(0.0, score - _narrow_penalty(passages[index]))
            scored.append((score, index, keep, item))
        # A narrow exception can still be verified evidence, but it must not
        # outrank a general population statement on a broad question.
        ordered = sorted(scored, key=lambda row: (-row[0], row[1]))
        lead = [item for _, _, keep, item in ordered if keep]
        related = [item for _, _, keep, item in ordered if not keep]
        problem["items"] = lead
        problem["related"] = related
        if lead or related:
            problem["status"] = "ok"


def coverage_report(question, problems):
    """One row per stated question part; never infer coverage from the lead list alone."""
    report = []
    for part in question_parts(question):
        matches = [p for p in problems if p.get("covers") and _norm(p["covers"]) == _norm(part)]
        if any(p.get("items") for p in matches):
            status = "answered"
        elif any(p.get("related") for p in matches):
            status = "related_only"
        elif any(p.get("status") == "error" for p in matches):
            # A failed retrieval/generation is not evidence that the guideline
            # lacks coverage. The separate "Analysis incomplete" state carries
            # that uncertainty; never label it "Not covered".
            continue
        else:
            status = "not_covered"
        report.append({"part": part, "status": status})
    return report


async def answer_problem(p, ret, backend, patient, config):
    sources = ret["sources"]
    res = {"title": p["title"], "queries": ret["queries"], "status": "not_found", "items": [], "related": [], "unverified": [],
           "sources": [_cite(s) | {"excerpt": s["text"][:400], "score": s["score"]} for s in sources.values()],
           "diagnostics": empty_diagnostics()}
    if p.get("covers"): res["covers"] = p["covers"]
    if p.get("resolved_question"): res["resolved_question"] = p["resolved_question"]
    if ret["errored"]: return res | {"status": "error", "detail": "Retrieval returned an error."}
    if not sources: return res
    k = p.get("max_items", 3)
    _ans_prompt = config.prompt("answer", ANSWER_PROMPT)
    try:
        out = await backend.generate(Cfg.render_prompt("answer", _ans_prompt, problem=p.get("resolved_question", p["title"]), patient=patient, k=k, context=context_text(sources)), 450 + 60 * k)
    except TruncatedGeneration:
        res["diagnostics"]["drafts"] = 1
        res["diagnostics"]["reasons"]["truncated"] = 1
        return res | {"status": "error", "detail": "Generation was truncated; no incomplete draft was verified."}
    stems = p.get("topic") if "topic" in p else topic_stems(p["title"])
    evaluated = verify_draft(out, sources, stems)
    res.update(evaluated)
    res["status"] = "ok" if res["items"] else ("unverified" if res["diagnostics"]["candidates"] else "not_found")
    return res


def detect_conflicts(problems):
    """Return opposite-polarity verified statements from different guidelines.

    This is deliberately transparent: both verified statements remain present,
    with their citations. Nothing is merged, ranked, or silently selected.
    """
    statements = []
    for problem in problems:
        for item in problem.get("items", []):
            citation = item.get("citation") or {}
            label = citation.get("label") or ""
            words = {w[:5] for w in re.findall(r"[a-z]{4,}", str(item.get("recommendation", "")).lower())
                     if w not in STOP}
            if not label or not words:
                continue
            statements.append({
                "label": label,
                "recommendation": item["recommendation"],
                "citation": citation,
                "words": words,
                "negative": bool(NEG.search(item["recommendation"])),
            })
    conflicts, seen = [], set()
    for index, left in enumerate(statements):
        for right in statements[index + 1:]:
            if left["label"] == right["label"] or left["negative"] == right["negative"]:
                continue
            shared = left["words"] & right["words"]
            if len(shared) < 2:
                continue
            key = tuple(sorted((left["label"], right["label"], left["recommendation"], right["recommendation"])))
            if key in seen:
                continue
            seen.add(key)
            conflicts.append({
                "point": "Potential guideline disagreement",
                "items": [
                    {"recommendation": left["recommendation"], "citation": left["citation"]},
                    {"recommendation": right["recommendation"], "citation": right["citation"]},
                ],
            })
    return conflicts


NOT_COVERED_REASONS = ("nothing_retrieved", "retrieved_unverified", "only_draft_sources")


def not_covered_reason(problem, counters):
    """Return a fixed, text-free reason category for a not-covered problem.

    The category is derived from the Prompt 3 source retrieval and verification
    counters. It never includes model output, a draft quote or a generated
    explanation.
    """
    if problem.get("status") not in ("not_found", "unverified"):
        return None
    sources = problem.get("sources") or []
    if not sources:
        return "nothing_retrieved"
    if all(bool(source.get("draft")) for source in sources):
        return "only_draft_sources"
    # Sources were retrieved and the verifier counter confirms zero verified
    # statements. Never derive this from a draft or model explanation.
    return "retrieved_unverified" if counters.get("verified", 0) == 0 else None


def empty_diagnostics():
    return {"drafts": 0, "candidates": 0, "verified": 0, "reasons": dict.fromkeys(REJECTION_REASONS, 0)}


def verify_draft(out, sources, stems):
    """Offline replay seam. Rejection codes are per candidate (possibly multiple); no text in counters."""
    diagnostics = empty_diagnostics()
    diagnostics["drafts"] = 1
    items, parse_errors = parse_draft(out)
    diagnostics["reasons"].update(parse_errors)
    diagnostics["candidates"] = len(items)
    res = {"items": [], "unverified": [], "diagnostics": diagnostics}
    seen = set()
    for it in items:
        it = repair_item(it, sources)
        codes = []
        issues = verify_item(it, sources, codes)
        src_ = sources.get(it["tag"])
        if src_ and not on_topic(stems, src_["text"] + " " + src_["section"]):
            issues.append("source does not mention the question topic"); codes.append("topic_mismatch")
        key = (it["tag"], _norm(it["rec"]), _norm(it["quote"]))
        if not issues and key in seen:
            diagnostics["reasons"]["deduplicated"] += 1
            continue
        if not issues: seen.add(key)
        for code in set(codes): diagnostics["reasons"][code] += 1
        entry = {"recommendation": it["rec"], "quote": it["quote"]} | ({"repaired": it["repaired"]} if it.get("repaired") else {})
        if it.get("table"): entry["table"] = it["table"]
        if it["tag"] in sources: entry["citation"] = _cite(sources[it["tag"]], it["quote"])
        (res["unverified"] if issues else res["items"]).append(entry | ({"issues": issues} if issues else {}))
    diagnostics["verified"] = len(res["items"])
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


async def question_plan(text, backend, config, history=None, max_problems=8):
    parts = question_parts(text)[:max(1, max_problems)]
    _rw_prompt = config.prompt("rewrite", REWRITE_PROMPT)
    if len(parts) == 1:
        cleaned = clean_question(parts[0])
        q = cleaned
        try:
            rewrite_input = _history_for_rewrite(history) + text[:500]
            r = (await backend.generate(Cfg.render_prompt("rewrite", _rw_prompt, q=rewrite_input), 60)).strip().splitlines()[0].strip(" \"'")
            if 2 <= len(r.split()) <= 24 and ":" not in r and "NOT_FOUND" not in r: q = r
        except BackendError: raise
        except Exception: pass
        queries = [q] + ([cleaned] if _norm(cleaned) != _norm(q) else [f"{q} recommendations"])
        queries = queries[:config.get("retrieval.queriesPerProblem", 2)]
        # Search rewriting is not evidence and must not replace an explicit question.
        resolved = q if _history_for_rewrite(history) else text.strip()
        if parts[0] not in text:
            return []
        return [{"title": text.strip()[:80], "resolved_question": resolved, "query": q, "queries": queries,
                 "topic": topic_stems(resolved, whole=True), "max_items": 4, "covers": parts[0]}]
    plan = []
    for part in parts:
        if part not in text:
            continue
        query = clean_question(part)[:300] or part[:300]
        queries = [query]
        if config.get("retrieval.queriesPerProblem", 2) > 1:
            queries.append(f"{query} recommendations")
        plan.append({"title": part.rstrip("?").strip()[:80], "resolved_question": part, "query": query,
                     "queries": queries, "topic": topic_stems(part, whole=True), "max_items": 4, "covers": part})
    return plan


@timed("pipeline", "pipeline.analyze", input_argument=0)
async def analyze(text, backend, emit, config=None, rules=None, history=None, drug_catalog=None,
                  max_problems=8, queries_per_problem=2, max_chunks=8, min_score=0.30, include_diagnostics=False):
    # Always create a request-local default; never share a mutable config object.
    config = config or Cfg.Config.from_dict()
    max_problems = config.get("retrieval.topK", max_problems)
    queries_per_problem = config.get("retrieval.queriesPerProblem", queries_per_problem)
    max_chunks = config.get("retrieval.maxChunks", max_chunks)
    min_score = config.get("retrieval.minRerank", min_score)

    smalltalk = is_smalltalk(text)
    interrogative = is_question(text)
    fx = None
    facts_error = None
    if not smalltalk:
        try:
            fx = F.analyze_facts(text, rules=rules, drug_catalog=drug_catalog)
        except Exception as error:
            facts_error = error
            fx = None
    if not interrogative and facts_error is not None and len(text.split()) >= 40:
        case_mode = True
    else:
        case_mode = not interrogative and has_clinical_facts(fx)
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
    if case_mode:
        await progress("facts", "started", "Reading values and medications")
        if facts_error is not None:
            await progress("facts", "failed", "Reading values and medications failed")
            raise facts_error
        await progress("facts", "completed", "Values and medications read")
    flags = fx["flags"] if fx else []
    patient = fx["summary"] if case_mode and fx else "not given"
    await progress("planning", "started", "Planning the guideline questions" if case_mode else "Preparing the search")
    try:
        if case_mode:
            plan, plan_source = await make_plan(text, flags, backend, max_problems, config)
        else:
            plan, plan_source = await question_plan(text, backend, config, history=history, max_problems=max_problems), "single_question"
            plan = [problem for problem in plan if problem.get("covers") and problem["covers"] in text]
            if not plan:
                raise ValueError("No question part could be mapped to a stated problem.")
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
            result = await retrieve_problem(problem, backend, queries_per_problem, max_chunks, min_score,
                                            config=config)
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
    diagnostics = []

    def collect(problem, index):
        counters = problem.pop("diagnostics", empty_diagnostics())
        record_answer_diagnostics(index, counters)
        diagnostics.append({"problemIndex": index, **counters})
        reason = not_covered_reason(problem, counters)
        if reason:
            problem["reason"] = reason
        problems.append(problem)
    for i, (p, ret) in enumerate(zip(plan, rets), 1):
        if isinstance(ret, Exception):
            collect({"title": p["title"], "queries": [], "status": "error", "detail": "Guideline retrieval failed.", "items": [], "related": [],
                     "unverified": [], "sources": [], **({"covers": p["covers"]} if p.get("covers") else {})}, i)
            continue
        if ret["errored"] or not ret["sources"]:
            # No generation/verification ran when retrieval yielded no evidence.
            collect(await answer_problem(p, ret, backend, patient, config), i)
            continue
        step_id = f"verification-{i}"
        await progress("verification", "started", f"Drafting and checking answer {i} of {len(plan)}", step_id)
        try:
            problem = await answer_problem(p, ret, backend, patient, config)
            collect(problem, i)
            await progress("verification", "failed" if problem["status"] == "error" else "completed", f"Answer {i} checked: {len(problem['items'])} verified, {len(problem['unverified'])} excluded", step_id)
        except BackendError as error:
            await progress("verification", "failed", f"Answer {i} generation or checking failed", step_id)
            if i == 1 or isinstance(error, ServiceUnavailable): raise
            collect({"title": p["title"], "queries": ret["queries"], "status": "error", "detail": "Answer generation failed; no recommendation was verified.", "items": [], "related": [],
                     "unverified": [], "sources": [], **({"covers": p["covers"]} if p.get("covers") else {})}, i)
        except Exception:
            await progress("verification", "failed", f"Answer {i} generation or checking failed", step_id)
            raise
    if plan_source == "single_question":
        await apply_relevance(problems, text, backend, config)
        coverage = coverage_report(text, problems)
    else:
        coverage = []
    if case_mode and fx:
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
    if any(pr.get("related") for pr in problems):
        warnings.append("Some verified statements were related but did not directly answer your question; they are shown separately and are not the lead answer.")
    if any(pr["status"] == "error" for pr in problems):
        warnings.append("Some problems could not be analyzed. Retry them; their guideline coverage is unknown.")
    if case_mode and fx:
        warnings.append(fx["assumptions"][0])
        if fx["values"].get("mentation_term"):
            warnings.append(f"“{fx['values']['mentation_term']}” was read as altered consciousness (used for qSOFA).")
        if any(m["confidence"] == "fuzzy" for m in fx["meds"]):
            warnings.append("A drug name was matched approximately (marked “verify”). Confirm the actual drug.")
        warnings.extend(fx["warnings"])
    conflicts = detect_conflicts(problems)
    not_covered = [p["title"] for p in problems if p["status"] in {"not_found", "unverified"}]
    not_covered += [row["part"] for row in coverage
                    if row["status"] == "not_covered" and row["part"] not in not_covered
                    and not any(p.get("covers") and _norm(p["covers"]) == _norm(row["part"]) and p["status"] == "error"
                                for p in problems)]
    return {"plan_source": plan_source, "problems": problems,
            "not_covered": not_covered,
            "coverage": coverage,
            "warnings": warnings, "disclaimer": DISCLAIMER, "version": VERSION,
            **({"conflicts": conflicts} if conflicts else {}),
            **({"diagnostics": diagnostics} if include_diagnostics else {})}
