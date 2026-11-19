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
    Task,
    TaskBlock,
    TaskParameters,
    WheelQuietWait,
)
from open_rigs.components.zapit import ZAPIT_MAX_RAMP_DOWN_SECONDS, ZAPIT_REPLY_MARGIN_SECONDS
from open_rigs.rigs import conditioned_task as ct
from open_rigs.rigs.conditioned_task import ABOVE_0, ALL_SPEAKERS, MAX_WEIGHT_SUM, SIDELESS, SoundAzimuth, if_
from pydantic import ConfigDict, Field, field_validator, model_validator

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
    """Grating stimulus parameters and the grating's positions; the contrasts are taskParameters' own."""

    initial_azimuths_deg: list[Double] = Field(
        default=[-60.0, 60.0],
        min_length=1,
        description=(
            "Signed azimuths the grating starts at, in degrees, one picked per trial: positive is right, negative left, never 0; "
            "with target visualSide the picked azimuth's sign is the trial's side. Unused where contrasts are all 0"
        ),
    )
    elevation_deg: Double = Field(default=15.0, ge=-90, le=90, description="Elevation of the grating and of the sound's position, in degrees")
    gaussian_window_sigma_deg: Double = Field(default=20.0, description="Sigma of the Gaussian aperture window, in degrees")
    orientation_deg: Double = Field(default=0.0, description="Grating stripe orientation, in degrees; drift direction is orthogonal to it (orientationDeg + 90 degrees)")
    spatial_frequency_cpd: Double = Field(default=0.066666667, description="Grating spatial frequency, in cycles per degree")
    temporal_frequency_hz: Double = Field(default=0.0, ge=0, description="Unsigned grating drift rate, in Hz, drifting orthogonal to orientationDeg (orientationDeg + 90 degrees); 0 is static")

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
    """The sound: a waveform of the set (src/sounds) at amplitude, on rig.yml's speaker nearest the picked azimuth (at
    visualStimulus.elevationDeg), or on every rig.yml speaker where the pick is "all"."""

    amplitude: Double = Field(
        default=1.0,
        ge=0,
        description=(
            "Scale of the waveform's samples as stored: 0 no sound, 1 the WAV as stored (-47.2 dBFS RMS, production av-dome's loudness), "
            "above 1 louder. Go refuses the largest amplitude any condition can draw if a waveform would peak at -1 dBFS or more after "
            "equalisation on any speaker; dB SPL is unknown until the speakers' SPL calibration"
        ),
    )
    initial_azimuths_deg: list[SoundAzimuth] = Field(
        default=[-60.0, 60.0],
        min_length=1,
        description=(
            "Signed azimuths the sound plays at, in degrees, one picked per trial: positive is right, negative left, 0 the centre; it plays on "
            "rig.yml's speaker nearest it. \"all\" plays it on every rig.yml speaker at once (logged auditoryAzimuthDeg NaN with an amplitude "
            "above 0). With target auditorySide the picked azimuth's sign is the trial's side, so 0 and \"all\" are refused. Unused where the amplitude is 0"
        ),
    )


class RewardSettings(ct.Reward):
    """Reward delivery: the reward size (the lab's RewardSize block) and the large-reward trials."""


class ResponseSettings(TaskBlock):
    """Lick-accumulation response rule; thresholdLicks 1 with degreesPerLick 0 is classic first-lick 2AFC."""

    threshold_licks: int = Field(default=1, ge=1, description="Net lick difference (left minus right, absolute) that ends the response window with a choice; in discriminationGoNoGo, licks at the left spout (the right is not counted)")
    degrees_per_lick: Double = Field(default=0.0, description="Degrees the grating azimuth moves per net lick difference; 0 keeps the stimulus fixed")


class TaskType(StrEnum):
    """What the mouse answers."""

    LOCALIZATION = "localization"
    DISCRIMINATION_GO_NO_GO = "discriminationGoNoGo"
    DISCRIMINATION_LEFT_RIGHT = "discriminationLeftRight"


class Target(StrEnum):
    """What a trial rewards: localization visualSide, auditorySide or random; discriminationGoNoGo go, noGo or noReward;
    discriminationLeftRight left, right or random (x-rules). Logged as the target column: Left or Right for a side, Go, NoGo,
    NoReward, Random."""

    VISUAL_SIDE = "visualSide"
    AUDITORY_SIDE = "auditorySide"
    RANDOM = "random"
    GO = "go"
    NO_GO = "noGo"
    NO_REWARD = "noReward"
    LEFT = "left"
    RIGHT = "right"


class RepeatOn(StrEnum):
    """When a trial is repeated: never, noResponse (after NoResponse) or notCorrect (after Incorrect or NoResponse)."""

    NEVER = "never"
    NO_RESPONSE = "noResponse"
    NOT_CORRECT = "notCorrect"


class TrialType(StrEnum):
    """A condition's name, logged as trialType: it names rows and analysis groups and drives nothing (derive_trial_type)."""

    VISUAL = "visual"
    AUDITORY = "auditory"
    AUDIOVISUAL_COHERENT = "audiovisualCoherent"
    AUDIOVISUAL_CONFLICT = "audiovisualConflict"
    NEUTRAL = "neutral"
    GO = "go"
    NO_GO = "noGo"
    LEFT = "left"
    RIGHT = "right"


# The session fields (the schema's x-session-fields; TrialDraw.SessionFields, a test holds both): what cannot change per
# trial, so a condition may not set it (the path or any path under it). A condition may set every other field: the draw
# reads it per trial from the trial's combination. A condition's blank takes the stage's, then the task's value.
SESSION_FIELDS = {
    "rngSeed": "seeds the one random stream every trial is drawn from, at Go",
    "curriculum": "the stage these values are: one per task file",
    "trialConditions": "the mix the conditions are",
    "taskType": "the task the file and its curriculum train; x-required-conditions keys on it",
    "backgroundNoise": "plays without a break from Ready, before the first trial",
    "spoutMovements": "the spouts' place before trial 1 is set once, at trial 1's interval start (SpoutCommands)",
    "penalty.sound.levelDbfs": "the penalty sound's buffer is laid out once at Go (OpenRigs.Audio PlayPenaltySound)",
    "optogenetics.enabled": "Zapit is connected and its conditions file loaded at Go",
    "optogenetics.zapitConditionsPath": "the conditions file loaded in Zapit and counted at Go",
    "optogenetics.laserDurationSeconds": "the trial loop's laser timing (lib-open-rigs LaserTiming) is fixed at Go",
}
# Where one setting limits another (the schema's x-rules, JSON Schema over dotted paths; open_rigs.rigs.conditions
# evaluates them), checked on the task and on every combination of every condition. A side target reads its azimuth to
# set the reward side, even unshown.
TARGETS = {
    TaskType.LOCALIZATION: ("visualSide", "auditorySide", "random"),
    TaskType.DISCRIMINATION_GO_NO_GO: ("go", "noGo", "noReward"),
    TaskType.DISCRIMINATION_LEFT_RIGHT: ("left", "right", "random"),
}


RULES = (
    {"if": if_({"repeats.maxRepeats": {"const": 0}}), "then": ct.props({"repeats.repeatOn": {"const": "never"}}),
     "message": "repeats.maxRepeats 0 needs repeats.repeatOn never"},
    {"if": if_({"target": {"const": "random"}}), "then": ct.props({"bothSpoutsProbability": {"const": 1}, "repeats.repeatOn": {"const": "never"}}),
     "message": "target random needs bothSpoutsProbability 1 and repeats.repeatOn never"},
    {"if": if_({"target": {"const": "noReward"}}), "then": ct.props({"repeats.repeatOn": {"const": "never"}}),
     "message": "target noReward needs repeats.repeatOn never"},
    *({"if": if_({"taskType": {"const": task_type.value}}), "then": ct.props({"target": {"enum": list(targets)}}),
       "message": f"taskType {task_type.value} needs target one of {', '.join(targets)}"} for task_type, targets in TARGETS.items()),
    {"if": if_({"contrasts": {"items": {"const": 0}}, "target": {"not": {"const": "visualSide"}}}), "unused": ["visualStimulus.initialAzimuthsDeg"],
     "message": "contrasts 0 shows no grating, so its azimuths are unread unless target visualSide"},
    {"if": if_({"auditoryStimulus.amplitude": {"const": 0}, "target": {"not": {"const": "auditorySide"}}}), "unused": ["auditoryStimulus.initialAzimuthsDeg"],
     "message": "auditoryStimulus.amplitude 0 plays no sound, so its azimuths are unread unless target auditorySide"},
    # The picked sound azimuth's sign is the side: 0 and "all" have none.
    {"if": if_({"target": {"const": "auditorySide"}}), "then": ct.props({"auditoryStimulus.initialAzimuthsDeg": {"items": {"not": {"enum": SIDELESS}}}}),
     "message": "target auditorySide needs auditory azimuths without 0 or all"},
)
# How a condition is named (the schema's x-trial-types; derive_trial_type): first match wins, on the condition's values
# each as a list. A role target names it; else by what it shows, the grating (a contrast above 0) and the sound as a cue
# (an amplitude above 0 at an azimuth off centre: 0 and "all" give no side): both, audiovisualConflict when the azimuths
# are paired mirrored, else audiovisualCoherent; one alone; or neutral.
_CUE = {"auditoryStimulus.amplitude": ABOVE_0, "auditoryStimulus.initialAzimuthsDeg": {"contains": {"not": {"enum": SIDELESS}}}}
TRIAL_TYPES = (
    *({"trialType": role, "if": if_({"target": {"items": {"const": role}}})} for role in ("go", "noGo", "left", "right")),
    {"trialType": "audiovisualConflict", "if": if_({"contrasts": ABOVE_0, **_CUE}),
     "mirrored": ["visualStimulus.initialAzimuthsDeg", "auditoryStimulus.initialAzimuthsDeg"]},
    {"trialType": "audiovisualCoherent", "if": if_({"contrasts": ABOVE_0, **_CUE})},
    {"trialType": "visual", "if": if_({"contrasts": ABOVE_0})},
    {"trialType": "auditory", "if": if_(_CUE)},
    {"trialType": "neutral"},
)
# What some weighted condition must draw (the schema's x-required-conditions; open_rigs.rigs.conditions.required_problems).
REQUIRED = (
    {"if": if_({"taskType": {"const": TaskType.DISCRIMINATION_GO_NO_GO.value}}), "some": if_({"target": {"const": "go"}}),
     "message": "taskType discriminationGoNoGo needs a condition with a weight above 0 that draws target go, or the task has no Go trials"},
)


class TrialCondition(ct.TrialCondition[TrialType]):
    """One kind of trial in the mix: its trialType, its relative weight, its condition-level values (set) by dotted path and
    the lists it pairs. A list in a scalar field's cell is one value picked per trial; a list field's list (LIST_FIELDS:
    contrasts, the initial azimuths), at any level, one entry picked per trial. Paired lists share one picked index per trial."""

    trial_type: TrialType = Field(
        description="The condition's name, logged as each trial's trialType: derived from its contrasts, amplitude, pairing and target (x-trial-types, derive_trial_type) unless chosen; it names rows and analysis groups and drives nothing"
    )
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


class RepeatSettings(TaskBlock):
    """Trial repetition policy, condition fields both: a trial is repeated after the outcome repeatOn names, at most maxRepeats times in a row."""

    max_repeats: int = Field(default=5, ge=0, description="Maximum number of consecutive repeats of the same trial; 0 never repeats (repeatOn never, x-rules)")
    repeat_on: RepeatOn = Field(
        default=RepeatOn.NEVER, description="never; noResponse: after NoResponse; notCorrect: after Incorrect or NoResponse. A probe (target random or noReward) never repeats (x-rules)"
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


class Curriculum(ct.Curriculum):
    """The Mouse Curriculum stage these values are, and the task file's only record of the stage: app-rig-control fills it at
    launch from the share's curriculum file (K:\config\curricula\<repo>\<task file stem>.conditions.yml), its stage's values written over the task
    file's; empty in the repo's task files. Bonsai only records it."""


class UclOpenCoenDomeDecisionMakingTaskParameters(TaskParameters, AutoReward, ct.ConditionedTask):
    """Task parameters for the coen-dome-decision-making audiovisual tasks: localization and two modality discriminations."""

    model_config = ConfigDict(json_schema_extra=ct.schema_extra(SESSION_FIELDS, RULES, TRIAL_TYPES, REQUIRED))
    SESSION_FIELDS = SESSION_FIELDS
    RULES = RULES
    TRIAL_TYPES = TRIAL_TYPES
    REQUIRED = REQUIRED

    task_type: TaskType = Field(
        description=(
            "localization: the answer is a side, at that side's spout. "
            "discriminationGoNoGo: one spout (the left); lick on a go trial (Go), withhold on a noGo trial (NoGo); a noReward trial is a probe, never "
            "rewarded or penalised. discriminationLeftRight: the left spout on a left trial, the right on a right trial. "
            "A target random trial (localization, discriminationLeftRight) is a random-reward probe: both spouts in, and a coin "
            "drawn with the trial from the seeded stream decides a choice at either: heads rewarded at that spout, tails penalised "
            "as an incorrect choice; a miss as a scored trial's miss. The targets each allows are x-rules'"
        ),
    )
    target: Target = Field(
        description=(
            "What a trial rewards: visualSide or auditorySide, the side of the picked grating or sound azimuth; left or right; go "
            "(lick) or noGo (withhold); random, a random-reward probe; noReward, a probe with no reward on offer and no penalty"
        )
    )
    trial_conditions: list[TrialCondition] = Field(
        min_length=1,
        description=(
            "The trial mix: each new trial picks one condition at random with the weights as relative chances, independently of every other "
            "trial (repeats copy the trial they repeat), then draws as the task's values with the condition's set over them. One whole value: a curriculum "
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
    contrasts: list[Double] = Field(default=[1.0], min_length=1, description="Candidate grating contrasts, one drawn per trial; 0 shows no grating (a grey screen)")
    spout_movements: Bool = Field(
        default=True,
        description="Whether the spouts move during trials; false keeps them in (habituation): no withdrawal and no single-spout move for a reward",
    )
    curriculum: Curriculum | None = Field(
        default=None,
        description="The Mouse Curriculum stage these values are, the only record of the stage, filled by app-rig-control at launch; empty or malformed stops the workflow at Go",
    )

    @field_validator("contrasts")
    @classmethod
    def _not_negative(cls, value: list[float]) -> list[float]:
        if any(contrast < 0 for contrast in value):
            raise ValueError("contrasts must each be 0 or more")
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


    @classmethod
    def between_trials(cls, tasks: list[dict]) -> None:
        _between_trials(tasks)


# The list fields one entry of which each trial draws (open_rigs.rigs.conditions.pick_lists).
LIST_FIELDS = UclOpenCoenDomeDecisionMakingTaskParameters.list_fields()


def _between_trials(tasks: list[dict]) -> None:
    """The timing rules between a trial and the next, each drawn from any weighted combination (TrialDraw.BetweenTrials):
    every drinkSeconds and stimulusSeconds within every itiMinSeconds, and with optogenetics laserDurationSeconds within
    the shortest trigger to the next trigger less the ramp-down and Zapit's reply margin."""
    if not tasks:
        return
    s = [task["trialStructure"] for task in tasks]
    iti = min(x["itiMinSeconds"] for x in s)
    for name in ("drinkSeconds", "stimulusSeconds"):
        if (longest := max(x[name] for x in s)) > iti + 1e-9:
            raise ValueError(f"trialStructure.{name} {longest:g} s exceeds trialStructure.itiMinSeconds {iti:g} s of a combination that may come next")
    opto = tasks[0]["optogenetics"]
    if opto["enabled"]:
        leads = [task["optogenetics"]["laserToStimulusSeconds"] for task in tasks]
        longest = (min(lead + x["responseDelaySeconds"] for lead, x in zip(leads, s)) + min(x["itiMinSeconds"] - lead for lead, x in zip(leads, s))
                   - ZAPIT_MAX_RAMP_DOWN_SECONDS - ZAPIT_REPLY_MARGIN_SECONDS)
        if opto["laserDurationSeconds"] > longest + 1e-9:
            raise ValueError(f"optogenetics.laserDurationSeconds ({opto['laserDurationSeconds']} s) must be at most {longest:g} s across the combinations: "
                             "the shortest lead plus response delay, plus the shortest interval less the lead, less the ramp-down and Zapit's reply margin")


class UclOpenCoenDomeDecisionMakingTaskLogic(Task):
    """Task logic definition for the coen-dome-decision-making experiment."""

    version: Literal[__semver__] = __semver__
    name: Literal["UclOpenCoenDomeDecisionMaking"] = Field(default="UclOpenCoenDomeDecisionMaking", description="Name of the task logic", frozen=True)
    task_parameters: UclOpenCoenDomeDecisionMakingTaskParameters = Field(description="Parameters of the task logic")
