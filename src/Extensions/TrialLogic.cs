using Bonsai;
using Bonsai.Harp;
using OpenRigs.Audio;
using OpenRigs.Core;
using OpenRigs.Devices;
using OpenRigs.Devices.Rigs;
using OpenRigs.Trials;
using OpenRigs.Zapit;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using UclOpenCoenDomeDecisionMakingTask;
using YamlDotNet.RepresentationModel;
using ConditionTarget = UclOpenCoenDomeDecisionMakingTask.Target;
using LabSpouts = OpenRigs.Trials.Spouts;
using BuiltTrialCondition = OpenRigs.Trials.BuiltTrialCondition<UclOpenCoenDomeDecisionMakingTask.UclOpenCoenDomeDecisionMakingTaskParameters>;

// The tests build a trial by hand with its combination's values (Trial.values), as every drawn trial has them.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Extensions.Tests")]

/// <summary>One trial as drawn: the lab's conditioned trial (OpenRigs.Trials ConditionedTrial: trialNumber, trialType,
/// trialConditionId, trialCombinationId, itiSeconds, rewardOfferedUl, the stimulus, the laser, autoReward) and this task's
/// own; its fields are named as the trial log's columns where they are one. Every other value is its combination's
/// (values), read per trial.</summary>
public class Trial : ConditionedTrial, ILaserDurationTrial, ISpoutTrial
{
    double ILaserDurationTrial.LaserDurationSeconds { get { return values.Optogenetics.LaserDurationSeconds; } }

    public string target;              // left or right; in discriminationGoNoGo go or noGo; on a trial with no right answer noReward or random (TargetWords)
    public bool singleSpoutOffered;    // only the target's spout came in (TrialDraw: go/no-go, or a scored trial with the spouts moving and the draw)
    public int repeatCount;            // 0 for a new draw
    public double visualInitialAzimuthDeg;  // NaN with no grating (visualInitialContrast 0), as the sound's when none plays
    public double gratingAzimuthDeg;   // what the renderer starts the grating at (not logged): visualInitialAzimuthDeg, 0 with no grating (hidden at contrast 0)
    public int auditorySoundFileIndex;
    public bool probeHeads;            // a random trial's coin: heads rewards a choice, tails does not (penalised only with penaliseRandomProbes)
    internal UclOpenCoenDomeDecisionMakingTaskParameters values;  // its combination's values, which the rules read per trial

    /// <summary>The grating's shape as the renderer draws it (initialElevationDeg, initialSigmaDeg, initialOrientationDeg, initialSpatialFrequencyCpd,
    /// initialTemporalFrequencyHz): its combination's visualStimulus, read per trial, not copied.</summary>
    public VisualStimulus VisualStimulus { get { return values.VisualStimulus; } }

    /// <summary>What the grating and the sound share (durationSeconds, responseToStimulusOffSeconds, rateHz): its combination's stimulus.</summary>
    public Stimulus Stimulus { get { return values.Stimulus; } }

    /// <summary>Whether the used spouts are always in on this trial (its combination's spouts.alwaysIn).</summary>
    public bool SpoutsAlwaysIn { get { return values.Spouts.AlwaysIn; } }

    /// <summary>Where the spouts go at the SpoutsIn cue: the target's alone on a single-spout trial (go/no-go: the left), else both in.</summary>
    public LickSpoutCommand SpoutsInCommand
    {
        get { return singleSpoutOffered ? LabSpouts.Only(target == TargetWords.Right ? "right" : "left") : LickSpoutCommand.SpoutsIn; }
    }

    public Trial Clone()
    {
        return (Trial)MemberwiseClone();
    }
}

[Combinator]
[Description("Reads the task file's text into the task (OpenRigs.Core TaskYaml.Read): as DeserializeFromYaml, but a trial condition's set values keep their YAML types (numbers, bools), so Settings_Task logs them as such.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class ReadTask
{
    public IObservable<UclOpenCoenDomeDecisionMakingTaskLogic> Process(IObservable<string> source)
    {
        return source.Select(TaskYaml.Read<UclOpenCoenDomeDecisionMakingTaskLogic>);
    }
}

[Combinator]
[Description("Checks the task at Go and gives the trials' rules (TrialRules, tested C#): curriculum is present and well formed, each trial condition builds and the draw's rules hold on rig.yml's speakers for the task and every condition (TrialDraw.CheckTask), and with optogenetics the session's Zapit bank (its conditions drawn from, its longest ramp-down). Inputs: the task file's text and rig.yml; Zapit.Bank (none on a rig without Zapit or before the setup loads one). Stops the workflow naming every problem.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class CheckTaskAtGo
{
    public IObservable<TrialRules> Process(IObservable<Tuple<string, LabRig>> source, IObservable<ZapitBank> bank)
    {
        // The bank as loaded at Go, null when none was: a rig without Zapit never loads one, and needs none without optogenetics.
        // Zapit.Bank is a BehaviorSubject, subscribed first, so its last bank is in hand before the task text comes, once.
        return bank.StartWith((ZapitBank)null).CombineLatest(source, (loaded, input) => Tuple.Create(input, loaded)).Take(1)
            .Select(x => TrialRules.Create(x.Item1.Item1, x.Item1.Item2, x.Item2));
    }
}

/// <summary>Every word the target column holds, in the task file's spelling: the sides left and right, discriminationGoNoGo's go
/// and noGo, and on a trial with no right answer (a probe, outcome probe) noReward (never rewarded or penalised) or random
/// (a choice rewarded on a coin's heads; penalised on tails only with penaliseRandomProbes).</summary>
public static class TargetWords
{
    public const string Left = "left";
    public const string Right = "right";
    public const string Go = "go";
    public const string NoGo = "noGo";
    public const string NoReward = "noReward";
    public const string Random = "random";
}

/// <summary>What the mouse chose (the choice column), in the target's spelling: left or right, in discriminationGoNoGo go
/// (thresholdLicks at the one spout), and noResponse when the window closed with none.</summary>
public static class Choice
{
    public const string Left = "left";
    public const string Right = "right";
    public const string Go = "go";
    public const string NoResponse = "noResponse";
}

/// <summary>The draw's state and rules, apart from Bonsai so a test can run them.</summary>
public class TrialDraw
{
    /// <summary>What the draw reads from the task schema (TaskSchema.Generated.cs).</summary>
    public static readonly ConditionsSchema Schema = new ConditionsSchema(TaskSchema.SessionFields, TaskSchema.ListFields, TaskSchema.MaxCombinations, TaskSchema.TrialTypes);

    readonly UclOpenCoenDomeDecisionMakingTaskParameters task;
    readonly List<BuiltTrialCondition> conditions;
    readonly EqualisedSpeakerArray speakers;
    readonly Random random;
    readonly ZapitBank bank;   // null without optogenetics
    Trial last;
    int drawn;

    /// <summary>The seed the random stream started from: the task's rngSeed, or one drawn when it is null.</summary>
    public int Seed { get; private set; }

    /// <summary>The task as the file gives it, before any condition's set.</summary>
    public UclOpenCoenDomeDecisionMakingTaskParameters Task { get { return task; } }

    /// <summary>The conditions as built at Go, in trialConditions' order.</summary>
    public IList<BuiltTrialCondition> Conditions { get { return conditions; } }

    /// <summary>The Zapit bank's longest ramp-down (ZapitBank.LongestRampSeconds), in seconds (0 without optogenetics): the
    /// next trial is armed once it has played after the laser.</summary>
    public double MaxRampDownSeconds { get; private set; }

    /// <summary>How long ZapitDaq stays busy after the laser's end, in seconds (0 without optogenetics): ZapitDaqTrial.FreeAfterLaserSeconds,
    /// plus rig.yml's earlyStopLeadSeconds when an early stop ends it (laserEnd stimulusOff).</summary>
    public double FreeAfterLaserSeconds { get; private set; }

    /// <summary>The draw of a task file's text (as rig-control wrote it at launch) on a rig's speakers, with the session's
    /// Zapit bank (null without one: refused with optogenetics on). Throws naming every problem, with optogenetics a laser
    /// that could leave Zapit busy at the next trigger (LaserProblems).</summary>
    public TrialDraw(string taskText, EqualisedSpeakerArray speakers, ZapitBank bank, double earlyStopLeadSeconds = 0)
    {
        conditions = CheckTask(taskText, speakers, out task);
        this.speakers = speakers;
        var optogenetics = task.Optogenetics;
        if (optogenetics.Enabled)
        {
            if (!(optogenetics.LaserPowerMw > 0))
            {
                throw new InvalidOperationException("optogenetics is enabled, so optogenetics.laserPowerMw must be a power in mW above 0.");
            }
            if (bank == null)
            {
                throw new InvalidOperationException("optogenetics is enabled, but no Zapit bank was loaded before Go: run Zapit's setup (scanner, sample, bank) in app-rig-control, or turn optogenetics off.");
            }
            this.bank = bank;
            MaxRampDownSeconds = bank.LongestRampSeconds;
            FreeAfterLaserSeconds = ZapitDaqTrial.FreeAfterLaserSeconds(bank) + (optogenetics.LaserEnd == LaserEndMode.StimulusOff ? earlyStopLeadSeconds : 0);
            var problems = LaserProblems(conditions, FreeAfterLaserSeconds);
            if (problems.Count > 0) throw new InvalidOperationException("The task cannot be run: " + string.Join("; ", problems) + ".");
        }
        Seed = RngSeed.Resolve(task.RngSeed);
        random = new Random(Seed);
    }

    /// <summary>The task the text holds and its conditions as drawn (OpenRigs.Trials TrialConditions.Build); throws unless the task file's curriculum (the
    /// only record of the stage, which app-rig-control fills at launch) is present and well formed and the task and every condition can be run on
    /// these speakers, naming every problem.</summary>
    public static List<BuiltTrialCondition> CheckTask(string taskText, EqualisedSpeakerArray speakers, out UclOpenCoenDomeDecisionMakingTaskParameters task)
    {
        task = TaskYaml.Read<UclOpenCoenDomeDecisionMakingTaskLogic>(taskText).TaskParameters;
        var curriculumProblem = CurriculumCheck.Problem(task.Curriculum);
        if (curriculumProblem != null) throw new InvalidOperationException("The task file's curriculum " + curriculumProblem + ": launch it from app-rig-control, which writes the stage's values and fills it.");
        var problems = new List<string>();
        Check(problems, task, speakers);
        if (task.Target != null) problems.Add("target is each trial condition's (its set's target), not the task's");
        var built = problems.Count == 0 ? TrialConditions.Build(taskText, task, t => TaskYaml.Read<UclOpenCoenDomeDecisionMakingTaskLogic>(t).TaskParameters, Schema, problems) : null;
        if (built != null)
        {
            BetweenTrials(problems, built.Where(c => c.Weight > 0).SelectMany(c => c.Combinations).Select(m => m.Task).ToList());
            // Every combination of every condition, each problem named by its condition (a value the task's own check
            // already named is said once).
            foreach (var condition in built)
            {
                foreach (var combination in condition.Combinations)
                {
                    var own = new List<string>();
                    Check(own, combination.Task, speakers);
                    var target = combination.Task.Target;
                    if (target == null) own.Add("needs a target in its set");
                    else if ((target == ConditionTarget.Random || target == ConditionTarget.NoReward) && combination.Task.Repeats.RepeatOn == RepeatOn.NotCorrect)
                    {
                        // The condition's, not the combination's: said once however many combinations it has
                        problems.Add(condition.Where + ": a probe (target random or noReward) needs repeats.repeatOn never or noResponse, not notCorrect");
                    }
                    problems.AddRange(own.Select(p => condition.Where + (combination.Picks.Length == 0 ? "" : " with " + combination.Picks) + ": " + p));
                }
            }
        }
        if (problems.Count > 0) throw new InvalidOperationException("The task cannot be run: " + string.Join("; ", problems.Distinct()) + ".");
        return built;
    }

    /// <summary>The timing rules between a trial and the next, each drawn from any weighted combination (task.py
    /// _between_trials): the lab's spouts (OpenRigs.Trials Spouts.BetweenTrialsProblems: the longest rewardConsumptionSeconds
    /// plus the longest lead within the shortest itiMinSeconds) and stimulus off between trials
    /// (StimulusTiming.BetweenTrialsProblems: the soonest outcome the response window's opening, the shortest outcome to next
    /// interval 0, MinOutcomeToItiSeconds), and with optogenetics the laser (LaserProblems, once the bank's
    /// ramp-down is known).</summary>
    static void BetweenTrials(List<string> problems, List<UclOpenCoenDomeDecisionMakingTaskParameters> tasks)
    {
        if (tasks.Count == 0) return;
        var iti = tasks.Select(t => t.TrialStructure.ItiMinSeconds).ToList();
        problems.AddRange(LabSpouts.BetweenTrialsProblems(tasks.Select(t => t.Spouts.StimulusToSpoutsInSeconds), tasks.Select(t => t.Spouts.AlwaysIn),
            tasks.Select(t => t.Spouts.RewardConsumptionSeconds), iti));
        problems.AddRange(StimulusTiming.BetweenTrialsProblems(tasks.Select(t => t.Stimulus.DurationSeconds), tasks.Select(t => t.TrialStructure.StimulusToResponseWindowSeconds),
            tasks.Select(t => t.Stimulus.ResponseToStimulusOffSeconds), iti, MinOutcomeToItiSeconds));
    }

    /// <summary>With optogenetics, the laser of every combination (task.py's same rules, looser before Go: it has the bank's
    /// ramp-down at most): each laserDurationSeconds within MaxLaserSeconds, and across the weighted combinations within the shortest
    /// lead plus stimulus to response window plus the shortest interval less the lead, less what a ZapitDaq trial outlasts its
    /// laser (<paramref name="freeAfterLaserSeconds"/>, ZapitDaqTrial.FreeAfterLaserSeconds: a scan cycle, the bank's longest
    /// ramp-down and the free margin) and Zapit's arm time, so Zapit is free at the next trigger.</summary>
    public static List<string> LaserProblems(IList<BuiltTrialCondition> conditions, double freeAfterLaserSeconds)
    {
        var problems = new List<string>();
        foreach (var condition in conditions)
        {
            foreach (var combination in condition.Combinations)
            {
                var opto = combination.Task.Optogenetics;
                var maxSeconds = MaxLaserSeconds(combination.Task, freeAfterLaserSeconds);
                if (opto.Enabled && opto.LaserDurationSeconds > maxSeconds + 1e-9)
                {
                    problems.Add(string.Format("{0}optogenetics.laserDurationSeconds ({1} s) must be at most {2} s (stimulusToResponseWindowSeconds + itiMinSeconds - the {3} s a Zapit trial outlasts its laser (a scan cycle, the bank's ramp-down and a {5} s margin) - {4} s arm time), so Zapit can be armed for the next trial in time",
                        condition.Where + (combination.Picks.Length == 0 ? "" : " with " + combination.Picks) + ": ", opto.LaserDurationSeconds, maxSeconds, freeAfterLaserSeconds, ZapitDaqTrial.ArmSeconds, ZapitDaqTrial.FreeMarginSeconds));
                }
            }
        }
        var tasks = conditions.Where(c => c.Weight > 0).SelectMany(c => c.Combinations).Select(m => m.Task).ToList();
        if (tasks.Count > 0 && tasks[0].Optogenetics.Enabled)
        {
            var across = tasks.Min(t => t.Optogenetics.LaserToStimulusSeconds + t.TrialStructure.StimulusToResponseWindowSeconds) + tasks.Min(t => t.TrialStructure.ItiMinSeconds - t.Optogenetics.LaserToStimulusSeconds)
                - freeAfterLaserSeconds - ZapitDaqTrial.ArmSeconds;
            var duration = tasks.Max(t => t.Optogenetics.LaserDurationSeconds);
            if (duration > across + 1e-9) problems.Add(string.Format("optogenetics.laserDurationSeconds ({0} s) must be at most {1} s across the combinations: the shortest lead plus stimulus to response window, plus the shortest interval less the lead, less the {2} s a Zapit trial outlasts its laser (a scan cycle, the bank's ramp-down and a margin) and Zapit's arm time", duration, across, freeAfterLaserSeconds));
        }
        return problems.Distinct().ToList();
    }

    /// <summary>The shortest outcome to next interval a trial can have (OpenRigs.Trials StimulusTiming: the shortest penalty
    /// timeout among the outcomes that can occur, 0 where a reward or neither can come): 0 in every task here, as every trial
    /// can end rewarded or neither (a correct choice, a random probe's heads, a go/no-go miss or withheld noGo, a no-reward
    /// probe), and a rewarded trial's interval starts at its end (task.py's same 0). Its end is the response window's
    /// (responseWindowAlwaysFull), never sooner than the outcome, so the checks built on it hold with room to spare.</summary>
    public const double MinOutcomeToItiSeconds = 0;

    // Each penalty kind's sound level: a number of dBFS RMS or mute (NaN), at most the peak limit.
    static void PenaltyLevel(List<string> problems, string block, PenaltyCost cost)
    {
        if (cost.Sound == null) { problems.Add(block + ".sound must be given"); return; }
        double level = double.NaN;
        try { level = SoundLevels.Parse(cost.Sound.LevelDbfs); }
        catch (InvalidOperationException e) { problems.Add(block + ".sound.levelDbfs: " + e.Message); }
        if (level > SoundLevel.PeakLimitDbfs) problems.Add(string.Format("{0}.sound.levelDbfs {1} is above {2} dBFS", block, level, SoundLevel.PeakLimitDbfs));
        if (!(cost.TimeoutSeconds >= 0) || double.IsInfinity(cost.TimeoutSeconds)) problems.Add(block + ".timeoutSeconds must be a finite number, 0 or more");
        if (cost.Flash == null) problems.Add(block + ".flash must be given");
    }

    // Bonsai reads the task file without pydantic, so what the draw needs is checked again here; x-rules are Python's alone
    // (task.py in check-workflow, app-rig-control at launch).
    static void Check(List<string> problems, UclOpenCoenDomeDecisionMakingTaskParameters task, EqualisedSpeakerArray speakers)
    {
        if (task.Repeats == null || task.Spouts == null || task.Stimulus == null || task.AuditoryStimulus == null || task.VisualStimulus == null)
        {
            problems.Add("repeats, spouts, stimulus, visualStimulus and auditoryStimulus must be given");
            return;
        }
        if (task.Repeats.MaxRepeats < 0) problems.Add("repeats.maxRepeats must be 0 or more");
        // x-rules (task.py): never exactly when maxRepeats is 0, checked here too, as Go is the last check of an edited task file.
        else if (task.Repeats.RepeatOn == RepeatOn.Never && task.Repeats.MaxRepeats > 0) problems.Add("repeats.repeatOn never needs repeats.maxRepeats 0");
        else if (task.Repeats.RepeatOn != RepeatOn.Never && task.Repeats.MaxRepeats == 0) problems.Add("repeats.maxRepeats 0 needs repeats.repeatOn never");
        var visual = task.VisualStimulus;
        var contrasts = visual.InitialContrasts ?? new List<double>();
        if (contrasts.Count == 0 || contrasts.Any(c => !(c >= 0) || double.IsInfinity(c))) problems.Add("visualStimulus.initialContrasts must each be a finite number, 0 or more, at least one");
        // The sound's level: a number of dBFS RMS or mute (NaN), at most the peak limit.
        double level = double.NaN;
        try { level = SoundLevels.Parse(task.AuditoryStimulus.InitialLevelDbfs); }
        catch (InvalidOperationException e) { problems.Add("auditoryStimulus.initialLevelDbfs: " + e.Message); }
        if (level > SoundLevel.PeakLimitDbfs) problems.Add(string.Format("auditoryStimulus.initialLevelDbfs {0} is above {1} dBFS", level, SoundLevel.PeakLimitDbfs));
        // Each penalty kind's own full values (lib-open-rigs Penalties): penalty.incorrect always; penalty.noResponse null
        // exactly when no miss is penalised (Penalties.Problems). discriminationGoNoGo never penalises a miss, so a
        // penalty.noResponse there would never be used (task.py's x-rule).
        if (task.Penalty == null || task.Penalty.Incorrect == null) problems.Add("penalty.incorrect must be given");
        else PenaltyLevel(problems, "penalty.incorrect", task.Penalty.Incorrect);
        var miss = task.Penalty == null ? null : task.Penalty.NoResponse;
        if (miss != null) PenaltyLevel(problems, "penalty.noResponse", miss);
        if (task.TaskType == TaskType.DiscriminationGoNoGo && miss != null) problems.Add("penalty.noResponse must be null in discriminationGoNoGo, which never penalises a miss");
        problems.AddRange(Penalties.Problems(task.TaskType != TaskType.DiscriminationGoNoGo && task.AutoRewardProbability < 1, miss != null));
        // The reward tone (task.py's same rule): this workflow has no tone sound to play yet, so only mute.
        double tone = double.NaN;
        try { if (task.Reward != null && task.Reward.Tone != null) tone = SoundLevels.Parse(task.Reward.Tone.LevelDbfs); }
        catch (InvalidOperationException e) { problems.Add("reward.tone.levelDbfs: " + e.Message); }
        if (!double.IsNaN(tone)) problems.Add("reward.tone.levelDbfs must be mute: this workflow has no reward tone sound to play yet");
        bool sounding = !double.IsNaN(level);
        // The positions (task.py's same rules): each grating azimuth signed and never 0 (its sign is the side); each sound
        // azimuth's nearest speaker, at auditoryStimulus.initialElevationDeg, on its side (0: any).
        if (!(visual.InitialElevationDeg >= -90 && visual.InitialElevationDeg <= 90)) problems.Add("visualStimulus.initialElevationDeg must be in [-90, 90]");
        var elevation = task.AuditoryStimulus.InitialElevationDeg;
        bool elevationOk = elevation >= -90 && elevation <= 90;
        if (!elevationOk) problems.Add("auditoryStimulus.initialElevationDeg must be in [-90, 90]");
        if (visual.InitialAzimuthsDeg == null || visual.InitialAzimuthsDeg.Count == 0) problems.Add("visualStimulus.initialAzimuthsDeg needs an azimuth");
        foreach (var listed in visual.InitialAzimuthsDeg ?? new List<double>())
        {
            if (!(listed >= -180 && listed <= 180)) problems.Add(string.Format("visualStimulus.initialAzimuthsDeg value {0} is outside [-180, 180]", listed));
            else if (listed == 0) problems.Add("visualStimulus.initialAzimuthsDeg must not hold 0: the sign is the trial's side");
        }
        var sounds = task.AuditoryStimulus.InitialAzimuthsDeg;
        if (sounds == null || sounds.Count == 0) problems.Add("auditoryStimulus.initialAzimuthsDeg needs an azimuth");
        foreach (var item in sounds ?? new List<object>())
        {
            var read = SoundAzimuths.Parse(item);
            if (read == null) { problems.Add(string.Format("auditoryStimulus.initialAzimuthsDeg value {0} is neither a number nor {1}", item, SoundAzimuths.AllSpeakers)); continue; }
            var azimuth = read.Value;
            if (double.IsNaN(azimuth))
            {
                if (sounding && (speakers == null || speakers.Speakers == null || speakers.Speakers.Count == 0)) problems.Add("rig.yml has no speakerArray.speakers to play auditoryStimulus.initialAzimuthsDeg all on");
            }
            else if (!(azimuth >= -180 && azimuth <= 180)) problems.Add(string.Format("auditoryStimulus.initialAzimuthsDeg value {0} is outside [-180, 180]", azimuth));
            else if (azimuth != 0 && elevationOk && sounding)
            {
                var speaker = SoundAzimuths.NearestSpeaker(speakers, azimuth, elevation);
                if (azimuth > 0 ? speaker.Value.Position.Azimuth <= 0 : speaker.Value.Position.Azimuth >= 0)
                {
                    problems.Add(string.Format("rig.yml's speaker nearest azimuth {0} (elevation {1}) is '{2}', not on the {3}", azimuth, elevation, speaker.Key, azimuth > 0 ? "right" : "left"));
                }
            }
        }
        var structure = task.TrialStructure;
        // A NaN or infinite time (YAML's .nan or .inf) would hold the trial start for ever, with no error.
        Action<string, double> finite = (name, value) =>
        {
            if (!(value >= 0) || double.IsInfinity(value)) problems.Add(name + " must be a finite number, 0 or more");
        };
        Action<string, double> positive = (name, value) =>
        {
            if (!(value > 0) || double.IsInfinity(value)) problems.Add(name + " must be a finite number above 0");
        };
        // The lab's blocks, checked by their one C# check each (OpenRigs.Core TaskBlocks, QuietWait).
        foreach (var p in TaskBlocks.ItiProblems(structure.ItiMinSeconds, structure.ItiMaxSeconds)) problems.Add("trialStructure." + p);
        finite("trialStructure.stimulusToResponseWindowSeconds", structure.StimulusToResponseWindowSeconds);
        if (structure.ResponseWindowSeconds == null) problems.Add("trialStructure.responseWindowSeconds must be given: this task's response window always closes");
        foreach (var p in TaskBlocks.ResponseWindowProblems(structure.ResponseWindowSeconds)) problems.Add("trialStructure." + p);
        if (!structure.ResponseWindowAlwaysFull) problems.Add("trialStructure.responseWindowAlwaysFull must be true: this task's response window always runs in full");
        foreach (var p in TaskBlocks.AutoRewardProblems(task.AutoRewardProbability)) problems.Add(p);
        positive("stimulus.rateHz", task.Stimulus.RateHz);
        if (task.Stimulus.RateHz > 1 / AuditoryStimulusSet.PulseSeconds + 1e-9) problems.Add(string.Format("stimulus.rateHz {0} is above {1}: each pulse is {2} s, so they would overlap", task.Stimulus.RateHz, 1 / AuditoryStimulusSet.PulseSeconds, AuditoryStimulusSet.PulseSeconds));
        // The quiet waits, the lab's one check each (OpenRigs.Core QuietWait: finite, 0 or more, at most 10 s and 20 cm, the
        // wheel's distance 0 no movement at all): a long wait and a licking mouse would hold the trial start with no message.
        foreach (var p in QuietWait.LickProblems(structure.LickQuietSeconds)) problems.Add("trialStructure." + p);
        foreach (var p in QuietWait.WheelProblems(structure.WheelQuietSeconds, structure.WheelQuietCm)) problems.Add("trialStructure." + p);
        if (double.IsNaN(task.Optogenetics.LaserToStimulusSeconds) || double.IsInfinity(task.Optogenetics.LaserToStimulusSeconds)) problems.Add("optogenetics.laserToStimulusSeconds must be a finite number");
        if (task.Optogenetics.Enabled) positive("optogenetics.laserDurationSeconds", task.Optogenetics.LaserDurationSeconds);
        // The lab's stimulus off and spouts blocks, by their one C# check each (OpenRigs.Trials StimulusTiming, Spouts): the
        // soonest outcome the response window's opening, the shortest outcome to next interval 0 (MinOutcomeToItiSeconds).
        var responseEnd = structure.StimulusToResponseWindowSeconds + (structure.ResponseWindowSeconds ?? double.PositiveInfinity);
        problems.AddRange(StimulusTiming.Problems(task.Stimulus.DurationSeconds, task.Stimulus.ResponseToStimulusOffSeconds, structure.ItiMinSeconds, responseEnd,
            structure.StimulusToResponseWindowSeconds, MinOutcomeToItiSeconds));
        problems.AddRange(LabSpouts.Problems(task.Spouts.StimulusToSpoutsInSeconds, task.Spouts.AlwaysIn, task.Spouts.RewardConsumptionSeconds, structure.ItiMinSeconds,
            structure.StimulusToResponseWindowSeconds));
        var opto = task.Optogenetics;
        var lead = opto.LaserToStimulusSeconds;
        var leadProblem = ZapitDaqTrial.LeadProblem(lead, structure.ItiMinSeconds);
        if (leadProblem != null) problems.Add(leadProblem);
        if (-lead > structure.StimulusToResponseWindowSeconds + structure.ResponseWindowSeconds + 1e-9)
        {
            problems.Add(string.Format("optogenetics.laserToStimulusSeconds ({0} s) puts the trigger after the response window (stimulusToResponseWindowSeconds + responseWindowSeconds, {1} s)", lead, structure.StimulusToResponseWindowSeconds + structure.ResponseWindowSeconds));
        }
        // laserEnd stimulusOff (task.py's same rules): the stimulus's own off, durationSeconds after its onset, after the trigger
        // and within the laser's cap. Only that off is known at Go: one that follows the answer (responseToStimulusOffSeconds, the
        // earlier of the two) is not checked, and the cap ends a laser still on then.
        var duration = task.Stimulus.DurationSeconds;
        if (opto.Enabled && opto.LaserEnd == LaserEndMode.StimulusOff && duration.HasValue)
        {
            if (-lead >= duration.Value - 1e-9)
                problems.Add(string.Format("optogenetics.laserToStimulusSeconds ({0} s) triggers the laser {1} s after the stimulus onset, when the stimulus (stimulus.durationSeconds {2} s) is already off: with optogenetics.laserEnd stimulusOff the laser would stop as it starts", lead, -lead, duration.Value));
            else if (lead + duration.Value > opto.LaserDurationSeconds + 1e-9)
                problems.Add(string.Format("optogenetics.laserDurationSeconds ({0} s) is the laser's cap with optogenetics.laserEnd stimulusOff, but the stimulus goes off {1} s after the trigger (optogenetics.laserToStimulusSeconds {2} s + stimulus.durationSeconds {3} s): the cap would end the laser before the stimulus off", opto.LaserDurationSeconds, lead + duration.Value, lead, duration.Value));
        }
        // Each RewardValve piece refuses a reward over MaxRewardUl when it plans it, mid-session (task.py's same rule).
        var reward = task.Reward;
        problems.AddRange(TaskBlocks.LargeRewardProblems(reward.SizeUl, reward.LargeMultiplier, reward.LargeProbability, LoadRewardLine.DefaultMaxRewardUl).Select(p => "reward." + p));
        // The condition fields' bounds (a condition's cell is not checked by pydantic before Go).
        problems.AddRange(TaskBlocks.RewardSizeProblems(reward.SizeUl, LoadRewardLine.DefaultMaxRewardUl).Select(p => "reward." + p));
        if (!(task.Spouts.SingleSpoutProbability >= 0 && task.Spouts.SingleSpoutProbability <= 1)) problems.Add("spouts.singleSpoutProbability must be in [0, 1]");
        problems.AddRange(TaskBlocks.LaserOnProbabilityProblems(opto.LaserOnProbability).Select(p => "optogenetics." + p));
    }

    /// <summary>The maximum laserDurationSeconds (task.py's same rule; with laserEnd stimulusOff the laser's cap): from a trigger to the
    /// next is at least stimulusToResponseWindowSeconds + itiMinSeconds (a response at once, nothing after the outcome, a quiet mouse),
    /// and in that time the laser, what the ZapitDaq trial outlasts it <paramref name="freeAfterLaserSeconds"/> (whole scan cycles,
    /// the ramp and a margin: ZapitDaqTrial.FreeAfterLaserSeconds, plus rig.yml's earlyStopLeadSeconds when an early stop ends it;
    /// the next trial is armed only once it has played) and ZapitDaq's arm time must fit before the next trigger.</summary>
    public static double MaxLaserSeconds(UclOpenCoenDomeDecisionMakingTaskParameters task, double freeAfterLaserSeconds)
    {
        var s = task.TrialStructure;
        return s.StimulusToResponseWindowSeconds + s.ItiMinSeconds - freeAfterLaserSeconds - ZapitDaqTrial.ArmSeconds;
    }

    /// <summary>The next trial, after the last one's row (its outcome and choice; null before the first).</summary>
    public Trial Next(TrialRow lastRow)
    {
        Trial trial;
        if (last != null && lastRow != null && ShouldRepeat(lastRow.outcome, lastRow.choice))
        {
            trial = last.Clone();
            trial.repeatCount = last.repeatCount + 1;
            if (trial.laserOn)   // a repeat never has the laser on
            {
                trial.laserOn = false;
                trial.laserPowerMw = 0;
            }
        }
        else
        {
            trial = New();
        }
        trial.trialNumber = ++drawn;
        // Every trial, a repeat too, draws its own interval.
        var structure = trial.values.TrialStructure;
        trial.itiSeconds = Uniform(structure.ItiMinSeconds, structure.ItiMaxSeconds);
        last = trial;
        return trial;
    }

    // The repeat rule is the repeated trial's combination's (repeats.maxRepeats, repeats.repeatOn). A probe (outcome probe,
    // whatever the choice) repeats only on noResponse after a miss; notCorrect on a probe is refused at Go. A withheld noGo
    // is correct, never a repeat. An auto trial (auto-reward, no choice, outcome auto) never repeats: it is neither a noResponse nor a notCorrect.
    bool ShouldRepeat(string outcome, string choice)
    {
        var repeats = last.values.Repeats;
        if (last.repeatCount >= repeats.MaxRepeats) return false;
        switch (repeats.RepeatOn)
        {
            case RepeatOn.NotCorrect: return outcome != TrialOutcomes.Correct && outcome != TrialOutcomes.Probe && outcome != TrialOutcomes.Auto;
            case RepeatOn.NoResponse: return outcome == TrialOutcomes.NoResponse || (outcome == TrialOutcomes.Probe && choice == Choice.NoResponse);
            default: return false;
        }
    }

    Trial New()
    {
        // The condition by weight, then one of its combinations (every list pick), whose values the trial draws from.
        int index = WeightedCondition();
        var condition = conditions[index];
        var trial = new Trial();
        var combination = condition.Combinations[condition.Combinations.Count == 1 ? 0 : random.Next(condition.Combinations.Count)];
        trial.trialCombinationId = combination.Id;
        var p = trial.values = combination.Task;
        trial.trialConditionId = index;
        trial.trialType = condition.TrialType;
        trial.autoReward = random.NextDouble() < p.AutoRewardProbability;
        var singleDrawn = random.NextDouble() < p.Spouts.SingleSpoutProbability;
        var large = random.NextDouble() < p.Reward.LargeProbability;
        trial.rewardOfferedUl = large ? p.Reward.SizeUl * p.Reward.LargeMultiplier : p.Reward.SizeUl;
        trial.DrawVisualPhase(random);

        // The grating: the combination's contrast and initial azimuth (each list one entry), shown where the contrast is above 0
        // (0: a grey screen, logged at NaN, NaN; the renderer's start at 0, 0 under contrast 0).
        var visual = p.VisualStimulus;
        double contrast = visual.InitialContrasts[0];
        double azimuth = visual.InitialAzimuthsDeg[0];
        trial.visualInitialAzimuthDeg = double.NaN;
        if (contrast > 0)
        {
            trial.visualInitialAzimuthDeg = azimuth;
            trial.gratingAzimuthDeg = azimuth;
            trial.visualInitialContrast = contrast;
        }
        // The sound: at its level (mute: none, NaN) on the speaker nearest an initial azimuth at auditoryStimulus.initialElevationDeg,
        // or on every speaker at "all" (logged at azimuth and elevation NaN, with its level).
        var sound = p.AuditoryStimulus;
        double soundAzimuth = SoundAzimuths.Parse(sound.InitialAzimuthsDeg[0]).Value;   // checked at Go
        trial.auditoryInitialLevelDbfs = SoundLevels.Parse(sound.InitialLevelDbfs);
        bool sounding = !double.IsNaN(trial.auditoryInitialLevelDbfs);
        if (sounding && double.IsNaN(soundAzimuth))
        {
            trial.speakerChannel = SoundAzimuths.AllSpeakersChannel;
            trial.auditoryInitialAzimuthDeg = double.NaN;
            trial.auditoryInitialElevationDeg = double.NaN;
        }
        else if (sounding)
        {
            var speaker = SoundAzimuths.NearestSpeaker(speakers, soundAzimuth, sound.InitialElevationDeg).Value;
            trial.speakerChannel = speaker.Channel;
            trial.auditoryInitialAzimuthDeg = speaker.Position.Azimuth;
            trial.auditoryInitialElevationDeg = speaker.Position.Elevation;
        }
        else
        {
            trial.speakerChannel = -1;
            trial.auditoryInitialAzimuthDeg = double.NaN;
            trial.auditoryInitialElevationDeg = double.NaN;
        }

        // The target: a side (the grating's or the sound's picked azimuth's, or named), a go/no-go role, or a probe (each
        // condition sets it, checked at Go).
        switch (p.Target.Value)
        {
            case ConditionTarget.VisualSide: trial.target = azimuth > 0 ? TargetWords.Right : TargetWords.Left; break;
            case ConditionTarget.AuditorySide: trial.target = soundAzimuth > 0 ? TargetWords.Right : TargetWords.Left; break;
            case ConditionTarget.Left: trial.target = TargetWords.Left; break;
            case ConditionTarget.Right: trial.target = TargetWords.Right; break;
            case ConditionTarget.Go: trial.target = TargetWords.Go; break;
            case ConditionTarget.NoGo: trial.target = TargetWords.NoGo; break;
            case ConditionTarget.NoReward: trial.target = TargetWords.NoReward; break;
            default: trial.target = TargetWords.Random; break;
        }
        if (task.TaskType == TaskType.DiscriminationGoNoGo)
        {
            // One spout, the left. Lick on go, withhold on noGo; a noReward trial is a probe, never rewarded or penalised
            // (Song et al. 2017).
            if (trial.target != TargetWords.Go)
            {
                trial.autoReward = false;   // auto-reward only on a go trial
                trial.rewardOfferedUl = 0;  // no reward on offer: a withheld noGo is correct, unrewarded
            }
        }
        else if (trial.target == TargetWords.NoReward)
        {
            // A probe with nothing on offer (localization, discriminationLeftRight): both spouts in, never rewarded or penalised.
            trial.autoReward = false;
            trial.rewardOfferedUl = 0;
        }
        if (trial.target == TargetWords.Random)
        {
            // No right answer: a random-reward probe. Both spouts move in, and a coin drawn
            // now, from the seeded stream, decides a choice at either: heads rewarded at that spout, tails unrewarded. Only
            // with penaliseRandomProbes (default false) is tails penalised as an incorrect choice and a miss not
            // auto-rewarded as penalty.noResponse (TrialRow.Penalised); otherwise neither costs anything. Its rewardOfferedUl is kept, heads or tails. It never repeats.
            trial.autoReward = false;
            trial.probeHeads = random.NextDouble() < 0.5;
        }
        // Only the target's spout comes in: always in go/no-go (the left), else on a scored trial whose spouts move, by the draw.
        trial.singleSpoutOffered = task.TaskType == TaskType.DiscriminationGoNoGo || (trial.target != TargetWords.Random && trial.target != TargetWords.NoReward && !p.Spouts.AlwaysIn && singleDrawn);

        var optogenetics = task.Optogenetics;
        if (optogenetics.Enabled)
        {
            // drawn + 1: this trial's number, which Next sets once it is drawn.
            ZapitDaqTrial.Draw(random, drawn + 1, bank, p.Optogenetics.TrialsBeforeLaser, p.Optogenetics.LaserOnProbability, p.Optogenetics.LaserPowerMw.Value,
                out trial.laserCondition, out trial.laserPointsMm, out trial.laserOn, out trial.laserPowerMw);
        }
        // Drawn last, so adding it changed no earlier draw; a repeat plays the waveform of the trial it repeats.
        trial.auditorySoundFileIndex = random.Next(AuditoryStimulusSet.WaveformCount);
        return trial;
    }

    /// <summary>The highest stimulus level the conditions can draw: the highest auditoryStimulus.initialLevelDbfs of any combination
    /// of a condition with a weight above 0 (NaN when all are mute), and where it is, for the stimulus set's peak check at Go
    /// (LevelAuditoryStimulusSet).</summary>
    public MaxSoundLevel MaxStimulusLevel()
    {
        return MaxSoundLevel.Of(conditions, t => t.AuditoryStimulus.InitialLevelDbfs);
    }

    IEnumerable<UclOpenCoenDomeDecisionMakingTaskParameters> Drawn()
    {
        return conditions.Where(c => c.Weight > 0).SelectMany(c => c.Combinations).Select(m => m.Task);
    }

    /// <summary>Every stimulus.rateHz the weighted conditions can draw, for the stimulus set's peak check.</summary>
    public double[] StimulusRatesHz() { return Drawn().Select(t => t.Stimulus.RateHz).Distinct().ToArray(); }

    /// <summary>The highest penalty level the conditions can draw (penalty.incorrect.sound.levelDbfs and
    /// penalty.noResponse.sound.levelDbfs, none when null), for PlayPenaltySound's buffer at Go.</summary>
    public MaxSoundLevel MaxPenaltyLevel()
    {
        return MaxSoundLevel.Of(conditions, t => t.Penalty.Incorrect.Sound.LevelDbfs, t => t.Penalty.NoResponse == null ? null : t.Penalty.NoResponse.Sound.LevelDbfs);
    }

    /// <summary>The penalty flashes the weighted conditions can draw (OpenRigs.Trials PenaltyFlashRules), for PlayPenaltyFlash's check at Go.</summary>
    public PenaltyFlashRules PenaltyFlashes()
    {
        return PenaltyFlashRules.Of(conditions, t => TrialRow.Cost(PenaltyKind.Incorrect, t), t => t.Penalty.NoResponse == null ? null : TrialRow.Cost(PenaltyKind.NoResponse, t));
    }

    int WeightedCondition()
    {
        int draw = random.Next(conditions.Sum(c => c.Weight));
        for (int i = 0; i < conditions.Count; i++)
        {
            draw -= conditions[i].Weight;
            if (draw < 0) return i;
        }
        throw new InvalidOperationException("No trial condition drawn.");
    }

    double Uniform(double low, double high)
    {
        return low + random.NextDouble() * (high - low);
    }
}

/// <summary>The response as it stands after a lick: where the net licks (left +1, right -1) have moved the grating.</summary>
public class DecisionUpdate
{
    public double GratingAzimuthDeg;
}

/// <summary>A trial's outcome (lib-open-rigs' TrialOutcome: its row, the reward, the penalty sound at the trial's level and
/// the penalty flash, the timeout) and, by its reward, the spouts it moves (ISpoutOutcome).</summary>
public class TrialOutcome : OpenRigs.Trials.TrialOutcome<TrialRow>
{
    TrialOutcome(TrialRow row, string instance, double penaltyLevelDbfs, double penaltyFlashRateHz) : base(row, instance, penaltyLevelDbfs, penaltyFlashRateHz) { }

    /// <summary>The row's rewarded (TrialRow.Rewarded) is the reward: rewardOfferedUl at the target's spout
    /// (discriminationGoNoGo's: the left), a random trial's at the spout chosen, the other spout drawn back, the next interval
    /// from the trial's end, the response window's (both out spouts.rewardConsumptionSeconds into it). Anything else draws both back, with the penalty sound, flash and timeout of its kind
    /// if it was penalised (TrialRow.Cost: a miss's penalty.noResponse, else penalty.incorrect). A trial whose spouts are
    /// always in leaves them where they are (OpenRigs.Trials SpoutCommands).</summary>
    public static TrialOutcome At(string choice, Trial trial, TrialTimes times)
    {
        var outcome = TrialRow.Outcome(choice, trial);
        var penalised = TrialRow.Penalised(outcome, choice, trial);
        // Resolved once, for the row's timeout and flash and the sound's level alike; nothing when not penalised (a
        // discriminationGoNoGo miss has no penalty.noResponse to resolve).
        var cost = penalised ? TrialRow.Cost(choice == Choice.NoResponse ? PenaltyKind.NoResponse : PenaltyKind.Incorrect, trial.values) : null;
        var row = TrialRow.Build(choice, trial, times, cost);
        // The valve's instance as rig.yml's wiring names it; go is answered at the left spout.
        var instance = (trial.target == TargetWords.Random ? row.choice : trial.target) == Choice.Right ? "right" : "left";
        return cost == null ? new TrialOutcome(row, instance, double.NaN, OpenRigs.Trials.PenaltyFlash.DefaultRateHz) : new TrialOutcome(row, instance, cost.LevelDbfs, cost.Flash.RateHz);
    }
}

/// <summary>
/// Decision-making's part of each trial, which lib-open-rigs' TrialLoop runs (the interval, the quiet waits, the lead,
/// the laser, the response window and the timeout are the loop's): the draw (TrialDraw), each trial's timing from the
/// task file, and the response rule. In the response window, stimulusToResponseWindowSeconds after the stimulus for responseWindowSeconds,
/// each lick adds 1 (left) or -1 (right) to the decision variable, which moves the grating azimuthShiftPerLickDeg per lick from
/// its drawn azimuth; the choice is left once it reaches thresholdLicks, right once it reaches -thresholdLicks, else
/// noResponse at the window's end. A lick before the window is not counted. In discriminationGoNoGo only the left
/// spout counts, and the choice is go once its licks reach thresholdLicks. The answer is the trial's target (TrialDraw).
/// </summary>
public class TrialRules : ITrialRules<Trial, DecisionUpdate, TrialOutcome>
{
    readonly UclOpenCoenDomeDecisionMakingTaskParameters task;
    readonly TrialDraw draw;
    readonly LaserTiming laser;
    int decision;   // the running trial's net licks in its window: +1 each left, -1 each right

    /// <summary>The rules for a task file's text on these speakers with the session's Zapit bank (null for none); earlyStopLeadSeconds
    /// rig.yml's zapit.earlyStopLeadSeconds, how far ahead ZapitDaq writes an early stop's ramp (laserEnd stimulusOff).</summary>
    public TrialRules(string taskText, EqualisedSpeakerArray speakers, ZapitBank bank, double earlyStopLeadSeconds = 0)
    {
        draw = new TrialDraw(taskText, speakers, bank, earlyStopLeadSeconds);
        task = draw.Task;
        var optogenetics = task.Optogenetics;
        // laserEnd duration: the line held each trial's laserDurationSeconds from the trigger (ILaserDurationTrial); the next trial
        // armed once ZapitDaq's whole trial has played. stimulusOff: the laser on to the trial's StimulusOff, then ZapitDaq's early
        // stop, capped at its laserDurationSeconds (the timing's cap, LaserBackstop's wall-clock wait, the longest any combination
        // has); the line held, and the next trial armed, once the early stop's lead, a scan cycle, the ramp and a margin have passed.
        if (!optogenetics.Enabled) laser = null;
        else if (optogenetics.LaserEnd == LaserEndMode.StimulusOff)
        {
            var cap = draw.Conditions.SelectMany(c => c.Combinations).Max(m => m.Task.Optogenetics.LaserDurationSeconds);
            laser = LaserTiming.UntilStimulusOff(draw.FreeAfterLaserSeconds, cap);
        }
        else laser = LaserTiming.ForDuration(draw.MaxRampDownSeconds);
    }

    /// <summary>The rules for a task file's text on a rig (its speakers and zapit.earlyStopLeadSeconds) with the session's Zapit bank
    /// (null for none); throws naming every problem.</summary>
    public static TrialRules Create(string taskText, LabRig rig, ZapitBank bank)
    {
        var lead = rig.Zapit != null && rig.Zapit.EarlyStopLeadSeconds.HasValue ? rig.Zapit.EarlyStopLeadSeconds.Value : 0;
        return new TrialRules(taskText, rig.SpeakerArray, bank, lead);
    }

    public LaserTiming Laser { get { return laser; } }

    /// <summary>The seed every draw of the experiment follows from (TrialDraw.Seed), logged once as an RngSeed event at trial 1's start.</summary>
    public int Seed { get { return draw.Seed; } }

    /// <summary>The stage as code-version (S03A-k7f2) from the task file's curriculum, which the check has found well formed.</summary>
    public string CurriculumStage { get { return task.Curriculum.Code + "-" + task.Curriculum.Version; } }

    /// <summary>The highest sound level the task's conditions can draw (TrialDraw.MaxStimulusLevel), for the stimulus set's peak check.</summary>
    public MaxSoundLevel MaxStimulusLevel { get { return draw.MaxStimulusLevel(); } }

    /// <summary>Every stimulus.rateHz the conditions can draw: the periods the stimulus set's peak check builds.</summary>
    public double[] StimulusRatesHz { get { return draw.StimulusRatesHz(); } }

    /// <summary>The highest penalty level the conditions can draw (NaN when all are mute): lib-open-rigs' PlayPenaltySound
    /// lays out its buffer at it at Go and plays each penalised trial's level from it.</summary>
    public double MaxPenaltyLevelDbfs { get { return draw.MaxPenaltyLevel().LevelDbfs; } }

    /// <summary>The penalty flashes the conditions can draw (TrialDraw.PenaltyFlashes): lib-open-rigs' PlayPenaltyFlash checks
    /// them at Go against the penalty sound's length.</summary>
    public PenaltyFlashRules PenaltyFlashes { get { return draw.PenaltyFlashes(); } }

    /// <summary>Settings_Combinations' text (OpenRigs.Trials TrialConditions.CombinationsJson): every combination built at Go, each a row the trial
    /// log's trialCombinationId names; logged by OpenRigs.Logging ConditionedTaskLogsZapitDaq in the Logging group.</summary>
    public string CombinationsJson { get { return TrialConditions.CombinationsJson(draw.Task, draw.Conditions, TrialDraw.Schema); } }

    public Trial Draw(TrialOutcome previous)
    {
        return draw.Next(previous == null ? null : previous.Row.Value);
    }

    public TrialTiming Timing(Trial trial)
    {
        var structure = trial.values.TrialStructure;
        var spouts = trial.values.Spouts;
        return new TrialTiming
        {
            ItiSeconds = trial.itiSeconds,
            LaserToStimulusSeconds = trial.values.Optogenetics.LaserToStimulusSeconds,   // its lead in every experiment, so timing never reveals Zapit
            LickQuietSeconds = structure.LickQuietSeconds,
            WheelQuietSeconds = structure.WheelQuietSeconds,
            WheelQuietCm = structure.WheelQuietCm,
            Laser = trial.laserCondition > 0,                                    // every trial of an optogenetics experiment, laser on or off
            ResponseDelaySeconds = structure.StimulusToResponseWindowSeconds,
            ResponseWindowSeconds = structure.ResponseWindowSeconds.Value,     // never null: the Go check refuses it
            ResponseWindowAlwaysFull = structure.ResponseWindowAlwaysFull,     // always true: a lick gives the outcome, the trial ends at the window's end
            // The stimulus off (the StimulusOff event: the grating hidden, the sound stopped) and the spouts' SpoutsIn and SpoutsOut cues.
            StimulusDurationSeconds = trial.Stimulus.DurationSeconds ?? double.NaN,
            ResponseToStimulusOffSeconds = trial.Stimulus.ResponseToStimulusOffSeconds,
            Cues = LabSpouts.Cues(spouts.StimulusToSpoutsInSeconds, spouts.AlwaysIn, spouts.RewardConsumptionSeconds),
        };
    }

    public void Start(Trial trial, TrialTimes times, double wheelCm)
    {
    }

    public void Stimulus(Trial trial, TrialTimes times, ICollection<DecisionUpdate> updates)
    {
        decision = 0;
        updates.Add(Update(trial));
    }

    public TrialOutcome Tick(Trial trial, TrialTimes times, ICollection<DecisionUpdate> updates)
    {
        return null;
    }

    public TrialOutcome Lick(Trial trial, int spout, TrialTimes times, ICollection<DecisionUpdate> updates)
    {
        if (!times.Responding) return null;
        bool goNoGo = task.TaskType == TaskType.DiscriminationGoNoGo;
        if (goNoGo && spout != 0) return null;   // one spout, the left
        decision += spout == 0 ? 1 : -1;
        updates.Add(Update(trial));
        var threshold = trial.values.Response.ThresholdLicks;
        if (decision >= threshold) return Outcome(trial, goNoGo ? Choice.Go : Choice.Left, times);
        if (decision <= -threshold) return Outcome(trial, Choice.Right, times);
        return null;
    }

    public TrialOutcome Wheel(Trial trial, double cm, TrialTimes times, ICollection<DecisionUpdate> updates)
    {
        return null;
    }

    public TrialOutcome ResponseEnd(Trial trial, TrialTimes times)
    {
        return Outcome(trial, Choice.NoResponse, times);
    }

    DecisionUpdate Update(Trial trial)
    {
        return new DecisionUpdate { GratingAzimuthDeg = decision * trial.values.Response.AzimuthShiftPerLickDeg + trial.gratingAzimuthDeg };
    }

    TrialOutcome Outcome(Trial trial, string choice, TrialTimes times)
    {
        return TrialOutcome.At(choice, trial, times);
    }
}

[Combinator]
[Description("Runs the trials (lib-open-rigs TrialLoop on TrialRules). Inputs: the rules, the Harp clock, the wheel in cm, the left and right lick states, ZapitDaq's events (Zapit.Events).")]
[WorkflowElementCategory(ElementCategory.Combinator)]
public class RunTrials
{
    public IObservable<TrialEvent<Trial, DecisionUpdate, TrialOutcome>> Process(IObservable<TrialRules> rules, IObservable<double> clock,
        IObservable<double> wheelCm, IObservable<bool> lickLeft, IObservable<bool> lickRight, IObservable<ZapitDaqEvent> zapitEvents)
    {
        return TrialLoop.Observe(rules, clock, new[] { lickLeft, lickRight }, zapitEvents, wheelCm);
    }
}
