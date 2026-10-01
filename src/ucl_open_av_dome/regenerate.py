import json
import subprocess
from pathlib import Path

import pydantic

import ucl_open_av_dome.rig

SCHEMA_ROOT = Path("./src/DataSchemas/")
EXTENSIONS_ROOT = Path("./src/Extensions/")
SCHEMA_FILE = SCHEMA_ROOT / "ucl_open_av_dome.json"


def main():
    """Regenerate the JSON schema and Bonsai.Sgen C# classes from the pydantic models."""
    model = pydantic.RootModel[ucl_open_av_dome.rig.UclOpenAvDomeRig]
    schema = model.model_json_schema(by_alias=True, mode="serialization", union_format="primitive_type_array")
    SCHEMA_ROOT.mkdir(parents=True, exist_ok=True)
    SCHEMA_FILE.write_text(json.dumps(schema, indent=2))
    subprocess.run(
        [
            "dotnet",
            "bonsai.sgen",
            str(SCHEMA_FILE),
            "--output",
            str(EXTENSIONS_ROOT),
            "--serializer",
            "json",
            "--serializer",
            "yaml",
        ],
        check=True,
    )


if __name__ == "__main__":
    main()
