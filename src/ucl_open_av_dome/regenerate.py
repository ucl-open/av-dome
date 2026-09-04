import json
import subprocess
from pathlib import Path

import pydantic

import ucl_open_av_dome.rig
import ucl_open_av_dome.task

SCHEMA_ROOT = Path("./src/DataSchemas/")
EXTENSIONS_ROOT = Path("./src/Extensions/")


def regenerate(model_type: type, schema_file: Path) -> None:
    """Write the JSON schema for one root model and run Bonsai.Sgen on it."""
    model = pydantic.RootModel[model_type]
    schema = model.model_json_schema(by_alias=True, mode="serialization", union_format="primitive_type_array")
    schema_file.write_text(json.dumps(schema, indent=2))
    subprocess.run(
        [
            "dotnet",
            "bonsai.sgen",
            str(schema_file),
            "--output",
            str(EXTENSIONS_ROOT),
            "--serializer",
            "json",
            "--serializer",
            "yaml",
        ],
        check=True,
    )


def main():
    """Regenerate the JSON schemas and Bonsai.Sgen C# classes from the pydantic models."""
    SCHEMA_ROOT.mkdir(parents=True, exist_ok=True)
    regenerate(ucl_open_av_dome.rig.UclOpenAvDomeRig, SCHEMA_ROOT / "ucl_open_av_dome.json")
    regenerate(ucl_open_av_dome.task.UclOpenAvDomeTaskLogic, SCHEMA_ROOT / "ucl_open_av_dome_task.json")


if __name__ == "__main__":
    main()
