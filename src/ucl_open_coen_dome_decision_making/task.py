from enum import StrEnum
from typing import Literal

from open_rigs.components import SphericalPosition
from open_rigs.core.base import Bool, Double, Int, String
from open_rigs.core.task import BackgroundNoise, Optogenetics, Penalty, QuietWait, StimulusSound, Task, TaskBlock, TaskParameters
from open_rigs.components.zapit import ZAPIT_MAX_RAMP_DOWN_SECONDS, ZAPIT_REPLY_MARGIN_SECONDS
from open_rigs.rigs.lab_rig import MAX_REWARD_UL
from pydantic import Field, field_validator, model_validator

from ucl_open_coen_dome_decision_making import __semver__



class TrialStructure(TaskBlock):
    """Timing of each trial, every wait on the Harp clock."""

    iti_min_seconds: Double = Field(
        default=3.4,
        ge=0,
        description="Lower bound of the inter-trial interval, drawn uniformly for every trial, in seconds: from the last outcome (after any timeout; the first trial: the experiment folder's creation) to this trial's stimulus, drinkSeconds included. The stage's quiet waits can only lengthen it (quietWaitSeconds). At least drinkSeconds",
    )
    iti_max_seconds: Double = Field(default=4.4, ge=0, description="Upper bound of the uniformly drawn inter-trial interval, in seconds (itiMinSeconds)")
    drink_seconds: Double = Field(default=2.0, ge=0, description="From the outcome (after any timeout) to drawing the spouts back, in seconds: time to drink a reward, inside the interval")
    open_loop_seconds: Double = Field(default=0.0, ge=0, description="From stimulus onset to the response window, in seconds")
    closed_loop_seconds: Double = Field(default=1.5, gt=0, description="The response window, in seconds")
    stimulus_seconds: Double = Field(
        default=1.5, gt=0, description="How long the grating shows, in seconds from onset; at most openLoopSeconds plus closedLoopSeconds, so it never outlasts the response window"
    )
    stimulus_to_spouts_in_seconds: Double = Field(default=0.5, ge=0, description="From stimulus onset to moving the response spouts in, in seconds")

    @model_validator(mode="after")
    def _ordered(self) -> "TrialStructure":
        if self.iti_max_seconds < self.iti_min_seconds:
            raise ValueError("iti_max_seconds must be >= iti_min_seconds")
        if self.iti_min_seconds < self.drink_seconds:
            raise ValueError("iti_min_seconds must be >= drink_seconds: the spouts go out drink_seconds into the interval, before the stimulus")
        if self.stimulus_seconds > self.open_loop_seconds + self.closed_loop_seconds:
            raise ValueError("stimulus_seconds must not exceed open_loop_seconds + closed_loop_seconds")
        return self


class VisualStimulus(TaskBlock):
    """Grating stimulus parameters common to all trials; contrast is set per training stage."""

    azimuth_min_deg: Double = Field(default=-60.0, ge=-180, le=180, description="Lower bound of the uniformly drawn grating azimuth, in degrees")
    azimuth_max_deg: Double = Field(default=60.0, ge=-180, le=180, description="Upper bound of the uniformly drawn grating azimuth, in degrees")
    fixed_azimuths_deg: list[Double] | None = Field(
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
    def _each_side_has_a_range(self) -> "VisualStimulus":
        # Side-restricted trials draw from one side each; TrialDraw.CheckTask refuses the same at Go,
        # since Bonsai reads the YAML without pydantic.
        if self.azimuth_max_deg < self.azimuth_min_deg:
            raise ValueError("azimuth_max_deg must be >= azimuth_min_deg")
        if self.elevation_max_deg < self.elevation_min_deg:
            raise ValueError("elevation_max_deg must be >= elevation_min_deg")
        values = self.fixed_azimuths_deg
        cap = self.coherent_azimuth_cap_deg
        if not values:
            low, high = self.azimuth_min_deg, self.azimuth_max_deg
            if not max(low, 0) < min(high, cap):
                raise ValueError("azimuth_min_deg to azimuth_max_deg needs a right-side range within coherent_azimuth_cap_deg")
            if not max(low, -cap) < min(high, 0):
                raise ValueError("azimuth_min_deg to azimuth_max_deg needs a left-side range within coherent_azimuth_cap_deg")
            return self
        for value in values:
            if not -180 <= value <= 180:
                raise ValueError(f"fixed_azimuths_deg value {value} is outside [-180, 180]")
        if not any(0 < value <= cap for value in values):
            raise ValueError(f"fixed_azimuths_deg needs a value in (0, {cap}] (coherent_azimuth_cap_deg) for right-side trials")
        if not any(-cap <= value < 0 for value in values):
            raise ValueError(f"fixed_azimuths_deg needs a value in [-{cap}, 0) (coherent_azimuth_cap_deg) for left-side trials")
        return self


class AudioStimulus(StimulusSound):
    """Auditory stimulus parameters common to all trials: every waveform of the set (src/LocalFiles/AuditoryStimuli)
    plays at levelDbfs, the lab's block (open_rigs.core.task StimulusSound)."""

    speaker_locations: list[SphericalPosition] = Field(
        description="Candidate lateral sound positions, one drawn per audio trial, at least one on each side; no azimuth-0 entry: visual-only and neutral trials play from the centre. Each plays on rig.yml's nearest speaker"
    )

    @field_validator("speaker_locations")
    @classmethod
    def _both_sides(cls, value: list[SphericalPosition]) -> list[SphericalPosition]:
        if any(location.azimuth == 0 for location in value):
            raise ValueError("speaker_locations must not have an azimuth-0 entry; the centre sound is fixed in code")
        if not any(location.azimuth < 0 for location in value) or not any(location.azimuth > 0 for location in value):
            raise ValueError("speaker_locations needs at least one location on each side")
        return value


class RewardSettings(TaskBlock):
    """Reward delivery parameters; per-stage reward size lives in the training stage table."""

    large_reward_multiplier: Double = Field(default=1.0, ge=0, description="Reward size multiplier applied on large-reward trials")
    large_reward_probability: Double = Field(default=0.0, ge=0, le=1, description="Probability that a trial is a large-reward trial")


class ResponseSettings(TaskBlock):
    """Lick-accumulation response rule; thresholdLicks 1 with degreesPerLick 0 is classic first-lick 2AFC."""

    threshold_licks: Int = Field(default=1, ge=1, description="Net lick difference (left minus right, absolute) that terminates the response window")
    degrees_per_lick: Double = Field(default=0.0, description="Degrees the grating azimuth moves per net lick difference; 0 keeps the stimulus fixed")


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


class TrialTypeWeights(TaskBlock):
    """Relative draw weight of each trial type, by the name the trial log gives it (what the trial showed)."""

    visual: Int = Field(ge=0, description="The grating alone")
    auditory: Int = Field(ge=0, description="The sound alone")
    audiovisual_coherent: Int = Field(ge=0, description="Grating and sound on the same side")
    audiovisual_conflict: Int = Field(ge=0, description="Grating and sound on opposite sides")
    neutral: Int = Field(ge=0, description="Grey screen, centre sound in localization, silent in discrimination")

    @model_validator(mode="after")
    def _some_weight(self) -> "TrialTypeWeights":
        if not any(self.model_dump().values()):
            raise ValueError("trial_weights needs a trial type with a weight above 0")
        return self


class TrialTypeRules(TaskBlock):
    """The repeat rule of each trial type, by the name the trial log gives it."""

    visual: RepeatRule = Field(default=RepeatRule.ON_FAILURE, description="The grating alone")
    auditory: RepeatRule = Field(default=RepeatRule.ON_FAILURE, description="The sound alone")
    audiovisual_coherent: RepeatRule = Field(default=RepeatRule.ON_FAILURE, description="Grating and sound on the same side")
    audiovisual_conflict: RepeatRule = Field(default=RepeatRule.ON_TIMEOUT, description="Grating and sound on opposite sides")
    neutral: RepeatRule = Field(default=RepeatRule.ON_TIMEOUT, description="Grey screen")


class RepeatSettings(TaskBlock):
    """Trial repetition policy."""

    max_repeats: Int = Field(default=5, ge=0, description="Maximum number of consecutive repeats of the same trial")
    rules_per_type: TrialTypeRules = Field(default=TrialTypeRules(), description="Repeat rule per trial type")


class OptogeneticsSettings(Optogenetics):
    """Zapit optogenetic stimulation: the lab's block (open_rigs.core.task Optogenetics) and this task's laser timing;
    laserDurationSeconds is a timed stimulus here, where coen-dome-navigation's laserMaxSeconds is a cap."""

    laser_duration_seconds: Double = Field(
        default=1.5,
        gt=0,
        description="How long the laser and the masking light stay on, in seconds from the trigger (a timed stimulus). With optogenetics on, at most openLoopSeconds + itiMinSeconds - 0.7 s: Zapit is armed for the next trial only once this stimulus and its ramp-down (0.5 s at most) have ended, and its reply must be in 0.2 s before the next trigger",
    )
    no_laser_on_repeats: Bool = Field(default=True, description="Whether repeat trials never have the laser on")


class TrainingStage(QuietWait):
    """One row of the training stage table, chosen by the TrainingStage parameter; its quiet waits the lab's (open_rigs.core.task QuietWait)."""

    label: String = Field(description="Human-readable stage name, for documentation and logs")
    trial_weights: TrialTypeWeights = Field(
        description="Relative draw weight per trial type: each new trial draws its type at random with these as relative chances, independently of every other trial (repeats copy the trial they repeat)"
    )
    auto_reward_probability: Double = Field(ge=0, le=1, description="Probability that a trial is an auto-reward trial: a miss on it is rewarded at the target spout, and still counts as a timeout for repeats")
    both_spouts_probability: Double = Field(ge=0, le=1, description="Probability that both spouts are presented rather than only the correct one")
    reward_size_ul: Double = Field(
        ge=0, le=MAX_REWARD_UL, description=f"Reward size, in microlitres; the valve's calibration plans the pulses. At most {MAX_REWARD_UL}, the most one reward may ask for, with largeRewardMultiplier applied"
    )
    contrasts: list[Double] = Field(min_length=1, description="Candidate grating contrasts, each above 0, one drawn per trial that shows the grating")
    allow_repeats: Bool = Field(description="Whether failed trials may be repeated in this stage")
    spout_movements: Bool = Field(
        default=True,
        description="Whether the spouts move during trials; false keeps them in (habituation): no withdrawal and no single-spout move for a reward",
    )
    @field_validator("contrasts")
    @classmethod
    def _positive(cls, value: list[float]) -> list[float]:
        if any(contrast <= 0 for contrast in value):
            raise ValueError("contrasts must each be above 0")
        return value


class UclOpenCoenDomeDecisionMakingTaskParameters(TaskParameters):
    """Task parameters for the coen-dome-decision-making audiovisual tasks: localization and discrimination."""

    task_type: TaskType = Field(
        default=TaskType.LOCALIZATION,
        description="localization: the answer is the side the stimulus came from. discrimination: the answer is the modality, the left spout for targetModality and the right spout for the other",
    )
    target_modality: Modality = Field(default=Modality.VISUAL, description="Discrimination only: the modality answered at the left spout")
    trial_structure: TrialStructure = Field(description="Timing of each trial")
    visual_stimulus: VisualStimulus = Field(description="Grating stimulus parameters")
    audio_stimulus: AudioStimulus = Field(description="Auditory stimulus parameters")
    response: ResponseSettings = Field(description="Lick-accumulation response rule")
    background_noise: BackgroundNoise = Field(
        default=BackgroundNoise(), description="The background white noise (src/LocalFiles/background_white_noise.wav) on every speaker, from Ready at the level of the task file as launched (open_rigs.core.task BackgroundNoise)"
    )
    penalty: Penalty = Field(
        description="The timeout and penalty sound after an incorrect response or a miss (a miss on an auto-reward trial is rewarded instead), open_rigs.core.task Penalty"
    )
    reward: RewardSettings = Field(description="Reward delivery parameters")
    repeats: RepeatSettings = Field(description="Trial repetition policy")
    optogenetics: OptogeneticsSettings = Field(description="Zapit optogenetic stimulation parameters")
    training_stages: list[TrainingStage] = Field(min_length=1, description="Training stage table indexed by the TrainingStage parameter; a stage past the end stops the workflow at Go")

    @model_validator(mode="after")
    def _timing_fits(self) -> "UclOpenCoenDomeDecisionMakingTaskParameters":
        # TrialDraw.CheckTask's same rules (Pip, 13 Nov 2026).
        opto, s = self.optogenetics, self.trial_structure
        opto.check_lead_fits(s.iti_min_seconds)
        lead = opto.laser_to_stimulus_seconds
        if -lead > s.open_loop_seconds + s.closed_loop_seconds + 1e-9:
            raise ValueError(
                f"optogenetics.laser_to_stimulus_seconds ({lead} s) puts the trigger after the response window "
                f"(open_loop_seconds + closed_loop_seconds, {s.open_loop_seconds + s.closed_loop_seconds:g} s)"
            )
        # Trigger to the next trigger is at least open_loop + iti_min (a response at once, no timeout, a quiet mouse); the
        # next arm waits for this laser and its ramp-down, and its reply needs the margin before that next trigger.
        longest = s.open_loop_seconds + s.iti_min_seconds - ZAPIT_MAX_RAMP_DOWN_SECONDS - ZAPIT_REPLY_MARGIN_SECONDS
        if opto.use_zapit and opto.laser_duration_seconds > longest + 1e-9:
            raise ValueError(
                f"optogenetics.laser_duration_seconds ({opto.laser_duration_seconds} s) must be at most {longest:g} s (open_loop_seconds + "
                f"iti_min_seconds - {ZAPIT_MAX_RAMP_DOWN_SECONDS} s ramp-down - {ZAPIT_REPLY_MARGIN_SECONDS} s reply margin), so Zapit can be armed for the next trial in time"
            )
        return self

    @model_validator(mode="after")
    def _large_reward_fits(self) -> "UclOpenCoenDomeDecisionMakingTaskParameters":
        # DeliverReward refuses a reward over MaxRewardUl when it plans it, mid-session; refuse it here instead.
        multiplier = self.reward.large_reward_multiplier
        if self.reward.large_reward_probability > 0 and multiplier > 1:
            for index, stage in enumerate(self.training_stages):
                if stage.reward_size_ul * multiplier > MAX_REWARD_UL + 1e-9:
                    raise ValueError(
                        f"training_stages[{index}] ({stage.label}): reward_size_ul ({stage.reward_size_ul} ul) x reward.large_reward_multiplier "
                        f"({multiplier}) is more than {MAX_REWARD_UL} ul, the most one reward may ask for"
                    )
        return self


class UclOpenCoenDomeDecisionMakingTaskLogic(Task):
    """Task logic definition for the coen-dome-decision-making experiment."""

    version: Literal[__semver__] = __semver__
    name: Literal["UclOpenCoenDomeDecisionMaking"] = Field(default="UclOpenCoenDomeDecisionMaking", description="Name of the task logic", frozen=True)
    task_parameters: UclOpenCoenDomeDecisionMakingTaskParameters = Field(description="Parameters of the task logic")
