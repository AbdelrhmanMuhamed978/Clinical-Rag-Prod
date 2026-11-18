"""Pinned text identity and portable validation shared with the API and editor.

Names use NFKC, Unicode 15.1 full casefold, and Unicode White_Space collapse.
Lengths are UTF-16 code units, matching SQL Server, .NET and HTML maxlength.
"""
import bisect
import json
import re
import unicodedata
from pathlib import Path

ASSET = json.loads((Path(__file__).parent / "clinical-text.json").read_text(encoding="utf-8"))
WHITESPACE = frozenset(ASSET["whitespace"])
_EXCLUDED = ASSET["excluded_name_ranges"]
_STARTS = [start for start, _ in _EXCLUDED]
_FOLD = {int(key): value for key, value in ASSET["casefold_expansions"].items()}
for _start, _end, _step, _delta in ASSET["casefold_ranges"]:
    _FOLD.update((value, chr(value + _delta)) for value in range(_start, _end + 1, _step))


def utf16_length(value):
    return len(value.encode("utf-16-le", errors="surrogatepass")) // 2


def has_text(value):
    return any(ord(char) not in WHITESPACE for char in value)


def excluded_name_char(char):
    value = ord(char)
    index = bisect.bisect_right(_STARTS, value) - 1
    return index >= 0 and value <= _EXCLUDED[index][1]


def valid_name(value):
    return (isinstance(value, str) and has_text(value) and utf16_length(value) <= 128
            and not any(excluded_name_char(char) for char in value))


def casefold(value):
    return value.translate(_FOLD)


def alias(value):
    folded = casefold(unicodedata.normalize("NFKC", value))
    # Do not use runtime-specific lower()/split()/\s definitions for name identity.
    words, word = [], []
    for char in folded:
        if ord(char) in WHITESPACE:
            if word:
                words.append("".join(word))
                word = []
        else:
            word.append(char)
    if word:
        words.append("".join(word))
    return " ".join(words)


def fold_with_spans(value):
    """Fold already-normalized text and map expansions back to displayed input."""
    text, spans = [], []
    for index, char in enumerate(value):
        folded = _FOLD.get(ord(char), char)
        text.append(folded)
        spans.extend([(index, index + 1)] * len(folded))
    return "".join(text), spans


def _ipv4(host):
    parts = host.split(".")
    return len(parts) == 4 and all(re.fullmatch(r"0|[1-9][0-9]{0,2}", part) and int(part) <= 255 for part in parts)


def _ipv6(host):
    if "." in host:
        tail = host.rsplit(":", 1)[-1]
        if not _ipv4(tail):
            return False
        host = host[:-len(tail)] + "0:0"
    halves = host.split("::")
    if len(halves) > 2:
        return False
    parts = [part for half in halves if half for part in half.split(":")]
    return ((len(parts) < 8 if len(halves) == 2 else len(parts) == 8)
            and all(re.fullmatch(r"[0-9A-Fa-f]{1,4}", part) for part in parts))


def http_source_url(value):
    """Unambiguous HTTP(S) syntax, not a reachability or clinical-evidence check.

    Require //, ASCII DNS/IP hosts (punycode for international domains), valid
    percent escapes and explicit decimal ports. Never repair malformed input.
    """
    if (not isinstance(value, str) or not value or utf16_length(value) > 2048
            or any(ord(char) in WHITESPACE or excluded_name_char(char) for char in value)
            or "\\" in value or re.search(r"%(?![0-9A-Fa-f]{2})", value)):
        return False
    match = re.match(r"https?://([^/?#]+)", value, re.I | re.ASCII)
    if not match:
        return False
    authority = match[1]
    if "@" in authority or "%" in authority:
        return False
    if authority.startswith("["):
        host_match = re.fullmatch(r"\[([^\]]+)\](?::([0-9]{1,5}))?", authority)
        if not host_match or not _ipv6(host_match[1]):
            return False
    else:
        host_match = re.fullmatch(r"([^:]+)(?::([0-9]{1,5}))?", authority)
        if not host_match:
            return False
        host = host_match[1].removesuffix(".")
        labels = host.split(".")
        if (len(host) > 253 or not all(re.fullmatch(r"[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?", label) for label in labels)
                or (re.fullmatch(r"[0-9]+|0[xX][0-9A-Fa-f]+", labels[-1]) and not _ipv4(host))):
            return False
    return host_match[2] is None or int(host_match[2]) <= 65535


def portable_regex_problem(pattern):
    """Inspect a small grammar; never execute an editor regex in JavaScript."""
    if pattern is None or pattern == "":
        return None
    if not isinstance(pattern, str) or utf16_length(pattern) > 512:
        return "Link terms must be a regular expression of at most 512 characters."
    invalid = "Use portable groups, classes, escapes and quantifiers (repeat bounds up to 10000); escape literal brackets and braces."
    escapes = r"bBdDsSwWnrtfv\.*+?^$[]{}()|/-"
    groups, in_class, content, repeatable, i = 0, False, False, False, 0
    class_set, class_range = False, False
    while i < len(pattern):
        char = pattern[i]
        if char == "\\":
            i += 1
            if i == len(pattern) or pattern[i] not in escapes or in_class and pattern[i] in "bB":
                return invalid
            if in_class and class_range and pattern[i] in "dDsSwW":
                return invalid
            class_set, class_range = in_class and pattern[i] in "dDsSwW", False
            content = True
            repeatable = pattern[i] not in "bB"
        elif in_class:
            if char == "[" or pattern[i:i + 2] in ("&&", "~~", "||", "--"):
                return invalid
            if char == "]":
                if not content:
                    return invalid
                in_class, repeatable = False, True
            else:
                range_endpoint = char == "-" and content and pattern[i + 1:i + 2] != "]"
                if range_endpoint and class_set:  # Python rejects shorthand sets as range endpoints.
                    return invalid
                class_range, class_set = range_endpoint, False
                content = True
        elif char == "[":
            in_class, content = True, False
            class_set, class_range = False, False
            if pattern[i + 1:i + 2] == "^":
                i += 1
        elif char == "(":
            if pattern[i + 1:i + 2] == "?":
                if pattern[i + 2:i + 3] != ":":
                    return invalid
                i += 2
            groups += 1
            repeatable = False
        elif char == ")":
            if not groups:
                return invalid
            groups -= 1
            repeatable = True
        elif char in "|^$":
            repeatable = False
        elif char in "*+?{":
            if not repeatable:
                return invalid
            if char == "{":
                end = pattern.find("}", i + 1)
                count = re.fullmatch(r"([0-9]{1,5})(?:,([0-9]{0,5}))?", pattern[i + 1:end]) if end >= 0 else None
                if not count:
                    return invalid
                lower, upper = int(count[1]), int(count[2]) if count[2] else None
                if lower > 10000 or upper is not None and (upper > 10000 or upper < lower):
                    return invalid
                i = end
            if pattern[i + 1:i + 2] == "?":
                i += 1
            repeatable = False
        elif char in "]}":
            return invalid
        else:
            repeatable = True
        i += 1
    return invalid if groups or in_class else None
