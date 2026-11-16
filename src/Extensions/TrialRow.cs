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

    // Left or Right against the target; in discriminationGoNoGo, Lick or NoResponse against Go or NoGo (a withheld
    // NoGo is Correct), and a probe's outcome is Probe whatever the choice.
    public static string Outcome(string choice, Trial trial)
    {
        switch (trial.target)
        {
            case Answer.Probe: return Answer.Probe;
            case Answer.Go: return choice == Choice.Lick ? "Correct" : "NoResponse";
            case Answer.NoGo: return choice == Choice.Lick ? "Incorrect" : "Correct";
            default: return choice == Choice.NoResponse ? "NoResponse" : choice == trial.target ? "Correct" : "Incorrect";
        }
    }

    // Penalised (the penalty sound and timeout): an incorrect choice, or a miss that was not auto-rewarded, except a
    // discriminationGoNoGo miss, which has neither (Song et al. 2017; Pip, 16 Nov 2026).
    public static bool Penalised(string outcome, Trial trial)
    {
        return outcome == "Incorrect" || (outcome == "NoResponse" && !trial.autoReward && trial.target != Answer.Go);
    }

    // The trial's row at its outcome, the choice made now (times.Now: the lick, or the end of the response window,
    // the row's own time too); the shared block's times are the trial loop's (TrialTimes).
    public static TrialRow Build(string choice, Trial trial, TrialTimes times, double timeoutSeconds, bool penaltySound)
    {
        var outcome = Outcome(choice, trial);
        var penalised = Penalised(outcome, trial);
        // The shared block (trial number, the loop's times, the penalty, the laser: 0 mW when answered before its trigger,
        // Pip 13 Nov 2026; the sound comes with every timeout, Pip 14 Nov 2026) is lib-open-rigs' SharedTrialColumns.Fill.
        var row = new TrialRow
        {
            trialType = trial.trialType,
            outcome = outcome,
            rewardSizeUl = trial.rewardSizeUl,
            target = trial.target,
            choice = choice,
            repeatCount = trial.repeatCount,
            autoRewarded = trial.autoReward && choice == Choice.NoResponse,
            bothSpouts = trial.bothSpouts,
            visualAzimuthDeg = trial.visualAzimuthDeg,
            visualElevationDeg = trial.visualElevationDeg,
            visualContrast = trial.visualContrast,
            visualPhaseRad = trial.visualPhaseRad,
            audioAzimuthDeg = trial.audioAzimuthDeg,
            audioElevationDeg = trial.audioElevationDeg,
            audioWaveformIndex = trial.audioWaveformIndex,
        };
        return SharedTrialColumns.Fill(row, trial, times, penalised, timeoutSeconds, penaltySound);
    }
}
