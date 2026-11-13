from pathlib import Path

import pytest
import yaml
from pydantic import ValidationError

from ucl_open_coen_dome_decision_making.task import (
    AudioStimulus,
    UclOpenCoenDomeDecisionMakingTaskParameters,
    VisualStimulus,
)

CONFIG = Path(__file__).parent.parent / "config"
TASK_FILES = sorted(CONFIG.glob("task*.yml"))


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


def test_the_discrimination_iti_is_gerion_s():
    # Plan v11: itiSeconds runs from the outcome (after any timeout) to the next trial and includes drinkSeconds, as
    # Gerion's ITI includes his 2 s drink, so his 1.5-2.5 s are the file's values.
    trial = _task("task-discrimination.yml").trial_structure
    assert (trial.iti_min_seconds, trial.iti_max_seconds, trial.drink_seconds) == (1.5, 2.5, 2.0)

def test_the_iti_descriptions_say_the_drink_is_in_the_interval():
    from ucl_open_coen_dome_decision_making.task import TrialStructure

    columns = yaml.safe_load((CONFIG.parent / "schemas" / "trial-columns.yml").read_text(encoding="utf-8"))
    for text in (TrialStructure.model_fields["iti_min_seconds"].description, columns["itiSeconds"]["description"]):
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
    on = {**data["optogenetics"], "useZapit": True, "zapitConfigPath": "C:\\zapit\\conditions.yml"}
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
def test_laser_to_stimulus_is_the_task_s_and_sets_the_wait(path):
    # Pip, 10 Nov 2026: the fixed 0.5 s wait between the laser's trigger and ExecuteTrial is the task's
    # laserToStimulusSeconds (0.5 keeps the timing), on every trial.
    opto = yaml.safe_load(path.read_text())["taskParameters"]["optogenetics"]
    assert opto["laserToStimulusSeconds"] == 0.5
    workflow = (CONFIG.parent / "src" / "main.bonsai").read_text(encoding="utf-8-sig")
    assert '<Property Name="DueSeconds" Selector="TaskParameters.Optogenetics.LaserToStimulusSeconds" />' in workflow


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


def test_a_laser_outlasting_the_soonest_next_trigger_is_refused():
    # TrialDraw.SoonestNextTriggerSeconds' rule: 0.5 + 0 + max(2.0, 1.5) + 0.1 = 2.6 s in the discrimination file.
    data = yaml.safe_load((CONFIG / "task-discrimination.yml").read_text())["taskParameters"]
    on = {**data["optogenetics"], "useZapit": True, "zapitConfigPath": "C:\\zapit\\conditions.yml", "laserPowerMw": 3.0}
    UclOpenCoenDomeDecisionMakingTaskParameters.model_validate({**data, "optogenetics": {**on, "laserDurationSeconds": 2.5}})
    with pytest.raises(ValidationError, match="laser_duration_seconds"):
        UclOpenCoenDomeDecisionMakingTaskParameters.model_validate({**data, "optogenetics": {**on, "laserDurationSeconds": 2.6}})
    UclOpenCoenDomeDecisionMakingTaskParameters.model_validate({**data, "optogenetics": {**on, "useZapit": False, "laserDurationSeconds": 2.6}})
