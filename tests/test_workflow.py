"""What src\\main.bonsai must keep: Harp-timed task logic, the shared blocks, and the bugs found fixed."""

import re
import xml.etree.ElementTree as ET
from pathlib import Path

from open_rigs.rigs import workflow_lint as lint


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


def test_the_stimulus_is_commanded_as_execute_trial_starts():
    # stimulusOnsetTime is stamped with no wait between ExecuteTrial's input and the stimulus.
    nodes, edges = _scope(_named("ExecuteTrial"))
    onset = next(i for i, n in enumerate(nodes) if n.get(XSI) == "rx:AsyncSubject" and n.findtext(W + "Name") == "Onset")
    path = _upstream(edges, onset)
    assert any(nodes[i].get(XSI) == "SubscribeSubject" and nodes[i].findtext(W + "Name") == "ExecTrial" for i in path)
    assert not any(_kind(nodes[i]) == "core:HarpDelay" for i in path)


def test_the_trigger_rises_only_once_zapit_armed_the_trial():
    # Pip, 13 Nov 2026: Zapit is armed during the interval; a reply not in by the trigger stops the workflow (RequireZapitArmed).
    nodes, edges = _scope(_named("Laser"))
    require = next(i for i, n in enumerate(nodes) if _kind(n) == "p1:RequireZapitArmed")
    check = next(i for i, n in enumerate(nodes) if _kind(n) == "p1:CheckZapitArmed")
    trigger = next(i for i, n in enumerate(nodes) if n.findtext(W + "Name") == "Trigger")
    # Pip, 13 Nov 2026: no laser on a trial answered before its trigger. UnlessAnswered and the row read one per-trial
    # record of which came first (audit round 3), not the row's Harp time.
    unless = next(i for i, n in enumerate(nodes) if _kind(n) == "p1:UnlessAnswered")
    assert [e for e in edges if e[1] == unless] == [(trigger, unless, "Source1")]
    assert (check, require, "Source2") in edges and (unless, require, "Source1") in edges
    # Audit round 3: without hardware triggering Zapit starts the laser on receipt, so ArmZapit sends at the trigger then.
    arm = next(i for i, n in enumerate(nodes) if n.findtext(W + "Name") == "ArmZapit")
    assert (require, arm, "Source2") in edges
    for target in (arm, require):
        mappings = [a for a, b, _ in edges if b == target and nodes[a].get(XSI) == "PropertyMapping"]
        assert any("Zapit.HardwareTriggered" in ET.tostring(nodes[a], encoding="unicode") for a in mappings)
    assert any(nodes[i].findtext(W + "Name") == "ZapitReplies" for i in _upstream(edges, check))
    # The trigger is timed from the trial's Go, never from Zapit's reply.
    assert check not in _upstream(edges, trigger)
    assert any(_kind(nodes[i]) == "p1:TrialGoFor" for i in _upstream(edges, trigger))
    zap = "{clr-namespace:OpenRigs.Devices.Zapit;assembly=OpenRigs.Devices}Set"
    line = next(i for i, n in enumerate(nodes) if _kind(n) == "zap:ZapitLineMessage" and n.find(W + "Combinator").findtext(zap) == "true")
    assert require in _upstream(edges, line)


def test_each_laser_runs_after_the_last_and_its_ramp_down():
    # Zapit refuses a StartStimulus while a timed stimulus and its ramp-down play: the next trial is armed after RampDown.
    top, top_edges = _scope(_named("TaskLogic"))
    laser = next(i for i, n in enumerate(top) if n.findtext(W + "Name") == "Laser")
    assert any(a == laser and _kind(top[b]) == "rx:Concat" for a, b, _ in top_edges)
    nodes, edges = _scope(_named("Laser"))
    output = next(i for i, n in enumerate(nodes) if n.get(XSI) == "WorkflowOutput")
    ramp = next(i for i, n in enumerate(nodes) if n.findtext(W + "Name") == "RampDown")
    assert (ramp, output, "Source1") in edges
    from open_rigs.components.zapit import ZAPIT_MAX_RAMP_DOWN_SECONDS
    assert float(nodes[ramp].find(W + "Combinator").findtext("{clr-namespace:OpenRigs.Core;assembly=OpenRigs.Core}DueSeconds")) == ZAPIT_MAX_RAMP_DOWN_SECONDS


def test_zapit_adds_no_time_to_the_stimulus():
    # Pip, 13 Nov 2026: the stimulus comes the lead after Go whether optogenetics is on or off; nothing of Zapit's is on its path.
    nodes, edges = _scope(_named("TaskLogic"))
    execute = next(i for i, n in enumerate(nodes) if n.findtext(W + "Name") == "ExecuteTrial")
    path = _upstream(edges, execute)
    names = {nodes[i].findtext(W + "Name") for i in path}
    assert {"WaitForGo", "LeadToStimulus", "DrawTrial"} <= names and "Laser" not in names
    assert [nodes[i].findtext(W + "Name") for i in path if _kind(nodes[i]) == "core:HarpDelay"] == ["LeadToStimulus"]
    assert "ZapitReplies" not in ET.tostring(_named("ExecuteTrial"), encoding="unicode")


def test_the_next_trial_is_drawn_at_the_start_of_the_interval():
    # ExecuteTrial's TrialEnd leaves at ItiStart (after any timeout); nothing waits between them.
    nodes, edges = _scope(_named("ExecuteTrial"))
    output = next(i for i, n in enumerate(nodes) if n.get(XSI) == "WorkflowOutput")
    path = _upstream(edges, output)
    assert any(nodes[i].findtext(W + "Name") == "ItiStart" and nodes[i].get(XSI) == "SubscribeSubject" for i in path)
    assert not [i for i in path if _kind(nodes[i]) == "core:HarpDelay"]
    assert "RestOfInterval" not in TEXT


def test_the_shared_blocks_replace_the_local_copies():
    for block in ("OpenRigs.Devices:RigHardware.bonsai", "OpenRigs.Devices:HarpLogs.bonsai",
                  "OpenRigs.Devices:BehaviorBoard.DeliverReward.bonsai", "OpenRigs.Vision:ProjectorDisplay.bonsai"):
        assert f'Path="{block}"' in TEXT
    assert not re.search(r'Path="Extensions\\', TEXT)
    assert not list((ROOT / "src" / "Extensions").glob("*.bonsai"))


def test_the_launch_properties():
    # No MissingCalibration: a reward valve without a calibration always stops the workflow (Pip, 15 Nov 2026).
    top = list(MAIN.find(W + "Workflow").find(W + "Nodes"))
    exposed = {p.get("Name") for n in top if n.get(XSI) == "ExternalizedMapping" for p in n.iter(W + "Property")}
    assert {"SubjectId", "SessionId", "TaskPath", "DisplayDevice"} <= exposed and "MissingCalibration" not in exposed


def test_rewards_go_to_reward_requests():
    # One name for the reward request in both dome tasks (Pip, 15 Nov 2026), as coen-dome-navigation's.
    assert "DeliverRewardCommand" not in TEXT and TEXT.count("<Name>RewardRequests</Name>") == 4


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
    assert r"<p1:Folder>LocalFiles\AuditoryStimuli</p1:Folder>" in TEXT


def test_the_trial_loop_starts_on_the_clock_thread():
    """The first trial starts from OnNextTick (the experiment folder and the Harp clock), not from
    WithLatestTimestamp, which ran the first draw under a lock the clock's thread needs: the clock
    stopped at trial 1 and the workflow hung at Stop (BLUE-OPTO-2, 14 Nov 2026)."""
    task = next(g for g in MAIN.find(f"{W}Workflow/{W}Nodes") if g.findtext(f"{W}Name") == "TaskLogic")
    nodes = list(task.find(f"{W}Workflow/{W}Nodes"))
    edges = [(int(e.get("From")), int(e.get("To")), e.get("Label")) for e in task.find(f"{W}Workflow/{W}Edges")]
    start = next(i for i, n in enumerate(nodes) if (n.find(f"{W}Combinator") is not None and n.find(f"{W}Combinator").get(XSI) == "p1:StartTrials"))
    (into,) = [f for f, t, _ in edges if t == start]
    assert nodes[into].find(f"{W}Combinator").get(XSI) == "p1:OnNextTick"
    clock = [f for f, t, label in edges if t == into and label == "Source2"]
    assert [nodes[f].findtext(f"{W}Name") for f in clock] == ["Timestamp"]


# The lab's rules for every workflow (open_rigs.rigs.workflow_lint, one copy in lib-open-rigs).
WORKFLOW = lint.Workflow(ROOT)


def test_every_harp_delay_runs_on_the_timestamp_clock():
    count, off = lint.harp_delays_off_the_clock(WORKFLOW)
    assert count >= 1 and off == []


def test_no_async_event_sender_completes_async_events():
    senders, finite = lint.finite_async_event_senders(WORKFLOW)
    assert senders == 2 and finite == []  # RngSeed and Control; DeliverReward sends RewardDelivered itself


def test_no_trial_graph_closes_a_shared_subject():
    assert lint.subjects_closed_by_a_nested_multicast(WORKFLOW) == []


def test_nothing_reads_a_startup_watchdog_s_output():
    assert lint.startup_watchdog_readers(WORKFLOW) == 0


def test_the_speakers_and_the_mixer_are_the_shared_block_s():
    # OpenRigs.Audio's SpeakerMixer loads the speakers and starts the mixer no sooner than 3 s after Start.
    assert lint.speaker_mixer_problems(WORKFLOW) == []


def test_every_sound_plays_at_the_task_file_s_level():
    assert lint.sound_level_problems(WORKFLOW) == []
    nodes, edges = _scope(_named("LoadAudioFiles"))
    level = next(i for i, n in enumerate(nodes) if _kind(n) == "p1:LevelAuditoryStimulusSet")
    assert any(nodes[a].findtext(W + "Selector") == "TaskParameters.AudioStimulus.LevelDbfs" for a in _upstream(edges, level))


def test_ready_the_cameras_and_the_watchdog_are_rig_hardware_s():
    assert lint.rig_hardware_problems(WORKFLOW) == []


def test_zapit_is_the_shared_include_given_the_conditions_file():
    assert lint.zapit_include_problems(WORKFLOW) == []


def test_the_manual_reward_is_the_fixed_drop():
    assert lint.manual_reward_problems(WORKFLOW) == []


def test_the_penalty_sound_is_the_lab_s_at_the_task_s_level():
    assert lint.penalty_sound_problems(WORKFLOW) == []


def test_the_readme_lists_every_open_rigs_block():
    assert lint.readme_missing_blocks(WORKFLOW) == []


def test_the_workflow_declares_every_command_it_handles():
    assert lint.undeclared_commands(WORKFLOW) == []


def test_the_layout_is_tracked():
    assert lint.layout_problems(WORKFLOW) == []


def test_the_cameras_are_logged_by_log_cameras():
    assert lint.camera_log_problems(WORKFLOW) == []


def test_no_wall_clock_wait_inside_task_logic():
    assert lint.wall_clock_waits(WORKFLOW) == []


def test_the_rng_seed_is_logged_at_trial_1s_go():
    assert lint.rng_seed_problems(WORKFLOW) == []


def test_each_trial_s_sound_carries_its_sync_copy():
    # SelectAuditoryStimulus gives the mixer's buffer at the task's level, sync copy and all (OpenRigs.Audio SpeakerSet.Layout),
    # straight to the mixer's StimulusSounds.
    nodes, edges = _scope(_named("PlayStim"))
    select = next(i for i, n in enumerate(nodes) if _kind(n) == "p1:SelectAuditoryStimulus")
    assert [nodes[b].findtext(W + "Name") for a, b, _ in edges if a == select] == ["StimulusSounds"]
    assert "Broadcast" not in TEXT and "ChannelActivation" not in TEXT and "PinkNoiseSync" not in TEXT
