using Bonsai;
using Bonsai.Harp;
using System;
using System.ComponentModel;
using System.Reactive.Linq;

// One row of BonsaiData_Trial. Each field is a column, named and typed as the lab's list
// (K:\config\data-dictionary.json) allows and as schemas	rial-columns.yml, which says what each
// means, lists it; the order is the file's. The row travels Timestamped at its outcome, which the
// lab's CsvWriter writes first as harpTime. The columns up to laserPowerMw are the dome tasks'
// shared block, in coen-dome-navigation's order and words (Pip, 14 Nov 2026); this task's own follow.
public class TrialRow
{
    public int trialNumber;
    public string trialType;
    public string outcome;
    public double itiStartTime;
    public double itiSeconds;
    public double quietWaitSeconds;
    public double trialStartTime;
    public double stimulusOnsetTime;
    public double outcomeTime;
    public double rewardSizeUl;
    public bool punishSound;
    public double punishTimeoutSeconds;
    public int laserCondition;
    public double laserPowerMw;
    public string target;
    public string choice;
    public int repeatCount;
    public bool autoRewarded;
    public bool bothSpouts;
    public double visualAzimuthDeg;
    public double visualElevationDeg;
    public double visualContrast;
    public double visualPhaseRad;
    public double audioAzimuthDeg;
    public double audioElevationDeg;
    public int audioWaveformIndex;
}

[Combinator]
[Description("Builds the trial's BonsaiData_Trial row when its outcome is known. Input: the choice (Left, Right or NoResponse) stamped with the Harp time it was made, the trial, and the Harp time of its stimulus onset (NaN if none).")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class CreateTrialRow
{
    [Description("The timeout after an incorrect choice or a miss not auto-rewarded, in seconds (punishment.timeoutSeconds).")]
    public double TimeoutSeconds { get; set; }

    [Description("Whether the error sound plays with every timeout: an incorrect choice or a miss not auto-rewarded (punishment.sound.enabled).")]
    public bool PunishSound { get; set; }

    public IObservable<Timestamped<TrialRow>> Process(IObservable<Tuple<Timestamped<string>, Trial, double>> source)
    {
        return source.Select(value => new Timestamped<TrialRow>(Build(value.Item1.Value, value.Item1.Seconds, value.Item2, value.Item3, TimeoutSeconds, PunishSound), value.Item1.Seconds));
    }

    public static string Outcome(string choice, Trial trial)
    {
        return choice == "NoResponse" ? "NoResponse" : choice == trial.target ? "Correct" : "Incorrect";
    }

    // ApplyConsequences: the timeout follows an incorrect choice or a miss that was not auto-rewarded.
    public static bool TimedOut(string outcome, Trial trial)
    {
        return outcome == "Incorrect" || (outcome == "NoResponse" && !trial.autoReward);
    }

    // outcomeTime is the choice's Harp time (the lick, or the end of the response window), the row's own time too.
    public static TrialRow Build(string choice, double outcomeTime, Trial trial, double stimulusOnsetTime, double timeoutSeconds, bool punishSound)
    {
        var outcome = Outcome(choice, trial);
        var timedOut = TimedOut(outcome, trial);
        return new TrialRow
        {
            trialNumber = trial.trialNumber,
            trialType = trial.trialType,
            outcome = outcome,
            itiStartTime = trial.itiStartTime,
            itiSeconds = trial.itiSeconds,
            quietWaitSeconds = trial.quietWaitSeconds,
            trialStartTime = trial.goTime,
            stimulusOnsetTime = stimulusOnsetTime,
            outcomeTime = outcomeTime,
            rewardSizeUl = trial.rewardSizeUl,
            punishSound = timedOut && punishSound,  // the sound comes with every timeout, as in coen-dome-navigation (Pip, 14 Nov 2026)
            punishTimeoutSeconds = timedOut ? timeoutSeconds : 0.0,
            laserCondition = trial.laserCondition,
            laserPowerMw = trial.LaserFiredByOutcome() ? trial.laserPowerMw : 0,  // answered before its trigger: no laser
            target = trial.target,
            choice = choice,
            repeatCount = trial.repeatCount,
            autoRewarded = trial.autoReward && choice == "NoResponse",
            bothSpouts = trial.bothSpouts,
            visualAzimuthDeg = trial.visualAzimuthDeg,
            visualElevationDeg = trial.visualElevationDeg,
            visualContrast = trial.visualContrast,
            visualPhaseRad = trial.visualPhaseRad,
            audioAzimuthDeg = trial.audioAzimuthDeg,
            audioElevationDeg = trial.audioElevationDeg,
            audioWaveformIndex = trial.audioWaveformIndex,
        };
    }
}
