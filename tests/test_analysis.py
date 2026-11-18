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

WEIGHTS = {"visual": 1, "auditory": 1, "audiovisualCoherent": 1, "audiovisualConflict": 0, "neutral": 0}


def session(root: Path, name: str, repo: str, task_type: str | None = "localization", stage: int = 1) -> Path:
    s = root / "sub-GN1" / name
    (s / "Settings").mkdir(parents=True)
    (s / "manifest.json").write_text(json.dumps({"source": {"repo": repo}}), encoding="utf-8")
    task = {"trialWeights": WEIGHTS, "trainingStages": [{"trialWeights": {"visual": 9}}, {"trialWeights": None}]}
    if task_type:
        task["taskType"] = task_type
    (s / "Settings" / "Settings_Task_2026-11-16T10-00-00.json").write_text(json.dumps({"taskParameters": task}), encoding="utf-8")
    (s / "Settings" / "Settings_Experiment_2026-11-16T10-00-00.json").write_text(json.dumps({"parameters": {"TrainingStage": str(stage)}}), encoding="utf-8")
    return s


def test_switch_over_sessions_count_and_production_av_dome_and_lookalikes_do_not(tmp_path):
    # The README counts av-dome's switch-over branch; a substring match missed it and matched a lookalike repo.
    ours = session(tmp_path, "ses-001_a", "https://github.com/Coen-Lab/coen-dome-decision-making.git")
    switch = session(tmp_path, "ses-002_b", "https://github.com/ucl-open/av-dome")
    session(tmp_path, "ses-003_c", "https://github.com/ucl-open/av-dome", task_type=None)   # production av-dome
    session(tmp_path, "ses-004_d", "https://github.com/x/coen-dome-decision-making-2p")
    assert lf.find_sessions(tmp_path) == [ours, switch]


def test_trial_weights_read_the_session_s_stage(tmp_path):
    # The stage is the session's own TrainingStage (Settings_Experiment), its own weights replacing the task's.
    assert lf.trial_weights(session(tmp_path, "ses-001_a", "av-dome", stage=0)) == {"visual": 9}
    assert lf.trial_weights(session(tmp_path, "ses-002_b", "av-dome", stage=1)) == WEIGHTS
    # From the training pipeline on, the task file holds the stage's values at the top level.
    flat = session(tmp_path, "ses-003_c", "coen-dome-decision-making", stage=3)
    task_file = flat / "Settings" / "Settings_Task_2026-11-16T10-00-00.json"
    task_file.write_text(json.dumps({"taskParameters": {"trialWeights": WEIGHTS, "trainingPipeline": {"id": 3}}}), encoding="utf-8")
    assert lf.trial_weights(flat) == WEIGHTS


def test_trial_conditions_and_weights_read_a_trial_conditions_session(tmp_path):
    # From trial conditions on, Settings_Task has trialConditions, no trialWeights; set values are YAML text
    # (TrialDrawTests.TheConditionsSetRoundTrips), read back typed.
    s = session(tmp_path, "ses-001_a", "coen-dome-decision-making", stage=4)
    conditions = [{"type": "visual", "weight": 3, "set": {"contrasts": ["0.5", "1"], "reward.rewardSizeUl": ["2", "3"]}},
                  {"type": "visual", "weight": 1, "set": {"reward.rewardSizeUl": "2.5"}}, {"type": "auditory", "weight": 0, "set": {}}]
    (s / "Settings" / "Settings_Task_2026-11-16T10-00-00.json").write_text(
        json.dumps({"taskParameters": {"taskType": "localization", "trialConditions": conditions, "trainingPipeline": {"id": 4}}}), encoding="utf-8")
    read = lf.trial_conditions(s)
    assert read[0]["set"] == {"contrasts": [0.5, 1], "reward.rewardSizeUl": [2, 3]} and read[1]["set"] == {"reward.rewardSizeUl": 2.5}
    assert lf.trial_weights(s) == {"visual": 4, "auditory": 0}
    assert lf.trial_conditions(session(tmp_path, "ses-002_b", "av-dome")) is None


def test_conditions_edges_signed_azimuths_no_set_empty_list_and_zero_weights(tmp_path):
    s = session(tmp_path, "ses-001_a", "coen-dome-decision-making", stage=4)
    task_file = s / "Settings" / "Settings_Task_2026-11-16T10-00-00.json"

    def write(conditions):
        task_file.write_text(json.dumps({"taskParameters": {"taskType": "localization", "trialConditions": conditions}}), encoding="utf-8")

    write([{"type": "visual", "weight": 1, "set": {"visualStimulus.startAzimuthsDeg": ["-75", "60"]}}, {"type": "visual", "weight": 0}, {"type": "auditory", "weight": 2, "set": None}])
    read = lf.trial_conditions(s)
    assert read[0]["set"] == {"visualStimulus.startAzimuthsDeg": [-75, 60]} and read[1]["set"] == {} and read[2]["set"] == {}
    assert [c["type"] for c in read] == ["visual", "visual", "auditory"]   # the list's order is the trialCondition index
    assert lf.trial_weights(s) == {"visual": 1, "auditory": 2}
    write([])
    assert lf.trial_conditions(s) == [] and lf.trial_weights(s) == {}


def test_new_draw_accuracy_counts_choices_on_new_draws_only():
    df = pd.DataFrame({"outcome": ["Correct", "Incorrect", "Correct", "NoResponse", "Probe"], "repeatCount": [0, 0, 1, 0, 0]})
    assert lf.new_draw_accuracy(df) == (0.5, 2)


def test_the_notebook_names_new_draw_accuracy_and_uses_every_import():
    # "performance" is lib-open-rigs' (summarise-experiment); trial_weights was imported only for a comment.
    cells = json.loads((ANALYSIS / "analysis.ipynb").read_text(encoding="utf-8"))["cells"]
    code = ["".join(c["source"]) for c in cells if c["cell_type"] == "code"]
    assert not hasattr(lf, "performance") and not any(".performance" in c for c in code)
    used = "\n".join(c for c in code[1:] if not c.lstrip().startswith("#"))
    for name in ("load_sessions", "probe_rates", "session_summary", "trial_weights"):
        assert f"{name}(" in used, name
