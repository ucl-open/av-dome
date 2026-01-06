from typing import Literal, Dict
from pydantic import Field

from ucl_open.rigs.base import BaseSchema
import ucl_open.rigs.device as Device
from ucl_open.rigs.video import SpinnakerCamera

from ucl_open_av_dome import __semver__

class UclOpenAvDomeRig(BaseSchema):
    version: Literal[__semver__] = __semver__
    behavior_boards: Dict[str, Device.BehaviorBoard] = Field(description="Mapping from logical names to BehaviorBoard devices (e.g. 'main', 'aux').")
    lickety_splits: Dict[str, Device.LicketySplit] = Field(description="Mapping from logical names to LicketySplit devices")
    cameras: Dict[str, SpinnakerCamera] = Field(description="Mapping from logical names to Spinnaker camera devices")
    led_driver: Dict[str, Device.ArduinoDevice] = Field(description="Mapping from logical names to Arduino devices driving LEDs")
    lick_spout_stage: Dict[str, Device.LickSpoutStageDriver] = Field(description="Mapping from logical names to Arduino devices driving stepper motors controlling a lick spout stage")
