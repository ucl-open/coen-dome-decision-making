import json
from pathlib import Path

import pydantic
import pytest
import yaml
from pydantic import ValidationError

from ucl_open_coen_dome_decision_making.rig import UclOpenCoenDomeDecisionMakingRig

ROOT = Path(__file__).parent.parent
TEMPLATE = ROOT / "config" / "rig-template.yml"


def template() -> dict:
    return yaml.safe_load(TEMPLATE.read_text(encoding="utf-8"))


def test_rig_py_is_the_dome_rig_model():
    from open_rigs.rigs.dome import DomeRig

    assert UclOpenCoenDomeDecisionMakingRig is DomeRig


def test_the_template_validates_with_a_reward_valve_per_side_and_the_zapit_trigger():
    rig = UclOpenCoenDomeDecisionMakingRig.model_validate(template())
    wiring = rig.behavior_board["main"].wiring
    assert (wiring.do1.side.value, wiring.do2.side.value, wiring.do3.device) == ("left", "right", "ZapitTrigger")
    assert rig.behavior_board["main"].running_wheel.wheel_diameter_mm == 75


@pytest.mark.parametrize("key", ["rewardValves", "interPulseInterval"])
def test_the_old_keys_are_refused(key):
    # The generated C# refuses a rig.yml key it lacks, so a rig.yml still holding these stops at Start.
    committed = json.loads((ROOT / "src" / "DataSchemas" / "ucl_open_coen_dome_decision_making.json").read_text(encoding="utf-8"))
    assert key not in json.dumps(committed)


def test_a_side_without_its_valve_is_refused_naming_the_rule():
    data = template()
    del data["behaviorBoard"]["main"]["wiring"]["DO2"]
    with pytest.raises(ValidationError, match=r"right RewardValve.*dome rig model, open_rigs.rigs.dome"):
        UclOpenCoenDomeDecisionMakingRig.model_validate(data)


def test_the_committed_schema_is_the_model_s():
    # The generated C# is made from this file and refuses a rig.yml key it lacks.
    # Regenerate with `uv run python -m ucl_open_coen_dome_decision_making.regenerate`.
    committed = json.loads((ROOT / "src" / "DataSchemas" / "ucl_open_coen_dome_decision_making.json").read_text(encoding="utf-8"))
    model = pydantic.RootModel[UclOpenCoenDomeDecisionMakingRig].model_json_schema(
        by_alias=True, mode="serialization", union_format="primitive_type_array")
    assert committed == json.loads(json.dumps(model))
