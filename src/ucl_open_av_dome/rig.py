from typing import Literal

from pydantic import Field
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
