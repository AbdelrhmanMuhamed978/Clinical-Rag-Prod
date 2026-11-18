import json

import pytest

from .harness import ROOT, fixtures, replay


DATA = json.loads((ROOT / "comparison.json").read_text(encoding="utf-8"))
BASELINE = {f"{capture['observation']}-{problem['index']}": (problem, sources, stems)
            for capture, problem, sources, stems in fixtures() if capture["observation"] in {"a", "b", "c"}}
REVISED = {name: (problem, sources, stems) for name, (_, problem, sources, stems) in zip(
    ("a-1", "c-1"), (value for value in fixtures() if value[0]["observation"] == "postfix-4b"))}


def comparison_pairs():
    for run in DATA["runs"]:
        for pair in run["pairs"]:
            problem, sources, stems = (BASELINE if run["bundle"] == "captured" else REVISED)[pair["id"]]
            output = BASELINE[pair["outputRef"]][0]["output"] if "outputRef" in pair else pair["output"]
            yield run, pair, problem | {"output": output}, sources, stems


@pytest.mark.parametrize("run,pair,problem,sources,stems", list(comparison_pairs()))
def test_real_same_gpu_comparison_replays_without_model_or_services(run, pair, problem, sources, stems):
    assert DATA["synthetic"] and DATA["retrievalRerun"] is False and DATA["gpu"] == "Tesla T4"
    assert run["model"] in {"Qwen/Qwen3-4B-Instruct-2507", "Qwen/Qwen3-8B-AWQ"}
    assert len(DATA["payloadSha256"][run["bundle"]][pair["id"]]) == 64
    counters = replay(problem, sources, stems)["diagnostics"]
    assert counters["drafts"] == 1
    assert counters["candidates"] == pair["expected"]["candidates"]
    assert counters["verified"] == pair["expected"]["verified"]
    assert {key: value for key, value in counters["reasons"].items() if value} == pair["expected"]["reasons"]
