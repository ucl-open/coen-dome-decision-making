using OpenRigs.Devices;
using OpenRigs.Trials;

// One row of WorkflowData_Trials. Each field is a column, named and typed as the Data Dictionary
// (K:\config\data-dictionary.json) allows and as schemas\trial-columns.yml, which says what each
// means, lists it; the order is the file's. The row travels Timestamped at its outcome, which the
// lab's CsvWriter writes first as harpTime. The columns up to autoReward are the lab's shared block (its core with the
// quietWait, optogenetics, conditions, stimulus and autoReward blocks), in coen-dome-navigation's order and words; this
// task's own follow. Words (outcome, target, choice) are in the task file's camelCase.
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
    public double penaltyTimeoutSeconds;
    public bool penalised;
    public int laserCondition;
    public double laserPowerMw;
    public double laserTriggerTime;
    public int trialConditionId;
    public int trialCombinationId;
    public double visualContrast;
    public double visualPhaseRad;
    public double auditoryLevelDbfs;
    public double auditoryInitialAzimuthDeg;
    public double auditoryElevationDeg;
    public bool autoReward;
    public string target;
    public string choice;
    public int repeatCount;
    public bool singleSpoutOffered;
    public double visualInitialAzimuthDeg;
    public int auditorySoundFileIndex;

    // left or right against the target; in discriminationGoNoGo, go or noResponse against go or noGo (a withheld
    // noGo is correct), and a probe's outcome is probe whatever the choice.
    public static string Outcome(string choice, Trial trial)
    {
        switch (trial.target)
        {
            case Target.NoReward:
            case Target.Random: return TrialOutcomes.Probe;
            case Target.Go: return choice == Choice.Go ? TrialOutcomes.Correct : TrialOutcomes.NoResponse;
            case Target.NoGo: return choice == Choice.Go ? TrialOutcomes.Incorrect : TrialOutcomes.Correct;
            default: return choice == Choice.NoResponse ? TrialOutcomes.NoResponse : choice == trial.target ? TrialOutcomes.Correct : TrialOutcomes.Incorrect;
        }
    }

    // Penalised (the penalty sound and timeout): an incorrect choice, or a miss that was not auto-rewarded, except a
    // discriminationGoNoGo miss, which has neither (Song et al. 2017). On a random-reward probe a choice
    // is penalised when its coin came up tails, and a miss as a scored trial's miss, by the same rule.
    public static bool Penalised(string outcome, string choice, Trial trial)
    {
        if (choice == Choice.NoResponse) return MissPenalised(trial);
        if (trial.target == Target.Random) return !trial.probeHeads;
        return outcome == TrialOutcomes.Incorrect;
    }

    // Rewarded (the valve, TrialOutcome.Of): a correct choice but a withheld noGo, an auto-rewarded miss, and a choice on a
    // random trial whose coin came up heads.
    public static bool Rewarded(string outcome, string choice, Trial trial)
    {
        if (trial.target == Target.Random) return choice != Choice.NoResponse && trial.probeHeads;
        return (outcome == TrialOutcomes.Correct && trial.target != Target.NoGo) || (trial.autoReward && choice == Choice.NoResponse);
    }

    // A miss's penalty, on every trial that can be penalised: none when auto-rewarded or on a go trial; never on
    // discriminationGoNoGo's other trials (a withheld noGo is correct, a probe pays nothing and costs nothing).
    static bool MissPenalised(Trial trial)
    {
        if (trial.target == Target.Go || trial.target == Target.NoGo || trial.target == Target.NoReward) return false;
        return !trial.autoReward;
    }

    // The trial's row at its outcome, the choice made now (times.Now: the lick, or the end of the response window,
    // the row's own time too); the shared block's times are the trial loop's (TrialTimes).
    public static TrialRow Build(string choice, Trial trial, TrialTimes times, double timeoutSeconds)
    {
        var outcome = Outcome(choice, trial);
        var penalised = Penalised(outcome, choice, trial);
        // The shared core (trial number, the loop's times, the offer on every trial, rewarded, the penalty, the laser: 0 mW
        // when answered before its trigger) is lib-open-rigs' SharedTrialColumns.Fill; the conditions, stimulus and
        // autoReward blocks are the trial's (ConditionedTrial).
        var row = new TrialRow
        {
            trialType = trial.trialType,
            trialConditionId = trial.trialConditionId,
            trialCombinationId = trial.trialCombinationId,
            visualContrast = trial.visualContrast,
            visualPhaseRad = trial.visualPhaseRad,
            auditoryLevelDbfs = trial.auditoryLevelDbfs,
            auditoryInitialAzimuthDeg = trial.auditoryInitialAzimuthDeg,
            auditoryElevationDeg = trial.auditoryElevationDeg,
            autoReward = trial.autoReward,
            outcome = outcome,
            target = trial.target,
            choice = choice,
            repeatCount = trial.repeatCount,
            singleSpoutOffered = trial.singleSpoutOffered,
            visualInitialAzimuthDeg = trial.visualInitialAzimuthDeg,
            auditorySoundFileIndex = trial.auditorySoundFileIndex,
        };
        return SharedTrialColumns.Fill(row, trial, times, trial.rewardOfferedUl, Rewarded(outcome, choice, trial), penalised, timeoutSeconds);
    }
}
