from enum import StrEnum
from typing import List, Literal

from pydantic import Field, field_validator, model_validator
from swc.aeon.schema import BaseSchema
from ucl_open.components import SphericalPosition
from ucl_open.core.base import Bool, Double, Int, String
from ucl_open.core.task import Task, TaskParameters

from ucl_open_av_dome import __semver__

TRIAL_TYPE_COUNT = 5
"""Trial types, by index: 0 unisensory visual, 1 unisensory audio, 2 multisensory coherent,
3 multisensory conflict, 4 neutral."""


class TrialStructure(BaseSchema):
    """Timing parameters of the trial state machine."""

    iti_min: Double = Field(default=4.0, ge=0, description="Lower bound of the uniformly drawn inter-trial interval, in seconds")
    iti_max: Double = Field(default=5.0, ge=0, description="Upper bound of the uniformly drawn inter-trial interval, in seconds")
    quiescent_min: Double = Field(default=0.1, ge=0, description="Lower bound of the uniformly drawn quiescent period, in seconds")
    quiescent_max: Double = Field(default=0.25, ge=0, description="Upper bound of the uniformly drawn quiescent period, in seconds")
    open_loop_duration: Double = Field(default=0.0, ge=0, description="Open-loop period after stimulus onset, in seconds")
    closed_loop_duration: Double = Field(default=1.5, ge=0, description="Closed-loop response period, in seconds")
    incorrect_timeout_duration: Double = Field(default=4.0, ge=0, description="Timeout after an incorrect response, in seconds")
    spout_in_delay: Double = Field(default=0.5, ge=0, description="Delay from stimulus onset to moving the response spouts in, in seconds")


class VisualStimulus(BaseSchema):
    """Grating stimulus parameters common to all trials; contrast is set per training stage."""

    azimuth_min: Double = Field(default=-60.0, ge=-180, le=180, description="Lower bound of the uniformly drawn grating azimuth, in degrees")
    azimuth_max: Double = Field(default=60.0, ge=-180, le=180, description="Upper bound of the uniformly drawn grating azimuth, in degrees")
    elevation_min: Double = Field(default=15.0, ge=-90, le=90, description="Lower bound of the uniformly drawn grating elevation, in degrees")
    elevation_max: Double = Field(default=15.0, ge=-90, le=90, description="Upper bound of the uniformly drawn grating elevation, in degrees")
    coherent_azimuth_cap: Double = Field(default=80.0, ge=0, le=180, description="On coherent trials the grating azimuth magnitude is kept below this cap, in degrees")
    gaussian_window_sigma: Double = Field(default=20.0, description="Sigma of the Gaussian aperture window, in degrees")
    orientation: Double = Field(default=0.0, description="Grating orientation, in degrees")
    spatial_frequency: Double = Field(default=0.066666667, description="Grating spatial frequency, in cycles per degree")
    temporal_frequency: Double = Field(default=0.0, description="Grating drift temporal frequency, in Hz")
    flicker_frequency: Double = Field(default=8.0, description="Grating contrast flicker frequency, in Hz")

    @model_validator(mode="after")
    def _bounds_ordered(self) -> "VisualStimulus":
        if self.azimuth_max < self.azimuth_min:
            raise ValueError("azimuth_max must be >= azimuth_min")
        if self.elevation_max < self.elevation_min:
            raise ValueError("elevation_max must be >= elevation_min")
        return self


class AudioStimulus(BaseSchema):
    """Auditory stimulus parameters common to all trials."""

    speaker_locations: List[SphericalPosition] = Field(
        description="Candidate sound positions, one drawn per trial; keep an azimuth-0 entry for trials that place the sound at the centre"
    )
    flicker_frequency: Double = Field(default=8.0, description="Auditory stimulus flicker frequency, in Hz")

    @field_validator("speaker_locations")
    @classmethod
    def _at_least_one_location(cls, value: List[SphericalPosition]) -> List[SphericalPosition]:
        if not value:
            raise ValueError("speaker_locations must have at least one entry")
        return value


class RewardSettings(BaseSchema):
    """Reward delivery parameters; per-stage reward size lives in the training stage table."""

    reward_probability: Double = Field(default=1.0, ge=0, le=1, description="Probability that a correct response is rewarded")
    large_reward_multiplier: Double = Field(default=1.0, ge=0, description="Reward size multiplier applied on large-reward trials")
    large_reward_probability: Double = Field(default=0.0, ge=0, le=1, description="Probability that a trial is a large-reward trial")


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
    """Zapit optogenetic stimulation parameters."""

    enabled: Bool = Field(default=False, description="Whether optogenetic stimulation is enabled")
    zapit_config_path: String = Field(default="", description="Path to the Zapit YAML config listing stimulation conditions")
    stim_probability: Double = Field(default=0.75, ge=0, le=1, description="Probability that a trial receives stimulation")
    stim_delay: Double = Field(default=0.0, ge=0, description="Delay from trial start to stimulation onset, in seconds")
    stim_duration: Double = Field(default=1.5, ge=0, description="Stimulation duration, in seconds")
    exclude_repeats: Bool = Field(default=True, description="Whether repeat trials are excluded from stimulation")
    pseudorandom_selection: Bool = Field(default=True, description="Draw conditions from a shuffled queue rather than independently")


class TrainingStage(BaseSchema):
    """One complete row of the training stage table; stages are selected by index at runtime."""

    label: String = Field(description="Human-readable stage name, for documentation and logs")
    trial_weights: List[Int] = Field(description="Relative draw weight per trial type, indexed by trial type")
    auto_reward_probability: Double = Field(ge=0, le=1, description="Probability that a trial is automatically rewarded")
    both_spouts_probability: Double = Field(ge=0, le=1, description="Probability that both spouts are presented rather than only the correct one")
    reward_size_ul: Double = Field(ge=0, description="Reward size, in microliters; delivered as ceil(size/2) valve pulses")
    contrasts: List[Double] = Field(description="Candidate grating contrasts to draw from on visual trials")
    allow_repeats: Bool = Field(description="Whether failed trials may be repeated in this stage")

    @field_validator("trial_weights")
    @classmethod
    def _one_weight_per_trial_type(cls, value: List[int]) -> List[int]:
        if len(value) != TRIAL_TYPE_COUNT:
            raise ValueError(f"trial_weights must have exactly {TRIAL_TYPE_COUNT} entries, got {len(value)}")
        return value


class UclOpenAvDomeTaskParameters(TaskParameters):
    """Task parameters for the av-dome audiovisual localisation task."""

    trial_structure: TrialStructure = Field(description="Timing parameters of the trial state machine")
    visual_stimulus: VisualStimulus = Field(description="Grating stimulus parameters")
    audio_stimulus: AudioStimulus = Field(description="Auditory stimulus parameters")
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
