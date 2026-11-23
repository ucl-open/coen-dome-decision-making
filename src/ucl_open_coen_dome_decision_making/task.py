from enum import StrEnum
from typing import Any, Literal

# Integers are int: open_rigs' Int carries its own range, which drops a Field's ge or gt.
from open_rigs.core.base import Bool, Double, String
from open_rigs.core.task import (
    MAX_REWARD_UL,
    MUTE,
    Iti,
    LickQuietWait,
    Penalty,
    ResponseWindow,
    ResponseWindowAlwaysFull,
    Reward as CoreReward,
    Spouts as LabSpouts,
    StimulusTiming,
    Task,
    TaskBlock,
    TaskParameters,
    WheelQuietWait,
    level_dbfs,
)
from open_rigs.core.task import Optogenetics as LabOptogenetics
from open_rigs.components.zapit import ZAPIT_ARM_SECONDS
from open_rigs.components.zapit import ramp_down_from
from open_rigs.rigs import conditioned_task as ct
from open_rigs.rigs.conditioned_task import ABOVE_0, MAX_WEIGHT_SUM, SIDELESS, SOUNDING, SoundAzimuth, if_
from pydantic import ConfigDict, Field, ValidationInfo, field_validator, model_validator

from ucl_open_coen_dome_decision_making import __semver__

# The stimulus sound's level (auditoryStimulus.initialLevelDbfs) in the task files: 3.22 dB louder than the waveforms as stored
# (-43.22 dBFS RMS while sounding, the level mice had until November 2026).
STIMULUS_LEVEL_DBFS = -40.0


class TrialStructure(Iti, ResponseWindow, ResponseWindowAlwaysFull, LickQuietWait, WheelQuietWait):
    """Timing of each trial, every wait on the Harp clock. The response window always runs in full (the lab's
    ResponseWindowAlwaysFull block, fixed true): a choice gives its outcome at once, but the trial ends at the window's end
    (trialEndTime), so a trial lasts the same whatever the mouse does. A rewarded trial is its end, then the interval (the
    lab's Iti block), the reward drunk from the outcome on (the spouts drawn out spouts.rewardConsumptionSeconds into the
    interval); a penalised one its end, its kind's timeout, then the interval; one neither rewarded nor penalised its end,
    then the interval (the first trial's: from the experiment folder's creation). The interval runs to this trial's
    stimulus; the quiet waits (the lab's LickQuietWait and WheelQuietWait blocks) can only lengthen it. A lick first counts
    stimulusToResponseWindowSeconds after stimulus onset, where the response window (the lab's ResponseWindow block)
    starts; responseWindowSeconds must be given: no limit is refused."""

    stimulus_to_response_window_seconds: Double = Field(default=0.0, ge=0, description="From stimulus onset to the response window opening, in seconds; responseWindowSeconds runs from that opening, and licks before it are not counted")
    response_window_always_full: Literal[True] = Field(
        default=True,
        frozen=True,
        description=(
            "Always true, fixed for this task: the response window always runs in full, so a trial lasts the same whatever the mouse "
            "does. A choice gives the outcome (reward, spouts, penalty sound and flash) at once, but the trial ends at the window's "
            "end (trialEndTime), rewarded or not; the next interval starts then, after a penalised trial's timeout"
        ),
    )


class Spouts(LabSpouts):
    """The spouts (the lab's spouts block, open_rigs.core.task Spouts) and this task's single-spout guide."""

    stimulus_to_spouts_in_seconds: Double = Field(default=0.5, description=LabSpouts.model_fields["stimulus_to_spouts_in_seconds"].description)
    single_spout_probability: Double = Field(
        default=0.0,
        ge=0,
        le=1,
        description=(
            "Probability that only the target's spout comes in, as a guide, on a scored trial of localization or discriminationLeftRight "
            "whose spouts move (alwaysIn false); else both come in. discriminationGoNoGo always uses the left spout alone, and a "
            "random-reward probe both (logged as singleSpoutOffered)"
        ),
    )


class Stimulus(StimulusTiming):
    """What the grating and the sound share: from onset, a pulse every 1/rateHz seconds (the sound file's 50 ms pulse, the
    grating on for 50 ms) until the stimulus off (the lab's StimulusTiming: durationSeconds after onset, or
    responseToStimulusOffSeconds after the response if that comes first), the last pulse cut where it ends."""

    duration_seconds: Double | None = Field(default=1.5, gt=0, description=StimulusTiming.model_fields["duration_seconds"].description)
    rate_hz: Double = Field(
        default=8.0, gt=0, le=20,
        description="Pulses a second, of the sound and the grating together, each 50 ms long (AuditoryStimulusSet.PulseSeconds, each sound file's length), so at most 20",
    )


class VisualStimulus(TaskBlock):
    """The grating as each trial starts: its initial contrasts and azimuths (one of each drawn per trial) and its shape."""

    initial_contrasts: list[Double] = Field(default=[1.0], min_length=1, description="Candidate grating contrasts as the trial starts, one drawn per trial; 0 shows no grating (a grey screen)")
    initial_azimuths_deg: list[Double] = Field(
        default=[-60.0, 60.0],
        min_length=1,
        description=(
            "Signed azimuths the grating starts at, in degrees, one picked per trial: positive is right, negative left, never 0; "
            "with target visualSide the picked azimuth's sign is the trial's side. Unused where initialContrasts are all 0"
        ),
    )
    initial_elevation_deg: Double = Field(default=15.0, ge=-90, le=90, description="Elevation of the grating as the trial starts, in degrees")
    initial_sigma_deg: Double = Field(default=20.0, description="Sigma of the grating's Gaussian window as the trial starts, in degrees")
    initial_orientation_deg: Double = Field(default=0.0, description="Grating stripe orientation as the trial starts, in degrees; drift direction is orthogonal to it (initialOrientationDeg + 90 degrees)")
    initial_spatial_frequency_cpd: Double = Field(default=0.066666667, description="Grating spatial frequency as the trial starts, in cycles per degree")
    initial_temporal_frequency_hz: Double = Field(default=0.0, ge=0, description="Unsigned grating drift rate as the trial starts, in Hz, drifting orthogonal to initialOrientationDeg (initialOrientationDeg + 90 degrees); 0 is static")

    @field_validator("initial_contrasts")
    @classmethod
    def _not_negative(cls, value: list[float]) -> list[float]:
        if any(contrast < 0 for contrast in value):
            raise ValueError("initialContrasts must each be 0 or more")
        return value

    @field_validator("initial_azimuths_deg")
    @classmethod
    def _signed(cls, value: list[float]) -> list[float]:
        # TrialDraw.CheckTask's same rule, with each azimuth's nearest speaker on its side.
        for azimuth in value:
            if not -180 <= azimuth <= 180:
                raise ValueError(f"initial_azimuths_deg value {azimuth} is outside [-180, 180]")
            if azimuth == 0:
                raise ValueError("initial_azimuths_deg must not hold 0: the sign is the trial's side")
        return value


class AuditoryStimulus(ct.AuditoryStimulus):
    """The sound: a waveform of the set (src/sounds) at initialLevelDbfs, on rig.yml's speaker nearest the picked azimuth at
    initialElevationDeg, or on every rig.yml speaker where the pick is "all"."""

    initial_level_dbfs: ct.SoundLevelDbfs = level_dbfs(STIMULUS_LEVEL_DBFS, "The stimulus sound (the waveform set as one, OpenRigs.Audio SoundLevel measuring the set)")
    initial_elevation_deg: Double = Field(default=15.0, ge=-90, le=90, description="Elevation the sound plays at as the trial starts, in degrees: with the azimuth drawn, it picks rig.yml's nearest speaker")
    initial_azimuths_deg: list[SoundAzimuth] = Field(
        default=[-90.0, 90.0],
        min_length=1,
        description=(
            "Signed azimuths the sound plays at, in degrees, one picked per trial: positive is right, negative left, 0 the centre; it plays on "
            "rig.yml's speaker nearest it. \"all\" plays it on every rig.yml speaker at once (logged auditoryInitialAzimuthDeg NaN with a level). "
            "With target auditorySide the picked azimuth's sign is the trial's side, so 0 and \"all\" are refused. Unused where the level is mute"
        ),
    )


class Reward(CoreReward):
    """Reward delivery (the lab's reward block): the reward size, the large-reward trials and the reward tone, which this
    workflow does not play yet (only mute)."""


class Response(TaskBlock):
    """Lick-accumulation response rule; thresholdLicks 1 with azimuthShiftPerLickDeg 0 is classic first-lick 2AFC."""

    threshold_licks: int = Field(default=1, ge=1, description="Net lick difference (left minus right, absolute) that makes the choice, its outcome at once (the response window still runs in full); in discriminationGoNoGo, licks at the left spout (the right is not counted)")
    azimuth_shift_per_lick_deg: Double = Field(default=0.0, description="Degrees the grating's azimuth moves per net lick; 0 keeps the stimulus fixed")


class TaskType(StrEnum):
    """What the mouse answers."""

    LOCALIZATION = "localization"
    DISCRIMINATION_GO_NO_GO = "discriminationGoNoGo"
    DISCRIMINATION_LEFT_RIGHT = "discriminationLeftRight"


class Target(StrEnum):
    """What a trial rewards: localization visualSide, auditorySide, random or noReward; discriminationGoNoGo go, noGo or noReward;
    discriminationLeftRight left, right, random or noReward (x-rules). Logged as the target column in the same spelling, a side as
    left or right."""

    VISUAL_SIDE = "visualSide"
    AUDITORY_SIDE = "auditorySide"
    RANDOM = "random"
    GO = "go"
    NO_GO = "noGo"
    NO_REWARD = "noReward"
    LEFT = "left"
    RIGHT = "right"


class RepeatOn(StrEnum):
    """When a trial is repeated: never, noResponse (after noResponse) or notCorrect (after incorrect or noResponse)."""

    NEVER = "never"
    NO_RESPONSE = "noResponse"
    NOT_CORRECT = "notCorrect"


# The session fields (the schema's x-session-fields; TrialDraw.SessionFields, a test holds both): only what cannot change
# per trial, so a condition may not set it (the path or any path under it). A condition may set every other field: the
# draw reads it per trial from the trial's combination, logged through trialCombinationId and Settings_Combinations.
SESSION_FIELDS = {
    **ct.COMMON_SESSION_FIELDS,
    "taskType": "the task the file and its curriculum train; x-required-conditions keys on it",
}
# Where one setting limits another (the schema's x-rules, JSON Schema over dotted paths; open_rigs.rigs.conditions
# evaluates them), checked on the task and on every combination of every condition. A side target reads its azimuth to
# set the reward side, even unshown.
TARGETS = {
    TaskType.LOCALIZATION: ("visualSide", "auditorySide", "random", "noReward"),
    TaskType.DISCRIMINATION_GO_NO_GO: ("go", "noGo", "noReward"),
    TaskType.DISCRIMINATION_LEFT_RIGHT: ("left", "right", "random", "noReward"),
}
PROBES = ("random", "noReward")  # the targets with no right answer


RULES = (
    {"if": if_({"repeats.maxRepeats": {"const": 0}}), "then": ct.props({"repeats.repeatOn": {"const": "never"}}),
     "message": "repeats.maxRepeats 0 needs repeats.repeatOn never"},
    {"if": if_({"repeats.repeatOn": {"const": "never"}}), "then": ct.props({"repeats.maxRepeats": {"const": 0}}),
     "message": "repeats.repeatOn never needs repeats.maxRepeats 0"},
    # A probe has no right answer: it may repeat after a miss (noResponse), never after a wrong answer.
    {"if": if_({"target": {"enum": list(PROBES)}}), "then": ct.props({"repeats.repeatOn": {"not": {"const": "notCorrect"}}}),
     "message": "a probe (target random or noReward) needs repeats.repeatOn never or noResponse, not notCorrect"},
    # target is each condition's (the task's is null, which no rule reads).
    *({"if": if_({"taskType": {"const": task_type.value}, "target": {"type": "string"}}), "then": ct.props({"target": {"enum": list(targets)}}),
       "message": f"taskType {task_type.value} needs target one of {', '.join(targets)}"} for task_type, targets in TARGETS.items()),
    {"if": if_({"visualStimulus.initialContrasts": {"items": {"const": 0}}, "target": {"not": {"const": "visualSide"}}}), "unused": ["visualStimulus.initialAzimuthsDeg"],
     "message": "visualStimulus.initialContrasts 0 shows no grating, so its azimuths are unread unless target visualSide"},
    {"if": if_({"auditoryStimulus.initialLevelDbfs": {"const": MUTE}, "target": {"not": {"const": "auditorySide"}}}), "unused": ["auditoryStimulus.initialAzimuthsDeg"],
     "message": "auditoryStimulus.initialLevelDbfs mute plays no sound, so its azimuths are unread unless target auditorySide"},
    # The picked sound azimuth's sign is the side: 0 and "all" have none.
    {"if": if_({"target": {"const": "auditorySide"}}), "then": ct.props({"auditoryStimulus.initialAzimuthsDeg": {"items": {"not": {"enum": SIDELESS}}}}),
     "message": "target auditorySide needs auditory azimuths without 0 or all"},
    # A go/no-go miss is never penalised, so a miss's own cost would never be used (TrialDraw.Check's same rule).
    {"if": if_({"taskType": {"const": TaskType.DISCRIMINATION_GO_NO_GO.value}}),
     "then": ct.props({"penalty.noResponse.timeoutSeconds": {"type": "null"}}),
     "message": "taskType discriminationGoNoGo never penalises a miss, so penalty.noResponse must be null"},
)
# How a condition is named (the schema's x-trial-types; derive_trial_type): first match wins, on the condition's values
# each as a list. A role target names it; else by what it shows, the grating (a contrast above 0) and the sound as a cue
# (a level, not mute, at an azimuth off centre: 0 and "all" give no side): both, audiovisualConflict when the azimuths
# are paired mirrored, else audiovisualCoherent; one alone; or neutral.
_CUE = {"auditoryStimulus.initialLevelDbfs": SOUNDING, "auditoryStimulus.initialAzimuthsDeg": {"contains": {"not": {"enum": SIDELESS}}}}
TRIAL_TYPES = (
    *({"trialType": role, "if": if_({"target": {"items": {"const": role}}})} for role in ("go", "noGo", "left", "right")),
    {"trialType": "audiovisualConflict", "if": if_({"visualStimulus.initialContrasts": ABOVE_0, **_CUE}),
     "mirrored": ["visualStimulus.initialAzimuthsDeg", "auditoryStimulus.initialAzimuthsDeg"]},
    {"trialType": "audiovisualCoherent", "if": if_({"visualStimulus.initialContrasts": ABOVE_0, **_CUE})},
    {"trialType": "visual", "if": if_({"visualStimulus.initialContrasts": ABOVE_0})},
    {"trialType": "auditory", "if": if_(_CUE)},
    {"trialType": "neutral"},
)
# What some weighted condition must draw (the schema's x-required-conditions; open_rigs.rigs.conditions.required_problems).
REQUIRED_CONDITIONS = (
    {"if": if_({"taskType": {"const": TaskType.DISCRIMINATION_GO_NO_GO.value}}), "some": if_({"target": {"const": "go"}}),
     "message": "taskType discriminationGoNoGo needs a condition with a weight above 0 that draws target go, or the task has no Go trials"},
)


class TrialCondition(ct.TrialCondition):
    """One kind of trial in the mix: its relative weight, its condition-level values (set) by dotted path and the lists it
    pairs; its trialType is derived from its values (x-trial-types, derive_trial_type), never stored. A list in a scalar
    field's cell is one value picked per trial; a list field's list (LIST_FIELDS: visualStimulus.initialContrasts, the initial
    azimuths), at any level, one entry picked per trial. Paired lists share one picked index per trial."""

    weight: int = Field(ge=0, le=1_000_000, description="Relative draw weight: each new trial picks a condition with these as relative chances")
    pairs: list[list[String]] = Field(
        default_factory=list,
        description=(
            "Groups of paths whose lists (the set's, else the task's) have equal length and share one picked index per trial: "
            "audiovisualCoherent pairs visualStimulus.initialAzimuthsDeg [-60, 60] with auditoryStimulus.initialAzimuthsDeg [-90, 90], "
            "audiovisualConflict with [90, -90]. Unpaired lists are picked independently"
        ),
    )
    set: dict[str, Any] = Field(default_factory=dict, description="Condition-level values by dotted path, any field but x-session-fields'; the rest are the task's")


class Repeats(TaskBlock):
    """Trial repetition policy, condition fields both: a trial is repeated after the outcome repeatOn names, at most maxRepeats times in a row."""

    max_repeats: int = Field(default=0, ge=0, description="Maximum number of consecutive repeats of the same trial; 0 exactly when repeatOn is never (x-rules)")
    repeat_on: RepeatOn = Field(
        default=RepeatOn.NEVER,
        description="never (maxRepeats 0); noResponse: after noResponse; notCorrect: after incorrect or noResponse. A probe (target random or noReward) repeats on noResponse or never (x-rules)",
    )


class Optogenetics(LabOptogenetics):
    """Zapit optogenetic stimulation: the lab's block (open_rigs.core.task Optogenetics) and this task's laser timing;
    laserDurationSeconds is a fixed laser time here, per trial (its combination's). A repeated trial never has the laser on."""

    laser_duration_seconds: Double = Field(
        default=1.5,
        gt=0,
        description="How long the laser and the masking light stay on, in seconds from the trigger. With optogenetics on, at most stimulusToResponseWindowSeconds + itiMinSeconds - the Zapit bank's longest ramp-down - 0.2 s: Zapit is armed for the next trial only once this laser and its ramp-down have ended, and arming takes up to 0.2 s before the next trigger (checked against the session's bank at Go)",
    )


class Curriculum(ct.Curriculum):
    r"""The Mouse Curriculum stage these values are, and the task file's only record of the stage: app-rig-control fills it at
    launch from the share's curriculum file (K:\config\curricula\<repo>\<task file stem>.conditions.yml), its stage's values written over the task
    file's; empty in the repo's task files. Bonsai only records it."""


class UclOpenCoenDomeDecisionMakingTaskParameters(TaskParameters, ct.ConditionedTask):
    """Task parameters for the coen-dome-decision-making audiovisual tasks: localization and two modality discriminations."""

    model_config = ConfigDict(json_schema_extra=ct.schema_extra(SESSION_FIELDS, RULES, TRIAL_TYPES, REQUIRED_CONDITIONS))
    SESSION_FIELDS = SESSION_FIELDS
    RULES = RULES
    TRIAL_TYPES = TRIAL_TYPES
    REQUIRED_CONDITIONS = REQUIRED_CONDITIONS

    curriculum: Curriculum | None = Field(
        default=None,
        description="The Mouse Curriculum stage these values are, the only record of the stage, filled by app-rig-control at launch; empty or malformed stops the workflow at Go",
    )
    trial_conditions: list[TrialCondition] = Field(
        min_length=1,
        description=(
            "The trial mix: each new trial picks one condition at random with the weights as relative chances, independently of every other "
            "trial (repeats copy the trial they repeat), then draws as the task's values with the condition's set over them. One whole value: a curriculum "
            "stage's conditions replace it; the trialConditionId column is its 0-based index"
        ),
        json_schema_extra={"x-whole": True},
    )
    trial_structure: TrialStructure = Field(description="Timing of each trial")
    spouts: Spouts = Field(description="The spouts: when they come in and go out, whether they are always in, and the single-spout guide")
    stimulus: Stimulus = Field(description="What the grating and the sound share: their duration, their off after the response, and the pulse rate")
    visual_stimulus: VisualStimulus = Field(description="The grating")
    auditory_stimulus: AuditoryStimulus = Field(description="The sound")
    reward: Reward = Field(description="Reward delivery")
    auto_reward_probability: Double = Field(
        default=0.0,
        ge=0,
        le=1,
        description="Probability that a trial is an auto-reward trial (logged as autoReward): its miss is rewarded at the target spout, and is never repeated (neither noResponse nor notCorrect); 0: never, 1: every trial",
    )
    penalty: Penalty = Field(
        description="What a penalised trial costs, by its kind (open_rigs.core.task Penalty): penalty.incorrect after an incorrect choice, and on a random-reward probe after a choice on the coin's tails when penaliseRandomProbes; penalty.noResponse after a miss (a miss on an auto-reward trial is rewarded instead), a random probe's too when penaliseRandomProbes; a noReward probe is never penalised. discriminationGoNoGo penalises only a lick on a noGo trial (a go miss and a no-reward probe have neither), so its penalty.noResponse is null"
    )
    penalise_random_probes: Bool = Field(
        default=False,
        description=(
            "Whether a random-reward probe (target random) is penalised; it affects only random probes (a noReward probe is never "
            "rewarded or penalised, a scored trial always is). true: a choice on the coin's tails as an incorrect choice "
            "(penalty.incorrect) and a miss not auto-rewarded as penalty.noResponse; false (the default): never, tails goes unrewarded with "
            "no timeout, sound or flash, and a miss costs nothing"
        ),
    )
    optogenetics: Optogenetics = Field(description="Zapit optogenetic stimulation")
    task_type: TaskType = Field(
        description=(
            "localization: the answer is a side, at that side's spout. "
            "discriminationGoNoGo: one spout (the left); lick on a go trial, withhold on a noGo trial; a noReward trial is a probe, never "
            "rewarded or penalised. discriminationLeftRight: the left spout on a left trial, the right on a right trial. "
            "A target random trial (localization, discriminationLeftRight) is a random-reward probe: both spouts in, and a coin "
            "drawn with the trial from the seeded stream decides a choice at either: heads rewarded at that spout, tails unrewarded "
            "(and penalised as an incorrect choice, a miss as a scored trial's miss, only when penaliseRandomProbes). A target noReward trial "
            "(any task type) is a probe with nothing on offer: never rewarded or penalised, and in localization and discriminationLeftRight both spouts in. "
            "The targets each allows are x-rules'"
        ),
    )
    target: Target | None = Field(
        default=None,
        description=(
            "What a trial rewards, set by every trial condition and never by the task: visualSide or auditorySide, the side of the "
            "picked grating or sound azimuth; left or right; go (lick) or noGo (withhold); random, a random-reward probe; noReward, "
            "a probe with no reward on offer and no penalty"
        ),
    )
    response: Response = Field(description="Lick-accumulation response rule")
    repeats: Repeats = Field(description="Trial repetition policy")

    @model_validator(mode="after")
    def _target_per_condition(self, info: ValidationInfo) -> "UclOpenCoenDomeDecisionMakingTaskParameters":
        # TrialDraw.CheckTask's same rule. A combination (validated with context condition) has its condition's target.
        if (info.context or {}).get("condition"):
            return self
        if self.target is not None:
            raise ValueError("target is each trial condition's (its set's target), not the task's")
        for i, condition in enumerate(self.trial_conditions):
            if "target" not in condition.set:
                raise ValueError(f"trial_conditions[{i}] ({self.derive_trial_type(condition)}) needs a target in its set")
        return self

    @model_validator(mode="after")
    def _timing_fits(self, info: ValidationInfo) -> "UclOpenCoenDomeDecisionMakingTaskParameters":
        # TrialDraw.CheckTask's same rules.
        opto, s = self.optogenetics, self.trial_structure
        # The soonest outcome is the response window's opening; the shortest outcome to next interval 0, as every trial
        # here can end rewarded or neither (a correct choice, a probe's heads, a go/no-go miss or withheld noGo).
        self.stimulus.check_fits(
            s.iti_min_seconds, s.stimulus_to_response_window_seconds + s.response_window_seconds,
            stimulus_to_earliest_outcome_seconds=s.stimulus_to_response_window_seconds,
            min_outcome_to_iti_seconds=0.0,
        )
        # A miss is penalised in localization and discriminationLeftRight unless every trial is auto-rewarded.
        self.penalty.check_misses(self.task_type != TaskType.DISCRIMINATION_GO_NO_GO and self.auto_reward_probability < 1)
        self.spouts.check_fits(s.iti_min_seconds, s.stimulus_to_response_window_seconds)
        opto.check_lead_fits(s.iti_min_seconds)
        lead = opto.laser_to_stimulus_seconds
        if -lead > s.stimulus_to_response_window_seconds + s.response_window_seconds + 1e-9:
            raise ValueError(
                f"optogenetics.laser_to_stimulus_seconds ({lead} s) puts the trigger after the response window "
                f"(stimulus_to_response_window_seconds + response_window_seconds, {s.stimulus_to_response_window_seconds + s.response_window_seconds:g} s)"
            )
        # The Zapit bank's longest ramp-down, once the session's bank is built (app-rig-control's open_rigs.rigs.check_task
        # passes it as context); without it, 0, the laser and the arm time alone. Go has the
        # last word either way (TrialDraw.LaserProblems).
        opto.check_zapit_free_in_time(
            s.iti_min_seconds,
            laser_duration_seconds=opto.laser_duration_seconds,
            ramp_down_seconds=ramp_down_from(info),
            response_delay_seconds=s.stimulus_to_response_window_seconds,
        )
        return self

    @model_validator(mode="after")
    def _reward_tone_mute(self) -> "UclOpenCoenDomeDecisionMakingTaskParameters":
        # TrialDraw.Check's same rule: the workflow has no reward tone to play yet.
        if self.reward.tone.level_dbfs != MUTE:
            raise ValueError("reward.tone.level_dbfs must be mute: this workflow has no reward tone sound to play yet")
        return self

    @model_validator(mode="after")
    def _large_reward_fits(self) -> "UclOpenCoenDomeDecisionMakingTaskParameters":
        # A RewardValve piece refuses a reward over MaxRewardUl when it plans it, mid-session; refuse it here instead.
        reward = self.reward
        if reward.large_probability > 0 and reward.size_ul * reward.large_multiplier > MAX_REWARD_UL + 1e-9:
            raise ValueError(
                f"reward.size_ul ({reward.size_ul} ul) x reward.large_multiplier "
                f"({reward.large_multiplier}) is more than {MAX_REWARD_UL} ul, the most one reward may ask for"
            )
        return self

    @classmethod
    def between_trials(cls, tasks: list[dict], context: dict | None = None) -> None:
        _between_trials(tasks, ramp_down_from(context))


# The list fields one entry of which each trial draws (open_rigs.rigs.conditions.pick_lists).
LIST_FIELDS = UclOpenCoenDomeDecisionMakingTaskParameters.list_fields()


def _between_trials(tasks: list[dict], ramp_down_seconds: Double = 0.0) -> None:
    """The timing rules between a trial and the next, each drawn from any weighted combination (TrialDraw.BetweenTrials):
    the longest spouts.rewardConsumptionSeconds plus the spouts' longest lead before the stimulus within the shortest
    itiMinSeconds (OpenRigs.Trials Spouts.BetweenTrialsProblems); every stimulus.durationSeconds past its soonest outcome,
    and every stimulus.responseToStimulusOffSeconds, within the shortest itiMinSeconds (the shortest outcome to next
    interval is 0: every trial can end rewarded or neither; StimulusTiming.BetweenTrialsProblems); and with optogenetics every laserDurationSeconds within the shortest trigger to the next trigger less the ramp-down and
    ZapitDaq's arm time."""
    if not tasks:
        return
    s = [task["trialStructure"] for task in tasks]
    iti = min(x["itiMinSeconds"] for x in s)
    spouts = [task["spouts"] for task in tasks]
    lead = max([0.0] + [-x["stimulusToSpoutsInSeconds"] for x in spouts if not x["alwaysIn"]])
    consumption = max(x["rewardConsumptionSeconds"] for x in spouts)
    if consumption + lead > iti + 1e-9:
        raise ValueError(
            f"spouts.rewardConsumptionSeconds {consumption:g} s plus the spouts' longest lead before the stimulus {lead:g} s exceeds "
            f"trialStructure.itiMinSeconds {iti:g} s of a combination that may come next"
        )
    gap = iti
    past = [task["stimulus"]["durationSeconds"] - x["stimulusToResponseWindowSeconds"] for task, x in zip(tasks, s) if task["stimulus"]["durationSeconds"] is not None]
    if past and max(past) > gap + 1e-9:
        raise ValueError(
            f"stimulus.durationSeconds runs {max(past):g} s past the soonest outcome, longer than itiMinSeconds ({gap:g} s) "
            "of a combination that may come next (the shortest outcome to next interval is 0, after a reward)"
        )
    after = [task["stimulus"]["responseToStimulusOffSeconds"] for task in tasks if task["stimulus"]["responseToStimulusOffSeconds"] is not None]
    if after and max(after) > gap + 1e-9:
        raise ValueError(
            f"stimulus.responseToStimulusOffSeconds {max(after):g} s is longer than itiMinSeconds ({gap:g} s) "
            "of a combination that may come next (the shortest outcome to next interval is 0, after a reward)"
        )
    if tasks[0]["optogenetics"]["enabled"]:
        leads = [task["optogenetics"]["laserToStimulusSeconds"] for task in tasks]
        cap = (min(lead + x["stimulusToResponseWindowSeconds"] for lead, x in zip(leads, s)) + min(x["itiMinSeconds"] - lead for lead, x in zip(leads, s))
                   - ramp_down_seconds - ZAPIT_ARM_SECONDS)
        duration = max(task["optogenetics"]["laserDurationSeconds"] for task in tasks)
        if duration > cap + 1e-9:
            raise ValueError(f"optogenetics.laserDurationSeconds ({duration} s) must be at most {cap:g} s across the combinations: "
                             "the shortest lead plus stimulus to response window, plus the shortest interval less the lead, "
                             f"less the Zapit bank's {ramp_down_seconds:g} s ramp-down and Zapit's arm time")


class UclOpenCoenDomeDecisionMakingTaskLogic(Task):
    """Task logic definition for the coen-dome-decision-making experiment."""

    version: Literal[__semver__] = __semver__
    name: Literal["UclOpenCoenDomeDecisionMaking"] = Field(default="UclOpenCoenDomeDecisionMaking", description="Name of the task logic", frozen=True)
    task_parameters: UclOpenCoenDomeDecisionMakingTaskParameters = Field(description="Parameters of the task logic")
