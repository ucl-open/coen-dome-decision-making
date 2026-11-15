using Bonsai;
using Bonsai.Harp;
using System;
using System.ComponentModel;
using System.Reactive.Linq;

// One row of BonsaiData_Trial. Each field is a column, named and typed as the lab's list
// (K:\config\data-dictionary.json) allows and as schemas\trial-columns.yml, which says what each
// means, lists it; the order is the file's. The row travels Timestamped at its outcome, which the
// lab's CsvWriter writes first as harpTime. The columns up to laserTriggerTime are the lab's
// shared block (its core, opto and quietWait), in coen-dome-navigation's order and words (Pip, 14 Nov 2026); this task's own follow.
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
    public bool penaltySound;
    public double penaltyTimeoutSeconds;
    public int laserCondition;
    public double laserPowerMw;
    public double laserTriggerTime;
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
    [Description("The timeout after an incorrect choice or a miss not auto-rewarded, in seconds (penalty.timeoutSeconds).")]
    public double TimeoutSeconds { get; set; }

    [Description("Whether the penalty sound plays with every timeout: an incorrect choice or a miss not auto-rewarded (penalty.sound.enabled).")]
    public bool PenaltySound { get; set; }

    public IObservable<Timestamped<TrialRow>> Process(IObservable<Tuple<Timestamped<string>, Trial, double>> source)
    {
        return source.Select(value => new Timestamped<TrialRow>(Build(value.Item1.Value, value.Item1.Seconds, value.Item2, value.Item3, TimeoutSeconds, PenaltySound), value.Item1.Seconds));
    }

    public static string Outcome(string choice, Trial trial)
    {
        return choice == "NoResponse" ? "NoResponse" : choice == trial.target ? "Correct" : "Incorrect";
    }

    // The timeout follows an incorrect choice or a miss that was not auto-rewarded.
    public static bool TimedOut(string outcome, Trial trial)
    {
        return outcome == "Incorrect" || (outcome == "NoResponse" && !trial.autoReward);
    }

    // outcomeTime is the choice's Harp time (the lick, or the end of the response window), the row's own time too.
    public static TrialRow Build(string choice, double outcomeTime, Trial trial, double stimulusOnsetTime, double timeoutSeconds, bool penaltySound)
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
            penaltySound = timedOut && penaltySound,  // the sound comes with every timeout, as in coen-dome-navigation (Pip, 14 Nov 2026)
            penaltyTimeoutSeconds = timedOut ? timeoutSeconds : 0.0,
            laserCondition = trial.laserCondition,
            laserPowerMw = trial.LaserFiredByOutcome() ? trial.laserPowerMw : 0,  // answered before its trigger: no laser
            laserTriggerTime = trial.LaserFiredByOutcome() ? trial.laserTriggerTime : double.NaN,
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
