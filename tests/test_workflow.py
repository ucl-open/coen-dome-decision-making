"""What src\\main.bonsai must keep: Harp-timed task logic, the shared blocks, and the bugs found fixed."""

import re
import xml.etree.ElementTree as ET
from pathlib import Path

import pytest

ROOT = Path(__file__).parents[1]
W = "{https://bonsai-rx.org/2018/workflow}"
XSI = "{http://www.w3.org/2001/XMLSchema-instance}type"
TEXT = (ROOT / "src" / "main.bonsai").read_text(encoding="utf-8-sig")
MAIN = ET.fromstring(TEXT.encode("utf-8"))
FINITE = ("rx:Take", "rx:TakeLast", "rx:First", "rx:Last")
WALL_CLOCK = ("rx:Timer", "rx:Delay", "rx:DelaySubscription", "rx:Throttle", "rx:Timeout", "rx:Interval", "rx:TimeInterval",
              "rx:Timestamp", "gl:Timer", "gl:Delay", "TimeOfDay")
SIBLING = ROOT.parent.parent / "Coen-Lab" / "lib-open-rigs"


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


def test_no_wall_clock_wait_inside_task_logic():
    # Plan v11: every task wait is on the Harp clock (Core HarpDelay on Timestamp); no Rx or render-clock timer.
    found = [(_named_or_type(e), _kind(e)) for e in _named("TaskLogic").iter(W + "Expression") if _kind(e) in WALL_CLOCK]
    assert not found
    assert "TimeOfDay" not in ET.tostring(_named("TaskLogic"), encoding="unicode")


def _named_or_type(e):
    return e.findtext(W + "Name") or _kind(e)


def test_every_harp_delay_runs_on_the_timestamp_clock():
    delays = 0
    for scope in [_named("TaskLogic"), *_named("TaskLogic").iter(W + "Expression")]:
        if scope.find(W + "Workflow") is None:
            continue
        nodes, edges = _scope(scope)
        for i, node in enumerate(nodes):
            if _kind(node) == "core:HarpDelay":
                delays += 1
                clocks = [nodes[a] for a, b, label in edges if b == i and label == "Source2"]
                assert len(clocks) == 1 and clocks[0].findtext(W + "Name") == "Timestamp", node.findtext(W + "Name")
    assert delays >= 10


def test_the_stimulus_is_commanded_as_execute_trial_starts():
    # stimulusOnsetTime is stamped with no wait between ExecuteTrial's input and the stimulus.
    nodes, edges = _scope(_named("ExecuteTrial"))
    onset = next(i for i, n in enumerate(nodes) if n.get(XSI) == "rx:AsyncSubject" and n.findtext(W + "Name") == "Onset")
    path = _upstream(edges, onset)
    assert any(nodes[i].get(XSI) == "SubscribeSubject" and nodes[i].findtext(W + "Name") == "ExecTrial" for i in path)
    assert not any(_kind(nodes[i]) == "core:HarpDelay" for i in path)


def test_no_async_event_sender_completes_async_events():
    # A MulticastSubject passes on its source's completion: a Take(1) ahead of an AsyncEvents multicast completed the
    # shared subject at trial 1 and nothing later reached BonsaiData_Async (Tom Childs, 9 Nov 2026).
    senders = 0
    for workflow in MAIN.iter(W + "Workflow"):
        nodes = list(workflow.find(W + "Nodes"))
        edges = [(int(e.get("From")), int(e.get("To")), e.get("Label")) for e in workflow.find(W + "Edges")]
        for i, node in enumerate(nodes):
            if node.get(XSI) == "MulticastSubject" and node.findtext(W + "Name") == "AsyncEvents":
                senders += 1
                assert not [_kind(nodes[a]) for a in _upstream(edges, i) if _kind(nodes[a]) in FINITE]
    assert senders == 2  # RngSeed and Control; DeliverReward sends RewardDelivered itself


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


def test_the_manual_reward_is_pip_s_fixed_drop():
    # Pip, 15 Nov 2026: RewardLeft and RewardRight each give 2.5 ul, before and during a run, at the side rig.yml names; no setting.
    assert not any(p.tag.endswith("Parameter") and p.findtext(W + "Name") == "ManualRewardUl" for p in MAIN.iter())
    nodes, edges = _scope(_named("Initialization"))
    subject = next(i for i, n in enumerate(nodes) if n.get(XSI) == "rx:BehaviorSubject" and n.findtext(W + "Name") == "ManualRewardDropUl")
    (source,) = [a for a, b, _ in edges if b == subject]
    assert float(next(nodes[source].iter(W + "Value")).text) == 2.5
    commands = _named("Commands")
    assert [n.findtext(W + "Name") for n in commands.iter(W + "Expression") if n.get(XSI) == "SubscribeSubject"].count("ManualRewardDropUl") == 2
    assert sorted(v.text for v in commands.iter(W + "Value")) == ["left", "right"]
    assert TEXT.count("<Name>ManualRewardDropUl</Name>") == 3 and "ManualRewardUl" not in TEXT


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


def test_zapit_is_the_shared_include_given_the_conditions_file():
    # Audit round 3: the connect, check and refusal chain is lib-open-rigs' Zapit.ConnectZapit (its stops are tested C#
    # there), shared with coen-dome-navigation; nothing of it is built here.
    nodes, edges = _scope(_named("Zapit"))
    include = next(i for i, n in enumerate(nodes) if n.get("Path") == "OpenRigs.Devices:Zapit.ConnectZapit.bonsai")
    path = [nodes[i] for i in _upstream(edges, include)]
    assert any(n.findtext(W + "Selector") == "TaskParameters.Optogenetics.ZapitConfigPath" for n in path)
    assert any(n.get(XSI) == "rx:Condition" and "Optogenetics.UseZapit" in ET.tostring(n, encoding="unicode") for n in path)
    for local in ("zap:ZapitClient", "zap:CheckZapitConditionCount", "core:ThrowError", "ConnectZapit</Name>"):
        assert local not in TEXT


def test_the_mixer_never_starts_sooner_than_3_s():
    # Deliberate: a fault while the sound card boots can blue-screen the PC. Never remove or shorten it.
    nodes, edges = _scope(_named("Mixer"))
    delay = next(i for i, n in enumerate(nodes) if n.findtext(W + "Name") == "MixerBootDelay")
    assert _kind(nodes[delay]) == "rx:DelaySubscription"
    assert nodes[delay].find(W + "Combinator").findtext("{clr-namespace:Bonsai.Reactive;assembly=Bonsai.Core}DueTime") == "PT3S"
    expressions = [nodes[a].findtext("{clr-namespace:Bonsai.Scripting.Expressions;assembly=Bonsai.Scripting.Expressions}Expression")
                   for a in _upstream(edges, delay)]
    assert "TimeSpan.FromSeconds(Math.Max(3.0, SpeakerArray.Device.BootDelay))" in expressions


def test_the_shared_blocks_replace_the_local_copies():
    for block in ("OpenRigs.Devices:RigHardware.bonsai", "OpenRigs.Devices:HarpLogs.bonsai",
                  "OpenRigs.Devices:BehaviorBoard.DeliverReward.bonsai", "OpenRigs.Vision:ProjectorDisplay.bonsai"):
        assert f'Path="{block}"' in TEXT
    assert not re.search(r'Path="Extensions\\', TEXT)
    assert not list((ROOT / "src" / "Extensions").glob("*.bonsai"))


def test_video_is_logged_after_the_experiment_folder():
    logs = ET.tostring(_named("ExperimentLogs"), encoding="unicode")
    assert logs.count("OpenRigs.Logging:LogVideoFfmpeg.bonsai") == 3
    assert "OpenRigs.Logging:LogVideo.bonsai" not in logs


def test_the_launch_properties():
    # No MissingCalibration: a reward valve without a calibration always stops the workflow (Pip, 15 Nov 2026).
    top = list(MAIN.find(W + "Workflow").find(W + "Nodes"))
    exposed = {p.get("Name") for n in top if n.get(XSI) == "ExternalizedMapping" for p in n.iter(W + "Property")}
    assert {"SubjectId", "SessionId", "TaskPath", "DisplayDevice"} <= exposed and "MissingCalibration" not in exposed


def _declared_commands():
    control = next(e for e in MAIN.iter(W + "Expression") if e.get("Path") == "OpenRigs.Control:ExperimentControl.bonsai")
    return {c.findtext(W + "Name") for c in control.iter(W + "Command")}


def test_the_workflow_declares_every_command_it_handles():
    handled = set(re.findall(r"<orcl:Name>(\w+)</orcl:Name>", ET.tostring(_named("Commands"), encoding="unicode")))
    assert handled <= _declared_commands()


@pytest.mark.skipif(not SIBLING.exists(), reason="needs the lib-open-rigs checkout beside this repo")
def test_the_workflow_declares_every_lick_spout_stage_command():
    # ControlServer refuses an undeclared command, so the stage's own commands must be declared here (cross-repo).
    stage = (SIBLING / "src" / "OpenRigs.Devices" / "HarpLickSpoutStage.bonsai").read_text(encoding="utf-8-sig")
    commands = set(re.findall(r"<orcl:Name>(\w+)</orcl:Name>", stage))
    assert len(commands) == 14 and commands <= _declared_commands()


def test_the_readme_lists_every_shared_block():
    # Plan v11: the README's Shared Blocks table lists each lib-open-rigs block the workflow uses.
    readme = (ROOT / "README.md").read_text(encoding="utf-8")
    table = readme[readme.index("## Shared Blocks"):]
    table = table[: table.find("\n## ", 3)] if "\n## " in table[3:] else table
    # Every OpenRigs include, and every node of any xmlns naming an OpenRigs namespace (not only core:, dev: and zap:).
    includes = set(re.findall(r'Path="OpenRigs\.[\w.]+:([\w.]+)\.bonsai"', TEXT))
    prefixes = [p for p, ns in re.findall(r'xmlns:(\w+)="clr-namespace:([\w.]+);', TEXT) if ns.startswith("OpenRigs")]
    operators = {name for p in prefixes for name in re.findall(r'xsi:type="' + p + r':(\w+)"', TEXT)}
    assert includes and operators
    missing = sorted(name for name in includes | operators if f"`{name}`" not in table)
    assert not missing, f"not in the README's Shared Blocks table: {missing}"


def test_rewards_go_to_reward_requests():
    # One name for the reward request in both dome tasks (Pip, 15 Nov 2026), as coen-dome-navigation's.
    assert "DeliverRewardCommand" not in TEXT and TEXT.count("<Name>RewardRequests</Name>") == 4


def test_the_layout_is_tracked():
    # Audit round 5: a tracked layout replaces a stale per-machine one at Setup, as coen-dome-navigation's.
    assert (ROOT / "config" / "main.layout").read_text(encoding="utf-8").count("<VisualizerLayout") == 1
    assert "..\\config\\main.layout" in (ROOT / ".bonsai" / "Setup.ps1").read_text(encoding="utf-8-sig")


def test_the_auditory_stimuli_are_the_tasks_own_files():
    """The 100 stimuli are in the repo (production av-dome keeps them in its LocalFiles), mono 16-bit
    at the mixer's 48 kHz and one length for the sync gate: nothing put them in each rig's
    calibration folder, so the workflow stopped at Start on every rig (BLUE-OPTO-2, 14 Nov 2026)."""
    import wave

    folder = ROOT / "src" / "LocalFiles" / "AuditoryStimuli"
    shapes = set()
    for i in range(100):
        with wave.open(str(folder / f"filtered_pink_pulse_train_{i}.wav")) as w:
            shapes.add((w.getnchannels(), w.getsampwidth(), w.getframerate(), w.getnframes()))
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


PER_ELEMENT = {"rx:SelectMany", "rx:CreateObservable", "rx:Concat", "rx:Switch"}


def _subjects_closed_by_a_nested_multicast(top):
    """Each MulticastSubject inside a per-element nested graph whose subject is not a BehaviorSubject declared in this
    workflow or inside that graph. Bonsai 2.9 passes a multicast source's completion to a PublishSubject or
    ReplaySubject, so the first trial's graph closes it for good; a declared BehaviorSubject ignores it."""
    declared = {}

    def declare(wf):
        for n in wf.find(f"{W}Nodes"):
            if (n.get(XSI) or "").startswith("rx:") and (n.get(XSI) or "").endswith("Subject"):
                declared[n.findtext("{clr-namespace:Bonsai.Reactive;assembly=Bonsai.Core}Name")] = n.get(XSI)
            inner = n.find(f"{W}Workflow")
            if inner is not None:
                declare(inner)

    declare(top)
    found = []

    def walk(wf, nested, local):
        local = local | {n.findtext("{clr-namespace:Bonsai.Reactive;assembly=Bonsai.Core}Name") for n in wf.find(f"{W}Nodes")
                         if (n.get(XSI) or "").endswith("Subject") and (n.get(XSI) or "").startswith("rx:")}
        for n in wf.find(f"{W}Nodes"):
            if n.get(XSI) == "MulticastSubject" and nested:
                name = n.findtext(f"{W}Name")
                if name not in local and declared.get(name) != "rx:BehaviorSubject":
                    found.append(name)
            inner = n.find(f"{W}Workflow")
            if inner is not None:
                per = n.get(XSI) in PER_ELEMENT
                walk(inner, nested or per, local if not per else set())

    walk(top, False, set())
    return found


def test_no_trial_graph_closes_a_shared_subject():
    """Before 14 Nov 2026 the grating, the sound, the spouts and every board command after the first laser trial ran on
    trial 1 only, the trial rows giving no sign (BLUE-OPTO-2, counted live): a per-trial graph multicast into
    PublishSubjects. Shared event subjects are BehaviorSubjects or, where another graph subscribes per trial or the
    subject is a block's, fed through a BehaviorSubject feed relayed once at TaskLogic's top level."""
    # RngSeed's AsyncEvents multicast is at TaskLogic's top level since 14 Nov 2026 (it was in ExperimentLogs).
    assert _subjects_closed_by_a_nested_multicast(MAIN.find(f"{W}Workflow")) == []


def test_the_rng_seed_is_logged_at_trial_1s_go():
    # Audit, 14 Nov 2026: at trial 1's Go, as coen-dome-navigation (was at trial 1's draw); the Take after the multicast.
    nodes, edges = _scope(_named("TaskLogic"))
    seed = next(i for i, n in enumerate(nodes) if n.findtext(W + "Name") == "RngSeed")
    source = [a for a, b, _ in edges if b == seed]
    assert len(source) == 1 and nodes[source[0]].findtext(W + "Name") == "TrialGo" and nodes[source[0]].get(XSI) == "SubscribeSubject"
    assert not [n for n in _scope(_named("ExperimentLogs"))[0] if n.findtext(W + "Name") == "RngSeed"]


def test_the_penalty_sound_is_the_labs_at_the_tasks_gain():
    # Audit, 14 Nov 2026: OpenRigs.Audio's penalty sound, made once the task file's gain is in (Zip, InputMapping), never
    # at the default gain from the speakers alone.
    nodes, edges = _scope(_named("LoadAudioFiles"))
    penalty = next(i for i, n in enumerate(nodes) if _kind(n) == "aud:PenaltySoundBuffer")
    (mapping,) = [a for a, b, _ in edges if b == penalty]
    assert nodes[mapping].get(XSI) == "InputMapping"
    assert 'Name="Gain" Selector="Item1"' in ET.tostring(nodes[mapping], encoding="unicode")
    (zip_,) = [a for a, b, _ in edges if b == mapping]
    inputs = sorted((label, nodes[a]) for a, b, label in edges if b == zip_)
    assert "TaskParameters.Penalty.Sound.Gain" in inputs[0][1].findtext(W + "Selector")
    assert inputs[1][1].findtext(W + "Name") == "Speakers"
    assert "Penalty.Sound.Path" not in TEXT
