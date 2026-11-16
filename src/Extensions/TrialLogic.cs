using Bonsai;
using Bonsai.Harp;
using OpenRigs.Core;
using OpenRigs.Devices;
using OpenRigs.Devices.Rigs;
using OpenRigs.Devices.Zapit;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using UclOpenCoenDomeDecisionMakingTask;

/// <summary>One trial as drawn: its fields are named as the trial log's columns where they are one.</summary>
public class Trial : IZapitTrial
{
    int ITrial.TrialNumber { get { return trialNumber; } }
    int IZapitTrial.LaserCondition { get { return laserCondition; } }
    bool IZapitTrial.LaserOn { get { return laserOn; } }
    double IZapitTrial.LaserPowerMw { get { return laserPowerMw; } }

    public int trialNumber;            // from 1, repeats counted
    public string trialType;           // TrialDraw.TypeNames: what the trial shows
    public string target;              // Left or Right; in discriminationGoNoGo Go or NoGo; on a trial with no right answer NoReward or Random (Answer)
    public double rewardOfferedUl;     // what a reward on this trial gives, on every trial; 0 where none is on offer
    public bool bothSpouts;
    public int repeatCount;            // 0 for a new draw
    public double itiSeconds;          // the interval drawn for this trial: its start to the stimulus, with a quiet mouse
    public double visualAzimuthDeg;
    public double visualElevationDeg;
    public double visualContrast;      // 0: grey screen
    public double visualPhaseRad;
    public bool audioEnabled;          // false: no sound (discrimination's visual and neutral trials)
    public int speakerChannel;         // the mixer channel of rig.yml's speaker nearest the drawn location; -1 when silent
    public double audioAzimuthDeg;     // that speaker's azimuth; NaN when silent
    public double audioElevationDeg;
    public int audioWaveformIndex;
    public int laserCondition;         // 0 without optogenetics; on every optogenetics trial, laser on or off
    public bool laserOn;
    public double laserPowerMw;        // 0 with the laser off
    public bool autoReward;
    public bool probeHeads;            // a Random trial's coin: heads rewards a choice, tails penalises it
    public bool spoutMovements;
    public int rngSeed;                // the experiment's seed (TrialDraw.Seed), logged once as an RngSeed event

    public Trial Clone()
    {
        return (Trial)MemberwiseClone();
    }
}

[Combinator]
[Description("Checks the task at Go and gives the trials' rules (TrialRules, tested C#): the stage is in the table and the draw's rules hold on rig.yml's speakers (TrialDraw.CheckTask), and with optogenetics the Zapit conditions file. Input: the task, TrainingStage and rig.yml; stops the workflow naming every problem.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class CheckTaskAtGo
{
    public IObservable<TrialRules> Process(IObservable<Tuple<UclOpenCoenDomeDecisionMakingTaskLogic, int, LabRig>> source)
    {
        return source.Select(input => TrialRules.Create(input.Item1.TaskParameters, input.Item2, input.Item3, null));
    }
}

/// <summary>The target column's answers other than a side: discriminationGoNoGo's Go and NoGo, and on a trial with no right
/// answer (a probe, outcome Probe) NoReward (go/no-go's: never rewarded or penalised) or Random (localization's and
/// left-right's: a choice rewarded or penalised on a coin, TrialDraw.RandomRewardProbe).</summary>
public static class Answer
{
    public const string Go = "Go";
    public const string NoGo = "NoGo";
    public const string NoReward = "NoReward";
    public const string Random = "Random";
    /// <summary>The outcome of a NoReward or Random trial, whatever the choice: left out of performance.</summary>
    public const string ProbeOutcome = "Probe";
}

/// <summary>What the mouse chose (the choice column), and in discriminationLeftRight the target's two sides: Left or Right, in
/// discriminationGoNoGo Lick at the one spout, and NoResponse when the window closed with none.</summary>
public static class Choice
{
    public const string Left = "Left";
    public const string Right = "Right";
    public const string Lick = "Lick";
    public const string NoResponse = "NoResponse";
}

/// <summary>The draw's state and rules, apart from Bonsai so a test can run them.</summary>
public class TrialDraw
{
    public static readonly string[] TypeNames = { "visual", "auditory", "audiovisualCoherent", "audiovisualConflict", "neutral" };
    const double CentreAzimuth = 0, CentreElevation = 15;  // the sound of visual and neutral trials in localization

    readonly UclOpenCoenDomeDecisionMakingTaskParameters task;
    readonly EqualisedSpeakerArray speakers;
    readonly TrainingStage stage;
    readonly Random random;
    readonly int conditionCount;
    Trial last;
    int drawn;

    /// <summary>The seed the random stream started from: the task's rngSeed, or one drawn when it is null.</summary>
    public int Seed { get; private set; }

    /// <summary>The stage's row of the task file's trainingStages.</summary>
    public TrainingStage Stage { get { return stage; } }

    public TrialDraw(UclOpenCoenDomeDecisionMakingTaskParameters task, EqualisedSpeakerArray speakers, int stage, Func<string, int> zapitConditionCount)
    {
        this.task = task;
        this.speakers = speakers;
        this.stage = CheckTask(task, stage, speakers);
        var optogenetics = task.Optogenetics;
        if (optogenetics.UseZapit)
        {
            if (!(optogenetics.LaserPowerMw > 0))
            {
                throw new InvalidOperationException("optogenetics.useZapit is on, so optogenetics.laserPowerMw must be a power in mW above 0.");
            }
            var path = optogenetics.ZapitConfigPath;
            conditionCount = zapitConditionCount != null ? zapitConditionCount(path) : ZapitConditions.Check(path);
        }
        Seed = RngSeed.Resolve(task.RngSeed);
        random = new Random(Seed);
    }

    /// <summary>The stage's row, once the stage is in the table and the task can be run on these speakers; throws
    /// naming every problem.</summary>
    public static TrainingStage CheckTask(UclOpenCoenDomeDecisionMakingTaskParameters task, int stage, EqualisedSpeakerArray speakers)
    {
        if (stage < 0 || stage >= task.TrainingStages.Count)
        {
            throw new InvalidOperationException(string.Format(
                "TrainingStage is {0}, but the task file's trainingStages has rows 0 to {1}.", stage, task.TrainingStages.Count - 1));
        }
        var row = task.TrainingStages[stage];
        Check(task, row, speakers);
        return row;
    }

    // Bonsai reads the task file without pydantic, so the rules the draw needs are checked again here.
    static void Check(UclOpenCoenDomeDecisionMakingTaskParameters task, TrainingStage stage, EqualisedSpeakerArray speakers)
    {
        var problems = new List<string>();
        // Each block names exactly the task type's keys (task.py's same rule), the stage's weights only when it sets its own.
        var weightKeys = KeyIndices(task.TaskType);
        KeyProblems(problems, "trialWeights", task.TaskType, weightKeys, task.TrialWeights == null ? null : AllKeys(task.TrialWeights));
        if (stage.TrialWeights != null) KeyProblems(problems, "stage '" + stage.Label + "' trialWeights", task.TaskType, weightKeys, AllKeys(stage.TrialWeights));
        if (problems.Count == 0)
        {
            var roles = RoleWeights(task, stage);
            if (roles.Any(w => w < 0) || roles.Sum() <= 0) problems.Add("stage '" + stage.Label + "' needs trial weights of 0 or more, one above 0");
            if (task.TaskType == TaskType.DiscriminationGoNoGo && roles[0] == 0) problems.Add("stage '" + stage.Label + "' has no Go trials: its trial weights' go is 0");
        }
        KeyProblems(problems, "repeats.rulesPerType", task.TaskType, RuleKeyIndices(task.TaskType), task.Repeats.RulesPerType == null ? null : AllKeys(task.Repeats.RulesPerType));
        if (task.TaskType == TaskType.DiscriminationGoNoGo && stage.BothSpoutsProbability != 0) problems.Add("stage '" + stage.Label + "' needs bothSpoutsProbability 0 in discriminationGoNoGo: only the left spout moves in");
        if (stage.Contrasts == null || stage.Contrasts.Count == 0 || stage.Contrasts.Any(c => !(c > 0))) problems.Add("stage '" + stage.Label + "' needs contrasts, each above 0");
        var visual = task.VisualStimulus;
        foreach (var right in new[] { false, true })
        {
            var side = right ? "right" : "left";
            if (AzimuthCandidates(visual, right, true).Count == 0 && !HasRange(visual, right, true)) problems.Add("visualStimulus has no " + side + " azimuth within coherentAzimuthCapDeg");
            var locations = task.AudioStimulus.SpeakerLocations.Where(l => right ? l.Azimuth > 0 : l.Azimuth < 0).ToList();
            if (locations.Count == 0) problems.Add("audioStimulus.speakerLocations has none on the " + side);
            foreach (var location in locations)
            {
                var speaker = NearestSpeaker(speakers, location.Azimuth, location.Elevation);
                if (right ? speaker.Value.Position.Azimuth <= 0 : speaker.Value.Position.Azimuth >= 0)
                {
                    problems.Add(string.Format("rig.yml's speaker nearest the {0} location ({1}, {2}) is '{3}', not on the {0}", side, location.Azimuth, location.Elevation, speaker.Key));
                }
            }
        }
        if (task.AudioStimulus.SpeakerLocations.Any(l => l.Azimuth == 0)) problems.Add("audioStimulus.speakerLocations has an azimuth-0 entry; the centre sound is fixed in code");
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
        finite("trialStructure.itiMinSeconds", structure.ItiMinSeconds);
        finite("trialStructure.itiMaxSeconds", structure.ItiMaxSeconds);
        finite("trialStructure.drinkSeconds", structure.DrinkSeconds);
        finite("trialStructure.responseDelaySeconds", structure.ResponseDelaySeconds);
        positive("trialStructure.responseWindowSeconds", structure.ResponseWindowSeconds);
        finite("trialStructure.stimulusSeconds", structure.StimulusSeconds);
        finite("trialStructure.stimulusToSpoutsInSeconds", structure.StimulusToSpoutsInSeconds);
        finite("penalty.timeoutSeconds", task.Penalty.TimeoutSeconds);
        // The quiet waits, the lab's one check (OpenRigs.Core QuietWait: finite, 0 or more, at most 10 s and 20 cm, the
        // wheel's pair both set or both 0): a long wait and a licking mouse would hold the trial start with no message.
        foreach (var p in QuietWait.SettingsProblems(stage.LickQuietSeconds, stage.WheelQuietSeconds, stage.WheelQuietCm)) problems.Add("stage '" + stage.Label + "': " + p);
        if (double.IsNaN(task.Optogenetics.LaserToStimulusSeconds) || double.IsInfinity(task.Optogenetics.LaserToStimulusSeconds)) problems.Add("optogenetics.laserToStimulusSeconds must be a finite number");
        if (task.Optogenetics.UseZapit) positive("optogenetics.laserDurationSeconds", task.Optogenetics.LaserDurationSeconds);
        if (structure.StimulusSeconds > structure.ResponseDelaySeconds + structure.ResponseWindowSeconds) problems.Add("trialStructure.stimulusSeconds is longer than responseDelaySeconds plus responseWindowSeconds");
        if (structure.ItiMaxSeconds < structure.ItiMinSeconds) problems.Add("trialStructure.itiMaxSeconds is below itiMinSeconds");
        if (structure.StimulusSeconds > structure.ItiMinSeconds) problems.Add(string.Format("trialStructure.stimulusSeconds ({0} s) is longer than itiMinSeconds ({1} s): the grating would still show when the next trial's stimulus starts", structure.StimulusSeconds, structure.ItiMinSeconds));
        if (structure.ItiMinSeconds < structure.DrinkSeconds) problems.Add(string.Format("trialStructure.itiMinSeconds ({0} s) is below drinkSeconds ({1} s): the spouts are drawn back drinkSeconds into the interval, before the stimulus", structure.ItiMinSeconds, structure.DrinkSeconds));
        var opto = task.Optogenetics;
        var lead = opto.LaserToStimulusSeconds;
        var leadProblem = ZapitTrial.LeadProblem(lead, structure.ItiMinSeconds);
        if (leadProblem != null) problems.Add(leadProblem);
        if (-lead > structure.ResponseDelaySeconds + structure.ResponseWindowSeconds + 1e-9)
        {
            problems.Add(string.Format("optogenetics.laserToStimulusSeconds ({0} s) puts the trigger after the response window (responseDelaySeconds + responseWindowSeconds, {1} s)", lead, structure.ResponseDelaySeconds + structure.ResponseWindowSeconds));
        }
        var longest = LongestLaserSeconds(task);
        if (opto.UseZapit && opto.LaserDurationSeconds > longest + 1e-9)
        {
            problems.Add(string.Format("optogenetics.laserDurationSeconds ({0} s) must be at most {1} s (responseDelaySeconds + itiMinSeconds - {2} s ramp-down - {3} s reply margin), so Zapit can be armed for the next trial in time", opto.LaserDurationSeconds, longest, ZapitConditions.MaximumRampDownSeconds, ZapitTrial.ReplyMarginSeconds));
        }
        // DeliverReward refuses a reward over MaxRewardUl when it plans it, mid-session (task.py's same rule).
        var multiplier = task.Reward.LargeRewardMultiplier;
        if (task.Reward.LargeRewardProbability > 0 && multiplier > 1 && stage.RewardSizeUl * multiplier > LoadRewardLine.DefaultMaxRewardUl + 1e-9)
        {
            problems.Add(string.Format("stage '{0}' rewardSizeUl ({1} ul) x reward.largeRewardMultiplier ({2}) is {3} ul, more than {4} ul, the most one reward may ask for", stage.Label, stage.RewardSizeUl, multiplier, stage.RewardSizeUl * multiplier, LoadRewardLine.DefaultMaxRewardUl));
        }
        if (problems.Count > 0) throw new InvalidOperationException("The task cannot be run: " + string.Join("; ", problems) + ".");
    }

    /// <summary>The longest laserDurationSeconds (task.py's same rule): from a trigger to the next is at least responseDelaySeconds
    /// + itiMinSeconds (a response at once, no timeout, a quiet mouse), and in that time the laser, the longest ramp-down a
    /// conditions file may have (Zapit refuses the next StartStimulus until it has played) and Zapit's reply margin must fit
    /// before the next trigger.</summary>
    public static double LongestLaserSeconds(UclOpenCoenDomeDecisionMakingTaskParameters task)
    {
        var s = task.TrialStructure;
        return s.ResponseDelaySeconds + s.ItiMinSeconds - ZapitConditions.MaximumRampDownSeconds - ZapitTrial.ReplyMarginSeconds;
    }

    // Every key trialWeights and rulesPerType may have, and which five each task type gives, in its order (task.py TRIAL_KEYS):
    // localization's by what a trial shows, the discriminations' by role, goModality or leftModality filling go or left.
    static readonly string[] AllKeyNames = { "visual", "auditory", "audiovisualCoherent", "audiovisualConflict", "neutral", "go", "noGo", "left", "right" };

    static int[] KeyIndices(TaskType type)
    {
        switch (type)
        {
            case TaskType.DiscriminationGoNoGo: return new[] { 5, 6, 2, 3, 4 };
            case TaskType.DiscriminationLeftRight: return new[] { 7, 8, 2, 3, 4 };
            default: return new[] { 0, 1, 2, 3, 4 };
        }
    }

    /// <summary>The five keys of trialWeights and rulesPerType in a task type: localization's TypeNames; discriminationGoNoGo's
    /// go, noGo, audiovisualCoherent, audiovisualConflict, neutral; discriminationLeftRight's left, right and the same three.</summary>
    public static string[] Keys(TaskType type)
    {
        return KeyIndices(type).Select(i => AllKeyNames[i]).ToArray();
    }

    static int?[] AllKeys(TrialTypeWeights w)
    {
        return new[] { w.Visual, w.Auditory, w.AudiovisualCoherent, w.AudiovisualConflict, w.Neutral, w.Go, w.NoGo, w.Left, w.Right };
    }

    static RepeatRule?[] AllKeys(TrialTypeRules r)
    {
        return new[] { r.Visual, r.Auditory, r.AudiovisualCoherent, null, null, r.Go, r.NoGo, r.Left, r.Right };
    }

    // rulesPerType's keys (task.py RULE_KEYS): only a trial with a right answer repeats, so localization names visual,
    // auditory, audiovisualCoherent and the discriminations their two one-modality roles (Pip, 16 Nov 2026).
    static int[] RuleKeyIndices(TaskType type)
    {
        switch (type)
        {
            case TaskType.DiscriminationGoNoGo: return new[] { 5, 6 };
            case TaskType.DiscriminationLeftRight: return new[] { 7, 8 };
            default: return new[] { 0, 1, 2 };
        }
    }

    static void KeyProblems<T>(List<string> problems, string name, TaskType type, int[] indices, T?[] values) where T : struct
    {
        var keys = indices.Select(i => AllKeyNames[i]).ToArray();
        var given = values == null ? new string[0] : AllKeyNames.Where((k, i) => values[i].HasValue).ToArray();
        if (!given.OrderBy(k => k).SequenceEqual(keys.OrderBy(k => k)))
        {
            problems.Add(string.Format("{0} must give exactly {1} in {2} (it gives {3})", name, string.Join(", ", keys), TaskTypeName(type), given.Length == 0 ? "none" : string.Join(", ", given)));
        }
    }

    static string TaskTypeName(TaskType type)
    {
        return type == TaskType.DiscriminationGoNoGo ? "discriminationGoNoGo" : type == TaskType.DiscriminationLeftRight ? "discriminationLeftRight" : "localization";
    }

    /// <summary>The stage's trial mix in its task type's key order (Keys): the stage's own trialWeights, else the task's.</summary>
    public static int[] RoleWeights(UclOpenCoenDomeDecisionMakingTaskParameters task, TrainingStage stage)
    {
        var all = AllKeys(stage.TrialWeights ?? task.TrialWeights);
        return KeyIndices(task.TaskType).Select(i => all[i].GetValueOrDefault()).ToArray();
    }

    /// <summary>The stage's trial mix by what each trial shows, in TypeNames' order: in the discriminations goModality's or
    /// leftModality's modality takes the go or left weight, the other modality the noGo or right.</summary>
    public static int[] Weights(UclOpenCoenDomeDecisionMakingTaskParameters task, TrainingStage stage)
    {
        return ByStimulus(task, RoleWeights(task, stage));
    }

    // Key order to TypeNames' order: only the first two differ, swapped when the go or left modality is auditory.
    static T[] ByStimulus<T>(UclOpenCoenDomeDecisionMakingTaskParameters task, T[] byKey)
    {
        var modality = task.TaskType == TaskType.DiscriminationGoNoGo ? task.GoModality : task.LeftModality;
        if (task.TaskType == TaskType.Localization || modality == Modality.Visual) return byKey;
        var swapped = (T[])byKey.Clone();
        swapped[0] = byKey[1];
        swapped[1] = byKey[0];
        return swapped;
    }

    static RepeatRule Rule(UclOpenCoenDomeDecisionMakingTaskParameters task, int type)
    {
        var all = AllKeys(task.Repeats.RulesPerType);
        var byStimulus = ByStimulus(task, RuleKeyIndices(task.TaskType).Select(i => all[i].GetValueOrDefault()).ToArray());
        return type < byStimulus.Length ? byStimulus[type] : RepeatRule.Never;   // no right answer: never repeated
    }

    /// <summary>The speaker in rig.yml at the smallest angle from the location.</summary>
    public static KeyValuePair<string, Speaker> NearestSpeaker(EqualisedSpeakerArray speakers, double azimuth, double elevation)
    {
        if (speakers == null || speakers.Speakers == null || speakers.Speakers.Count == 0)
        {
            throw new InvalidOperationException("rig.yml has no speakerArray.speakers to play the auditory stimuli on.");
        }
        return speakers.Speakers.OrderBy(s => Angle(azimuth, elevation, s.Value.Position.Azimuth, s.Value.Position.Elevation)).First();
    }

    static double Angle(double az1, double el1, double az2, double el2)
    {
        const double rad = Math.PI / 180;
        var cos = Math.Sin(el1 * rad) * Math.Sin(el2 * rad) + Math.Cos(el1 * rad) * Math.Cos(el2 * rad) * Math.Cos((az1 - az2) * rad);
        return Math.Acos(Math.Max(-1, Math.Min(1, cos)));
    }

    /// <summary>The next trial, after the last one's outcome (Correct, Incorrect or NoResponse; null before the first).</summary>
    public Trial Next(string lastOutcome)
    {
        Trial trial;
        if (last != null && lastOutcome != null && ShouldRepeat(lastOutcome))
        {
            trial = last.Clone();
            trial.repeatCount = last.repeatCount + 1;
            if (task.Optogenetics.NoLaserOnRepeats && trial.laserOn)
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
        trial.rngSeed = Seed;
        // Every trial, a repeat too, draws its own interval (Pip, 13 Nov 2026).
        var structure = task.TrialStructure;
        trial.itiSeconds = Uniform(structure.ItiMinSeconds, structure.ItiMaxSeconds);
        last = trial;
        return trial;
    }

    bool ShouldRepeat(string outcome)
    {
        if (!stage.AllowRepeats || last.repeatCount >= task.Repeats.MaxRepeats || outcome == Answer.ProbeOutcome) return false;
        var type = Array.IndexOf(TypeNames, last.trialType);
        switch (Rule(task, type))
        {
            case RepeatRule.OnFailure: return outcome != "Correct";
            case RepeatRule.OnNoResponse: return outcome == "NoResponse";
            default: return false;
        }
    }

    Trial New()
    {
        var trial = new Trial();
        trial.autoReward = random.NextDouble() < stage.AutoRewardProbability;
        trial.spoutMovements = stage.SpoutMovements;
        trial.bothSpouts = random.NextDouble() < stage.BothSpoutsProbability;
        var large = random.NextDouble() < task.Reward.LargeRewardProbability;
        trial.rewardOfferedUl = large ? stage.RewardSizeUl * task.Reward.LargeRewardMultiplier : stage.RewardSizeUl;
        trial.visualPhaseRad = 2 * Math.PI * random.NextDouble();

        int type = WeightedType();
        trial.trialType = TypeNames[type];
        bool discrimination = task.TaskType != TaskType.Localization;
        // Localization plays a centre sound on visual and neutral trials; the discriminations' are silent.
        bool showsSound = !discrimination || (type != 0 && type != 4);

        bool right;
        double soundAzimuth = CentreAzimuth, soundElevation = CentreElevation;
        switch (type)
        {
            case 0: // the grating alone; in localization a centre sound
                right = random.NextDouble() >= 0.5;
                SetVisual(trial, right, false);
                break;
            case 1: // the sound alone
                right = random.NextDouble() >= 0.5;
                PickLocation(right, out soundAzimuth, out soundElevation);
                break;
            case 2: // coherent: both on one side, the grating capped
                right = random.NextDouble() >= 0.5;
                SetVisual(trial, right, true);
                PickLocation(right, out soundAzimuth, out soundElevation);
                break;
            case 3: // conflict: the grating on one side, the sound on the other
                right = random.NextDouble() >= 0.5;
                SetVisual(trial, right, false);
                PickLocation(!right, out soundAzimuth, out soundElevation);
                break;
            default: // neutral: grey screen (the draw kept, so the stream of draws is as before)
                right = random.NextDouble() >= 0.5;
                break;
        }
        trial.target = right ? Choice.Right : Choice.Left;
        if (task.TaskType == TaskType.DiscriminationGoNoGo)
        {
            // Lick for goModality (Go), withhold for the other modality (NoGo); a trial showing both modalities or neither
            // (neutral, the grey screen) is a NoReward probe, never rewarded or penalised (Song et al. 2017; Pip, 16 Nov 2026).
            bool goShown = (type == 0 || type == 1) && (type == 0) == (task.GoModality == Modality.Visual);
            trial.target = type == 2 || type == 3 || type == 4 ? Answer.NoReward : goShown ? Answer.Go : Answer.NoGo;
            trial.bothSpouts = false;                                   // one spout, the left
            if (trial.target != Answer.Go)
            {
                trial.autoReward = false;   // auto-reward only on a Go trial
                trial.rewardOfferedUl = 0;  // no reward on offer: a withheld NoGo is Correct, unrewarded
            }
        }
        else if (task.TaskType == TaskType.DiscriminationLeftRight)
        {
            // The answer is the modality: leftModality's trials at the left spout, the other's at the right.
            if (type == 0 || type == 1) trial.target = (type == 0) == (task.LeftModality == Modality.Visual) ? Choice.Left : Choice.Right;
        }
        if (RandomRewardProbe(task.TaskType, type))
        {
            // No right answer (left-right's two-modality and neutral trials, localization's conflict and neutral): a
            // random-reward probe, target Random. Both spouts move in, and a coin drawn now, from the seeded stream, decides
            // a choice at either: heads rewarded at that spout, tails penalised as an incorrect choice; a miss as a scored
            // trial's miss. Its rewardOfferedUl is kept, heads or tails (Pip, 16 Nov 2026). It never repeats.
            trial.target = Answer.Random;
            trial.bothSpouts = true;
            trial.autoReward = false;
            trial.probeHeads = random.NextDouble() < 0.5;
        }

        trial.audioEnabled = showsSound;
        if (showsSound)
        {
            var speaker = NearestSpeaker(speakers, soundAzimuth, soundElevation).Value;
            trial.speakerChannel = speaker.Channel;
            trial.audioAzimuthDeg = speaker.Position.Azimuth;
            trial.audioElevationDeg = speaker.Position.Elevation;
        }
        else
        {
            trial.speakerChannel = -1;
            trial.audioAzimuthDeg = double.NaN;
            trial.audioElevationDeg = double.NaN;
        }

        var optogenetics = task.Optogenetics;
        if (optogenetics.UseZapit)
        {
            // drawn + 1: this trial's number, which Next sets once it is drawn.
            ZapitTrial.Draw(random, drawn + 1, conditionCount, optogenetics.TrialsBeforeLaser, optogenetics.LaserOnProbability, optogenetics.LaserPowerMw.Value,
                out trial.laserCondition, out trial.laserOn, out trial.laserPowerMw);
        }
        // Drawn last, so adding it changed no earlier draw; a repeat plays the waveform of the trial it repeats.
        trial.audioWaveformIndex = random.Next(AuditoryStimulusSet.WaveformCount);
        return trial;
    }

    /// <summary>Whether a trial type has no right answer and pays at random: localization's audiovisualConflict and neutral,
    /// discriminationLeftRight's two-modality and neutral trials. discriminationGoNoGo's probes pay nothing.</summary>
    public static bool RandomRewardProbe(TaskType taskType, int type)
    {
        if (taskType == TaskType.Localization) return type == 3 || type == 4;
        return taskType == TaskType.DiscriminationLeftRight && type >= 2;
    }

    int WeightedType()
    {
        var weights = Weights(task, stage);
        int draw = random.Next(weights.Sum());
        for (int type = 0; type < weights.Length; type++)
        {
            draw -= weights[type];
            if (draw < 0) return type;
        }
        throw new InvalidOperationException("No trial type drawn.");
    }

    void SetVisual(Trial trial, bool right, bool capped)
    {
        var visual = task.VisualStimulus;
        var fixedAzimuths = AzimuthCandidates(visual, right, capped);
        if (visual.FixedAzimuthsDeg != null && visual.FixedAzimuthsDeg.Count > 0)
        {
            trial.visualAzimuthDeg = fixedAzimuths[random.Next(fixedAzimuths.Count)];
        }
        else
        {
            double cap = capped ? visual.CoherentAzimuthCapDeg : 180;
            trial.visualAzimuthDeg = right
                ? Uniform(Math.Max(visual.AzimuthMinDeg, 0), Math.Min(visual.AzimuthMaxDeg, cap))
                : Uniform(Math.Max(visual.AzimuthMinDeg, -cap), Math.Min(visual.AzimuthMaxDeg, 0));
        }
        trial.visualElevationDeg = Uniform(visual.ElevationMinDeg, visual.ElevationMaxDeg);
        trial.visualContrast = stage.Contrasts[random.Next(stage.Contrasts.Count)];
    }

    static List<double> AzimuthCandidates(VisualStimulus visual, bool right, bool capped)
    {
        var values = visual.FixedAzimuthsDeg ?? new List<double>();
        return values.Where(a => (right ? a > 0 : a < 0) && (!capped || Math.Abs(a) <= visual.CoherentAzimuthCapDeg)).ToList();
    }

    static bool HasRange(VisualStimulus visual, bool right, bool capped)
    {
        if (visual.FixedAzimuthsDeg != null && visual.FixedAzimuthsDeg.Count > 0) return false;
        double cap = capped ? visual.CoherentAzimuthCapDeg : 180;
        return right ? Math.Max(visual.AzimuthMinDeg, 0) < Math.Min(visual.AzimuthMaxDeg, cap)
                     : Math.Max(visual.AzimuthMinDeg, -cap) < Math.Min(visual.AzimuthMaxDeg, 0);
    }

    void PickLocation(bool right, out double azimuth, out double elevation)
    {
        var candidates = task.AudioStimulus.SpeakerLocations.Where(l => right ? l.Azimuth > 0 : l.Azimuth < 0).ToList();
        var picked = candidates[random.Next(candidates.Count)];
        azimuth = picked.Azimuth;
        elevation = picked.Elevation;
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

/// <summary>A trial's outcome: its row, and what it sets off (the reward, the spouts, the penalty sound, the timeout).</summary>
public class TrialOutcome : ITrialOutcome
{
    public Timestamped<TrialRow> Row;      // the BonsaiData_Trial row, stamped with the outcome's Harp time
    public bool Rewarded;
    public Tuple<string, double> Reward;   // (side, microlitres) for RewardRequests, the side as rig.yml's wiring names it (left, right); null without a reward
    public bool MoveSpouts;
    public LickSpoutCommand SpoutCommand;
    public bool PenaltySound;

    /// <summary>Gets the timeout after the trial, in seconds: the row's penaltyTimeoutSeconds.</summary>
    public double PenaltyTimeoutSeconds { get; private set; }

    /// <summary>The row's rewarded (TrialRow.Rewarded) is the reward: rewardOfferedUl at the target's spout
    /// (discriminationGoNoGo's: the left), a Random trial's at the spout chosen, the other spout drawn back. Anything else
    /// draws both back, with the row's penalty sound and timeout if it was penalised. A stage without spout movements
    /// leaves the spouts where they are.</summary>
    public static TrialOutcome Of(TrialRow row, Trial trial)
    {
        var o = new TrialOutcome { Row = new Timestamped<TrialRow>(row, row.outcomeTime), PenaltySound = row.penaltySound, PenaltyTimeoutSeconds = row.penaltyTimeoutSeconds };
        o.Rewarded = row.rewarded;
        var side = (trial.target == Answer.Random ? row.choice : trial.target) == Choice.Right ? "right" : "left";   // Go is answered at the left spout
        o.Reward = o.Rewarded ? Tuple.Create(side, row.rewardOfferedUl) : null;
        o.MoveSpouts = trial.spoutMovements;
        o.SpoutCommand = !o.Rewarded ? LickSpoutCommand.SpoutsOut
            : side == "left" ? LickSpoutCommand.LeftIn : LickSpoutCommand.RightIn;
        return o;
    }
}

/// <summary>
/// Decision-making's part of each trial, which lib-open-rigs' TrialLoop runs (the interval, the quiet waits, the lead,
/// the laser, the response window and the timeout are the loop's): the draw (TrialDraw), each trial's timing from the
/// task file, and the response rule. In the response window, responseDelaySeconds after the stimulus for responseWindowSeconds,
/// each lick adds 1 (left) or -1 (right) to the decision variable, which moves the grating degreesPerLick per lick from
/// its drawn azimuth; the choice is Left once it reaches thresholdLicks, Right once it reaches -thresholdLicks, else
/// NoResponse at the window's end. A lick before the window is not counted. In discriminationGoNoGo only the left
/// spout counts, and the choice is Lick once its licks reach thresholdLicks. The answer is the trial's target (TrialDraw).
/// </summary>
public class TrialRules : ITrialRules<Trial, DecisionUpdate, TrialOutcome>
{
    /// <summary>The cue that ends the grating, stimulusSeconds after the stimulus.</summary>
    public const string GratingOffCue = "GratingOff";
    /// <summary>The cue that brings the spouts in, stimulusToSpoutsInSeconds after the stimulus.</summary>
    public const string SpoutsInCue = "SpoutsIn";
    /// <summary>The cue that draws the spouts out, drinkSeconds into the next interval, on a stage with spout movements.</summary>
    public const string DrinkCue = "Drink";

    readonly UclOpenCoenDomeDecisionMakingTaskParameters task;
    readonly TrialDraw draw;
    readonly LaserTiming laser;
    int decision;   // the running trial's net licks in its window: +1 each left, -1 each right

    public TrialRules(UclOpenCoenDomeDecisionMakingTaskParameters task, int stage, EqualisedSpeakerArray speakers, bool hardwareTriggered, Func<string, int> zapitConditionCount)
    {
        this.task = task;
        draw = new TrialDraw(task, speakers, stage, zapitConditionCount);
        var optogenetics = task.Optogenetics;
        // The line held laserDurationSeconds from the trigger; the next trial armed once the longest ramp-down a conditions
        // file may have has played (Zapit refuses a StartStimulus until then).
        laser = optogenetics.UseZapit ? LaserTiming.ForDuration(hardwareTriggered, optogenetics.LaserDurationSeconds, ZapitConditions.MaximumRampDownSeconds) : null;
    }

    /// <summary>The rules for a task file's stage on a rig (its speakers, and Zapit's hardware triggering); throws naming every problem.</summary>
    public static TrialRules Create(UclOpenCoenDomeDecisionMakingTaskParameters task, int stage, LabRig rig, Func<string, int> zapitConditionCount)
    {
        return new TrialRules(task, stage, rig.SpeakerArray, rig.Zapit != null && rig.Zapit.HardwareTriggered, zapitConditionCount);
    }

    public LaserTiming Laser { get { return laser; } }

    public Trial Draw(TrialOutcome previous)
    {
        return draw.Next(previous == null ? null : previous.Row.Value.outcome);
    }

    public TrialTiming Timing(Trial trial)
    {
        var structure = task.TrialStructure;
        var stage = draw.Stage;
        var cues = new List<TrialCue>
        {
            new TrialCue(GratingOffCue, CueAnchor.Stimulus, structure.StimulusSeconds),
            new TrialCue(SpoutsInCue, CueAnchor.Stimulus, structure.StimulusToSpoutsInSeconds, true),
        };
        if (trial.spoutMovements) cues.Add(new TrialCue(DrinkCue, CueAnchor.NextIti, structure.DrinkSeconds));
        return new TrialTiming
        {
            ItiSeconds = trial.itiSeconds,
            LaserToStimulusSeconds = task.Optogenetics.LaserToStimulusSeconds,   // its lead in every experiment, so timing never reveals Zapit
            LickQuietSeconds = stage.LickQuietSeconds,
            WheelQuietSeconds = stage.WheelQuietSeconds,
            WheelQuietCm = stage.WheelQuietCm,
            Laser = trial.laserCondition > 0,                                    // every trial of an optogenetics experiment, laser on or off
            ResponseDelaySeconds = structure.ResponseDelaySeconds,
            ResponseWindowSeconds = structure.ResponseWindowSeconds,
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
        var threshold = task.Response.ThresholdLicks;
        if (decision >= threshold) return Outcome(trial, goNoGo ? Choice.Lick : Choice.Left, times);
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
        return new DecisionUpdate { GratingAzimuthDeg = decision * task.Response.DegreesPerLick + trial.visualAzimuthDeg };
    }

    TrialOutcome Outcome(Trial trial, string choice, TrialTimes times)
    {
        var row = TrialRow.Build(choice, trial, times, task.Penalty.TimeoutSeconds, task.Penalty.Sound.Enabled);
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
        return TrialLoop.Observe(rules, clock, wheelCm, lickLeft, lickRight, zapitReplies);
    }
}

[Combinator]
[Description("Where the spouts move once the stimulus is on: both in on a both-spouts trial, else the target's alone.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class SpoutsIn
{
    public IObservable<LickSpoutCommand> Process(IObservable<Trial> source)
    {
        return source.Select(Command);
    }

    public static LickSpoutCommand Command(Trial trial)
    {
        if (trial.bothSpouts) return LickSpoutCommand.SpoutsIn;
        return trial.target == Choice.Right ? LickSpoutCommand.RightIn : LickSpoutCommand.LeftIn;   // go-no-go: the left
    }
}
