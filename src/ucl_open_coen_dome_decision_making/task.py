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
    Task,
    TaskBlock,
    TaskParameters,
    WheelQuietWait,
    level_dbfs,
)
from open_rigs.core.task import Optogenetics as LabOptogenetics
from open_rigs.components.zapit import ZAPIT_MAX_RAMP_DOWN_SECONDS, ZAPIT_REPLY_MARGIN_SECONDS
from open_rigs.rigs import conditioned_task as ct
from open_rigs.rigs.conditioned_task import ABOVE_0, MAX_WEIGHT_SUM, SIDELESS, SOUNDING, SoundAzimuth, if_
from pydantic import ConfigDict, Field, ValidationInfo, field_validator, model_validator

from ucl_open_coen_dome_decision_making import __semver__

# The stimulus sound's level (auditoryStimulus.levelDbfs) in the task files: 3.22 dB louder than the waveforms as stored
# (-43.22 dBFS RMS while sounding, the level mice had until November 2026).
STIMULUS_LEVEL_DBFS = -40.0


class TrialStructure(Iti, ResponseWindow, LickQuietWait, WheelQuietWait):
    """Timing of each trial, every wait on the Harp clock. The interval (the lab's Iti block) runs from the last outcome
    (after any timeout; the first trial: the experiment folder's creation) to this trial's stimulus,
    outcomeToSpoutsOutSeconds included, so itiMinSeconds is at least outcomeToSpoutsOutSeconds; the quiet waits (the lab's
    LickQuietWait and WheelQuietWait blocks) can only lengthen it. A lick first counts responseDelaySeconds after stimulus
    onset, where the response window (the lab's ResponseWindow block) starts; responseWindowSeconds must be given: no limit is refused."""

    outcome_to_spouts_out_seconds: Double = Field(
        default=2.0, ge=0, description="From the outcome (after any timeout) to drawing the spouts out (the SpoutsOut cue), in seconds: time to drink a reward, inside the interval"
    )
    response_delay_seconds: Double = Field(default=0.0, ge=0, description="The response delay: from stimulus onset to the response window opening, in seconds; licks before it are not counted")
    visual_stimulus_seconds: Double = Field(
        default=1.5, gt=0, description="How long the grating shows, in seconds from onset; at most responseDelaySeconds plus responseWindowSeconds, so it never outlasts the response window, and at most itiMinSeconds, so it is off before the next stimulus"
    )
    stimulus_to_spouts_in_seconds: Double = Field(default=0.5, ge=0, description="From stimulus onset to moving the response spouts in (the SpoutsIn cue), in seconds")

    @model_validator(mode="after")
    def _ordered(self) -> "TrialStructure":
        if self.response_window_seconds is None:
            raise ValueError("response_window_seconds must be given: this task's response window always closes")
        if self.iti_min_seconds < self.outcome_to_spouts_out_seconds:
            raise ValueError("iti_min_seconds must be >= outcome_to_spouts_out_seconds: the spouts are drawn out outcome_to_spouts_out_seconds into the interval, before the stimulus")
        if self.visual_stimulus_seconds > self.response_delay_seconds + self.response_window_seconds:
            raise ValueError("visual_stimulus_seconds must not exceed response_delay_seconds + response_window_seconds")
        if self.visual_stimulus_seconds > self.iti_min_seconds:
            raise ValueError("visual_stimulus_seconds must not exceed iti_min_seconds: the grating would still show when the next trial's stimulus starts")
        return self


class VisualStimulus(TaskBlock):
    """The grating: its contrasts and initial azimuths (one of each drawn per trial) and its shape."""

    contrasts: list[Double] = Field(default=[1.0], min_length=1, description="Candidate grating contrasts, one drawn per trial; 0 shows no grating (a grey screen)")
    initial_azimuths_deg: list[Double] = Field(
        default=[-60.0, 60.0],
        min_length=1,
        description=(
            "Signed azimuths the grating starts at, in degrees, one picked per trial: positive is right, negative left, never 0; "
            "with target visualSide the picked azimuth's sign is the trial's side. Unused where contrasts are all 0"
        ),
    )
    elevation_deg: Double = Field(default=15.0, ge=-90, le=90, description="Elevation of the grating, in degrees")
    sigma_deg: Double = Field(default=20.0, description="Sigma of the grating's Gaussian window, in degrees")
    orientation_deg: Double = Field(default=0.0, description="Grating stripe orientation, in degrees; drift direction is orthogonal to it (orientationDeg + 90 degrees)")
    spatial_frequency_cpd: Double = Field(default=0.066666667, description="Grating spatial frequency, in cycles per degree")
    temporal_frequency_hz: Double = Field(default=0.0, ge=0, description="Unsigned grating drift rate, in Hz, drifting orthogonal to orientationDeg (orientationDeg + 90 degrees); 0 is static")

    @field_validator("contrasts")
    @classmethod
    def _not_negative(cls, value: list[float]) -> list[float]:
        if any(contrast < 0 for contrast in value):
            raise ValueError("contrasts must each be 0 or more")
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
    """The sound: a waveform of the set (src/sounds) at levelDbfs, on rig.yml's speaker nearest the picked azimuth at
    elevationDeg, or on every rig.yml speaker where the pick is "all"."""

    level_dbfs: ct.SoundLevelDbfs = level_dbfs(STIMULUS_LEVEL_DBFS, "The stimulus sound (the waveform set as one, OpenRigs.Audio SoundLevel measuring the set)")
    elevation_deg: Double = Field(default=15.0, ge=-90, le=90, description="Elevation the sound plays at, in degrees: with the azimuth drawn, it picks rig.yml's nearest speaker")
    initial_azimuths_deg: list[SoundAzimuth] = Field(
        default=[-60.0, 60.0],
        min_length=1,
        description=(
            "Signed azimuths the sound plays at, in degrees, one picked per trial: positive is right, negative left, 0 the centre; it plays on "
            "rig.yml's speaker nearest it. \"all\" plays it on every rig.yml speaker at once (logged auditoryInitialAzimuthDeg NaN with a level). "
            "With target auditorySide the picked azimuth's sign is the trial's side, so 0 and \"all\" are refused. Unused where the level is mute"
        ),
    )


class Reward(ct.Reward):
    """Reward delivery: the reward size and the large-reward trials (the lab's reward block)."""


class Response(TaskBlock):
    """Lick-accumulation response rule; thresholdLicks 1 with azimuthShiftPerLickDeg 0 is classic first-lick 2AFC."""

    threshold_licks: int = Field(default=1, ge=1, description="Net lick difference (left minus right, absolute) that ends the response window with a choice; in discriminationGoNoGo, licks at the left spout (the right is not counted)")
    azimuth_shift_per_lick_deg: Double = Field(default=0.0, description="Degrees the grating's azimuth moves per net lick; 0 keeps the stimulus fixed")


class TaskType(StrEnum):
    """What the mouse answers."""

    LOCALIZATION = "localization"
    DISCRIMINATION_GO_NO_GO = "discriminationGoNoGo"
    DISCRIMINATION_LEFT_RIGHT = "discriminationLeftRight"


class Target(StrEnum):
    """What a trial rewards: localization visualSide, auditorySide or random; discriminationGoNoGo go, noGo or noReward;
    discriminationLeftRight left, right or random (x-rules). Logged as the target column in the same spelling, a side as
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
    TaskType.LOCALIZATION: ("visualSide", "auditorySide", "random"),
    TaskType.DISCRIMINATION_GO_NO_GO: ("go", "noGo", "noReward"),
    TaskType.DISCRIMINATION_LEFT_RIGHT: ("left", "right", "random"),
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
    {"if": if_({"visualStimulus.contrasts": {"items": {"const": 0}}, "target": {"not": {"const": "visualSide"}}}), "unused": ["visualStimulus.initialAzimuthsDeg"],
     "message": "visualStimulus.contrasts 0 shows no grating, so its azimuths are unread unless target visualSide"},
    {"if": if_({"auditoryStimulus.levelDbfs": {"const": MUTE}, "target": {"not": {"const": "auditorySide"}}}), "unused": ["auditoryStimulus.initialAzimuthsDeg"],
     "message": "auditoryStimulus.levelDbfs mute plays no sound, so its azimuths are unread unless target auditorySide"},
    # The picked sound azimuth's sign is the side: 0 and "all" have none.
    {"if": if_({"target": {"const": "auditorySide"}}), "then": ct.props({"auditoryStimulus.initialAzimuthsDeg": {"items": {"not": {"enum": SIDELESS}}}}),
     "message": "target auditorySide needs auditory azimuths without 0 or all"},
)
# How a condition is named (the schema's x-trial-types; derive_trial_type): first match wins, on the condition's values
# each as a list. A role target names it; else by what it shows, the grating (a contrast above 0) and the sound as a cue
# (a level, not mute, at an azimuth off centre: 0 and "all" give no side): both, audiovisualConflict when the azimuths
# are paired mirrored, else audiovisualCoherent; one alone; or neutral.
_CUE = {"auditoryStimulus.levelDbfs": SOUNDING, "auditoryStimulus.initialAzimuthsDeg": {"contains": {"not": {"enum": SIDELESS}}}}
TRIAL_TYPES = (
    *({"trialType": role, "if": if_({"target": {"items": {"const": role}}})} for role in ("go", "noGo", "left", "right")),
    {"trialType": "audiovisualConflict", "if": if_({"visualStimulus.contrasts": ABOVE_0, **_CUE}),
     "mirrored": ["visualStimulus.initialAzimuthsDeg", "auditoryStimulus.initialAzimuthsDeg"]},
    {"trialType": "audiovisualCoherent", "if": if_({"visualStimulus.contrasts": ABOVE_0, **_CUE})},
    {"trialType": "visual", "if": if_({"visualStimulus.contrasts": ABOVE_0})},
    {"trialType": "auditory", "if": if_(_CUE)},
    {"trialType": "neutral"},
)
# What some weighted condition must draw (the schema's x-required-conditions; open_rigs.rigs.conditions.required_problems).
REQUIRED = (
    {"if": if_({"taskType": {"const": TaskType.DISCRIMINATION_GO_NO_GO.value}}), "some": if_({"target": {"const": "go"}}),
     "message": "taskType discriminationGoNoGo needs a condition with a weight above 0 that draws target go, or the task has no Go trials"},
)


class TrialCondition(ct.TrialCondition):
    """One kind of trial in the mix: its relative weight, its condition-level values (set) by dotted path and the lists it
    pairs; its trialType is derived from its values (x-trial-types, derive_trial_type), never stored. A list in a scalar
    field's cell is one value picked per trial; a list field's list (LIST_FIELDS: visualStimulus.contrasts, the initial
    azimuths), at any level, one entry picked per trial. Paired lists share one picked index per trial."""

    weight: int = Field(ge=0, le=1_000_000, description="Relative draw weight: each new trial picks a condition with these as relative chances")
    pairs: list[list[String]] = Field(
        default_factory=list,
        description=(
            "Groups of paths whose lists (the set's, else the task's) have equal length and share one picked index per trial: "
            "audiovisualCoherent pairs visualStimulus.initialAzimuthsDeg [-60, 60] with auditoryStimulus.initialAzimuthsDeg [-60, 60], "
            "audiovisualConflict with [60, -60]. Unpaired lists are picked independently"
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
    laserDurationSeconds is a timed stimulus here, per trial (its combination's). A repeated trial never has the laser on."""

    laser_duration_seconds: Double = Field(
        default=1.5,
        gt=0,
        description="How long the laser and the masking light stay on, in seconds from the trigger (a timed stimulus). With optogenetics on, at most responseDelaySeconds + itiMinSeconds - 0.7 s: Zapit is armed for the next trial only once this stimulus and its ramp-down (0.5 s at most) have ended, and its reply must be in 0.2 s before the next trigger",
    )


class Curriculum(ct.Curriculum):
    """The Mouse Curriculum stage these values are, and the task file's only record of the stage: app-rig-control fills it at
    launch from the share's curriculum file (K:\config\curricula\<repo>\<task file stem>.conditions.yml), its stage's values written over the task
    file's; empty in the repo's task files. Bonsai only records it."""


class UclOpenCoenDomeDecisionMakingTaskParameters(TaskParameters, ct.ConditionedTask):
    """Task parameters for the coen-dome-decision-making audiovisual tasks: localization and two modality discriminations."""

    model_config = ConfigDict(json_schema_extra=ct.schema_extra(SESSION_FIELDS, RULES, TRIAL_TYPES, REQUIRED))
    SESSION_FIELDS = SESSION_FIELDS
    RULES = RULES
    TRIAL_TYPES = TRIAL_TYPES
    REQUIRED = REQUIRED

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
    visual_stimulus: VisualStimulus = Field(description="The grating")
    auditory_stimulus: AuditoryStimulus = Field(description="The sound")
    reward: Reward = Field(description="Reward delivery")
    auto_reward_probability: Double = Field(
        default=0.0,
        ge=0,
        le=1,
        description="Probability that a trial is an auto-reward trial (logged as autoReward): its miss is rewarded at the target spout, and is still a noResponse for repeats; 0: never, 1: every trial",
    )
    penalty: Penalty = Field(
        description="The timeout and penalty sound after an incorrect response or a miss (a miss on an auto-reward trial is rewarded instead), and on a random-reward probe after a choice on the coin's tails or a miss; in discriminationGoNoGo only after a lick on a noGo trial (a go miss and a no-reward probe have neither), open_rigs.core.task Penalty"
    )
    optogenetics: Optogenetics = Field(description="Zapit optogenetic stimulation")
    task_type: TaskType = Field(
        description=(
            "localization: the answer is a side, at that side's spout. "
            "discriminationGoNoGo: one spout (the left); lick on a go trial, withhold on a noGo trial; a noReward trial is a probe, never "
            "rewarded or penalised. discriminationLeftRight: the left spout on a left trial, the right on a right trial. "
            "A target random trial (localization, discriminationLeftRight) is a random-reward probe: both spouts in, and a coin "
            "drawn with the trial from the seeded stream decides a choice at either: heads rewarded at that spout, tails penalised "
            "as an incorrect choice; a miss as a scored trial's miss. The targets each allows are x-rules'"
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
    single_spout_probability: Double = Field(
        default=0.0,
        ge=0,
        le=1,
        description=(
            "Probability that only the target's spout comes in, as a guide, on a scored trial of localization or discriminationLeftRight "
            "whose spouts move (spoutsStayIn false); else both come in. discriminationGoNoGo always uses the left spout alone, and a "
            "random-reward probe both (logged as singleSpoutOffered)"
        ),
    )
    spouts_stay_in: Bool = Field(
        default=False,
        description=(
            "Whether the used spouts stay in on this trial (habituation): no withdrawal and no single-spout move; at each interval's "
            "start the coming trial's value decides whether the spouts go out"
        ),
    )

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
    def between_trials(cls, tasks: list[dict]) -> None:
        _between_trials(tasks)


# The list fields one entry of which each trial draws (open_rigs.rigs.conditions.pick_lists).
LIST_FIELDS = UclOpenCoenDomeDecisionMakingTaskParameters.list_fields()


def _between_trials(tasks: list[dict]) -> None:
    """The timing rules between a trial and the next, each drawn from any weighted combination (TrialDraw.BetweenTrials):
    every outcomeToSpoutsOutSeconds and visualStimulusSeconds within every itiMinSeconds, and with optogenetics every
    laserDurationSeconds within the shortest trigger to the next trigger less the ramp-down and Zapit's reply margin."""
    if not tasks:
        return
    s = [task["trialStructure"] for task in tasks]
    iti = min(x["itiMinSeconds"] for x in s)
    for name in ("outcomeToSpoutsOutSeconds", "visualStimulusSeconds"):
        if (longest := max(x[name] for x in s)) > iti + 1e-9:
            raise ValueError(f"trialStructure.{name} {longest:g} s exceeds trialStructure.itiMinSeconds {iti:g} s of a combination that may come next")
    if tasks[0]["optogenetics"]["enabled"]:
        leads = [task["optogenetics"]["laserToStimulusSeconds"] for task in tasks]
        longest = (min(lead + x["responseDelaySeconds"] for lead, x in zip(leads, s)) + min(x["itiMinSeconds"] - lead for lead, x in zip(leads, s))
                   - ZAPIT_MAX_RAMP_DOWN_SECONDS - ZAPIT_REPLY_MARGIN_SECONDS)
        duration = max(task["optogenetics"]["laserDurationSeconds"] for task in tasks)
        if duration > longest + 1e-9:
            raise ValueError(f"optogenetics.laserDurationSeconds ({duration} s) must be at most {longest:g} s across the combinations: "
                             "the shortest lead plus response delay, plus the shortest interval less the lead, less the ramp-down and Zapit's reply margin")


class UclOpenCoenDomeDecisionMakingTaskLogic(Task):
    """Task logic definition for the coen-dome-decision-making experiment."""

    version: Literal[__semver__] = __semver__
    name: Literal["UclOpenCoenDomeDecisionMaking"] = Field(default="UclOpenCoenDomeDecisionMaking", description="Name of the task logic", frozen=True)
    task_parameters: UclOpenCoenDomeDecisionMakingTaskParameters = Field(description="Parameters of the task logic")
