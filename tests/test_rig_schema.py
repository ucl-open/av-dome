from pathlib import Path

import pytest
import yaml
from pydantic import ValidationError

from ucl_open_av_dome.rig import UclOpenAvDomeRig

TEMPLATE = Path(__file__).parent.parent / "config" / "rig-template.yml"


def template() -> dict:
    return yaml.safe_load(TEMPLATE.read_text(encoding="utf-8"))


def test_the_rig_template_validates_and_names_each_side_s_reward_valve():
    rig = UclOpenAvDomeRig.model_validate(template())
    assert (rig.reward_valves.left.value, rig.reward_valves.right.value) == ("DO1", "DO2")


def test_reward_valves_is_required():
    # The workflow takes each side's line from it (LoadSideRewardLine); nothing is assumed.
    data = template()
    del data["rewardValves"]
    with pytest.raises(ValidationError):
        UclOpenAvDomeRig.model_validate(data)


def test_the_sides_may_be_swapped():
    data = template()
    data["rewardValves"] = {"left": "DO2", "right": "DO1"}
    assert UclOpenAvDomeRig.model_validate(data).reward_valves.left.value == "DO2"


def test_the_committed_schema_is_the_model_s():
    # The generated C# (UclOpenAvDome.Generated.cs) is made from this file and refuses a rig.yml key
    # it lacks: a stale schema stopped every rig whose rig.yml had a board's wiring (audit, 8 Nov 2026).
    # Regenerate with `uv run python -m ucl_open_av_dome.regenerate`.
    import json

    import pydantic

    committed = json.loads((TEMPLATE.parent.parent / "src" / "DataSchemas" / "ucl_open_av_dome.json").read_text(encoding="utf-8"))
    model = pydantic.RootModel[UclOpenAvDomeRig].model_json_schema(
        by_alias=True, mode="serialization", union_format="primitive_type_array")
    assert committed == json.loads(json.dumps(model))


def test_a_board_s_wiring_validates():
    data = template()
    data["behaviorBoard"]["main"]["wiring"] = {"DO1": {"device": "RewardValve", "side": "left"}}
    assert UclOpenAvDomeRig.model_validate(data).behavior_board["main"].wiring.do1.side.value == "left"


@pytest.mark.parametrize("sides",[{"left": "DO1", "right": "DO1"}, {"left": "DO4", "right": "DO1"}, {"left": "DO1"}])
def test_each_side_needs_its_own_board_line(sides):
    data = template()
    data["rewardValves"] = sides
    with pytest.raises(ValidationError):
        UclOpenAvDomeRig.model_validate(data)
