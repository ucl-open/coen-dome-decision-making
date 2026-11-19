from pathlib import Path

from open_rigs.rigs import conditioned_task as ct

from ucl_open_coen_dome_decision_making.task import UclOpenCoenDomeDecisionMakingTaskLogic, UclOpenCoenDomeDecisionMakingTaskParameters

SCHEMA_ROOT = Path("./src/DataSchemas/")


def main():
    """Regenerate the task's JSON schema, TaskSchema.Generated.cs and Bonsai.Sgen C# classes from its pydantic model (the rig's are lib-open-rigs' OpenRigs.Devices.Rigs)."""
    ct.regenerate(UclOpenCoenDomeDecisionMakingTaskLogic, UclOpenCoenDomeDecisionMakingTaskParameters, SCHEMA_ROOT / "ucl_open_coen_dome_decision_making_task.json")


if __name__ == "__main__":
    main()
