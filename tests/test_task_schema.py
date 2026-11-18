import json
import re
from pathlib import Path

import pydantic
import pytest
import yaml
from open_rigs.rigs.conditions import unused_paths
from pydantic import ValidationError

from ucl_open_coen_dome_decision_making.task import (
    CONDITION_FIELDS,
    MAX_COMBINATIONS,
    MAX_WEIGHT_SUM,
    RULES,
    TARGETS,
    TaskType,
    TrialCondition,
    TRIAL_TYPES,
    derive_trial_type,
    UclOpenCoenDomeDecisionMakingTaskLogic,
    Curriculum,
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
    assert (visual["initialAzimuthsDeg"], visual["elevationDeg"]) == ([-60, 60], 15)


@pytest.mark.parametrize("values, words", [([0], "must not hold 0"), ([-60, 0], "must not hold 0"), ([-60, 200], "outside"), ([], "at least 1")])
def test_initial_azimuths_are_signed_and_never_zero(values, words):
    with pytest.raises(ValidationError, match=words):
        VisualStimulus(initialAzimuthsDeg=values)
    assert VisualStimulus(initialAzimuthsDeg=[30]).initial_azimuths_deg == [30]   # one side alone is a task's choice


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
        assert not task.spout_movements   # the defaults are habituation's, the curriculum's first stage


def test_the_two_discrimination_files_differ_only_in_what_the_task_needs():
    # Same mixes and timing (Gerion's), go and noGo where left-right has left and right, noReward probes where it has random
    # ones (both spouts in); go-no-go offers one spout.
    lr = yaml.safe_load((CONFIG / "task-discrimination-left-right.yml").read_text())["taskParameters"]
    gng = yaml.safe_load((CONFIG / "task-discrimination-go-no-go.yml").read_text())["taskParameters"]
    assert gng["bothSpoutsProbability"] == 0
    roles = {"go": "left", "noGo": "right", "noReward": "random"}
    for data in (lr, gng):
        del data["taskType"], data["bothSpoutsProbability"]
        data["target"] = roles.get(data["target"], data["target"])
        for c in data["trialConditions"]:
            c["trialType"] = roles.get(c["trialType"], c["trialType"])
            c["set"]["target"] = roles.get(c["set"]["target"], c["set"]["target"])
            c["set"].pop("bothSpoutsProbability", None)
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
    [("trialStructure", "itiMaxSeconds", float("inf")), ("visualStimulus", "initialAzimuthsDeg", [float("nan"), 60]), ("reward", "largeRewardMultiplier", float("inf")),
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
    # MAX_COMBINATIONS is the task's own, the same number as TrialDraw.MaxCombinations; MAX_WEIGHT_SUM is the int.MaxValue
    # TrialDraw.CheckTask refuses a weight sum above.
    assert re.findall(r"(?m)^([A-Z_]+) = [0-9.]+", task_py) == ["MAX_WEIGHT_SUM", "MAX_COMBINATIONS"]
    assert MAX_WEIGHT_SUM == 2**31 - 1 and "Sum(c => (long)c.Weight) > int.MaxValue" in logic
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


def test_each_committed_condition_s_trial_type_is_derived_and_logged():
    # A trialType names a condition (the trialType column's words) and drives nothing; the committed files' are the derived ones.
    columns = yaml.safe_load((CONFIG.parent / "schemas" / "trial-columns.yml").read_text(encoding="utf-8"))
    for path in TASK_FILES:
        task = _task(path.name)
        for condition in task.trial_conditions:
            assert condition.trial_type.value in columns["trialType"]["values"]
            assert derive_trial_type(task, condition) == condition.trial_type.value, (path.name, condition.trial_type)


@pytest.mark.parametrize("task_type, name", [
    ("localization", "task-localization.yml"), ("discriminationGoNoGo", "task-discrimination-go-no-go.yml"), ("discriminationLeftRight", "task-discrimination-left-right.yml")])
def test_a_target_of_another_task_type_is_refused(task_type, name):
    data = yaml.safe_load((CONFIG / name).read_text())["taskParameters"]
    for target in {"visualSide", "auditorySide", "random", "go", "noGo", "noReward", "left", "right"} - set(TARGETS[TaskType(task_type)]):
        data["trialConditions"][0]["set"]["target"] = target
        with pytest.raises(ValidationError, match=rf"taskType {task_type} needs target one of .* \(x-rules\)"):
            UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)


@pytest.mark.parametrize("set_, words", [
    ({"target": "random", "bothSpoutsProbability": 0.5, "repeats.repeatOn": "never"}, "target random needs bothSpoutsProbability 1 and repeats.repeatOn never"),
    ({"target": "random", "bothSpoutsProbability": 1, "repeats.repeatOn": "notCorrect"}, "target random needs bothSpoutsProbability 1 and repeats.repeatOn never"),
    ({"repeats.maxRepeats": 0, "repeats.repeatOn": "noResponse"}, "repeats.maxRepeats 0 needs repeats.repeatOn never"),
    ({"target": "auditorySide", "auditoryStimulus.initialAzimuthsDeg": [0, 60]}, r"target auditorySide needs auditory azimuths without 0 or all \(x-rules\)"),
    ({"target": "auditorySide", "auditoryStimulus.initialAzimuthsDeg": ["all", 60]}, r"target auditorySide needs auditory azimuths without 0 or all \(x-rules\)"),
    ({"auditoryStimulus.initialAzimuthsDeg": ["left"]}, "auditoryStimulus.initialAzimuthsDeg"),
    ({"auditoryStimulus.amplitude": -0.1}, "greater than or equal to 0"),
    ({"contrasts": [-1]}, "contrasts must each be 0 or more"),
])
def test_a_condition_breaking_a_rule_is_refused(set_, words):
    with pytest.raises(ValidationError, match=words):
        _conditions({"trialType": "visual", "weight": 1, "set": set_})


V, S = "visualStimulus.initialAzimuthsDeg", "auditoryStimulus.initialAzimuthsDeg"


@pytest.mark.parametrize("set_, pairs, trial_type", [
    ({"target": "go"}, [], "go"), ({"target": "noGo", "contrasts": [0]}, [], "noGo"), ({"target": "left"}, [], "left"), ({"target": "right"}, [], "right"),
    ({}, [], "audiovisualCoherent"), ({V: [-60, 60], S: [-60, 60]}, [[V, S]], "audiovisualCoherent"),
    ({V: [-60, 60], S: [60, -60]}, [[V, S]], "audiovisualConflict"), ({V: [-60, 60], S: [60, -60]}, [], "audiovisualCoherent"),
    ({S: [0]}, [], "visual"), ({S: ["all"]}, [], "visual"), ({S: [0, "all"]}, [], "visual"), ({S: [0, 60]}, [], "audiovisualCoherent"),
    ({"auditoryStimulus.amplitude": 0}, [], "visual"), ({"auditoryStimulus.amplitude": [0, 0.5]}, [], "audiovisualCoherent"),
    ({"contrasts": [0]}, [], "auditory"), ({"contrasts": [0, 0.5]}, [], "audiovisualCoherent"),
    ({"contrasts": [0], S: ["all"]}, [], "neutral"), ({"contrasts": [0], S: [0]}, [], "neutral"),
    ({"contrasts": [0], "auditoryStimulus.amplitude": 0}, [], "neutral"), ({"target": "random", "contrasts": [0], S: [0]}, [], "neutral"),
])
def test_every_trial_type_is_derived_by_x_trial_types(set_, pairs, trial_type):
    # The localization task's contrast 3 and amplitude 1 at [-60, 60] under each set; the condition itself is not checked.
    task = UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(yaml.safe_load((CONFIG / "task-localization.yml").read_text())["taskParameters"])
    assert derive_trial_type(task, TrialCondition(trialType="neutral", weight=1, set=set_, pairs=pairs)) == trial_type


def test_a_go_no_go_probe_must_not_repeat():
    data = yaml.safe_load((CONFIG / "task-discrimination-go-no-go.yml").read_text())["taskParameters"]
    data["trialConditions"][4]["set"]["repeats.repeatOn"] = "noResponse"
    with pytest.raises(ValidationError, match="target noReward needs repeats.repeatOn never"):
        UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)


def test_all_plays_on_every_speaker_and_pairs():
    # "all" is a sound azimuth item: alone, in a list, paired with the grating's azimuths; it has no side, so no cue.
    visual, sound = "visualStimulus.initialAzimuthsDeg", "auditoryStimulus.initialAzimuthsDeg"
    task = _conditions({"trialType": "audiovisualCoherent", "weight": 1, "pairs": [[visual, sound]], "set": {visual: [-60, 60], sound: ["all", 60]}},
                       {"trialType": "neutral", "weight": 1, "set": {"contrasts": [0], sound: ["all"], "target": "random", "bothSpoutsProbability": 1}})
    assert derive_trial_type(task, task.trial_conditions[1]) == "neutral"
    items = json.loads(SCHEMA.read_text())["$defs"]["AuditoryStimulus"]["properties"]["initialAzimuthsDeg"]["items"]
    assert items == {"oneOf": [{"type": "number"}, {"const": "all", "type": "string"}]}   # oneOf: Bonsai.Sgen's List<object>


def test_an_amplitude_above_1_is_the_peak_check_s_to_refuse():
    # Pydantic cannot know the speakers: Go checks the largest amplitude against the -1 dBFS peak (LevelAuditoryStimulusSet).
    _conditions({"trialType": "auditory", "weight": 1, "set": {"target": "auditorySide", "contrasts": [0], "auditoryStimulus.amplitude": [1, 4]}})


def test_pairs_zip_their_lists_and_the_rest_combine():
    from ucl_open_coen_dome_decision_making.task import condition_combinations

    base = _task("task-localization.yml").model_dump(by_alias=True, mode="json")
    visual, sound = "visualStimulus.initialAzimuthsDeg", "auditoryStimulus.initialAzimuthsDeg"
    combos = condition_combinations(base, {sound: [60, -60], "contrasts": [0.5, 1], "auditoryStimulus.amplitude": [0.5, 1]}, [[visual, sound], ["contrasts", "auditoryStimulus.amplitude"]])
    assert combos == [
        {visual: [-60], sound: [60], "contrasts": [0.5], "auditoryStimulus.amplitude": 0.5},
        {visual: [-60], sound: [60], "contrasts": [1], "auditoryStimulus.amplitude": 1},
        {visual: [60], sound: [-60], "contrasts": [0.5], "auditoryStimulus.amplitude": 0.5},
        {visual: [60], sound: [-60], "contrasts": [1], "auditoryStimulus.amplitude": 1},
    ]
    # Unpaired, a list field set at condition level is one entry per combination; a list in a scalar cell one value.
    assert condition_combinations(base, {"contrasts": [0.5, 1], "reward.rewardSizeUl": [2, 3]}) == [
        {"contrasts": [0.5], "reward.rewardSizeUl": 2}, {"contrasts": [0.5], "reward.rewardSizeUl": 3},
        {"contrasts": [1], "reward.rewardSizeUl": 2}, {"contrasts": [1], "reward.rewardSizeUl": 3}]
    with pytest.raises(ValidationError, match=r"pairs \['visualStimulus.initialAzimuthsDeg', 'auditoryStimulus.initialAzimuthsDeg'\] have lists of lengths 2, 3"):
        _conditions({"trialType": "audiovisualCoherent", "weight": 1, "pairs": [[visual, sound]], "set": {sound: [-60, 0, 60]}})
    for pairs in ([[visual]], [[visual, sound], [sound, "contrasts"]]):
        with pytest.raises(ValidationError, match="each pairs group needs two paths or more"):
            _conditions({"trialType": "audiovisualCoherent", "weight": 1, "pairs": pairs})
    with pytest.raises(ValidationError, match="pairs trialStructure.itiMinSeconds, which is not a condition field"):
        _conditions({"trialType": "visual", "weight": 1, "pairs": [[visual, "trialStructure.itiMinSeconds"]]})


def test_a_go_no_go_task_with_no_go_trials_is_refused():
    data = yaml.safe_load((CONFIG / "task-discrimination-go-no-go.yml").read_text())["taskParameters"]
    data["trialConditions"][0]["weight"] = 0
    with pytest.raises(ValidationError, match="no condition with target go and a weight above 0"):
        UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)


def test_a_go_no_go_condition_with_a_set_is_accepted():
    # Each condition is checked as a task of its own (_conditions_run); a noGo condition's copy has no go condition, which
    # refused every go/no-go task with any set until the go-trials rule was left to the whole task (audit round 2).
    data = yaml.safe_load((CONFIG / "task-discrimination-go-no-go.yml").read_text())["taskParameters"]
    data["trialConditions"][0]["set"]["reward.rewardSizeUl"] = 2
    UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)
    data["trialConditions"][0]["weight"] = 1_000_001
    with pytest.raises(ValidationError, match="less than or equal to 1000000"):
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
        # x-rules and x-trial-types hold subschemas over dotted paths, not fields.
        return set(node.get("properties", {})) | {n for k, v in node.items() if not k.startswith("x-") for n in _property_names(v)}
    return {n for v in node for n in _property_names(v)} if isinstance(node, list) else set()


def test_the_generated_csharp_has_every_schema_field():
    # A field added to the model and schema but not regenerated into C# stops the workflow at Go: run regenerate-schemas.
    generated = (CONFIG.parent / "src" / "Extensions" / "UclOpenCoenDomeDecisionMakingTask.Generated.cs").read_text(encoding="utf-8")
    names = set(re.findall(r'JsonPropertyAttribute\("([^"]+)"', generated))
    assert names == _property_names(json.loads(SCHEMA.read_text(encoding="utf-8")))


GOOD_CURRICULUM = {"id": 3, "code": "S03A", "version": "k7f2", "path": "A", "label": "Conflict Added", "sha256": "abc"}


def test_the_curriculum_is_the_stage_s_only_record_and_checks_its_patterns():
    # No TrainingStage parameter: app-rig-control writes exactly these fields and Bonsai only records them.
    assert set(Curriculum.model_fields) == set(GOOD_CURRICULUM)
    Curriculum(**GOOD_CURRICULUM)
    for key, bad in [("id", -1), ("code", "S3A"), ("code", "s03a"), ("code", "S03AB"), ("version", "k7f"), ("version", "k7f1"), ("version", "K7F2"),
                     ("path", "a"), ("path", "AB")]:
        with pytest.raises(ValidationError):
            Curriculum(**{**GOOD_CURRICULUM, key: bad})
    assert "TrainingStage" not in (CONFIG.parent / "src" / "main.bonsai").read_text(encoding="utf-8-sig")


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
    # app-rig-control reads them: trialConditions is one leaf (never indexed or carried), x-condition-fields the allow-list
    # (TrialDraw.ConditionFields, a C# test holds it to the schema), x-rules the rules linking them, x-trial-types the names.
    schema = json.loads(SCHEMA.read_text(encoding="utf-8"))
    parameters = schema["$defs"]["UclOpenCoenDomeDecisionMakingTaskParameters"]
    assert parameters["properties"]["trialConditions"]["x-whole"] is True
    assert parameters["x-condition-fields"] == list(CONDITION_FIELDS)
    assert parameters["x-rules"] == list(RULES)
    assert parameters["x-trial-types"] == list(TRIAL_TYPES)
    # Every path a rule or trial type names is a condition field, or the task's type.
    named = {path for entry in [*RULES, *TRIAL_TYPES] for part in ("if", "then") for path in entry.get(part, {}).get("properties", {})}
    named |= {path for entry in [*RULES, *TRIAL_TYPES] for path in [*entry.get("unused", []), *entry.get("mirrored", [])]}
    assert named <= set(CONDITION_FIELDS) | {"taskType"}
    assert "trialWeights" not in parameters["properties"]


def test_a_condition_s_set_is_built_and_checked_as_a_task():
    task = _conditions({"trialType": "visual", "weight": 3, "set": {"contrasts": [0.5, 1], "reward.rewardSizeUl": [2, 3], "visualStimulus.initialAzimuthsDeg": [60]}},
                       {"trialType": "auditory", "weight": 1})
    assert [c.weight for c in task.trial_conditions] == [3, 1]
    for condition, words in [
        ({"trialType": "visual", "weight": 1, "set": {"trialStructure.itiMinSeconds": 4}}, "sets or pairs trialStructure.itiMinSeconds, which is not a condition field"),
        ({"trialType": "visual", "weight": 1, "set": {"optogenetics.laserOnProbability": 1}}, "with optogenetics off"),
        ({"trialType": "visual", "weight": 1, "set": {"visualStimulus.initialAzimuthsDeg": [60, 0]}}, r"(?s)trial_conditions\[0\] \(visual\).*must not hold 0"),
        ({"trialType": "visual", "weight": 1, "set": {"reward.rewardSizeUl": [2, 99]}}, r"trial_conditions\[0\] \(visual\) with \{'reward.rewardSizeUl': 99\}"),
        ({"trialType": "visual", "weight": 1, "set": {"reward.rewardSizeUl": []}}, "is an empty list"),
        ({"trialType": "visual", "weight": 0}, "a weight above 0"),
    ]:
        with pytest.raises(ValidationError, match=words):
            _conditions(condition)


def test_a_condition_may_expand_into_at_most_the_cap():
    cells = {"reward.rewardSizeUl": [1, 2, 3, 4, 5, 6, 7, 8], "autoRewardProbability": [0, 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7]}
    _conditions({"trialType": "visual", "weight": 1, "set": cells})
    cells["autoRewardProbability"].append(0.8)
    with pytest.raises(ValidationError, match=f"expands into 72 combinations, more than {MAX_COMBINATIONS}"):
        _conditions({"trialType": "visual", "weight": 1, "set": cells})


def test_a_condition_pins_the_laser_with_optogenetics_on():
    data = yaml.safe_load((CONFIG / "task-discrimination-left-right.yml").read_text())["taskParameters"]
    on = {**data["optogenetics"], "enabled": True, "laserPowerMw": 2.0, "laserDurationSeconds": 0.8, "zapitConditionsPath": "C:\\zapit\\conditions.yml"}
    _conditions({"trialType": "left", "weight": 1, "set": {"optogenetics.laserOnProbability": 1}}, {"trialType": "right", "weight": 1, "set": {"optogenetics.laserOnProbability": 0}},
                name="task-discrimination-left-right.yml", optogenetics=on)


def test_condition_edges_a_zero_among_others_duplicates_one_condition_the_weight_cap_and_a_wrong_type():
    _conditions({"trialType": "visual", "weight": 0}, {"trialType": "visual", "weight": 2}, {"trialType": "visual", "weight": 1, "set": {"contrasts": [1]}})
    _conditions({"trialType": "neutral", "weight": 1})
    _conditions({"trialType": "visual", "weight": 1000000}, {"trialType": "auditory", "weight": 1000000})
    with pytest.raises(ValidationError, match="1000000"):
        _conditions({"trialType": "visual", "weight": 1000001})
    with pytest.raises(ValidationError):
        _conditions({"trialType": "visual", "weight": 1, "set": {"reward.rewardSizeUl": "abc"}})
    with pytest.raises(ValidationError):
        _conditions({"trialType": "visual", "weight": 1, "set": {"visualStimulus.initialAzimuthsDeg": "left"}})
    with pytest.raises(ValidationError, match="at least 1|no condition|a weight above 0"):
        _conditions()


def test_weights_may_add_to_the_draw_s_int_and_no_more():
    # Go refuses the same sum (TrialDraw.CheckTask): 2147 conditions at the cap and one of 483647 make exactly int.MaxValue.
    full = [{"trialType": "visual", "weight": 1000000}] * 2147
    _conditions(*full, {"trialType": "auditory", "weight": 483647})
    with pytest.raises(ValidationError, match=f"weights add to more than {MAX_WEIGHT_SUM}"):
        _conditions(*full, {"trialType": "auditory", "weight": 483648})


def test_side_target_keeps_its_azimuth_read():
    # visualSide and auditorySide set the reward side from the picked azimuth, shown or not (TrialDraw.New).
    base = _task("task-localization.yml").model_dump(by_alias=True, mode="json")
    unshown = {**base, "contrasts": [0], "auditoryStimulus": {**base["auditoryStimulus"], "amplitude": 0}}
    assert unused_paths(RULES, {**unshown, "target": "left"}) == {"visualStimulus.initialAzimuthsDeg", "auditoryStimulus.initialAzimuthsDeg"}
    assert unused_paths(RULES, {**unshown, "target": "visualSide"}) == {"auditoryStimulus.initialAzimuthsDeg"}
    assert unused_paths(RULES, {**unshown, "target": "auditorySide"}) == {"visualStimulus.initialAzimuthsDeg"}
