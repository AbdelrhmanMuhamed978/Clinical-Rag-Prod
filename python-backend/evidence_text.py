"""One normalization path for PDF parsing, chunking and quote verification.

The same visible text must be used everywhere.  Keep this module dependency-free
so the Colab parser, local chunker and pipeline verifier can all import it.
"""
from __future__ import annotations

import html
import re
import unicodedata

LIGATURES = str.maketrans({
    "ﬀ": "ff", "ﬁ": "fi", "ﬂ": "fl", "ﬃ": "ffi", "ﬄ": "ffl", "ﬅ": "st", "ﬆ": "st",
})

# A dash that ended a wrapped line often arrives as "word– next".  Join only
# when there was no space before the dash, so ordinary "word - word" separators
# and ranges keep their spacing.
_WRAPPED_DASH = re.compile(r"(?<=\w)[\u2013\u2014-][ \t]*(?:\n[ \t]*)?(?=[a-z])")


def decode_entities(value: str) -> str:
    """Decode HTML entities until stable (at most three passes)."""
    text = str(value)
    for _ in range(3):
        decoded = html.unescape(text)
        if decoded == text:
            break
        text = decoded
    return text


def normalize_evidence_text(value: str) -> str:
    """Normalize extraction artifacts while preserving visible words.

    This is intentionally conservative: it does not rewrite clinical terms,
    numbers or units.  It decodes table entities, normalizes ligatures/soft
    hyphens, joins a hyphenated line wrap and a dash at a wrapped line, then
    collapses repeated spaces.  Newlines are retained for page/paragraph use.
    """
    text = decode_entities(value)
    text = unicodedata.normalize("NFC", text).replace("\r\n", "\n").replace("\r", "\n")
    text = text.translate(LIGATURES).replace("\u00a0", " ").replace("\u00ad", "-")
    # Existing parser behaviour: a line-end hyphen before lowercase text is a
    # word continuation and is removed with the newline.
    text = re.sub(r"(?<=\w)-[ \t]*\n(?=[a-z])", "", text)
    text = _WRAPPED_DASH.sub(lambda match: match.group(0)[0], text)
    text = re.sub(r"[ \t]+", " ", text)
    text = re.sub(r"\n{3,}", "\n\n", text)
    return text.strip()


def normalize_for_match(value: str) -> str:
    """Whitespace-collapsed form used by verbatim checks and quote matching."""
    return re.sub(r"\s+", " ", normalize_evidence_text(value)).strip()


_DASHES = "\u2010\u2011\u2012\u2013\u2014\u2015\u2212"
_DASH_SPACE = re.compile(rf"\s*[{_DASHES}]\s*")
_MARKER_PATTERNS = re.compile(r"[^\W_]+$", re.UNICODE)


def _marker_patterns(footnote_markers) -> set[tuple[str, str]]:
    """Known parser markers as (left word, marker letter) patterns.

    The parser records the visible context around every removed superscript.
    Using that context avoids guessing that any trailing a-d letter is a
    footnote marker.  A marker is only removable when the recorded word is
    followed by a non-alphanumeric boundary, which is how a superscript span
    was separated from the visible word.
    """
    patterns: set[tuple[str, str]] = set()
    for marker in footnote_markers or []:
        if not isinstance(marker, dict):
            continue
        letter = str(marker.get("marker", "")).strip()
        context = str(marker.get("context", ""))
        offset = marker.get("offset")
        if len(letter) != 1 or not letter.isalpha() or not context or not isinstance(offset, int):
            continue
        start = max(0, offset - 40)
        position = offset - start
        if position < 0 or position > len(context):
            continue
        left, right = context[:position], context[position:]
        if right[:1].isalnum():
            continue
        word = _MARKER_PATTERNS.search(left)
        if not word or len(word.group(0)) < 3:
            continue
        patterns.add((word.group(0).casefold(), letter.casefold()))
    return patterns


def _remove_known_markers(value: str, patterns: set[tuple[str, str]]) -> str:
    text = value
    for word, letter in patterns:
        text = re.sub(
            rf"(?<!\w)({re.escape(word)})\s*{re.escape(letter)}(?=\W|$)",
            r"\1",
            text,
            flags=re.IGNORECASE,
        )
    return text


def canonical_text(value: str, footnote_markers=None) -> str:
    """Canonical comparison text: formatting only, never meaning.

    This is the single canonical view used for quote location.  It applies
    Unicode NFKC (so ``mm³`` and ``mm3`` compare equal), decodes entities,
    removes parser-recorded footnote markers, unifies dashes, ignores
    whitespace/punctuation differences and keeps every word and number.
    Negation, strength, scope and number checks use their own views and are
    never bypassed by this function.
    """
    text = str(value)
    text = decode_entities(text)
    text = unicodedata.normalize("NFKC", text).replace("\r\n", "\n").replace("\r", "\n")
    text = _remove_known_markers(text, _marker_patterns(footnote_markers))
    text = normalize_evidence_text(text)
    text = text.casefold()
    # Preserve decimal points between digits (and normalize a leading .5),
    # while dropping sentence punctuation.
    text = re.sub(r"(?<![\w\d])\.(?=\d)", "0.", text)
    text = re.sub(r"(?<!\d)\.|\.(?!\d)", " ", text)
    text = re.sub(r"(?<=\d),(?=\d{3}\b)", "", text)
    text = re.sub(r"['\u2018\u2019\u201c\u201d`]", "", text)
    text = re.sub(r"[;:,!?()\[\]{}*|\\&_#@\u2026]", " ", text)
    text = _DASH_SPACE.sub("-", text)
    # A hyphen lost or added at a PDF line wrap is formatting, not meaning.
    # Compare high-dose and highdose identically while keeping digit ranges
    # such as 100-200 and number-unit pairs such as 24-hour unchanged.
    text = re.sub(r"(?<=[a-z])-(?=[a-z])", "", text)
    # Keep comparison/arithmetic symbols: <100 and >100 are not formatting.
    text = re.sub(r"\s*([<>=+\u00b1~\u2264\u2265^])\s*", r"\1", text)
    text = re.sub(r"\s*([/%])\s*", r"\1", text)
    text = re.sub(r"\s+", " ", text)
    return text.strip()


def formatting_view(value: str, footnote_markers=None) -> str:
    """Loose view used only to classify a rejected quote.

    It removes whitespace and a possible fused one-letter marker, but keeps
    comparison/arithmetic symbols so a changed `<`, `>` or `=` is still a
    meaning change, not a formatting mismatch.  It never accepts a quote.
    """
    text = canonical_text(value, footnote_markers)
    text = re.sub(r"(?<=[a-z])[abcd](?=[^a-z]|$)", "", text)
    return re.sub(r"\s+", "", text)
