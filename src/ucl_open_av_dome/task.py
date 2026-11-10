from enum import StrEnum
from typing import List, Literal, Optional

from pydantic import ConfigDict, Field, field_validator, model_validator
from swc.aeon.schema import BaseSchema
from open_rigs.components import SphericalPosition
from open_rigs.core.base import Bool, Double, Int, String
from open_rigs.core.task import Task, TaskParameters

from ucl_open_av_dome import __semver__

TRIAL_TYPE_COUNT = 5
"""Trial types, by index: 0 unisensory visual, 1 unisensory audio, 2 multisensory coherent,
3 multisensory conflict, 4 neutral."""


class TrialStructure(BaseSchema):
    """Timing parameters of the trial state machine."""

    iti_min_seconds: Double = Field(default=4.0, ge=0, description="Lower bound of the uniformly drawn inter-trial interval, in seconds")
    iti_max_seconds: Double = Field(default=5.0, ge=0, description="Upper bound of the uniformly drawn inter-trial interval, in seconds")
    quiescent_min_seconds: Double = Field(default=0.1, ge=0, description="Lower bound of the uniformly drawn quiescent period, in seconds")
    quiescent_max_seconds: Double = Field(default=0.25, ge=0, description="Upper bound of the uniformly drawn quiescent period, in seconds")
    open_loop_seconds: Double = Field(default=0.0, ge=0, description="Open-loop period after stimulus onset, in seconds")
    closed_loop_seconds: Double = Field(default=1.5, ge=0, description="Closed-loop response period, in seconds")
    spout_in_delay_seconds: Double = Field(default=0.5, ge=0, description="Delay from stimulus onset to moving the response spouts in, in seconds")


class VisualStimulus(BaseSchema):
    """Grating stimulus parameters common to all trials; contrast is set per training stage."""

    azimuth_min_deg: Double = Field(default=-60.0, ge=-180, le=180, description="Lower bound of the uniformly drawn grating azimuth, in degrees")
    azimuth_max_deg: Double = Field(default=60.0, ge=-180, le=180, description="Upper bound of the uniformly drawn grating azimuth, in degrees")
    fixed_azimuths_deg: Optional[List[Double]] = Field(
        default=None,
        description="Discrete grating azimuths, in degrees; when set and non-empty, every trial draws from this list (filtered to the trial's side, capped on coherent trials) instead of [azimuthMinDeg, azimuthMaxDeg]",
    )
    elevation_min_deg: Double = Field(default=15.0, ge=-90, le=90, description="Lower bound of the uniformly drawn grating elevation, in degrees")
    elevation_max_deg: Double = Field(default=15.0, ge=-90, le=90, description="Upper bound of the uniformly drawn grating elevation, in degrees")
    coherent_azimuth_cap_deg: Double = Field(default=80.0, ge=0, le=180, description="On coherent trials the grating azimuth magnitude is kept below this cap, in degrees")
    gaussian_window_sigma_deg: Double = Field(default=20.0, description="Sigma of the Gaussian aperture window, in degrees")
    orientation_deg: Double = Field(default=0.0, description="Grating stripe orientation, in degrees; drift direction is orthogonal to it (orientationDeg + 90 degrees)")
    spatial_frequency_cpd: Double = Field(default=0.066666667, description="Grating spatial frequency, in cycles per degree")
    temporal_frequency_hz: Double = Field(default=0.0, ge=0, description="Unsigned grating drift rate, in Hz, drifting orthogonal to orientationDeg (orientationDeg + 90 degrees); 0 is static")

    @model_validator(mode="after")
    def _bounds_ordered(self) -> "VisualStimulus":
        if self.azimuth_max_deg < self.azimuth_min_deg:
            raise ValueError("azimuth_max_deg must be >= azimuth_min_deg")
        if self.elevation_max_deg < self.elevation_min_deg:
            raise ValueError("elevation_max_deg must be >= elevation_min_deg")
        return self

    @model_validator(mode="after")
    def _overwrite_azimuths_cover_both_sides(self) -> "VisualStimulus":
        # Coherent and conflict trials draw from one side each, so a list needs a usable value on
        # both. InitialiseTrial.cs repeats this check, since Bonsai loads the YAML without pydantic.
        values = self.fixed_azimuths_deg
        if not values:
            return self
        for value in values:
            if not -180 <= value <= 180:
                raise ValueError(f"fixed_azimuths_deg value {value} is outside [-180, 180]")
        cap = self.coherent_azimuth_cap_deg
        if not any(0 < value <= cap for value in values):
            raise ValueError(f"fixed_azimuths_deg needs a value in (0, {cap}] (coherent_azimuth_cap_deg) for right-side trials")
        if not any(-cap <= value < 0 for value in values):
            raise ValueError(f"fixed_azimuths_deg needs a value in [-{cap}, 0) (coherent_azimuth_cap_deg) for left-side trials")
        return self


class AudioStimulus(BaseSchema):
    """Auditory stimulus parameters common to all trials."""

    speaker_locations: List[SphericalPosition] = Field(
        description="Candidate lateral sound positions, one drawn per audio trial; no azimuth-0 entry, the centre sound of visual-only and neutral trials is fixed in code"
    )

    @field_validator("speaker_locations")
    @classmethod
    def _at_least_one_location(cls, value: List[SphericalPosition]) -> List[SphericalPosition]:
        if not value:
            raise ValueError("speaker_locations must have at least one entry")
        # A centre entry would make some unisensory audio trials centre trials with a coin-flip target.
        if any(location.azimuth == 0 for location in value):
            raise ValueError("speaker_locations must not have an azimuth-0 entry; the centre sound is fixed in code")
        return value


class RewardSettings(BaseSchema):
    """Reward delivery parameters; per-stage reward size lives in the training stage table."""

    reward_probability: Double = Field(default=1.0, ge=0, le=1, description="Probability that a correct response is rewarded")
    large_reward_multiplier: Double = Field(default=1.0, ge=0, description="Reward size multiplier applied on large-reward trials")
    large_reward_probability: Double = Field(default=0.0, ge=0, le=1, description="Probability that a trial is a large-reward trial")


class ResponseSettings(BaseSchema):
    """Lick-accumulation response rule; thresholdLicks 1 with degreesPerLick 0 is classic first-lick 2AFC."""

    threshold_licks: Int = Field(default=1, ge=1, description="Net lick difference (left minus right, absolute) that terminates the response window")
    degrees_per_lick: Double = Field(default=0.0, description="Degrees the grating azimuth moves per net lick difference; 0 keeps the stimulus fixed")


class PunishmentSound(BaseSchema):
    """The error sound played on an incorrect response."""

    enabled: Bool = Field(default=False, description="Whether the error sound plays on incorrect responses")
    path: String = Field(default="LocalFiles\\error_tone.wav", description="Wav file of the error sound")
    channel: Int = Field(default=2, ge=0, description="Mixer output channel the error sound plays on")
    gain: Double = Field(default=1.0, ge=0, description="Playback gain of the error sound")


class Punishment(BaseSchema):
    """What an error costs, as the lab's tasks name it (the trial log's punishSound and punishTimeoutSeconds):
    the timeout after an incorrect response or a miss (a miss on an auto-reward trial is rewarded instead), and
    the error sound on an incorrect response."""

    timeout_seconds: Double = Field(default=4.0, ge=0, description="Timeout after an incorrect response or a miss, in seconds")
    sound: PunishmentSound = Field(default=PunishmentSound(), description="The error sound on an incorrect response")


class TaskType(StrEnum):
    """What the mouse answers."""

    LOCALIZATION = "localization"
    DISCRIMINATION = "discrimination"


class Modality(StrEnum):
    """A stimulus modality."""

    VISUAL = "visual"
    AUDITORY = "auditory"


class RepeatRule(StrEnum):
    """When a trial type is repeated after a failed trial."""

    NEVER = "never"
    ON_FAILURE = "onFailure"
    ON_TIMEOUT = "onTimeout"


class RepeatSettings(BaseSchema):
    """Trial repetition policy."""

    max_repeats: Int = Field(default=5, ge=0, description="Maximum number of consecutive repeats of the same trial")
    rules_per_type: List[RepeatRule] = Field(
        default=[
            RepeatRule.ON_FAILURE,
            RepeatRule.ON_FAILURE,
            RepeatRule.ON_FAILURE,
            RepeatRule.ON_TIMEOUT,
            RepeatRule.ON_TIMEOUT,
        ],
        description="Repeat rule per trial type, indexed by trial type",
    )

    @field_validator("rules_per_type")
    @classmethod
    def _one_rule_per_trial_type(cls, value: List[RepeatRule]) -> List[RepeatRule]:
        if len(value) != TRIAL_TYPE_COUNT:
            raise ValueError(f"rules_per_type must have exactly {TRIAL_TYPE_COUNT} entries, got {len(value)}")
        return value


class OptogeneticsSettings(BaseSchema):
    """Zapit optogenetic stimulation parameters. Names as coen-dome-navigation's where they mean the same (Pip,
    9 Nov 2026); laser_duration_seconds is a timed stimulus here, where the corridor's laserMaxSeconds is a cap."""

    # Required with optogenetics on, in the JSON schema too: rig-control checks the task file against the
    # schema, not this model. additionalProperties is spelt out because rig-control closes every object
    # that lists properties (as coen-dome-navigation's).
    model_config = ConfigDict(
        json_schema_extra={
            "if": {"properties": {"enabled": {"const": True}}, "required": ["enabled"], "additionalProperties": True},
            "then": {"properties": {"laserPowerMw": {"type": "number"}}, "required": ["laserPowerMw"], "additionalProperties": True},
        }
    )

    enabled: Bool = Field(default=False, description="Whether optogenetic stimulation is enabled")
    zapit_config_path: String = Field(default="", description="Path to the Zapit conditions file (the one loaded in Zapit) listing the stimulation conditions. Named ...ConfigPath so rig-control keeps a copy beside the task file at Go")
    laser_power_mw: Optional[Double] = Field(
        default=None,
        gt=0,
        description="Laser power on laser-on trials, in mW, the same for every condition: sent to Zapit with each StartStimulus (overriding the conditions file's power) and logged as laserPowerMw (0 on laser-off trials). Required with optogenetics on",
    )
    laser_on_probability: Double = Field(default=0.75, ge=0, le=1, description="Probability that a trial after trials_before_laser is a laser-on trial")
    laser_delay_seconds: Double = Field(default=0.0, ge=0, description="Delay from the trial's start to laser onset, in seconds")
    laser_duration_seconds: Double = Field(default=1.5, ge=0, description="How long the laser stays on, in seconds (a timed stimulus)")
    no_laser_on_repeats: Bool = Field(default=True, description="Whether repeat trials never have the laser on")
    balanced_conditions: Bool = Field(default=True, description="Draw conditions from a shuffled queue, so each is used equally often, rather than independently each trial")
    trials_before_laser: Int = Field(default=0, ge=0, description="Trials at the experiment's start with no laser-on trial among them (counted by TrialCount, repeats included)")

    @model_validator(mode="after")
    def _power_when_on(self) -> "OptogeneticsSettings":
        if self.enabled and self.laser_power_mw is None:
            raise ValueError("optogenetics is enabled but laser_power_mw is not set")
        return self

class TrainingStage(BaseSchema):
    """One complete row of the training stage table; stages are selected by index at runtime."""

    label: String = Field(description="Human-readable stage name, for documentation and logs")
    trial_weights: List[Int] = Field(description="Relative draw weight per trial type, indexed by trial type")
    auto_reward_probability: Double = Field(ge=0, le=1, description="Probability that a trial is an auto-reward trial: a miss on it is rewarded at the target spout, and still counts as a timeout for repeats")
    both_spouts_probability: Double = Field(ge=0, le=1, description="Probability that both spouts are presented rather than only the correct one")
    reward_size_ul: Double = Field(ge=0, description="Reward size, in microliters; delivered as ceil(size/2) valve pulses")
    contrasts: List[Double] = Field(description="Candidate grating contrasts to draw from on visual trials")
    allow_repeats: Bool = Field(description="Whether failed trials may be repeated in this stage")
    spout_movements: Bool = Field(
        default=True,
        description="Whether the spouts move during trials; false keeps them in (habituation): no withdrawal and no single-spout move for a reward",
    )

    @field_validator("trial_weights")
    @classmethod
    def _one_weight_per_trial_type(cls, value: List[int]) -> List[int]:
        if len(value) != TRIAL_TYPE_COUNT:
            raise ValueError(f"trial_weights must have exactly {TRIAL_TYPE_COUNT} entries, got {len(value)}")
        return value


class UclOpenAvDomeTaskParameters(TaskParameters):
    """Task parameters for the av-dome audiovisual tasks: localization and discrimination."""

    task_type: TaskType = Field(
        default=TaskType.LOCALIZATION,
        description="localization: the answer is the side the stimulus came from. discrimination: the answer is the modality, the left spout for targetModality and the right spout for the other; trial types 0 and 1 are then target and distractor trials",
    )
    target_modality: Modality = Field(
        default=Modality.VISUAL,
        description="Discrimination only: the modality answered at the left spout",
    )
    trial_structure: TrialStructure = Field(description="Timing parameters of the trial state machine")
    visual_stimulus: VisualStimulus = Field(description="Grating stimulus parameters")
    audio_stimulus: AudioStimulus = Field(description="Auditory stimulus parameters")
    response: ResponseSettings = Field(description="Lick-accumulation response rule")
    punishment: Punishment = Field(description="The timeout and error sound an error costs")
    reward: RewardSettings = Field(description="Reward delivery parameters")
    repeats: RepeatSettings = Field(description="Trial repetition policy")
    optogenetics: OptogeneticsSettings = Field(description="Zapit optogenetic stimulation parameters")
    training_stages: List[TrainingStage] = Field(
        description="Training stage table indexed by stage id; ids past the end clamp to the last row"
    )

    @field_validator("training_stages")
    @classmethod
    def _at_least_one_stage(cls, value: List[TrainingStage]) -> List[TrainingStage]:
        if not value:
            raise ValueError("training_stages must have at least one entry")
        return value


class UclOpenAvDomeTaskLogic(Task):
    """Task logic definition for the av-dome experiment."""

    version: Literal[__semver__] = __semver__
    name: Literal["UclOpenAvDome"] = Field(default="UclOpenAvDome", description="Name of the task logic", frozen=True)
    task_parameters: UclOpenAvDomeTaskParameters = Field(description="Parameters of the task logic")
