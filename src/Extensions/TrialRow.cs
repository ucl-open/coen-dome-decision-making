using OpenRigs.Devices;

// One row of BonsaiData_Trial. Each field is a column, named and typed as the lab's list
// (K:\config\data-dictionary.json) allows and as schemas\trial-columns.yml, which says what each
// means, lists it; the order is the file's. The row travels Timestamped at its outcome, which the
// lab's CsvWriter writes first as harpTime. The columns up to laserTriggerTime are the lab's
// shared block (its core, optogenetics and quietWait), in coen-dome-navigation's order and words (Pip, 14 Nov 2026); this task's own follow.
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
    public double rewardOfferedUl;
    public bool rewarded;
    public bool penaltySound;
    public double penaltyTimeoutSeconds;
    public bool penalised;
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
    public double auditoryAzimuthDeg;
    public double auditoryElevationDeg;
    public int auditoryWaveformIndex;

    // Left or Right against the target; in discriminationGoNoGo, Lick or NoResponse against Go or NoGo (a withheld
    // NoGo is Correct), and a probe's outcome is Probe whatever the choice.
    public static string Outcome(string choice, Trial trial)
    {
        switch (trial.target)
        {
            case Answer.NoReward:
            case Answer.Random: return TrialOutcomes.Probe;
            case Answer.Go: return choice == Choice.Lick ? TrialOutcomes.Correct : TrialOutcomes.NoResponse;
            case Answer.NoGo: return choice == Choice.Lick ? TrialOutcomes.Incorrect : TrialOutcomes.Correct;
            default: return choice == Choice.NoResponse ? TrialOutcomes.NoResponse : choice == trial.target ? TrialOutcomes.Correct : TrialOutcomes.Incorrect;
        }
    }

    // Penalised (the penalty sound and timeout): an incorrect choice, or a miss that was not auto-rewarded, except a
    // discriminationGoNoGo miss, which has neither (Song et al. 2017; Pip, 16 Nov 2026). On a random-reward probe a choice
    // is penalised when its coin came up tails, and a miss as a scored trial's miss, by the same rule (Pip, 16 Nov 2026).
    public static bool Penalised(string outcome, string choice, Trial trial)
    {
        if (choice == Choice.NoResponse) return MissPenalised(trial);
        if (trial.target == Answer.Random) return !trial.probeHeads;
        return outcome == TrialOutcomes.Incorrect;
    }

    // Rewarded (the valve, TrialOutcome.Of): a correct choice but a withheld NoGo, an auto-rewarded miss, and a choice on a
    // Random trial whose coin came up heads.
    public static bool Rewarded(string outcome, string choice, Trial trial)
    {
        if (trial.target == Answer.Random) return choice != Choice.NoResponse && trial.probeHeads;
        return (outcome == TrialOutcomes.Correct && trial.target != Answer.NoGo) || (trial.autoReward && choice == Choice.NoResponse);
    }

    // A miss's penalty, on every trial that can be penalised: none when auto-rewarded or on a Go trial; never on
    // discriminationGoNoGo's other trials (a withheld NoGo is Correct, a probe pays nothing and costs nothing).
    static bool MissPenalised(Trial trial)
    {
        if (trial.target == Answer.Go || trial.target == Answer.NoGo || trial.target == Answer.NoReward) return false;
        return !trial.autoReward;
    }

    // The trial's row at its outcome, the choice made now (times.Now: the lick, or the end of the response window,
    // the row's own time too); the shared block's times are the trial loop's (TrialTimes).
    public static TrialRow Build(string choice, Trial trial, TrialTimes times, double timeoutSeconds, bool penaltySound)
    {
        var outcome = Outcome(choice, trial);
        var penalised = Penalised(outcome, choice, trial);
        // The shared block (trial number, the loop's times, the offer on every trial, rewarded, the penalty, the laser: 0 mW
        // when answered before its trigger, Pip 13 Nov 2026; the sound comes with every timeout, Pip 14 Nov 2026) is
        // lib-open-rigs' SharedTrialColumns.Fill.
        var row = new TrialRow
        {
            trialType = trial.trialType,
            outcome = outcome,
            target = trial.target,
            choice = choice,
            repeatCount = trial.repeatCount,
            autoRewarded = trial.autoReward && choice == Choice.NoResponse,
            bothSpouts = trial.bothSpouts,
            visualAzimuthDeg = trial.visualAzimuthDeg,
            visualElevationDeg = trial.visualElevationDeg,
            visualContrast = trial.visualContrast,
            visualPhaseRad = trial.visualPhaseRad,
            auditoryAzimuthDeg = trial.auditoryAzimuthDeg,
            auditoryElevationDeg = trial.auditoryElevationDeg,
            auditoryWaveformIndex = trial.auditoryWaveformIndex,
        };
        return SharedTrialColumns.Fill(row, trial, times, trial.rewardOfferedUl, Rewarded(outcome, choice, trial), penalised, timeoutSeconds, penaltySound);
    }
}
