"""Single-source async hybrid chunker for the clinical RAG platform.

The model is injected as ``await llm(prompt)``. Structure, validation, front-matter
detection and page accounting stay deterministic. Model calls only profile,
criticise boundaries and extract metadata; chunk text is never rewritten.
"""

import hashlib
import json
import re
from collections.abc import Awaitable, Callable
from typing import Any

from evidence_text import normalize_evidence_text, normalize_for_match

AsyncLLM = Callable[[str], Awaitable[str]]

MAX_TOKENS_DEFAULT = 400
WORDS_PER_TOKEN = 0.75
PAGE_MARKER = re.compile(r"(?m)^\s*---\s*Page\s+(\d+)\s*---\s*$")
HEADING_RE = re.compile(r"(?m)^(#{1,6}\s+.+|[A-Z][A-Za-z ,\-]{3,60}:?\s*)$")

_ABBREV = re.compile(
    r"\b(?:e\.g|i\.e|Dr|Mr|Mrs|Ms|Prof|Sr|Jr|St|vs|etc|cf|no|fig|p\.|pp|vol|ed|trans)\.\s",
    re.I,
)
_RECOMMENDATION_HEADERS = re.compile(
    r"(?im)^\s*(summary\s+of\s+recommendations?|recommendations?|key\s+recommendations?|"
    r"guidance\s+recommendations?|clinical\s+recommendations?)\s*:?\s*$"
)
_RECOMMENDATION_RE = re.compile(
    r"\b(?:we|clinicians?|the\s+panel|guideline)\s+recommend\w*\b|"
    r"\b(?:strong|weak|conditional)\s+recommendation\b",
    re.I,
)
_STRENGTH_RE = re.compile(r"\b(strong|weak|conditional)\s+recommendation\b", re.I)
_CERTAINTY_RE = re.compile(r"\b(very\s+low|high|moderate|low)[\s-]certainty\s+evidence\b", re.I)
_DEFINITION_RE = re.compile(
    r"\b(?:definitions?|defined\s+as|is\s+defined\s+as|refers?\s+to|means?|in\s+this\s+guideline)\b",
    re.I,
)
_SUMMARY_RE = re.compile(
    r"\b(?:summary\s+of\s+recommendations?|key\s+recommendations?|recommendations?\s+summary|overview)\b",
    re.I,
)
_NARROW_RE = re.compile(
    r"\b(?:pregnan\w*|tubercul\w*|\btb\b|children|infants?|neonat\w*|"
    r"renal\s+failure|dialysis|contraindicat\w*|only\s+if|except)\b",
    re.I,
)
_YEAR_RE = re.compile(r"\b(?:19|20)\d{2}\b")


def _grade(text: str) -> tuple[str, str]:
    value = re.sub(r"\s+", " ", normalize_evidence_text(str(text or ""))).lower()
    strength = _STRENGTH_RE.search(value)
    certainty = _CERTAINTY_RE.search(value)
    return (strength.group(1) if strength else "",
            re.sub(r"\s+", "_", certainty.group(1)) if certainty else "")


def classify_chunk(text: str, section: str = "") -> str:
    """One deterministic semantic kind per atomic chunk.

    The kind is structural metadata, not a clinical judgement. It is derived
    only from the verbatim chunk text and its section path.
    """
    stripped = text.strip()
    section_value = str(section or "")
    if "[TABLES]" in stripped or stripped.startswith("|"):
        # A graded table row is a recommendation object with a table layout;
        # an ungraded table is a plain TableRow object.
        return "recommendation" if _STRENGTH_RE.search(stripped) else "table_row"
    if _SUMMARY_RE.search(section_value) or _SUMMARY_RE.search(stripped[:300]):
        return "summary"
    if _STRENGTH_RE.search(stripped) or _RECOMMENDATION_RE.search(stripped):
        return "recommendation"
    if _DEFINITION_RE.search(stripped):
        return "definition"
    return "clinical_consideration"


def _first_title(text: str, section: str = "") -> str:
    for line in text.splitlines():
        value = line.strip().lstrip("#").strip().rstrip(":")
        if 2 <= len(value) <= 120 and not value.startswith(("|", "[")):
            return value
    return str(section or "").strip()[:120] or "Untitled section"


def _population(text: str) -> str:
    match = _NARROW_RE.search(text)
    return match.group(0) if match else ""


def structured_metadata(chunk: dict[str, Any], supplied: dict[str, Any] | None = None) -> dict[str, Any]:
    """Bind title, year, body and grade into one atomic metadata payload.

    The LLM may propose section/population/status/topic, but kind, body,
    strength and certainty are derived deterministically from the verbatim
    chunk. Adjacent recommendations therefore cannot share a grade.
    """
    supplied = supplied or {}
    text = str(chunk.get("text") or "")
    section = str(supplied.get("section_path") or supplied.get("section") or "").strip()
    kind = classify_chunk(text, section)
    title = section or _first_title(text, section)
    year_match = _YEAR_RE.search(text)
    strength, certainty = _grade(text)
    body = text
    identity = f"{chunk.get('page', '')}|{title}|{body}".encode("utf-8")
    metadata = {
        "kind": kind,
        "title": title[:200],
        "year": year_match.group(0) if year_match else "",
        "body": body,
        "table": "[TABLES]" in text or text.strip().startswith("|"),
        "strength": strength,
        "certainty": certainty,
        "population": str(supplied.get("population") or _population(text))[:120],
        "section_path": section[:256],
        "status": str(supplied.get("status") or "").strip()[:32],
        "topic": str(supplied.get("topic") or "").strip()[:120],
        "recommendation_id": hashlib.sha256(identity).hexdigest()[:16]
        if kind in {"recommendation", "summary", "definition"}
        or (kind == "table_row" and (strength or certainty)) else "",
    }
    return metadata

FRONT_MATTER_INDICATORS = (
    r"isbn",
    r"copyright",
    r"library of congress",
    r"printed in",
    r"disclaimer",
    r"dedication",
    r"foreword",
    r"preface",
    r"acknowledg",
    r"contents",
    r"table of contents",
    r"publisher",
    r"edition",
)

# Never skip a page with clinical content merely because it also contains a
# front-matter-looking word such as "contents" or "edition".
CLINICAL_CONTENT_RE = re.compile(
    r"\b(?:patient|diagnos\w*|treat\w*|therap\w*|dose|dosage|mg|"
    r"antibi\w*|antimicrob\w*|surg\w*|renal|kidney|cardiac|sepsis|shock|"
    r"hemorrhag\w*|thrombo\w*|ventilat\w*|oxygen|fluid|transfus\w*|"
    r"creatinine|potassium|sodium|glucose|insulin|heparin|aspirin)\b",
    re.I,
)

PROFILE_PROMPT = """You are profiling a clinical document to choose a chunking strategy.
Document sample:
{sample}
User guidance: {guidance}
Reply ONLY JSON: {{"kind":"guideline|protocol|paper|other",
  "structure":"headings|numbered_recommendations|free_text|mixed",
  "strategy":"split by recommendation grade; keep tables intact",
  "notes":"one sentence"}}"""

CRITIC_PROMPT = """You are a chunk critic. User guidance: {guidance}
Decide: KEEP, MERGE_WITH_PREVIOUS, or SPLIT.
Rules: chunk text stays verbatim; self-contained one topic; don't split tables or recommendation from strength.
Candidates:
{candidates}
Reply ONLY JSON: {{"decisions":[{{"i":0,"action":"KEEP|MERGE_WITH_PREVIOUS|SPLIT","note":"..."}}]}}"""

ENRICH_PROMPT = """Extract metadata for this clinical chunk:
{text}
Reply ONLY JSON: {{"section_path":"...", "strength":"strong|weak|conditional|none",
  "population":"...", "status":"FINAL|DRAFT|unknown", "topic":"..."}}"""


def _tok(text: str) -> int:
    return int(len(text.split()) / WORDS_PER_TOKEN)


def _json_object(raw: str, fallback: dict[str, Any]) -> dict[str, Any]:
    try:
        return json.loads(raw[raw.index("{"): raw.rindex("}") + 1])
    except (ValueError, json.JSONDecodeError, TypeError):
        return fallback


def parse_pages(text: str) -> list[dict[str, Any]]:
    """Split text using ``--- Page N ---`` markers."""
    pages: list[dict[str, Any]] = []
    parts = PAGE_MARKER.split(text)
    if len(parts) == 1:
        return [{"page": 1, "text": normalize_evidence_text(text)}]
    if parts[0].strip():
        pages.append({"page": 1, "text": normalize_evidence_text(parts[0])})
    for i in range(1, len(parts), 2):
        page = int(parts[i])
        body = parts[i + 1] if i + 1 < len(parts) else ""
        pages.append({"page": page, "text": normalize_evidence_text(body)})
    return pages


def _sentences(text: str) -> list[str]:
    protected = _ABBREV.sub(lambda m: m.group(0).replace(".", "<PERIOD>"), text)
    parts = re.split(r"(?<=[.!?])\s+", protected)
    return [part.strip().replace("<PERIOD>", ".") for part in parts if part.strip()]


def _recommendation_boxes(lines: list[str]) -> dict[int, tuple[int, int]]:
    regions: dict[int, tuple[int, int]] = {}
    i = 0
    while i < len(lines):
        if not _RECOMMENDATION_HEADERS.match(lines[i].strip()):
            i += 1
            continue
        start = i
        i += 1
        while i < len(lines):
            line = lines[i].strip()
            if i > start + 1 and (HEADING_RE.match(line) or _RECOMMENDATION_HEADERS.match(line)):
                break
            i += 1
        end = i - 1
        for index in range(start, end + 1):
            regions[index] = (start, end)
    return regions


def _table_parts(text: str) -> list[tuple[bool, str]]:
    """Keep each markdown table block separate and atomic."""
    parts: list[tuple[bool, str]] = []
    current: list[str] = []
    table: list[str] = []
    in_table = False
    for line in text.splitlines():
        if not in_table and line.strip() == "[TABLES]":
            if current:
                parts.append((False, "\n".join(current)))
                current = []
            in_table, table = True, [line]
            continue
        if in_table:
            table.append(line)
            if line.strip() == "[/TABLES]":
                parts.append((True, "\n".join(table)))
                table, in_table = [], False
            continue
        current.append(line)
    if current:
        parts.append((False, "\n".join(current)))
    if table:
        parts.append((True, "\n".join(table)))
    return parts


def _is_table_separator(line: str) -> bool:
    cells = [cell.strip() for cell in line.strip().strip("|").split("|")]
    return bool(cells) and all(re.fullmatch(r":?-{3,}:?", cell) for cell in cells)


def _split_table_recommendations(block: str) -> list[str]:
    """Split a graded recommendation table into one atomic object per row.

    The header and separator are repeated in each row so its year, body and
    grade stay together. Ungraded tables remain one atomic TableRow chunk.
    """
    lines = block.splitlines()
    if not any(line.strip() == "[TABLES]" for line in lines):
        return [block.strip()]
    body = [line for line in lines if line.strip() not in ("[TABLES]", "[/TABLES]") and line.strip()]
    if len(body) < 3 or not _is_table_separator(body[1]):
        return [block.strip()]
    rows = body[2:]
    if not any(_STRENGTH_RE.search(row) or _CERTAINTY_RE.search(row) for row in rows):
        return [block.strip()]
    return ["\n".join(["[TABLES]", body[0], body[1], row, "[/TABLES]"]) for row in rows]


def split_page(text: str, max_tokens: int = MAX_TOKENS_DEFAULT) -> list[str]:
    """Split narrative while keeping recommendations and markdown tables atomic."""
    chunks: list[str] = []
    for is_table, part in _table_parts(text):
        if is_table:
            if part.strip():
                chunks.extend(_split_table_recommendations(part))
        else:
            chunks.extend(_split_narrative(part, max_tokens))
    return chunks


def _split_grade_units(block: str) -> list[str]:
    """Keep each graded recommendation atomic; never merge adjacent grades."""
    if len(_STRENGTH_RE.findall(block)) <= 1:
        return [block]
    units: list[str] = []
    current: list[str] = []
    for sentence in _sentences(block):
        if current and _STRENGTH_RE.search(sentence) and any(_STRENGTH_RE.search(item) for item in current):
            units.append(" ".join(current).strip())
            current = [sentence]
        else:
            current.append(sentence)
        if _STRENGTH_RE.search(sentence):
            units.append(" ".join(current).strip())
            current = []
    if current:
        units.append(" ".join(current).strip())
    return [unit for unit in units if unit] or [block]


def _split_narrative(text: str, max_tokens: int = MAX_TOKENS_DEFAULT) -> list[str]:
    max_tokens = max(32, max_tokens)
    lines = text.splitlines()
    recommendation_regions = _recommendation_boxes(lines)
    blocks: list[str] = []
    current: list[str] = []
    i = 0

    while i < len(lines):
        if i in recommendation_regions:
            start, end = recommendation_regions[i]
            if current:
                blocks.append("\n".join(current).strip())
                current = []
            box = "\n".join(lines[start:end + 1]).strip()
            if box:
                blocks.append(box)
            i = end + 1
            continue

        line = lines[i]
        if HEADING_RE.match(line.strip()) and current:
            blocks.append("\n".join(current).strip())
            current = [line]
        else:
            current.append(line)
        i += 1

    if current:
        blocks.append("\n".join(current).strip())

    chunks: list[str] = []
    for block in (block for block in blocks if block):
        for unit in _split_grade_units(block):
            if _tok(unit) <= max_tokens or _RECOMMENDATION_HEADERS.match(unit.splitlines()[0].strip()):
                chunks.append(unit)
                continue
            buffer: list[str] = []
            for sentence in _sentences(unit):
                if buffer and _tok(" ".join(buffer + [sentence])) > max_tokens:
                    chunks.append(" ".join(buffer))
                    buffer = [sentence]
                else:
                    buffer.append(sentence)
            if buffer:
                chunks.append(" ".join(buffer))
    return chunks


def detect_front_matter(pages: list[dict[str, Any]]) -> list[dict[str, Any]]:
    """Return explicitly skipped front-matter pages and reasons.

    A page requires at least two indicators and must contain no recommendation
    or strength wording. Page length alone is never a reason to skip it.
    """
    skipped: list[dict[str, Any]] = []
    for page in pages:
        text = page["text"]
        indicators = [pattern for pattern in FRONT_MATTER_INDICATORS if re.search(pattern, text, re.I)]
        has_clinical_content = bool(
            _RECOMMENDATION_RE.search(text) or _STRENGTH_RE.search(text) or CLINICAL_CONTENT_RE.search(text)
        )
        if len(indicators) >= 2 and not has_clinical_content:
            skipped.append({
                "page": page["page"],
                "reason": "front_matter: " + ", ".join(indicators[:3]),
            })
    return skipped


async def profile_document(llm: AsyncLLM, pages: list[dict[str, Any]], guidance: str = "") -> dict[str, Any]:
    count = len(pages)
    if count <= 9:
        sample_pages = pages
    else:
        middle = max(3, count // 2 - 1)
        sample_pages = pages[:3] + pages[middle:middle + 3] + pages[-3:]
    sample = " ".join(page["text"] for page in sample_pages)[:6000]
    raw = await llm(PROFILE_PROMPT.format(sample=sample, guidance=guidance or "(none)"))
    profile = _json_object(raw, {
        "kind": "other",
        "structure": "mixed",
        "strategy": "split by headings then sentences",
        "notes": "profiling failed; using default strategy",
    })
    skipped = detect_front_matter(pages)
    profile["front_matter_pages"] = skipped
    return profile


def candidate_chunks(pages: list[dict[str, Any]], max_tokens: int) -> list[dict[str, Any]]:
    candidates: list[dict[str, Any]] = []
    for page in pages:
        for index, text in enumerate(split_page(page["text"], max_tokens)):
            candidates.append({
                "page": page["page"],
                "page_start": page["page"],
                "page_end": page["page"],
                "text": text,
                "index": index,
                "type": "clinical",
            })
    return candidates


async def critic(llm: AsyncLLM, chunks: list[dict[str, Any]], guidance: str = "",
                 batch_size: int = 12) -> list[dict[str, Any]]:
    decisions: list[dict[str, Any]] = []
    for start in range(0, len(chunks), batch_size):
        batch = chunks[start:start + batch_size]
        candidates = "\n".join(f"[{i}] {chunk['text'][:500]}" for i, chunk in enumerate(batch))
        raw = await llm(CRITIC_PROMPT.format(guidance=guidance or "(none)", candidates=candidates))
        parsed = _json_object(raw, {"decisions": []}).get("decisions", [])
        for decision in parsed:
            index = decision.get("i", -1)
            if isinstance(index, int) and 0 <= index < len(batch):
                decisions.append({
                    "global": start + index,
                    "action": decision.get("action", "KEEP"),
                    "note": decision.get("note", ""),
                })
        await __yield_control()

    decided = {decision["global"] for decision in decisions}
    decisions.extend(
        {"global": index, "action": "KEEP", "note": "critic default"}
        for index in range(len(chunks)) if index not in decided
    )
    return decisions


async def __yield_control() -> None:
    # Imported lazily to keep deterministic helpers import-light.
    import asyncio
    await asyncio.sleep(0)


def _atomic_for_merge(chunk: dict[str, Any]) -> bool:
    metadata = chunk.get("metadata") if isinstance(chunk.get("metadata"), dict) else {}
    kind = classify_chunk(chunk.get("text", ""), metadata.get("section_path", ""))
    return kind in {"recommendation", "summary", "table_row"} or bool(_STRENGTH_RE.search(chunk.get("text", "")))


def apply_decisions(chunks: list[dict[str, Any]], decisions: list[dict[str, Any]]) -> list[dict[str, Any]]:
    by_index = {decision["global"]: decision for decision in decisions}
    output: list[dict[str, Any]] = []
    for index, chunk in enumerate(chunks):
        decision = by_index.get(index, {"action": "KEEP", "note": ""})
        can_merge = (
            decision.get("action") == "MERGE_WITH_PREVIOUS"
            and output
            and output[-1]["page"] == chunk["page"]
            and not _atomic_for_merge(output[-1])
            and not _atomic_for_merge(chunk)
        )
        if can_merge:
            output[-1]["text"] += "\n\n" + chunk["text"]
            output[-1]["critic_note"] = decision.get("note", "")
            continue
        item = dict(chunk)
        item["critic_action"] = decision.get("action", "KEEP")
        item["critic_note"] = decision.get("note", "")
        output.append(item)
    return output


async def enrich(llm: AsyncLLM, chunk: dict[str, Any]) -> None:
    raw = await llm(ENRICH_PROMPT.format(text=chunk["text"][:1500]))
    supplied = _json_object(raw, {
        "section_path": "",
        "strength": "none",
        "population": "",
        "status": "unknown",
        "topic": "",
    })
    chunk["metadata"] = structured_metadata(chunk, supplied)
    chunk["kind"] = chunk["metadata"]["kind"]


def is_verbatim(chunk_text: str, page_text: str) -> bool:
    chunk = normalize_for_match(chunk_text)
    return bool(chunk) and chunk in normalize_for_match(page_text)


def _table_chunk_verbatim(chunk_text: str, page_text: str) -> bool:
    """A split table row reuses exact header/row lines from one page table."""
    if "[TABLES]" not in chunk_text:
        return False
    lines = [line.strip() for line in chunk_text.splitlines()
             if line.strip() and line.strip() not in ("[TABLES]", "[/TABLES]")
             and not _is_table_separator(line)]
    if len(lines) < 2:
        return False
    page = normalize_for_match(page_text)
    return all(normalize_for_match(line) in page for line in lines)


def validate_chunks(chunks: list[dict[str, Any]], pages: list[dict[str, Any]]) -> None:
    page_text = {page["page"]: page["text"] for page in pages}
    for chunk in chunks:
        source_text = page_text.get(chunk["page"], "")
        chunk["verbatim"] = is_verbatim(chunk["text"], source_text)
        if not chunk["verbatim"] and _table_chunk_verbatim(chunk["text"], source_text):
            chunk["verbatim"] = True
            chunk["table_split"] = True
        if not chunk["verbatim"]:
            chunk["validation_error"] = "chunk text is not a verbatim slice of the source page"


async def chunk_document(llm: AsyncLLM, text: str, guidance: str = "",
                         max_tokens: int = MAX_TOKENS_DEFAULT,
                         preview_pages: int = 0) -> dict[str, Any]:
    """Run the complete async chunking pipeline."""
    pages = parse_pages(text)
    if preview_pages > 0:
        pages = pages[:preview_pages]

    profile = await profile_document(llm, pages, guidance)
    skipped_pages = profile.get("front_matter_pages", [])
    skipped_numbers = {item["page"] for item in skipped_pages}
    clinical_pages = [page for page in pages if page["page"] not in skipped_numbers]

    candidates = candidate_chunks(clinical_pages, max_tokens)
    decisions = await critic(llm, candidates, guidance)
    chunks = apply_decisions(candidates, decisions)
    for chunk in chunks:
        await enrich(llm, chunk)
        await __yield_control()

    validate_chunks(chunks, clinical_pages)
    accepted = [chunk for chunk in chunks if chunk.get("verbatim", False)]
    failed = [chunk for chunk in chunks if not chunk.get("verbatim", False)]
    structured_kinds: dict[str, int] = {}
    for chunk in accepted:
        kind = str((chunk.get("metadata") or {}).get("kind") or "unknown")
        structured_kinds[kind] = structured_kinds.get(kind, 0) + 1

    return {
        "profile": profile,
        "pages": [
            {
                "page": page["page"],
                "chars": len(page["text"]),
                "front_matter": page["page"] in skipped_numbers,
            }
            for page in pages
        ],
        "chunks": accepted,
        "failed_chunks": failed,
        "skipped_pages": skipped_pages,
        "stats": {
            "candidates": len(candidates),
            "accepted": len(accepted),
            "failed": len(failed),
            "skipped_pages": len(skipped_pages),
            "structuredKinds": structured_kinds,
        },
        "preview": preview_pages > 0,
    }
