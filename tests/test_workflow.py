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
    from open_rigs.rigs.dome import ZAPIT_MAX_RAMP_DOWN_SECONDS
    assert float(nodes[ramp].find(W + "Combinator").findtext("{clr-namespace:OpenRigs.Core;assembly=OpenRigs.Core}DueSeconds")) == ZAPIT_MAX_RAMP_DOWN_SECONDS


def test_the_manual_reward_is_capped_as_every_reward():
    # Audit round 4: ManualRewardUl's Maximum is the lib's MAX_REWARD_UL (DeliverReward's MaxRewardUl).
    from open_rigs.rigs.dome import MAX_REWARD_UL
    parameter = next(p for p in MAIN.iter() if p.tag.endswith("Parameter") and p.findtext(W + "Name") == "ManualRewardUl")
    assert float(parameter.findtext(W + "Maximum")) == MAX_REWARD_UL


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
    for block in ("OpenRigs.Devices:DomeHardware.bonsai", "OpenRigs.Devices:DomeHarpLogs.bonsai",
                  "OpenRigs.Devices:BehaviorBoard.DeliverReward.bonsai", "OpenRigs.Vision:DomeDisplay.bonsai"):
        assert f'Path="{block}"' in TEXT
    assert not re.search(r'Path="Extensions\\', TEXT)
    assert not list((ROOT / "src" / "Extensions").glob("*.bonsai"))


def test_video_is_logged_after_the_experiment_folder():
    logs = ET.tostring(_named("ExperimentLogs"), encoding="unicode")
    assert logs.count("OpenRigs.Logging:LogVideo.bonsai") == 3


def test_missing_calibration_is_a_launch_property():
    top = list(MAIN.find(W + "Workflow").find(W + "Nodes"))
    exposed = {p.get("Name") for n in top if n.get(XSI) == "ExternalizedMapping" for p in n.iter(W + "Property")}
    assert {"SubjectId", "SessionId", "TaskPath", "MissingCalibration", "DisplayDevice"} <= exposed


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


def test_manual_rewards_use_their_own_subject_name():
    # Audit round 3: the subject a manual reward reads is ManualRewardUl, as the controller's parameter; no RewardUl.
    assert "<Name>RewardUl</Name>" not in TEXT and TEXT.count("<Name>ManualRewardUl</Name>") >= 4
