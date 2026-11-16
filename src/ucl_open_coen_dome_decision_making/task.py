from enum import StrEnum
from typing import Literal

from open_rigs.components import SphericalPosition
# Integers are int: open_rigs' Int carries its own range, which drops a Field's ge or gt (round 3 F17).
from open_rigs.core.base import Bool, Double, String
from open_rigs.core.task import BackgroundNoise, Optogenetics, Penalty, QuietWait, StimulusSound, Task, TaskBlock, TaskParameters
from open_rigs.components.zapit import ZAPIT_MAX_RAMP_DOWN_SECONDS
from open_rigs.rigs.lab_rig import MAX_REWARD_UL
from pydantic import ConfigDict, Field, field_validator, model_validator

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
    response_delay_seconds: Double = Field(default=0.0, ge=0, description="The response delay: from stimulus onset to the response window opening, in seconds; licks before it are not counted")
    response_window_seconds: Double = Field(default=1.5, gt=0, description="The response window: how long it stays open, from responseDelaySeconds after stimulus onset, in seconds; with no choice by its end the trial is NoResponse")
    stimulus_seconds: Double = Field(
        default=1.5, gt=0, description="How long the grating shows, in seconds from onset; at most responseDelaySeconds plus responseWindowSeconds, so it never outlasts the response window, and at most itiMinSeconds, so it is off before the next stimulus"
    )
    stimulus_to_spouts_in_seconds: Double = Field(default=0.5, ge=0, description="From stimulus onset to moving the response spouts in, in seconds")

    @model_validator(mode="after")
    def _ordered(self) -> "TrialStructure":
        if self.iti_max_seconds < self.iti_min_seconds:
            raise ValueError("iti_max_seconds must be >= iti_min_seconds")
        if self.iti_min_seconds < self.drink_seconds:
            raise ValueError("iti_min_seconds must be >= drink_seconds: the spouts are drawn back drink_seconds into the interval, before the stimulus")
        if self.stimulus_seconds > self.response_delay_seconds + self.response_window_seconds:
            raise ValueError("stimulus_seconds must not exceed response_delay_seconds + response_window_seconds")
        if self.stimulus_seconds > self.iti_min_seconds:
            raise ValueError("stimulus_seconds must not exceed iti_min_seconds: the grating would still show when the next trial's stimulus starts")
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

    threshold_licks: int = Field(default=1, ge=1, description="Net lick difference (left minus right, absolute) that ends the response window with a choice; in discriminationGoNoGo, licks at the left spout (the right is not counted)")
    degrees_per_lick: Double = Field(default=0.0, description="Degrees the grating azimuth moves per net lick difference; 0 keeps the stimulus fixed")


class TaskType(StrEnum):
    """What the mouse answers."""

    LOCALIZATION = "localization"
    DISCRIMINATION_GO_NO_GO = "discriminationGoNoGo"
    DISCRIMINATION_LEFT_RIGHT = "discriminationLeftRight"


class Modality(StrEnum):
    """A stimulus modality."""

    VISUAL = "visual"
    AUDITORY = "auditory"


class RepeatRule(StrEnum):
    """When a trial type is repeated: never, onFailure (after Incorrect or NoResponse) or onNoResponse (after NoResponse only)."""

    NEVER = "never"
    ON_FAILURE = "onFailure"
    ON_NO_RESPONSE = "onNoResponse"


# The keys of trialWeights and rulesPerType for each task type (TrialDraw.Keys): localization's name what a trial shows;
# the discriminations' name its role, the mouse's goModality or leftModality choosing which modality fills it (Pip, 16 Nov 2026).
TRIAL_KEYS = {
    TaskType.LOCALIZATION: ("visual", "auditory", "audiovisualCoherent", "audiovisualConflict", "neutral"),
    TaskType.DISCRIMINATION_GO_NO_GO: ("go", "noGo", "audiovisualCoherent", "audiovisualConflict", "neutral"),
    TaskType.DISCRIMINATION_LEFT_RIGHT: ("left", "right", "audiovisualCoherent", "audiovisualConflict", "neutral"),
}
# rulesPerType's keys: only a trial with a right answer repeats (Pip, 16 Nov 2026).
RULE_KEYS = {
    TaskType.LOCALIZATION: ("visual", "auditory", "audiovisualCoherent"),
    TaskType.DISCRIMINATION_GO_NO_GO: ("go", "noGo"),
    TaskType.DISCRIMINATION_LEFT_RIGHT: ("left", "right"),
}


class TrialTypeWeights(TaskBlock):
    """Relative draw weight of each kind of trial: exactly the task type's five keys (TRIAL_KEYS), the others left out.
    localization: by what the trial shows. discriminationGoNoGo: go, noGo, audiovisualCoherent, audiovisualConflict, neutral.
    discriminationLeftRight: left, right, audiovisualCoherent, audiovisualConflict, neutral."""

    model_config = ConfigDict(extra="forbid")

    visual: int | None = Field(default=None, ge=0, description="localization only: the grating alone")
    auditory: int | None = Field(default=None, ge=0, description="localization only: the sound alone")
    audiovisual_coherent: int | None = Field(default=None, ge=0, description="Every task type: grating and sound on the same side")
    audiovisual_conflict: int | None = Field(default=None, ge=0, description="Every task type: grating and sound on opposite sides")
    go: int | None = Field(default=None, ge=0, description="discriminationGoNoGo only: goModality alone (Go)")
    no_go: int | None = Field(default=None, ge=0, description="discriminationGoNoGo only: the other modality alone (NoGo)")
    left: int | None = Field(default=None, ge=0, description="discriminationLeftRight only: leftModality alone (answered at the left spout)")
    right: int | None = Field(default=None, ge=0, description="discriminationLeftRight only: the other modality alone (answered at the right spout)")
    neutral: int | None = Field(default=None, ge=0, description="Every task type: grey screen, centre sound in localization, silent in the discriminations")

    @model_validator(mode="after")
    def _some_weight(self) -> "TrialTypeWeights":
        if not any(self.model_dump(exclude_none=True).values()):
            raise ValueError("trial_weights needs a trial type with a weight above 0")
        return self


class TrialTypeRules(TaskBlock):
    """The repeat rule of each kind of trial with a right answer, by trialWeights' keys: exactly the task type's (RULE_KEYS),
    the others left out. The rest never repeat: localization's audiovisualConflict and neutral, every two-modality and neutral
    trial in the discriminations (go/no-go's no-reward probes; left-right's random-reward probes). A key it lacks is refused, as Bonsai's
    reader refuses it at Go."""

    model_config = ConfigDict(extra="forbid")

    visual: RepeatRule | None = Field(default=None, description="localization only: the grating alone")
    auditory: RepeatRule | None = Field(default=None, description="localization only: the sound alone")
    audiovisual_coherent: RepeatRule | None = Field(default=None, description="localization only: grating and sound on the same side")
    go: RepeatRule | None = Field(default=None, description="discriminationGoNoGo only: goModality alone (Go)")
    no_go: RepeatRule | None = Field(default=None, description="discriminationGoNoGo only: the other modality alone (NoGo)")
    left: RepeatRule | None = Field(default=None, description="discriminationLeftRight only: leftModality alone")
    right: RepeatRule | None = Field(default=None, description="discriminationLeftRight only: the other modality alone")


class RepeatSettings(TaskBlock):
    """Trial repetition policy."""

    max_repeats: int = Field(default=5, ge=0, description="Maximum number of consecutive repeats of the same trial")
    rules_per_type: TrialTypeRules = Field(
        description="Repeat rule per kind of trial with a right answer: localization visual, auditory, audiovisualCoherent; discriminationGoNoGo go, noGo; discriminationLeftRight left, right. The others never repeat"
    )


class OptogeneticsSettings(Optogenetics):
    """Zapit optogenetic stimulation: the lab's block (open_rigs.core.task Optogenetics) and this task's laser timing;
    laserDurationSeconds is a timed stimulus here, where coen-dome-navigation's laserMaxSeconds is a cap."""

    laser_duration_seconds: Double = Field(
        default=1.5,
        gt=0,
        description="How long the laser and the masking light stay on, in seconds from the trigger (a timed stimulus). With optogenetics on, at most responseDelaySeconds + itiMinSeconds - 0.7 s: Zapit is armed for the next trial only once this stimulus and its ramp-down (0.5 s at most) have ended, and its reply must be in 0.2 s before the next trigger",
    )
    no_laser_on_repeats: Bool = Field(default=True, description="Whether repeat trials never have the laser on")


class TrainingStage(QuietWait):
    """One row of the training stage table, chosen by the TrainingStage parameter; its quiet waits the lab's (open_rigs.core.task QuietWait)."""

    label: String = Field(description="Human-readable stage name, for documentation and logs")
    trial_weights: TrialTypeWeights | None = Field(
        default=None,
        description="This stage's own trial mix, keyed as the task's trialWeights, replacing it for this stage; null or left out: the task's trialWeights",
    )
    auto_reward_probability: Double = Field(ge=0, le=1, description="Probability that a trial is an auto-reward trial: a miss on it is rewarded at the target spout, and is still a NoResponse for repeats")
    both_spouts_probability: Double = Field(ge=0, le=1, description="Probability that both spouts are presented rather than only the correct one")
    reward_size_ul: Double = Field(
        gt=0, le=MAX_REWARD_UL, description=f"Reward size, in microlitres; the valve's calibration plans the pulses. At most {MAX_REWARD_UL}, the most one reward may ask for, with largeRewardMultiplier applied"
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
    """Task parameters for the coen-dome-decision-making audiovisual tasks: localization and two modality discriminations."""

    task_type: TaskType = Field(
        description=(
            "localization: the answer is the side the stimulus came from, at that side's spout. "
            "discriminationGoNoGo: one spout (the left); lick on a goModality trial (Go), withhold on the other modality's (NoGo); "
            "a trial showing both modalities, or a neutral trial, is a probe, never rewarded or penalised. "
            "discriminationLeftRight: the left spout for leftModality, the right spout for the other; "
            "a trial showing both modalities, or a neutral trial, is a random-reward probe. "
            "In localization (audiovisualConflict, neutral) and discriminationLeftRight a random-reward probe moves both spouts in, and a coin "
            "drawn with the trial from the seeded stream decides a choice at either: heads rewarded at that spout, tails penalised "
            "as an incorrect choice; a miss as a scored trial's miss"
        ),
    )
    go_modality: Modality = Field(default=Modality.VISUAL, description="discriminationGoNoGo only: the modality to lick for, shown on the stages' go trials; the other modality's (noGo) trials are NoGo")
    left_modality: Modality = Field(
        default=Modality.VISUAL, description="discriminationLeftRight only: the modality answered at the left spout, shown on the stages' left trials; the other (right) is answered at the right"
    )
    trial_weights: TrialTypeWeights = Field(
        description=(
            "Relative draw weight per kind of trial, per mouse, on every stage that sets none of its own: each new trial draws one at random with these as "
            "relative chances, independently of every other trial (repeats copy the trial they repeat). localization by what the trial shows (visual, auditory, "
            "audiovisualCoherent, audiovisualConflict, neutral); discriminationGoNoGo by role (go, noGo, audiovisualCoherent, audiovisualConflict, neutral), discriminationLeftRight "
            "(left, right, audiovisualCoherent, audiovisualConflict, neutral), goModality or leftModality choosing the modality of go or left, the other modality's noGo or right"
        )
    )
    trial_structure: TrialStructure = Field(description="Timing of each trial")
    visual_stimulus: VisualStimulus = Field(description="Grating stimulus parameters")
    audio_stimulus: AudioStimulus = Field(description="Auditory stimulus parameters")
    response: ResponseSettings = Field(description="Lick-accumulation response rule")
    background_noise: BackgroundNoise = Field(
        default=BackgroundNoise(), description="The lab's background white noise (rig.yml's speakerArray.backgroundNoise) on every speaker, from Ready at the level of the task file as launched (open_rigs.core.task BackgroundNoise)"
    )
    penalty: Penalty = Field(
        description="The timeout and penalty sound after an incorrect response or a miss (a miss on an auto-reward trial is rewarded instead), and on a random-reward probe after a choice on the coin's tails or a miss; in discriminationGoNoGo only after a lick on a NoGo trial (a Go miss and a no-reward probe have neither), open_rigs.core.task Penalty"
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
        if -lead > s.response_delay_seconds + s.response_window_seconds + 1e-9:
            raise ValueError(
                f"optogenetics.laser_to_stimulus_seconds ({lead} s) puts the trigger after the response window "
                f"(response_delay_seconds + response_window_seconds, {s.response_delay_seconds + s.response_window_seconds:g} s)"
            )
        opto.check_zapit_free_in_time(
            s.iti_min_seconds,
            laser_duration_seconds=opto.laser_duration_seconds,
            ramp_down_seconds=ZAPIT_MAX_RAMP_DOWN_SECONDS,
            response_delay_seconds=s.response_delay_seconds,
        )
        return self

    @model_validator(mode="after")
    def _keys_of_the_task_type(self) -> "UclOpenCoenDomeDecisionMakingTaskParameters":
        # TrialDraw.CheckTask's same rules: each block names exactly its task type's keys, and a go/no-go stage has Go trials.
        weights, rules = TRIAL_KEYS[self.task_type], RULE_KEYS[self.task_type]
        blocks = [("trial_weights", self.trial_weights, weights), ("repeats.rules_per_type", self.repeats.rules_per_type, rules)]
        blocks += [(f"training_stages[{i}] ({s.label}).trial_weights", s.trial_weights, weights) for i, s in enumerate(self.training_stages) if s.trial_weights is not None]
        for where, block, keys in blocks:
            given = set(block.model_dump(by_alias=True, exclude_none=True))
            if given != set(keys):
                raise ValueError(f"{where} must give exactly {', '.join(keys)} in {self.task_type.value} (it gives {', '.join(sorted(given)) or 'none'})")
        if self.task_type == TaskType.DISCRIMINATION_GO_NO_GO:
            for index, stage in enumerate(self.training_stages):
                if self.weights_of(stage).go == 0:
                    raise ValueError(f"training_stages[{index}] ({stage.label}): its trial weights' go is 0, so the stage has no Go trials")
        return self

    def weights_of(self, stage: TrainingStage) -> TrialTypeWeights:
        """The stage's trial mix: its own trial_weights, else the task's (TrialDraw.Weights)."""
        return self.trial_weights if stage.trial_weights is None else stage.trial_weights

    @model_validator(mode="after")
    def _one_spout_in_go_no_go(self) -> "UclOpenCoenDomeDecisionMakingTaskParameters":
        # TrialDraw.CheckTask's same rule: go/no-go uses the left spout alone.
        if self.task_type == TaskType.DISCRIMINATION_GO_NO_GO:
            for index, stage in enumerate(self.training_stages):
                if stage.both_spouts_probability != 0:
                    raise ValueError(f"training_stages[{index}] ({stage.label}): both_spouts_probability must be 0 in discriminationGoNoGo: only the left spout moves in")
        else:
            # The SpoutsIn cue's LeftIn or RightIn draws the other spout back, which a stage without spout movements never returns.
            for index, stage in enumerate(self.training_stages):
                if not stage.spout_movements and stage.both_spouts_probability != 1:
                    raise ValueError(f"training_stages[{index}] ({stage.label}): both_spouts_probability must be 1 with spout_movements false: a one-spout trial draws the other spout back")
        return self

    @model_validator(mode="after")
    def _large_reward_fits(self) -> "UclOpenCoenDomeDecisionMakingTaskParameters":
        # A RewardValve piece refuses a reward over MaxRewardUl when it plans it, mid-session; refuse it here instead.
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
