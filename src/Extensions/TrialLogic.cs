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
using ConditionTarget = UclOpenCoenDomeDecisionMakingTask.Target;

/// <summary>One trial as drawn: its fields are named as the trial log's columns where they are one.</summary>
public class Trial : IZapitTrial
{
    int ITrial.TrialNumber { get { return trialNumber; } }
    int IZapitTrial.LaserCondition { get { return laserCondition; } }
    bool IZapitTrial.LaserOn { get { return laserOn; } }
    double IZapitTrial.LaserPowerMw { get { return laserPowerMw; } }

    public int trialNumber;            // from 1, repeats counted
    public string trialType;           // its condition's trialType
    public int trialCondition;         // the 0-based index of its condition in the task's trialConditions
    public int trialCombinationId;     // its combination's row in Settings_Combinations (TrialConditionCombination.Id): every value it was drawn from
    public string target;              // Left or Right; in discriminationGoNoGo Go or NoGo; on a trial with no right answer NoReward or Random (Target)
    public double rewardOfferedUl;     // what a reward on this trial gives, on every trial; 0 where none is on offer
    public bool bothSpoutsOffered;
    public int repeatCount;            // 0 for a new draw
    public double itiSeconds;          // the interval drawn for this trial: its start to the stimulus, with a quiet mouse
    public double visualAzimuthDeg;    // NaN with no grating (visualContrast 0), as the sound's when none plays
    public double visualElevationDeg;  // NaN with no grating
    public double gratingAzimuthDeg;   // what the renderer starts the grating at (not logged): visualAzimuthDeg, 0 with no grating (hidden at contrast 0)
    public double gratingElevationDeg; // likewise visualElevationDeg
    public double gratingOrientationDeg;      // the renderer's grating (not logged): the combination's visualStimulus
    public double gratingSpatialFrequencyCpd;
    public double gratingTemporalFrequencyHz;
    public double gratingSigmaDeg;
    public double visualContrast;      // 0: grey screen
    public double visualPhaseRad;
    public double auditoryAmplitude;   // the waveform's scale as stored; 0 when silent (no sound plays)
    public int speakerChannel;         // the mixer channel of rig.yml's speaker nearest the drawn location; -1 when silent, AllSpeakersChannel at "all"
    public double auditoryAzimuthDeg;     // that speaker's azimuth; NaN when silent or at "all" (auditoryAmplitude tells them apart)
    public double auditoryElevationDeg;
    public int auditorySoundFileIndex;
    public int laserCondition;         // 0 without optogenetics; on every optogenetics trial, laser on or off
    public bool laserOn;
    public double laserPowerMw;        // 0 with the laser off
    public bool autoReward;
    public bool probeHeads;            // a Random trial's coin: heads rewards a choice, tails penalises it
    public bool spoutMovements;
    public int rngSeed;                // the experiment's seed (TrialDraw.Seed), logged once as an RngSeed event
    internal UclOpenCoenDomeDecisionMakingTaskParameters values;  // its combination's values, which the rules read per trial

    /// <summary>speakerChannel on a trial whose sound plays on every rig.yml speaker (auditoryStimulus.initialAzimuthsDeg "all").</summary>
    public const int AllSpeakersChannel = -2;

    public Trial Clone()
    {
        return (Trial)MemberwiseClone();
    }
}

[Combinator]
[Description("Reads the task file's text into the task (TrialDraw.Read): as DeserializeFromYaml, but a trial condition's set values keep their YAML types (numbers, bools), so Settings_Task logs them as such.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class ReadTask
{
    public IObservable<UclOpenCoenDomeDecisionMakingTaskLogic> Process(IObservable<string> source)
    {
        return source.Select(TrialDraw.Read);
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

/// <summary>The target column's answers other than a side: discriminationGoNoGo's Go and NoGo, and on a trial with no right
/// answer (a probe, outcome Probe) NoReward (target noReward: never rewarded or penalised) or Random (target random: a
/// choice rewarded or penalised on a coin).</summary>
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
    /// <summary>The condition's trialType, its trials'.</summary>
    public string TrialType;
    /// <summary>Its relative draw weight.</summary>
    public int Weight;
    /// <summary>Where it is, for a message: trialConditions[index] (trialType).</summary>
    public string Where;
    /// <summary>The task with its set over it, one per combination of its list cells, picked uniformly per trial.</summary>
    public List<TrialConditionCombination> Combinations;
}

/// <summary>One combination of a condition's list cells: the task with the condition's set over it.</summary>
public class TrialConditionCombination
{
    /// <summary>The task as this combination's trials draw it.</summary>
    public UclOpenCoenDomeDecisionMakingTaskParameters Task;
    /// <summary>The picks that make it, for a message ("" with no list cell).</summary>
    public string Picks;
    /// <summary>Its 0-based row in Settings_Combinations: build order across the session, conditions in turn (the trialCombinationId column).</summary>
    public int Id;
}

/// <summary>The largest amplitude a task's conditions can draw, and where (a condition and its picks).</summary>
public class LoudestSound
{
    /// <summary>The amplitude: 1 the waveform as stored.</summary>
    public double Amplitude;
    /// <summary>The condition that draws it, for a message.</summary>
    public string Where;
}

/// <summary>The draw's state and rules, apart from Bonsai so a test can run them.</summary>
public class TrialDraw
{
    /// <summary>The auditoryStimulus.initialAzimuthsDeg item that plays the sound on every rig.yml speaker (task.py ALL_SPEAKERS).</summary>
    public const string AllSpeakers = "all";

    /// <summary>An auditoryStimulus.initialAzimuthsDeg item as read (a number, or AllSpeakers): its azimuth in degrees, NaN
    /// for AllSpeakers, null for anything else.</summary>
    public static double? SoundAzimuth(object item)
    {
        if (item as string == AllSpeakers) return double.NaN;
        double azimuth;
        if ((item is string || item is IConvertible) && !(item is bool) && double.TryParse(Convert.ToString(item, System.Globalization.CultureInfo.InvariantCulture),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out azimuth) && !double.IsNaN(azimuth)) return azimuth;
        return null;
    }

    /// <summary>The most a condition's weight may be (task.py's TrialCondition.weight le), so the weights' sum stays an int.</summary>
    public const int MaxWeight = 1000000;

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
            conditionCount = zapitConditionCount != null ? zapitConditionCount(path) : ZapitConditions.Check(path);
        }
        Seed = RngSeed.Resolve(task.RngSeed);
        random = new Random(Seed);
    }

    /// <summary>What is wrong with a curriculum (null when present and well formed: an id of 0 or more, a code like S03A, a
    /// 4-character version, a path of one capital letter, a label and a sha256), else the reason.</summary>
    public static string CurriculumProblem(Curriculum curriculum)
    {
        if (curriculum == null) return "is empty";
        var bad = new List<string>();
        if (curriculum.Id < 0) bad.Add("id " + curriculum.Id + " is below 0");
        if (curriculum.Code == null || !System.Text.RegularExpressions.Regex.IsMatch(curriculum.Code, @"^S\d{2}[A-Z]$")) bad.Add("code '" + curriculum.Code + "' is not like S03A");
        if (curriculum.Version == null || !System.Text.RegularExpressions.Regex.IsMatch(curriculum.Version, "^[abcdefghjkmnpqrstuvwxyz23456789]{4}$")) bad.Add("version '" + curriculum.Version + "' is not 4 characters of abcdefghjkmnpqrstuvwxyz23456789");
        if (curriculum.Path == null || !System.Text.RegularExpressions.Regex.IsMatch(curriculum.Path, "^[A-Z]$")) bad.Add("path '" + curriculum.Path + "' is not one capital letter");
        if (string.IsNullOrEmpty(curriculum.Label)) bad.Add("label is empty");
        if (string.IsNullOrEmpty(curriculum.Sha256)) bad.Add("sha256 is empty");
        return bad.Count == 0 ? null : "is malformed (" + string.Join("; ", bad) + ")";
    }

    /// <summary>The task the text holds and its conditions as drawn (BuildConditions); throws unless the task file's curriculum (the
    /// only record of the stage, which app-rig-control fills at launch) is present and well formed and the task and every condition can be run on
    /// these speakers, naming every problem.</summary>
    public static List<TrialConditionDraw> CheckTask(string taskText, EqualisedSpeakerArray speakers, out UclOpenCoenDomeDecisionMakingTaskParameters task)
    {
        task = Read(taskText).TaskParameters;
        var curriculum = task.Curriculum;
        var curriculumProblem = CurriculumProblem(curriculum);
        if (curriculumProblem != null) throw new InvalidOperationException("The task file's curriculum " + curriculumProblem + ": launch it from app-rig-control, which writes the stage's values and fills it.");
        var problems = new List<string>();
        Check(problems, task, speakers);
        var built = problems.Count == 0 ? BuildConditions(taskText, task, problems) : null;
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
                    problems.AddRange(own.Select(p => condition.Where + (combination.Picks.Length == 0 ? "" : " with " + combination.Picks) + ": " + p));
                }
            }
        }
        if (problems.Count > 0) throw new InvalidOperationException("The task cannot be run: " + string.Join("; ", problems.Distinct()) + ".");
        return built;
    }

    /// <summary>The task file's text read as Bonsai's DeserializeFromYaml reads it, except that a trial condition's set (typed
    /// object) keeps its YAML types, numbers and bools, where DeserializeFromYaml reads them as text; so Settings_Task logs them so.</summary>
    public static UclOpenCoenDomeDecisionMakingTaskLogic Read(string text)
    {
        var deserializer = new YamlDotNet.Serialization.DeserializerBuilder()
            .WithTypeConverter(new YamlDotNet.Serialization.Converters.DateTimeOffsetConverter())
            .WithAttemptingUnquotedStringTypeDeserialization()
            .Build();
        return deserializer.Deserialize<UclOpenCoenDomeDecisionMakingTaskLogic>(new YamlDotNet.Core.MergingParser(new YamlDotNet.Core.Parser(new StringReader(text))));
    }

    /// <summary>Each trial condition as drawn: its set and pairs written over a copy of the task file's text at their dotted
    /// paths and read back, once for every combination (task.py condition_combinations): each pairs group's lists (the set's,
    /// else the task's) zipped, equally long; then by path each other list cell of its set and every list field
    /// (TaskSchema.ListFields) one pick (a scalar field's list one value, a list field's entry a one-entry list); all combined
    /// as a product, the first cell's pick changing fastest (each combination's Id its row, across the session), at most
    /// TaskSchema.MaxCombinations. A session field (TaskSchema.SessionFields) is refused. Adds a problem for each condition
    /// that cannot be built, and returns null then.</summary>
    public static List<TrialConditionDraw> BuildConditions(string taskText, UclOpenCoenDomeDecisionMakingTaskParameters task, List<string> problems)
    {
        var nodes = Parameters(Load(taskText)).Children;
        YamlNode sequence;
        var conditionNodes = nodes.TryGetValue(new YamlScalarNode("trialConditions"), out sequence) && sequence is YamlSequenceNode
            ? ((YamlSequenceNode)sequence).Children : new List<YamlNode>();
        var typed = task.TrialConditions ?? new List<TrialCondition>();
        if (typed.Count == 0) problems.Add("trialConditions needs a condition");
        if (typed.Sum(c => (long)c.Weight) > int.MaxValue)
        {
            problems.Add(string.Format("trialConditions' weights add to more than {0}, the most the draw can sum", int.MaxValue));
            return null;
        }
        var built = new List<TrialConditionDraw>();
        int id = 0;   // each combination's row in Settings_Combinations
        for (int i = 0; i < typed.Count; i++)
        {
            var c = typed[i];
            var trialType = Name(c.TrialType);
            var where = string.Format("trialConditions[{0}] ({1})", i, trialType);
            var before = problems.Count;
            if (c.Weight < 0) problems.Add(where + ": its weight is below 0");
            if (c.Weight > MaxWeight) problems.Add(string.Format("{0}: its weight is above {1}", where, MaxWeight));
            var conditionNode = (YamlMappingNode)conditionNodes[i];
            // The typed read fills a missing weight with 0 and merges a << key, which the node tree below does not: refuse both.
            if (!conditionNode.Children.ContainsKey(new YamlScalarNode("weight"))) problems.Add(where + ": it has no weight");
            // Each cell: its paths (a pairs group's, or one set path) and its picks, one node per path.
            var cells = new List<KeyValuePair<string[], List<YamlNode[]>>>();
            var values = new Dictionary<string, YamlNode>();
            YamlNode set;
            var hasSet = conditionNode.Children.TryGetValue(new YamlScalarNode("set"), out set);
            if (conditionNode.Children.ContainsKey(new YamlScalarNode("<<")) || (set is YamlMappingNode && ((YamlMappingNode)set).Children.ContainsKey(new YamlScalarNode("<<"))))
                problems.Add(where + ": a YAML merge key (<<) is not read by the draw; write the condition out in full");
            if (hasSet && !(set is YamlMappingNode)) problems.Add(where + ": its set is not a map");
            else if (hasSet)
            {
                foreach (var cell in ((YamlMappingNode)set).Children)
                {
                    if (!(cell.Key is YamlScalarNode)) { problems.Add(where + ": a set key is not a dotted path"); continue; }
                    values[((YamlScalarNode)cell.Key).Value] = cell.Value;
                }
            }
            var pairs = c.Pairs ?? new List<List<string>>();
            var paired = pairs.SelectMany(g => g ?? new List<string>()).ToList();
            foreach (var path in values.Keys.Concat(paired))
            {
                var session = TaskSchema.SessionFields.FirstOrDefault(f => path == f || path.StartsWith(f + ".") || f.StartsWith(path + "."));
                if (session != null) problems.Add(string.Format("{0} sets or pairs {1}, a session field (x-session-fields: {2})", where, path, session));
                else if (path.StartsWith("optogenetics.") && !task.Optogenetics.Enabled) problems.Add(string.Format("{0} sets {1} with optogenetics off", where, path));
            }
            if (paired.Distinct().Count() < paired.Count || pairs.Any(g => g == null || g.Count < 2)) problems.Add(where + ": each pairs group needs two paths or more, each path in one group");
            if (problems.Count > before) continue;
            var unknown = values.Keys.Concat(paired).FirstOrDefault(path => !Known(nodes, path));
            if (unknown != null) { problems.Add(string.Format("{0} sets or pairs {1}, which is not a task field", where, unknown)); continue; }
            foreach (var group in pairs)
            {
                var lists = group.Select(path => Options(nodes, values, path)).ToList();
                if (lists.Select(l => l.Count).Distinct().Count() > 1)
                {
                    problems.Add(string.Format("{0}: pairs [{1}] have lists of lengths {2}; paired lists must be equally long", where, string.Join(", ", group), string.Join(", ", lists.Select(l => l.Count))));
                    continue;
                }
                cells.Add(new KeyValuePair<string[], List<YamlNode[]>>(group.ToArray(), Enumerable.Range(0, lists[0].Count).Select(k => lists.Select(l => l[k]).ToArray()).ToList()));
            }
            foreach (var path in values.Keys.Concat(TaskSchema.ListFields).Distinct().Where(p => !paired.Contains(p)).OrderBy(p => p, StringComparer.Ordinal))
            {
                cells.Add(new KeyValuePair<string[], List<YamlNode[]>>(new[] { path }, Options(nodes, values, path).Select(o => new[] { o }).ToList()));
            }
            foreach (var cell in cells.Where(cell => cell.Value.Count == 0)) problems.Add(string.Format("{0}: {1} is an empty list", where, string.Join(", ", cell.Key)));
            var count = cells.Aggregate(1L, (n, cell) => n * cell.Value.Count);
            if (count > TaskSchema.MaxCombinations) problems.Add(string.Format("{0} expands into {1} combinations, more than {2} (x-max-combinations)", where, count, TaskSchema.MaxCombinations));
            if (problems.Count > before) continue;
            var draw = new TrialConditionDraw { TrialType = trialType, Weight = c.Weight, Where = where, Combinations = new List<TrialConditionCombination>() };
            for (int n = 0; n < count; n++)
            {
                var stream = Load(taskText);
                var parameters = Parameters(stream).Children;
                var named = new List<string>();
                int rest = n;
                foreach (var cell in cells)
                {
                    var picked = cell.Value[rest % cell.Value.Count];
                    rest /= cell.Value.Count;
                    for (int k = 0; k < cell.Key.Length; k++)
                    {
                        Put(parameters, cell.Key[k], picked[k]);
                        if (cell.Value.Count > 1) named.Add(cell.Key[k] + " " + picked[k]);
                    }
                }
                var writer = new StringWriter();
                stream.Save(writer, false);
                try
                {
                    draw.Combinations.Add(new TrialConditionCombination { Task = Read(writer.ToString()).TaskParameters, Picks = string.Join(", ", named), Id = id++ });
                }
                catch (YamlDotNet.Core.YamlException e)
                {
                    problems.Add(string.Format("{0}: {1}", where, e.InnerException != null ? e.InnerException.Message : e.Message));
                    break;
                }
            }
            built.Add(draw);
        }
        if (problems.Count == 0 && built.Sum(c => (long)c.Weight) <= 0) problems.Add("trialConditions needs a condition with a weight above 0");
        return problems.Count == 0 ? built : null;
    }

    /// <summary>Settings_Combinations: one row per combination of every condition, in build order (its Id, the trial log's
    /// trialCombinationId), with its trialCondition, trialType, conditionWeight and values. Each task is serialised as
    /// SerializeToJson serialises Settings_Task and flattened to dotted paths, a one-entry list unwrapped only at a list
    /// field (TaskSchema.ListFields; an x-whole list stays a list); a path is in values when it differs between rows or from
    /// the task's own, so a set every condition shares is kept. NaN is written as JSON NaN.</summary>
    public static string CombinationsJson(UclOpenCoenDomeDecisionMakingTaskParameters task, IList<TrialConditionDraw> conditions)
    {
        var own = Flatten(task);
        var all = conditions.SelectMany(c => c.Combinations.Select(m => Flatten(m.Task))).ToList();
        var kept = all.SelectMany(r => r.Keys).Distinct().Where(path => all.Any(r => !Same(r, own, path) || !Same(r, all[0], path)))
            .OrderBy(p => p, StringComparer.Ordinal).ToList();
        var rows = new Newtonsoft.Json.Linq.JArray();
        int n = 0;
        for (int i = 0; i < conditions.Count; i++)
        {
            foreach (var combination in conditions[i].Combinations)
            {
                var values = new Newtonsoft.Json.Linq.JObject();
                foreach (var path in kept) values[path] = all[n][path];
                rows.Add(new Newtonsoft.Json.Linq.JObject(
                    new Newtonsoft.Json.Linq.JProperty("trialCombinationId", combination.Id),
                    new Newtonsoft.Json.Linq.JProperty("trialCondition", i),
                    new Newtonsoft.Json.Linq.JProperty("trialType", conditions[i].TrialType),
                    new Newtonsoft.Json.Linq.JProperty("conditionWeight", conditions[i].Weight),
                    new Newtonsoft.Json.Linq.JProperty("values", values)));
                n++;
            }
        }
        var text = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        var writer = new Newtonsoft.Json.JsonTextWriter(text) { Formatting = Newtonsoft.Json.Formatting.Indented, FloatFormatHandling = Newtonsoft.Json.FloatFormatHandling.Symbol };
        new Newtonsoft.Json.Linq.JObject(new Newtonsoft.Json.Linq.JProperty("rows", rows)).WriteTo(writer);
        writer.Flush();
        return text.ToString();
    }

    /// <summary>A task as dotted paths to its leaves (Settings_Combinations' normalisation): serialised as SerializeToJson
    /// does, a one-entry list unwrapped only at a list field.</summary>
    public static Dictionary<string, Newtonsoft.Json.Linq.JToken> Flatten(UclOpenCoenDomeDecisionMakingTaskParameters task)
    {
        var leaves = new Dictionary<string, Newtonsoft.Json.Linq.JToken>();
        Flatten(Newtonsoft.Json.Linq.JToken.FromObject(task), "", leaves);
        return leaves;
    }

    static void Flatten(Newtonsoft.Json.Linq.JToken token, string path, Dictionary<string, Newtonsoft.Json.Linq.JToken> leaves)
    {
        var map = token as Newtonsoft.Json.Linq.JObject;
        if (map != null)
        {
            foreach (var property in map.Properties()) Flatten(property.Value, path.Length == 0 ? property.Name : path + "." + property.Name, leaves);
            return;
        }
        var list = token as Newtonsoft.Json.Linq.JArray;
        leaves[path] = list != null && list.Count == 1 && TaskSchema.ListFields.Contains(path) ? list[0] : token;
    }

    static bool Same(Dictionary<string, Newtonsoft.Json.Linq.JToken> a, Dictionary<string, Newtonsoft.Json.Linq.JToken> b, string path)
    {
        Newtonsoft.Json.Linq.JToken x, y;
        return a.TryGetValue(path, out x) == b.TryGetValue(path, out y) && Newtonsoft.Json.Linq.JToken.DeepEquals(x, y);
    }

    // A cell's picks (task.py _options): a list field's entries, each a one-entry list; a scalar field's list, else its one
    // value (a whole list, x-whole, is one value). The value is the set's, else the task's.
    static List<YamlNode> Options(IDictionary<YamlNode, YamlNode> nodes, Dictionary<string, YamlNode> values, string path)
    {
        YamlNode value;
        var own = At(nodes, path);
        if (!values.TryGetValue(path, out value)) value = own;
        var list = value as YamlSequenceNode;
        if (list == null || own is YamlSequenceNode && !TaskSchema.ListFields.Contains(path)) return new List<YamlNode> { value };
        return TaskSchema.ListFields.Contains(path) ? list.Children.Select(entry => (YamlNode)new YamlSequenceNode(entry)).ToList() : list.Children.ToList();
    }

    // Whether a dotted path's parents are maps in the task file (its leaf may be left out, a default).
    static bool Known(IDictionary<YamlNode, YamlNode> nodes, string path)
    {
        var parts = path.Split('.');
        for (int i = 0; i < parts.Length - 1; i++)
        {
            YamlNode node;
            if (!nodes.TryGetValue(new YamlScalarNode(parts[i]), out node) || !(node is YamlMappingNode)) return false;
            nodes = ((YamlMappingNode)node).Children;
        }
        return true;
    }

    /// <summary>The timing rules between a trial and the next, each drawn from any weighted combination (task.py
    /// _between_trials): every drinkSeconds and stimulusSeconds within every itiMinSeconds, and with optogenetics
    /// laserDurationSeconds within the shortest trigger to the next trigger less the ramp-down and Zapit's reply margin.</summary>
    static void BetweenTrials(List<string> problems, List<UclOpenCoenDomeDecisionMakingTaskParameters> tasks)
    {
        if (tasks.Count == 0) return;
        var iti = tasks.Min(t => t.TrialStructure.ItiMinSeconds);
        var drink = tasks.Max(t => t.TrialStructure.DrinkSeconds);
        var stimulus = tasks.Max(t => t.TrialStructure.StimulusSeconds);
        if (drink > iti + 1e-9) problems.Add(string.Format("trialStructure.drinkSeconds {0} s exceeds trialStructure.itiMinSeconds {1} s of a combination that may come next", drink, iti));
        if (stimulus > iti + 1e-9) problems.Add(string.Format("trialStructure.stimulusSeconds {0} s exceeds trialStructure.itiMinSeconds {1} s of a combination that may come next", stimulus, iti));
        var opto = tasks[0].Optogenetics;
        if (!opto.Enabled) return;
        var longest = tasks.Min(t => t.Optogenetics.LaserToStimulusSeconds + t.TrialStructure.ResponseDelaySeconds) + tasks.Min(t => t.TrialStructure.ItiMinSeconds - t.Optogenetics.LaserToStimulusSeconds)
            - ZapitConditions.MaximumRampDownSeconds - ZapitTrial.ReplyMarginSeconds;
        if (opto.LaserDurationSeconds > longest + 1e-9) problems.Add(string.Format("optogenetics.laserDurationSeconds ({0} s) must be at most {1} s across the combinations: the shortest lead plus response delay, plus the shortest interval less the lead, less the ramp-down and Zapit's reply margin", opto.LaserDurationSeconds, longest));
    }

    /// <summary>An enum value as the task file writes it (its EnumMember name: visualSide, discriminationGoNoGo).</summary>
    public static string Name(Enum value)
    {
        var field = value.GetType().GetField(value.ToString());
        var member = field == null ? null : (System.Runtime.Serialization.EnumMemberAttribute)Attribute.GetCustomAttribute(field, typeof(System.Runtime.Serialization.EnumMemberAttribute));
        return member != null ? member.Value : value.ToString();
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
        if (task.TaskType == TaskType.DiscriminationGoNoGo && task.BothSpoutsProbability != 0) problems.Add("bothSpoutsProbability must be 0 in discriminationGoNoGo: only the left spout moves in");
        // The SpoutsIn cue's LeftOnly or RightOnly draws the other spout back, which a task without spout movements never returns.
        if (task.TaskType != TaskType.DiscriminationGoNoGo && !task.SpoutMovements && task.BothSpoutsProbability != 1) problems.Add("bothSpoutsProbability must be 1 with spoutMovements false: a one-spout trial draws the other spout back");
        if (task.Contrasts == null || task.Contrasts.Count == 0 || task.Contrasts.Any(c => !(c >= 0) || double.IsInfinity(c))) problems.Add("contrasts must each be a finite number, 0 or more, at least one");
        var amplitude = task.AuditoryStimulus.Amplitude;
        if (!(amplitude >= 0) || double.IsInfinity(amplitude)) problems.Add("auditoryStimulus.amplitude must be a finite number, 0 or more");
        // The positions (task.py's same rules): each grating azimuth signed and never 0 (its sign is the side); each sound
        // azimuth's nearest speaker on its side (0: any).
        var visual = task.VisualStimulus;
        var elevation = visual.ElevationDeg;
        bool elevationOk = elevation >= -90 && elevation <= 90;
        if (!elevationOk) problems.Add("visualStimulus.elevationDeg must be in [-90, 90]");
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
            var read = SoundAzimuth(item);
            if (read == null) { problems.Add(string.Format("auditoryStimulus.initialAzimuthsDeg value {0} is neither a number nor {1}", item, AllSpeakers)); continue; }
            var azimuth = read.Value;
            if (double.IsNaN(azimuth))
            {
                if (amplitude > 0 && (speakers == null || speakers.Speakers == null || speakers.Speakers.Count == 0)) problems.Add("rig.yml has no speakerArray.speakers to play auditoryStimulus.initialAzimuthsDeg all on");
            }
            else if (!(azimuth >= -180 && azimuth <= 180)) problems.Add(string.Format("auditoryStimulus.initialAzimuthsDeg value {0} is outside [-180, 180]", azimuth));
            else if (azimuth != 0 && elevationOk && amplitude > 0)
            {
                var speaker = NearestSpeaker(speakers, azimuth, elevation);
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
            if (trial.values.Optogenetics.NoLaserOnRepeats && trial.laserOn)
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
        var structure = trial.values.TrialStructure;
        trial.itiSeconds = Uniform(structure.ItiMinSeconds, structure.ItiMaxSeconds);
        last = trial;
        return trial;
    }

    // The repeat rule is the repeated trial's combination's (repeats.maxRepeats, repeats.repeatOn); a probe never repeats.
    bool ShouldRepeat(string outcome)
    {
        var repeats = last.values.Repeats;
        if (last.repeatCount >= repeats.MaxRepeats || outcome == TrialOutcomes.Probe) return false;
        switch (repeats.RepeatOn)
        {
            case RepeatOn.NotCorrect: return outcome != TrialOutcomes.Correct;
            case RepeatOn.NoResponse: return outcome == TrialOutcomes.NoResponse;
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
        trial.trialCondition = index;
        trial.trialType = condition.TrialType;
        trial.autoReward = random.NextDouble() < p.AutoRewardProbability;
        trial.spoutMovements = task.SpoutMovements;
        trial.bothSpoutsOffered = random.NextDouble() < p.BothSpoutsProbability;
        var large = random.NextDouble() < p.Reward.LargeRewardProbability;
        trial.rewardOfferedUl = large ? p.Reward.RewardSizeUl * p.Reward.LargeRewardMultiplier : p.Reward.RewardSizeUl;
        trial.visualPhaseRad = 2 * Math.PI * random.NextDouble();

        // The grating: the combination's contrast and initial azimuth (each list one entry), shown where the contrast is above 0
        // (0: a grey screen, logged at NaN, NaN; the renderer's start at 0, 0 under contrast 0).
        var visual = p.VisualStimulus;
        double contrast = p.Contrasts[0];
        double azimuth = visual.InitialAzimuthsDeg[0];
        trial.gratingOrientationDeg = visual.OrientationDeg;
        trial.gratingSpatialFrequencyCpd = visual.SpatialFrequencyCpd;
        trial.gratingTemporalFrequencyHz = visual.TemporalFrequencyHz;
        trial.gratingSigmaDeg = visual.GaussianWindowSigmaDeg;
        trial.visualAzimuthDeg = double.NaN;
        trial.visualElevationDeg = double.NaN;
        if (contrast > 0)
        {
            trial.visualAzimuthDeg = azimuth;
            trial.visualElevationDeg = visual.ElevationDeg;
            trial.gratingAzimuthDeg = azimuth;
            trial.gratingElevationDeg = visual.ElevationDeg;
            trial.visualContrast = contrast;
        }
        // The sound: at its amplitude (0: none) on the speaker nearest an initial azimuth, at the grating's elevation, or on
        // every speaker at "all" (logged at azimuth and elevation NaN, its amplitude above 0).
        var sound = p.AuditoryStimulus;
        double soundAzimuth = SoundAzimuth(sound.InitialAzimuthsDeg[0]).Value;   // checked at Go
        trial.auditoryAmplitude = sound.Amplitude;
        if (sound.Amplitude > 0 && double.IsNaN(soundAzimuth))
        {
            trial.speakerChannel = Trial.AllSpeakersChannel;
            trial.auditoryAzimuthDeg = double.NaN;
            trial.auditoryElevationDeg = double.NaN;
        }
        else if (sound.Amplitude > 0)
        {
            var speaker = NearestSpeaker(speakers, soundAzimuth, visual.ElevationDeg).Value;
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

        // The target: a side (the grating's or the sound's picked azimuth's, or named), a go/no-go role, or a probe.
        switch (p.Target)
        {
            case ConditionTarget.VisualSide: trial.target = azimuth > 0 ? Choice.Right : Choice.Left; break;
            case ConditionTarget.AuditorySide: trial.target = soundAzimuth > 0 ? Choice.Right : Choice.Left; break;
            case ConditionTarget.Left: trial.target = Choice.Left; break;
            case ConditionTarget.Right: trial.target = Choice.Right; break;
            case ConditionTarget.Go: trial.target = Target.Go; break;
            case ConditionTarget.NoGo: trial.target = Target.NoGo; break;
            case ConditionTarget.NoReward: trial.target = Target.NoReward; break;
            default: trial.target = Target.Random; break;
        }
        if (task.TaskType == TaskType.DiscriminationGoNoGo)
        {
            // One spout, the left. Lick on Go, withhold on NoGo; a NoReward trial is a probe, never rewarded or penalised
            // (Song et al. 2017).
            trial.bothSpoutsOffered = false;
            if (trial.target != Target.Go)
            {
                trial.autoReward = false;   // auto-reward only on a Go trial
                trial.rewardOfferedUl = 0;  // no reward on offer: a withheld NoGo is Correct, unrewarded
            }
        }
        if (trial.target == Target.Random)
        {
            // No right answer: a random-reward probe. Both spouts move in (bothSpoutsProbability 1, x-rules), and a coin drawn
            // now, from the seeded stream, decides a choice at either: heads rewarded at that spout, tails penalised as an
            // incorrect choice; a miss as a scored trial's miss. Its rewardOfferedUl is kept, heads or tails. It never repeats.
            trial.autoReward = false;
            trial.probeHeads = random.NextDouble() < 0.5;
        }

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

    /// <summary>The loudest sound the conditions can draw: the largest amplitude of any combination of a condition with a
    /// weight above 0, and where it is, for the stimulus set's peak check at Go (LevelAuditoryStimulusSet).</summary>
    public LoudestSound Loudest()
    {
        var loudest = new LoudestSound { Amplitude = 0, Where = "no condition" };
        foreach (var condition in conditions.Where(c => c.Weight > 0))
        {
            foreach (var combination in condition.Combinations)
            {
                if (combination.Task.AuditoryStimulus.Amplitude > loudest.Amplitude)
                {
                    loudest = new LoudestSound { Amplitude = combination.Task.AuditoryStimulus.Amplitude, Where = condition.Where + (combination.Picks.Length == 0 ? "" : " with " + combination.Picks) };
                }
            }
        }
        return loudest;
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

    public TrialRules(string taskText, EqualisedSpeakerArray speakers, bool hardwareTriggered, Func<string, int> zapitConditionCount)
    {
        draw = new TrialDraw(taskText, speakers, zapitConditionCount);
        task = draw.Task;
        var optogenetics = task.Optogenetics;
        // The line held laserDurationSeconds from the trigger; the next trial armed once the longest ramp-down a conditions
        // file may have has played (Zapit refuses a StartStimulus until then).
        laser = optogenetics.Enabled ? LaserTiming.ForDuration(hardwareTriggered, optogenetics.LaserDurationSeconds, ZapitConditions.MaximumRampDownSeconds) : null;
    }

    /// <summary>The rules for a task file's text on a rig (its speakers, and Zapit's hardware triggering); throws naming every problem.</summary>
    public static TrialRules Create(string taskText, LabRig rig, Func<string, int> zapitConditionCount)
    {
        return new TrialRules(taskText, rig.SpeakerArray, rig.Zapit != null && rig.Zapit.HardwareTriggered, zapitConditionCount);
    }

    public LaserTiming Laser { get { return laser; } }

    /// <summary>The stage as code-version (S03A-k7f2) from the task file's curriculum, which the check has found well formed.</summary>
    public string CurriculumStage { get { return task.Curriculum.Code + "-" + task.Curriculum.Version; } }

    /// <summary>The loudest sound the task's conditions can draw (TrialDraw.Loudest), for the stimulus set's peak check.</summary>
    public LoudestSound Loudest { get { return draw.Loudest(); } }

    /// <summary>Settings_Combinations' text (TrialDraw.CombinationsJson): every combination built at Go, each a row the trial
    /// log's trialCombinationId names; logged through LogDataSchema in the Logging group.</summary>
    public string CombinationsJson { get { return TrialDraw.CombinationsJson(draw.Task, draw.Conditions); } }

    public Trial Draw(TrialOutcome previous)
    {
        return draw.Next(previous == null ? null : previous.Row.Value.outcome);
    }

    public TrialTiming Timing(Trial trial)
    {
        var structure = trial.values.TrialStructure;
        var cues = new List<TrialCue>
        {
            new TrialCue(GratingOffCue, CueAnchor.Stimulus, structure.StimulusSeconds),
            new TrialCue(SpoutsInCue, CueAnchor.Stimulus, structure.StimulusToSpoutsInSeconds, true),
        };
        if (trial.spoutMovements) cues.Add(new TrialCue(DrinkCue, CueAnchor.NextIti, structure.DrinkSeconds));
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
        return new DecisionUpdate { GratingAzimuthDeg = decision * trial.values.Response.DegreesPerLick + trial.gratingAzimuthDeg };
    }

    TrialOutcome Outcome(Trial trial, string choice, TrialTimes times)
    {
        var row = TrialRow.Build(choice, trial, times, trial.values.Penalty.TimeoutSeconds, trial.values.Penalty.Sound.Enabled);
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
