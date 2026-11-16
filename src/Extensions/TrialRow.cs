using OpenRigs.Devices;

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

    public static string Outcome(string choice, Trial trial)
    {
        return choice == "NoResponse" ? "NoResponse" : choice == trial.target ? "Correct" : "Incorrect";
    }

    // Penalised (the penalty sound and timeout): an incorrect choice or a miss that was not auto-rewarded.
    public static bool Penalised(string outcome, Trial trial)
    {
        return outcome == "Incorrect" || (outcome == "NoResponse" && !trial.autoReward);
    }

    // The trial's row at its outcome, the choice made now (times.Now: the lick, or the end of the response window,
    // the row's own time too); the shared block's times are the trial loop's (TrialTimes).
    public static TrialRow Build(string choice, Trial trial, TrialTimes times, double timeoutSeconds, bool penaltySound)
    {
        var outcome = Outcome(choice, trial);
        var penalised = Penalised(outcome, trial);
        return new TrialRow
        {
            trialNumber = trial.trialNumber,
            trialType = trial.trialType,
            outcome = outcome,
            itiStartTime = times.IntervalStartTime,
            itiSeconds = times.IntervalSeconds,
            quietWaitSeconds = times.QuietWaitSeconds,
            trialStartTime = times.TrialStartTime,
            stimulusOnsetTime = times.StimulusOnsetTime,
            outcomeTime = times.Now,
            rewardSizeUl = trial.rewardSizeUl,
            penaltySound = penalised && penaltySound,  // the sound comes with every timeout, as in coen-dome-navigation (Pip, 14 Nov 2026)
            penaltyTimeoutSeconds = penalised ? timeoutSeconds : 0.0,
            laserCondition = trial.laserCondition,
            laserPowerMw = times.LaserFired ? trial.laserPowerMw : 0,  // answered before its trigger: no laser (Pip, 13 Nov 2026)
            laserTriggerTime = times.LaserTriggerTime,
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
