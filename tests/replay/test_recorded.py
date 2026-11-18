import json

import pytest

from .harness import fixtures, replay


@pytest.mark.parametrize("capture,problem,sources,stems", list(fixtures()), ids=["a-1", "b-1", "b-2", "b-3", "c-1", "postfix-a", "postfix-c"])
def test_recorded_synthetic_draft_counts_and_closed_reason_histograms(capture, problem, sources, stems):
    result = replay(problem, sources, stems)
    assert len(result["items"]) == problem["expected"]["verified"]
    assert {reason:count for reason,count in result["diagnostics"]["reasons"].items() if count} == problem["expected"]["reasons"]
    assert capture["synthetic"] and '"quote":' not in json.dumps(result["diagnostics"])
