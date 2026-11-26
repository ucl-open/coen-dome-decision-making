"""What src\\main.bonsai must keep: Harp-timed task logic, the shared blocks, and the bugs found fixed."""

import re
import xml.etree.ElementTree as ET
from pathlib import Path

import yaml
from open_rigs.rigs import workflow_checks
from open_rigs.rigs import workflow_lint as lint


ROOT = Path(__file__).parents[1]
W = "{https://bonsai-rx.org/2018/workflow}"
XSI = "{http://www.w3.org/2001/XMLSchema-instance}type"
TEXT = (ROOT / "src" / "main.bonsai").read_text(encoding="utf-8-sig")
MAIN = ET.fromstring(TEXT.encode("utf-8"))


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


def test_rewarded_follows_the_boards_answer():
    # F12: rewarded is the board's answer to the valve's write, through lib's TrialOutcomeEffects (the reward requested first, then the row by
    # RewardedAsDelivered over RewardDelivered, which Rewards declares before it), so a reward the board never answered (a stop cut it off) logs
    # it unrewarded or not at all. Included once, and no row of main.bonsai's own goes into TrialRow around it.
    effects = 'Path="OpenRigs.Trials:TrialOutcomeEffects.bonsai"'
    assert TEXT.count(effects) == 1
    assert not [e for e in MAIN.iter(W + "Expression") if e.get(XSI) == "MulticastSubject" and e.findtext(W + "Name") == "TrialRow"]
    assert TEXT.index('Path="OpenRigs.Devices:Rewards.bonsai"') < TEXT.index(effects)
    rewarded = yaml.safe_load((ROOT / "schemas" / "trial-columns.yml").read_text(encoding="utf-8"))["rewarded"]["description"]
    assert rewarded.startswith("True when the board answered the write that opened the valve") and "had not answered by the next outcome" in rewarded


PICK = "trl:PickTrialEvents"
TRL = "{clr-namespace:OpenRigs.Trials;assembly=OpenRigs.Trials}"


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
    picks = [i for i, n in enumerate(nodes) if _kind(n) == PICK and n.find(W + "Combinator").findtext(TRL + "Kind") == kind
             and n.find(W + "Combinator").findtext(TRL + "Cue") == cue]
    assert picks, (kind, cue)
    reached = set()
    for p in picks:
        for i in _downstream(edges, p):
            if nodes[i].get(XSI) == "MulticastSubject":
                reached.add(nodes[i].findtext(W + "Name"))
            elif nodes[i].get("Path"):
                reached.add(nodes[i].get("Path").split(":")[1] + (f" Set={nodes[i].findtext(W + 'Set')}" if nodes[i].findtext(W + "Set") else ""))
            elif _kind(nodes[i]) == "trl:ZapitDaqTrialCommand":
                reached.add(nodes[i].findtext(W + "Name"))
    return reached


def test_task_logic_runs_the_trial_loop():
    # One state machine for both dome tasks (lib-open-rigs TrialLoop): RunTrials, given the rules from the
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
        "Timestamp", "WheelDistanceCm", "LickStateLeft", "LickStateRight", "Zapit.Events"]
    assert [nodes[b].findtext(W + "Name") for a, b, _ in edges if a == run] == ["TrialEvents"]
    # No nested graph: Zapit's commands are OpenRigs.Trials ZapitDaqTrials', given the rules.
    assert not [n for n in _named("TaskLogic").iter(W + "Expression") if n.get(XSI) in lint.PER_ELEMENT]


def test_the_task_is_checked_once_at_go_into_the_rules():
    # The one task check: CheckTaskAtGo builds TrialRules from the task, the stage, rig.yml and the Zapit bank the setup
    # loaded (Zapit.Bank, ConnectForTaskZapitDaq's) and whether ZapitDaq opened at launch (ZapitRig, ConnectForTaskZapitDaq's),
    # so TaskRules comes after both Zapit pieces, which come after DomeHardware.
    assert TEXT.count('"ext:CheckTaskAtGo"') == 1
    top = [n.get("Path") or n.findtext(W + "Name") for n in ET.fromstring(TEXT).find(W + "Workflow").find(W + "Nodes")]
    assert top.index("OpenRigs.Devices:DomeHardware.bonsai") < top.index(lint.ZAPIT_DAQ_FOR_TASK) < top.index("TaskRules") < top.index("Audio")
    assert top.index(lint.ZAPIT_FOR_TASK) < top.index("TaskRules")
    nodes, edges = _scope(_named("TaskRules"))
    (check,) = [i for i, n in enumerate(nodes) if _kind(n) == "ext:CheckTaskAtGo"]
    assert [(nodes[b].get(XSI), nodes[b].findtext(W + "Name")) for a, b, _ in edges if a == check] == [("rx:AsyncSubject", "TrialRules")]
    assert {nodes[i].findtext(W + "Name") for i in _upstream(edges, check) if nodes[i].get(XSI) == "SubscribeSubject"} == {
        "TaskText", "RigConfigs", "Zapit.Bank", "ZapitRig"}
    assert [nodes[a].findtext(W + "Name") for a, b, label in edges if b == check and label == "Source3"] == ["ZapitRig"]


def test_each_trial_event_reaches_its_hardware():
    assert _reached("Start") == {"Trial"}   # Trial at the trial's start; LogRngSeed reads Trial through its own subscription
    assert _reached("Stimulus") == {"StimulusTrial"}   # one subject, read by Display and Audio
    assert _reached("Update") == {"GratingAzimuthDeg"}
    assert _reached("StimulusOff") == {"StimulusOff"}   # once a trial (OpenRigs.Trials StimulusTiming): the grating hidden, the sound stopped
    assert TEXT.count("<Name>StimulusOff</Name>") == 3 and "GratingOff" not in TEXT   # one publisher, two readers
    assert _reached("Outcome") == {"TrialOutcomeEffects.bonsai", "PlayPenaltyFlash.bonsai"}   # the flash on the outcomes TrialOutcomeEffects passes on
    # Arm, Trigger and TriggerOff are ZapitDaqTrials': no pick of them here.
    nodes, _ = _scope(_named("TaskLogic"))
    kinds = {n.find(W + "Combinator").findtext(TRL + "Kind") for n in nodes if _kind(n) == PICK}
    assert not kinds & {"Arm", "Trigger", "TriggerOff", "LaserCap"}


def test_every_spout_move_is_spout_commands_over_the_trial_events():
    # One path (lib-open-rigs SpoutCommands on the trials' ISpoutTrial): trial 1's spouts out, the SpoutsIn cue and the outcome, none read from TaskConfig.
    nodes, edges = _scope(_named("TaskLogic"))
    senders = [i for i, n in enumerate(nodes) if n.get(XSI) == "MulticastSubject" and n.findtext(W + "Name") == "LickSpoutStage.Commands"]
    assert len(senders) == 1 and TEXT.count("LickSpoutStage.Commands") == 1
    (command,) = [a for a, b, _ in edges if b == senders[0]]
    assert _kind(nodes[command]) == "trl:SpoutCommands"
    assert [nodes[a].findtext(W + "Name") for a, b, _ in edges if b == command] == ["TrialEvents"]
    assert "TaskConfig" not in {n.findtext(W + "Name") for n in nodes if n.get(XSI) == "SubscribeSubject"}
    assert "SpoutMovements" not in TEXT and "SpoutsInCommand" not in TEXT


def test_zapit_s_trials_are_the_shared_includes_each_given_the_rules_with_its_box():
    # OpenRigs.Trials ZapitDaqTrials (Zapit-Bonsai) and ZapitTrials (Zapit-MATLAB), once each, each given TrialRules only through
    # its box's condition (TrialRules.ZapitBonsai, ZapitMatlab), so only the session's Zapit is armed and writes the trigger
    # line: each writes nothing until given rules with a laser (lib zapit-native .24). Timed by the rules' Laser, fixed at Go.
    nodes, edges = _scope(_named("TaskLogic"))
    for path, box in (("OpenRigs.Trials:ZapitDaqTrials.bonsai", "ZapitBonsai"), ("OpenRigs.Trials:ZapitTrials.bonsai", "ZapitMatlab")):
        assert TEXT.count(f'Path="{path}"') == 1
        (zapit,) = [i for i, n in enumerate(nodes) if n.get("Path") == path]
        (gate,) = [a for a, b, label in edges if b == zapit and label == "Source1"]
        assert nodes[gate].get(XSI) == "scr:ExpressionCondition"
        assert nodes[gate].findtext("{clr-namespace:Bonsai.Scripting.Expressions;assembly=Bonsai.Scripting.Expressions}Expression") == box
        assert [(nodes[a].get(XSI), nodes[a].findtext(W + "Name")) for a, b, _ in edges if b == gate] == [("SubscribeSubject", "TrialRules")]
    assert "ZapitDaqTrialCommand" not in TEXT and "SetZapitTriggerLine" not in TEXT and "HardwareTriggered" not in TEXT
    # The loop reads both Zapits' answers; RunTrials takes MATLAB Zapit's replies with Zapit-MATLAB ticked, else ZapitDaq's events.
    (run,) = [i for i, n in enumerate(nodes) if _kind(n) == "ext:RunTrials"]
    assert [nodes[a].findtext(W + "Name") for a, b, label in edges if b == run and label == "Source7"] == ["ZapitReplies"]


def test_each_zapit_piece_is_gated_on_its_own_box():
    # Pip, 26 Nov 2026: two boxes, mutually exclusive, Zapit-Bonsai and Zapit-MATLAB. Each lib piece reads its own box
    # (OptogeneticsSwitch); ConnectForTaskZapitDaq opens the NI card and the Zapit camera at launch only when the task file
    # then ticks Zapit-Bonsai (OpenedBy: Pip, 26 Nov 2026), and its refusal names both boxes; ConnectForTask publishes its
    # switch under its own name, as ConnectForTaskZapitDaq publishes Optogenetics; ZapitLogs writes Settings_ZapitConditions
    # only with Zapit-MATLAB.
    def props(path):
        (node,) = [e for e in MAIN.iter(W + "Expression") if e.get("Path") == path]
        return {c.tag.removeprefix(W): c.text for c in node}
    native, matlab, logs = props(lint.ZAPIT_DAQ_FOR_TASK), props(lint.ZAPIT_FOR_TASK), props("OpenRigs.Devices:ZapitLogs.bonsai")
    assert native["OptogeneticsSwitch"] == "TaskParameters.Optogenetics.ZapitBonsai" and native["OpenedBy"] == "optogenetics.zapitBonsai"
    assert "ClosedBy" not in native
    assert "tick Zapit-MATLAB instead, or fill in rig.yml's zapit block" in native["NoZapitDaqMessage"]
    assert matlab["OptogeneticsSwitch"] == logs["OptogeneticsSwitch"] == "TaskParameters.Optogenetics.ZapitMatlab"
    assert matlab["OptogeneticsSubjectName"] == "OptogeneticsMatlab"
    # The manifest's Settings_ZapitConditions is either Zapit's: OptogeneticsExperiment, either box (TaskConfig's Optogenetics.On).
    nodes, edges = _scope(_named("TaskFile"))
    (either,) = [i for i, n in enumerate(nodes) if n.findtext(W + "Name") == "OptogeneticsExperiment"]
    (selector,) = [a for a, b, _ in edges if b == either]
    assert nodes[selector].findtext(W + "Selector") == "TaskParameters.Optogenetics.On"
    assert "Settings/Settings_ZapitConditions_?OptogeneticsExperiment" in TEXT


def test_each_grating_starts_at_its_own_trial_s_azimuth():
    # GratingAzimuthDeg is a PublishSubject: the grating's LocationX is its trial's gratingAzimuthDeg (visualAzimuthDeg, 0 with no grating), then each lick's move.
    nodes, edges = _scope(_named("ShowGrating"))
    (location,) = [i for i, n in enumerate(nodes) if n.get(XSI) == "PropertyMapping" and 'Name="LocationX"' in ET.tostring(n, encoding="unicode")]
    (merge,) = [a for a, b, _ in edges if b == location]
    assert _kind(nodes[merge]) == "rx:Merge"
    sources = {label: a for a, b, label in edges if b == merge}
    assert nodes[sources["Source1"]].findtext(W + "Selector") == "gratingAzimuthDeg"
    assert nodes[sources["Source2"]].findtext(W + "Name") == "GratingAzimuthDeg"
    declared = next(n for n in _named("Declarations").iter(W + "Expression") if n.findtext("{clr-namespace:Bonsai.Reactive;assembly=Bonsai.Core}Name") == "GratingAzimuthDeg")
    assert declared.get(XSI) == "rx:PublishSubject"


def test_the_shared_blocks_replace_the_local_copies():
    for block in ("OpenRigs.Devices:ReadLabRig.bonsai", "OpenRigs.Devices:DomeHardware.bonsai",
                  lint.REWARDS, "OpenRigs.Devices:ConnectForTaskZapitDaq.bonsai",
                  "OpenRigs.Logging:ExperimentLogs.bonsai", lint.LAUNCH_SETTINGS, lint.BACKGROUND_NOISE, lint.EXPERIMENT_RECORDS):
        assert f'Path="{block}"' in TEXT
    # The speakers and the projector are DomeHardware's (every task has background noise and a screen).
    assert lint.SPEAKER_MIXER not in TEXT and lint.PROJECTOR_DISPLAY not in TEXT
    assert not re.search(r'Path="Extensions\\', TEXT)
    assert not list((ROOT / "src" / "Extensions").glob("*.bonsai"))


def test_the_launch_properties():
    # No MissingCalibration: a reward valve without a calibration always stops the workflow.
    top = list(MAIN.find(W + "Workflow").find(W + "Nodes"))
    exposed = {p.get("Name") for n in top if n.get(XSI) == "ExternalizedMapping" for p in n.iter(W + "Property")}
    assert {"SubjectId", "SessionId", "TaskPath", "DisplayDevice", "ConfigRoot", "LaunchId", "LaserCeilingVolts"} == exposed
    assert lint.launch_id_problems(WORKFLOW) == []


def test_rewards_are_the_shared_include_s():
    # Rewards declares RewardRequests, gives the manual drops and delivers each reward (lib batch 2).
    assert lint.reward_problems(WORKFLOW) == []


def test_the_auditory_stimuli_are_the_tasks_own_files():
    """The 100 stimuli are in the repo (production av-dome keeps them in its LocalFiles), mono 16-bit
    at the mixer's 48 kHz: nothing put them in each rig's
    calibration folder, so the workflow stopped at Start on every rig."""
    import wave

    folder = ROOT / "src" / "sounds"
    shapes = set()
    for i in range(100):
        with wave.open(str(folder / f"filtered_pink_pulse_{i}.wav")) as w:
            shapes.add((w.getnchannels(), w.getsampwidth(), w.getframerate()))
    assert len(shapes) == 1
    assert shapes.pop()[:3] == (1, 2, 48000)
    assert r"<ext:Folder>sounds</ext:Folder>" in TEXT


def test_the_metrics_are_the_view_s():
    names = {c.findtext("{clr-namespace:OpenRigs.Control;assembly=OpenRigs.Control}Name") for c in MAIN.iter(W + "Combinator") if c.get(XSI) == "orcl:ReportMetric"}
    # The shared four are OpenRigs.Control's ReportTrialMetrics', once, at its default members.
    (shared,) = [c for c in MAIN.iter(W + "Combinator") if c.get(XSI) == "orcl:ReportTrialMetrics"]
    members = shared.findtext("{clr-namespace:OpenRigs.Control;assembly=OpenRigs.Control}Members").split(",")
    assert members == ["trialNumber", "trialType", "rewardOfferedUl", "laserCondition"] and not names & set(members)
    names |= set(members)
    assert names == {"trialNumber", "target", "trialType", "visualInitialAzimuthDeg", "auditoryInitialAzimuthDeg", "rewardOfferedUl", "laserCondition", "curriculumStage"}


# The lab's rules for every workflow (open_rigs.rigs.workflow_lint, one copy in lib-open-rigs), the shared ones as
# workflow_checks' tests; test_the_hardware_is_the_lab_s_pieces below replaces the shared one, adding DomeHardware's devices.
WORKFLOW = lint.Workflow(ROOT)
globals().update(workflow_checks.tests(ROOT))

# Both Zapits in one workflow (Zapit-Bonsai and Zapit-MATLAB, 26 Nov 2026): open_rigs' workflow_lint (pinned) knows a
# workflow on one Zapit only, so these three shared checks are held here without exactly the problems a workflow with both
# Zapits has by design (test_each_zapit_piece_is_gated_on_its_own_box holds what it must keep instead). They go back to the
# shared ones when workflow_lint learns both Zapits, or when MATLAB Zapit is retired.
DUAL_ZAPIT = {
    "the workflow includes ConnectForTask, MATLAB Zapit's, beside ConnectForTaskZapitDaq",
    "the workflow includes ZapitLogs, MATLAB Zapit's, beside ConnectForTaskZapitDaq",
    "the workflow includes ZapitTrials, MATLAB Zapit's, beside ConnectForTaskZapitDaq",
    "the workflow includes ZapitLogs itself (TaskLogs does)",
    "ExpectedFiles names Settings/Settings_ZapitConditions_?OptogeneticsExperiment; "
    "its block writes it as Settings/Settings_ZapitConditions_?Optogenetics",
}


def test_zapit_is_the_shared_include_given_the_conditions_file():
    assert [p for p in lint.zapit_include_problems(WORKFLOW) if p not in DUAL_ZAPIT] == []
    assert TEXT.count(f'Path="{lint.ZAPIT_FOR_TASK}"') == 1 and f'Path="{lint.ZAPIT_INCLUDE}"' not in TEXT


def test_the_task_logs_are_the_shared_include():
    assert [p for p in lint.task_logs_problems(WORKFLOW) if p not in DUAL_ZAPIT] == []
    assert TEXT.count('Path="OpenRigs.Devices:ZapitLogs.bonsai"') == 1


def test_every_file_the_workflow_writes_is_in_the_manifest_s_expected_files():
    assert [p for p in lint.expected_files_problems(WORKFLOW) if p not in DUAL_ZAPIT] == []


def test_every_harp_delay_runs_on_the_timestamp_clock():
    _, off = lint.harp_delays_off_the_clock(WORKFLOW)
    assert off == []


def test_the_speakers_and_the_mixer_are_the_shared_block_s():
    # OpenRigs.Audio's SpeakerMixer loads the speakers and starts the mixer no sooner than 3 s after Start.
    assert lint.speaker_mixer_problems(WORKFLOW) == []


def test_every_sound_plays_at_the_task_file_s_level():
    # The stimulus set is checked at Go for the highest level any condition can draw, at every rate for the longest
    # duration: the whole rules into LevelAuditoryStimulusSet (TrialRules.MaxStimulusLevel, StimulusRatesHz, MaxStimulusSeconds).
    assert lint.sound_level_problems(WORKFLOW) == []
    nodes, edges = _scope(_named("Audio"))
    level = next(i for i, n in enumerate(nodes) if _kind(n) == "ext:LevelAuditoryStimulusSet")
    upstream = list(_upstream(edges, level))
    assert not any(_kind(nodes[a]) == "MemberSelector" for a in upstream)
    assert any(nodes[a].findtext(W + "Name") == "TrialRules" for a in upstream)


def test_the_background_noise_is_the_shared_include():
    # OpenRigs.Audio's BackgroundNoise: the noise unequalised on every speaker at the launched task file's
    # level from Ready, Go stopping on a backgroundNoise changed after launch. The noise is rig.yml's speakerArray.backgroundNoise.
    assert lint.background_noise_problems(WORKFLOW) == []


def test_the_stage_is_the_task_file_s_alone():
    # No TrainingStage parameter, include or subject: the stage is the task file's curriculum, which CheckTaskAtGo found
    # well formed; TaskRules publishes its code-version as CurriculumStage (from the TrialRules) and reports it as the metric curriculumStage.
    assert "TrainingStage" not in TEXT
    nodes, edges = _scope(_named("TaskRules"))
    (subject,) = [i for i, n in enumerate(nodes) if n.findtext(W + "Name") == "CurriculumStage"]
    (source,) = [a for a, b, _ in edges if b == subject]
    assert nodes[source].get(XSI) == "MemberSelector" and nodes[source].findtext(W + "Selector") == "CurriculumStage"
    (metric,) = [b for a, b, _ in edges if a == subject]
    assert nodes[metric].find(W + "Combinator").findtext("{clr-namespace:OpenRigs.Control;assembly=OpenRigs.Control}Name") == "curriculumStage"


def test_the_hardware_is_the_lab_s_pieces():
    # DomeHardware's pieces, one clock, Ready waiting for every stage they announce, MixerStarted included.
    assert lint.hardware_problems(WORKFLOW) == []
    # DomeHardware's devices, pinned: dropping one from the list fails here, not at the rig.
    assert lint.required_devices(WORKFLOW) == [
        ("TimestampGeneratorGen3-Main", "timestampGeneratorGen3"), ("BehaviorBoard-Main", "behaviorBoard"),
        ("LicketySplit-Left", "licketySplit"), ("LicketySplit-Right", "licketySplit"),
        ("StepperDriver-A", "stepperDriver"), ("StepperDriver-B", "stepperDriver"),
        ("Camera-Face", "camera"), ("Camera-Pupil", "camera"), ("Camera-Body", "camera")]


def test_no_device_is_opened_by_hand():
    assert lint.hand_opened_devices(WORKFLOW) == []


def test_no_wall_clock_wait_inside_task_logic():
    assert lint.wall_clock_waits(WORKFLOW) == []


def test_the_rng_seed_is_logged_at_trial_1s_start():
    # OpenRigs.Logging's LogRngSeed at TaskLogic's top level, given the rules' seed (TrialRules.Seed) at each trial's Start event.
    assert lint.rng_seed_problems(WORKFLOW) == []
    nodes, edges = lint.scope(WORKFLOW.named("TaskLogic"))
    (log,) = [i for i, n in enumerate(nodes) if n.get("Path") == lint.LOG_RNG_SEED]
    (seed,) = [a for a, b, _ in edges if b == log]
    assert nodes[seed].findtext(lint.W + "Selector") == "Item2.Seed"
    (latest,) = [a for a, b, _ in edges if b == seed]
    assert _kind(nodes[latest]) == "rx:WithLatestFrom"
    assert [nodes[a].findtext(W + "Name") for a, b, label in edges if b == latest and label == "Source2"] == ["TrialRules"]
    (source,) = [a for a, b, label in edges if b == latest and label == "Source1"]
    assert (nodes[source].get(XSI), nodes[source].findtext(W + "Name")) == ("SubscribeSubject", "Trial")
    assert not [i for i in lint.upstream(edges, seed) if _kind(nodes[i]) == PICK]   # its own Trial, not the Multicast's chain


def test_each_trial_s_sound_carries_its_sync_copy():
    # SelectAuditoryStimulus gives the mixer's buffer at the task's level, sync copy and all (OpenRigs.Audio SpeakerSet.Layout),
    # straight to the mixer's LoopStimulusSounds, which loops it until StopStimulusSounds, sent at each StimulusOff (the
    # trial loop's, once a trial: OpenRigs.Trials StimulusTiming); each sounding trial paired with the levelled set by
    # WithLatestFrom, no per-trial graph.
    nodes, edges = _scope(_named("Audio"))
    (select,) = [i for i, n in enumerate(nodes) if _kind(n) == "ext:SelectAuditoryStimulus"]
    assert [nodes[b].findtext(W + "Name") for a, b, _ in edges if a == select] == ["LoopStimulusSounds"]
    (stop,) = [i for i, n in enumerate(nodes) if _kind(n) == "MulticastSubject" and n.findtext(W + "Name") == "StopStimulusSounds"]
    assert [nodes[a].findtext(W + "Name") for a, b, _ in edges if b == stop] == ["StimulusOff"]
    (latest,) = [a for a, b, _ in edges if b == select]
    assert _kind(nodes[latest]) == "rx:WithLatestFrom"
    into = {label: nodes[a].findtext(W + "Name") for a, b, label in edges if b == latest}
    assert into == {"Source1": "AuditoryTrial?", "Source2": "StimulusSoundBuffers"}
    assert not [n for n in _named("Audio").iter(W + "Expression") if n.get(XSI) in lint.PER_ELEMENT]
    assert "Broadcast" not in TEXT and "ChannelActivation" not in TEXT and "PinkNoiseSync" not in TEXT
