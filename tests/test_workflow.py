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


def test_arm_zapit_waits_for_zapit_to_arm_the_trial():
    nodes, edges = _scope(_named("ArmZapit"))
    output = next(i for i, n in enumerate(nodes) if n.get(XSI) == "WorkflowOutput")
    check = next(i for i, n in enumerate(nodes) if _kind(n) == "p1:CheckZapitArmed")
    assert check in _upstream(edges, output)
    assert any(nodes[i].findtext(W + "Name") == "ZapitReplies" for i in _upstream(edges, check))
    feeding = [a for a, b, _ in edges if b == output]
    assert len(feeding) == 1 and _kind(nodes[feeding[0]]) == "rx:Take"


@pytest.mark.parametrize("name", ["ZapitError?", "SoftwareTriggeredRealMouse?", "NoConditionsLoaded?"])
def test_connect_zapit_stops_the_workflow(name):
    nodes, edges = _scope(_named("ConnectZapit"))
    condition = next(i for i, n in enumerate(nodes) if n.findtext(W + "Name") == name)
    downstream, frontier = set(), [condition]
    while frontier:
        a = frontier.pop()
        for x, b, _ in edges:
            if x == a and b not in downstream:
                downstream.add(b)
                frontier.append(b)
    assert any(_kind(nodes[i]) == "core:ThrowError" for i in downstream)


def test_the_mixer_never_starts_sooner_than_3_s():
    # Deliberate: a fault while the sound card boots can blue-screen the PC. Never remove or shorten it.
    mixer = TEXT[TEXT.index("<Name>Mixer</Name>"):TEXT.index("<Name>PlayStim</Name>")]
    assert re.search(r"<Name>MixerBootDelay</Name>[\s\S]*?<rx:DueTime>PT3S</rx:DueTime>", mixer)
    assert "Math.Max(3.0, SpeakerArray.Device.BootDelay)" in mixer


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
    includes = set(re.findall(r'Path="OpenRigs\.(?:Devices|Vision|Core|Logging|Control):([\w.]+)\.bonsai"', TEXT))
    operators = set(re.findall(r'xsi:type="(?:core|dev|zap):(\w+)"', TEXT))
    assert includes and operators
    missing = sorted(name for name in includes | operators if f"`{name}`" not in table)
    assert not missing, f"not in the README's Shared Blocks table: {missing}"
