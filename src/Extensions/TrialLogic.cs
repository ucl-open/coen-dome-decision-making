using Bonsai;
using Bonsai.Harp;
using OpenRigs.Audio;
using OpenRigs.Core;
using OpenRigs.Devices;
using OpenRigs.Devices.Rigs;
using OpenRigs.Devices.Zapit;
using OpenRigs.Trials;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using UclOpenCoenDomeDecisionMakingTask;
using YamlDotNet.RepresentationModel;
using ConditionTarget = UclOpenCoenDomeDecisionMakingTask.Target;
using BuiltTrialCondition = OpenRigs.Trials.BuiltTrialCondition<UclOpenCoenDomeDecisionMakingTask.UclOpenCoenDomeDecisionMakingTaskParameters>;

/// <summary>One trial as drawn: the lab's conditioned trial (OpenRigs.Trials ConditionedTrial: trialNumber, trialType,
/// trialConditionId, trialCombinationId, itiSeconds, rewardOfferedUl, the stimulus, the laser, autoReward) and this task's
/// own; its fields are named as the trial log's columns where they are one. Every other value is its combination's
/// (values), read per trial.</summary>
public class Trial : ConditionedTrial, ILaserDurationTrial
{
    double ILaserDurationTrial.LaserDurationSeconds { get { return values.Optogenetics.LaserDurationSeconds; } }

    public string target;              // left or right; in discriminationGoNoGo go or noGo; on a trial with no right answer noReward or random (TargetWords)
    public bool singleSpoutOffered;    // only the target's spout came in (TrialDraw: go/no-go, or a scored trial with the spouts moving and the draw)
    public int repeatCount;            // 0 for a new draw
    public double visualInitialAzimuthDeg;  // NaN with no grating (visualContrast 0), as the sound's when none plays
    public double gratingAzimuthDeg;   // what the renderer starts the grating at (not logged): visualInitialAzimuthDeg, 0 with no grating (hidden at contrast 0)
    public int auditorySoundFileIndex;
    public bool probeHeads;            // a random trial's coin: heads rewards a choice, tails penalises it
    internal UclOpenCoenDomeDecisionMakingTaskParameters values;  // its combination's values, which the rules read per trial

    /// <summary>The grating's shape as the renderer draws it (elevationDeg, sigmaDeg, orientationDeg, spatialFrequencyCpd,
    /// temporalFrequencyHz): its combination's visualStimulus, read per trial, not copied.</summary>
    public VisualStimulus VisualStimulus { get { return values.VisualStimulus; } }

    /// <summary>Whether the used spouts stay in on this trial (its combination's spoutsStayIn).</summary>
    public bool SpoutsStayIn { get { return values.SpoutsStayIn; } }

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
[Description("Checks the task at Go and gives the trials' rules (TrialRules, tested C#): curriculum is present and well formed, each trial condition builds and the draw's rules hold on rig.yml's speakers for the task and every condition (TrialDraw.CheckTask), and with optogenetics the Zapit conditions file. Input: the task file's text and rig.yml; stops the workflow naming every problem.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class CheckTaskAtGo
{
    public IObservable<TrialRules> Process(IObservable<Tuple<string, LabRig>> source)
    {
        return source.Select(input => TrialRules.Create(input.Item1, input.Item2, null));
    }
}

/// <summary>Every word the target column holds, in the task file's spelling: the sides left and right, discriminationGoNoGo's go
/// and noGo, and on a trial with no right answer (a probe, outcome probe) noReward (never rewarded or penalised) or random
/// (a choice rewarded or penalised on a coin).</summary>
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
    readonly int conditionCount;
    Trial last;
    int drawn;

    /// <summary>The seed the random stream started from: the task's rngSeed, or one drawn when it is null.</summary>
    public int Seed { get; private set; }

    /// <summary>The task as the file gives it, before any condition's set.</summary>
    public UclOpenCoenDomeDecisionMakingTaskParameters Task { get { return task; } }

    /// <summary>The conditions as built at Go, in trialConditions' order.</summary>
    public IList<BuiltTrialCondition> Conditions { get { return conditions; } }

    /// <summary>The draw of a task file's text (as rig-control wrote it at launch) on a rig's speakers; throws naming every problem.</summary>
    public TrialDraw(string taskText, EqualisedSpeakerArray speakers, Func<string, int> zapitConditionCount)
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
            var path = optogenetics.ZapitConditionsPath;
            conditionCount = zapitConditionCount != null ? zapitConditionCount(path) : ZapitConditions.Check(path).Count;
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
                        own.Add("a probe (target random or noReward) needs repeats.repeatOn never or noResponse, not notCorrect");
                    }
                    problems.AddRange(own.Select(p => condition.Where + (combination.Picks.Length == 0 ? "" : " with " + combination.Picks) + ": " + p));
                }
            }
        }
        if (problems.Count > 0) throw new InvalidOperationException("The task cannot be run: " + string.Join("; ", problems.Distinct()) + ".");
        return built;
    }

    /// <summary>The timing rules between a trial and the next, each drawn from any weighted combination (task.py
    /// _between_trials): every outcomeToSpoutsOutSeconds and visualStimulusSeconds within every itiMinSeconds, and with optogenetics
    /// every laserDurationSeconds within the shortest trigger to the next trigger less the ramp-down and Zapit's reply margin.</summary>
    static void BetweenTrials(List<string> problems, List<UclOpenCoenDomeDecisionMakingTaskParameters> tasks)
    {
        if (tasks.Count == 0) return;
        var iti = tasks.Min(t => t.TrialStructure.ItiMinSeconds);
        var spoutsOut = tasks.Max(t => t.TrialStructure.OutcomeToSpoutsOutSeconds);
        var stimulus = tasks.Max(t => t.TrialStructure.VisualStimulusSeconds);
        if (spoutsOut > iti + 1e-9) problems.Add(string.Format("trialStructure.outcomeToSpoutsOutSeconds {0} s exceeds trialStructure.itiMinSeconds {1} s of a combination that may come next", spoutsOut, iti));
        if (stimulus > iti + 1e-9) problems.Add(string.Format("trialStructure.visualStimulusSeconds {0} s exceeds trialStructure.itiMinSeconds {1} s of a combination that may come next", stimulus, iti));
        if (!tasks[0].Optogenetics.Enabled) return;
        var maxSeconds = tasks.Min(t => t.Optogenetics.LaserToStimulusSeconds + t.TrialStructure.ResponseDelaySeconds) + tasks.Min(t => t.TrialStructure.ItiMinSeconds - t.Optogenetics.LaserToStimulusSeconds)
            - ZapitConditions.MaximumRampDownSeconds - ZapitTrial.ReplyMarginSeconds;
        var duration = tasks.Max(t => t.Optogenetics.LaserDurationSeconds);
        if (duration > maxSeconds + 1e-9) problems.Add(string.Format("optogenetics.laserDurationSeconds ({0} s) must be at most {1} s across the combinations: the shortest lead plus response delay, plus the shortest interval less the lead, less the ramp-down and Zapit's reply margin", duration, maxSeconds));
    }

    // Bonsai reads the task file without pydantic, so what the draw needs is checked again here; x-rules are Python's alone
    // (task.py in check-workflow, app-rig-control at launch).
    static void Check(List<string> problems, UclOpenCoenDomeDecisionMakingTaskParameters task, EqualisedSpeakerArray speakers)
    {
        if (task.Repeats == null || task.AuditoryStimulus == null || task.VisualStimulus == null)
        {
            problems.Add("repeats, visualStimulus and auditoryStimulus must be given");
            return;
        }
        if (task.Repeats.MaxRepeats < 0) problems.Add("repeats.maxRepeats must be 0 or more");
        var visual = task.VisualStimulus;
        var contrasts = visual.Contrasts ?? new List<double>();
        if (contrasts.Count == 0 || contrasts.Any(c => !(c >= 0) || double.IsInfinity(c))) problems.Add("visualStimulus.contrasts must each be a finite number, 0 or more, at least one");
        // The sound's level: a number of dBFS RMS or mute (NaN), at most the peak limit.
        double level = double.NaN;
        try { level = SoundLevels.Parse(task.AuditoryStimulus.LevelDbfs); }
        catch (InvalidOperationException e) { problems.Add("auditoryStimulus.levelDbfs: " + e.Message); }
        if (level > SoundLevel.PeakLimitDbfs) problems.Add(string.Format("auditoryStimulus.levelDbfs {0} is above {1} dBFS", level, SoundLevel.PeakLimitDbfs));
        try { SoundLevels.Parse(task.Penalty.Sound.LevelDbfs); }
        catch (InvalidOperationException e) { problems.Add("penalty.sound.levelDbfs: " + e.Message); }
        bool sounding = !double.IsNaN(level);
        // The positions (task.py's same rules): each grating azimuth signed and never 0 (its sign is the side); each sound
        // azimuth's nearest speaker, at auditoryStimulus.elevationDeg, on its side (0: any).
        if (!(visual.ElevationDeg >= -90 && visual.ElevationDeg <= 90)) problems.Add("visualStimulus.elevationDeg must be in [-90, 90]");
        var elevation = task.AuditoryStimulus.ElevationDeg;
        bool elevationOk = elevation >= -90 && elevation <= 90;
        if (!elevationOk) problems.Add("auditoryStimulus.elevationDeg must be in [-90, 90]");
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
        finite("trialStructure.outcomeToSpoutsOutSeconds", structure.OutcomeToSpoutsOutSeconds);
        finite("trialStructure.responseDelaySeconds", structure.ResponseDelaySeconds);
        if (structure.ResponseWindowSeconds == null) problems.Add("trialStructure.responseWindowSeconds must be given: this task's response window always closes");
        foreach (var p in TaskBlocks.ResponseWindowProblems(structure.ResponseWindowSeconds)) problems.Add("trialStructure." + p);
        foreach (var p in TaskBlocks.AutoRewardProblems(task.AutoRewardProbability)) problems.Add(p);
        finite("trialStructure.visualStimulusSeconds", structure.VisualStimulusSeconds);
        finite("trialStructure.stimulusToSpoutsInSeconds", structure.StimulusToSpoutsInSeconds);
        finite("penalty.timeoutSeconds", task.Penalty.TimeoutSeconds);
        // The quiet waits, the lab's one check each (OpenRigs.Core QuietWait: finite, 0 or more, at most 10 s and 20 cm, the
        // wheel's distance 0 no movement at all): a long wait and a licking mouse would hold the trial start with no message.
        foreach (var p in QuietWait.LickProblems(structure.LickQuietSeconds)) problems.Add("trialStructure." + p);
        foreach (var p in QuietWait.WheelProblems(structure.WheelQuietSeconds, structure.WheelQuietCm)) problems.Add("trialStructure." + p);
        if (double.IsNaN(task.Optogenetics.LaserToStimulusSeconds) || double.IsInfinity(task.Optogenetics.LaserToStimulusSeconds)) problems.Add("optogenetics.laserToStimulusSeconds must be a finite number");
        if (task.Optogenetics.Enabled) positive("optogenetics.laserDurationSeconds", task.Optogenetics.LaserDurationSeconds);
        if (structure.VisualStimulusSeconds > structure.ResponseDelaySeconds + structure.ResponseWindowSeconds) problems.Add("trialStructure.visualStimulusSeconds is longer than responseDelaySeconds plus responseWindowSeconds");
        if (structure.VisualStimulusSeconds > structure.ItiMinSeconds) problems.Add(string.Format("trialStructure.visualStimulusSeconds ({0} s) is longer than itiMinSeconds ({1} s): the grating would still show when the next trial's stimulus starts", structure.VisualStimulusSeconds, structure.ItiMinSeconds));
        if (structure.ItiMinSeconds < structure.OutcomeToSpoutsOutSeconds) problems.Add(string.Format("trialStructure.itiMinSeconds ({0} s) is below outcomeToSpoutsOutSeconds ({1} s): the spouts are drawn out outcomeToSpoutsOutSeconds into the interval, before the stimulus", structure.ItiMinSeconds, structure.OutcomeToSpoutsOutSeconds));
        var opto = task.Optogenetics;
        var lead = opto.LaserToStimulusSeconds;
        var leadProblem = ZapitTrial.LeadProblem(lead, structure.ItiMinSeconds);
        if (leadProblem != null) problems.Add(leadProblem);
        if (-lead > structure.ResponseDelaySeconds + structure.ResponseWindowSeconds + 1e-9)
        {
            problems.Add(string.Format("optogenetics.laserToStimulusSeconds ({0} s) puts the trigger after the response window (responseDelaySeconds + responseWindowSeconds, {1} s)", lead, structure.ResponseDelaySeconds + structure.ResponseWindowSeconds));
        }
        var maxSeconds = MaxLaserSeconds(task);
        if (opto.Enabled && opto.LaserDurationSeconds > maxSeconds + 1e-9)
        {
            problems.Add(string.Format("optogenetics.laserDurationSeconds ({0} s) must be at most {1} s (responseDelaySeconds + itiMinSeconds - {2} s ramp-down - {3} s reply margin), so Zapit can be armed for the next trial in time", opto.LaserDurationSeconds, maxSeconds, ZapitConditions.MaximumRampDownSeconds, ZapitTrial.ReplyMarginSeconds));
        }
        // Each RewardValve piece refuses a reward over MaxRewardUl when it plans it, mid-session (task.py's same rule).
        var reward = task.Reward;
        problems.AddRange(TaskBlocks.LargeRewardProblems(reward.SizeUl, reward.LargeMultiplier, reward.LargeProbability, LoadRewardLine.DefaultMaxRewardUl).Select(p => "reward." + p));
        // The condition fields' bounds (a condition's cell is not checked by pydantic before Go).
        problems.AddRange(TaskBlocks.RewardSizeProblems(reward.SizeUl, LoadRewardLine.DefaultMaxRewardUl).Select(p => "reward." + p));
        if (!(task.SingleSpoutProbability >= 0 && task.SingleSpoutProbability <= 1)) problems.Add("singleSpoutProbability must be in [0, 1]");
        problems.AddRange(TaskBlocks.LaserOnProbabilityProblems(opto.LaserOnProbability).Select(p => "optogenetics." + p));
    }

    /// <summary>The maximum laserDurationSeconds (task.py's same rule): from a trigger to the next is at least responseDelaySeconds
    /// + itiMinSeconds (a response at once, no timeout, a quiet mouse), and in that time the laser, the maximum ramp-down a
    /// conditions file may have (Zapit refuses the next StartStimulus until it has played) and Zapit's reply margin must fit
    /// before the next trigger.</summary>
    public static double MaxLaserSeconds(UclOpenCoenDomeDecisionMakingTaskParameters task)
    {
        var s = task.TrialStructure;
        return s.ResponseDelaySeconds + s.ItiMinSeconds - ZapitConditions.MaximumRampDownSeconds - ZapitTrial.ReplyMarginSeconds;
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
    // is correct, never a repeat.
    bool ShouldRepeat(string outcome, string choice)
    {
        var repeats = last.values.Repeats;
        if (last.repeatCount >= repeats.MaxRepeats) return false;
        switch (repeats.RepeatOn)
        {
            case RepeatOn.NotCorrect: return outcome != TrialOutcomes.Correct && outcome != TrialOutcomes.Probe;
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
        var singleDrawn = random.NextDouble() < p.SingleSpoutProbability;
        var large = random.NextDouble() < p.Reward.LargeProbability;
        trial.rewardOfferedUl = large ? p.Reward.SizeUl * p.Reward.LargeMultiplier : p.Reward.SizeUl;
        trial.DrawVisualPhase(random);

        // The grating: the combination's contrast and initial azimuth (each list one entry), shown where the contrast is above 0
        // (0: a grey screen, logged at NaN, NaN; the renderer's start at 0, 0 under contrast 0).
        var visual = p.VisualStimulus;
        double contrast = visual.Contrasts[0];
        double azimuth = visual.InitialAzimuthsDeg[0];
        trial.visualInitialAzimuthDeg = double.NaN;
        if (contrast > 0)
        {
            trial.visualInitialAzimuthDeg = azimuth;
            trial.gratingAzimuthDeg = azimuth;
            trial.visualContrast = contrast;
        }
        // The sound: at its level (mute: none, NaN) on the speaker nearest an initial azimuth at auditoryStimulus.elevationDeg,
        // or on every speaker at "all" (logged at azimuth and elevation NaN, with its level).
        var sound = p.AuditoryStimulus;
        double soundAzimuth = SoundAzimuths.Parse(sound.InitialAzimuthsDeg[0]).Value;   // checked at Go
        trial.auditoryLevelDbfs = SoundLevels.Parse(sound.LevelDbfs);
        bool sounding = !double.IsNaN(trial.auditoryLevelDbfs);
        if (sounding && double.IsNaN(soundAzimuth))
        {
            trial.speakerChannel = SoundAzimuths.AllSpeakersChannel;
            trial.auditoryInitialAzimuthDeg = double.NaN;
            trial.auditoryElevationDeg = double.NaN;
        }
        else if (sounding)
        {
            var speaker = SoundAzimuths.NearestSpeaker(speakers, soundAzimuth, sound.ElevationDeg).Value;
            trial.speakerChannel = speaker.Channel;
            trial.auditoryInitialAzimuthDeg = speaker.Position.Azimuth;
            trial.auditoryElevationDeg = speaker.Position.Elevation;
        }
        else
        {
            trial.speakerChannel = -1;
            trial.auditoryInitialAzimuthDeg = double.NaN;
            trial.auditoryElevationDeg = double.NaN;
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
        if (trial.target == TargetWords.Random)
        {
            // No right answer: a random-reward probe. Both spouts move in, and a coin drawn
            // now, from the seeded stream, decides a choice at either: heads rewarded at that spout, tails penalised as an
            // incorrect choice; a miss as a scored trial's miss. Its rewardOfferedUl is kept, heads or tails. It never repeats.
            trial.autoReward = false;
            trial.probeHeads = random.NextDouble() < 0.5;
        }
        // Only the target's spout comes in: always in go/no-go (the left), else on a scored trial whose spouts move, by the draw.
        trial.singleSpoutOffered = task.TaskType == TaskType.DiscriminationGoNoGo || (trial.target != TargetWords.Random && !p.SpoutsStayIn && singleDrawn);

        var optogenetics = task.Optogenetics;
        if (optogenetics.Enabled)
        {
            // drawn + 1: this trial's number, which Next sets once it is drawn.
            ZapitTrial.Draw(random, drawn + 1, conditionCount, p.Optogenetics.TrialsBeforeLaser, p.Optogenetics.LaserOnProbability, p.Optogenetics.LaserPowerMw.Value,
                out trial.laserCondition, out trial.laserOn, out trial.laserPowerMw);
        }
        // Drawn last, so adding it changed no earlier draw; a repeat plays the waveform of the trial it repeats.
        trial.auditorySoundFileIndex = random.Next(AuditoryStimulusSet.WaveformCount);
        return trial;
    }

    /// <summary>The highest stimulus level the conditions can draw: the highest auditoryStimulus.levelDbfs of any combination
    /// of a condition with a weight above 0 (NaN when all are mute), and where it is, for the stimulus set's peak check at Go
    /// (LevelAuditoryStimulusSet).</summary>
    public MaxSoundLevel MaxStimulusLevel()
    {
        return MaxSoundLevel.Of(conditions, t => t.AuditoryStimulus.LevelDbfs);
    }

    /// <summary>The highest penalty level the conditions can draw (penalty.sound.levelDbfs), for PlayPenaltySound's buffer at Go.</summary>
    public MaxSoundLevel MaxPenaltyLevel()
    {
        return MaxSoundLevel.Of(conditions, t => t.Penalty.Sound.LevelDbfs);
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

/// <summary>A trial's outcome (lib-open-rigs' TrialOutcome: its row, the reward, the penalty sound at the trial's level, the
/// timeout) and the spouts it moves.</summary>
public class TrialOutcome : OpenRigs.Trials.TrialOutcome<TrialRow>
{
    public bool SpoutsStayIn;
    public LickSpoutCommand SpoutCommand;

    TrialOutcome(TrialRow row, string instance, double penaltyLevelDbfs) : base(row, instance, penaltyLevelDbfs) { }

    /// <summary>The row's rewarded (TrialRow.Rewarded) is the reward: rewardOfferedUl at the target's spout
    /// (discriminationGoNoGo's: the left), a random trial's at the spout chosen, the other spout drawn back. Anything else
    /// draws both back, with the penalty sound (at the trial's penalty.sound.levelDbfs) and timeout if it was penalised. A
    /// trial whose spouts stay in leaves them where they are.</summary>
    public static TrialOutcome Of(TrialRow row, Trial trial)
    {
        // The valve's instance as rig.yml's wiring names it; go is answered at the left spout.
        var instance = (trial.target == TargetWords.Random ? row.choice : trial.target) == Choice.Right ? "right" : "left";
        var o = new TrialOutcome(row, instance, SoundLevels.Parse(trial.values.Penalty.Sound.LevelDbfs));
        o.SpoutsStayIn = trial.SpoutsStayIn;
        o.SpoutCommand = !o.Rewarded ? LickSpoutCommand.SpoutsOut
            : instance == "left" ? LickSpoutCommand.LeftOnly : LickSpoutCommand.RightOnly;
        return o;
    }
}

/// <summary>
/// Decision-making's part of each trial, which lib-open-rigs' TrialLoop runs (the interval, the quiet waits, the lead,
/// the laser, the response window and the timeout are the loop's): the draw (TrialDraw), each trial's timing from the
/// task file, and the response rule. In the response window, responseDelaySeconds after the stimulus for responseWindowSeconds,
/// each lick adds 1 (left) or -1 (right) to the decision variable, which moves the grating azimuthShiftPerLickDeg per lick from
/// its drawn azimuth; the choice is left once it reaches thresholdLicks, right once it reaches -thresholdLicks, else
/// noResponse at the window's end. A lick before the window is not counted. In discriminationGoNoGo only the left
/// spout counts, and the choice is go once its licks reach thresholdLicks. The answer is the trial's target (TrialDraw).
/// </summary>
public class TrialRules : ITrialRules<Trial, DecisionUpdate, TrialOutcome>
{
    /// <summary>The cue that ends the grating, visualStimulusSeconds after the stimulus.</summary>
    public const string GratingOffCue = "GratingOff";
    /// <summary>The cue that brings the spouts in, stimulusToSpoutsInSeconds after the stimulus.</summary>
    public const string SpoutsInCue = "SpoutsIn";
    /// <summary>The cue that draws the spouts out, outcomeToSpoutsOutSeconds into the next interval, unless the coming trial's spouts stay in.</summary>
    public const string SpoutsOutCue = "SpoutsOut";

    readonly UclOpenCoenDomeDecisionMakingTaskParameters task;
    readonly TrialDraw draw;
    readonly LaserTiming laser;
    int decision;   // the running trial's net licks in its window: +1 each left, -1 each right

    public TrialRules(string taskText, EqualisedSpeakerArray speakers, bool hardwareTriggered, Func<string, int> zapitConditionCount)
    {
        draw = new TrialDraw(taskText, speakers, zapitConditionCount);
        task = draw.Task;
        var optogenetics = task.Optogenetics;
        // The line held each trial's laserDurationSeconds from the trigger (ILaserDurationTrial); the next trial armed once the
        // maximum ramp-down a conditions file may have has played (Zapit refuses a StartStimulus until then).
        laser = optogenetics.Enabled ? LaserTiming.ForDuration(hardwareTriggered, ZapitConditions.MaximumRampDownSeconds) : null;
    }

    /// <summary>The rules for a task file's text on a rig (its speakers, and Zapit's hardware triggering); throws naming every problem.</summary>
    public static TrialRules Create(string taskText, LabRig rig, Func<string, int> zapitConditionCount)
    {
        return new TrialRules(taskText, rig.SpeakerArray, rig.Zapit != null && rig.Zapit.HardwareTriggered, zapitConditionCount);
    }

    public LaserTiming Laser { get { return laser; } }

    /// <summary>The seed every draw of the experiment follows from (TrialDraw.Seed), logged once as an RngSeed event at trial 1's start.</summary>
    public int Seed { get { return draw.Seed; } }

    /// <summary>The stage as code-version (S03A-k7f2) from the task file's curriculum, which the check has found well formed.</summary>
    public string CurriculumStage { get { return task.Curriculum.Code + "-" + task.Curriculum.Version; } }

    /// <summary>The highest sound level the task's conditions can draw (TrialDraw.MaxStimulusLevel), for the stimulus set's peak check.</summary>
    public MaxSoundLevel MaxStimulusLevel { get { return draw.MaxStimulusLevel(); } }

    /// <summary>The highest penalty level the conditions can draw (NaN when all are mute): lib-open-rigs' PlayPenaltySound
    /// lays out its buffer at it at Go and plays each penalised trial's level from it.</summary>
    public double MaxPenaltyLevelDbfs { get { return draw.MaxPenaltyLevel().LevelDbfs; } }

    /// <summary>Settings_Combinations' text (OpenRigs.Trials TrialConditions.CombinationsJson): every combination built at Go, each a row the trial
    /// log's trialCombinationId names; logged by OpenRigs.Logging ConditionedTaskLogs in the Logging group.</summary>
    public string CombinationsJson { get { return TrialConditions.CombinationsJson(draw.Task, draw.Conditions, TrialDraw.Schema); } }

    public Trial Draw(TrialOutcome previous)
    {
        return draw.Next(previous == null ? null : previous.Row.Value);
    }

    public TrialTiming Timing(Trial trial)
    {
        var structure = trial.values.TrialStructure;
        var cues = new List<TrialCue>
        {
            new TrialCue(GratingOffCue, CueAnchor.Stimulus, structure.VisualStimulusSeconds),
            new TrialCue(SpoutsInCue, CueAnchor.Stimulus, structure.StimulusToSpoutsInSeconds, true),
            new TrialCue(SpoutsOutCue, CueAnchor.NextIti, structure.OutcomeToSpoutsOutSeconds),
        };
        return new TrialTiming
        {
            ItiSeconds = trial.itiSeconds,
            LaserToStimulusSeconds = trial.values.Optogenetics.LaserToStimulusSeconds,   // its lead in every experiment, so timing never reveals Zapit
            LickQuietSeconds = structure.LickQuietSeconds,
            WheelQuietSeconds = structure.WheelQuietSeconds,
            WheelQuietCm = structure.WheelQuietCm,
            Laser = trial.laserCondition > 0,                                    // every trial of an optogenetics experiment, laser on or off
            ResponseDelaySeconds = structure.ResponseDelaySeconds,
            ResponseWindowSeconds = structure.ResponseWindowSeconds.Value,     // never null: the Go check refuses it
            Cues = cues,
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
        var row = TrialRow.Build(choice, trial, times, trial.values.Penalty.TimeoutSeconds);
        return TrialOutcome.Of(row, trial);
    }
}

[Combinator]
[Description("Runs the trials (lib-open-rigs TrialLoop on TrialRules). Inputs: the rules, the Harp clock, the wheel in cm, the left and right lick states, Zapit's replies.")]
[WorkflowElementCategory(ElementCategory.Combinator)]
public class RunTrials
{
    public IObservable<TrialEvent<Trial, DecisionUpdate, TrialOutcome>> Process(IObservable<TrialRules> rules, IObservable<double> clock,
        IObservable<double> wheelCm, IObservable<bool> lickLeft, IObservable<bool> lickRight, IObservable<ZapitReply> zapitReplies)
    {
        return TrialLoop.Observe(rules, clock, new[] { lickLeft, lickRight }, zapitReplies, wheelCm);
    }
}

[Combinator]
[Description("Every spout move, from the trial events: the spouts out at trial 1's interval start and at each SpoutsOut cue, unless the coming trial's spouts stay in (spoutsStayIn, the trial drawn at its interval's start); at each SpoutsIn cue both in, or the target's alone on a single-spout trial (singleSpoutOffered); at the outcome, unless the trial's spouts stay in, the outcome's (the rewarded spout alone, else both out).")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class SpoutCommands
{
    public IObservable<LickSpoutCommand> Process(IObservable<TrialEvent<Trial, DecisionUpdate, TrialOutcome>> source)
    {
        return Observable.Defer(() =>
        {
            var firstIti = true;
            Trial coming = null;
            return source.SelectMany(e =>
            {
                var command = Command(e, firstIti, coming);
                if (e.Kind == TrialEventKind.Iti)
                {
                    firstIti = false;
                    coming = e.Trial;
                }
                return command.HasValue ? new[] { command.Value } : new LickSpoutCommand[0];
            });
        });
    }

    /// <summary>The spout move an event makes, if any. firstIti on trial 1's interval start, where the spouts go out; coming,
    /// the trial whose interval the SpoutsOut cue falls in (drawn at that interval's start), which decides whether they go out.</summary>
    public static LickSpoutCommand? Command(TrialEvent<Trial, DecisionUpdate, TrialOutcome> e, bool firstIti, Trial coming)
    {
        switch (e.Kind)
        {
            case TrialEventKind.Iti:
                return firstIti && !e.Trial.SpoutsStayIn ? LickSpoutCommand.SpoutsOut : (LickSpoutCommand?)null;
            case TrialEventKind.Cue:
                if (e.Cue == TrialRules.SpoutsInCue) return SpoutsIn(e.Trial);
                if (e.Cue == TrialRules.SpoutsOutCue) return coming != null && coming.SpoutsStayIn ? (LickSpoutCommand?)null : LickSpoutCommand.SpoutsOut;
                return null;
            case TrialEventKind.Outcome:
                return e.Outcome.SpoutsStayIn ? (LickSpoutCommand?)null : e.Outcome.SpoutCommand;
            default:
                return null;
        }
    }

    /// <summary>Where the spouts move at the SpoutsIn cue: the target's alone on a single-spout trial (go/no-go: the left),
    /// else both in.</summary>
    public static LickSpoutCommand SpoutsIn(Trial trial)
    {
        if (!trial.singleSpoutOffered) return LickSpoutCommand.SpoutsIn;
        return trial.target == TargetWords.Right ? LickSpoutCommand.RightOnly : LickSpoutCommand.LeftOnly;   // go-no-go: the left
    }
}
