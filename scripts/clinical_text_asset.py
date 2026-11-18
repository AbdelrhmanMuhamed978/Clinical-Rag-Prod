"""Reproduce the pinned Unicode asset without changing repository files.

Requires Python's Unicode 15.1.0 database. Print a patch with --patch; otherwise
compare the checked-in artifact. Review any Unicode upgrade as a policy change.
"""
import argparse
import json
import unicodedata
from pathlib import Path


def asset():
    if unicodedata.unidata_version != "15.1.0":
        raise SystemExit("Use a Python runtime with Unicode 15.1.0 to reproduce this asset.")
    simple, expansions, excluded, whitespace = [], {}, [], []
    for value in range(0x110000):
        char = chr(value)
        folded = char.casefold()
        if folded != char:
            if len(folded) == 1:
                simple.append((value, ord(folded) - value))
            else:
                expansions[str(value)] = folded
        if unicodedata.category(char).startswith("C"):
            if excluded and excluded[-1][1] == value - 1:
                excluded[-1][1] = value
            else:
                excluded.append([value, value])
        # Unicode White_Space; unlike Python str.isspace, excludes C0 separators.
        if value in (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8232, 8233, 8239, 8287, 12288) or 8192 <= value <= 8202:
            whitespace.append(value)
    ranges, i = [], 0
    while i < len(simple):
        start, delta = simple[i]
        step = 1
        if i + 1 < len(simple) and simple[i + 1][1] == delta and simple[i + 1][0] - start in (1, 2):
            step = simple[i + 1][0] - start
        end, j = start, i + 1
        while j < len(simple) and simple[j][0] == end + step and simple[j][1] == delta:
            end = simple[j][0]
            j += 1
        ranges.append([start, end, step, delta])
        i = j
    return {"unicode_version": unicodedata.unidata_version, "casefold_ranges": ranges,
            "casefold_expansions": expansions, "excluded_name_ranges": excluded, "whitespace": whitespace}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--patch", action="store_true")
    args = parser.parse_args()
    expected = asset()
    if args.patch:
        content = "{\n" + ",\n".join("  " + json.dumps(key) + ": " + json.dumps(value, ensure_ascii=True, separators=(",", ":"))
                                          for key, value in expected.items()) + "\n}\n"
        print("*** Begin Patch\n*** Add File: python-backend/clinical-text.json\n" + "\n".join("+" + line for line in content.splitlines()) + "\n*** End Patch")
    else:
        actual = json.loads((Path(__file__).resolve().parents[1] / "python-backend" / "clinical-text.json").read_text(encoding="utf-8"))
        if actual != expected:
            raise SystemExit("clinical-text.json does not match the pinned Unicode data.")
        print(f"Unicode {expected['unicode_version']}: asset verified; {len(expected['casefold_ranges'])} folding ranges, "
              f"{len(expected['casefold_expansions'])} expansions, {len(expected['excluded_name_ranges'])} excluded ranges.")
