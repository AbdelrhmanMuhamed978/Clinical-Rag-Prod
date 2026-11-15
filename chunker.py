"""Single-source async hybrid chunker for the clinical RAG platform.

The model is injected as ``await llm(prompt)``. Structure, validation, front-matter
detection and page accounting stay deterministic. Model calls only profile,
criticise boundaries and extract metadata; chunk text is never rewritten.
"""

import json
import re
from collections.abc import Awaitable, Callable
from typing import Any

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
_RECOMMENDATION_RE = re.compile(r"\brecommend\w*\b", re.I)
_STRENGTH_RE = re.compile(r"\b(strong|weak|conditional)\s+recommendation\b", re.I)

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
        return [{"page": 1, "text": text.strip()}]
    if parts[0].strip():
        pages.append({"page": 1, "text": parts[0].strip()})
    for i in range(1, len(parts), 2):
        page = int(parts[i])
        body = parts[i + 1] if i + 1 < len(parts) else ""
        pages.append({"page": page, "text": body.strip()})
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


def split_page(text: str, max_tokens: int = MAX_TOKENS_DEFAULT) -> list[str]:
    """Split headings/sentences while keeping recommendation boxes atomic."""
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
        if _tok(block) <= max_tokens or _RECOMMENDATION_HEADERS.match(block.splitlines()[0].strip()):
            chunks.append(block)
            continue
        buffer: list[str] = []
        for sentence in _sentences(block):
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
        has_clinical_recommendation = bool(_RECOMMENDATION_RE.search(text) or _STRENGTH_RE.search(text))
        if len(indicators) >= 2 and not has_clinical_recommendation:
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


def apply_decisions(chunks: list[dict[str, Any]], decisions: list[dict[str, Any]]) -> list[dict[str, Any]]:
    by_index = {decision["global"]: decision for decision in decisions}
    output: list[dict[str, Any]] = []
    for index, chunk in enumerate(chunks):
        decision = by_index.get(index, {"action": "KEEP", "note": ""})
        can_merge = (
            decision.get("action") == "MERGE_WITH_PREVIOUS"
            and output
            and output[-1]["page"] == chunk["page"]
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
    chunk["metadata"] = _json_object(raw, {
        "section_path": "",
        "strength": "none",
        "population": "",
        "status": "unknown",
        "topic": "",
    })


def is_verbatim(chunk_text: str, page_text: str) -> bool:
    def normalise(value: str) -> str:
        return re.sub(r"\s+", " ", value).strip()
    chunk = normalise(chunk_text)
    return bool(chunk) and chunk in normalise(page_text)


def validate_chunks(chunks: list[dict[str, Any]], pages: list[dict[str, Any]]) -> None:
    page_text = {page["page"]: page["text"] for page in pages}
    for chunk in chunks:
        chunk["verbatim"] = is_verbatim(chunk["text"], page_text.get(chunk["page"], ""))
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
        },
        "preview": preview_pages > 0,
    }
