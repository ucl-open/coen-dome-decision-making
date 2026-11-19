"""The task files app-rig-control writes at launch (tests/fixtures/handover, golden files its tests/test_handover.py writes
for every stage of the share's curricula): each one this repo's task model takes (the schema, x-rules,
x-session-fields, x-required-conditions and the x-max-combinations cap, all in model_validate), none storing a trialType
(x-trial-types derives it). The C# draw of the same files is HandoverTests.cs."""

from pathlib import Path

from open_rigs.rigs import conditioned_task as ct

from ucl_open_coen_dome_decision_making.task import UclOpenCoenDomeDecisionMakingTaskParameters

FIXTURES = Path(__file__).parent / "fixtures"


def test_each_golden_file_is_a_task_this_repo_takes():
    assert ct.handover_problems(UclOpenCoenDomeDecisionMakingTaskParameters, FIXTURES) == []
