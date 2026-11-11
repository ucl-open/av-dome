from enum import Enum
from typing import Dict, Literal

from pydantic import Field, model_validator
from swc.aeon.schema import BaseSchema
from open_rigs.components import ProjectionCalibration, SpeakerArray
from open_rigs.core.rig import Rig
from open_rigs.devices import (
    HarpTimestampGeneratorGen3,
    LicketySplit,
    StepperPositions,
)
# The lab's board, camera and stepper driver: open_rigs' with a liveView: block (OpenRigs.LiveView),
# the board's usbSerial required (OpenRigs.HarpDevices checks the board by it), and the board's and
# driver's wiring: (what each line or motor is connected to).
from open_rigs.rigs.behaviour_box import BehaviorBoard, HarpStepperDriver, SpinnakerCamera

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


class ValveLine(str, Enum):
    """A behavior board digital output that can drive a reward valve."""

    DO1 = "DO1"
    DO2 = "DO2"
    DO3 = "DO3"


class RewardValves(BaseSchema):
    """Which behavior board line drives each side's reward valve. The workflow reads it at startup
    (LoadSideRewardLine): a left reward pulses the left line, a right reward the right line, each
    with that line's entry in behaviorBoard.main.pulseController.outputs. rig-control's Calibrations
    page names each valve line's side from it."""

    left: ValveLine = Field(description="The line that drives the left lick spout's reward valve")
    right: ValveLine = Field(description="The line that drives the right lick spout's reward valve")

    @model_validator(mode="after")
    def _two_lines(self):
        if self.left == self.right:
            raise ValueError(f"rewardValves gives {self.left.value} to both sides; each side needs its own line")
        return self


class UclOpenAvDomeRig(Rig):
    """Rig configuration for the av-dome experiment.

    Each kind of device is a group of named entries, as the lab's rig.yml (open_rigs.rigs.behaviour_box):
    the name in the data is the kind and the entry joined by a hyphen, so behaviorBoard: main: is
    BehaviorBoard-Main. The workflow takes each device it drives by that name (RigDevice). Harp kinds
    are named as lib-open-rigs' HarpRig names them, so RecordHarpDevices checks each board's WhoAmI.
    """

    version: Literal[__semver__] = __semver__
    behavior_board: Dict[str, BehaviorBoard] = Field(
        description="Harp Behavior boards by name: main (BehaviorBoard-Main) drives the valves, camera trigger and wheel encoder"
    )
    timestamp_generator_gen3: Dict[str, HarpTimestampGeneratorGen3] = Field(
        description="Harp timestamp generators by name: main (TimestampGeneratorGen3-Main) syncs the boards' clocks"
    )
    stepper_driver: Dict[str, HarpStepperDriver] = Field(
        description=(
            "Harp StepperDriver boards by name: a and b (StepperDriver-A, StepperDriver-B) drive the lick spout "
            "stage's 5 axes; which motor moves which axis is fixed in the workflow"
        )
    )
    lickety_split: Dict[str, LicketySplit] = Field(
        description="Lick detectors by name: left (LicketySplit-Left) and right (LicketySplit-Right)"
    )
    camera: Dict[str, SpinnakerCamera] = Field(
        description="Spinnaker cameras by name: face, pupil and body (Camera-Face, Camera-Pupil, Camera-Body)"
    )
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
    reward_valves: RewardValves = Field(
        description="Which behavior board line drives the left and which the right reward valve, such as { left: DO1, right: DO2 }"
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
