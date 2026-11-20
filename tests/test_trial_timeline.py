"""docs/trial-timeline.html is what `uv run trial-timeline` makes from the task files now, and its outcome rules are the C#'s:
tests/fixtures/outcomes.json, which OutcomeTableTests.cs writes from TrialRow.cs and TrialDraw (OUTCOMES_REGEN=1) and fails
on whenever those rules change."""

import json
from pathlib import Path

import pytest

from ucl_open_coen_dome_decision_making import trial_timeline as tt


def test_the_committed_page_matches_the_task_files():
    assert tt.OUTPUT.exists() and tt.OUTPUT.read_text(encoding="utf-8") == tt.render(), (
        "docs/trial-timeline.html is out of date with config/task-discrimination-*.yml: run `uv run trial-timeline`"
    )


OUTCOMES = json.loads((Path(__file__).parent / "fixtures" / "outcomes.json").read_text(encoding="utf-8"))


@pytest.mark.parametrize("row", OUTCOMES["outcomes"], ids=lambda r: "-".join(str(r[k]) for k in ("taskType", "target", "choice", "autoReward", "heads", "penaliseProbes")))
def test_outcomes_are_the_csharps(row):
    # tests/fixtures/outcomes.json is TrialRow.cs's rules, written by OutcomeTableTests.cs (which fails when the C# changes).
    choice = None if row["choice"] == "noResponse" else row["choice"]
    out = tt.outcome(row["taskType"], row["target"], choice, row["autoReward"])
    got = (out, tt.rewarded(row["target"], choice, row["autoReward"], row["heads"], out),
           tt.penalised(row["target"], choice, row["autoReward"], row["heads"], out, row["penaliseProbes"]))
    assert got == (row["outcome"], row["rewarded"], row["penalised"]), "trial_timeline.py's copy of TrialRow.cs's rules has drifted"


@pytest.mark.parametrize("task_type", sorted(OUTCOMES["autoRewardPossible"]))
def test_auto_reward_targets_are_the_csharps(task_type):
    # Which targets TrialDraw auto-rewards, by OutcomeTableTests.cs's draws.
    for target, possible in OUTCOMES["autoRewardPossible"][task_type].items():
        assert tt.auto_reward_possible(task_type, target) == possible, f"{task_type} {target}"


def test_a_conditions_own_timeout_shows_on_its_card(tmp_path):
    # A stage that sets a condition's own penalty timeout: that card's penalised row carries it, the others the task's.
    text = (tt.ROOT / "config" / "task-discrimination-go-no-go.yml").read_text(encoding="utf-8")
    text = text.replace("set: { target: noGo, visualStimulus.contrasts: [0] }",
                        "set: { target: noGo, visualStimulus.contrasts: [0], penalty.incorrect.timeoutSeconds: 7.0 }")
    path = tmp_path / "task-discrimination-go-no-go.yml"
    path.write_text(text, encoding="utf-8")
    nogo = next(c for c in tt.task_timeline(path)["conditions"] if c["target"] == "noGo")
    assert [r["penalty"]["timeout"] for v in nogo["variants"] for r in v["rows"] if r["penalty"]] == [7.0]
