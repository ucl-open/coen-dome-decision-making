import json
import re
import warnings
import wave
from pathlib import Path

import pydantic
import pytest
import yaml
from open_rigs.rigs import conditioned_task as ct
from open_rigs.rigs.conditions import MAX_COMBINATIONS, flatten, unused_paths
from pydantic import ValidationError

from ucl_open_coen_dome_decision_making.task import (
    MAX_WEIGHT_SUM,
    OLD_ZAPIT_ON_IGNORED,
    OldZapitOnIgnored,
    REQUIRED_CONDITIONS,
    RULES,
    SESSION_FIELDS,
    TARGETS,
    TaskType,
    TrialCondition,
    TRIAL_TYPES,
    LIST_FIELDS,
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
    # The laser is off unless the experimenter turns it on: neither Zapit box ticked, and no old enabled.
    opto = yaml.safe_load(path.read_text())["taskParameters"]["optogenetics"]
    assert opto["zapitBonsai"] is False and opto["zapitMatlab"] is False and "enabled" not in opto


@pytest.mark.parametrize("path", TASK_FILES, ids=lambda path: path.name)
def test_committed_task_files_start_at_the_two_azimuths(path):
    visual = yaml.safe_load(path.read_text())["taskParameters"]["visualStimulus"]
    assert (visual["initialAzimuthsDeg"], visual["initialElevationDeg"]) == ([-60, 60], 15)
    sound = yaml.safe_load(path.read_text())["taskParameters"]["auditoryStimulus"]   # the speakers' (Gerion, 20 Nov 2026)
    assert (sound["initialAzimuthsDeg"], sound["initialElevationDeg"]) == ([-90, 90], 15)


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
    assert not task.spouts.always_in


def test_a_task_file_must_name_its_task_type():
    data = yaml.safe_load((CONFIG / "task-localization.yml").read_text())["taskParameters"]
    del data["taskType"]
    with pytest.raises(ValidationError, match="task_type|taskType"):
        UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)


def test_the_discrimination_files_keep_the_spouts_in_for_habituation():
    for name, task_type in (("task-discrimination-left-right.yml", "discriminationLeftRight"), ("task-discrimination-go-no-go.yml", "discriminationGoNoGo")):
        task = _task(name)
        assert task.task_type == task_type
        assert task.spouts.always_in   # the defaults are habituation's, the curriculum's first stage


def test_the_two_discrimination_files_differ_only_in_what_the_task_needs():
    # Same mixes and timing (Gerion's), go and noGo where left-right has left and right; both have noReward probes
    # (left-right with both spouts in, go-no-go one spout by design, with no setting).
    lr = yaml.safe_load((CONFIG / "task-discrimination-left-right.yml").read_text())["taskParameters"]
    gng = yaml.safe_load((CONFIG / "task-discrimination-go-no-go.yml").read_text())["taskParameters"]
    roles = {"go": "left", "noGo": "right"}
    assert gng["penalty"]["noResponse"] is None   # go/no-go never penalises a miss
    lr["penalty"]["noResponse"] = None
    for data in (lr, gng):
        del data["taskType"]
        for c in data["trialConditions"]:
            c["set"]["target"] = roles.get(c["set"]["target"], c["set"]["target"])
    assert lr == gng


@pytest.mark.parametrize("name", [path.name for path in TASK_FILES])
def test_single_spouts_and_spouts_staying_in_need_no_other_setting(name):
    # P7: the task type says which spouts are used; spouts.alwaysIn and spouts.singleSpoutProbability are refused in no combination.
    data = yaml.safe_load((CONFIG / name).read_text())["taskParameters"]
    for stay in (False, True):
        for single in (0, 0.5, 1):
            UclOpenCoenDomeDecisionMakingTaskParameters.model_validate({**data, "spouts": {**data["spouts"], "alwaysIn": stay, "singleSpoutProbability": single}})


def test_spouts_staying_in_is_condition_level():
    # P19: a condition may set it; the C# reads each trial's (lib-open-rigs SpoutCommands, the trial's SpoutsAlwaysIn).
    assert ct.session_field("spouts.alwaysIn", SESSION_FIELDS) is None
    _conditions({"weight": 1, "set": {"spouts.alwaysIn": True, "spouts.singleSpoutProbability": 1}})


def test_no_text_says_a_probe_s_side_is_drawn_at_random():
    # A two-modality or neutral trial is a probe (target noReward or random, a coin on a random trial's choice),
    # never a trial whose side is drawn at random; and a random probe's tails is penalised.
    stale = ("drawn at random, left or right", "side drawn at random", "a probe never does", "`rewardSizeUl` 0 where", "`rewardSizeUl` above 0", "gain stays 20")
    for path in [SCHEMA, CONFIG.parent / "BACKLOG.md", CONFIG.parent / "README.md", *TASK_FILES]:
        text = path.read_text(encoding="utf-8")
        assert not [s for s in stale if s in text], path.name


def test_every_task_file_has_the_lab_s_timing_defaults():
    # Pip, 20 Nov 2026: a rewarded trial is its outcome, 1.5 s to drink, then a 2.5 to 3.5 s interval; a penalised one
    # 4 s of timeout, then the interval; neither, the interval. The reward tone is mute, the penalty flash off.
    for path in TASK_FILES:
        task = _task(path.name)
        s, penalty = task.trial_structure, task.penalty
        assert (s.iti_min_seconds, s.iti_max_seconds, task.spouts.reward_consumption_seconds, penalty.incorrect.timeout_seconds) == (2.5, 3.5, 1.5, 4.0)
        assert task.reward.tone.level_dbfs == "mute" and penalty.incorrect.sound.level_dbfs == -35
        assert penalty.incorrect.flash.brightness == 0 and penalty.incorrect.flash.rate_hz == 10
        if task.task_type == "discriminationGoNoGo":
            assert penalty.no_response is None
        else:
            assert penalty.no_response.timeout_seconds == 4.0 and penalty.no_response.flash.brightness == 0
            assert penalty.no_response.sound.level_dbfs == "mute"   # no error tone on a miss (Pip, 20 Nov 2026)


def test_the_reward_tone_is_mute_until_the_workflow_plays_one():
    data = yaml.safe_load((CONFIG / "task-localization.yml").read_text())["taskParameters"]
    data["reward"]["tone"] = {"levelDbfs": -50}
    with pytest.raises(ValidationError, match="reward.tone.level_dbfs must be mute"):
        UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)

def test_the_iti_descriptions_say_the_drink_is_in_the_interval():
    from ucl_open_coen_dome_decision_making.task import TrialStructure

    columns = yaml.safe_load((CONFIG.parent / "schemas" / "trial-columns.yml").read_text(encoding="utf-8"))
    # The column's description is the dome tasks' shared one; this task's detail is its note.
    column = columns["itiSeconds"]["description"] + " " + columns["itiSeconds"]["note"]
    for text in (TrialStructure.__doc__, column):
        assert "rewardConsumptionSeconds" in text and "timeout" in text

@pytest.mark.parametrize("path", TASK_FILES, ids=lambda p: p.name)
def test_rig_control_keeps_the_zapit_file(path):
    # rig-control copies a task field ending in ConfigPath beside the task file at Go (taskfile.keep_files):
    # a field not ending so is not kept.
    opto = yaml.safe_load(path.read_text())["taskParameters"]["optogenetics"]
    assert [k for k in opto if k.endswith("Path")] == ["zapitConditionsPath"]


def test_laser_power_is_required_with_optogenetics_on():
    # This workflow sends and logs laserPowerMw as coen-dome-navigation does, required when on.
    data = yaml.safe_load((CONFIG / "task-discrimination-left-right.yml").read_text())["taskParameters"]
    on = {**data["optogenetics"], "zapitBonsai": True, "zapitConditionsPath": "C:\\zapit\\conditions.yml", "laserDurationSeconds": 0.8}
    with pytest.raises(ValidationError, match="laser_power_mw is not set"):
        UclOpenCoenDomeDecisionMakingTaskParameters.model_validate({**data, "optogenetics": on})
    with pytest.raises(ValidationError):
        UclOpenCoenDomeDecisionMakingTaskParameters.model_validate({**data, "optogenetics": {**on, "laserPowerMw": 0}})
    UclOpenCoenDomeDecisionMakingTaskParameters.model_validate({**data, "optogenetics": {**on, "laserPowerMw": 2.0}})
    # The same rule in the committed schema, which is what rig-control checks before Go.
    import json
    schema = json.loads((CONFIG.parent / "src" / "DataSchemas" / "ucl_open_coen_dome_decision_making_task.json").read_text(encoding="utf-8"))
    rule = schema["$defs"]["Optogenetics"]
    assert [branch["properties"] for branch in rule["if"]["anyOf"]] == [{"zapitBonsai": {"const": True}}, {"zapitMatlab": {"const": True}}]
    assert rule["then"]["required"] == ["laserPowerMw"] and "enabled" not in rule["properties"]


def test_zapit_needs_its_file_in_the_model_and_the_schema():
    # An empty zapitConditionsPath with Zapit ticked must fail rig-control's schema check, not stop the experiment at its
    # first trial; the form says to set it first, and coen-dome-navigation's schema refuses it too.
    data = yaml.safe_load((CONFIG / "task-discrimination-left-right.yml").read_text())["taskParameters"]
    on = {**data["optogenetics"], "zapitBonsai": True, "laserPowerMw": 2.0, "zapitConditionsPath": ""}
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
    task["taskParameters"]["optogenetics"] = {**on, "zapitBonsai": False}
    assert jsonschema.Draft202012Validator(schema).is_valid(task)


def _with(name="task-discrimination-left-right.yml", opto=None, structure=None, spouts=None, stimulus=None):
    data = yaml.safe_load((CONFIG / name).read_text())["taskParameters"]
    data["optogenetics"].update(opto or {})
    data["trialStructure"].update(structure or {})
    data["spouts"].update(spouts or {})
    data["stimulus"].update(stimulus or {})
    return UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)


@pytest.mark.parametrize("lead", [2.3, 0.5, 0.1, 0.0, -0.5, -2.5])
def test_a_signed_lead_inside_the_interval_and_the_response_window_is_accepted(lead):
    # Discrimination: itiMinSeconds 2.5 less the 0.2 s arm time; the response window 0.5 + 2.0 s.
    assert _with(opto={"laserToStimulusSeconds": lead}).optogenetics.laser_to_stimulus_seconds == lead


@pytest.mark.parametrize("lead, words", [(2.31, "reply margin"),  # the shared block's words for the same 0.2 s (ZAPIT_REPLY_MARGIN_SECONDS)
                                          (-2.51, "after the response window")])
def test_a_lead_outside_them_is_refused(lead, words):
    with pytest.raises(ValidationError, match=words):
        _with(opto={"laserToStimulusSeconds": lead})


def test_a_laser_that_would_delay_the_next_arm_is_refused():
    # TrialDraw.MaxLaserSeconds' rule: the trial's end (0.5 s stimulus to window + the 2.0 s window, which always runs in
    # full) + 2.5 s interval - the ramp-down - 0.2 s margin. The ramp-down is the conditions file's, which only Go reads
    # (TrialDraw.LaserProblems): here 4.8 s, the file's taken off at Go.
    on = {"zapitBonsai": True, "zapitConditionsPath": "C:\\zapit\\conditions.yml", "laserPowerMw": 3.0}
    _with(opto={**on, "laserDurationSeconds": 4.8})
    with pytest.raises(ValidationError, match=r"laser_duration_seconds \(4.81 s\) must be at most 4.8 s"):
        _with(opto={**on, "laserDurationSeconds": 4.81})
    _with(opto={**on, "zapitBonsai": False, "laserDurationSeconds": 5.0})
    # With laserEnd stimulusOff laserDurationSeconds is the laser's cap, its end then followed by the ramp-down: the same limit.
    _with(opto={**on, "laserEnd": "stimulusOff", "laserDurationSeconds": 4.8})
    with pytest.raises(ValidationError, match="laser_duration_seconds"):
        _with(opto={**on, "laserEnd": "stimulusOff", "laserDurationSeconds": 4.81})
    with pytest.raises(ValidationError, match="Input should be 'duration' or 'stimulusOff'"):
        _with(opto={**on, "laserEnd": "outcome"})


def test_with_laser_end_stimulus_off_the_cap_covers_the_stimulus_s_own_off():
    # TrialDraw.CheckTask's same rule: the trigger 0.1 s before the stimulus, off 1.5 s after it, so a cap of at least 1.6 s.
    on = {"zapitBonsai": True, "zapitConditionsPath": "C:\\zapit\\conditions.yml", "laserPowerMw": 3.0, "laserEnd": "stimulusOff"}
    _with(opto={**on, "laserDurationSeconds": 1.6})
    with pytest.raises(ValidationError, match=r"laserDurationSeconds \(1.5 s\) is the laser's cap .* goes off 1.6 s after the trigger"):
        _with(opto={**on, "laserDurationSeconds": 1.5})
    _with(opto={**on, "laserEnd": "duration", "laserDurationSeconds": 1.5})
    _with(opto={**on, "zapitBonsai": False, "laserDurationSeconds": 1.5})
    # An off that follows the answer is not known at Go: the cap may end the laser first.
    _with(opto={**on, "laserDurationSeconds": 1.5}, stimulus={"durationSeconds": None, "responseToStimulusOffSeconds": 0.1})


def test_with_laser_end_stimulus_off_a_trigger_after_the_stimulus_s_own_off_is_refused():
    # TrialDraw.CheckTask's same rule: the stimulus off 1.5 s after its onset, so the trigger at most 1.5 s after it, exclusive.
    on = {"zapitBonsai": True, "zapitConditionsPath": "C:\\zapit\\conditions.yml", "laserPowerMw": 3.0, "laserEnd": "stimulusOff"}
    _with(opto={**on, "laserToStimulusSeconds": -1.4})
    with pytest.raises(ValidationError, match=r"laserToStimulusSeconds \(-1.5 s\) triggers the laser 1.5 s after the stimulus onset, when the stimulus .* is already off"):
        _with(opto={**on, "laserToStimulusSeconds": -1.5})
    _with(opto={**on, "laserEnd": "duration", "laserToStimulusSeconds": -1.5})
    _with(opto={**on, "laserToStimulusSeconds": -1.5}, stimulus={"durationSeconds": None, "responseToStimulusOffSeconds": 0.1})


def test_the_conditions_file_s_ramp_down_given_as_context_shortens_the_laser_limit():
    # On the rig app-rig-control's check_task reads the session's Zapit bank and passes its longest ramp-down (RAMP_DOWN_CONTEXT):
    # the trial's end (0.5 + the 2.0 s window, always in full) + 2.5 - 0.5 - 0.2 = 4.3 s, so Gerion's 2.5 s passes there (2.3 s,
    # refused, when the limit counted from the window's opening) and 4.4 s is refused; without the file (no context) 4.4 s
    # passes, Go deciding.
    from open_rigs.components.zapit import RAMP_DOWN_CONTEXT
    data = yaml.safe_load((CONFIG / "task-discrimination-left-right.yml").read_text())["taskParameters"]
    data["optogenetics"].update({"zapitBonsai": True, "zapitConditionsPath": "C:\\zapit\\conditions.yml", "laserPowerMw": 3.0, "laserDurationSeconds": 2.5})
    UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data, context={RAMP_DOWN_CONTEXT: 0.5})
    data["optogenetics"]["laserDurationSeconds"] = 4.4
    UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)
    with pytest.raises(ValidationError, match="must be at most 4.3 s"):
        UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data, context={RAMP_DOWN_CONTEXT: 0.5})
    UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data, context={RAMP_DOWN_CONTEXT: 0.1})
    # A shorter window brings it in by as much (1.0 s: 3.3 s); a penalty timeout leaves it (a rewarded trial has none).
    data["optogenetics"]["laserDurationSeconds"] = 3.3
    data["trialStructure"]["responseWindowSeconds"] = 1.0
    UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data, context={RAMP_DOWN_CONTEXT: 0.5})
    data["optogenetics"]["laserDurationSeconds"] = 3.4
    with pytest.raises(ValidationError, match="must be at most 3.3 s"):
        UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data, context={RAMP_DOWN_CONTEXT: 0.5})
    data["trialStructure"]["responseWindowSeconds"] = 2.0
    data["optogenetics"]["laserDurationSeconds"] = 4.3
    data["penalty"]["incorrect"]["timeoutSeconds"] = 10.0
    UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data, context={RAMP_DOWN_CONTEXT: 0.5})


def test_between_trials_counts_from_each_trial_s_end():
    # _between_trials (TrialDraw.BetweenTrials): the window always runs in full, so the next trigger and stimulus count from
    # the trial's end (0.5 + 2.0 s), not the window's opening. Across combinations: the shortest lead plus trial end, plus
    # the shortest interval less the lead, less the ramp-down and the 0.2 s arm time: with leads 0.1 and 1.0 s and a 0.5 s
    # ramp-down, 0.1 + 2.5 + (2.5 - 1.0) - 0.5 - 0.2 = 3.4 s (0.9 s from the opening).
    from ucl_open_coen_dome_decision_making.task import _between_trials
    task = yaml.safe_load((CONFIG / "task-discrimination-left-right.yml").read_text())["taskParameters"]
    task["optogenetics"].update({"zapitBonsai": True, "laserDurationSeconds": 3.4})
    other = {**task, "optogenetics": {**task["optogenetics"], "laserToStimulusSeconds": 1.0}}
    _between_trials([task, other], 0.5)
    task["optogenetics"]["laserDurationSeconds"] = 3.5
    with pytest.raises(ValueError, match="must be at most 3.4 s across the combinations"):
        _between_trials([task, other], 0.5)
    # A stimulus may last to the window's close (5.5 s with a 5 s window): its end before the next stimulus counts from there.
    task["optogenetics"]["zapitBonsai"] = False
    task["trialStructure"] = {**task["trialStructure"], "responseWindowSeconds": 5.0}
    task["stimulus"] = {**task["stimulus"], "durationSeconds": 5.5}
    _between_trials([task], 0.5)
    _localization(durationSeconds=None, responseToStimulusOffSeconds=2.5)   # from the outcome, a miss's at the window's end: at most itiMinSeconds
    with pytest.raises(ValidationError, match="response_to_stimulus_off_seconds"):
        _localization(durationSeconds=None, responseToStimulusOffSeconds=2.6)


@pytest.mark.parametrize(
    "block, key, value",
    [("trialStructure", "itiMaxSeconds", float("inf")), ("visualStimulus", "initialAzimuthsDeg", [float("nan"), 60]), ("reward", "largeMultiplier", float("inf")),
     ("response", "azimuthShiftPerLickDeg", float("nan"))],
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
    with pytest.raises(ValidationError, match="response_window_always_full needs a response_window_seconds"):
        _with(structure={"responseWindowSeconds": None})


def test_the_response_window_always_runs_in_full():
    # Fixed true: a task file may leave it out or say true, never false, and it cannot be changed once read.
    with pytest.raises(ValidationError, match="responseWindowAlwaysFull"):
        _with(structure={"responseWindowAlwaysFull": False})
    task = _with(structure={"responseWindowAlwaysFull": True})
    assert task.trial_structure.response_window_always_full is True
    with pytest.raises(ValidationError, match="frozen"):
        task.trial_structure.response_window_always_full = False


def test_the_drink_and_the_spouts_lead_fit_the_interval():
    # The drink is inside the interval (spouts.rewardConsumptionSeconds 1.5): with the spouts' lead, at most itiMinSeconds 2.5.
    _with(spouts={"stimulusToSpoutsInSeconds": -1.0, "alwaysIn": False})   # signed: in before the stimulus
    with pytest.raises(ValidationError, match=r"plus the 1.1 s the spouts come in"):
        _with(spouts={"stimulusToSpoutsInSeconds": -1.1, "alwaysIn": False})
    _with(spouts={"stimulusToSpoutsInSeconds": -1.1, "alwaysIn": True})   # always in: no lead
    with pytest.raises(ValidationError, match="reward_consumption_seconds"):
        _with(spouts={"rewardConsumptionSeconds": 2.6})


def test_the_quiet_waits_are_the_task_s_off_and_refused_negative():
    for path in TASK_FILES:
        structure = yaml.safe_load(path.read_text())["taskParameters"]["trialStructure"]
        assert (structure["lickQuietSeconds"], structure["wheelQuietSeconds"], structure["wheelQuietCm"]) == (0, 0, 0)
    _with(structure={"lickQuietSeconds": 1.0, "wheelQuietSeconds": 1.0, "wheelQuietCm": 2.0})
    _with(structure={"wheelQuietSeconds": 1.0, "wheelQuietCm": 0})   # cm 0: no movement at all (P5)
    for bad in ({"lickQuietSeconds": -1}, {"wheelQuietSeconds": -1}, {"wheelQuietCm": -1}):
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
    # ZapitDaq's arm time is lib-open-rigs' alone (ZapitDaqTrial.ArmSeconds, ZAPIT_ARM_SECONDS).
    assert "ZapitDaqTrial.ArmSeconds" in logic and not re.search(r"const double \w*Margin", logic)
    task_py = (CONFIG.parent / "src" / "ucl_open_coen_dome_decision_making" / "task.py").read_text(encoding="utf-8")
    # The ramp-down and reward caps are lib-open-rigs' too, each tied there to its C# value.
    assert "from open_rigs.components.zapit import ZAPIT_ARM_SECONDS" in task_py
    # The ramp-down is the session's bank's at Go (TrialDraw.MaxRampDownSeconds, ZapitBank.LongestRampSeconds); before, the context's.
    assert "RAMP_DOWN" not in task_py and "draw.MaxRampDownSeconds" in logic and "MaximumRampDownSeconds" not in logic
    assert "check_zapit_free_in_time" in task_py
    assert "    MAX_REWARD_UL,\n" in task_py and "class Reward(CoreReward)" in task_py
    # No constants of its own: the quiet waits' caps are lib-open-rigs' too (open_rigs.core.task LickQuietWait and
    # WheelQuietWait in task.py, OpenRigs.Core QuietWait.LickProblems and WheelProblems at Go).
    # MAX_COMBINATIONS and MAX_WEIGHT_SUM (the int.MaxValue the draw refuses a weight sum above) are open_rigs.rigs'.
    assert re.findall(r"(?m)^([A-Z_]+) = [0-9.]+", task_py) == []
    assert MAX_WEIGHT_SUM == 2**31 - 1 and "TrialConditions.Build(" in logic   # lib's build refuses a sum above int.MaxValue
    assert "TaskSchema.MaxCombinations" in logic and "QuietWait.LickProblems" in logic and "QuietWait.WheelProblems" in logic


def test_a_reward_over_the_cap_is_refused():
    from open_rigs.core.task import MAX_REWARD_UL

    data = yaml.safe_load((CONFIG / "task-discrimination-left-right.yml").read_text())["taskParameters"]
    data["reward"]["sizeUl"] = MAX_REWARD_UL
    UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)
    data["reward"]["sizeUl"] = MAX_REWARD_UL + 1
    with pytest.raises(ValidationError, match="less than or equal to 20"):
        UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)


def test_a_large_reward_over_the_cap_is_refused():
    data = yaml.safe_load((CONFIG / "task-discrimination-left-right.yml").read_text())["taskParameters"]
    data["reward"].update({"largeMultiplier": 3, "largeProbability": 0.1})
    data["reward"]["sizeUl"] = 7
    with pytest.raises(ValidationError, match=r"reward.size_ul \(7.0 ul\) x reward.large_multiplier \(3.0\) is more than 20 ul"):
        UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)
    data["reward"]["sizeUl"] = 6
    UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)
    data["reward"]["largeProbability"] = 0
    data["reward"]["sizeUl"] = 7
    UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)


def test_the_spout_wait_is_named_by_its_ends():
    # Pip's rule: a gap between two moments is named by its ends, as laserToStimulusSeconds.
    schema = (CONFIG.parent / "src" / "DataSchemas" / "ucl_open_coen_dome_decision_making_task.json").read_text(encoding="utf-8")
    assert '"stimulusToSpoutsInSeconds"' in schema and '"spoutInDelaySeconds"' not in schema
    for path in TASK_FILES:
        assert yaml.safe_load(path.read_text())["taskParameters"]["spouts"]["stimulusToSpoutsInSeconds"] == 0.5


def test_reward_size_says_the_calibration_plans_its_pulses():
    from ucl_open_coen_dome_decision_making.task import Reward
    assert "calibration" in Reward.model_fields["size_ul"].description


def _localization(**stimulus):
    from ucl_open_coen_dome_decision_making.task import UclOpenCoenDomeDecisionMakingTaskParameters
    task = yaml.safe_load((CONFIG / "task-localization.yml").read_text(encoding="utf-8"))["taskParameters"]
    task["stimulus"].update(stimulus)
    return UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(task)


def test_the_stimulus_never_outlasts_the_response_window_or_the_interval():
    # localization: response window 0.5 + 2.0 s, itiMinSeconds 1.0.
    _localization(durationSeconds=2.5)
    with pytest.raises(ValidationError, match="duration_seconds \\(2.6 s\\) is longer than the stimulus to the response window's close"):
        _localization(durationSeconds=2.6)
    # No duration: off only responseToStimulusOffSeconds after the response; both null never ends, refused.
    _localization(durationSeconds=None, responseToStimulusOffSeconds=0.5)
    with pytest.raises(ValidationError, match="both null"):
        _localization(durationSeconds=None, responseToStimulusOffSeconds=None)


def test_a_response_window_opening_before_the_spouts_is_refused():
    with pytest.raises(ValidationError, match=r"(?s)stimulus_to_response_window_seconds.*stimulus_to_spouts_in_seconds"):
        _with(structure={"stimulusToResponseWindowSeconds": 0.4}, spouts={"stimulusToSpoutsInSeconds": 0.5})
    _with(structure={"stimulusToResponseWindowSeconds": 0.5}, spouts={"stimulusToSpoutsInSeconds": 0.5})


def test_the_stimulus_rate_is_at_most_one_pulse_after_another():
    # 20 Hz is one 50 ms pulse after another: the C# pulse and the sound files' length.
    cs = (CONFIG.parent / "src" / "Extensions" / "AuditoryStimulusSet.cs").read_text(encoding="utf-8-sig")
    assert "public const double PulseSeconds = 0.05;" in cs
    with wave.open(str(CONFIG.parent / "src" / "sounds" / "filtered_pink_pulse_0.wav")) as w:
        assert w.getnframes() == 0.05 * w.getframerate()
    _localization(rateHz=20)
    with pytest.raises(ValidationError, match="rate_hz|rateHz"):
        _localization(rateHz=20.5)


def test_every_task_file_shares_one_stimulus_block():
    # Pip's rule: what the grating and the sound share is the stimulus's, not the trial structure's.
    for path in TASK_FILES:
        task = yaml.safe_load(path.read_text())["taskParameters"]
        assert "visualStimulusSeconds" not in task["trialStructure"]
        assert task["stimulus"] == {"durationSeconds": 1.5, "rateHz": 8, "responseToStimulusOffSeconds": None}


def test_each_committed_condition_s_trial_type_is_derived_and_logged_and_never_stored():
    # A trialType names a condition (the trialType column's words) and drives nothing: derived, never stored (P9).
    columns = yaml.safe_load((CONFIG.parent / "schemas" / "trial-columns.yml").read_text(encoding="utf-8"))
    for path in TASK_FILES:
        task = _task(path.name)
        for condition in task.trial_conditions:
            assert task.derive_trial_type(condition) in columns["trialType"]["values"], path.name
    data = yaml.safe_load(TASK_FILES[0].read_text())["taskParameters"]
    data["trialConditions"][0]["trialType"] = "visual"
    with pytest.raises(ValidationError, match=r"trialConditions\.0\.trialType\s+Extra inputs are not permitted"):
        UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)


@pytest.mark.parametrize("task_type, name", [
    ("localization", "task-localization.yml"), ("discriminationGoNoGo", "task-discrimination-go-no-go.yml"), ("discriminationLeftRight", "task-discrimination-left-right.yml")])
def test_a_target_of_another_task_type_is_refused(task_type, name):
    data = yaml.safe_load((CONFIG / name).read_text())["taskParameters"]
    for target in {"visualSide", "auditorySide", "random", "go", "noGo", "noReward", "left", "right"} - set(TARGETS[TaskType(task_type)]):
        data["trialConditions"][0]["set"]["target"] = target
        with pytest.raises(ValidationError, match=rf"taskType {task_type} needs target one of .* \(x-rules\)"):
            UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)


@pytest.mark.parametrize("set_, words", [
    ({"target": "random", "repeats.repeatOn": "notCorrect", "repeats.maxRepeats": 5}, r"a probe \(target random or noReward\) needs repeats.repeatOn never or noResponse"),
    ({"repeats.maxRepeats": 0, "repeats.repeatOn": "noResponse"}, "repeats.maxRepeats 0 needs repeats.repeatOn never"),
    ({"repeats.maxRepeats": 5, "repeats.repeatOn": "never"}, "repeats.repeatOn never needs repeats.maxRepeats 0"),
    ({"target": "auditorySide", "auditoryStimulus.initialAzimuthsDeg": [0, 60]}, r"target auditorySide needs auditory azimuths without 0 or all \(x-rules\)"),
    ({"target": "auditorySide", "auditoryStimulus.initialAzimuthsDeg": ["all", 60]}, r"target auditorySide needs auditory azimuths without 0 or all \(x-rules\)"),
    ({"auditoryStimulus.initialAzimuthsDeg": ["left"]}, "auditoryStimulus.initialAzimuthsDeg"),
    ({"auditoryStimulus.initialLevelDbfs": -0.5}, "auditoryStimulus.initialLevelDbfs"),
    ({"auditoryStimulus.initialLevelDbfs": "off"}, "auditoryStimulus.initialLevelDbfs"),
    ({"visualStimulus.initialContrasts": [-1]}, "initialContrasts must each be 0 or more"),
])
def test_a_condition_breaking_a_rule_is_refused(set_, words):
    with pytest.raises(ValidationError, match=words):
        _conditions({"weight": 1, "set": set_})


V, S = "visualStimulus.initialAzimuthsDeg", "auditoryStimulus.initialAzimuthsDeg"


@pytest.mark.parametrize("set_, pairs, trial_type", [
    ({"target": "go"}, [], "go"), ({"target": "noGo", "visualStimulus.initialContrasts": [0]}, [], "noGo"), ({"target": "left"}, [], "left"), ({"target": "right"}, [], "right"),
    ({}, [], "audiovisualCoherent"), ({V: [-60, 60], S: [-60, 60]}, [[V, S]], "audiovisualCoherent"),
    ({V: [-60, 60], S: [60, -60]}, [[V, S]], "audiovisualConflict"), ({V: [-60, 60], S: [60, -60]}, [], "audiovisualCoherent"),
    ({S: [0]}, [], "visual"), ({S: ["all"]}, [], "visual"), ({S: [0, "all"]}, [], "visual"), ({S: [0, 60]}, [], "audiovisualCoherent"),
    ({"auditoryStimulus.initialLevelDbfs": "mute"}, [], "visual"), ({"auditoryStimulus.initialLevelDbfs": ["mute", -45]}, [], "audiovisualCoherent"),
    ({"visualStimulus.initialContrasts": [0]}, [], "auditory"), ({"visualStimulus.initialContrasts": [0, 0.5]}, [], "audiovisualCoherent"),
    ({"visualStimulus.initialContrasts": [0], S: ["all"]}, [], "neutral"), ({"visualStimulus.initialContrasts": [0], S: [0]}, [], "neutral"),
    ({"visualStimulus.initialContrasts": [0], "auditoryStimulus.initialLevelDbfs": "mute"}, [], "neutral"), ({"target": "random", "visualStimulus.initialContrasts": [0], S: [0]}, [], "neutral"),
])
def test_every_trial_type_is_derived_by_x_trial_types(set_, pairs, trial_type):
    # The localization task's contrast 3 and level -40 dBFS at [-60, 60] under each set; the condition itself is not checked.
    task = UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(yaml.safe_load((CONFIG / "task-localization.yml").read_text())["taskParameters"])
    assert task.derive_trial_type(TrialCondition(weight=1, set=set_, pairs=pairs)) == trial_type


def test_a_probe_may_repeat_on_no_response_but_not_on_not_correct():
    # P6: a probe has no right answer: it repeats never or after a miss; notCorrect, set or inherited, is refused naming it.
    data = yaml.safe_load((CONFIG / "task-discrimination-go-no-go.yml").read_text())["taskParameters"]
    data["trialConditions"][4]["set"].update({"repeats.repeatOn": "noResponse", "repeats.maxRepeats": 2})
    UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)
    data["repeats"] = {"repeatOn": "notCorrect", "maxRepeats": 5}
    with pytest.raises(ValidationError, match=r"(?s)trial_conditions\[2\] \(audiovisualCoherent\).*a probe \(target random or noReward\) needs repeats.repeatOn never or noResponse"):
        UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)


def test_never_is_exactly_max_repeats_zero():
    # P3: repeatOn never <=> maxRepeats 0, on the task and on each combination; the committed files repeat never.
    for path in TASK_FILES:
        repeats = _task(path.name).repeats
        assert (repeats.repeat_on, repeats.max_repeats) == ("never", 0), path.name
        data = yaml.safe_load(path.read_text())["taskParameters"]
        assert not any(k.startswith("repeats.") for c in data["trialConditions"] for k in c["set"]), "no forced probe never lines"
        for repeats, words in (({"repeatOn": "never", "maxRepeats": 5}, "repeats.repeatOn never needs repeats.maxRepeats 0"), ({"repeatOn": "noResponse", "maxRepeats": 0}, "repeats.maxRepeats 0 needs")):
            with pytest.raises(ValidationError, match=words):
                UclOpenCoenDomeDecisionMakingTaskParameters.model_validate({**data, "repeats": repeats})


def test_target_is_each_condition_s_and_never_the_task_s():
    # P10: every condition sets target; a task-level target and a condition without one are refused.
    data = yaml.safe_load((CONFIG / "task-localization.yml").read_text())["taskParameters"]
    assert "target" not in data and all("target" in c["set"] for c in data["trialConditions"])
    with pytest.raises(ValidationError, match="target is each trial condition's"):
        UclOpenCoenDomeDecisionMakingTaskParameters.model_validate({**data, "target": "visualSide"})
    del data["trialConditions"][2]["set"]["target"]
    with pytest.raises(ValidationError, match=r"trial_conditions\[2\] \(audiovisualCoherent\) needs a target in its set"):
        UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)


def test_all_plays_on_every_speaker_and_pairs():
    # "all" is a sound azimuth item: alone, in a list, paired with the grating's azimuths; it has no side, so no cue.
    visual, sound = "visualStimulus.initialAzimuthsDeg", "auditoryStimulus.initialAzimuthsDeg"
    task = _conditions({"weight": 1, "pairs": [[visual, sound]], "set": {visual: [-60, 60], sound: ["all", 60]}},
                       {"weight": 1, "set": {"visualStimulus.initialContrasts": [0], sound: ["all"], "target": "random"}})
    assert task.derive_trial_type(task.trial_conditions[1]) == "neutral"
    items = json.loads(SCHEMA.read_text())["$defs"]["AuditoryStimulus"]["properties"]["initialAzimuthsDeg"]["items"]
    assert items == {"oneOf": [{"type": "number"}, {"const": "all", "type": "string"}]}   # oneOf: Bonsai.Sgen's List<object>


def test_a_loud_level_is_the_peak_check_s_to_refuse():
    # Pydantic cannot know the speakers: Go checks the highest level against the -1 dBFS peak (LevelAuditoryStimulusSet).
    _conditions({"weight": 1, "set": {"target": "auditorySide", "visualStimulus.initialContrasts": [0], "auditoryStimulus.initialLevelDbfs": [-40, -2]}})


def test_pairs_zip_their_lists_and_the_rest_combine():
    base = _task("task-localization.yml").model_dump(by_alias=True, mode="json")
    visual, sound = "visualStimulus.initialAzimuthsDeg", "auditoryStimulus.initialAzimuthsDeg"
    combos = ct.condition_combinations(base, {sound: [60, -60], "visualStimulus.initialContrasts": [0.5, 1], "auditoryStimulus.initialLevelDbfs": [-45, -40]}, [[visual, sound], ["visualStimulus.initialContrasts", "auditoryStimulus.initialLevelDbfs"]], LIST_FIELDS)
    # The first cell's pick changes fastest (OpenRigs.Trials TrialConditions.Build' order, Settings_Combinations' rows).
    assert combos == [
        {visual: [-60], sound: [60], "visualStimulus.initialContrasts": [0.5], "auditoryStimulus.initialLevelDbfs": -45},
        {visual: [60], sound: [-60], "visualStimulus.initialContrasts": [0.5], "auditoryStimulus.initialLevelDbfs": -45},
        {visual: [-60], sound: [60], "visualStimulus.initialContrasts": [1], "auditoryStimulus.initialLevelDbfs": -40},
        {visual: [60], sound: [-60], "visualStimulus.initialContrasts": [1], "auditoryStimulus.initialLevelDbfs": -40},
    ]
    # Unpaired, every list field (the set's or the task's) is one entry per combination, a list in a scalar cell one value,
    # by path, the first changing fastest.
    combos = ct.condition_combinations(base, {"visualStimulus.initialContrasts": [0.5, 1], "reward.sizeUl": [2, 3]}, [], LIST_FIELDS)
    assert len(combos) == 16 and combos[:3] == [
        {sound: [-90], "reward.sizeUl": 2, "visualStimulus.initialContrasts": [0.5], visual: [-60]},
        {sound: [90], "reward.sizeUl": 2, "visualStimulus.initialContrasts": [0.5], visual: [-60]},
        {sound: [-90], "reward.sizeUl": 3, "visualStimulus.initialContrasts": [0.5], visual: [-60]}]
    with pytest.raises(ValidationError, match=r"pairs \['visualStimulus.initialAzimuthsDeg', 'auditoryStimulus.initialAzimuthsDeg'\] have lists of lengths 2, 3"):
        _conditions({"weight": 1, "pairs": [[visual, sound]], "set": {sound: [-60, 0, 60]}})
    for pairs in ([[visual]], [[visual, sound], [sound, "visualStimulus.initialContrasts"]]):
        with pytest.raises(ValidationError, match="each pairs group needs two paths or more"):
            _conditions({"weight": 1, "pairs": pairs})
    with pytest.raises(ValidationError, match="pairs rngSeed, a session field"):
        _conditions({"weight": 1, "pairs": [[visual, "rngSeed"]]})


def test_a_go_no_go_task_with_no_go_trials_is_refused():
    data = yaml.safe_load((CONFIG / "task-discrimination-go-no-go.yml").read_text())["taskParameters"]
    data["trialConditions"][0]["weight"] = 0
    with pytest.raises(ValidationError, match=r"needs a condition with a weight above 0 that draws target go, .* \(x-required-conditions\)"):
        UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)


def test_a_go_no_go_miss_s_cost_is_refused():
    # A go/no-go miss is never penalised (TrialDraw.Check's same rule): the committed file's penalty.noResponse is null.
    data = yaml.safe_load((CONFIG / "task-discrimination-go-no-go.yml").read_text())["taskParameters"]
    UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)
    data["penalty"]["noResponse"] = {"timeoutSeconds": 4.0, "sound": {"levelDbfs": "mute"}}
    with pytest.raises(ValidationError, match="discriminationGoNoGo never penalises a miss"):
        UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)


def test_a_task_that_penalises_a_miss_needs_its_cost():
    # Localization penalises a miss (lib-open-rigs Penalty.check_misses), unless every trial is auto-rewarded.
    data = yaml.safe_load((CONFIG / "task-localization.yml").read_text())["taskParameters"]
    data["penalty"]["noResponse"] = None
    with pytest.raises(ValidationError, match="penalty.no_response is null, but this task penalises a miss"):
        UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)
    UclOpenCoenDomeDecisionMakingTaskParameters.model_validate({**data, "autoRewardProbability": 1.0})


def test_the_localization_file_mutes_a_miss_and_keeps_its_timeout():
    data = yaml.safe_load((CONFIG / "task-localization.yml").read_text())["taskParameters"]
    assert data["penalty"]["noResponse"] == {"timeoutSeconds": 4.0, "sound": {"levelDbfs": "mute"}, "flash": {"brightness": 0, "rateHz": 10}}


def test_a_go_no_go_condition_with_a_set_is_accepted():
    # Each condition is checked as a task of its own (_conditions_run); a noGo condition's copy has no go condition, which
    # refused every go/no-go task with any set until the go-trials rule was left to the whole task (audit round 2).
    data = yaml.safe_load((CONFIG / "task-discrimination-go-no-go.yml").read_text())["taskParameters"]
    data["trialConditions"][0]["set"]["reward.sizeUl"] = 2
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
    (("response",), "thresholdLicks", 0), (("repeats",), "maxRepeats", -1), (("reward",), "sizeUl", 0)])
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
    # Each condition's target the file's first condition's unless its set gives one (P10: target is each condition's).
    data = yaml.safe_load((CONFIG / name).read_text())["taskParameters"]
    data.update(changes)
    target = data["trialConditions"][0]["set"]["target"]
    data["trialConditions"] = [{**c, "set": {"target": target, **c.get("set", {})}} for c in conditions]
    return UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)


def test_the_schema_marks_the_conditions_whole_and_names_the_session_fields():
    # app-rig-control reads them: trialConditions is one leaf (never indexed or carried), x-session-fields what a condition
    # may not set (TrialDraw.SessionFields, a C# test holds it to the schema), x-rules the rules linking the rest,
    # x-trial-types the names, x-required-conditions what some weighted condition must draw.
    schema = json.loads(SCHEMA.read_text(encoding="utf-8"))
    parameters = schema["$defs"]["UclOpenCoenDomeDecisionMakingTaskParameters"]
    assert parameters["properties"]["trialConditions"]["x-whole"] is True
    assert parameters["x-session-fields"] == list(SESSION_FIELDS)
    assert "x-condition-fields" not in parameters
    assert parameters["x-rules"] == list(RULES)
    assert parameters["x-trial-types"] == list(TRIAL_TYPES)
    assert parameters["x-required-conditions"] == list(REQUIRED_CONDITIONS)
    # Every path a rule, trial type or requirement names is a task field; only the task's type is a session field.
    leaves = set(flatten(_task("task-localization.yml").model_dump(by_alias=True, mode="json")))
    entries = [*RULES, *TRIAL_TYPES, *REQUIRED_CONDITIONS]
    named = {path for entry in entries for part in ("if", "then", "some") for path in entry.get(part, {}).get("properties", {})}
    named |= {path for entry in entries for path in [*entry.get("unused", []), *entry.get("mirrored", [])]}
    assert named <= leaves and {ct.session_field(path, SESSION_FIELDS) for path in named} == {None, "taskType"}
    assert {ct.session_field(leaf, SESSION_FIELDS) for leaf in leaves} - {None} == set(SESSION_FIELDS)
    assert "trialWeights" not in parameters["properties"]


def test_a_condition_may_set_any_field_but_a_session_field():
    _conditions({"weight": 1, "set": {"trialStructure.itiMinSeconds": 2, "visualStimulus.initialOrientationDeg": [0, 90], "penalty.incorrect.timeoutSeconds": 2}})
    for path in ["rngSeed", "taskType", "curriculum.code", "optogenetics.zapitConditionsPath", "optogenetics.zapitBonsai", "optogenetics.zapitMatlab", "optogenetics.laserEnd"]:
        field = ct.session_field(path, SESSION_FIELDS)
        with pytest.raises(ValidationError, match=rf"sets or pairs {re.escape(path)}, a session field \(x-session-fields: {re.escape(field)} "):
            _conditions({"weight": 1, "set": {path: 1}})
    with pytest.raises(ValidationError, match="sets or pairs visualStimulus.colour, which is not a task field"):
        _conditions({"weight": 1, "set": {"visualStimulus.colour": 1}})


def test_timing_between_a_trial_and_the_next_holds_across_combinations():
    # Each combination alone is fine, but a 2.5 s drink may come before a 2 s interval (Spouts.BetweenTrialsProblems).
    with pytest.raises(ValidationError, match="rewardConsumptionSeconds 2.5 s plus the spouts' longest lead before the stimulus 0 s exceeds trialStructure.itiMinSeconds 2 s"):
        _conditions({"weight": 1, "set": {"spouts.rewardConsumptionSeconds": 2.5}}, {"weight": 1, "set": {"trialStructure.itiMinSeconds": 2}})
    _conditions({"weight": 1, "set": {"spouts.rewardConsumptionSeconds": 2.5}}, {"weight": 0, "set": {"trialStructure.itiMinSeconds": 2}})


def test_a_condition_s_set_is_built_and_checked_as_a_task():
    task = _conditions({"weight": 3, "set": {"visualStimulus.initialContrasts": [0.5, 1], "reward.sizeUl": [2, 3], "visualStimulus.initialAzimuthsDeg": [60]}},
                       {"weight": 1})
    assert [c.weight for c in task.trial_conditions] == [3, 1]
    for condition, words in [
        ({"weight": 1, "set": {"rngSeed": 4}}, "sets or pairs rngSeed, a session field"),
        ({"weight": 1, "set": {"optogenetics.laserOnProbability": 1}}, "with optogenetics off"),
        ({"weight": 1, "set": {"visualStimulus.initialAzimuthsDeg": [60, 0]}}, r"(?s)trial_conditions\[0\] \(audiovisualCoherent\).*must not hold 0"),
        ({"weight": 1, "set": {"reward.sizeUl": [2, 99]}}, r"trial_conditions\[0\] \(audiovisualCoherent\) with \{.*'reward.sizeUl': 99"),
        ({"weight": 1, "set": {"reward.sizeUl": []}}, "is an empty list"),
        ({"weight": 0}, "a weight above 0"),
    ]:
        with pytest.raises(ValidationError, match=words):
            _conditions(condition)


def test_a_condition_may_expand_into_at_most_the_cap():
    # Every list field expands too: the task's two sound azimuths double the set's 16 x 32.
    cells = {"visualStimulus.initialContrasts": [1], "visualStimulus.initialAzimuthsDeg": [60], "reward.sizeUl": [1 + i / 16 for i in range(16)], "autoRewardProbability": [i / 32 for i in range(32)]}
    assert MAX_COMBINATIONS == 1024 == 2 * 16 * 32
    _conditions({"weight": 1, "set": cells})
    cells["autoRewardProbability"].append(1)
    with pytest.raises(ValidationError, match=f"expands into 1056 combinations, more than {MAX_COMBINATIONS} \\(x-max-combinations\\)"):
        _conditions({"weight": 1, "set": cells})


def test_an_oversized_condition_is_refused_before_its_combinations_are_built():
    # Counted first, as OpenRigs.Trials TrialConditions.Build does: 2 x 1000^3 combinations would take hours and all memory to build.
    many = [i / 1000 for i in range(1000)]
    cells = {"visualStimulus.initialContrasts": [1], "visualStimulus.initialAzimuthsDeg": [60], "reward.sizeUl": [1 + x for x in many], "autoRewardProbability": many, "spouts.singleSpoutProbability": [1 - x for x in many]}
    with pytest.raises(ValidationError, match="expands into 2000000000 combinations"):
        _conditions({"weight": 1, "set": cells})


def test_a_condition_pins_the_laser_with_optogenetics_on():
    data = yaml.safe_load((CONFIG / "task-discrimination-left-right.yml").read_text())["taskParameters"]
    on = {**data["optogenetics"], "zapitBonsai": True, "laserPowerMw": 2.0, "laserDurationSeconds": 0.8, "zapitConditionsPath": "C:\\zapit\\conditions.yml"}
    _conditions({"weight": 1, "set": {"optogenetics.laserOnProbability": 1}}, {"weight": 1, "set": {"optogenetics.laserOnProbability": 0}},
                name="task-discrimination-left-right.yml", optogenetics=on)


def test_condition_edges_a_zero_among_others_duplicates_one_condition_the_weight_cap_and_a_wrong_type():
    _conditions({"weight": 0}, {"weight": 2}, {"weight": 1, "set": {"visualStimulus.initialContrasts": [1]}})
    _conditions({"weight": 1})
    _conditions({"weight": 1000000}, {"weight": 1000000})
    with pytest.raises(ValidationError, match="1000000"):
        _conditions({"weight": 1000001})
    with pytest.raises(ValidationError):
        _conditions({"weight": 1, "set": {"reward.sizeUl": "abc"}})
    with pytest.raises(ValidationError):
        _conditions({"weight": 1, "set": {"visualStimulus.initialAzimuthsDeg": "left"}})
    with pytest.raises(ValidationError, match="at least 1|no condition|a weight above 0"):
        _conditions()


def test_weights_may_add_to_the_draw_s_int_and_no_more():
    # Go refuses the same sum (TrialDraw.CheckTask): 2147 conditions at the cap and one of 483647 make exactly int.MaxValue.
    full = [{"weight": 1000000}] * 2147
    _conditions(*full, {"weight": 483647})
    with pytest.raises(ValidationError, match=f"weights add to more than {MAX_WEIGHT_SUM}"):
        _conditions(*full, {"weight": 483648})


def test_side_target_keeps_its_azimuth_read():
    # visualSide and auditorySide set the reward side from the picked azimuth, shown or not (TrialDraw.New).
    base = _task("task-localization.yml").model_dump(by_alias=True, mode="json")
    unshown = {**base, "visualStimulus": {**base["visualStimulus"], "initialContrasts": [0]}, "auditoryStimulus": {**base["auditoryStimulus"], "initialLevelDbfs": "mute"}}
    assert unused_paths(RULES, {**unshown, "target": "left"}) == {"visualStimulus.initialAzimuthsDeg", "auditoryStimulus.initialAzimuthsDeg"}
    assert unused_paths(RULES, {**unshown, "target": "visualSide"}) == {"auditoryStimulus.initialAzimuthsDeg"}
    assert unused_paths(RULES, {**unshown, "target": "auditorySide"}) == {"visualStimulus.initialAzimuthsDeg"}


def test_the_committed_task_schema_cs_is_the_schema_s():
    # TrialDraw reads x-session-fields, x-max-combinations and the list fields from TaskSchema, generated with the schema.
    assert (CONFIG.parent / "src" / "Extensions" / "TaskSchema.Generated.cs").read_text(encoding="utf-8") == ct.task_schema_cs(SESSION_FIELDS, LIST_FIELDS, TRIAL_TYPES)


@pytest.mark.parametrize("name", ["task-localization.yml", "task-discrimination-left-right.yml"])
def test_the_probe_conditions_default_to_no_reward_probes(name):
    # Neither rewarded nor penalised by default; target random stays allowed per condition.
    data = yaml.safe_load((CONFIG / name).read_text())["taskParameters"]
    targets = [c["set"]["target"] for c in data["trialConditions"]]
    assert "noReward" in targets and "random" not in targets
    for target in ("noReward", "random"):
        UclOpenCoenDomeDecisionMakingTaskParameters.model_validate({**data, "trialConditions": [{"weight": 1, "set": {"target": target}}]})


# The two Zapit boxes (Pip, 26 Nov 2026: "two boxes, mutually exclusive, Zapit-Bonsai and Zapit-MATLAB"). The MATLAB cases go
# when MATLAB Zapit is retired.
ZAPIT_ON = {"zapitConditionsPath": "C:/zapit/conditions.yml", "laserPowerMw": 3.0, "laserDurationSeconds": 0.8}


def _schema():
    return json.loads((CONFIG.parent / "src" / "DataSchemas" / "ucl_open_coen_dome_decision_making_task.json").read_text(encoding="utf-8"))


def test_neither_zapit_box_is_no_optogenetics():
    opto = _with().optogenetics
    assert not opto.zapit_bonsai and not opto.zapit_matlab and not opto.enabled
    # The lab's enabled is kept inside, never in the schema or the dump.
    assert "enabled" not in _schema()["$defs"]["Optogenetics"]["properties"]
    assert "enabled" not in opto.model_dump(by_alias=True)
    labels = {name: field["title"] for name, field in _schema()["$defs"]["Optogenetics"]["properties"].items() if name.startswith("zapit") and name != "zapitConditionsPath"}
    assert labels == {"zapitBonsai": "Zapit-Bonsai", "zapitMatlab": "Zapit-MATLAB"}


@pytest.mark.parametrize("box, field", [("zapitBonsai", "zapit_bonsai"), ("zapitMatlab", "zapit_matlab")])
def test_each_zapit_box_alone_is_an_optogenetics_experiment(box, field):
    opto = _with(opto={**ZAPIT_ON, box: True}).optogenetics
    assert getattr(opto, field) and opto.enabled
    with pytest.raises(ValidationError, match=("Zapit-Bonsai" if box == "zapitBonsai" else "Zapit-MATLAB") + " is ticked but laser_power_mw is not set"):
        _with(opto={**ZAPIT_ON, box: True, "laserPowerMw": None})


def test_both_zapit_boxes_are_refused_by_the_model_and_the_schema():
    with pytest.raises(ValidationError, match=r"optogenetics.zapitBonsai \(Zapit-Bonsai\) and optogenetics.zapitMatlab \(Zapit-MATLAB\) are both ticked"):
        _with(opto={**ZAPIT_ON, "zapitBonsai": True, "zapitMatlab": True})
    jsonschema = pytest.importorskip("jsonschema")
    task = yaml.safe_load((CONFIG / "task-discrimination-left-right.yml").read_text())
    for bonsai, matlab, valid in ((True, True, False), (True, False, True), (False, True, True), (False, False, True)):
        task["taskParameters"]["optogenetics"].update({**ZAPIT_ON, "zapitBonsai": bonsai, "zapitMatlab": matlab})
        assert jsonschema.Draft202012Validator(_schema()).is_valid(task) == valid, (bonsai, matlab)


def test_an_old_files_enabled_is_ignored_with_a_warning():
    # Pip, 26 Nov 2026: "Untick both + Go warns". An old file's Zapit On ticks neither box, and warns (never refuses).
    data = yaml.safe_load((CONFIG / "task-discrimination-left-right.yml").read_text())["taskParameters"]
    old = {k: v for k, v in data["optogenetics"].items() if k not in ("zapitBonsai", "zapitMatlab")}
    for value in (True, "true"):
        with pytest.warns(OldZapitOnIgnored) as said:
            on = UclOpenCoenDomeDecisionMakingTaskParameters.model_validate({**data, "optogenetics": {**old, **ZAPIT_ON, "enabled": value}}).optogenetics
        assert [str(w.message) for w in said] == [OLD_ZAPIT_ON_IGNORED]
        assert not on.zapit_matlab and not on.zapit_bonsai and not on.enabled
        dumped = on.model_dump(by_alias=True)
        assert dumped["zapitMatlab"] is False and dumped["zapitBonsai"] is False and "enabled" not in dumped
    assert "tick Zapit-Bonsai or Zapit-MATLAB" in OLD_ZAPIT_ON_IGNORED
    with warnings.catch_warnings():
        warnings.simplefilter("error")  # enabled: false ignores nothing, so says nothing
        off = UclOpenCoenDomeDecisionMakingTaskParameters.model_validate({**data, "optogenetics": {**old, "enabled": False}}).optogenetics
    assert not off.zapit_matlab and not off.zapit_bonsai and not off.enabled
    # The C#'s warning (OldOptogeneticsSwitch.OldZapitOnIgnored, written by CheckTaskAtGo) is the same words.
    cs = (Path(__file__).parent.parent / "src" / "Extensions" / "TrialLogic.cs").read_text(encoding="utf-8")
    assert f'public const string OldZapitOnIgnored = "{OLD_ZAPIT_ON_IGNORED}";' in cs
    for box in ("zapitBonsai", "zapitMatlab"):
        with pytest.raises(ValidationError, match="enabled, the old switch, beside zapitBonsai or zapitMatlab"):
            UclOpenCoenDomeDecisionMakingTaskParameters.model_validate({**data, "optogenetics": {**old, **ZAPIT_ON, "enabled": True, box: False}})


def test_between_trials_takes_each_zapits_margin():
    # _between_trials: ZapitDaq's 0.2 s arm time with Zapit-Bonsai, MATLAB Zapit's 0.2 s reply margin with Zapit-MATLAB (the same
    # length, named for each); none with neither.
    from ucl_open_coen_dome_decision_making.task import _between_trials
    task = yaml.safe_load((CONFIG / "task-discrimination-left-right.yml").read_text())["taskParameters"]
    for box, words in (("zapitBonsai", "arm time"), ("zapitMatlab", "reply margin")):
        task["optogenetics"].update({"zapitBonsai": False, "zapitMatlab": False, box: True, "laserDurationSeconds": 4.56})
        with pytest.raises(ValueError, match=words):
            _between_trials([task], 0.25)
        task["optogenetics"]["laserDurationSeconds"] = 4.55   # 0.1 s lead + 2.5 s trial end + (2.5 - 0.1) s interval - 0.25 s ramp-down - 0.2 s
        _between_trials([task], 0.25)
    task["optogenetics"].update({"zapitBonsai": False, "zapitMatlab": False, "laserDurationSeconds": 9.0})
    _between_trials([task], 0.25)
