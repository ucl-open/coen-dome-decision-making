import json
import re
from pathlib import Path

import pydantic
import pytest
import yaml
from pydantic import ValidationError

from ucl_open_coen_dome_decision_making.task import (
    RULE_KEYS,
    TRIAL_KEYS,
    AuditoryStimulus,
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
    # The laser is off unless the experimenter turns it on (Pip, 16 Nov 2026).
    assert yaml.safe_load(path.read_text())["taskParameters"]["optogenetics"]["enabled"] is False


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
        AuditoryStimulus(
            speakerLocations=[
                {"azimuth": -60, "elevation": 15},
                {"azimuth": 0, "elevation": 15},
                {"azimuth": 60, "elevation": 15},
            ]
        )


def _task(name):
    data = yaml.safe_load((CONFIG / name).read_text())
    return UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data["taskParameters"])


def test_the_localization_file_moves_the_spouts_on_every_stage():
    task = _task("task-localization.yml")
    assert task.task_type == "localization"
    assert all(stage.spout_movements for stage in task.training_stages)


def test_a_task_file_must_name_its_task_type():
    data = yaml.safe_load((CONFIG / "task-localization.yml").read_text())["taskParameters"]
    del data["taskType"]
    with pytest.raises(ValidationError, match="task_type|taskType"):
        UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)


def test_the_discrimination_files_keep_the_spouts_in_for_habituation():
    for name, task_type in (("task-discrimination-left-right.yml", "discriminationLeftRight"), ("task-discrimination-go-no-go.yml", "discriminationGoNoGo")):
        task = _task(name)
        assert task.task_type == task_type
        assert not task.training_stages[0].spout_movements
        assert all(stage.spout_movements for stage in task.training_stages[1:])
    assert _task("task-discrimination-left-right.yml").left_modality == "visual"
    assert _task("task-discrimination-go-no-go.yml").go_modality == "visual"


def test_the_two_discrimination_files_differ_only_in_what_the_task_needs():
    # Same stages, mixes and timing (Gerion's), go and noGo where left-right has left and right; go-no-go names goModality,
    # offers one spout and repeats go and noGo alone, as left-right left and right.
    lr = yaml.safe_load((CONFIG / "task-discrimination-left-right.yml").read_text())["taskParameters"]
    gng = yaml.safe_load((CONFIG / "task-discrimination-go-no-go.yml").read_text())["taskParameters"]
    assert all(s["bothSpoutsProbability"] == 0 for s in gng["trainingStages"])
    roles = {"go": "left", "noGo": "right"}
    for data, key in ((lr, "leftModality"), (gng, "goModality")):
        del data["taskType"], data[key]
        for block, name in [(data, "trialWeights"), (data["repeats"], "rulesPerType")] + [(s, "trialWeights") for s in data["trainingStages"]]:
            if name in block:
                block[name] = {roles.get(k, k): v for k, v in block[name].items()}
        for s in data["trainingStages"]:
            del s["bothSpoutsProbability"]
    assert lr == gng


def test_go_no_go_refuses_both_spouts():
    data = yaml.safe_load((CONFIG / "task-discrimination-go-no-go.yml").read_text())["taskParameters"]
    data["trainingStages"][2]["bothSpoutsProbability"] = 0.5
    with pytest.raises(ValidationError, match="both_spouts_probability must be 0"):
        UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)


@pytest.mark.parametrize("name", ["task-localization.yml", "task-discrimination-left-right.yml"])
def test_a_stage_without_spout_movements_needs_both_spouts(name):
    # The SpoutsIn cue's LeftIn or RightIn draws the other spout back, never returned without spout movements.
    data = yaml.safe_load((CONFIG / name).read_text())["taskParameters"]
    data["trainingStages"][0].update(spoutMovements=False, bothSpoutsProbability=0.5)
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
    # Pip, 13 Nov 2026, measured from 18 of Gerion's sessions: 1.5 s drink + max(ITI - 2, 0) with ITI 1.5-2.5 s, so
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
    for text in (TrialStructure.model_fields["iti_min_seconds"].description, column):
        assert "drinkSeconds" in text and "timeout" in text

@pytest.mark.parametrize("path", TASK_FILES, ids=lambda p: p.name)
def test_rig_control_keeps_the_zapit_file(path):
    # rig-control copies a task field ending in ConfigPath beside the task file at Go (taskfile.keep_files):
    # a field not ending so is not kept.
    opto = yaml.safe_load(path.read_text())["taskParameters"]["optogenetics"]
    assert [k for k in opto if k.endswith("Path")] == ["zapitConditionsPath"]


def test_laser_power_is_required_with_optogenetics_on():
    # Pip, 10 Nov 2026: this workflow sends and logs laserPowerMw as coen-dome-navigation does, required when on.
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


def _with(name="task-discrimination-left-right.yml", opto=None, structure=None, stage=None):
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
    on = {"enabled": True, "zapitConditionsPath": "C:\\zapit\\conditions.yml", "laserPowerMw": 3.0}
    _with(opto={**on, "laserDurationSeconds": 0.8})
    with pytest.raises(ValidationError, match="laser_duration_seconds"):
        _with(opto={**on, "laserDurationSeconds": 0.81})
    _with(opto={**on, "enabled": False, "laserDurationSeconds": 2.5})


@pytest.mark.parametrize(
    "block, key, value",
    [("trialStructure", "itiMaxSeconds", float("inf")), ("visualStimulus", "fixedAzimuthsDeg", [float("nan"), 60]), ("reward", "largeRewardMultiplier", float("inf")),
     ("response", "degreesPerLick", float("nan"))],
)
def test_the_task_s_own_blocks_refuse_inf_and_nan(block, key, value):
    # Each is a lib-open-rigs TaskBlock (allow_inf_nan off), as the lab's blocks are.
    data = yaml.safe_load((CONFIG / "task-discrimination-left-right.yml").read_text())["taskParameters"]
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


@pytest.mark.parametrize("stage", [{"lickQuietSeconds": 10.5}, {"wheelQuietSeconds": 10.5, "wheelQuietCm": 1.0}, {"wheelQuietSeconds": 1.0, "wheelQuietCm": 20.5}])
def test_a_quiet_wait_above_its_maximum_is_refused(stage):
    # Pip, 15 Nov 2026: a long wait and a licking mouse would hold the trial start with no message. TrialDraw.CheckTask refuses it at Go too.
    _with(stage={"lickQuietSeconds": 10.0, "wheelQuietSeconds": 10.0, "wheelQuietCm": 20.0})
    with pytest.raises(ValidationError, match="less than or equal to"):
        _with(stage=stage)


def test_the_constants_are_the_c_sharp_s():
    import re
    logic = (CONFIG.parent / "src" / "Extensions" / "TrialLogic.cs").read_text(encoding="utf-8-sig")
    # Zapit's reply margin is lib-open-rigs' alone (ZapitTrial.ReplyMarginSeconds, ZAPIT_REPLY_MARGIN_SECONDS).
    assert "ZapitTrial.ReplyMarginSeconds" in logic and not re.search(r"const double \w*Margin", logic)
    task_py = (CONFIG.parent / "src" / "ucl_open_coen_dome_decision_making" / "task.py").read_text(encoding="utf-8")
    # The ramp-down and reward caps are lib-open-rigs' too, each tied there to its C# value.
    assert "from open_rigs.components.zapit import ZAPIT_MAX_RAMP_DOWN_SECONDS" in task_py
    assert "check_zapit_free_in_time" in task_py
    assert "from open_rigs.rigs.lab_rig import MAX_REWARD_UL" in task_py
    # No constants of its own: the quiet waits' caps are lib-open-rigs' too (open_rigs.core.task QuietWait in task.py,
    # OpenRigs.Core QuietWait.SettingsProblems at Go).
    assert not re.findall(r"(?m)^([A-Z_]+) = [0-9.]+", task_py) and "QuietWait.SettingsProblems" in logic
    assert "ZapitConditions.MaximumRampDownSeconds" in logic


def test_a_reward_over_the_cap_is_refused():
    from open_rigs.rigs.lab_rig import MAX_REWARD_UL

    _with(stage={"rewardSizeUl": MAX_REWARD_UL})
    with pytest.raises(ValidationError, match="less than or equal to 20"):
        _with(stage={"rewardSizeUl": MAX_REWARD_UL + 1})


def test_a_large_reward_over_the_cap_is_refused():
    data = yaml.safe_load((CONFIG / "task-discrimination-left-right.yml").read_text())["taskParameters"]
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


def test_the_modalities_are_visual_or_auditory():
    data = yaml.safe_load((CONFIG / "task-discrimination-left-right.yml").read_text())["taskParameters"]
    for key in ("leftModality", "goModality"):
        UclOpenCoenDomeDecisionMakingTaskParameters.model_validate({**data, key: "auditory"})
        with pytest.raises(ValidationError):
            UclOpenCoenDomeDecisionMakingTaskParameters.model_validate({**data, key: "smell"})

def test_reward_size_says_the_calibration_plans_its_pulses():
    from ucl_open_coen_dome_decision_making.task import TrainingStage
    assert "calibration" in TrainingStage.model_fields["reward_size_ul"].description


def test_the_stimulus_never_outlasts_the_response_window():
    from ucl_open_coen_dome_decision_making.task import TrialStructure
    TrialStructure(stimulusSeconds=1.5, responseDelaySeconds=0, responseWindowSeconds=1.5)
    with pytest.raises(ValidationError, match="stimulus_seconds"):
        TrialStructure(stimulusSeconds=2.0, responseDelaySeconds=0, responseWindowSeconds=1.5)


def test_the_stimulus_is_off_before_the_next_one_starts():
    from ucl_open_coen_dome_decision_making.task import TrialStructure
    TrialStructure(stimulusSeconds=1.5, itiMinSeconds=1.5, itiMaxSeconds=2, drinkSeconds=1, responseDelaySeconds=0, responseWindowSeconds=3)
    with pytest.raises(ValidationError, match="stimulus_seconds must not exceed iti_min_seconds"):
        TrialStructure(stimulusSeconds=1.6, itiMinSeconds=1.5, itiMaxSeconds=2, drinkSeconds=1, responseDelaySeconds=0, responseWindowSeconds=3)


def test_weights_and_repeat_rules_are_keyed_by_the_task_type_s_keys():
    # Localization by what a trial shows (the trialType column's words), the discriminations by role (Pip, 16 Nov 2026);
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
        assert set(task["trialWeights"]) == set(TRIAL_KEYS[task_type]), path.name
        assert set(task["repeats"]["rulesPerType"]) == set(RULE_KEYS[task_type]), path.name
        assert all(set(s["trialWeights"]) == set(TRIAL_KEYS[task_type]) for s in task["trainingStages"] if "trialWeights" in s), path.name


def test_the_top_level_weights_are_the_last_stage_s_mix_and_the_earlier_stages_keep_theirs():
    # Today's shaping unchanged (Pip, 16 Nov 2026): the last stage takes the task's weights.
    for path in TASK_FILES:
        task = UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(yaml.safe_load(path.read_text())["taskParameters"])
        assert task.training_stages[-1].trial_weights is None
        assert task.weights_of(task.training_stages[-1]) == task.trial_weights
        assert task.training_stages[0].trial_weights is not None


@pytest.mark.parametrize(
    "name, where, block, words",
    [
        ("task-discrimination-go-no-go.yml", "trialWeights", {"visual": 10, "auditory": 10, "audiovisualCoherent": 5, "audiovisualConflict": 5, "neutral": 1}, "trial_weights must give exactly go, noGo"),
        ("task-discrimination-left-right.yml", "trialWeights", {"go": 10, "noGo": 10, "audiovisualCoherent": 5, "audiovisualConflict": 5, "neutral": 1}, "trial_weights must give exactly left, right"),
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


def test_a_go_no_go_stage_with_no_go_trials_is_refused():
    data = yaml.safe_load((CONFIG / "task-discrimination-go-no-go.yml").read_text())["taskParameters"]
    data["trialWeights"]["go"] = 0   # stages 3 and 4 take it
    with pytest.raises(ValidationError, match=r"training_stages\[3\] \(Stage 3\): its trial weights' go is 0"):
        UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data)




def test_every_task_file_states_its_task_type():
    """app-rig-control's stage suggestion reads taskParameters.taskType from the task file itself (its
    view's [task.suggest]); a file leaving it to the schema's default got "taskType is not a field of
    the task file" and no suggestion on every localization launch (BLUE-OPTO-2, 14 Nov 2026)."""
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


@pytest.mark.parametrize("path", TASK_FILES, ids=lambda path: path.name)
def test_training_stage_s_maximum_is_the_task_file_s_last_row(path):
    # A row added to trainingStages is reachable only once ExperimentControl's TrainingStage Maximum (main.bonsai) says so.
    declared = re.search(r"<Name>TrainingStage</Name>.*?<Maximum>(\d+)</Maximum>", (CONFIG.parent / "src" / "main.bonsai").read_text(encoding="utf-8-sig"), re.S)
    assert int(declared.group(1)) == len(yaml.safe_load(path.read_text(encoding="utf-8"))["taskParameters"]["trainingStages"]) - 1


@pytest.mark.parametrize("where, key, value", [
    (("response",), "thresholdLicks", 0), (("repeats",), "maxRepeats", -1), (("trainingStages", 0), "rewardSizeUl", 0)])
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
