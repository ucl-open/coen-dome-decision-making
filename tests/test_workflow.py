"""What src\\main.bonsai must keep: Harp-timed task logic, the shared blocks, and the bugs found fixed."""

import re
import xml.etree.ElementTree as ET
from pathlib import Path

import open_rigs.rigs
import yaml
from open_rigs.rigs import workflow_lint as lint
from open_rigs.rigs.lab_rig import LabRig


ROOT = Path(__file__).parents[1]
W = "{https://bonsai-rx.org/2018/workflow}"
XSI = "{http://www.w3.org/2001/XMLSchema-instance}type"
TEXT = (ROOT / "src" / "main.bonsai").read_text(encoding="utf-8-sig")
MAIN = ET.fromstring(TEXT.encode("utf-8"))
FINITE = ("rx:Take", "rx:TakeLast", "rx:First", "rx:Last")
WALL_CLOCK = ("rx:Timer", "rx:Delay", "rx:DelaySubscription", "rx:Throttle", "rx:Timeout", "rx:Interval", "rx:TimeInterval",
              "rx:Timestamp", "gl:Timer", "gl:Delay", "TimeOfDay")


def _kind(node):
    combinator = node.find(W + "Combinator")
    return combinator.get(XSI) if combinator is not None else node.get(XSI)


def _named(name, root=MAIN):
    return next(e for e in root.iter(W + "Expression") if e.findtext(W + "Name") == name)


def _scope(node):
    workflow = node.find(W + "Workflow")
    nodes = list(workflow.find(W + "Nodes"))
    edges = [(int(e.get("From")), int(e.get("To")), e.get("Label")) for e in workflow.find(W + "Edges")]
    return nodes, edges


def _upstream(edges, start):
    seen, frontier = set(), [start]
    while frontier:
        to = frontier.pop()
        for a, b, _ in edges:
            if b == to and a not in seen:
                seen.add(a)
                frontier.append(a)
    return seen


PICK = "dev:PickTrialEvents"
DEV = "{clr-namespace:OpenRigs.Devices;assembly=OpenRigs.Devices}"


def _downstream(edges, start):
    seen, frontier = set(), [start]
    while frontier:
        at = frontier.pop()
        for a, b, _ in edges:
            if a == at and b not in seen:
                seen.add(b)
                frontier.append(b)
    return seen


def _reached(kind, cue=None, group="TaskLogic"):
    """What each PickTrialEvents of `kind` (and `cue`) in `group` sends to: multicast subjects and includes, by name."""
    nodes, edges = _scope(_named(group))
    picks = [i for i, n in enumerate(nodes) if _kind(n) == PICK and n.find(W + "Combinator").findtext(DEV + "Kind") == kind
             and n.find(W + "Combinator").findtext(DEV + "Cue") == cue]
    assert picks, (kind, cue)
    reached = set()
    for p in picks:
        for i in _downstream(edges, p):
            if nodes[i].get(XSI) == "MulticastSubject":
                reached.add(nodes[i].findtext(W + "Name"))
            elif nodes[i].get("Path"):
                reached.add(nodes[i].get("Path").split(":")[1] + (f" Set={nodes[i].findtext(W + 'Set')}" if nodes[i].findtext(W + "Set") else ""))
            elif _kind(nodes[i]).startswith("zap:"):
                reached.add(nodes[i].findtext(W + "Name"))
    return reached


def test_task_logic_runs_the_trial_loop():
    # One state machine for both dome tasks (lib-open-rigs TrialLoop, 16 Nov 2026): RunTrials, given the rules from the
    # clock's thread once the experiment folder exists, into TrialEvents; no per-trial graph.
    nodes, edges = _scope(_named("TaskLogic"))
    (run,) = [i for i, n in enumerate(nodes) if _kind(n) == "ext:RunTrials"]
    into = {label: a for a, b, label in edges if b == run}
    assert _kind(nodes[into["Source1"]]) == "MemberSelector" and nodes[into["Source1"]].findtext(W + "Selector") == "Value"
    (tick,) = [a for a, b, _ in edges if b == into["Source1"]]
    assert _kind(nodes[tick]) == "core:OnNextTick"
    assert [nodes[a].findtext(W + "Name") for a, b, label in edges if b == tick and label == "Source2"] == ["Timestamp"]
    rules = _upstream(edges, tick)
    assert {nodes[i].findtext(W + "Name") for i in rules if nodes[i].get(XSI) == "SubscribeSubject"} == {"TrialRules", "PathPrefix", "Timestamp"}
    assert [nodes[into[f"Source{k}"]].findtext(W + "Name") for k in range(2, 7)] == [
        "Timestamp", "WheelDistanceCm", "LickStateLeft", "LickStateRight", "ZapitReplies"]
    assert [nodes[b].findtext(W + "Name") for a, b, _ in edges if a == run] == ["TrialEvents"]
    # The one nested graph runs once, at Go: Zapit's commands in an optogenetics experiment.
    per_element = [n.findtext(W + "Name") for n in _named("TaskLogic").iter(W + "Expression") if n.get(XSI) in lint.PER_ELEMENT]
    assert per_element == ["Zapit"]
    zapit = next(i for i, n in enumerate(nodes) if n.findtext(W + "Name") == "Zapit")
    assert {nodes[i].findtext(W + "Name") for i in _upstream(edges, zapit)} == {"Optogenetics?", "TrialRules"}


def test_the_task_is_checked_once_at_go_into_the_rules():
    # The one task check (Pip's audit A5): CheckTaskAtGo builds TrialRules from the task, the stage and rig.yml.
    assert TEXT.count('"ext:CheckTaskAtGo"') == 1
    nodes, edges = _scope(_named("TaskFile"))
    (check,) = [i for i, n in enumerate(nodes) if _kind(n) == "ext:CheckTaskAtGo"]
    assert [(nodes[b].get(XSI), nodes[b].findtext(W + "Name")) for a, b, _ in edges if a == check] == [("rx:AsyncSubject", "TrialRules")]
    assert {nodes[i].findtext(W + "Name") for i in _upstream(edges, check) if nodes[i].get(XSI) == "SubscribeSubject"} == {
        "TaskConfig", "TrainingStage", "RigConfigs"}


def test_each_trial_event_reaches_its_hardware():
    assert _reached("Start") == {"Trial", "LogRngSeed.bonsai"}   # A2: Trial at the trial's start
    assert _reached("Stimulus") == {"VisualStimulus", "AuditoryStimulus"}
    assert _reached("Update") == {"GratingAzimuthDeg"}
    assert _reached("Cue", "GratingOff") == {"VisualStimulusOff"}
    assert _reached("Cue", "SpoutsIn") == _reached("Cue", "Drink") == {"LickSpoutStage.Commands"}
    # Round 3 F13: the cue's command is SpoutsInCommand, apart from the cue and lib's LickSpoutCommand.SpoutsIn.
    assert TEXT.count('"ext:SpoutsInCommand"') == 1 and '"ext:SpoutsIn"' not in TEXT
    assert _reached("Outcome") == {"TrialRow", "RewardRequests", "LickSpoutStage.Commands", "PlayPenaltySound.bonsai"}
    assert _reached("Trigger") == {"BehaviorBoard.SetZapitTriggerLine.bonsai Set=true"}
    assert _reached("TriggerOff") == {"BehaviorBoard.SetZapitTriggerLine.bonsai Set=false"}
    assert _reached("Arm", group="Zapit") == _reached("Trigger", group="Zapit") == {"ArmZapit", "ZapitCommands"}


def test_zapit_s_start_stimulus_goes_at_arm_or_at_the_trigger():
    # ZapitTrialCommand sends at Arm (Source1) on a hardware-triggered rig, else at the trigger (Source2); it reads
    # HardwareTriggered and DurationSeconds as it starts, so it starts at Go, after TaskConfig.
    nodes, edges = _scope(_named("Zapit"))
    (arm,) = [i for i, n in enumerate(nodes) if n.findtext(W + "Name") == "ArmZapit"]
    for label, kind in (("Source1", "Arm"), ("Source2", "Trigger")):
        (into,) = [a for a, b, lab in edges if b == arm and lab == label]
        assert any(_kind(nodes[i]) == PICK and nodes[i].find(W + "Combinator").findtext(DEV + "Kind") == kind for i in _upstream(edges, into))
    mapped = " ".join(ET.tostring(nodes[a], encoding="unicode") for a, b, _ in edges if b == arm and nodes[a].get(XSI) == "PropertyMapping")
    assert 'Name="HardwareTriggered" Selector="Zapit.HardwareTriggered"' in mapped
    assert 'Name="DurationSeconds" Selector="TaskParameters.Optogenetics.LaserDurationSeconds"' in mapped


def test_each_grating_starts_at_its_own_trial_s_azimuth():
    # GratingAzimuthDeg is a PublishSubject: the grating's LocationX is its trial's visualAzimuthDeg, then each lick's move.
    nodes, edges = _scope(_named("ShowGrating"))
    (location,) = [i for i, n in enumerate(nodes) if n.get(XSI) == "PropertyMapping" and 'Name="LocationX"' in ET.tostring(n, encoding="unicode")]
    (merge,) = [a for a, b, _ in edges if b == location]
    assert _kind(nodes[merge]) == "rx:Merge"
    sources = {label: a for a, b, label in edges if b == merge}
    assert nodes[sources["Source1"]].findtext(W + "Selector") == "visualAzimuthDeg"
    assert nodes[sources["Source2"]].findtext(W + "Name") == "GratingAzimuthDeg"
    declared = next(n for n in _named("Declarations").iter(W + "Expression") if n.findtext("{clr-namespace:Bonsai.Reactive;assembly=Bonsai.Core}Name") == "GratingAzimuthDeg")
    assert declared.get(XSI) == "rx:PublishSubject"


def test_the_shared_blocks_replace_the_local_copies():
    for block in ("OpenRigs.Devices:ReadLabRig.bonsai", "OpenRigs.Devices:DomeHardware.bonsai",
                  lint.REWARDS, "OpenRigs.Devices:Zapit.ConnectForTask.bonsai",
                  "OpenRigs.Logging:LogLabExperiment.bonsai", lint.LAUNCH_SETTINGS, lint.BACKGROUND_NOISE, lint.EXPERIMENT_RECORDS):
        assert f'Path="{block}"' in TEXT
    # The speakers and the projector are DomeHardware's (Pip, 15 Nov 2026: every task has background noise and a screen).
    assert lint.SPEAKER_MIXER not in TEXT and lint.PROJECTOR_DISPLAY not in TEXT
    assert not re.search(r'Path="Extensions\\', TEXT)
    assert not list((ROOT / "src" / "Extensions").glob("*.bonsai"))


def test_every_config_root_is_the_lab_s():
    # check-workflow holds every node that reads rig.yml to one root (3 nodes: ReadLabRig, ExperimentRecords, LiveView); this holds that root to the lab's.
    assert set(re.findall(r"<(?:\w+:)?ConfigRoot>([^<]*)</", TEXT)) == {r"C:\Coen-Lab-Local\RigConfigs"}


def test_the_launch_properties():
    # No MissingCalibration: a reward valve without a calibration always stops the workflow (Pip, 15 Nov 2026).
    top = list(MAIN.find(W + "Workflow").find(W + "Nodes"))
    exposed = {p.get("Name") for n in top if n.get(XSI) == "ExternalizedMapping" for p in n.iter(W + "Property")}
    assert {"SubjectId", "SessionId", "TaskPath", "DisplayDevice", "ConfigRoot", "LaunchId"} == exposed
    assert lint.launch_id_problems(WORKFLOW) == []


def test_rewards_are_the_shared_include_s():
    # BehaviorBoard.Rewards declares RewardRequests, gives the manual drops and delivers each reward (lib batch 2).
    assert lint.reward_problems(WORKFLOW) == []


def test_the_auditory_stimuli_are_the_tasks_own_files():
    """The 100 stimuli are in the repo (production av-dome keeps them in its LocalFiles), mono 16-bit
    at the mixer's 48 kHz: nothing put them in each rig's
    calibration folder, so the workflow stopped at Start on every rig (BLUE-OPTO-2, 14 Nov 2026)."""
    import wave

    folder = ROOT / "src" / "LocalFiles" / "AuditoryStimuli"
    shapes = set()
    for i in range(100):
        with wave.open(str(folder / f"filtered_pink_pulse_train_{i}.wav")) as w:
            shapes.add((w.getnchannels(), w.getsampwidth(), w.getframerate()))
    assert len(shapes) == 1
    assert shapes.pop()[:3] == (1, 2, 48000)
    assert r"<ext:Folder>LocalFiles\AuditoryStimuli</ext:Folder>" in TEXT


def test_the_metrics_are_the_view_s():
    names = {c.findtext("{clr-namespace:OpenRigs.Control;assembly=OpenRigs.Control}Name") for c in MAIN.iter(W + "Combinator") if c.get(XSI) == "orcl:ReportMetric"}
    assert names == {"trialNumber", "target", "trialType", "visualAzimuthDeg", "audioAzimuthDeg", "rewardOfferedUl", "laserCondition"}
    # trainingStage is OpenRigs.Control's TrainingStage's (16 Nov 2026).
    assert lint.training_stage_problems(WORKFLOW) == []


def test_zapit_s_hardware_triggering_is_rig_yml_s():
    # lib-open-rigs' ZapitTrialCommand, its HardwareTriggered mapped from rig.yml's zapit.hardwareTriggered, never typed in.
    assert TEXT.count('"zap:ZapitTrialCommand"') == 1 and "ext:ZapitTrialCommand" not in TEXT
    assert '<Property Name="HardwareTriggered" Selector="Zapit.HardwareTriggered" />' in TEXT and "HardwareTriggered>true<" not in TEXT


# The lab's rules for every workflow (open_rigs.rigs.workflow_lint, one copy in lib-open-rigs).
WORKFLOW = lint.Workflow(ROOT)


def test_no_fixed_width_pulse_reaches_a_reward_valve():
    # Every reward is its RewardValve piece's calibrated pulse; checked against the lab's rig template's valve lines.
    rig = LabRig.model_validate(yaml.safe_load((Path(open_rigs.rigs.__file__).parent / "rig-template.yml").read_text(encoding="utf-8").replace("CHANGE-ME", "X")))
    assert rig.reward_valve_lines() and lint.fixed_width_pulses_on_reward_valves(WORKFLOW, rig.reward_valve_lines()) == []


def test_every_harp_delay_runs_on_the_timestamp_clock():
    _, off = lint.harp_delays_off_the_clock(WORKFLOW)
    assert off == []


def test_no_async_event_sender_completes_async_events():
    senders, finite = lint.finite_async_event_senders(WORKFLOW)
    assert senders == 0 and finite == []  # LogRngSeed sends RngSeed, LogControlEvents Control and Rewards RewardDelivered


def test_no_trial_graph_closes_a_shared_subject():
    assert lint.subjects_closed_by_a_nested_multicast(WORKFLOW) == []


def test_the_speakers_and_the_mixer_are_the_shared_block_s():
    # OpenRigs.Audio's SpeakerMixer loads the speakers and starts the mixer no sooner than 3 s after Start.
    assert lint.speaker_mixer_problems(WORKFLOW) == []


def test_every_sound_plays_at_the_task_file_s_level():
    assert lint.sound_level_problems(WORKFLOW) == []
    nodes, edges = _scope(_named("Audio"))
    level = next(i for i, n in enumerate(nodes) if _kind(n) == "ext:LevelAuditoryStimulusSet")
    assert any(nodes[a].findtext(W + "Selector") == "TaskParameters.AudioStimulus.LevelDbfs" for a in _upstream(edges, level))


def test_the_background_noise_is_the_shared_include():
    # OpenRigs.Audio's BackgroundNoise (16 Nov 2026): the noise unequalised on every speaker at the launched task file's
    # level from Ready, Go stopping on a backgroundNoise changed after launch. The noise is rig.yml's speakerArray.backgroundNoise.
    assert lint.background_noise_problems(WORKFLOW) == []


def test_the_training_stage_is_read_at_go_on_its_own():
    # TrainingStage is the ParameterValue at Go into its own AsyncSubject, as coen-dome-navigation's, not RunSettings' Item3:
    # RunSettings is LaunchSettings' (subject, session).
    # OpenRigs.Control's TrainingStage, which also reports trainingStage (16 Nov 2026).
    assert lint.training_stage_problems(WORKFLOW) == [] and "<Selector>Item3</Selector>" not in TEXT


def test_the_hardware_is_the_lab_s_pieces():
    # DomeHardware's pieces, one clock, Ready waiting for every stage they announce, MixerStarted included.
    assert lint.hardware_problems(WORKFLOW) == []
    # DomeHardware's devices, pinned: dropping one from the list fails here, not at the rig.
    assert lint.required_devices(WORKFLOW) == [
        ("TimestampGeneratorGen3-Main", "timestampGeneratorGen3"), ("BehaviorBoard-Main", "behaviorBoard"),
        ("LicketySplit-Left", "licketySplit"), ("LicketySplit-Right", "licketySplit"),
        ("StepperDriver-A", "stepperDriver"), ("StepperDriver-B", "stepperDriver"),
        ("Camera-Face", "camera"), ("Camera-Pupil", "camera"), ("Camera-Body", "camera")]


def test_zapit_is_the_shared_include_given_the_conditions_file():
    assert lint.zapit_include_problems(WORKFLOW) == []


def test_no_device_is_opened_by_hand():
    assert lint.hand_opened_devices(WORKFLOW) == []


def test_the_live_view_is_last_and_fed_by_nothing():
    assert lint.live_view_problems(WORKFLOW) == []


def test_the_penalty_sound_is_the_lab_s_at_the_task_s_level():
    assert lint.penalty_sound_problems(WORKFLOW) == []


def test_the_readme_lists_every_open_rigs_block():
    assert lint.readme_missing_blocks(WORKFLOW) == []


def test_every_command_is_declared_once_by_the_task_or_its_piece():
    # The pieces declare their own (LickSpoutStage's spout commands and SpoutStepSize, each RewardValve's
    # <Name>.Reward, 16 Nov 2026): a task's copy stops ControlServer at Start.
    assert lint.undeclared_commands(WORKFLOW) == [] and lint.declared_twice(WORKFLOW) == []


def test_spout_step_size_is_the_lick_spout_stage_s():
    assert lint.spout_step_size_problems(WORKFLOW) == []


def test_the_experiment_records_are_the_shared_include():
    assert lint.experiment_records_problems(WORKFLOW) == []


def test_the_launch_settings_are_the_shared_include():
    assert lint.launch_settings_problems(WORKFLOW) == []


def test_the_layout_is_tracked():
    assert lint.layout_problems(WORKFLOW) == []


def test_the_experiment_logs_are_the_lab_s():
    assert lint.experiment_log_problems(WORKFLOW) == []


def test_every_file_the_workflow_writes_is_in_the_manifest_s_expected_files():
    assert lint.expected_files_problems(WORKFLOW) == []


def test_no_wall_clock_wait_inside_task_logic():
    assert lint.wall_clock_waits(WORKFLOW) == []


def test_the_rng_seed_is_logged_at_trial_1s_start():
    # OpenRigs.Logging's LogRngSeed at TaskLogic's top level, given each trial's seed from its Start event.
    assert lint.rng_seed_problems(WORKFLOW) == []
    nodes, edges = lint.scope(WORKFLOW.named("TaskLogic"))
    (log,) = [i for i, n in enumerate(nodes) if n.get("Path") == lint.LOG_RNG_SEED]
    (seed,) = [a for a, b, _ in edges if b == log]
    assert nodes[seed].findtext(lint.W + "Selector") == "rngSeed"
    picks = [i for i in lint.upstream(edges, seed) if _kind(nodes[i]) == PICK]
    assert [nodes[i].find(W + "Combinator").findtext(DEV + "Kind") for i in picks] == ["Start"]


def test_the_task_logs_are_the_shared_include():
    assert lint.task_logs_problems(WORKFLOW) == []


def test_each_trial_s_sound_carries_its_sync_copy():
    # SelectAuditoryStimulus gives the mixer's buffer at the task's level, sync copy and all (OpenRigs.Audio SpeakerSet.Layout),
    # straight to the mixer's StimulusSounds.
    nodes, edges = _scope(_named("PlayAuditoryStimulus"))
    select = next(i for i, n in enumerate(nodes) if _kind(n) == "ext:SelectAuditoryStimulus")
    assert [nodes[b].findtext(W + "Name") for a, b, _ in edges if a == select] == ["StimulusSounds"]
    assert "Broadcast" not in TEXT and "ChannelActivation" not in TEXT and "PinkNoiseSync" not in TEXT


def test_the_extensions_readme_names_every_public_class():
    # Round 3 F18: the README had fallen behind the code (classes left out, the lick inputs misdescribed).
    import re

    folder = Path(__file__).parents[1] / "src" / "Extensions"
    readme = (folder / "README.md").read_text(encoding="utf-8-sig")
    code = "".join(f.read_text(encoding="utf-8-sig") for f in folder.glob("*.cs") if not f.name.endswith(".Generated.cs"))
    classes = set(re.findall(r"^public (?:static )?class (\w+)", code, re.M))
    assert sorted(c for c in classes if f"`{c}`" not in readme) == []
