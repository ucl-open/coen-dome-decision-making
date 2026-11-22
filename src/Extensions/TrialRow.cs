using OpenRigs.Devices;
using OpenRigs.Trials;
using UclOpenCoenDomeDecisionMakingTask;

// One row of WorkflowData_Trials. Each field is a column, named and typed as the Data Dictionary
// (K:\config\data-dictionary.json) allows and as schemas\trial-columns.yml, which says what each
// means, lists it; the order is the file's. The row travels Timestamped at its outcome, which the
// lab's CsvWriter writes first as harpTime. The columns up to autoReward are the lab's shared block (its core, stimulusOffTime included, with the
// quietWait, trialEnd, optogenetics, conditions, stimulus and autoReward blocks), in coen-dome-navigation's order and words; this
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
    public double stimulusOffTime;
    public double outcomeTime;
    public double trialEndTime;
    public double rewardOfferedUl;
    public bool rewarded;
    public double penaltyTimeoutSeconds;
    public double penaltyFlashBrightness;
    public bool penalised;
    public int laserCondition;
    public string laserPointsMm;
    public double laserPowerMw;
    public double laserTriggerTime;
    public int trialConditionId;
    public int trialCombinationId;
    public double visualInitialContrast;
    public double visualPhaseRad;
    public double auditoryInitialLevelDbfs;
    public double auditoryInitialAzimuthDeg;
    public double auditoryInitialElevationDeg;
    public bool autoReward;
    public string target;
    public string choice;
    public int repeatCount;
    public bool singleSpoutOffered;
    public double visualInitialAzimuthDeg;
    public int auditorySoundFileIndex;

    // left or right against the target, or no choice (NoChoice); in discriminationGoNoGo, go or no choice against go or
    // noGo (a withheld noGo is correct), and a probe's outcome is probe whatever the choice.
    public static string Outcome(string choice, Trial trial)
    {
        switch (trial.target)
        {
            case TargetWords.NoReward:
            case TargetWords.Random: return TrialOutcomes.Probe;
            case TargetWords.Go: return choice == Choice.Go ? TrialOutcomes.Correct : NoChoice(trial);
            case TargetWords.NoGo: return choice == Choice.Go ? TrialOutcomes.Incorrect : TrialOutcomes.Correct;
            default: return choice == Choice.NoResponse ? NoChoice(trial) : choice == trial.target ? TrialOutcomes.Correct : TrialOutcomes.Incorrect;
        }
    }

    // A trial with no choice: auto when the auto-reward answered it (autoReward, never on a probe or a noGo trial), else
    // noResponse.
    static string NoChoice(Trial trial)
    {
        return trial.autoReward ? TrialOutcomes.Auto : TrialOutcomes.NoResponse;
    }

    // Penalised (the penalty's timeout, sound and flash, by its kind): an incorrect choice, or a miss that was not
    // auto-rewarded, except a discriminationGoNoGo miss, which has neither (Song et al. 2017). A random-reward probe only
    // when its condition's penaliseRandomProbes: then a choice when its coin came up tails, and a miss as a scored trial's miss.
    public static bool Penalised(string outcome, string choice, Trial trial)
    {
        if (trial.target == TargetWords.Random && !trial.values.PenaliseRandomProbes) return false;
        if (choice == Choice.NoResponse) return MissPenalised(trial);
        if (trial.target == TargetWords.Random) return !trial.probeHeads;
        return outcome == TrialOutcomes.Incorrect;
    }

    // Rewarded (the valve, TrialOutcome.Of): a correct choice but a withheld noGo, an auto-rewarded miss, and a choice on a
    // random trial whose coin came up heads.
    public static bool Rewarded(string outcome, string choice, Trial trial)
    {
        if (trial.target == TargetWords.Random) return choice != Choice.NoResponse && trial.probeHeads;
        return (outcome == TrialOutcomes.Correct && trial.target != TargetWords.NoGo) || (trial.autoReward && choice == Choice.NoResponse);
    }

    // A miss's penalty, on every trial that can be penalised: none when auto-rewarded or on a go trial; never on
    // discriminationGoNoGo's other trials (a withheld noGo is correct, a probe pays nothing and costs nothing).
    static bool MissPenalised(Trial trial)
    {
        if (trial.target == TargetWords.Go || trial.target == TargetWords.NoGo || trial.target == TargetWords.NoReward) return false;
        return !trial.autoReward;
    }

    // What a penalised trial of a kind costs (lib-open-rigs Penalties.For): a miss (no choice, a probe's too)
    // penalty.noResponse's, a choice penalty.incorrect's, each its timeout, sound level and flash.
    public static ResolvedPenalty Cost(PenaltyKind kind, UclOpenCoenDomeDecisionMakingTaskParameters task)
    {
        var p = task.Penalty;
        var miss = p.NoResponse;
        return Penalties.For(kind, p.Incorrect.TimeoutSeconds, p.Incorrect.Sound.LevelDbfs, p.Incorrect.Flash.Brightness, p.Incorrect.Flash.RateHz,
            miss == null ? (double?)null : miss.TimeoutSeconds, miss == null ? null : miss.Sound.LevelDbfs,
            miss == null ? (double?)null : miss.Flash.Brightness, miss == null ? (double?)null : miss.Flash.RateHz);
    }

    // The trial's row at its outcome, the choice made now (times.Now: the lick, or the end of the response window,
    // the row's own time too), at the cost Cost resolved (null when not penalised); the shared block's times are the
    // trial loop's (TrialTimes).
    public static TrialRow Build(string choice, Trial trial, TrialTimes times, ResolvedPenalty cost)
    {
        var outcome = Outcome(choice, trial);
        var penalised = Penalised(outcome, choice, trial);
        if (penalised && cost == null) throw new System.ArgumentNullException("cost", "A penalised trial's row needs its cost.");
        // The shared core (trial number, the loop's times, the offer on every trial, rewarded, the penalty, the laser: 0 mW
        // when answered before its trigger) is lib-open-rigs' SharedTrialColumns.Fill; the conditions, stimulus and
        // autoReward blocks are the trial's (ConditionedTrial).
        var row = new TrialRow
        {
            trialType = trial.trialType,
            trialConditionId = trial.trialConditionId,
            trialCombinationId = trial.trialCombinationId,
            visualInitialContrast = trial.visualInitialContrast,
            visualPhaseRad = trial.visualPhaseRad,
            auditoryInitialLevelDbfs = trial.auditoryInitialLevelDbfs,
            auditoryInitialAzimuthDeg = trial.auditoryInitialAzimuthDeg,
            auditoryInitialElevationDeg = trial.auditoryInitialElevationDeg,
            autoReward = trial.autoReward,
            outcome = outcome,
            target = trial.target,
            choice = choice,
            repeatCount = trial.repeatCount,
            singleSpoutOffered = trial.singleSpoutOffered,
            visualInitialAzimuthDeg = trial.visualInitialAzimuthDeg,
            auditorySoundFileIndex = trial.auditorySoundFileIndex,
        };
        SharedTrialColumns.Fill(row, trial, times, trial.rewardOfferedUl, Rewarded(outcome, choice, trial), penalised,
            penalised ? cost.TimeoutSeconds : 0, penalised ? cost.Flash.Brightness : 0);
        return row;
    }
}
