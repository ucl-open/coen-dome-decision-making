using Bonsai;
using Bonsai.Harp;
using System;
using System.ComponentModel;
using System.Reactive.Linq;

// One row of BonsaiData_Trial. Each field is a column, named and typed as the lab's list
// (K:\config\data-dictionary.json) allows and as schemas\trial-columns.yml, which says what each
// means, lists it; the order is the file's. The row travels Timestamped, and its Seconds (the Harp
// time the outcome was known: the choice, or the end of the response window) is the first column.
public class TrialRow
{
    public int trialNumber;
    public string trialType;
    public string target;
    public string choice;
    public string outcome;
    public double stimulusOnsetTime;
    public double choiceTime;
    public double rewardSizeUl;
    public bool bothSpouts;
    public int repeatCount;
    public double itiSeconds;
    public double itiStartTime;
    public double quietWaitSeconds;
    public bool punishSound;
    public double punishTimeoutSeconds;
    public double visualAzimuthDeg;
    public double visualElevationDeg;
    public double visualContrast;
    public double visualPhaseRad;
    public double audioAzimuthDeg;
    public double audioElevationDeg;
    public int audioWaveformIndex;
    public int laserCondition;
    public double laserPowerMw;
    public bool autoRewarded;
}

[Combinator]
[Description("Builds the trial's BonsaiData_Trial row when its outcome is known. Input: the choice (Left, Right or NoResponse) stamped with the Harp time it was made, the trial, and the Harp time of its stimulus onset (NaN if none).")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class CreateTrialRow
{
    [Description("The timeout after an incorrect choice or a miss not auto-rewarded, in seconds (punishment.timeoutSeconds).")]
    public double TimeoutSeconds { get; set; }

    [Description("Whether the error sound plays on an incorrect choice (punishment.sound.enabled).")]
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

    public static TrialRow Build(string choice, double choiceTime, Trial trial, double stimulusOnsetTime, double timeoutSeconds, bool punishSound)
    {
        var outcome = Outcome(choice, trial);
        return new TrialRow
        {
            trialNumber = trial.trialNumber,
            trialType = trial.trialType,
            target = trial.target,
            choice = choice,
            outcome = outcome,
            stimulusOnsetTime = stimulusOnsetTime,
            choiceTime = choice == "NoResponse" ? double.NaN : choiceTime,
            rewardSizeUl = trial.rewardSizeUl,
            bothSpouts = trial.bothSpouts,
            repeatCount = trial.repeatCount,
            itiSeconds = trial.itiSeconds,
            itiStartTime = trial.itiStartTime,
            quietWaitSeconds = trial.quietWaitSeconds,
            punishSound = outcome == "Incorrect" && punishSound,
            punishTimeoutSeconds = TimedOut(outcome, trial) ? timeoutSeconds : 0.0,
            visualAzimuthDeg = trial.visualAzimuthDeg,
            visualElevationDeg = trial.visualElevationDeg,
            visualContrast = trial.visualContrast,
            visualPhaseRad = trial.visualPhaseRad,
            audioAzimuthDeg = trial.audioAzimuthDeg,
            audioElevationDeg = trial.audioElevationDeg,
            audioWaveformIndex = trial.audioWaveformIndex,
            laserCondition = trial.laserCondition,
            laserPowerMw = trial.laserPowerMw,
            autoRewarded = trial.autoReward && choice == "NoResponse",
        };
    }
}
