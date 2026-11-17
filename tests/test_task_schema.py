import json
import re
from pathlib import Path

import pydantic
import pytest
import yaml
from pydantic import ValidationError

from ucl_open_coen_dome_decision_making.task import (
    CONDITION_FIELDS,
    MAX_COMBINATIONS,
    RULE_KEYS,
    TRIAL_KEYS,
    TaskType,
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
def test_committed_task_files_start_with_the_laser_off(path):
    # The laser is off unless the experimenter turns it on.
    assert yaml.safe_load(path.read_text())["taskParameters"]["optogenetics"]["enabled"] is False


@pytest.mark.parametrize("path", TASK_FILES, ids=lambda path: path.name)
def test_committed_task_files_start_at_the_two_azimuths(path):
    visual = yaml.safe_load(path.read_text())["taskParameters"]["visualStimulus"]
    assert (visual["startAzimuthsDeg"], visual["elevationDeg"]) == ([-60, 60], 15)


@pytest.mark.parametrize("values, words", [([0], "must not hold 0"), ([-60, 0], "must not hold 0"), ([-60, 200], "outside"), ([], "at least 1")])
def test_start_azimuths_are_signed_and_never_zero(values, words):
    with pytest.raises(ValidationError, match=words):
        VisualStimulus(startAzimuthsDeg=values)
    assert VisualStimulus(startAzimuthsDeg=[30]).start_azimuths_deg == [30]   # one side alone is a task's choice


def _task(name):
    data = yaml.safe_load((CONFIG / name).read_text())
    return UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data["taskParameters"])


def test_the_localization_file_moves_the_spouts():
    task = _task("task-localization.yml")
    assert task.task_type == "localization"
    assert task.spout_movements


def test_a_task_file_must_name_its_task_type():
    data = yaml.safe_load((CONFIG / "task-localization.yml").read_text())["taskParameters"]
    del data["taskType"]
    with pytest.raises(ValidationError, match="task_type|taskType"):
        UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)


def test_the_discrimination_files_keep_the_spouts_in_for_habituation():
    for name, task_type in (("task-discrimination-left-right.yml", "discriminationLeftRight"), ("task-discrimination-go-no-go.yml", "discriminationGoNoGo")):
        task = _task(name)
        assert task.task_type == task_type
        assert not task.spout_movements   # the defaults are habituation's, the pipeline's first stage
    assert _task("task-discrimination-left-right.yml").left_modality == "visual"
    assert _task("task-discrimination-go-no-go.yml").go_modality == "visual"


def test_the_two_discrimination_files_differ_only_in_what_the_task_needs():
    # Same mixes and timing (Gerion's), go and noGo where left-right has left and right; go-no-go names goModality,
    # offers one spout and repeats go and noGo alone, as left-right left and right.
    lr = yaml.safe_load((CONFIG / "task-discrimination-left-right.yml").read_text())["taskParameters"]
    gng = yaml.safe_load((CONFIG / "task-discrimination-go-no-go.yml").read_text())["taskParameters"]
    assert gng["bothSpoutsProbability"] == 0
    roles = {"go": "left", "noGo": "right"}
    for data, key in ((lr, "leftModality"), (gng, "goModality")):
        del data["taskType"], data[key], data["bothSpoutsProbability"]
        data["trialConditions"] = [{**c, "type": roles.get(c["type"], c["type"])} for c in data["trialConditions"]]
        data["repeats"]["rulesPerType"] = {roles.get(k, k): v for k, v in data["repeats"]["rulesPerType"].items()}
    assert lr == gng


def test_go_no_go_refuses_both_spouts():
    data = yaml.safe_load((CONFIG / "task-discrimination-go-no-go.yml").read_text())["taskParameters"]
    data["bothSpoutsProbability"] = 0.5
    with pytest.raises(ValidationError, match="both_spouts_probability must be 0"):
        UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)


@pytest.mark.parametrize("name", ["task-localization.yml", "task-discrimination-left-right.yml"])
def test_a_task_without_spout_movements_needs_both_spouts(name):
    # The SpoutsIn cue's LeftOnly or RightOnly draws the other spout back, never returned without spout movements.
    data = yaml.safe_load((CONFIG / name).read_text())["taskParameters"]
    data.update(spoutMovements=False, bothSpoutsProbability=0.5)
    with pytest.raises(ValidationError, match="both_spouts_probability must be 1 with spout_movements false"):
        UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)


def test_no_text_says_a_probe_s_side_is_drawn_at_random():
    # A two-modality or neutral trial is a probe (target NoReward or Random, a coin on a Random trial's choice),
    # never a trial whose side is drawn at random; and a Random probe's tails is penalised.
    stale = ("drawn at random, left or right", "side drawn at random", "a probe never does", "`rewardSizeUl` 0 where", "`rewardSizeUl` above 0", "gain stays 20")
    for path in [SCHEMA, CONFIG.parent / "BACKLOG.md", CONFIG.parent / "README.md", *TASK_FILES]:
        text = path.read_text(encoding="utf-8")
        assert not [s for s in stale if s in text], path.name


def test_the_discrimination_iti_is_what_gerion_s_mice_got():
    # Measured from 18 of Gerion's sessions: 1.5 s drink + max(ITI - 2, 0) with ITI 1.5-2.5 s, so
    # 1.5-2.0 s from the outcome to the next stimulus, and the 4 s timeout first after an incorrect choice or a miss.
    task = _task("task-discrimination-left-right.yml")
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
    for text in (TrialStructure.__doc__, column):
        assert "drinkSeconds" in text and "timeout" in text

@pytest.mark.parametrize("path", TASK_FILES, ids=lambda p: p.name)
def test_rig_control_keeps_the_zapit_file(path):
    # rig-control copies a task field ending in ConfigPath beside the task file at Go (taskfile.keep_files):
    # a field not ending so is not kept.
    opto = yaml.safe_load(path.read_text())["taskParameters"]["optogenetics"]
    assert [k for k in opto if k.endswith("Path")] == ["zapitConditionsPath"]


def test_laser_power_is_required_with_optogenetics_on():
    # This workflow sends and logs laserPowerMw as coen-dome-navigation does, required when on.
    data = yaml.safe_load((CONFIG / "task-discrimination-left-right.yml").read_text())["taskParameters"]
    on = {**data["optogenetics"], "enabled": True, "zapitConditionsPath": "C:\\zapit\\conditions.yml", "laserDurationSeconds": 0.8}
    with pytest.raises(ValidationError, match="laser_power_mw is not set"):
        UclOpenCoenDomeDecisionMakingTaskParameters.model_validate({**data, "optogenetics": on})
    with pytest.raises(ValidationError):
        UclOpenCoenDomeDecisionMakingTaskParameters.model_validate({**data, "optogenetics": {**on, "laserPowerMw": 0}})
    UclOpenCoenDomeDecisionMakingTaskParameters.model_validate({**data, "optogenetics": {**on, "laserPowerMw": 2.0}})
    # The same rule in the committed schema, which is what rig-control checks before Go.
    import json
    schema = json.loads((CONFIG.parent / "src" / "DataSchemas" / "ucl_open_coen_dome_decision_making_task.json").read_text(encoding="utf-8"))
    rule = schema["$defs"]["OptogeneticsSettings"]
    assert rule["if"]["properties"]["enabled"] == {"const": True} and rule["then"]["required"] == ["laserPowerMw"]


def test_zapit_needs_its_file_in_the_model_and_the_schema():
    # An empty zapitConditionsPath with Zapit ticked must fail rig-control's schema check, not stop the experiment at its
    # first trial; the form says to set it first, and coen-dome-navigation's schema refuses it too.
    data = yaml.safe_load((CONFIG / "task-discrimination-left-right.yml").read_text())["taskParameters"]
    on = {**data["optogenetics"], "enabled": True, "laserPowerMw": 2.0, "zapitConditionsPath": ""}
    with pytest.raises(ValidationError, match="zapit_conditions_path is empty"):
        UclOpenCoenDomeDecisionMakingTaskParameters.model_validate({**data, "optogenetics": on})
    jsonschema = pytest.importorskip("jsonschema")
    import json
    schema = json.loads((CONFIG.parent / "src" / "DataSchemas" / "ucl_open_coen_dome_decision_making_task.json").read_text(encoding="utf-8"))
    task = yaml.safe_load((CONFIG / "task-discrimination-left-right.yml").read_text())
    task["taskParameters"]["optogenetics"] = on
    assert not jsonschema.Draft202012Validator(schema).is_valid(task)
    task["taskParameters"]["optogenetics"] = {**on, "zapitConditionsPath": "C:\\zapit\\conditions.yml"}
    assert jsonschema.Draft202012Validator(schema).is_valid(task)
    task["taskParameters"]["optogenetics"] = {**on, "enabled": False}
    assert jsonschema.Draft202012Validator(schema).is_valid(task)


def _with(name="task-discrimination-left-right.yml", opto=None, structure=None):
    data = yaml.safe_load((CONFIG / name).read_text())["taskParameters"]
    data["optogenetics"].update(opto or {})
    data["trialStructure"].update(structure or {})
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
    on = {"enabled": True, "zapitConditionsPath": "C:\\zapit\\conditions.yml", "laserPowerMw": 3.0}
    _with(opto={**on, "laserDurationSeconds": 0.8})
    with pytest.raises(ValidationError, match="laser_duration_seconds"):
        _with(opto={**on, "laserDurationSeconds": 0.81})
    _with(opto={**on, "enabled": False, "laserDurationSeconds": 2.5})


@pytest.mark.parametrize(
    "block, key, value",
    [("trialStructure", "itiMaxSeconds", float("inf")), ("visualStimulus", "startAzimuthsDeg", [float("nan"), 60]), ("reward", "largeRewardMultiplier", float("inf")),
     ("response", "degreesPerLick", float("nan"))],
)
def test_the_task_s_own_blocks_refuse_inf_and_nan(block, key, value):
    # Each is a lib-open-rigs TaskBlock (allow_inf_nan off), as the lab's blocks are.
    data = yaml.safe_load((CONFIG / "task-discrimination-left-right.yml").read_text())["taskParameters"]
    data[block][key] = value
    with pytest.raises(ValidationError, match="finite number"):
        UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)


@pytest.mark.parametrize("path", TASK_FILES, ids=lambda path: path.name)
def test_committed_task_files_read_back_unchanged(path):
    # The lab's Iti, ResponseWindow, AutoReward and RewardSize blocks keep every key where the file has it.
    data = yaml.safe_load(path.read_text())["taskParameters"]
    task = UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)
    assert task.model_dump(by_alias=True, exclude_unset=True, mode="json") == data


def test_the_response_window_always_closes():
    with pytest.raises(ValidationError, match="response_window_seconds must be given"):
        _with(structure={"responseWindowSeconds": None})


def test_the_interval_is_at_least_the_drink():
    with pytest.raises(ValidationError, match="drink_seconds"):
        _with(structure={"drinkSeconds": 1.6})


def test_the_quiet_waits_are_the_task_s_off_and_refused_negative_or_half_set():
    for path in TASK_FILES:
        structure = yaml.safe_load(path.read_text())["taskParameters"]["trialStructure"]
        assert (structure["lickQuietSeconds"], structure["wheelQuietSeconds"], structure["wheelQuietCm"]) == (0, 0, 0)
    _with(structure={"lickQuietSeconds": 1.0, "wheelQuietSeconds": 1.0, "wheelQuietCm": 2.0})
    for bad in ({"lickQuietSeconds": -1}, {"wheelQuietSeconds": 1.0}, {"wheelQuietCm": 2.0}):
        with pytest.raises(ValidationError):
            _with(structure=bad)


@pytest.mark.parametrize("structure", [{"lickQuietSeconds": 10.5}, {"wheelQuietSeconds": 10.5, "wheelQuietCm": 1.0}, {"wheelQuietSeconds": 1.0, "wheelQuietCm": 20.5}])
def test_a_quiet_wait_above_its_maximum_is_refused(structure):
    # A long wait and a licking mouse would hold the trial start with no message. TrialDraw.CheckTask refuses it at Go too.
    _with(structure={"lickQuietSeconds": 10.0, "wheelQuietSeconds": 10.0, "wheelQuietCm": 20.0})
    with pytest.raises(ValidationError, match="less than or equal to"):
        _with(structure=structure)


def test_the_constants_are_the_c_sharp_s():
    import re
    logic = (CONFIG.parent / "src" / "Extensions" / "TrialLogic.cs").read_text(encoding="utf-8-sig")
    # Zapit's reply margin is lib-open-rigs' alone (ZapitTrial.ReplyMarginSeconds, ZAPIT_REPLY_MARGIN_SECONDS).
    assert "ZapitTrial.ReplyMarginSeconds" in logic and not re.search(r"const double \w*Margin", logic)
    task_py = (CONFIG.parent / "src" / "ucl_open_coen_dome_decision_making" / "task.py").read_text(encoding="utf-8")
    # The ramp-down and reward caps are lib-open-rigs' too, each tied there to its C# value.
    assert "from open_rigs.components.zapit import ZAPIT_MAX_RAMP_DOWN_SECONDS" in task_py
    assert "check_zapit_free_in_time" in task_py
    assert "    MAX_REWARD_UL,\n" in task_py and "RewardSize" in task_py
    # No constants of its own: the quiet waits' caps are lib-open-rigs' too (open_rigs.core.task LickQuietWait and
    # WheelQuietWait in task.py, OpenRigs.Core QuietWait.LickProblems and WheelProblems at Go).
    # MAX_COMBINATIONS is the task's own, the same number as TrialDraw.MaxCombinations.
    assert re.findall(r"(?m)^([A-Z_]+) = [0-9.]+", task_py) == ["MAX_COMBINATIONS"]
    assert f"public const int MaxCombinations = {MAX_COMBINATIONS};" in logic and "QuietWait.LickProblems" in logic and "QuietWait.WheelProblems" in logic
    assert "ZapitConditions.MaximumRampDownSeconds" in logic


def test_a_reward_over_the_cap_is_refused():
    from open_rigs.core.task import MAX_REWARD_UL

    data = yaml.safe_load((CONFIG / "task-discrimination-left-right.yml").read_text())["taskParameters"]
    data["reward"]["rewardSizeUl"] = MAX_REWARD_UL
    UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)
    data["reward"]["rewardSizeUl"] = MAX_REWARD_UL + 1
    with pytest.raises(ValidationError, match="less than or equal to 20"):
        UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)


def test_a_large_reward_over_the_cap_is_refused():
    data = yaml.safe_load((CONFIG / "task-discrimination-left-right.yml").read_text())["taskParameters"]
    data["reward"].update({"largeRewardMultiplier": 3, "largeRewardProbability": 0.1})
    data["reward"]["rewardSizeUl"] = 7
    with pytest.raises(ValidationError, match=r"reward.reward_size_ul \(7.0 ul\) x reward.large_reward_multiplier \(3.0\) is more than 20 ul"):
        UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)
    data["reward"]["rewardSizeUl"] = 6
    UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)
    data["reward"]["largeRewardProbability"] = 0
    data["reward"]["rewardSizeUl"] = 7
    UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)


def test_the_spout_wait_is_named_by_its_ends():
    # Pip's rule: a gap between two moments is named by its ends, as laserToStimulusSeconds.
    schema = (CONFIG.parent / "src" / "DataSchemas" / "ucl_open_coen_dome_decision_making_task.json").read_text(encoding="utf-8")
    assert '"stimulusToSpoutsInSeconds"' in schema and '"spoutInDelaySeconds"' not in schema
    for path in TASK_FILES:
        assert yaml.safe_load(path.read_text())["taskParameters"]["trialStructure"]["stimulusToSpoutsInSeconds"] == 0.5


def test_the_modalities_are_visual_or_auditory():
    data = yaml.safe_load((CONFIG / "task-discrimination-left-right.yml").read_text())["taskParameters"]
    for key in ("leftModality", "goModality"):
        UclOpenCoenDomeDecisionMakingTaskParameters.model_validate({**data, key: "auditory"})
        with pytest.raises(ValidationError):
            UclOpenCoenDomeDecisionMakingTaskParameters.model_validate({**data, key: "smell"})

def test_reward_size_says_the_calibration_plans_its_pulses():
    from ucl_open_coen_dome_decision_making.task import RewardSettings
    assert "calibration" in RewardSettings.model_fields["reward_size_ul"].description


def test_the_stimulus_never_outlasts_the_response_window():
    from ucl_open_coen_dome_decision_making.task import TrialStructure
    iti = {"itiMinSeconds": 3.4, "itiMaxSeconds": 4.4}
    TrialStructure(**iti, stimulusSeconds=1.5, responseDelaySeconds=0, responseWindowSeconds=1.5)
    with pytest.raises(ValidationError, match="stimulus_seconds"):
        TrialStructure(**iti, stimulusSeconds=2.0, responseDelaySeconds=0, responseWindowSeconds=1.5)


def test_the_stimulus_is_off_before_the_next_one_starts():
    from ucl_open_coen_dome_decision_making.task import TrialStructure
    TrialStructure(stimulusSeconds=1.5, itiMinSeconds=1.5, itiMaxSeconds=2, drinkSeconds=1, responseDelaySeconds=0, responseWindowSeconds=3)
    with pytest.raises(ValidationError, match="stimulus_seconds must not exceed iti_min_seconds"):
        TrialStructure(stimulusSeconds=1.6, itiMinSeconds=1.5, itiMaxSeconds=2, drinkSeconds=1, responseDelaySeconds=0, responseWindowSeconds=3)


def test_conditions_and_repeat_rules_are_keyed_by_the_task_type_s_keys():
    # Condition types: localization by what a trial shows (the trialType column's words), the discriminations by role;
    # rulesPerType names only the trials with a right answer.
    columns = yaml.safe_load((CONFIG.parent / "schemas" / "trial-columns.yml").read_text(encoding="utf-8"))
    assert tuple(columns["trialType"]["values"]) == TRIAL_KEYS[TaskType.LOCALIZATION]
    assert RULE_KEYS == {
        TaskType.LOCALIZATION: ("visual", "auditory", "audiovisualCoherent"),
        TaskType.DISCRIMINATION_GO_NO_GO: ("go", "noGo"),
        TaskType.DISCRIMINATION_LEFT_RIGHT: ("left", "right"),
    }
    for path in TASK_FILES:
        task = yaml.safe_load(path.read_text())["taskParameters"]
        task_type = TaskType(task["taskType"])
        assert [c["type"] for c in task["trialConditions"]] == list(TRIAL_KEYS[task_type]), path.name
        assert all(c["set"] == {} for c in task["trialConditions"]), path.name
        assert set(task["repeats"]["rulesPerType"]) == set(RULE_KEYS[task_type]), path.name


@pytest.mark.parametrize(
    "name, where, block, words",
    [
        ("task-discrimination-go-no-go.yml", "trialConditions", [{"type": "visual", "weight": 10}], "type visual is not one of go, noGo"),
        ("task-discrimination-left-right.yml", "trialConditions", [{"type": "go", "weight": 10}], "type go is not one of left, right"),
        ("task-discrimination-go-no-go.yml", "rulesPerType", {"go": "onFailure", "noGo": "onFailure", "neutral": "onNoResponse"}, r"rulesPerType.neutral\s+Extra inputs are not permitted"),
        ("task-localization.yml", "rulesPerType", {"visual": "onFailure", "auditory": "onFailure", "audiovisualCoherent": "onFailure", "neutral": "never"}, r"rulesPerType.neutral\s+Extra inputs are not permitted"),
    ],
)
def test_keys_of_another_task_type_or_a_probe_s_repeat_rule_are_refused(name, where, block, words):
    data = yaml.safe_load((CONFIG / name).read_text())["taskParameters"]
    if where == "rulesPerType":
        data["repeats"]["rulesPerType"] = block
    else:
        data[where] = block
    with pytest.raises(ValidationError, match=words):
        UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)


def test_a_go_no_go_task_with_no_go_trials_is_refused():
    data = yaml.safe_load((CONFIG / "task-discrimination-go-no-go.yml").read_text())["taskParameters"]
    data["trialConditions"][0]["weight"] = 0
    with pytest.raises(ValidationError, match="no go condition with a weight above 0"):
        UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)




def test_every_task_file_states_its_task_type():
    """app-rig-control's stage suggestion reads taskParameters.taskType from the task file itself (its
    view's [task.suggest]); a file leaving it to the schema's default got "taskType is not a field of
    the task file" and no suggestion on every localization launch (BLUE-OPTO-2)."""
    import yaml

    for path in sorted((Path(__file__).parents[1] / "config").glob("task*.yml")):
        task = yaml.safe_load(path.read_text(encoding="utf-8"))["taskParameters"]
        assert task.get("taskType") in ("localization", "discriminationGoNoGo", "discriminationLeftRight"), path.name


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


def test_training_stage_s_maximum_takes_any_pipeline_id():
    # TrainingStage carries the pipeline stage's id (ids never reused); app-rig-control's page refuses an id above 99.
    declared = re.search(r"<Name>TrainingStage</Name>.*?<Maximum>(\d+)</Maximum>", (CONFIG.parent / "src" / "main.bonsai").read_text(encoding="utf-8-sig"), re.S)
    assert int(declared.group(1)) == 99


@pytest.mark.parametrize("where, key, value", [
    (("response",), "thresholdLicks", 0), (("repeats",), "maxRepeats", -1), (("reward",), "rewardSizeUl", 0)])
def test_the_bounds_hold_with_the_lab_s_types(where, key, value):
    # Open_rigs' Int carried its own range, which dropped thresholdLicks' ge=1 and maxRepeats' ge=0; a reward
    # is above 0, as navigation's.
    data = yaml.safe_load((CONFIG / "task-localization.yml").read_text())["taskParameters"]
    block = data
    for step in where:
        block = block[step]
    block[key] = value
    with pytest.raises(ValidationError):
        UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)
    source = (CONFIG.parent / "src" / "ucl_open_coen_dome_decision_making" / "task.py").read_text(encoding="utf-8")
    assert not re.search(r"\w: (float|bool|str|Int)\b", source)


def _conditions(*conditions, name="task-localization.yml", **changes):
    data = yaml.safe_load((CONFIG / name).read_text())["taskParameters"]
    data.update(changes)
    data["trialConditions"] = list(conditions)
    return UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)


def test_the_schema_marks_the_conditions_whole_and_names_the_condition_fields():
    # app-rig-control reads both: trialConditions is one leaf (never indexed or carried), and x-conditionFields is the
    # allow-list with the types each applies to (TrialDraw.ConditionFields, a C# test holds it to the schema).
    schema = json.loads(SCHEMA.read_text(encoding="utf-8"))
    parameters = schema["$defs"]["UclOpenCoenDomeDecisionMakingTaskParameters"]
    assert parameters["properties"]["trialConditions"]["x-whole"] is True
    assert parameters["x-conditionFields"] == {path: list(types) for path, types in CONDITION_FIELDS.items()}
    assert "trialWeights" not in parameters["properties"]


def test_a_condition_s_set_is_built_and_checked_as_a_task():
    task = _conditions({"type": "visual", "weight": 3, "set": {"contrasts": [0.5, 1], "reward.rewardSizeUl": [2, 3], "visualStimulus.startAzimuthsDeg": [60]}},
                       {"type": "auditory", "weight": 1})
    assert [c.weight for c in task.trial_conditions] == [3, 1]
    for condition, words in [
        ({"type": "visual", "weight": 1, "set": {"trialStructure.itiMinSeconds": 4}}, "trialStructure.itiMinSeconds, which is not a condition field of visual"),
        ({"type": "auditory", "weight": 1, "set": {"contrasts": [1]}}, "contrasts, which is not a condition field of auditory"),
        ({"type": "neutral", "weight": 1, "set": {"visualStimulus.startAzimuthsDeg": [60]}}, "not a condition field of neutral"),
        ({"type": "visual", "weight": 1, "set": {"optogenetics.laserOnProbability": 1}}, "with optogenetics off"),
        ({"type": "visual", "weight": 1, "set": {"visualStimulus.startAzimuthsDeg": [60, 0]}}, r"(?s)trial_conditions\[0\] \(visual\).*must not hold 0"),
        ({"type": "visual", "weight": 1, "set": {"reward.rewardSizeUl": [2, 99]}}, r"trial_conditions\[0\] \(visual\) with \{'reward.rewardSizeUl': 99\}"),
        ({"type": "visual", "weight": 1, "set": {"reward.rewardSizeUl": []}}, "is an empty list"),
        ({"type": "visual", "weight": 0}, "a weight above 0"),
    ]:
        with pytest.raises(ValidationError, match=words):
            _conditions(condition)


def test_a_condition_may_expand_into_at_most_the_cap():
    cells = {"reward.rewardSizeUl": [1, 2, 3, 4, 5, 6, 7, 8], "autoRewardProbability": [0, 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7]}
    _conditions({"type": "visual", "weight": 1, "set": cells})
    cells["autoRewardProbability"].append(0.8)
    with pytest.raises(ValidationError, match=f"expands into 72 combinations, more than {MAX_COMBINATIONS}"):
        _conditions({"type": "visual", "weight": 1, "set": cells})


def test_a_condition_pins_the_laser_with_optogenetics_on():
    data = yaml.safe_load((CONFIG / "task-discrimination-left-right.yml").read_text())["taskParameters"]
    on = {**data["optogenetics"], "enabled": True, "laserPowerMw": 2.0, "laserDurationSeconds": 0.8, "zapitConditionsPath": "C:\\zapit\\conditions.yml"}
    _conditions({"type": "left", "weight": 1, "set": {"optogenetics.laserOnProbability": 1}}, {"type": "right", "weight": 1, "set": {"optogenetics.laserOnProbability": 0}},
                name="task-discrimination-left-right.yml", optogenetics=on)
