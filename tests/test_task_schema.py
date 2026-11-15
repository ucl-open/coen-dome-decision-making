import json
import re
from pathlib import Path

import pydantic
import pytest
import yaml
from pydantic import ValidationError

from ucl_open_coen_dome_decision_making.task import (
    AudioStimulus,
    UclOpenCoenDomeDecisionMakingTaskLogic,
    UclOpenCoenDomeDecisionMakingTaskParameters,
    VisualStimulus,
)

CONFIG = Path(__file__).parent.parent / "config"
TASK_FILES = sorted(CONFIG.glob("task*.yml"))
SCHEMA = CONFIG.parent / "src" / "DataSchemas" / "ucl_open_coen_dome_decision_making_task.json"


@pytest.mark.parametrize("path", TASK_FILES, ids=lambda path: path.name)
def test_committed_task_files_validate(path):
    data = yaml.safe_load(path.read_text())
    UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data["taskParameters"])


@pytest.mark.parametrize("path", TASK_FILES, ids=lambda path: path.name)
def test_committed_task_files_use_the_discrete_azimuths(path):
    data = yaml.safe_load(path.read_text())
    assert data["taskParameters"]["visualStimulus"]["fixedAzimuthsDeg"] == [-60, 60]


def test_overwrite_azimuths_default_keeps_the_uniform_range():
    assert VisualStimulus().fixed_azimuths_deg is None


def test_overwrite_azimuths_empty_list_keeps_the_uniform_range():
    assert VisualStimulus(fixedAzimuthsDeg=[]).fixed_azimuths_deg == []


def test_overwrite_azimuths_set_alongside_min_max():
    visual = VisualStimulus(azimuthMinDeg=-30, azimuthMaxDeg=30, fixedAzimuthsDeg=[-60, 60])
    assert visual.fixed_azimuths_deg == [-60, 60]


@pytest.mark.parametrize("values", [[60], [-60], [0], [-60, 0]])
def test_overwrite_azimuths_needs_both_sides(values):
    with pytest.raises(ValidationError, match="fixed_azimuths_deg needs a value"):
        VisualStimulus(fixedAzimuthsDeg=values)


def test_overwrite_azimuths_respects_the_coherent_cap():
    with pytest.raises(ValidationError, match="right-side"):
        VisualStimulus(fixedAzimuthsDeg=[-60, 100], coherentAzimuthCapDeg=80)


def test_overwrite_azimuths_rejects_out_of_range():
    with pytest.raises(ValidationError, match="outside"):
        VisualStimulus(fixedAzimuthsDeg=[-60, 60, 200])


def test_speaker_locations_reject_a_centre_entry():
    with pytest.raises(ValidationError, match="azimuth-0"):
        AudioStimulus(
            speakerLocations=[
                {"azimuth": -60, "elevation": 15},
                {"azimuth": 0, "elevation": 15},
                {"azimuth": 60, "elevation": 15},
            ]
        )


def _task(name):
    data = yaml.safe_load((CONFIG / name).read_text())
    return UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data["taskParameters"])


def test_a_task_file_without_task_type_is_localization_with_moving_spouts():
    task = _task("task-localization.yml")
    assert task.task_type == "localization"
    assert all(stage.spout_movements for stage in task.training_stages)


def test_the_discrimination_file_keeps_the_spouts_in_for_habituation():
    task = _task("task-discrimination.yml")
    assert task.task_type == "discrimination"
    assert task.target_modality == "visual"
    assert not task.training_stages[0].spout_movements
    assert all(stage.spout_movements for stage in task.training_stages[1:])


def test_the_discrimination_iti_is_what_gerion_s_mice_got():
    # Pip, 13 Nov 2026, measured from 18 of Gerion's sessions: 1.5 s drink + max(ITI - 2, 0) with ITI 1.5-2.5 s, so
    # 1.5-2.0 s from the outcome to the next stimulus, and the 4 s timeout first after an incorrect choice or a miss.
    task = _task("task-discrimination.yml")
    trial = task.trial_structure
    assert (trial.iti_min_seconds, trial.iti_max_seconds, trial.drink_seconds) == (1.5, 2.0, 1.5)
    assert task.penalty.timeout_seconds == 4.0
    # The localization file keeps its interval.
    local = _task("task-localization.yml").trial_structure
    assert (local.iti_min_seconds, local.iti_max_seconds, local.drink_seconds) == (3.4, 4.4, 2.0)

def test_the_iti_descriptions_say_the_drink_is_in_the_interval():
    from ucl_open_coen_dome_decision_making.task import TrialStructure

    columns = yaml.safe_load((CONFIG.parent / "schemas" / "trial-columns.yml").read_text(encoding="utf-8"))
    # The column's description is the dome tasks' shared one; this task's detail is its note.
    column = columns["itiSeconds"]["description"] + " " + columns["itiSeconds"]["note"]
    for text in (TrialStructure.model_fields["iti_min_seconds"].description, column):
        assert "drinkSeconds" in text and "timeout" in text

@pytest.mark.parametrize("path", TASK_FILES, ids=lambda p: p.name)
def test_rig_control_keeps_the_zapit_file(path):
    # rig-control copies a task field ending in ConfigPath beside the task file at Go (taskfile.keep_files):
    # renamed zapitConditionsPath on 9 Nov 2026, the file stopped being kept (audit, 10 Nov 2026).
    opto = yaml.safe_load(path.read_text())["taskParameters"]["optogenetics"]
    assert [k for k in opto if k.endswith("Path")] == ["zapitConfigPath"]


def test_laser_power_is_required_with_optogenetics_on():
    # Pip, 10 Nov 2026: this workflow sends and logs laserPowerMw as coen-dome-navigation does, required when on.
    data = yaml.safe_load((CONFIG / "task-discrimination.yml").read_text())["taskParameters"]
    on = {**data["optogenetics"], "useZapit": True, "zapitConfigPath": "C:\\zapit\\conditions.yml", "laserDurationSeconds": 0.8}
    with pytest.raises(ValidationError, match="laser_power_mw is not set"):
        UclOpenCoenDomeDecisionMakingTaskParameters.model_validate({**data, "optogenetics": on})
    with pytest.raises(ValidationError):
        UclOpenCoenDomeDecisionMakingTaskParameters.model_validate({**data, "optogenetics": {**on, "laserPowerMw": 0}})
    UclOpenCoenDomeDecisionMakingTaskParameters.model_validate({**data, "optogenetics": {**on, "laserPowerMw": 2.0}})
    # The same rule in the committed schema, which is what rig-control checks before Go.
    import json
    schema = json.loads((CONFIG.parent / "src" / "DataSchemas" / "ucl_open_coen_dome_decision_making_task.json").read_text(encoding="utf-8"))
    rule = schema["$defs"]["OptogeneticsSettings"]
    assert rule["if"]["properties"]["useZapit"] == {"const": True} and rule["then"]["required"] == ["laserPowerMw"]


def test_zapit_needs_its_file_in_the_model_and_the_schema():
    # Audit, 10 Nov 2026, round 2: an empty zapitConfigPath with Zapit ticked passed rig-control's schema check and
    # stopped the experiment at its first trial; the form says to set it first, and the corridor's schema refuses it.
    data = yaml.safe_load((CONFIG / "task-discrimination.yml").read_text())["taskParameters"]
    on = {**data["optogenetics"], "useZapit": True, "laserPowerMw": 2.0, "zapitConfigPath": ""}
    with pytest.raises(ValidationError, match="zapit_config_path is empty"):
        UclOpenCoenDomeDecisionMakingTaskParameters.model_validate({**data, "optogenetics": on})
    jsonschema = pytest.importorskip("jsonschema")
    import json
    schema = json.loads((CONFIG.parent / "src" / "DataSchemas" / "ucl_open_coen_dome_decision_making_task.json").read_text(encoding="utf-8"))
    task = yaml.safe_load((CONFIG / "task-discrimination.yml").read_text())
    task["taskParameters"]["optogenetics"] = on
    assert not jsonschema.Draft202012Validator(schema).is_valid(task)
    task["taskParameters"]["optogenetics"] = {**on, "zapitConfigPath": "C:\\zapit\\conditions.yml"}
    assert jsonschema.Draft202012Validator(schema).is_valid(task)
    task["taskParameters"]["optogenetics"] = {**on, "useZapit": False}
    assert jsonschema.Draft202012Validator(schema).is_valid(task)


def test_the_laser_delay_is_gone():
    # Pip, 10 Nov 2026: laserDelaySeconds (stimDelay) was 0 in every task file since 24 Sep 2026; dropped.
    schema = (CONFIG.parent / "src" / "DataSchemas" / "ucl_open_coen_dome_decision_making_task.json").read_text(encoding="utf-8")
    assert "laserDelaySeconds" not in schema and "stimDelay" not in schema
    assert "OptogeneticDelay" not in (CONFIG.parent / "src" / "main.bonsai").read_text(encoding="utf-8-sig")


@pytest.mark.parametrize("path", TASK_FILES, ids=lambda p: p.name)
def test_laser_to_stimulus_is_the_task_s_and_arm_to_trigger_is_gone(path):
    # Pip, 13 Nov 2026: Zapit adds no time, so the arm-to-trigger wait is gone; laserToStimulusSeconds keeps 0.5 s.
    opto = yaml.safe_load(path.read_text())["taskParameters"]["optogenetics"]
    assert opto["laserToStimulusSeconds"] == 0.5 and "armToTriggerSeconds" not in opto
    for text in (CONFIG.parent / "src" / "DataSchemas" / "ucl_open_coen_dome_decision_making_task.json", CONFIG.parent / "src" / "main.bonsai",
                 CONFIG.parent / "src" / "Extensions" / "TrialLogic.cs"):
        assert "ArmToTrigger" not in text.read_text(encoding="utf-8-sig") and "armToTrigger" not in text.read_text(encoding="utf-8-sig")


def _with(name="task-discrimination.yml", opto=None, structure=None, stage=None):
    data = yaml.safe_load((CONFIG / name).read_text())["taskParameters"]
    data["optogenetics"].update(opto or {})
    data["trialStructure"].update(structure or {})
    data["trainingStages"][0].update(stage or {})
    return UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)


@pytest.mark.parametrize("lead", [1.3, 0.5, 0.0, -0.5, -1.5])
def test_a_signed_lead_inside_the_interval_and_the_response_window_is_accepted(lead):
    # Discrimination: itiMinSeconds 1.5 less the 0.2 s reply margin; the response window 0 + 1.5 s.
    assert _with(opto={"laserToStimulusSeconds": lead}).optogenetics.laser_to_stimulus_seconds == lead


@pytest.mark.parametrize("lead, words", [(1.31, "reply margin"), (-1.51, "after the response window")])
def test_a_lead_outside_them_is_refused(lead, words):
    with pytest.raises(ValidationError, match=words):
        _with(opto={"laserToStimulusSeconds": lead})


def test_a_laser_that_would_delay_the_next_arm_is_refused():
    # TrialDraw.LongestLaserSeconds' rule: 0 open loop + 1.5 s interval - 0.5 s ramp-down - 0.2 s margin = 0.8 s.
    on = {"useZapit": True, "zapitConfigPath": "C:\\zapit\\conditions.yml", "laserPowerMw": 3.0}
    _with(opto={**on, "laserDurationSeconds": 0.8})
    with pytest.raises(ValidationError, match="laser_duration_seconds"):
        _with(opto={**on, "laserDurationSeconds": 0.81})
    _with(opto={**on, "useZapit": False, "laserDurationSeconds": 2.5})


@pytest.mark.parametrize(
    "block, key, value",
    [("trialStructure", "itiMaxSeconds", float("inf")), ("visualStimulus", "fixedAzimuthsDeg", [float("nan"), 60]), ("reward", "largeRewardMultiplier", float("inf")),
     ("response", "degreesPerLick", float("nan"))],
)
def test_the_task_s_own_blocks_refuse_inf_and_nan(block, key, value):
    # Each is a lib-open-rigs TaskBlock (allow_inf_nan off), as the lab's blocks are.
    data = yaml.safe_load((CONFIG / "task-discrimination.yml").read_text())["taskParameters"]
    data[block][key] = value
    with pytest.raises(ValidationError, match="finite number"):
        UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)


def test_the_interval_is_at_least_the_drink():
    with pytest.raises(ValidationError, match="drink_seconds"):
        _with(structure={"drinkSeconds": 1.6})


def test_the_quiet_waits_are_per_stage_off_by_default_and_refused_negative_or_half_set():
    for path in TASK_FILES:
        for stage in yaml.safe_load(path.read_text())["taskParameters"]["trainingStages"]:
            assert (stage["lickQuietSeconds"], stage["wheelQuietSeconds"], stage["wheelQuietCm"]) == (0, 0, 0)
    _with(stage={"lickQuietSeconds": 1.0, "wheelQuietSeconds": 1.0, "wheelQuietCm": 2.0})
    for bad in ({"lickQuietSeconds": -1}, {"wheelQuietSeconds": 1.0}, {"wheelQuietCm": 2.0}):
        with pytest.raises(ValidationError):
            _with(stage=bad)


def test_the_constants_are_the_c_sharp_s():
    import re
    logic = (CONFIG.parent / "src" / "Extensions" / "TrialLogic.cs").read_text(encoding="utf-8-sig")
    # Zapit's reply margin is lib-open-rigs' alone (ZapitTrial.ReplyMarginSeconds, ZAPIT_REPLY_MARGIN_SECONDS; audit round 3).
    assert "ZapitTrial.ReplyMarginSeconds" in logic and not re.search(r"const double \w*Margin", logic)
    task_py = (CONFIG.parent / "src" / "ucl_open_coen_dome_decision_making" / "task.py").read_text(encoding="utf-8")
    # The ramp-down and reward caps are lib-open-rigs' too (audit round 4), each tied there to its C# value.
    assert "from open_rigs.components.zapit import ZAPIT_MAX_RAMP_DOWN_SECONDS, ZAPIT_REPLY_MARGIN_SECONDS" in task_py
    assert "from open_rigs.rigs.lab_rig import MAX_REWARD_UL" in task_py
    assert not re.search(r"(?m)^[A-Z_]+ = [0-9.]+$", task_py)
    assert "ZapitConditions.MaximumRampDownMs / 1000" in logic


def test_a_reward_over_the_cap_is_refused():
    from open_rigs.rigs.lab_rig import MAX_REWARD_UL

    _with(stage={"rewardSizeUl": MAX_REWARD_UL})
    with pytest.raises(ValidationError, match="less than or equal to 20"):
        _with(stage={"rewardSizeUl": MAX_REWARD_UL + 1})


def test_a_large_reward_over_the_cap_is_refused():
    data = yaml.safe_load((CONFIG / "task-discrimination.yml").read_text())["taskParameters"]
    data["reward"].update({"largeRewardMultiplier": 3, "largeRewardProbability": 0.1})
    data["trainingStages"][0]["rewardSizeUl"] = 7
    with pytest.raises(ValidationError, match=r"reward_size_ul \(7.0 ul\) x reward.large_reward_multiplier \(3.0\) is more than 20 ul"):
        UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)
    data["trainingStages"][0]["rewardSizeUl"] = 6
    UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)
    data["reward"]["largeRewardProbability"] = 0
    data["trainingStages"][0]["rewardSizeUl"] = 7
    UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)


def test_the_spout_wait_is_named_by_its_ends():
    # Pip's rule (10 Nov 2026): a gap between two moments is named by its ends, as laserToStimulusSeconds.
    schema = (CONFIG.parent / "src" / "DataSchemas" / "ucl_open_coen_dome_decision_making_task.json").read_text(encoding="utf-8")
    assert '"stimulusToSpoutsInSeconds"' in schema and '"spoutInDelaySeconds"' not in schema
    for path in TASK_FILES:
        assert yaml.safe_load(path.read_text())["taskParameters"]["trialStructure"]["stimulusToSpoutsInSeconds"] == 0.5


def test_target_modality_is_visual_or_auditory():
    data = yaml.safe_load((CONFIG / "task-discrimination.yml").read_text())["taskParameters"]
    UclOpenCoenDomeDecisionMakingTaskParameters.model_validate({**data, "targetModality": "auditory"})
    with pytest.raises(ValidationError):
        UclOpenCoenDomeDecisionMakingTaskParameters.model_validate({**data, "targetModality": "smell"})

def test_reward_size_says_the_calibration_plans_its_pulses():
    from ucl_open_coen_dome_decision_making.task import TrainingStage
    assert "calibration" in TrainingStage.model_fields["reward_size_ul"].description


def test_the_stimulus_never_outlasts_the_response_window():
    from ucl_open_coen_dome_decision_making.task import TrialStructure
    TrialStructure(stimulusSeconds=1.5, openLoopSeconds=0, closedLoopSeconds=1.5)
    with pytest.raises(ValidationError, match="stimulus_seconds"):
        TrialStructure(stimulusSeconds=2.0, openLoopSeconds=0, closedLoopSeconds=1.5)


def test_weights_and_repeat_rules_are_keyed_by_trial_type_name():
    names = {"visual", "auditory", "audiovisualCoherent", "audiovisualConflict", "neutral"}
    columns = yaml.safe_load((CONFIG.parent / "schemas" / "trial-columns.yml").read_text(encoding="utf-8"))
    assert set(columns["trialType"]["values"]) == names
    for path in TASK_FILES:
        task = yaml.safe_load(path.read_text())["taskParameters"]
        assert set(task["repeats"]["rulesPerType"]) == names
        assert all(set(stage["trialWeights"]) == names for stage in task["trainingStages"])




def test_every_task_file_states_its_task_type():
    """app-rig-control's stage suggestion reads taskParameters.taskType from the task file itself (its
    view's [task.suggest]); a file leaving it to the schema's default got "taskType is not a field of
    the task file" and no suggestion on every localization launch (BLUE-OPTO-2, 14 Nov 2026)."""
    import yaml

    for path in sorted((Path(__file__).parents[1] / "config").glob("task*.yml")):
        task = yaml.safe_load(path.read_text(encoding="utf-8"))["taskParameters"]
        assert task.get("taskType") in ("localization", "discrimination"), path.name


def test_the_committed_task_schema_is_the_model_s():
    # app-rig-control checks the form against this file: a model change not regenerated (regenerate-schemas) fails here.
    model = pydantic.RootModel[UclOpenCoenDomeDecisionMakingTaskLogic].model_json_schema(
        by_alias=True, mode="serialization", union_format="primitive_type_array"
    )
    assert json.loads(SCHEMA.read_text(encoding="utf-8")) == json.loads(json.dumps(model))


def _property_names(node) -> set[str]:
    if isinstance(node, dict):
        return set(node.get("properties", {})) | {n for v in node.values() for n in _property_names(v)}
    return {n for v in node for n in _property_names(v)} if isinstance(node, list) else set()


def test_the_generated_csharp_has_every_schema_field():
    # A field added to the model and schema but not regenerated into C# stops the workflow at Go: run regenerate-schemas.
    generated = (CONFIG.parent / "src" / "Extensions" / "UclOpenCoenDomeDecisionMakingTask.Generated.cs").read_text(encoding="utf-8")
    names = set(re.findall(r'JsonPropertyAttribute\("([^"]+)"', generated))
    assert names == _property_names(json.loads(SCHEMA.read_text(encoding="utf-8")))


@pytest.mark.parametrize("path", TASK_FILES, ids=lambda path: path.name)
def test_training_stage_s_maximum_is_the_task_file_s_last_row(path):
    # A row added to trainingStages is reachable only once ExperimentControl's TrainingStage Maximum (main.bonsai) says so.
    declared = re.search(r"<Name>TrainingStage</Name>.*?<Maximum>(\d+)</Maximum>", (CONFIG.parent / "src" / "main.bonsai").read_text(encoding="utf-8-sig"), re.S)
    assert int(declared.group(1)) == len(yaml.safe_load(path.read_text(encoding="utf-8"))["taskParameters"]["trainingStages"]) - 1
