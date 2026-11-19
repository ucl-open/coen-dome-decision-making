"""The task files app-rig-control writes at launch (tests/fixtures/handover, golden files its tests/test_handover.py writes
for every stage of the share's curricula): each one this repo's task model takes (the schema, x-rules,
x-session-fields, x-required-conditions and the x-max-combinations cap, all in model_validate), none storing a trialType
(x-trial-types derives it). The C# draw of the same files is HandoverTests.cs."""

from pathlib import Path

import pytest
import yaml
from open_rigs.rigs import conditioned_task as ct
from open_rigs.rigs.conditions import derive_trial_type

from ucl_open_coen_dome_decision_making.task import TRIAL_TYPES, UclOpenCoenDomeDecisionMakingTaskParameters

FIXTURES = Path(__file__).parent / "fixtures"
CONFIG = Path(__file__).parent.parent / "config"
CURRICULA = Path(r"K:\config\curricula\coen-dome-decision-making")
# The old task files' task-level target: a K: condition's where it sets none (step 6 writes it into that condition).
OLD_TARGET = {"task-localization": "visualSide", "task-discrimination-left-right": "left", "task-discrimination-go-no-go": "go"}


def test_each_golden_file_is_a_task_this_repo_takes():
    assert ct.handover_problems(UclOpenCoenDomeDecisionMakingTaskParameters, FIXTURES) == []


def _renamed(values: dict) -> dict:
    # The old names x-trial-types reads, as the K: migration converts them (amplitude 0 mute, 1 -40).
    level = lambda a: "mute" if a == 0 else -40  # noqa: E731
    out = {}
    for path, value in (values or {}).items():
        if path == "auditoryStimulus.amplitude":
            path, value = "auditoryStimulus.levelDbfs", [level(a) for a in value] if isinstance(value, list) else level(value)
        out["visualStimulus.contrasts" if path == "contrasts" else path] = value
    return out


@pytest.mark.skipif(not CURRICULA.is_dir(), reason="the share (K:) is not mapped")
def test_each_k_stage_s_stored_trial_type_is_the_one_derived_after_the_migration():
    # Before step 6 deletes the share's stored trialTypes (read only here): dropping them renames no condition. Delete this
    # test once step 6 has run.
    stored = 0
    for stem, target in OLD_TARGET.items():
        data = yaml.safe_load((CONFIG / f"{stem}.yml").read_text(encoding="utf-8"))["taskParameters"]
        task = UclOpenCoenDomeDecisionMakingTaskParameters.model_validate(data).model_dump(by_alias=True, mode="json")
        for stage in yaml.safe_load((CURRICULA / f"{stem}.conditions.yml").read_text(encoding="utf-8"))["stages"]:
            for i, condition in enumerate(stage["conditions"]):
                if "trialType" not in condition:
                    continue
                values = {"target": target, **_renamed(stage.get("set")), **_renamed(condition.get("set"))}
                pairs = [["visualStimulus.contrasts" if path == "contrasts" else path for path in group] for group in condition.get("pairs") or []]
                derived = derive_trial_type(list(TRIAL_TYPES), task, values, pairs)
                assert derived == condition["trialType"], (stem, stage["id"], i, derived)
                stored += 1
    assert stored, "no K: stage stores a trialType any more (step 6 ran): delete this test"
