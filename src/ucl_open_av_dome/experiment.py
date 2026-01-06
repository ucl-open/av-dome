# Import core types
from typing import Literal, Dict
import json
from pathlib import Path
from pydantic import Field
from ucl_open.rigs.experiment import Experiment as ExperimentBase
from ucl_open.rigs.base import BaseSchema
from ucl_open_av_dome import __semver__, rig

class Experiment(ExperimentBase):
    rig : rig.UclOpenAvDomeRig

class AvDome(BaseSchema):
    experiment: Experiment|None = None

def main():
    schema = AvDome.model_json_schema(union_format="primitive_type_array")
    schema.pop('properties')
    Path("AvDome.json").write_text(json.dumps(schema, indent=2))

if __name__ == "__main__":
    main()