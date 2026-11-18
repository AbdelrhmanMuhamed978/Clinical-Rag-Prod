"""Load exact recorded source text and verify recorded model drafts offline."""
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

import pipeline as P

ROOT = Path(__file__).parent


def fixtures():
    contexts = json.loads((ROOT / "contexts.json").read_text(encoding="utf-8"))
    for filename in ("a.json", "b.json", "c.json", "postfix-4b.json"):
        capture = json.loads((ROOT / filename).read_text(encoding="utf-8"))
        assert capture["synthetic"] is True
        for problem in capture["problems"]:
            sources = {tag: contexts[key] | {"id":tag} for tag, key in problem["sources"].items()}
            stems = set(problem["topic"]) if "topic" in problem else P.topic_stems(problem["title"], whole=capture.get("retrievalRerun") is False)
            yield capture, problem, sources, stems


def replay(problem, sources, stems):
    output = problem["output"]
    if output["truncated"] or output["finish_reason"] in {"length", "max_tokens"}:
        counters = P.empty_diagnostics()
        counters["drafts"] = 1
        counters["reasons"]["truncated"] = 1
        return {"items":[], "unverified":[], "diagnostics":counters}
    return P.verify_draft(output["response"], sources, stems)


if __name__ == "__main__":
    for capture, problem, sources, stems in fixtures():
        result = replay(problem, sources, stems)
        print(json.dumps({"observation":capture["observation"], "problemIndex":problem["index"],
                          "diagnostics":result["diagnostics"]}))
