from typing import Literal

from pydantic import Field
from swc.aeon.schema import BaseSchema
from ucl_open.components import ProjectionCalibration, SpeakerArray
from ucl_open.core.rig import Rig
from ucl_open.devices import (
    BehaviorBoard,
    HarpLickSpoutStage,
    HarpTimestampGeneratorGen3,
    LicketySplit,
    StepperPositions,
)
from ucl_open.video.video import SpinnakerCamera

from ucl_open_av_dome import __semver__


class ZapitServer(BaseSchema):
    """TCP endpoint of the Zapit optogenetics server (tcpServer section of the Zapit settings file)."""

    host: str = Field(
        default="127.0.0.1",
        description="Host name or IP address of the PC running Zapit; 127.0.0.1 when Zapit runs on this rig PC",
    )
    port: int = Field(default=1488, ge=1, le=65535, description="TCP port the Zapit server listens on")
    hardware_triggered: bool = Field(
        default=False,
        description=(
            "True: Zapit arms the DAQ on the TCP command and the behavior board fires a TTL on triggerLine "
            "when Zapit confirms it is armed. False: Zapit starts the stimulus on receipt of the command."
        ),
    )
    trigger_line: int = Field(
        default=3,
        ge=0,
        le=3,
        description=(
            "Behavior board digital output (0 to 3) wired to the Zapit DAQ trigger input; pulsed with the width set in "
            "behaviorBoard.pulseController.pulseWidths for that line. Only used when hardwareTriggered is true."
        ),
    )


class UclOpenAvDomeRig(Rig):
    """Rig configuration for the av-dome experiment."""

    version: Literal[__semver__] = __semver__
    behavior_board: BehaviorBoard = Field(description="Main Harp behavior board (valve outputs, wheel encoder)")
    timestamp_generator: HarpTimestampGeneratorGen3 = Field(description="Harp timestamp generator (clock sync)")
    lick_spout_stage: HarpLickSpoutStage = Field(
        description="Two Harp StepperDriver boards driving the 5-axis lick spout stage"
    )
    lickety_split_left: LicketySplit = Field(description="Left lick detector")
    lickety_split_right: LicketySplit = Field(description="Right lick detector")
    camera_face: SpinnakerCamera = Field(description="Face camera")
    camera_pupil: SpinnakerCamera = Field(description="Pupil camera")
    camera_body: SpinnakerCamera = Field(description="Body camera")
    projection_calibration: ProjectionCalibration = Field(
        description="Mesh-map and gamma-LUT file paths for the dome projection"
    )
    speaker_array: SpeakerArray = Field(description="Dome speaker array and audio output device")
    lick_spout_default_position: StepperPositions = Field(
        description="Default absolute stage position at session start"
    )
    zapit: ZapitServer = Field(
        description="Zapit optogenetics server endpoint; only contacted when the task enables optogenetics"
    )
    # Reward delivery structure; moves to the task schema when task parameters migrate.
    inter_pulse_interval: int = Field(
        default=50,
        ge=0,
        description=(
            "Gap between consecutive reward pulses, in milliseconds. Must be long enough for the "
            "valve to seat before the next trigger; 0 re-triggers the instant the board closes the "
            "valve and risks merged or dropped pulses."
        ),
    )
