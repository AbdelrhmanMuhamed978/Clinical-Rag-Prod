import pytest

import config as Cfg


@pytest.mark.parametrize("settings", [
    {"minRerank": 0.0, "topK": 1, "candidates": 5, "queriesPerProblem": 1, "maxChunks": 1},
    {"minRerank": 1.0, "topK": 20, "candidates": 100, "queriesPerProblem": 5, "maxChunks": 20},
])
def test_pipeline_accepts_the_workflow_servers_inclusive_retrieval_limits(settings):
    cfg = Cfg.Config.from_dict({"retrieval": settings})
    for key, value in settings.items():
        assert cfg.get(f"retrieval.{key}") == value


@pytest.mark.parametrize("settings", [
    {"minRerank": -0.01}, {"minRerank": 1.01}, {"minRerank": "0.3"},
    {"topK": 0}, {"topK": 21}, {"topK": True},
    {"candidates": 4}, {"candidates": 101}, {"candidates": None},
    {"queriesPerProblem": 0}, {"queriesPerProblem": 6},
])
def test_pipeline_rejects_the_same_invalid_retrieval_values_as_workflow(settings):
    with pytest.raises(ValueError):
        Cfg.Config.from_dict({"retrieval": settings})


def test_defaults_and_merges_preserve_extra_keys_without_unlocking_the_verifier():
    cfg = Cfg.Config.from_dict({
        "retrieval": {"topK": 10, "extra": {"keep": [1, 2]}},
        "safety": {"verifierLocked": False}, "future": {"keep": True},
    })
    assert cfg.get("retrieval.candidates") == 30
    assert cfg.get("retrieval.extra.keep") == [1, 2]
    assert cfg.get("future.keep") is True
    assert cfg.get("safety.verifierLocked") is True


def test_shared_placeholder_validation_preserves_json_braces_and_ignores_unknown_tokens():
    prompt = 'Read {case}; at most {n}. JSON: {{"problems":[]}}. Literal: {future}.'
    assert Cfg.validate_prompt("plan", prompt) is None
    assert Cfg.render_prompt("plan", prompt, case="synthetic case", n=2) == (
        'Read synthetic case; at most 2. JSON: {{"problems":[]}}. Literal: {future}.'
    )
