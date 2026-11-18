import copy
from enum import StrEnum
from typing import Any, Literal

# Integers are int: open_rigs' Int carries its own range, which drops a Field's ge or gt.
from open_rigs.core.base import Bool, Double, String
from open_rigs.core.task import (
    MAX_REWARD_UL,
    AutoReward,
    BackgroundNoise,
    Iti,
    LickQuietWait,
    Optogenetics,
    Penalty,
    ResponseWindow,
    RewardSize,
    StimulusSound,
    Task,
    TaskBlock,
    TaskParameters,
    WheelQuietWait,
)
from open_rigs.components.zapit import ZAPIT_MAX_RAMP_DOWN_SECONDS
from pydantic import ConfigDict, Field, ValidationError, ValidationInfo, field_validator, model_validator

from ucl_open_coen_dome_decision_making import __semver__



class TrialStructure(Iti, ResponseWindow, LickQuietWait, WheelQuietWait):
    """Timing of each trial, every wait on the Harp clock. The interval (the lab's Iti block) runs from the last outcome
    (after any timeout; the first trial: the experiment folder's creation) to this trial's stimulus, drinkSeconds included,
    so itiMinSeconds is at least drinkSeconds; the quiet waits (the lab's LickQuietWait and WheelQuietWait blocks) can
    only lengthen it. A lick first counts responseDelaySeconds after stimulus onset, where the
    response window (the lab's ResponseWindow block) starts; responseWindowSeconds must be given: no limit is refused."""

    drink_seconds: Double = Field(default=2.0, ge=0, description="From the outcome (after any timeout) to drawing the spouts back, in seconds: time to drink a reward, inside the interval")
    response_delay_seconds: Double = Field(default=0.0, ge=0, description="The response delay: from stimulus onset to the response window opening, in seconds; licks before it are not counted")
    stimulus_seconds: Double = Field(
        default=1.5, gt=0, description="How long the grating shows, in seconds from onset; at most responseDelaySeconds plus responseWindowSeconds, so it never outlasts the response window, and at most itiMinSeconds, so it is off before the next stimulus"
    )
    stimulus_to_spouts_in_seconds: Double = Field(default=0.5, ge=0, description="From stimulus onset to moving the response spouts in, in seconds")

    @model_validator(mode="after")
    def _ordered(self) -> "TrialStructure":
        if self.response_window_seconds is None:
            raise ValueError("response_window_seconds must be given: this task's response window always closes")
        if self.iti_min_seconds < self.drink_seconds:
            raise ValueError("iti_min_seconds must be >= drink_seconds: the spouts are drawn back drink_seconds into the interval, before the stimulus")
        if self.stimulus_seconds > self.response_delay_seconds + self.response_window_seconds:
            raise ValueError("stimulus_seconds must not exceed response_delay_seconds + response_window_seconds")
        if self.stimulus_seconds > self.iti_min_seconds:
            raise ValueError("stimulus_seconds must not exceed iti_min_seconds: the grating would still show when the next trial's stimulus starts")
        return self


class VisualStimulus(TaskBlock):
    """Grating stimulus parameters and the stimulus positions; the contrasts are taskParameters' own."""

    start_azimuths_deg: list[Double] = Field(
        default=[-60.0, 60.0],
        min_length=1,
        description=(
            "Signed azimuths a trial's stimulus starts at, in degrees, one picked per trial with a stimulus: positive is right, negative left, "
            "never 0. The grating starts there exactly and the sound plays on rig.yml's speaker nearest it (an audiovisualConflict trial's sound "
            "at the mirrored azimuth); the picked azimuth's sign is the trial's side"
        ),
    )
    elevation_deg: Double = Field(default=15.0, ge=-90, le=90, description="Elevation of the grating and of the sound's position, in degrees")
    gaussian_window_sigma_deg: Double = Field(default=20.0, description="Sigma of the Gaussian aperture window, in degrees")
    orientation_deg: Double = Field(default=0.0, description="Grating stripe orientation, in degrees; drift direction is orthogonal to it (orientationDeg + 90 degrees)")
    spatial_frequency_cpd: Double = Field(default=0.066666667, description="Grating spatial frequency, in cycles per degree")
    temporal_frequency_hz: Double = Field(default=0.0, ge=0, description="Unsigned grating drift rate, in Hz, drifting orthogonal to orientationDeg (orientationDeg + 90 degrees); 0 is static")

    @field_validator("start_azimuths_deg")
    @classmethod
    def _signed(cls, value: list[float]) -> list[float]:
        # TrialDraw.CheckTask's same rule, with each azimuth's nearest speaker on its side.
        for azimuth in value:
            if not -180 <= azimuth <= 180:
                raise ValueError(f"start_azimuths_deg value {azimuth} is outside [-180, 180]")
            if azimuth == 0:
                raise ValueError("start_azimuths_deg must not hold 0: the sign is the trial's side")
        return value


class AuditoryStimulus(StimulusSound):
    """Auditory stimulus parameters common to all trials: every waveform of the set (src/sounds)
    plays at levelDbfs, the lab's block (open_rigs.core.task StimulusSound). Where it plays is visualStimulus'
    startAzimuthsDeg and elevationDeg, on rig.yml's nearest speaker."""


class RewardSettings(RewardSize):
    """Reward delivery: the reward size (the lab's RewardSize block) and the large-reward trials."""

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


# The trial condition types and rulesPerType keys for each task type (TrialDraw.Keys): localization's name what a trial shows;
# the discriminations' name its role, the mouse's goModality or leftModality choosing which modality fills it.
TRIAL_KEYS = {
    TaskType.LOCALIZATION: ("visual", "auditory", "audiovisualCoherent", "audiovisualConflict", "neutral"),
    TaskType.DISCRIMINATION_GO_NO_GO: ("go", "noGo", "audiovisualCoherent", "audiovisualConflict", "neutral"),
    TaskType.DISCRIMINATION_LEFT_RIGHT: ("left", "right", "audiovisualCoherent", "audiovisualConflict", "neutral"),
}
# rulesPerType's keys: only a trial with a right answer repeats.
RULE_KEYS = {
    TaskType.LOCALIZATION: ("visual", "auditory", "audiovisualCoherent"),
    TaskType.DISCRIMINATION_GO_NO_GO: ("go", "noGo"),
    TaskType.DISCRIMINATION_LEFT_RIGHT: ("left", "right"),
}


# The fields a pipeline may make condition-level (its conditionFields), each with the trial types it applies to (a
# condition of another type gives it no value): only what TrialDraw.New() alone reads per trial, never what is fixed at
# Go. The schema's x-conditionFields; TrialDraw.ConditionFields is the same (a test holds both).
_ALL = ("visual", "auditory", "audiovisualCoherent", "audiovisualConflict", "neutral", "go", "noGo", "left", "right")
_STIMULUS = tuple(key for key in _ALL if key != "neutral")
_GRATING = tuple(key for key in _STIMULUS if key != "auditory")
CONDITION_FIELDS = {
    "contrasts": _GRATING,
    "visualStimulus.startAzimuthsDeg": _STIMULUS,
    "visualStimulus.elevationDeg": _STIMULUS,
    "optogenetics.laserOnProbability": _ALL,
    "reward.rewardSizeUl": _ALL,
    "autoRewardProbability": _ALL,
    "bothSpoutsProbability": _ALL,
}
# The most combinations of list-valued scalar cells one condition may expand into (TrialDraw.MaxCombinations).
MAX_COMBINATIONS = 64


class TrialCondition(TaskBlock):
    """One kind of trial in the mix: its type, its relative weight and its condition-level values (set), by dotted path.
    A list in a scalar field's cell is one value picked at random per trial; a list field (contrasts,
    visualStimulus.startAzimuthsDeg) takes the list as is, one entry picked per trial as at task level."""

    model_config = ConfigDict(extra="forbid")

    type: String = Field(description="The condition's type: one of the task type's keys (localization visual, auditory, audiovisualCoherent, audiovisualConflict, neutral; the discriminations by role, go/noGo or left/right, and the last three). Its trials' trialType is what they show: the type itself, or for a role the modality goModality or leftModality gives it")
    weight: int = Field(ge=0, le=1_000_000, description="Relative draw weight: each new trial picks a condition with these as relative chances")
    set: dict[str, Any] = Field(default_factory=dict, description="Condition-level values by dotted path (x-conditionFields' paths that apply to the type); the rest are the task's")


def _put(data: dict, dotted, value: Any) -> None:
    *parents, key = dotted.split(".")
    for part in parents:
        data = data[part]
    if key not in data:
        raise KeyError(dotted)
    data[key] = value


def _get(data: dict, dotted) -> Any:
    for part in dotted.split("."):
        data = data[part]
    return data


def condition_combinations(base: dict, values: dict[str, Any]) -> list[dict[str, Any]]:
    """Every combination of a condition's set: a list in a cell whose task-level value is not a list is one pick each."""
    combos: list[dict[str, Any]] = [{}]
    for path, value in values.items():
        options = value if isinstance(value, list) and not isinstance(_get(base, path), list) else [value]
        if not options:
            raise ValueError(f"{path} is an empty list")
        combos = [{**combo, path: option} for combo in combos for option in options]
    return combos


class TrialTypeRules(TaskBlock):
    """The repeat rule of each kind of trial with a right answer, by trialConditions' types: exactly the task type's (RULE_KEYS),
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


class TrainingPipeline(TaskBlock):
    """The training pipeline stage these values are: app-rig-control fills it at launch from the share's pipeline file
    (the share's config/training-pipelines), its stage's values written over the task file's; empty in the repo's task files."""

    id: int = Field(ge=0, description="The stage's id in the pipeline: the TrainingStage parameter must equal it, or the workflow stops at Go")
    label: String = Field(description="The stage's name in the pipeline")
    sha256: String = Field(description="SHA-256 of the pipeline file's text the stage was read from")


class UclOpenCoenDomeDecisionMakingTaskParameters(TaskParameters, AutoReward):
    """Task parameters for the coen-dome-decision-making audiovisual tasks: localization and two modality discriminations."""

    model_config = ConfigDict(json_schema_extra={"x-conditionFields": {path: list(types) for path, types in CONDITION_FIELDS.items()}})

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
    go_modality: Modality = Field(default=Modality.VISUAL, description="discriminationGoNoGo only: the modality to lick for, shown on go trials; the other modality's (noGo) trials are NoGo")
    left_modality: Modality = Field(
        default=Modality.VISUAL, description="discriminationLeftRight only: the modality answered at the left spout, shown on left trials; the other (right) is answered at the right"
    )
    trial_conditions: list[TrialCondition] = Field(
        min_length=1,
        description=(
            "The trial mix: each new trial picks one condition at random with the weights as relative chances, independently of every other "
            "trial (repeats copy the trial they repeat), then draws as the task's values with the condition's set over them. Types: "
            "localization by what the trial shows (visual, auditory, audiovisualCoherent, audiovisualConflict, neutral); discriminationGoNoGo "
            "by role (go, noGo, audiovisualCoherent, audiovisualConflict, neutral), discriminationLeftRight (left, right, audiovisualCoherent, "
            "audiovisualConflict, neutral), goModality or leftModality choosing the modality of go or left. One whole value: a pipeline "
            "stage's conditions replace it; the trialCondition column is its 0-based index"
        ),
        json_schema_extra={"x-whole": True},
    )
    trial_structure: TrialStructure = Field(description="Timing of each trial")
    visual_stimulus: VisualStimulus = Field(description="Grating stimulus parameters")
    auditory_stimulus: AuditoryStimulus = Field(description="Auditory stimulus parameters")
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
    auto_reward_probability: Double = Field(
        default=0.0,
        ge=0,
        le=1,
        description="Probability that a trial is an auto-reward trial (the lab's AutoReward block): its miss is rewarded at the target spout, and is still a NoResponse for repeats; 0: never, 1: every trial",
    )
    both_spouts_probability: Double = Field(default=0.0, ge=0, le=1, description="Probability that both spouts are presented rather than only the correct one")
    contrasts: list[Double] = Field(default=[1.0], min_length=1, description="Candidate grating contrasts, each above 0, one drawn per trial that shows the grating")
    allow_repeats: Bool = Field(default=False, description="Whether failed trials may be repeated (repeats.rulesPerType)")
    spout_movements: Bool = Field(
        default=True,
        description="Whether the spouts move during trials; false keeps them in (habituation): no withdrawal and no single-spout move for a reward",
    )
    training_pipeline: TrainingPipeline | None = Field(
        default=None,
        description="The training pipeline stage these values are, filled by app-rig-control at launch; empty or its id not the TrainingStage parameter stops the workflow at Go",
    )

    @field_validator("contrasts")
    @classmethod
    def _positive(cls, value: list[float]) -> list[float]:
        if any(contrast <= 0 for contrast in value):
            raise ValueError("contrasts must each be above 0")
        return value

    @model_validator(mode="after")
    def _timing_fits(self) -> "UclOpenCoenDomeDecisionMakingTaskParameters":
        # TrialDraw.CheckTask's same rules.
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
    def _keys_of_the_task_type(self, info: ValidationInfo) -> "UclOpenCoenDomeDecisionMakingTaskParameters":
        # TrialDraw.CheckTask's same rules: rulesPerType names exactly its task type's keys; each condition is of one of
        # them, a go/no-go task has Go trials (the whole task's, not one condition's copy in _conditions_run).
        rules = RULE_KEYS[self.task_type]
        given = set(self.repeats.rules_per_type.model_dump(by_alias=True, exclude_none=True))
        if given != set(rules):
            raise ValueError(f"repeats.rules_per_type must give exactly {', '.join(rules)} in {self.task_type.value} (it gives {', '.join(sorted(given)) or 'none'})")
        keys = TRIAL_KEYS[self.task_type]
        for i, condition in enumerate(self.trial_conditions):
            if condition.type not in keys:
                raise ValueError(f"trial_conditions[{i}]'s type {condition.type} is not one of {', '.join(keys)} in {self.task_type.value}")
        if not sum(condition.weight for condition in self.trial_conditions) > 0:
            raise ValueError("trial_conditions needs a condition with a weight above 0")
        if self.task_type == TaskType.DISCRIMINATION_GO_NO_GO and not (info.context or {}).get("condition") and not any(c.type == "go" and c.weight > 0 for c in self.trial_conditions):
            raise ValueError("trial_conditions has no go condition with a weight above 0, so the task has no Go trials")
        return self

    @model_validator(mode="after")
    def _conditions_run(self, info: ValidationInfo) -> "UclOpenCoenDomeDecisionMakingTaskParameters":
        # TrialDraw's same build: each condition's set over the task's values, every combination of its list cells
        # (at most MAX_COMBINATIONS), each a task that must validate.
        if (info.context or {}).get("condition") or not any(c.set for c in self.trial_conditions):
            return self
        base = self.model_dump(by_alias=True, mode="json")
        for i, condition in enumerate(self.trial_conditions):
            where = f"trial_conditions[{i}] ({condition.type})"
            for path in condition.set:
                if condition.type not in CONDITION_FIELDS.get(path, ()):
                    raise ValueError(f"{where} sets {path}, which is not a condition field of {condition.type} (x-conditionFields)")
                if path.startswith("optogenetics.") and not self.optogenetics.enabled:
                    raise ValueError(f"{where} sets {path} with optogenetics off")
            combos = condition_combinations(base, condition.set)
            if len(combos) > MAX_COMBINATIONS:
                raise ValueError(f"{where} expands into {len(combos)} combinations, more than {MAX_COMBINATIONS}")
            for combo in combos:
                data = copy.deepcopy(base)
                for path, value in combo.items():
                    _put(data, path, value)
                data["trialConditions"] = [{"type": condition.type, "weight": 1}]
                try:
                    type(self).model_validate(data, context={"condition": True})
                except ValidationError as e:
                    raise ValueError(f"{where} with {combo}: {e}") from None
        return self

    @model_validator(mode="after")
    def _one_spout_in_go_no_go(self) -> "UclOpenCoenDomeDecisionMakingTaskParameters":
        # TrialDraw.CheckTask's same rule: go/no-go uses the left spout alone.
        if self.task_type == TaskType.DISCRIMINATION_GO_NO_GO:
            if self.both_spouts_probability != 0:
                raise ValueError("both_spouts_probability must be 0 in discriminationGoNoGo: only the left spout moves in")
        elif not self.spout_movements and self.both_spouts_probability != 1:
            # The SpoutsIn cue's LeftOnly or RightOnly draws the other spout back, which a task without spout movements never returns.
            raise ValueError("both_spouts_probability must be 1 with spout_movements false: a one-spout trial draws the other spout back")
        return self

    @model_validator(mode="after")
    def _large_reward_fits(self) -> "UclOpenCoenDomeDecisionMakingTaskParameters":
        # A RewardValve piece refuses a reward over MaxRewardUl when it plans it, mid-session; refuse it here instead.
        reward = self.reward
        if reward.large_reward_probability > 0 and reward.large_reward_multiplier > 1 and reward.reward_size_ul * reward.large_reward_multiplier > MAX_REWARD_UL + 1e-9:
            raise ValueError(
                f"reward.reward_size_ul ({reward.reward_size_ul} ul) x reward.large_reward_multiplier "
                f"({reward.large_reward_multiplier}) is more than {MAX_REWARD_UL} ul, the most one reward may ask for"
            )
        return self


class UclOpenCoenDomeDecisionMakingTaskLogic(Task):
    """Task logic definition for the coen-dome-decision-making experiment."""

    version: Literal[__semver__] = __semver__
    name: Literal["UclOpenCoenDomeDecisionMaking"] = Field(default="UclOpenCoenDomeDecisionMaking", description="Name of the task logic", frozen=True)
    task_parameters: UclOpenCoenDomeDecisionMakingTaskParameters = Field(description="Parameters of the task logic")
