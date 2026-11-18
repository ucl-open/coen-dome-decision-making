using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Devices;
using OpenRigs.Trials;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UclOpenCoenDomeDecisionMakingTask;
using YamlDotNet.RepresentationModel;
using Event = OpenRigs.Trials.TrialEvent<global::Trial, global::DecisionUpdate, global::TrialOutcome>;

/// <summary>The task files app-rig-control writes at launch (tests\fixtures\handover, golden files its tests\test_handover.py
/// writes for every stage of the share's curricula; HANDOVER_DIR names another folder, as its scripts\mutation_check.py
/// does), each read as the workflow reads it and drawn by this repo's C#: the draw, the old trial types' meaning, and the
/// trials run by lib-open-rigs' TrialLoop with a pretend mouse licking at random, each logged row checked.</summary>
[TestClass]
public class HandoverTests
{
    const int Draws = 5000;   // trials drawn per file for the shares
    const int Run = 200;      // trials run through the loop per file

    // Each golden file's name and text, rngSeed set to 1 (app-rig-control leaves it null for the workflow to draw).
    static List<KeyValuePair<string, string>> Goldens()
    {
        var folder = Environment.GetEnvironmentVariable("HANDOVER_DIR");
        if (string.IsNullOrEmpty(folder)) folder = Path.Combine(Fixture.Repo(), "tests", "fixtures", "handover");
        var files = Directory.GetFiles(folder, "*.yml").OrderBy(f => f, StringComparer.Ordinal).ToList();
        Assert.IsTrue(files.Count > 0, "no golden task files in " + folder);
        return files.Select(f =>
        {
            var text = File.ReadAllText(f);
            Assert.AreEqual(1, text.Split(new[] { "rngSeed: null" }, StringSplitOptions.None).Length - 1, f + ": rngSeed: null once");
            return new KeyValuePair<string, string>(Path.GetFileName(f), text.Replace("rngSeed: null", "rngSeed: 1"));
        }).ToList();
    }

    // The file's conditions as written, read apart from the draw's own reader: (trialType, weight, pairs).
    static List<Tuple<string, int, YamlSequenceNode>> Written(string text, out YamlMappingNode parameters)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        var root = (YamlMappingNode)((YamlMappingNode)stream.Documents[0].RootNode).Children[new YamlScalarNode("taskParameters")];
        parameters = root;
        return ((YamlSequenceNode)root.Children[new YamlScalarNode("trialConditions")]).Children.Cast<YamlMappingNode>().Select(c =>
        {
            YamlNode pairs;
            c.Children.TryGetValue(new YamlScalarNode("pairs"), out pairs);
            return Tuple.Create(((YamlScalarNode)c.Children[new YamlScalarNode("trialType")]).Value,
                int.Parse(((YamlScalarNode)c.Children[new YamlScalarNode("weight")]).Value), pairs as YamlSequenceNode);
        }).ToList();
    }

    static List<TrialConditionDraw> Built(string name, string text)
    {
        UclOpenCoenDomeDecisionMakingTaskParameters task;
        try { return TrialDraw.CheckTask(text, Fixture.Speakers(), out task); }
        catch (InvalidOperationException e) { Assert.Fail(name + ": " + e.Message); return null; }
    }

    // The old task file a golden file was written from: task-localization-S02A.yml -> task-localization.yml.
    static string OldFile(string name)
    {
        return name.Substring(0, name.LastIndexOf('-')) + ".yml";
    }

    [TestMethod]
    public void EachGoldenFileBuildsItsConditionsWithTheirWeightsAndPairs()
    {
        foreach (var golden in Goldens())
        {
            YamlMappingNode parameters;
            var written = Written(golden.Value, out parameters);
            var built = Built(golden.Key, golden.Value);
            CollectionAssert.AreEqual(written.Select(w => w.Item1).ToList(), built.Select(c => c.TrialType).ToList(), golden.Key);
            CollectionAssert.AreEqual(written.Select(w => w.Item2).ToList(), built.Select(c => c.Weight).ToList(), golden.Key);
            for (int i = 0; i < built.Count; i++)
            {
                // A pairs group of the grating's and the sound's azimuths: each combination picks both at one index of the
                // condition's two lists.
                var pairs = written[i].Item3;
                if (pairs == null) continue;
                var set = (YamlMappingNode)((YamlMappingNode)((YamlSequenceNode)parameters.Children[new YamlScalarNode("trialConditions")]).Children[i]).Children[new YamlScalarNode("set")];
                foreach (YamlSequenceNode group in pairs)
                {
                    var paths = group.Children.Select(n => ((YamlScalarNode)n).Value).ToList();
                    CollectionAssert.AreEquivalent(new[] { "visualStimulus.initialAzimuthsDeg", "auditoryStimulus.initialAzimuthsDeg" }, paths, golden.Key + ": a pairs group this test does not check");
                    var visual = ((YamlSequenceNode)set.Children[new YamlScalarNode(paths[0])]).Children.Select(n => ((YamlScalarNode)n).Value).ToList();
                    var sound = ((YamlSequenceNode)set.Children[new YamlScalarNode(paths[1])]).Children.Select(n => ((YamlScalarNode)n).Value).ToList();
                    if (paths[0] != "visualStimulus.initialAzimuthsDeg") { var t = visual; visual = sound; sound = t; }
                    foreach (var combination in built[i].Combinations)
                    {
                        var v = visual.IndexOf(Number(combination.Task.VisualStimulus.InitialAzimuthsDeg[0]));
                        Assert.IsTrue(v >= 0 && v == sound.IndexOf(Number(combination.Task.AuditoryStimulus.InitialAzimuthsDeg[0])), golden.Key + " " + built[i].Where + " " + combination.Picks);
                    }
                }
            }
        }
    }

    static string Number(object value)
    {
        return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    [TestMethod]
    public void EachCombinationIsDrawnAtItsConditionsShareOfTheWeights()
    {
        foreach (var golden in Goldens())
        {
            var built = Built(golden.Key, golden.Value);
            var draw = new TrialDraw(golden.Value, Fixture.Speakers(), path => 2);
            var counts = new Dictionary<string, int>();
            for (int n = 0; n < Draws; n++)
            {
                var t = draw.Next("Correct");   // never repeated, so every trial is a new draw
                CheckTrial(golden.Key, built, t.trialType, t.trialCondition, t.trialCombination, t.visualContrast, t.visualAzimuthDeg, t.auditoryAmplitude, t.auditoryAzimuthDeg, t.target);
                var key = t.trialCondition + "/" + t.trialCombination;
                counts[key] = (counts.ContainsKey(key) ? counts[key] : 0) + 1;
            }
            double total = built.Sum(c => c.Weight);
            for (int i = 0; i < built.Count; i++)
            {
                for (int k = 0; k < built[i].Combinations.Count; k++)
                {
                    var p = built[i].Weight / total / built[i].Combinations.Count;
                    var key = i + "/" + k;
                    var share = (counts.ContainsKey(key) ? counts[key] : 0) / (double)Draws;
                    Assert.IsTrue(Math.Abs(share - p) <= 4.5 * Math.Sqrt(p * (1 - p) / Draws), string.Format("{0} {1}: share {2}, expected {3}", golden.Key, built[i].Where + " " + built[i].Combinations[k].Picks, share, p));
                }
            }
        }
    }

    // One trial against its combination (the file's values as BuildConditions picked them): its trialType the file's, the
    // grating (NaN azimuth at contrast 0), the sound on the speaker nearest its azimuth (NaN silent or at all), the target
    // as the combination names it (a side target from the side picked).
    static void CheckTrial(string file, List<TrialConditionDraw> built, string trialType, int condition, int combination, double contrast,
        double visualAzimuth, double amplitude, double auditoryAzimuth, string target)
    {
        var where = file + " " + built[condition].Where + " combination " + combination;
        Assert.AreEqual(built[condition].TrialType, trialType, where);
        var c = built[condition].Combinations[combination].Task;
        var picked = c.Contrasts[0];
        Assert.AreEqual(picked, contrast, where);
        Assert.AreEqual(picked > 0 ? c.VisualStimulus.InitialAzimuthsDeg[0] : double.NaN, visualAzimuth, where);
        Assert.AreEqual(c.AuditoryStimulus.Amplitude, amplitude, where);
        var soundAzimuth = TrialDraw.SoundAzimuth(c.AuditoryStimulus.InitialAzimuthsDeg[0]).Value;
        var heard = amplitude > 0 && !double.IsNaN(soundAzimuth)
            ? TrialDraw.NearestSpeaker(Fixture.Speakers(), soundAzimuth, c.VisualStimulus.ElevationDeg).Value.Position.Azimuth : double.NaN;
        Assert.AreEqual(heard, auditoryAzimuth, where);
        var named = TrialDraw.Name(c.Target);
        var expected = c.Target == UclOpenCoenDomeDecisionMakingTask.Target.VisualSide ? (c.VisualStimulus.InitialAzimuthsDeg[0] > 0 ? Choice.Right : Choice.Left)
            : c.Target == UclOpenCoenDomeDecisionMakingTask.Target.AuditorySide ? (soundAzimuth > 0 ? Choice.Right : Choice.Left)
            : char.ToUpperInvariant(named[0]) + named.Substring(1);
        Assert.AreEqual(expected, target, where);
    }

    [TestMethod]
    public void EachTrialTypeDrawsWhatItsOldTypeDrew()
    {
        // Item 2: convert-on-read keeps an old type's name as the trialType, so each trialType's draws are among what that
        // old type drew (Fixture.OldDraws, on the same speakers).
        foreach (var golden in Goldens())
        {
            var old = Fixture.OldDraws[OldFile(golden.Key)];
            var draw = new TrialDraw(golden.Value, Fixture.Speakers(), path => 2);
            for (int n = 0; n < 1000; n++)
            {
                var t = draw.Next("Correct");
                Assert.IsTrue(old.ContainsKey(t.trialType), golden.Key + ": no old type " + t.trialType);
                CollectionAssert.Contains(old[t.trialType], Fixture.Signature(t), golden.Key + " " + t.trialType);
            }
        }
    }

    [TestMethod]
    public void EachGoldenFileRunsInTheTrialLoopAndLogsItsCombinationsValues()
    {
        // Item 3: the trials as the workflow runs them (TrialRules on lib-open-rigs' TrialLoop, a 1 ms clock), a pretend
        // mouse licking either spout at random between the stimulus and the outcome; each row as LogTrials writes it.
        var noGoLicked = 0;
        foreach (var golden in Goldens())
        {
            var built = Built(golden.Key, golden.Value);
            var loop = new TrialLoop<Trial, DecisionUpdate, TrialOutcome>(new TrialRules(golden.Value, Fixture.Speakers(), true, path => 2), true);
            var mouse = new Random(1);
            var rows = new List<TrialRow>();
            var trials = new Dictionary<int, Trial>();
            bool responding = false;
            int ms = 10000;
            loop.OnWheel(0);
            Action<IEnumerable<Event>> take = events =>
            {
                foreach (var e in events)
                {
                    if (e.Kind == TrialEventKind.Start) trials[e.Trial.trialNumber] = e.Trial;
                    if (e.Kind == TrialEventKind.Stimulus) responding = true;
                    if (e.Kind == TrialEventKind.Outcome) { responding = false; rows.Add(e.Outcome.Row.Value); }
                }
            };
            take(loop.OnClock(ms / 1000.0));
            while (rows.Count < Run)
            {
                Assert.IsTrue(ms < 10000 + Run * 60000, golden.Key + ": the loop stalled at " + rows.Count + " rows");
                take(loop.OnClock(++ms / 1000.0));
                if (responding && mouse.NextDouble() < 0.004)
                {
                    var spout = mouse.Next(2);
                    take(loop.OnLick(spout, true));
                    take(loop.OnLick(spout, false));
                }
            }
            foreach (var r in rows)
            {
                CheckTrial(golden.Key, built, r.trialType, r.trialCondition, r.trialCombination, r.visualContrast, r.visualAzimuthDeg, r.auditoryAmplitude, r.auditoryAzimuthDeg, r.target);
                Assert.AreEqual(trials[r.trialNumber].trialCombination, r.trialCombination, golden.Key);
                if (r.target == Target.NoGo && r.choice == Choice.Lick)
                {
                    noGoLicked++;
                    Assert.IsTrue(!r.rewarded && r.outcome == TrialOutcomes.Incorrect, golden.Key + ": a lick on a NoGo trial is rewarded");
                }
            }
        }
        Assert.IsTrue(noGoLicked > 0, "no golden file ran a lick on a NoGo trial");
    }
}
