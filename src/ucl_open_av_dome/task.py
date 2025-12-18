# Import core types
from typing import Literal
from pydantic import Field

from swc.aeon.io import reader
from swc.aeon.schema import BaseSchema, data_reader

from ucl_open_av_dome import __semver__

# TODO - should inherit from some TaskParameters base class rather than BaseSchema
class UclOpenAvDomeTaskParameters(BaseSchema):
    ...


class UclOpenAvDomeTaskLogic(BaseSchema):
    version: Literal[__semver__] = __semver__
    name: Literal["UclOpenAvDome"] = Field(default="UclOpenAvDome", description="Name of the task logic", frozen=True)
    task_parameters: UclOpenAvDomeTaskParameters = Field(description="Parameters of the task logic")
    ...