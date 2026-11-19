"""analysis/modules/loading_functions.py: which sessions are this workflow's, what new-draw accuracy counts, and the
trial weights a session ran with."""

import json
import sys
from pathlib import Path

import pytest

pd = pytest.importorskip("pandas")
ANALYSIS = Path(__file__).parents[1] / "analysis"
sys.path.insert(0, str(ANALYSIS))
from modules import loading_functions as lf  # noqa: E402

CONDITIONS = [{"trialType": "visual", "weight": 3, "pairs": [], "set": {"contrasts": [0.5, 1], "reward.rewardSizeUl": [2, 3]}},
              {"trialType": "visual", "weight": 1, "pairs": [], "set": {"reward.rewardSizeUl": 2.5}},
              {"trialType": "auditory", "weight": 0, "pairs": [], "set": {}}]


def session(root: Path, name: str, repo: str, task: dict | None = None) -> Path:
    s = root / "sub-GN1" / name
    (s / "Settings").mkdir(parents=True)
    (s / "manifest.json").write_text(json.dumps({"source": {"repo": repo}}), encoding="utf-8")
    settings(s, task or {"taskType": "localization", "trialConditions": CONDITIONS})
    return s


def settings(s: Path, task: dict) -> None:
    # Settings_Task as run, each condition's trialType (given here beside it) only in its Settings_Combinations rows (P9).
    conditions = task.get("trialConditions", [])
    rows = [{"trialCombinationId": i, "trialConditionId": i, "trialType": c["trialType"]} for i, c in enumerate(conditions)]
    if "trialConditions" in task:
        task = {**task, "trialConditions": [{k: v for k, v in c.items() if k != "trialType"} for c in conditions]}
    (s / "Settings" / "Settings_Task_2026-11-16T10-00-00.json").write_text(json.dumps({"taskParameters": task}), encoding="utf-8")
    (s / "Settings" / "Settings_Combinations_2026-11-16T10-00-00.json").write_text(json.dumps({"rows": rows}), encoding="utf-8")


def test_this_repo_s_sessions_count_and_av_dome_s_and_lookalikes_do_not(tmp_path):
    # A substring match matched a lookalike repo.
    ours = session(tmp_path, "ses-001_a", "https://github.com/Coen-Lab/coen-dome-decision-making.git")
    session(tmp_path, "ses-002_b", "https://github.com/ucl-open/av-dome")
    session(tmp_path, "ses-004_d", "https://github.com/x/coen-dome-decision-making-2p")
    assert lf.find_sessions(tmp_path) == [ours]


def test_curriculum_stage_reads_curriculum(tmp_path):
    stage = {"id": 3, "code": "S03A", "version": "k7f2", "path": "A", "label": "Conflict Added", "sha256": "abc"}
    s = session(tmp_path, "ses-100_x", "coen-dome-decision-making", {"trialConditions": CONDITIONS, "curriculum": stage})
    assert lf.curriculum_stage(s) == stage
    assert lf.curriculum_stage(session(tmp_path, "ses-101_x", "coen-dome-decision-making")) is None


def test_an_old_task_file_fails_loudly(tmp_path):
    # Clean break: a task file without taskParameters.trialConditions (trialWeights, trainingStages) is refused, never read.
    s = session(tmp_path, "ses-001_a", "coen-dome-decision-making", {"trialWeights": {"visual": 1}})
    with pytest.raises(KeyError):
        lf.trial_weights(s)


def test_trial_conditions_and_weights_read_a_trial_conditions_session(tmp_path):
    # Settings_Task's set values are YAML text (TrialDrawTests.TheConditionsSetRoundTrips), read back typed.
    s = session(tmp_path, "ses-001_a", "coen-dome-decision-making")
    read = lf.trial_conditions(s)
    assert read[0]["set"] == {"contrasts": [0.5, 1], "reward.rewardSizeUl": [2, 3]} and read[1]["set"] == {"reward.rewardSizeUl": 2.5}
    assert lf.trial_weights(s) == {"visual": 4, "auditory": 0}
    empty = tmp_path / "sub-GN1" / "ses-002_b"
    (empty / "Settings").mkdir(parents=True)
    assert lf.trial_conditions(empty) is None and lf.trial_weights(empty) is None


def test_conditions_edges_signed_azimuths_no_set_empty_list_and_zero_weights(tmp_path):
    s = session(tmp_path, "ses-001_a", "coen-dome-decision-making")

    def write(conditions):
        settings(s, {"taskType": "localization", "trialConditions": conditions})

    write([{"trialType": "visual", "weight": 1, "set": {"visualStimulus.initialAzimuthsDeg": [-75, 60]}}, {"trialType": "visual", "weight": 0}, {"trialType": "auditory", "weight": 2, "set": None}])
    read = lf.trial_conditions(s)
    assert read[0]["set"] == {"visualStimulus.initialAzimuthsDeg": [-75, 60]} and read[1]["set"] == {} and read[2]["set"] == {}
    assert [c["trialType"] for c in read] == ["visual", "visual", "auditory"]   # the list's order is the trialConditionId index
    assert lf.trial_weights(s) == {"visual": 1, "auditory": 2}
    write([{"trialType": "go", "weight": 2, "pairs": [], "set": {"target": "go"}}, {"trialType": "noGo", "weight": 1}])
    assert [(c["trialType"], c["set"]) for c in lf.trial_conditions(s)] == [("go", {"target": "go"}), ("noGo", {})]
    assert lf.trial_weights(s) == {"go": 2, "noGo": 1}
    write([])
    assert lf.trial_conditions(s) == [] and lf.trial_weights(s) == {}


def test_new_draw_accuracy_counts_choices_on_new_draws_only():
    df = pd.DataFrame({"outcome": ["correct", "incorrect", "correct", "noResponse", "probe"], "repeatCount": [0, 0, 1, 0, 0]})
    assert lf.new_draw_accuracy(df) == (0.5, 2)


def test_the_notebook_names_new_draw_accuracy_and_uses_every_import():
    # "performance" is lib-open-rigs' (summarise-experiment); trial_weights was imported only for a comment.
    cells = json.loads((ANALYSIS / "analysis.ipynb").read_text(encoding="utf-8"))["cells"]
    code = ["".join(c["source"]) for c in cells if c["cell_type"] == "code"]
    assert not hasattr(lf, "performance") and not any(".performance" in c for c in code)
    used = "\n".join(c for c in code[1:] if not c.lstrip().startswith("#"))
    for name in ("load_sessions", "probe_rates", "session_summary", "trial_weights"):
        assert f"{name}(" in used, name
