using Bonsai;
using Bonsai.Harp;
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

/// <summary>One trial as drawn: its fields are named as the trial log's columns where they are one.</summary>
public class Trial : IZapitTrial
{
    int ITrial.TrialNumber { get { return trialNumber; } }
    int IZapitTrial.LaserCondition { get { return laserCondition; } }
    bool IZapitTrial.LaserOn { get { return laserOn; } }
    double IZapitTrial.LaserPowerMw { get { return laserPowerMw; } }

    public int trialNumber;            // from 1, repeats counted
    public string trialType;           // TrialDraw.TypeNames: what the trial shows
    public int trialCondition;         // the 0-based index of its condition in the task's trialConditions
    public string target;              // Left or Right; in discriminationGoNoGo Go or NoGo; on a trial with no right answer NoReward or Random (Target)
    public double rewardOfferedUl;     // what a reward on this trial gives, on every trial; 0 where none is on offer
    public bool bothSpoutsOffered;
    public int repeatCount;            // 0 for a new draw
    public double itiSeconds;          // the interval drawn for this trial: its start to the stimulus, with a quiet mouse
    public double visualAzimuthDeg;
    public double visualElevationDeg;
    public double visualContrast;      // 0: grey screen
    public double visualPhaseRad;
    public bool auditoryOn;            // false: no sound (discrimination's visual and neutral trials)
    public int speakerChannel;         // the mixer channel of rig.yml's speaker nearest the drawn location; -1 when silent
    public double auditoryAzimuthDeg;     // that speaker's azimuth; NaN when silent
    public double auditoryElevationDeg;
    public int auditorySoundFileIndex;
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
[Description("Checks the task at Go and gives the trials' rules (TrialRules, tested C#): trainingPipeline names TrainingStage's stage, each trial condition builds and the draw's rules hold on rig.yml's speakers for the task and every condition (TrialDraw.CheckTask), and with optogenetics the Zapit conditions file. Input: the task file's text, TrainingStage and rig.yml; stops the workflow naming every problem.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class CheckTaskAtGo
{
    public IObservable<TrialRules> Process(IObservable<Tuple<string, int, LabRig>> source)
    {
        return source.Select(input => TrialRules.Create(input.Item1, input.Item2, input.Item3, null));
    }
}

/// <summary>The target column's answers other than a side: discriminationGoNoGo's Go and NoGo, and on a trial with no right
/// answer (a probe, outcome Probe) NoReward (go/no-go's: never rewarded or penalised) or Random (localization's and
/// left-right's: a choice rewarded or penalised on a coin, TrialDraw.RandomRewardProbe).</summary>
public static class Target
{
    public const string Go = "Go";
    public const string NoGo = "NoGo";
    public const string NoReward = "NoReward";
    public const string Random = "Random";
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

/// <summary>One of the task's trialConditions as built at Go (TrialDraw.BuildConditions).</summary>
public class TrialConditionDraw
{
    /// <summary>The condition's type (TrialDraw.Keys).</summary>
    public string Type;
    /// <summary>What its trials show (TrialDraw.TypeNames): the type, a role filled by goModality or leftModality.</summary>
    public string TrialType;
    /// <summary>Its relative draw weight.</summary>
    public int Weight;
    /// <summary>Where it is, for a message: trialConditions[index] (type).</summary>
    public string Where;
    /// <summary>The task with its set over it, one per combination of its list cells, picked uniformly per trial.</summary>
    public List<TrialConditionVariant> Variants;
}

/// <summary>One combination of a condition's list cells: the task with the condition's set over it.</summary>
public class TrialConditionVariant
{
    /// <summary>The task as this combination's trials draw it.</summary>
    public UclOpenCoenDomeDecisionMakingTaskParameters Task;
    /// <summary>The picks that make it, for a message ("" with no list cell).</summary>
    public string Cells;
}

/// <summary>The draw's state and rules, apart from Bonsai so a test can run them.</summary>
public class TrialDraw
{
    public const string Visual = "visual";                           // the grating alone
    public const string Auditory = "auditory";                       // the sound alone
    public const string AudiovisualCoherent = "audiovisualCoherent"; // both on one side
    public const string AudiovisualConflict = "audiovisualConflict"; // the grating on one side, the sound on the other
    public const string Neutral = "neutral";                         // grey screen
    /// <summary>The trial types, the trial row's trialType and localization's trialConditions types (coen-dome-navigation's names).</summary>
    public static readonly string[] TypeNames = { Visual, Auditory, AudiovisualCoherent, AudiovisualConflict, Neutral };
    // The discriminations' trialConditions types and rulesPerType keys by role, goModality or leftModality filling go or left.
    public const string GoKey = "go", NoGoKey = "noGo", LeftKey = "left", RightKey = "right";
    const double CentreAzimuth = 0, CentreElevation = 15;  // the sound of visual and neutral trials in localization

    /// <summary>The fields a pipeline may make condition-level, each with the trial types (Keys) it applies to: task.py's
    /// CONDITION_FIELDS, the schema's x-conditionFields (a test holds them equal). Only what New alone reads per trial.</summary>
    public static readonly Dictionary<string, string[]> ConditionFields = new Dictionary<string, string[]>
    {
        { "contrasts", new[] { Visual, AudiovisualCoherent, AudiovisualConflict, GoKey, NoGoKey, LeftKey, RightKey } },
        { "visualStimulus.startAzimuthsDeg", new[] { Visual, Auditory, AudiovisualCoherent, AudiovisualConflict, GoKey, NoGoKey, LeftKey, RightKey } },
        { "visualStimulus.elevationDeg", new[] { Visual, Auditory, AudiovisualCoherent, AudiovisualConflict, GoKey, NoGoKey, LeftKey, RightKey } },
        { "optogenetics.laserOnProbability", new[] { Visual, Auditory, AudiovisualCoherent, AudiovisualConflict, Neutral, GoKey, NoGoKey, LeftKey, RightKey } },
        { "reward.rewardSizeUl", new[] { Visual, Auditory, AudiovisualCoherent, AudiovisualConflict, Neutral, GoKey, NoGoKey, LeftKey, RightKey } },
        { "autoRewardProbability", new[] { Visual, Auditory, AudiovisualCoherent, AudiovisualConflict, Neutral, GoKey, NoGoKey, LeftKey, RightKey } },
        { "bothSpoutsProbability", new[] { Visual, Auditory, AudiovisualCoherent, AudiovisualConflict, Neutral, GoKey, NoGoKey, LeftKey, RightKey } },
    };
    /// <summary>The most combinations of list-valued scalar cells one condition may expand into (task.py MAX_COMBINATIONS).</summary>
    public const int MaxCombinations = 64;

    readonly UclOpenCoenDomeDecisionMakingTaskParameters task;
    readonly List<TrialConditionDraw> conditions;
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
    public IList<TrialConditionDraw> Conditions { get { return conditions; } }

    /// <summary>The draw of a task file's text (as rig-control wrote it at launch) on a rig's speakers; throws naming every problem.</summary>
    public TrialDraw(string taskText, EqualisedSpeakerArray speakers, int stage, Func<string, int> zapitConditionCount)
    {
        conditions = CheckTask(taskText, stage, speakers, out task);
        this.speakers = speakers;
        var optogenetics = task.Optogenetics;
        if (optogenetics.Enabled)
        {
            if (!(optogenetics.LaserPowerMw > 0))
            {
                throw new InvalidOperationException("optogenetics is enabled, so optogenetics.laserPowerMw must be a power in mW above 0.");
            }
            var path = optogenetics.ZapitConditionsPath;
            conditionCount = zapitConditionCount != null ? zapitConditionCount(path) : ZapitConditions.Check(path);
        }
        Seed = RngSeed.Resolve(task.RngSeed);
        random = new Random(Seed);
    }

    /// <summary>The task the text holds and its conditions as drawn (BuildConditions); throws unless the values are TrainingStage's
    /// pipeline stage (trainingPipeline, which app-rig-control fills at launch) and the task and every condition can be run on
    /// these speakers, naming every problem.</summary>
    public static List<TrialConditionDraw> CheckTask(string taskText, int stage, EqualisedSpeakerArray speakers, out UclOpenCoenDomeDecisionMakingTaskParameters task)
    {
        task = Read(taskText).TaskParameters;
        var pipeline = task.TrainingPipeline;
        if (pipeline == null || string.IsNullOrEmpty(pipeline.Sha256))
        {
            throw new InvalidOperationException(string.Format(
                "TrainingStage is {0}, but the task file's trainingPipeline is empty: launch it from app-rig-control, which writes the stage's values and fills it.", stage));
        }
        if (pipeline.Id != stage)
        {
            throw new InvalidOperationException(string.Format(
                "TrainingStage is {0}, but the task file's values are training pipeline stage {1} ({2}).", stage, pipeline.Id, pipeline.Label));
        }
        var problems = new List<string>();
        Check(problems, task, speakers);
        var built = problems.Count == 0 ? BuildConditions(taskText, task, problems) : null;
        if (built != null)
        {
            // Every combination of every condition, each problem named by its condition (a value the task's own check
            // already named is said once).
            foreach (var condition in built)
            {
                foreach (var variant in condition.Variants)
                {
                    var own = new List<string>();
                    Check(own, variant.Task, speakers);
                    problems.AddRange(own.Select(p => condition.Where + (variant.Cells.Length == 0 ? "" : " with " + variant.Cells) + ": " + p));
                }
            }
        }
        if (problems.Count > 0) throw new InvalidOperationException("The task cannot be run: " + string.Join("; ", problems.Distinct()) + ".");
        return built;
    }

    /// <summary>The task file's text read as Bonsai's DeserializeFromYaml reads it.</summary>
    public static UclOpenCoenDomeDecisionMakingTaskLogic Read(string text)
    {
        var deserializer = new YamlDotNet.Serialization.DeserializerBuilder()
            .WithTypeConverter(new YamlDotNet.Serialization.Converters.DateTimeOffsetConverter())
            .Build();
        return deserializer.Deserialize<UclOpenCoenDomeDecisionMakingTaskLogic>(new YamlDotNet.Core.MergingParser(new YamlDotNet.Core.Parser(new StringReader(text))));
    }

    /// <summary>Each trial condition as drawn: its set written over a copy of the task file's text at its dotted paths and read
    /// back, once for every combination of its list-valued scalar cells (a list in a cell whose task-level value is a scalar,
    /// one picked per trial; a list field such as contrasts takes the list as is), at most MaxCombinations. Adds a problem
    /// for each condition that cannot be built, and returns null then.</summary>
    public static List<TrialConditionDraw> BuildConditions(string taskText, UclOpenCoenDomeDecisionMakingTaskParameters task, List<string> problems)
    {
        var keys = Keys(task.TaskType);
        var nodes = Parameters(Load(taskText)).Children;
        YamlNode sequence;
        var conditionNodes = nodes.TryGetValue(new YamlScalarNode("trialConditions"), out sequence) && sequence is YamlSequenceNode
            ? ((YamlSequenceNode)sequence).Children : new List<YamlNode>();
        var typed = task.TrialConditions ?? new List<TrialCondition>();
        if (typed.Count == 0) problems.Add("trialConditions needs a condition");
        var built = new List<TrialConditionDraw>();
        for (int i = 0; i < typed.Count; i++)
        {
            var c = typed[i];
            var where = string.Format("trialConditions[{0}] ({1})", i, c.Type);
            var before = problems.Count;
            if (!keys.Contains(c.Type)) problems.Add(string.Format("{0}: its type is not one of {1} in {2}", where, string.Join(", ", keys), TaskTypeName(task.TaskType)));
            if (c.Weight < 0) problems.Add(where + ": its weight is below 0");
            var cells = new List<KeyValuePair<string, List<YamlNode>>>();
            YamlNode set;
            if (((YamlMappingNode)conditionNodes[i]).Children.TryGetValue(new YamlScalarNode("set"), out set) && set is YamlMappingNode)
            {
                foreach (var cell in ((YamlMappingNode)set).Children)
                {
                    var path = ((YamlScalarNode)cell.Key).Value;
                    string[] types;
                    if (!ConditionFields.TryGetValue(path, out types) || !types.Contains(c.Type))
                    {
                        problems.Add(string.Format("{0} sets {1}, which is not a condition field of {2} (x-conditionFields)", where, path, c.Type));
                        continue;
                    }
                    if (path.StartsWith("optogenetics.") && !task.Optogenetics.Enabled) problems.Add(string.Format("{0} sets {1} with optogenetics off", where, path));
                    var list = cell.Value as YamlSequenceNode;
                    var options = list != null && At(nodes, path) is YamlScalarNode ? list.Children.ToList() : new List<YamlNode> { cell.Value };
                    if (options.Count == 0) problems.Add(string.Format("{0}: {1} is an empty list", where, path));
                    cells.Add(new KeyValuePair<string, List<YamlNode>>(path, options));
                }
            }
            var count = cells.Aggregate(1L, (n, cell) => n * cell.Value.Count);
            if (count > MaxCombinations) problems.Add(string.Format("{0} expands into {1} combinations, more than {2}", where, count, MaxCombinations));
            if (problems.Count > before) continue;
            var draw = new TrialConditionDraw { Type = c.Type, TrialType = StimulusType(task, c.Type), Weight = c.Weight, Where = where, Variants = new List<TrialConditionVariant>() };
            for (int n = 0; n < count; n++)
            {
                var stream = Load(taskText);
                var parameters = Parameters(stream).Children;
                var named = new List<string>();
                int rest = n;
                foreach (var cell in cells)
                {
                    var value = cell.Value[rest % cell.Value.Count];
                    rest /= cell.Value.Count;
                    Put(parameters, cell.Key, value);
                    if (cell.Value.Count > 1) named.Add(cell.Key + " " + value);
                }
                var writer = new StringWriter();
                stream.Save(writer, false);
                try
                {
                    draw.Variants.Add(new TrialConditionVariant { Task = Read(writer.ToString()).TaskParameters, Cells = string.Join(", ", named) });
                }
                catch (YamlDotNet.Core.YamlException e)
                {
                    problems.Add(string.Format("{0}: {1}", where, e.InnerException != null ? e.InnerException.Message : e.Message));
                    break;
                }
            }
            built.Add(draw);
        }
        if (problems.Count == 0)
        {
            if (built.Sum(c => c.Weight) <= 0) problems.Add("trialConditions needs a condition with a weight above 0");
            if (task.TaskType == TaskType.DiscriminationGoNoGo && !built.Any(c => c.Type == GoKey && c.Weight > 0)) problems.Add("trialConditions has no Go trials: no go condition has a weight above 0");
        }
        return problems.Count == 0 ? built : null;
    }

    static YamlStream Load(string text)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        return stream;
    }

    static YamlMappingNode Parameters(YamlStream stream)
    {
        return (YamlMappingNode)((YamlMappingNode)stream.Documents[0].RootNode).Children[new YamlScalarNode("taskParameters")];
    }

    // The node at a dotted path under taskParameters; a key the file leaves out is read as null, a scalar.
    static YamlNode At(IDictionary<YamlNode, YamlNode> nodes, string path)
    {
        var parts = path.Split('.');
        for (int i = 0; i < parts.Length - 1; i++) nodes = ((YamlMappingNode)nodes[new YamlScalarNode(parts[i])]).Children;
        YamlNode node;
        return nodes.TryGetValue(new YamlScalarNode(parts[parts.Length - 1]), out node) ? node : new YamlScalarNode();
    }

    static void Put(IDictionary<YamlNode, YamlNode> nodes, string path, YamlNode value)
    {
        var parts = path.Split('.');
        for (int i = 0; i < parts.Length - 1; i++) nodes = ((YamlMappingNode)nodes[new YamlScalarNode(parts[i])]).Children;
        nodes[new YamlScalarNode(parts[parts.Length - 1])] = value;
    }

    /// <summary>What a condition type shows (TypeNames): localization's types and the two-modality and neutral types are
    /// themselves; go or left is goModality's or leftModality's modality alone, noGo or right the other's.</summary>
    public static string StimulusType(UclOpenCoenDomeDecisionMakingTaskParameters task, string type)
    {
        bool first = type == GoKey || type == LeftKey;
        if (!first && type != NoGoKey && type != RightKey) return type;
        var modality = type == GoKey || type == NoGoKey ? task.GoModality : task.LeftModality;
        return (modality == Modality.Visual) == first ? Visual : Auditory;
    }

    // Bonsai reads the task file without pydantic, so the rules the draw needs are checked again here.
    static void Check(List<string> problems, UclOpenCoenDomeDecisionMakingTaskParameters task, EqualisedSpeakerArray speakers)
    {
        KeyProblems(problems, "repeats.rulesPerType", task.TaskType, RuleKeys(task.TaskType),task.Repeats.RulesPerType == null ? null : AllKeys(task.Repeats.RulesPerType));
        if (task.TaskType == TaskType.DiscriminationGoNoGo && task.BothSpoutsProbability != 0) problems.Add("bothSpoutsProbability must be 0 in discriminationGoNoGo: only the left spout moves in");
        // The SpoutsIn cue's LeftOnly or RightOnly draws the other spout back, which a task without spout movements never returns.
        if (task.TaskType != TaskType.DiscriminationGoNoGo && !task.SpoutMovements && task.BothSpoutsProbability != 1) problems.Add("bothSpoutsProbability must be 1 with spoutMovements false: a one-spout trial draws the other spout back");
        if (task.Contrasts == null || task.Contrasts.Count == 0 || task.Contrasts.Any(c => !(c > 0))) problems.Add("contrasts must each be above 0, at least one");
        // The positions (task.py's same rules): each azimuth signed and never 0 (its sign is the side), the speaker nearest it
        // and nearest its mirror (a conflict trial's sound) each on its side.
        var visual = task.VisualStimulus;
        var elevation = visual.ElevationDeg;
        bool elevationOk = elevation >= -90 && elevation <= 90;
        if (!elevationOk) problems.Add("visualStimulus.elevationDeg must be in [-90, 90]");
        if (visual.StartAzimuthsDeg == null || visual.StartAzimuthsDeg.Count == 0) problems.Add("visualStimulus.startAzimuthsDeg needs an azimuth");
        foreach (var listed in visual.StartAzimuthsDeg ?? new List<double>())
        {
            if (!(listed >= -180 && listed <= 180)) problems.Add(string.Format("visualStimulus.startAzimuthsDeg value {0} is outside [-180, 180]", listed));
            else if (listed == 0) problems.Add("visualStimulus.startAzimuthsDeg must not hold 0: the sign is the trial's side");
            else if (elevationOk)
            {
                foreach (var azimuth in new[] { listed, -listed })
                {
                    var speaker = NearestSpeaker(speakers, azimuth, elevation);
                    if (azimuth > 0 ? speaker.Value.Position.Azimuth <= 0 : speaker.Value.Position.Azimuth >= 0)
                    {
                        problems.Add(string.Format("rig.yml's speaker nearest azimuth {0} (elevation {1}) is '{2}', not on the {3}", azimuth, elevation, speaker.Key, azimuth > 0 ? "right" : "left"));
                    }
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
        finite("trialStructure.drinkSeconds", structure.DrinkSeconds);
        finite("trialStructure.responseDelaySeconds", structure.ResponseDelaySeconds);
        if (structure.ResponseWindowSeconds == null) problems.Add("trialStructure.responseWindowSeconds must be given: this task's response window always closes");
        foreach (var p in TaskBlocks.ResponseWindowProblems(structure.ResponseWindowSeconds)) problems.Add("trialStructure." + p);
        foreach (var p in TaskBlocks.AutoRewardProblems(task.AutoRewardProbability)) problems.Add(p);
        finite("trialStructure.stimulusSeconds", structure.StimulusSeconds);
        finite("trialStructure.stimulusToSpoutsInSeconds", structure.StimulusToSpoutsInSeconds);
        finite("penalty.timeoutSeconds", task.Penalty.TimeoutSeconds);
        // The quiet waits, the lab's one check each (OpenRigs.Core QuietWait: finite, 0 or more, at most 10 s and 20 cm, the
        // wheel's pair both set or both 0): a long wait and a licking mouse would hold the trial start with no message.
        foreach (var p in QuietWait.LickProblems(structure.LickQuietSeconds)) problems.Add("trialStructure." + p);
        foreach (var p in QuietWait.WheelProblems(structure.WheelQuietSeconds, structure.WheelQuietCm)) problems.Add("trialStructure." + p);
        if (double.IsNaN(task.Optogenetics.LaserToStimulusSeconds) || double.IsInfinity(task.Optogenetics.LaserToStimulusSeconds)) problems.Add("optogenetics.laserToStimulusSeconds must be a finite number");
        if (task.Optogenetics.Enabled) positive("optogenetics.laserDurationSeconds", task.Optogenetics.LaserDurationSeconds);
        if (structure.StimulusSeconds > structure.ResponseDelaySeconds + structure.ResponseWindowSeconds) problems.Add("trialStructure.stimulusSeconds is longer than responseDelaySeconds plus responseWindowSeconds");
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
        if (opto.Enabled && opto.LaserDurationSeconds > longest + 1e-9)
        {
            problems.Add(string.Format("optogenetics.laserDurationSeconds ({0} s) must be at most {1} s (responseDelaySeconds + itiMinSeconds - {2} s ramp-down - {3} s reply margin), so Zapit can be armed for the next trial in time", opto.LaserDurationSeconds, longest, ZapitConditions.MaximumRampDownSeconds, ZapitTrial.ReplyMarginSeconds));
        }
        // Each RewardValve piece refuses a reward over MaxRewardUl when it plans it, mid-session (task.py's same rule).
        var reward = task.Reward;
        if (reward.LargeRewardProbability > 0 && reward.LargeRewardMultiplier > 1 && reward.RewardSizeUl * reward.LargeRewardMultiplier > LoadRewardLine.DefaultMaxRewardUl + 1e-9)
        {
            problems.Add(string.Format("reward.rewardSizeUl ({0} ul) x reward.largeRewardMultiplier ({1}) is {2} ul, more than {3} ul, the most one reward may ask for", reward.RewardSizeUl, reward.LargeRewardMultiplier, reward.RewardSizeUl * reward.LargeRewardMultiplier, LoadRewardLine.DefaultMaxRewardUl));
        }
        // The condition fields' bounds (a condition's cell is not checked by pydantic before Go).
        if (!(reward.RewardSizeUl > 0 && reward.RewardSizeUl <= LoadRewardLine.DefaultMaxRewardUl)) problems.Add(string.Format("reward.rewardSizeUl must be above 0 and at most {0} ul", LoadRewardLine.DefaultMaxRewardUl));
        if (!(task.BothSpoutsProbability >= 0 && task.BothSpoutsProbability <= 1)) problems.Add("bothSpoutsProbability must be in [0, 1]");
        if (!(opto.LaserOnProbability >= 0 && opto.LaserOnProbability <= 1)) problems.Add("optogenetics.laserOnProbability must be in [0, 1]");
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

    // Every condition type and rulesPerType key, and the five each task type has, in its order (task.py TRIAL_KEYS):
    // localization's by what a trial shows, the discriminations' by role, goModality or leftModality filling go or left.
    static readonly string[] AllKeyNames = { Visual, Auditory, AudiovisualCoherent, AudiovisualConflict, Neutral, GoKey, NoGoKey, LeftKey, RightKey };

    /// <summary>The five condition types (and rulesPerType's keys among them) in a task type: localization's TypeNames; discriminationGoNoGo's
    /// go, noGo, audiovisualCoherent, audiovisualConflict, neutral; discriminationLeftRight's left, right and the same three.</summary>
    public static string[] Keys(TaskType type)
    {
        switch (type)
        {
            case TaskType.DiscriminationGoNoGo: return new[] { GoKey, NoGoKey, AudiovisualCoherent, AudiovisualConflict, Neutral };
            case TaskType.DiscriminationLeftRight: return new[] { LeftKey, RightKey, AudiovisualCoherent, AudiovisualConflict, Neutral };
            default: return (string[])TypeNames.Clone();
        }
    }

    // rulesPerType's values in AllKeyNames' order, null where it gives none.
    static RepeatRule?[] AllKeys(TrialTypeRules r)
    {
        return new[] { r.Visual, r.Auditory, r.AudiovisualCoherent, null, null, r.Go, r.NoGo, r.Left, r.Right };
    }

    // rulesPerType's keys (task.py RULE_KEYS): only a trial with a right answer repeats, so localization names visual,
    // auditory, audiovisualCoherent and the discriminations their two one-modality roles.
    static string[] RuleKeys(TaskType type)
    {
        switch (type)
        {
            case TaskType.DiscriminationGoNoGo: return new[] { GoKey, NoGoKey };
            case TaskType.DiscriminationLeftRight: return new[] { LeftKey, RightKey };
            default: return new[] { Visual, Auditory, AudiovisualCoherent };
        }
    }

    static T[] Pick<T>(T?[] all, string[] keys) where T : struct
    {
        return keys.Select(k => all[Array.IndexOf(AllKeyNames, k)].GetValueOrDefault()).ToArray();
    }

    static void KeyProblems<T>(List<string> problems, string name, TaskType type, string[] keys, T?[] values) where T : struct
    {
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

    static RepeatRule Rule(UclOpenCoenDomeDecisionMakingTaskParameters task, string trialType)
    {
        // RuleKeys by stimulus are TypeNames' first two or three: a type past them has no right answer, never repeated.
        var byStimulus = ByStimulus(task, Pick(AllKeys(task.Repeats.RulesPerType), RuleKeys(task.TaskType)));
        var type = Array.IndexOf(TypeNames, trialType);
        return type >= 0 && type < byStimulus.Length ? byStimulus[type] : RepeatRule.Never;
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
        // Every trial, a repeat too, draws its own interval.
        var structure = task.TrialStructure;
        trial.itiSeconds = Uniform(structure.ItiMinSeconds, structure.ItiMaxSeconds);
        last = trial;
        return trial;
    }

    bool ShouldRepeat(string outcome)
    {
        if (!task.AllowRepeats || last.repeatCount >= task.Repeats.MaxRepeats || outcome == TrialOutcomes.Probe) return false;
        switch (Rule(task, last.trialType))
        {
            case RepeatRule.OnFailure: return outcome != TrialOutcomes.Correct;
            case RepeatRule.OnNoResponse: return outcome == TrialOutcomes.NoResponse;
            default: return false;
        }
    }

    Trial New()
    {
        // The condition by weight, then one of its combinations (its list cells' picks), whose values the trial draws from.
        int index = WeightedCondition();
        var condition = conditions[index];
        var p = condition.Variants.Count == 1 ? condition.Variants[0].Task : condition.Variants[random.Next(condition.Variants.Count)].Task;
        var trial = new Trial();
        trial.trialCondition = index;
        trial.autoReward = random.NextDouble() < p.AutoRewardProbability;
        trial.spoutMovements = task.SpoutMovements;
        trial.bothSpoutsOffered = random.NextDouble() < p.BothSpoutsProbability;
        var large = random.NextDouble() < p.Reward.LargeRewardProbability;
        trial.rewardOfferedUl = large ? p.Reward.RewardSizeUl * p.Reward.LargeRewardMultiplier : p.Reward.RewardSizeUl;
        trial.visualPhaseRad = 2 * Math.PI * random.NextDouble();

        string type = condition.TrialType;
        trial.trialType = type;
        bool discrimination = task.TaskType != TaskType.Localization;
        // Localization plays a centre sound on visual and neutral trials; the discriminations' are silent.
        bool showsSound = !discrimination || (type != Visual && type != Neutral);

        // The start azimuth, one of the condition's: its sign is the side. The grating starts there; the sound plays on
        // the speaker nearest it, a conflict trial's nearest its mirror.
        var visual = p.VisualStimulus;
        double azimuth = type == Neutral ? 0 : visual.StartAzimuthsDeg[random.Next(visual.StartAzimuthsDeg.Count)];
        double elevation = visual.ElevationDeg;
        double soundAzimuth = CentreAzimuth, soundElevation = CentreElevation;
        if (type == Visual || type == AudiovisualCoherent || type == AudiovisualConflict)
        {
            trial.visualAzimuthDeg = azimuth;
            trial.visualElevationDeg = elevation;
            trial.visualContrast = p.Contrasts[random.Next(p.Contrasts.Count)];
        }
        if (type == Auditory || type == AudiovisualCoherent || type == AudiovisualConflict)
        {
            soundAzimuth = type == AudiovisualConflict ? -azimuth : azimuth;
            soundElevation = elevation;
        }
        trial.target = azimuth > 0 ? Choice.Right : Choice.Left;
        if (task.TaskType == TaskType.DiscriminationGoNoGo)
        {
            // Lick for goModality (Go), withhold for the other modality (NoGo); a trial showing both modalities or neither
            // (neutral, the grey screen) is a NoReward probe, never rewarded or penalised (Song et al. 2017).
            bool oneModality = type == Visual || type == Auditory;
            bool goShown = oneModality && (type == Visual) == (task.GoModality == Modality.Visual);
            trial.target = !oneModality ? Target.NoReward : goShown ? Target.Go : Target.NoGo;
            trial.bothSpoutsOffered = false;                                   // one spout, the left
            if (trial.target != Target.Go)
            {
                trial.autoReward = false;   // auto-reward only on a Go trial
                trial.rewardOfferedUl = 0;  // no reward on offer: a withheld NoGo is Correct, unrewarded
            }
        }
        else if (task.TaskType == TaskType.DiscriminationLeftRight)
        {
            // The answer is the modality: leftModality's trials at the left spout, the other's at the right.
            if (type == Visual || type == Auditory) trial.target = (type == Visual) == (task.LeftModality == Modality.Visual) ? Choice.Left : Choice.Right;
        }
        if (RandomRewardProbe(task.TaskType, type))
        {
            // No right answer (left-right's two-modality and neutral trials, localization's conflict and neutral): a
            // random-reward probe, target Random. Both spouts move in, and a coin drawn now, from the seeded stream, decides
            // a choice at either: heads rewarded at that spout, tails penalised as an incorrect choice; a miss as a scored
            // trial's miss. Its rewardOfferedUl is kept, heads or tails. It never repeats.
            trial.target = Target.Random;
            trial.bothSpoutsOffered = true;
            trial.autoReward = false;
            trial.probeHeads = random.NextDouble() < 0.5;
        }

        trial.auditoryOn = showsSound;
        if (showsSound)
        {
            var speaker = NearestSpeaker(speakers, soundAzimuth, soundElevation).Value;
            trial.speakerChannel = speaker.Channel;
            trial.auditoryAzimuthDeg = speaker.Position.Azimuth;
            trial.auditoryElevationDeg = speaker.Position.Elevation;
        }
        else
        {
            trial.speakerChannel = -1;
            trial.auditoryAzimuthDeg = double.NaN;
            trial.auditoryElevationDeg = double.NaN;
        }

        var optogenetics = task.Optogenetics;
        if (optogenetics.Enabled)
        {
            // drawn + 1: this trial's number, which Next sets once it is drawn.
            ZapitTrial.Draw(random, drawn + 1, conditionCount, optogenetics.TrialsBeforeLaser, p.Optogenetics.LaserOnProbability, optogenetics.LaserPowerMw.Value,
                out trial.laserCondition, out trial.laserOn, out trial.laserPowerMw);
        }
        // Drawn last, so adding it changed no earlier draw; a repeat plays the waveform of the trial it repeats.
        trial.auditorySoundFileIndex = random.Next(AuditoryStimulusSet.WaveformCount);
        return trial;
    }

    /// <summary>Whether a trial type has no right answer and pays at random: localization's audiovisualConflict and neutral,
    /// discriminationLeftRight's two-modality and neutral trials. discriminationGoNoGo's probes pay nothing.</summary>
    public static bool RandomRewardProbe(TaskType taskType, string trialType)
    {
        if (taskType == TaskType.Localization) return trialType == AudiovisualConflict || trialType == Neutral;
        return taskType == TaskType.DiscriminationLeftRight && trialType != Visual && trialType != Auditory;
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

/// <summary>A trial's outcome (lib-open-rigs' TrialOutcome: its row, the reward, the penalty sound, the timeout) and the
/// spouts it moves.</summary>
public class TrialOutcome : OpenRigs.Trials.TrialOutcome<TrialRow>
{
    public bool SpoutMovements;
    public LickSpoutCommand SpoutCommand;

    TrialOutcome(TrialRow row, string instance) : base(row, instance) { }

    /// <summary>The row's rewarded (TrialRow.Rewarded) is the reward: rewardOfferedUl at the target's spout
    /// (discriminationGoNoGo's: the left), a Random trial's at the spout chosen, the other spout drawn back. Anything else
    /// draws both back, with the row's penalty sound and timeout if it was penalised. A task without spout movements
    /// leaves the spouts where they are.</summary>
    public static TrialOutcome Of(TrialRow row, Trial trial)
    {
        // The valve's instance as rig.yml's wiring names it; Go is answered at the left spout.
        var instance = (trial.target == Target.Random ? row.choice : trial.target) == Choice.Right ? "right" : "left";
        var o = new TrialOutcome(row, instance);
        o.SpoutMovements = trial.spoutMovements;
        o.SpoutCommand = !o.Rewarded ? LickSpoutCommand.SpoutsOut
            : instance == "left" ? LickSpoutCommand.LeftOnly : LickSpoutCommand.RightOnly;
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
    /// <summary>The cue that draws the spouts out, drinkSeconds into the next interval, with spout movements.</summary>
    public const string DrinkCue = "Drink";

    readonly UclOpenCoenDomeDecisionMakingTaskParameters task;
    readonly TrialDraw draw;
    readonly LaserTiming laser;
    int decision;   // the running trial's net licks in its window: +1 each left, -1 each right

    public TrialRules(string taskText, int stage, EqualisedSpeakerArray speakers, bool hardwareTriggered, Func<string, int> zapitConditionCount)
    {
        draw = new TrialDraw(taskText, speakers, stage, zapitConditionCount);
        task = draw.Task;
        var optogenetics = task.Optogenetics;
        // The line held laserDurationSeconds from the trigger; the next trial armed once the longest ramp-down a conditions
        // file may have has played (Zapit refuses a StartStimulus until then).
        laser = optogenetics.Enabled ? LaserTiming.ForDuration(hardwareTriggered, optogenetics.LaserDurationSeconds, ZapitConditions.MaximumRampDownSeconds) : null;
    }

    /// <summary>The rules for a task file's text and stage on a rig (its speakers, and Zapit's hardware triggering); throws naming every problem.</summary>
    public static TrialRules Create(string taskText, int stage, LabRig rig, Func<string, int> zapitConditionCount)
    {
        return new TrialRules(taskText, stage, rig.SpeakerArray, rig.Zapit != null && rig.Zapit.HardwareTriggered, zapitConditionCount);
    }

    public LaserTiming Laser { get { return laser; } }

    public Trial Draw(TrialOutcome previous)
    {
        return draw.Next(previous == null ? null : previous.Row.Value.outcome);
    }

    public TrialTiming Timing(Trial trial)
    {
        var structure = task.TrialStructure;
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
        return TrialLoop.Observe(rules, clock, new[] { lickLeft, lickRight }, zapitReplies, wheelCm);
    }
}

[Combinator]
[Description("Where the spouts move at the SpoutsIn cue: both in on a both-spouts trial, else the target's alone.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class SpoutsInCommand
{
    public IObservable<LickSpoutCommand> Process(IObservable<Trial> source)
    {
        return source.Select(Command);
    }

    public static LickSpoutCommand Command(Trial trial)
    {
        if (trial.bothSpoutsOffered) return LickSpoutCommand.SpoutsIn;
        return trial.target == Choice.Right ? LickSpoutCommand.RightOnly : LickSpoutCommand.LeftOnly;   // go-no-go: the left
    }
}
