using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using OpenRigs.Devices;
using OpenRigs.Devices.Rigs;
using OpenRigs.Devices.Zapit;
using OpenRigs.Trials;
using UclOpenCoenDomeDecisionMakingTask;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using BuiltTrialCondition = OpenRigs.Trials.BuiltTrialCondition<UclOpenCoenDomeDecisionMakingTask.UclOpenCoenDomeDecisionMakingTaskParameters>;

static class Fixture
{
    public static string Repo([CallerFilePath] string here = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here), "..", ".."));
    }

    // The share's Mouse Curriculum stages of each task file (config\curricula), as the task file's rows were
    // before they moved there: the ticked fields only, a stage's weights by condition label, null left to the task file.
    class Stage
    {
        public string Label, Weights;
        public double Auto, Single;
        public double[] Contrasts;
        public bool Repeats, StayIn;
    }

    const string Coherent = "{ visual: 0, auditory: 0, audiovisualCoherent: 10, audiovisualConflict: 0, neutral: 0 }";
    static readonly Dictionary<string, Stage[]> Stages = new Dictionary<string, Stage[]>
    {
        { "task-localization.yml", new[] {
            new Stage { Label = "Habituation", Weights = Coherent, Auto = 0, Single = 0.5, Contrasts = new[] { 3.0 }, Repeats = false },
            new Stage { Label = "Coherent Only", Weights = Coherent, Auto = 0, Single = 0, Contrasts = new[] { 3.0 }, Repeats = true },
            new Stage { Label = "Auditory Added", Weights = "{ visual: 0, auditory: 10, audiovisualCoherent: 10, audiovisualConflict: 0, neutral: 0 }", Auto = 0, Single = 0, Contrasts = new[] { 0.25, 0.5, 1.0 }, Repeats = true },
            new Stage { Label = "Visual Added", Weights = "{ visual: 10, auditory: 10, audiovisualCoherent: 10, audiovisualConflict: 0, neutral: 0 }", Auto = 0, Single = 0, Contrasts = new[] { 0.25, 0.5, 1.0 }, Repeats = true },
            new Stage { Label = "Conflict Added", Auto = 0, Single = 0, Contrasts = new[] { 0.25, 0.5, 1.0 }, Repeats = true } } },
        { "task-discrimination-go-no-go.yml", Discrimination("go", "noGo", 0) },
        { "task-discrimination-left-right.yml", Discrimination("left", "right", 0) },
    };

    static Stage[] Discrimination(string first, string second, double single)
    {
        Func<int, int, string> weights = (a, b) => string.Format("{{ {0}: 10, {1}: {2}, audiovisualCoherent: {3}, audiovisualConflict: 0, neutral: 0 }}", first, second, a, b);
        return new[] {
            new Stage { Label = "Habituation", Weights = weights(0, 0), Auto = 1, Single = single, Contrasts = new[] { 1.0 }, StayIn = true },
            new Stage { Label = "Only", Weights = weights(0, 0), Auto = 1, Single = single, Contrasts = new[] { 1.0 } },
            new Stage { Label = "Added", Weights = weights(10, 10), Auto = 0, Single = single, Contrasts = new[] { 0.5, 1.0 } },
            new Stage { Label = "Probes Added", Auto = 0, Single = single, Contrasts = new[] { 0.5, 1.0 } },
            new Stage { Label = "Wide Contrasts", Auto = 0, Single = single, Contrasts = new[] { 0.5, 1.0, 2.0, 3.0 } } };
    }

    // The task file as app-rig-control writes it at launch for a curriculum stage: the stage's values over the file's, and
    // curriculum filled. A stage that repeats on notCorrect has maxRepeats 5 and each probe condition never and 0, as K:'s stages.
    public static UclOpenCoenDomeDecisionMakingTaskParameters Task(string file, int stage)
    {
        var text = File.ReadAllText(Path.Combine(Repo(), "config", file));
        var task = new DeserializerBuilder().Build().Deserialize<UclOpenCoenDomeDecisionMakingTaskLogic>(text).TaskParameters;
        Assert.IsNull(task.Curriculum, "the repo's task files leave curriculum empty");
        var s = Stages[file][stage];
        if (s.Weights != null) task.TrialConditions = Conditions(task, s.Weights);
        task.AutoRewardProbability = s.Auto;
        task.Spouts.SingleSpoutProbability = s.Single;
        task.VisualStimulus.Contrasts = s.Contrasts.ToList();
        if (s.Repeats)
        {
            task.Repeats.RepeatOn = RepeatOn.NotCorrect;
            task.Repeats.MaxRepeats = 5;
            foreach (var c in task.TrialConditions.Where(IsProbe))
            {
                var set = (IDictionary<object, object>)c.Set;
                set["repeats.repeatOn"] = "never";
                set["repeats.maxRepeats"] = 0;
            }
        }
        task.Spouts.AlwaysIn = s.StayIn;
        task.Curriculum = Curriculum(stage, s.Label);
        task.RngSeed = 1;
        return task;
    }

    /// <summary>Whether a condition's set target has no right answer (random or noReward).</summary>
    public static bool IsProbe(TrialCondition c)
    {
        object target;
        return ((IDictionary<object, object>)c.Set).TryGetValue("target", out target) && (target as string == "random" || target as string == "noReward");
    }

    /// <summary>A well formed curriculum for a stage (id, code S0nA, a fixed version, path A).</summary>
    public static Curriculum Curriculum(int id, string label)
    {
        return new Curriculum { Id = id, Code = "S0" + id + "A", Version = "k7f2", Path = "A", Label = label, Sha256 = "test" };
    }

    // A stage's trial mix: the file's conditions weighted by trialType.
    static List<TrialCondition> Conditions(UclOpenCoenDomeDecisionMakingTaskParameters task, string weights)
    {
        var byName = new DeserializerBuilder().Build().Deserialize<Dictionary<string, int>>(weights);
        foreach (var c in task.TrialConditions) c.Weight = byName[Name(task, c)];
        return task.TrialConditions;
    }

    /// <summary>A condition's trialType, derived from its set over the task's values as the draw derives it at Go (the text read
    /// back, so its set's values are typed; OpenRigs.Trials TrialTypes).</summary>
    public static string Name(UclOpenCoenDomeDecisionMakingTaskParameters task, TrialCondition c)
    {
        var typed = TaskYaml.Read<UclOpenCoenDomeDecisionMakingTaskLogic>(Text(task)).TaskParameters;
        var condition = typed.TrialConditions[task.TrialConditions.IndexOf(c)];
        var set = Newtonsoft.Json.Linq.JObject.FromObject(condition.Set).Properties().ToDictionary(p => p.Name, p => p.Value);
        return TrialTypes.Derive(TrialDraw.Schema.TrialTypes, TaskYaml.Flatten(typed, new string[0]), set, condition.Pairs);
    }

    /// <summary>A condition of a weight with these condition-level values; target visualSide unless given.</summary>
    public static TrialCondition Condition(int weight, params object[] pathsAndValues)
    {
        var set = new Dictionary<object, object>();
        for (int i = 0; i < pathsAndValues.Length; i += 2) set.Add(pathsAndValues[i], pathsAndValues[i + 1]);
        if (!set.ContainsKey("target")) set.Add("target", "visualSide");
        return new TrialCondition { Weight = weight, Set = set, Pairs = new List<List<string>>() };
    }

    /// <summary>The condition with its grating and sound azimuths paired.</summary>
    public static TrialCondition Paired(TrialCondition condition)
    {
        condition.Pairs = new List<List<string>> { new List<string> { "visualStimulus.initialAzimuthsDeg", "auditoryStimulus.initialAzimuthsDeg" } };
        return condition;
    }

    /// <summary>A list value for a condition's cell.</summary>
    public static List<object> List(params object[] values)
    {
        return values.ToList();
    }

    public static void Weight(UclOpenCoenDomeDecisionMakingTaskParameters task, string label, int weight)
    {
        task.TrialConditions.Single(c => Name(task, c) == label).Weight = weight;
    }

    // Every condition not of these labels weighted 0.
    public static void Only(UclOpenCoenDomeDecisionMakingTaskParameters task, params string[] labels)
    {
        foreach (var c in task.TrialConditions.Where(c => !labels.Contains(Name(task, c))).ToList()) c.Weight = 0;
    }

    /// <summary>The task file's text as rig-control writes it, from the task (the draw and the Go check read the text).</summary>
    public static string Text(UclOpenCoenDomeDecisionMakingTaskParameters task)
    {
        return new SerializerBuilder().Build().Serialize(new UclOpenCoenDomeDecisionMakingTaskLogic { TaskParameters = task });
    }

    /// <summary>TrialDraw.CheckTask on the task's text: its conditions as built, or the refusal thrown.</summary>
    public static List<BuiltTrialCondition> Check(UclOpenCoenDomeDecisionMakingTaskParameters task, EqualisedSpeakerArray speakers)
    {
        UclOpenCoenDomeDecisionMakingTaskParameters read;
        return TrialDraw.CheckTask(Text(task), speakers, out read);
    }

    public static EqualisedSpeakerArray Speakers()
    {
        var array = new EqualisedSpeakerArray();
        array.Speakers = new Dictionary<string, Speaker>();
        array.Speakers.Add("left", Speaker(-90, 15, 0));
        array.Speakers.Add("right", Speaker(90, 15, 1));
        array.Speakers.Add("centre", Speaker(0, 15, 2));
        return array;
    }

    public static Speaker Speaker(double azimuth, double elevation, int channel)
    {
        var position = new OpenRigs.Devices.Rigs.SphericalPosition();
        position.Azimuth = azimuth;
        position.Elevation = elevation;
        var speaker = new Speaker();
        speaker.Position = position;
        speaker.Channel = channel;
        return speaker;
    }

    // Trials drawn one after another, each last one ending as outcome says (correct by default): a probe's outcome is probe,
    // its choice noResponse after a noResponse, else a lick at the left.
    public static List<Trial> Draw(UclOpenCoenDomeDecisionMakingTaskParameters task, int count, Func<Trial, string> outcome = null)
    {
        var draw = new TrialDraw(Text(task), Speakers(), path => new ZapitConditionsFile(2, ZapitConditions.MaximumRampDownSeconds));
        var trials = new List<Trial>();
        TrialRow last = null;
        for (int i = 0; i < count; i++)
        {
            var trial = draw.Next(last);
            trials.Add(trial);
            var said = outcome == null ? TrialOutcomes.Correct : outcome(trial);
            var probe = trial.target == TargetWords.Random || trial.target == TargetWords.NoReward;
            last = new TrialRow { outcome = probe ? TrialOutcomes.Probe : said, choice = said == TrialOutcomes.NoResponse ? Choice.NoResponse : Choice.Left };
        }
        return trials;
    }

    public static string Refusal(Action action)
    {
        try { action(); }
        catch (InvalidOperationException e) { return e.Message; }
        return "accepted";
    }
}

[TestClass]
public class TrialDrawTests
{
    [TestMethod]
    public void LocalizationAnswersTheStimulusSide()
    {
        var trials = Fixture.Draw(Fixture.Task("task-localization.yml", 4), 500);
        foreach (var t in trials)
        {
            Assert.IsTrue(!t.SpoutsAlwaysIn && t.auditoryLevelDbfs == -40, t.trialType);
            if (t.trialType == "visual" || t.trialType == "audiovisualCoherent")
                Assert.AreEqual(t.visualInitialAzimuthDeg > 0 ? TargetWords.Right : TargetWords.Left, t.target, t.trialType);
            if (t.trialType == "auditory" || t.trialType == "audiovisualCoherent")
                Assert.AreEqual(t.auditoryInitialAzimuthDeg > 0 ? TargetWords.Right : TargetWords.Left, t.target, t.trialType);
            if (t.trialType == "audiovisualConflict")
                Assert.AreEqual(t.visualInitialAzimuthDeg > 0, t.auditoryInitialAzimuthDeg < 0);
            if (t.trialType == "visual" || t.trialType == "neutral")
                Assert.AreEqual(2, t.speakerChannel, "the centre speaker");
        }
        CollectionAssert.AreEquivalent(new[] { "visual", "auditory", "audiovisualCoherent", "audiovisualConflict", "neutral" }, trials.Select(t => t.trialType).Distinct().ToArray());
    }

    [TestMethod]
    public void LocalizationConflictKeepsItsStimuliAndIsAProbe()
    {
        // A random-reward probe: the grating and sound on opposite sides, no side to answer, its coin
        // heads about half the time (ATrialWithNoRightAnswerRewardsOrPenalisesEitherChoiceOnACoin scores it).
        var task = Fixture.Task("task-localization.yml", 4);
        Fixture.Only(task, "audiovisualConflict");
        var trials = Fixture.Draw(task, 600);
        Assert.IsTrue(trials.All(t => t.trialType == "audiovisualConflict" && (t.visualInitialAzimuthDeg > 0) == (t.auditoryInitialAzimuthDeg < 0) && t.target == TargetWords.Random));
        Assert.AreEqual(0.5, trials.Count(t => t.probeHeads) / 600.0, 0.07);
    }

    [TestMethod]
    public void EachSoundPlaysOnTheNearestSpeaker()
    {
        foreach (var t in Fixture.Draw(Fixture.Task("task-localization.yml", 4), 300))
        {
            var speaker = Fixture.Speakers().Speakers.Values.Single(s => s.Channel == t.speakerChannel);
            Assert.AreEqual(speaker.Position.Azimuth, t.auditoryInitialAzimuthDeg);
            Assert.AreEqual(speaker.Position.Elevation, t.auditoryElevationDeg);
        }
        var nearest = SoundAzimuths.NearestSpeaker(Fixture.Speakers(), -60, 15);
        Assert.AreEqual("left", nearest.Key);
        Assert.AreEqual("centre", SoundAzimuths.NearestSpeaker(Fixture.Speakers(), 30, 15).Key);
    }

    [TestMethod]
    public void DiscriminationAnswersTheModality()
    {
        foreach (var t in Fixture.Draw(Fixture.Task("task-discrimination-left-right.yml", 3), 500))
        {
            if (t.trialType == "left") Assert.IsTrue(double.IsNaN(t.auditoryLevelDbfs) && t.visualContrast > 0 && t.target == TargetWords.Left);
            if (t.trialType == "right") Assert.IsTrue(t.auditoryLevelDbfs > -120 && t.visualContrast == 0 && t.target == TargetWords.Right);
            if (t.trialType == "neutral") Assert.IsTrue(double.IsNaN(t.auditoryLevelDbfs) && double.IsNaN(t.auditoryInitialAzimuthDeg) && t.speakerChannel == -1);
        }
    }

    [TestMethod]
    public void DiscriminationHabituationAutoRewardsAtTheLeftSpoutWithTheSpoutsIn()
    {
        Assert.IsTrue(Fixture.Draw(Fixture.Task("task-discrimination-left-right.yml", 0), 50).All(t => t.autoReward && t.target == TargetWords.Left && t.SpoutsAlwaysIn && t.trialType == "left"));
        var task = Fixture.Task("task-discrimination-left-right.yml", 2);
        task.AutoRewardProbability = 1;
        var stage2 = Fixture.Draw(task, 300);
        Assert.IsTrue(stage2.Where(t => t.target != TargetWords.Random).All(t => t.autoReward), "left-right: either spout's answer may be auto-rewarded");
        Assert.IsTrue(stage2.Where(t => t.target == TargetWords.Random).All(t => !t.autoReward), "a probe never is");
    }

    [TestMethod]
    public void GoNoGoAnswersTheRoleAtTheLeftSpout()
    {
        var task = Fixture.Task("task-discrimination-go-no-go.yml", 3);
        task.AutoRewardProbability = 1;
        foreach (var t in Fixture.Draw(task, 600))
        {
            var expected = t.trialType == "go" ? TargetWords.Go : t.trialType == "noGo" ? TargetWords.NoGo : TargetWords.NoReward;
            Assert.AreEqual(expected, t.target, t.trialType);
            Assert.AreEqual(expected == TargetWords.Go, t.autoReward, "auto-reward only on Go");
            Assert.AreEqual(expected == TargetWords.Go, t.rewardOfferedUl > 0, "a reward on offer only on Go");
            Assert.IsFalse(!t.singleSpoutOffered);
            Assert.AreEqual(LickSpoutCommand.LeftOnly, t.SpoutsInCommand);
        }
    }

    [TestMethod]
    public void AnAuditoryGoMouseIsItsConditionsSettings()
    {
        // goModality is gone: the go condition shows the sound alone, the noGo condition the grating alone.
        var task = Fixture.Task("task-discrimination-go-no-go.yml", 2);
        Fixture.Weight(task, "audiovisualCoherent", 0);
        task.TrialConditions[0].Set = new Dictionary<object, object> { { "target", "go" }, { "visualStimulus.contrasts", Fixture.List(0.0) } };
        task.TrialConditions[1].Set = new Dictionary<object, object> { { "target", "noGo" }, { "auditoryStimulus.levelDbfs", "mute" } };
        var trials = Fixture.Draw(task, 400);
        Assert.IsTrue(trials.All(t => t.trialType == "go" ? t.target == TargetWords.Go && t.auditoryLevelDbfs > -120 && t.visualContrast == 0 : t.target == TargetWords.NoGo && double.IsNaN(t.auditoryLevelDbfs) && t.visualContrast > 0));
        Assert.AreEqual(0.5, trials.Count(t => t.trialType == "go") / 400.0, 0.08, "go 10, noGo 10");
    }

    [TestMethod]
    public void SingleSpoutOfferedFollowsTheTaskTypeTheTargetAndSpoutsAlwaysIn()
    {
        // go/no-go: always (the left alone); localization and left-right: a scored trial with its spouts moving, at
        // singleSpoutProbability (A28: 1 - the old bothSpoutsProbability); never a random-reward probe or a stay-in trial.
        Assert.IsTrue(Fixture.Draw(Fixture.Task("task-discrimination-go-no-go.yml", 4), 300).All(t => t.singleSpoutOffered && t.SpoutsInCommand == LickSpoutCommand.LeftOnly));
        foreach (var file in new[] { "task-localization.yml", "task-discrimination-left-right.yml" })
        {
            var task = Fixture.Task(file, 4);
            task.Spouts.SingleSpoutProbability = 0.3;
            var trials = Fixture.Draw(task, 3000);
            var scored = trials.Where(t => t.target != TargetWords.Random).ToList();
            Assert.AreEqual(0.3, scored.Count(t => t.singleSpoutOffered) / (double)scored.Count, 0.03, file);
            Assert.IsTrue(trials.Where(t => t.target == TargetWords.Random).All(t => !t.singleSpoutOffered), file + ": a probe has both");
            Assert.IsTrue(scored.Where(t => t.singleSpoutOffered).All(t => t.SpoutsInCommand == (t.target == TargetWords.Right ? LickSpoutCommand.RightOnly : LickSpoutCommand.LeftOnly)), file);
            task.Spouts.SingleSpoutProbability = 1;
            task.Spouts.AlwaysIn = true;
            Assert.IsTrue(Fixture.Draw(task, 300).All(t => !t.singleSpoutOffered && t.SpoutsInCommand == LickSpoutCommand.SpoutsIn), file + ": spouts stay in, both used");
        }
    }

    [TestMethod]
    public void SpoutsAlwaysInIsEachTrialsAndInvertsTheOldSpoutMovements()
    {
        // A18: spoutMovements false is spouts.alwaysIn true; a condition may set it, so one condition's trials stay in and another's move.
        var task = Fixture.Task("task-localization.yml", 3);
        ((IDictionary<object, object>)task.TrialConditions[1].Set)["spouts.alwaysIn"] = true;
        var trials = Fixture.Draw(task, 400);
        Assert.IsTrue(trials.All(t => t.SpoutsAlwaysIn == (t.trialConditionId == 1)));
        Assert.IsTrue(trials.Any(t => t.SpoutsAlwaysIn) && trials.Any(t => !t.SpoutsAlwaysIn));
        Assert.IsTrue(Fixture.Draw(Fixture.Task("task-discrimination-left-right.yml", 0), 20).All(t => t.SpoutsAlwaysIn), "habituation: in");
    }

    [TestMethod]
    public void TheWeightsAreByTrialType()
    {
        var task = Fixture.Task("task-localization.yml", 0);
        Assert.AreEqual(0, task.TrialConditions.Where(c => Fixture.Name(task, c) != "audiovisualCoherent").Sum(c => c.Weight));
        Assert.IsTrue(Fixture.Draw(task, 100).All(t => t.trialType == "audiovisualCoherent"));
    }

    [TestMethod]
    public void EachConditionIsDrawnAtRandomByWeight()
    {
        // Stage 3: left 10, right 10, coherent 5, conflict 5, neutral 1, each new trial drawn independently.
        var trials = Fixture.Draw(Fixture.Task("task-discrimination-left-right.yml", 3), 6200);
        var weights = new Dictionary<string, double> { { "left", 10 }, { "right", 10 }, { "audiovisualCoherent", 5 }, { "audiovisualConflict", 5 }, { "neutral", 1 } };
        foreach (var w in weights)
        {
            Assert.AreEqual(w.Value / 31, trials.Count(t => t.trialType == w.Key) / 6200.0, 0.015, w.Key);
        }
        var runs = trials.Zip(trials.Skip(1), (a, b) => a.trialType == b.trialType).Count(x => x) / 6199.0;
        Assert.AreEqual(weights.Values.Sum(v => (v / 31) * (v / 31)), runs, 0.02);   // independent: no queue
    }

    [TestMethod]
    public void RepeatsFollowEachConditionsRule()
    {
        var task = Fixture.Task("task-localization.yml", 4);
        task.RngSeed = 7;
        var trials = Fixture.Draw(task, 400, t => TrialOutcomes.Incorrect);
        for (int i = 1; i < trials.Count; i++)
        {
            var before = trials[i - 1];
            bool repeats = before.repeatCount < task.Repeats.MaxRepeats &&
                (before.trialType == "visual" || before.trialType == "auditory" || before.trialType == "audiovisualCoherent");
            Assert.AreEqual(repeats ? before.repeatCount + 1 : 0, trials[i].repeatCount, before.trialType);
            if (repeats) Assert.IsTrue(before.visualInitialAzimuthDeg.Equals(trials[i].visualInitialAzimuthDeg) && before.trialConditionId == trials[i].trialConditionId);
        }
        Assert.IsTrue(trials.Any(t => t.repeatCount == task.Repeats.MaxRepeats));
        // Only a trial with a right answer repeats: never a conflict or neutral one (repeatOn never, x-rules).
        var noResponse = Fixture.Draw(task, 400, t => Choice.NoResponse);
        Assert.IsTrue(noResponse.Any(t => t.trialType == "neutral") && noResponse.Any(t => t.trialType == "audiovisualConflict"));
        Assert.IsFalse(noResponse.Any(t => t.repeatCount > 0 && (t.trialType == "neutral" || t.trialType == "audiovisualConflict")));
        var leftRight = Fixture.Task("task-discrimination-left-right.yml", 3);
        leftRight.Repeats.RepeatOn = RepeatOn.NotCorrect;
        leftRight.Repeats.MaxRepeats = 5;
        foreach (var c in leftRight.TrialConditions.Where(Fixture.IsProbe)) { ((IDictionary<object, object>)c.Set)["repeats.repeatOn"] = "never"; ((IDictionary<object, object>)c.Set)["repeats.maxRepeats"] = 0; }
        var lr = Fixture.Draw(leftRight, 400, t => TrialOutcomes.Incorrect);
        Assert.IsTrue(lr.Any(t => t.repeatCount > 0 && (t.trialType == "left" || t.trialType == "right")));
        Assert.IsFalse(lr.Any(t => t.repeatCount > 0 && (t.trialType == "audiovisualCoherent" || t.trialType == "audiovisualConflict" || t.trialType == "neutral")));
        // A condition's own rule: the left condition never repeats, the right does.
        ((IDictionary<object, object>)leftRight.TrialConditions[0].Set)["repeats.repeatOn"] = "never";
        ((IDictionary<object, object>)leftRight.TrialConditions[0].Set)["repeats.maxRepeats"] = 0;
        lr = Fixture.Draw(leftRight, 400, t => TrialOutcomes.Incorrect);
        Assert.IsTrue(lr.Any(t => t.repeatCount > 0 && t.trialType == "right") && !lr.Any(t => t.repeatCount > 0 && t.trialType == "left"));
    }

    [TestMethod]
    public void AProbeRepeatsOnlyOnNoResponseAndNotCorrectOnAProbeIsRefused()
    {
        // P6: a probe (no right answer) may repeat after a miss when its repeatOn is noResponse; never after a choice.
        var task = Fixture.Task("task-localization.yml", 4);
        Fixture.Only(task, "audiovisualConflict");
        var set = (IDictionary<object, object>)task.TrialConditions[3].Set;
        set["repeats.repeatOn"] = "noResponse";
        set["repeats.maxRepeats"] = 2;
        Assert.IsTrue(Fixture.Draw(task, 100, t => TrialOutcomes.Incorrect).All(t => t.repeatCount == 0), "a choice on a probe");
        var missed = Fixture.Draw(task, 100, t => TrialOutcomes.NoResponse);
        Assert.IsTrue(missed.Any(t => t.repeatCount == 2) && missed.All(t => t.repeatCount <= 2));
        set["repeats.repeatOn"] = "notCorrect";
        var refusal = Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers()));
        StringAssert.Contains(refusal, "trialConditions[3] (audiovisualConflict): a probe (target random or noReward) needs repeats.repeatOn never or noResponse, not notCorrect");
        Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(refusal, "a probe [(]target").Count, "said once per condition");
        // A withheld noGo is correct, its choice noResponse: never a noResponse repeat.
        var goNoGo = Fixture.Task("task-discrimination-go-no-go.yml", 2);
        Fixture.Only(goNoGo, "noGo");
        goNoGo.Repeats.RepeatOn = RepeatOn.NoResponse;
        goNoGo.Repeats.MaxRepeats = 5;
        var draw = new TrialDraw(Fixture.Text(goNoGo), Fixture.Speakers(), path => new ZapitConditionsFile(2, ZapitConditions.MaximumRampDownSeconds));
        var first = draw.Next(null);
        Assert.AreEqual(0, draw.Next(TrialRow.Build(Choice.NoResponse, first, new TrialTimes { Now = 1 }, Penalties.Of(4, -35.0, 0, 10))).repeatCount);
    }

    [TestMethod]
    public void EachConditionNamesItsTargetAndTheTaskNone()
    {
        // P10: target is each condition's; the task's is refused, and so is a condition without one.
        var task = Fixture.Task("task-localization.yml", 4);
        Assert.IsNull(task.Target, "the repo's task files set none");
        task.Target = UclOpenCoenDomeDecisionMakingTask.Target.VisualSide;
        StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers())), "target is each trial condition's (its set's target), not the task's");
        task = Fixture.Task("task-localization.yml", 4);
        ((IDictionary<object, object>)task.TrialConditions[2].Set).Remove("target");
        var refusal = Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers()));
        StringAssert.Contains(refusal, "trialConditions[2] (audiovisualCoherent) with ");
        StringAssert.Contains(refusal, ": needs a target in its set");
    }

    [TestMethod]
    public void NoResponseRepeatsAfterANoResponseAlone()
    {
        // noResponse names its outcome: a NoResponse repeats the trial, an Incorrect never does.
        var task = Fixture.Task("task-localization.yml", 4);
        task.RngSeed = 7;
        task.Repeats.RepeatOn = RepeatOn.NoResponse;
        Assert.IsTrue(Fixture.Draw(task, 100, t => TrialOutcomes.Incorrect).All(t => t.repeatCount == 0));
        Assert.IsTrue(Fixture.Draw(task, 100, t => Choice.NoResponse).Any(t => t.repeatCount > 0));
    }

    [TestMethod]
    public void AnAutoTrialNeverRepeats()
    {
        // An auto trial (auto-reward, no choice) is neither a noResponse nor a notCorrect: the next trial is a fresh draw.
        var task = Fixture.Task("task-localization.yml", 4);
        task.RngSeed = 7;
        foreach (var on in new[] { RepeatOn.NoResponse, RepeatOn.NotCorrect })
        {
            task.Repeats.RepeatOn = on;
            Assert.IsTrue(Fixture.Draw(task, 100, t => TrialOutcomes.Auto).All(t => t.repeatCount == 0), on.ToString());
        }
    }

    [TestMethod]
    public void EachTrialDrawsItsOwnInterval()
    {
        var task = Fixture.Task("task-discrimination-left-right.yml", 3);
        var trials = Fixture.Draw(task, 300, t => t.trialNumber % 3 == 0 ? TrialOutcomes.Incorrect : TrialOutcomes.Correct);
        for (int i = 0; i < trials.Count; i++) Assert.AreEqual(i + 1, trials[i].trialNumber);
        // The task files' interval (Pip, 20 Nov 2026): 2.5 to 3.5 s.
        Assert.IsTrue(trials.All(t => t.itiSeconds >= 2.5 && t.itiSeconds <= 3.5));
        Assert.IsTrue(trials.Max(t => t.itiSeconds) > 3.45 && trials.Min(t => t.itiSeconds) < 2.55);
        var repeats = Fixture.Draw(Fixture.Task("task-localization.yml", 4), 300, t => TrialOutcomes.Incorrect).Where(t => t.repeatCount > 0).ToList();
        Assert.IsTrue(repeats.Count > 10 && repeats.Select(t => t.itiSeconds).Distinct().Count() == repeats.Count, "a repeat draws its own");
    }

    [TestMethod]
    public void TheDiscriminationFileHasTheLabsDefaults()
    {
        // Pip, 20 Nov 2026: a rewarded trial is its outcome, 1.5 s to drink, then a 2.5 to 3.5 s interval; a penalised one 4 s of timeout, then the interval.
        var s = Fixture.Task("task-discrimination-left-right.yml", 0);
        Assert.AreEqual(2.5, s.TrialStructure.ItiMinSeconds);
        Assert.AreEqual(3.5, s.TrialStructure.ItiMaxSeconds);
        Assert.AreEqual(1.5, s.Spouts.RewardConsumptionSeconds);
        Assert.AreEqual(4.0, s.Penalty.Incorrect.TimeoutSeconds);
        Assert.AreEqual(4.0, s.Penalty.NoResponse.TimeoutSeconds);
        Assert.AreEqual(0.0, s.Penalty.Incorrect.Flash.Brightness);
    }

    [TestMethod]
    public void OptogeneticsSendsAConditionOnEveryTrial()
    {
        var task = Fixture.Task("task-localization.yml", 4);
        task.Optogenetics.Enabled = true;
        task.Optogenetics.TrialsBeforeLaser = 20;
        task.Optogenetics.LaserOnProbability = 0.5;
        task.Optogenetics.LaserDurationSeconds = 2.3;   // the most itiMinSeconds 2.5 allows
        var trials = Fixture.Draw(task, 300, t => Choice.NoResponse);
        Assert.IsTrue(trials.All(t => t.laserCondition == 1 || t.laserCondition == 2));
        Assert.IsTrue(trials.Take(20).All(t => !t.laserOn));
        Assert.IsTrue(trials.All(t => t.laserPowerMw == (t.laserOn ? 3.0 : 0)));
        Assert.IsTrue(trials.Any(t => t.laserOn) && trials.Skip(20).Any(t => !t.laserOn));
        Assert.IsTrue(trials.Any(t => t.repeatCount > 0) && trials.Where(t => t.repeatCount > 0).All(t => !t.laserOn && t.laserPowerMw == 0), "a repeat never has the laser");
        Assert.AreEqual(2, trials.Select(t => t.laserCondition).Distinct().Count());
    }

    [TestMethod]
    public void WithoutZapitNoConditionIsSent()
    {
        Assert.IsTrue(Fixture.Draw(Fixture.Task("task-discrimination-left-right.yml", 3), 100).All(t => t.laserCondition == 0 && !t.laserOn && t.laserPowerMw == 0));
    }

    [TestMethod]
    public void ARepeatOfTheSameSeedDrawsTheSameTrials()
    {
        var a = Fixture.Draw(Fixture.Task("task-localization.yml", 4), 50);
        var b = Fixture.Draw(Fixture.Task("task-localization.yml", 4), 50);
        CollectionAssert.AreEqual(a.Select(t => t.visualPhaseRad).ToList(), b.Select(t => t.visualPhaseRad).ToList());
    }

    [TestMethod]
    public void TheDrawRefusesATaskItCannotRun()
    {
        var speakers = Fixture.Speakers();
        Func<UclOpenCoenDomeDecisionMakingTaskParameters, string> refusal = parameters =>
            Fixture.Refusal(() => new TrialDraw(Fixture.Text(parameters), speakers, path => new ZapitConditionsFile(2, ZapitConditions.MaximumRampDownSeconds)));

        var task = Fixture.Task("task-localization.yml", 4);
        task.VisualStimulus.Contrasts.Clear();
        StringAssert.Contains(refusal(task), "contrasts must each be a finite number, 0 or more, at least one");

        task = Fixture.Task("task-localization.yml", 4);
        task.AuditoryStimulus.LevelDbfs = "loud";
        StringAssert.Contains(refusal(task), "auditoryStimulus.levelDbfs: A sound level is a number of dBFS RMS or mute, not loud");
        task = Fixture.Task("task-localization.yml", 4);
        task.AuditoryStimulus.LevelDbfs = -0.5;
        StringAssert.Contains(refusal(task), "auditoryStimulus.levelDbfs -0.5 is above -1 dBFS");
        task = Fixture.Task("task-localization.yml", 4);
        task.Penalty.Incorrect.Sound.LevelDbfs = "off";
        StringAssert.Contains(refusal(task), "penalty.incorrect.sound.levelDbfs: A sound level is a number of dBFS RMS or mute, not off");
        // The penalty's level is bounded at Go as the stimulus's is, naming the condition: task-wide, and in a condition's set naming it.
        task = Fixture.Task("task-localization.yml", 4);
        task.Penalty.Incorrect.Sound.LevelDbfs = 0;
        StringAssert.Contains(refusal(task), "penalty.incorrect.sound.levelDbfs 0 is above -1 dBFS");
        task = Fixture.Task("task-localization.yml", 4);
        ((IDictionary<object, object>)task.TrialConditions[1].Set)["penalty.incorrect.sound.levelDbfs"] = 10;
        StringAssert.Contains(refusal(task), "trialConditions[1]");
        StringAssert.Contains(refusal(task), "penalty.incorrect.sound.levelDbfs 10 is above -1 dBFS");
        // repeatOn never exactly when maxRepeats is 0 (task.py's x-rule), both ways.
        task = Fixture.Task("task-localization.yml", 4);
        task.Repeats.RepeatOn = RepeatOn.Never;
        task.Repeats.MaxRepeats = 3;
        StringAssert.Contains(refusal(task), "repeats.repeatOn never needs repeats.maxRepeats 0");
        task = Fixture.Task("task-localization.yml", 4);
        task.Repeats.RepeatOn = RepeatOn.NoResponse;
        task.Repeats.MaxRepeats = 0;
        StringAssert.Contains(refusal(task), "repeats.maxRepeats 0 needs repeats.repeatOn never");

        task = Fixture.Task("task-localization.yml", 4);
        task.VisualStimulus.InitialAzimuthsDeg = new List<double> { -60, 200 };
        StringAssert.Contains(refusal(task), "initialAzimuthsDeg value 200 is outside [-180, 180]");

        task = Fixture.Task("task-localization.yml", 4);
        task.Stimulus.DurationSeconds = 3.0;
        StringAssert.Contains(refusal(task), "stimulus.durationSeconds (3 s) is longer than the stimulus to the response window's close (2.5 s)");

        task = Fixture.Task("task-localization.yml", 4);
        task.Stimulus.DurationSeconds = null;
        task.Stimulus.ResponseToStimulusOffSeconds = null;
        StringAssert.Contains(refusal(task), "stimulus.durationSeconds and stimulus.responseToStimulusOffSeconds are both null");

        task = Fixture.Task("task-localization.yml", 4);
        // Off before the next stimulus: from the soonest outcome (the window's opening, 0.5 s) at most the shortest outcome
        // to next interval (0: a reward's interval starts at the outcome) plus itiMinSeconds (2.5), so 3 s from onset.
        task.TrialStructure.ResponseWindowSeconds = 5;
        task.Stimulus.DurationSeconds = 3.0;
        Assert.AreEqual("accepted", refusal(task));
        task.Stimulus.DurationSeconds = 3.1;
        StringAssert.Contains(refusal(task), "stimulus.durationSeconds (3.1 s) runs 2.6 s past the soonest outcome, longer than the shortest outcome to next interval (0 s");

        task = Fixture.Task("task-localization.yml", 4);
        task.Stimulus.RateHz = 21;
        StringAssert.Contains(refusal(task), "stimulus.rateHz 21 is above 20");

        task = Fixture.Task("task-localization.yml", 4);
        task.Stimulus.RateHz = 0;
        StringAssert.Contains(refusal(task), "stimulus.rateHz must be a finite number above 0");

        task = Fixture.Task("task-localization.yml", 4);
        task.TrialStructure.ResponseWindowSeconds = null;
        StringAssert.Contains(refusal(task), "responseWindowSeconds must be given");

        task = Fixture.Task("task-localization.yml", 4);
        speakers.Speakers.Remove("right");
        StringAssert.Contains(refusal(task), "is 'centre', not on the right");

        task = Fixture.Task("task-discrimination-left-right.yml", 4);
        task.Optogenetics.Enabled = true;
        task.Optogenetics.LaserPowerMw = null;
        task.Optogenetics.LaserDurationSeconds = 0.8;
        StringAssert.Contains(Fixture.Refusal(() => new TrialDraw(Fixture.Text(task), Fixture.Speakers(), path => new ZapitConditionsFile(2, ZapitConditions.MaximumRampDownSeconds))), "laserPowerMw");
    }

    [TestMethod]
    public void TheCurriculumStageIsCheckedAtGoBeforeTheFirstTrial()
    {
        // curriculum (app-rig-control's at launch) is the task file's only record of the stage: present and well formed.
        var task = Fixture.Task("task-localization.yml", 4);
        Assert.AreEqual("accepted", Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers())));
        Action<string> broken = reason =>
        {
            var curriculum = Fixture.Curriculum(4, "Conflict Added");
            task.Curriculum = curriculum;
            switch (reason)
            {
                case "code": curriculum.Code = "S3A"; break;
                case "version": curriculum.Version = "k7f1"; break;
                case "path": curriculum.Path = "a"; break;
                case "sha256": curriculum.Sha256 = ""; break;
                case "label": curriculum.Label = ""; break;
                case "id": curriculum.Id = -1; break;
            }
            StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers())), "The task file's curriculum is malformed (");
            StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers())), reason == "id" ? "id -1" : reason);
        };
        foreach (var reason in new[] { "code", "version", "path", "sha256", "label", "id" }) broken(reason);
        task.Curriculum = null;
        StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers())), "The task file's curriculum is empty: launch it from app-rig-control");
    }

    [TestMethod]
    public void TheDiscriminationsDrawByRole()
    {
        Assert.IsTrue(Fixture.Draw(Fixture.Task("task-discrimination-left-right.yml", 0), 50).All(t => t.trialType == "left" && t.target == TargetWords.Left), "left-right stage 0: left only");
        Assert.IsTrue(Fixture.Draw(Fixture.Task("task-discrimination-go-no-go.yml", 1), 50).All(t => t.trialType == "go" && t.target == TargetWords.Go), "go/no-go stage 1: go only");
        var goNoGo = Fixture.Task("task-discrimination-go-no-go.yml", 2);
        Fixture.Weight(goNoGo, "audiovisualCoherent", 0);
        var stage2 = Fixture.Draw(goNoGo, 400);
        Assert.IsTrue(stage2.All(t => t.trialType == "go" ? t.target == TargetWords.Go && t.visualContrast > 0 && double.IsNaN(t.auditoryLevelDbfs) : t.trialType == "noGo" && t.target == TargetWords.NoGo && t.auditoryLevelDbfs > -120));
        Assert.AreEqual(0.5, stage2.Count(t => t.trialType == "go") / 400.0, 0.08, "go 10, noGo 10");
        var built = Fixture.Check(Fixture.Task("task-discrimination-left-right.yml", 2), Fixture.Speakers());
        CollectionAssert.AreEqual(new[] { "left", "right", "audiovisualCoherent", "audiovisualConflict", "neutral" }, built.Select(c => c.TrialType).ToArray());
        Assert.AreEqual("accepted", Fixture.Refusal(() => Fixture.Check(Fixture.Task("task-localization.yml", 0), Fixture.Speakers())));
    }

    [TestMethod]
    public void TheDrawTakesTheTasksConditionWeights()
    {
        // The weights edited for a launch (Setup) are the draw's.
        var task = Fixture.Task("task-localization.yml", 4);
        Fixture.Only(task, "neutral");
        Fixture.Weight(task, "neutral", 1);
        Assert.IsTrue(Fixture.Draw(task, 50).All(t => t.trialType == "neutral"));
        var goNoGo = Fixture.Task("task-discrimination-go-no-go.yml", 3);
        Fixture.Only(goNoGo, "go");
        Assert.IsTrue(Fixture.Draw(goNoGo, 50).All(t => t.trialType == "go" && t.target == TargetWords.Go));
    }

    [TestMethod]
    public void ALaserThatWouldDelayTheNextArmIsRefused()
    {
        // Discrimination: 0.5 s stimulus to window + 2.5 s interval - the conditions file's largest ramp-down - 0.2 s reply
        // margin: 2.3 s with a 0.5 s ramp-down, 2.7 s with a 0.1 s one (the file's own, ZapitConditions.Check).
        var task = Fixture.Task("task-discrimination-left-right.yml", 4);
        task.Optogenetics.Enabled = true;
        task.Optogenetics.LaserPowerMw = 3;
        Func<double, string> refusal = rampDown => Fixture.Refusal(() => new TrialDraw(Fixture.Text(task), Fixture.Speakers(), path => new ZapitConditionsFile(2, rampDown)));
        Assert.AreEqual(2.3, TrialDraw.MaxLaserSeconds(task, 0.5), 1e-9);
        Assert.AreEqual(2.7, TrialDraw.MaxLaserSeconds(task, 0.1), 1e-9);
        StringAssert.Contains(refusal(0.5), "laserDurationSeconds (2.5 s) must be at most 2.3 s");
        StringAssert.Contains(refusal(0.5), "the Zapit conditions file's 0.5 s ramp-down");
        Assert.AreEqual("accepted", refusal(0.1), "a 0.1 s ramp-down allows 2.7 s");
        task.Optogenetics.LaserDurationSeconds = 2.8;
        StringAssert.Contains(refusal(0.1), "laserDurationSeconds (2.8 s) must be at most 2.7 s");
        task.Optogenetics.LaserDurationSeconds = 2.3;
        Assert.AreEqual("accepted", refusal(0.5));
        var rules = new TrialRules(Fixture.Text(task), Fixture.Speakers(), true, path => new ZapitConditionsFile(2, 0.1));
        Assert.AreEqual(0.1, rules.Laser.RampDownSeconds, "the next trial armed once the file's own ramp-down has played");
        task.Optogenetics.LaserDurationSeconds = 2.5;
        task.Optogenetics.Enabled = false;
        Assert.AreEqual("accepted", refusal(0.5));
    }

    [TestMethod]
    public void ALargeRewardOverTheCapIsRefusedAtGo()
    {
        // A RewardValve piece refuses a reward over its MaxRewardUl (lib's MAX_REWARD_UL) mid-session; Go refuses it first.
        var task = Fixture.Task("task-localization.yml", 4);
        task.Reward.LargeProbability = 0.2;
        task.Reward.LargeMultiplier = 3;
        task.Reward.SizeUl = 8;
        var refusal = Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers()));
        StringAssert.Contains(refusal, "reward.sizeUl (8 ul) x largeMultiplier (3) is 24 ul, more than 20 ul");
        task.Reward.SizeUl = OpenRigs.Core.LoadRewardLine.DefaultMaxRewardUl / 3;
        Assert.AreEqual("accepted", Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers())));
        task.Reward.SizeUl = 8;
        task.Reward.LargeProbability = 0;
        Assert.AreEqual("accepted", Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers())));
    }

    [TestMethod]
    public void TheSignedLeadIsRefusedOutsideTheIntervalAndTheResponseWindow()
    {
        Func<double, string> refusal = lead =>
        {
            var task = Fixture.Task("task-discrimination-left-right.yml", 4);   // itiMinSeconds 2.5, response window 0.5 + 2.0 s
            task.Optogenetics.LaserToStimulusSeconds = lead;
            return Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers()));
        };
        foreach (var ok in new[] { 2.3, 0.5, 0.1, 0.0, -0.5, -2.5 }) Assert.AreEqual("accepted", refusal(ok), ok.ToString());
        StringAssert.Contains(refusal(2.31), "reply margin must be at most trialStructure.itiMinSeconds");
        StringAssert.Contains(refusal(-2.51), "after the response window");
    }

    [TestMethod]
    public void AResponseWindowOpeningBeforeTheSpoutsIsRefused()
    {
        Func<double, double, string> refusal = (window, spouts) =>
        {
            var task = Fixture.Task("task-discrimination-left-right.yml", 4);
            task.Spouts.RewardConsumptionSeconds = 1.0;
            task.TrialStructure.StimulusToResponseWindowSeconds = window;
            task.Spouts.StimulusToSpoutsInSeconds = spouts;
            return Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers()));
        };
        var early = refusal(0.4, 0.5);
        StringAssert.Contains(early, "stimulusToResponseWindowSeconds (0.4 s) is below spouts.stimulusToSpoutsInSeconds (0.5 s)");
        Assert.AreEqual("accepted", refusal(0, -0.5), "negative: the spouts in before the stimulus, which leads the trial start");
        Assert.AreEqual("accepted", refusal(0.5, 0.5), "equal: the window opens as the spouts arrive");
    }

    [TestMethod]
    public void TheQuietWaitsAreRefusedNegative()
    {
        var task = Fixture.Task("task-localization.yml", 4);
        task.TrialStructure.WheelQuietSeconds = 1;
        Assert.AreEqual("accepted", Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers())), "wheelQuietCm 0: no movement at all");
        task.TrialStructure.WheelQuietCm = 2;
        Assert.AreEqual("accepted", Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers())));
        task.TrialStructure.LickQuietSeconds = -1;
        StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers())), "trialStructure.lickQuietSeconds is -1, not from 0 to 10 s");
        // A NaN or infinite time would hold the trial start for ever, with no error.
        task.TrialStructure.LickQuietSeconds = double.PositiveInfinity;
        StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers())), "lickQuietSeconds is ");
        task = Fixture.Task("task-localization.yml", 4);
        task.TrialStructure.WheelQuietSeconds = double.NaN;
        StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers())), "wheelQuietSeconds is NaN, not from 0 to 10 s");
        task = Fixture.Task("task-localization.yml", 4);
        task.TrialStructure.ItiMaxSeconds = double.PositiveInfinity;
        StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers())), "trialStructure.itiMaxSeconds is ");
        task = Fixture.Task("task-localization.yml", 4);
        task.Penalty.Incorrect.TimeoutSeconds = double.NaN;
        task.Optogenetics.LaserToStimulusSeconds = double.NaN;
        var refusal = Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers()));
        StringAssert.Contains(refusal, "penalty.incorrect.timeoutSeconds must be a finite number, 0 or more");
        StringAssert.Contains(refusal, "laserToStimulusSeconds must be a finite number");
        task = Fixture.Task("task-localization.yml", 4);
        // The drink is inside the interval: the spouts go out rewardConsumptionSeconds into it, before the stimulus.
        task.Spouts.RewardConsumptionSeconds = 3.5;
        StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers())), "spouts.rewardConsumptionSeconds (3.5 s) is above itiMinSeconds");
        task.Spouts.RewardConsumptionSeconds = double.NaN;
        StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers())), "spouts.rewardConsumptionSeconds must be a finite number, 0 or more");
    }

    [TestMethod]
    public void AQuietWaitAboveItsMaximumIsRefusedAtGo()
    {
        var cases = new Dictionary<string, Action<TrialStructure>>
        {
            { "lickQuietSeconds is 10.5, not from 0 to 10 s", s => s.LickQuietSeconds = 10.5 },
            { "wheelQuietSeconds is 11, not from 0 to 10 s", s => { s.WheelQuietSeconds = 11; s.WheelQuietCm = 1; } },
            { "wheelQuietCm is 21, not from 0 to 20 cm", s => { s.WheelQuietSeconds = 1; s.WheelQuietCm = 21; } },
        };
        foreach (var c in cases)
        {
            var task = Fixture.Task("task-localization.yml", 4);
            c.Value(task.TrialStructure);
            StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers())), "trialStructure." + c.Key);
        }
        var atMax = Fixture.Task("task-localization.yml", 4);
        atMax.TrialStructure.LickQuietSeconds = 10;
        atMax.TrialStructure.WheelQuietSeconds = 10;
        atMax.TrialStructure.WheelQuietCm = 20;
        Assert.AreEqual("accepted", Fixture.Refusal(() => Fixture.Check(atMax, Fixture.Speakers())));
    }

    [TestMethod]
    public void NoWeightANegativeWeightOrAReversedIntervalIsRefused()
    {
        // Bonsai reads the task file without pydantic: zero weights would draw no type at the first trial.
        var task = Fixture.Task("task-localization.yml", 4);
        Fixture.Only(task);
        StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers())), "trialConditions needs a condition with a weight above 0");
        task = Fixture.Task("task-localization.yml", 3);
        Fixture.Weight(task, "neutral", -1);
        StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers())), "trialConditions[4] (neutral): its weight is below 0");
        task.TrialConditions.Clear();
        StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers())), "trialConditions needs a condition");
        task = Fixture.Task("task-localization.yml", 4);
        task.TrialStructure.ItiMaxSeconds = task.TrialStructure.ItiMinSeconds - 0.1;
        StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers())), "trialStructure.itiMaxSeconds (");
        task = Fixture.Task("task-localization.yml", 4);
        task.AutoRewardProbability = 1.5;
        StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers())), "autoRewardProbability is 1.5, not from 0 to 1");
    }

    [TestMethod]
    public void AConditionTheTypedReadWouldMisreadIsRefused()
    {
        // A hand-edited file run from the editor: the typed read fills a missing weight with 0, merges a << key and ignores a
        // set that is not a map, where the draw reads the node tree; each is refused at Go, naming the condition.
        Func<YamlSequenceNode, YamlMappingNode> first = s => (YamlMappingNode)s.Children[0];
        var cases = new Dictionary<string, Action<YamlSequenceNode>>
        {
            { "trialConditions[0] (visual): it has no weight", s => first(s).Children.Remove(new YamlScalarNode("weight")) },
            { "trialConditions[0] (audiovisualCoherent): its set is not a map", s => first(s).Children[new YamlScalarNode("set")] = new YamlScalarNode("5") },
            // Condition [1] merges condition [0] (an alias, saved as *anchor): the typed read sees [0]'s set, the node tree does not.
            { "trialConditions[1] (visual): a YAML merge key (<<)", s => ((YamlMappingNode)s.Children[1]).Children[new YamlScalarNode("<<")] = first(s) },
            { "trialConditions[0] (visual): its weight is above 1000000", s => first(s).Children[new YamlScalarNode("weight")] = new YamlScalarNode("1000001") },
        };
        foreach (var c in cases)
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(Fixture.Text(Fixture.Task("task-localization.yml", 4))));
            var parameters = ((YamlMappingNode)stream.Documents[0].RootNode).Children.Values.OfType<YamlMappingNode>().First(m => m.Children.ContainsKey(new YamlScalarNode("trialConditions")));
            c.Value((YamlSequenceNode)parameters.Children[new YamlScalarNode("trialConditions")]);
            var writer = new StringWriter();
            stream.Save(writer, true);   // true: a node met twice is saved once with an anchor, then as its alias
            UclOpenCoenDomeDecisionMakingTaskParameters read;
            StringAssert.Contains(Fixture.Refusal(() => TrialDraw.CheckTask(writer.ToString(), Fixture.Speakers(), out read)), c.Key);
        }
    }
}

[TestClass]
public class TrialConditionTests
{
    // Localization stage 4 with these conditions, repeating never.
    static UclOpenCoenDomeDecisionMakingTaskParameters With(params TrialCondition[] conditions)
    {
        var task = Fixture.Task("task-localization.yml", 4);
        task.TrialConditions = conditions.ToList();
        task.Repeats.RepeatOn = RepeatOn.Never;   // a probe condition may not inherit notCorrect
        task.Repeats.MaxRepeats = 0;
        return task;
    }

    static string Refused(UclOpenCoenDomeDecisionMakingTaskParameters task)
    {
        return Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers()));
    }

    // Localization's three stimulus conditions at one azimuth, as the task file writes them.
    static TrialCondition AtAzimuth(string label, double azimuth)
    {
        var position = Fixture.List(azimuth);
        if (label == "visual") return Fixture.Condition(1, "target", "visualSide", "visualStimulus.initialAzimuthsDeg", position, "auditoryStimulus.initialAzimuthsDeg", Fixture.List(0.0));
        if (label == "auditory") return Fixture.Condition(1, "target", "auditorySide", "visualStimulus.contrasts", Fixture.List(0.0), "auditoryStimulus.initialAzimuthsDeg", position);
        return Fixture.Paired(Fixture.Condition(1, "target", "visualSide", "visualStimulus.initialAzimuthsDeg", position, "auditoryStimulus.initialAzimuthsDeg", position));
    }

    [TestMethod]
    public void TheAzimuthsSignGivesTheSideAndTheTarget()
    {
        foreach (var azimuth in new[] { 60.0, -75.0 })
        {
            foreach (var type in new[] { "visual", "auditory", "audiovisualCoherent" })
            {
                var trials = Fixture.Draw(With(AtAzimuth(type, azimuth)), 40);
                var side = azimuth > 0 ? Choice.Right : Choice.Left;
                Assert.IsTrue(trials.All(t => t.target == side && t.trialType == type), type);
                if (type != "auditory") Assert.IsTrue(trials.All(t => t.visualInitialAzimuthDeg == azimuth && t.VisualStimulus.ElevationDeg == 15), "the grating starts there exactly");
                if (type != "visual") Assert.IsTrue(trials.All(t => t.auditoryInitialAzimuthDeg == (azimuth > 0 ? 90 : -90)), "the nearest speaker");
                if (type == "visual") Assert.IsTrue(trials.All(t => t.auditoryInitialAzimuthDeg == 0), "the centre sound");
            }
        }
        // Several listed: one picked per trial, both sides drawn.
        var mixed = Fixture.Draw(With(Fixture.Condition(1, "visualStimulus.initialAzimuthsDeg", Fixture.List(-60.0, 60.0))), 200);
        Assert.AreEqual(0.5, mixed.Count(t => t.target == TargetWords.Right) / 200.0, 0.1);
        Assert.IsTrue(mixed.All(t => (t.visualInitialAzimuthDeg > 0) == (t.target == TargetWords.Right)));
    }

    [TestMethod]
    public void ANoGratingTrialLogsItsAzimuthAsNaNAndRendersAtZero()
    {
        var none = Fixture.Draw(With(Fixture.Condition(1, "visualStimulus.contrasts", Fixture.List(0.0))), 20);
        Assert.IsTrue(none.All(t => t.visualContrast == 0 && double.IsNaN(t.visualInitialAzimuthDeg)), "logged NaN, contrast 0");
        Assert.IsTrue(none.All(t => t.gratingAzimuthDeg == 0), "the renderer never gets NaN");
        var shown = Fixture.Draw(With(Fixture.Condition(1, "visualStimulus.contrasts", Fixture.List(1.0))), 20);
        Assert.IsTrue(shown.All(t => t.gratingAzimuthDeg == t.visualInitialAzimuthDeg && !double.IsNaN(t.visualInitialAzimuthDeg)));
        // P13: the grating's shape is read from the trial's combination, a condition's set too.
        var shaped = Fixture.Draw(With(Fixture.Condition(1, "visualStimulus.orientationDeg", Fixture.List(0.0, 90.0), "visualStimulus.sigmaDeg", 10.0)), 100);
        Assert.IsTrue(shaped.All(t => t.VisualStimulus.SigmaDeg == 10 && t.VisualStimulus.ElevationDeg == 15));
        Assert.IsTrue(shaped.Any(t => t.VisualStimulus.OrientationDeg == 90) && shaped.Any(t => t.VisualStimulus.OrientationDeg == 0));
    }

    [TestMethod]
    public void PairedListsShareOnePickAndUnpairedOnesDrawIndependently()
    {
        var both = Fixture.List(-60.0, 60.0);
        var paired = Fixture.Draw(With(Fixture.Paired(Fixture.Condition(1, "target", "random",
            "visualStimulus.initialAzimuthsDeg", both, "auditoryStimulus.initialAzimuthsDeg", Fixture.List(60.0, -60.0)))), 200);
        Assert.IsTrue(paired.All(t => (t.visualInitialAzimuthDeg > 0) == (t.auditoryInitialAzimuthDeg < 0) && t.target == TargetWords.Random && !t.singleSpoutOffered));
        Assert.AreEqual(0.5, paired.Count(t => t.visualInitialAzimuthDeg > 0) / 200.0, 0.1);
        var unpaired = Fixture.Draw(With(Fixture.Condition(1, "visualStimulus.initialAzimuthsDeg", both, "auditoryStimulus.initialAzimuthsDeg", both)), 400);
        Assert.AreEqual(0.5, unpaired.Count(t => (t.visualInitialAzimuthDeg > 0) == (t.auditoryInitialAzimuthDeg > 0)) / 400.0, 0.08, "independent");
        // Contrast paired with level: the brighter grating with the louder sound.
        var levels = Fixture.Condition(1, "visualStimulus.contrasts", Fixture.List(0.25, 1.0), "auditoryStimulus.levelDbfs", Fixture.List(-46.0, -40.0));
        levels.Pairs = new List<List<string>> { new List<string> { "visualStimulus.contrasts", "auditoryStimulus.levelDbfs" } };
        var drawn = Fixture.Draw(With(levels), 200);
        Assert.IsTrue(drawn.All(t => t.visualContrast == 0.25 ? t.auditoryLevelDbfs == -46 : t.auditoryLevelDbfs == -40));
        Assert.IsTrue(drawn.Any(t => t.auditoryLevelDbfs == -46) && drawn.Any(t => t.auditoryLevelDbfs == -40));
        Assert.AreEqual(8, Fixture.Check(With(levels), Fixture.Speakers())[0].Combinations.Count, "the pair's 2 by the task's two lists of 2 azimuths");
        levels.Set = new Dictionary<object, object> { { "target", "visualSide" }, { "visualStimulus.contrasts", Fixture.List(0.25, 0.5, 1.0) }, { "auditoryStimulus.levelDbfs", Fixture.List(-46.0, -40.0) } };
        StringAssert.Contains(Refused(With(levels)), "trialConditions[0] (audiovisualCoherent): pairs [visualStimulus.contrasts, auditoryStimulus.levelDbfs] have lists of lengths 3, 2");
        levels.Pairs = new List<List<string>> { new List<string> { "visualStimulus.contrasts" } };
        StringAssert.Contains(Refused(With(levels)), "each pairs group needs two paths or more");
        levels.Pairs = new List<List<string>> { new List<string> { "visualStimulus.contrasts", "rngSeed" } };
        StringAssert.Contains(Refused(With(levels)), "sets or pairs rngSeed, a session field (x-session-fields: rngSeed)");
    }

    [TestMethod]
    public void TheMaxSoundLevelIsTheHighestLevelAWeightedConditionDraws()
    {
        var rules = new TrialRules(Fixture.Text(With(Fixture.Condition(1, "auditoryStimulus.levelDbfs", Fixture.List("mute", -46.0, -38.0)), Fixture.Condition(0, "auditoryStimulus.levelDbfs", -10.0))),
            Fixture.Speakers(), false, path => new ZapitConditionsFile(2, ZapitConditions.MaximumRampDownSeconds));
        Assert.AreEqual(-38.0, rules.MaxStimulusLevel.LevelDbfs);
        StringAssert.StartsWith(rules.MaxStimulusLevel.Where, "trialConditions[0] (audiovisualCoherent) with ");
        StringAssert.Contains(rules.MaxStimulusLevel.Where, "auditoryStimulus.levelDbfs -38, ");
        var mute = new TrialRules(Fixture.Text(With(Fixture.Condition(1, "auditoryStimulus.levelDbfs", "mute"))), Fixture.Speakers(), false, path => new ZapitConditionsFile(2, ZapitConditions.MaximumRampDownSeconds));
        Assert.IsTrue(double.IsNaN(mute.MaxStimulusLevel.LevelDbfs), "all mute: none");
    }

    [TestMethod]
    public void ThePenaltyLevelIsEachTrialsAndTheMaxPenaltyLevelIsLaidOutAtGo()
    {
        // P19: penalty.incorrect.sound.levelDbfs is condition-level; each outcome carries its trial's level (NaN mute), and
        // MaxPenaltyLevelDbfs is the highest a weighted condition draws, for PlayPenaltySound's buffer at Go.
        var task = With(Fixture.Condition(1, "penalty.incorrect.sound.levelDbfs", -50.0), Fixture.Condition(1, "penalty.incorrect.sound.levelDbfs", "mute"),
            Fixture.Condition(0, "penalty.incorrect.sound.levelDbfs", -20.0));
        Assert.AreEqual(-50.0, new TrialRules(Fixture.Text(task), Fixture.Speakers(), false, path => new ZapitConditionsFile(2, ZapitConditions.MaximumRampDownSeconds)).MaxPenaltyLevelDbfs, "the weight-0 condition's -20 is never drawn");
        foreach (var t in Fixture.Draw(task, 100))
        {
            var wrong = TrialOutcome.At(t.target == Choice.Left ? Choice.Right : Choice.Left, t, new TrialTimes { Now = 1 });
            Assert.IsTrue(wrong.Penalised && wrong.Row.Value.penalised);
            if (t.trialConditionId == 0) Assert.AreEqual(-50.0, wrong.PenaltyLevelDbfs);
            else Assert.IsTrue(double.IsNaN(wrong.PenaltyLevelDbfs), "mute: penalised, no sound");
        }
    }

    [TestMethod]
    public void AMissCostsPenaltyNoResponseAndAChoicePenaltyIncorrect()
    {
        // The localization file: a miss has the 4 s timeout and no sound (noResponse.sound.levelDbfs mute), a wrong choice both.
        var task = With(Fixture.Condition(1));
        var t = Fixture.Draw(task, 1)[0];
        var wrong = TrialOutcome.At(t.target == Choice.Left ? Choice.Right : Choice.Left, t, new TrialTimes { Now = 1 });
        var miss = TrialOutcome.At(Choice.NoResponse, t, new TrialTimes { Now = 1 });
        Assert.IsTrue(wrong.PenaltyTimeoutSeconds == 4 && wrong.PenaltyLevelDbfs == -35 && wrong.Row.Value.penaltyTimeoutSeconds == 4);
        Assert.IsTrue(miss.Penalised && miss.PenaltyTimeoutSeconds == 4 && double.IsNaN(miss.PenaltyLevelDbfs) && miss.Row.Value.penaltyTimeoutSeconds == 4);
        // The miss's own values changed: the choice's unchanged; the row logs the timeout it used.
        task.Penalty.NoResponse.TimeoutSeconds = 2;
        task.Penalty.NoResponse.Sound.LevelDbfs = -45.0;
        t = Fixture.Draw(task, 1)[0];
        miss = TrialOutcome.At(Choice.NoResponse, t, new TrialTimes { Now = 1 });
        wrong = TrialOutcome.At(t.target == Choice.Left ? Choice.Right : Choice.Left, t, new TrialTimes { Now = 1 });
        Assert.IsTrue(miss.PenaltyTimeoutSeconds == 2 && miss.PenaltyLevelDbfs == -45 && miss.Row.Value.penaltyTimeoutSeconds == 2);
        Assert.IsTrue(wrong.PenaltyTimeoutSeconds == 4 && wrong.PenaltyLevelDbfs == -35);
        // A random-reward probe is unpenalised by default (penaliseProbes false): tails and a miss cost nothing.
        var free = Fixture.Draw(With(Fixture.Condition(1, "target", "random")), 1)[0];
        free.probeHeads = false;
        foreach (var c in new[] { Choice.NoResponse, Choice.Left })
        {
            var o = TrialOutcome.At(c, free, new TrialTimes { Now = 1 });
            Assert.IsTrue(!o.Penalised && !o.Row.Value.penalised && o.PenaltyTimeoutSeconds == 0 && !o.Rewarded, c);
        }
        // With penaliseProbes, a probe's miss (outcome probe) is a miss; its choice on tails is a choice.
        var probe = Fixture.Draw(With(Fixture.Condition(1, "target", "random", "penalty.noResponse.timeoutSeconds", 2.0, "penaliseProbes", true)), 1)[0];
        probe.probeHeads = false;
        var probeMiss = TrialOutcome.At(Choice.NoResponse, probe, new TrialTimes { Now = 1 });
        var tails = TrialOutcome.At(Choice.Left, probe, new TrialTimes { Now = 1 });
        Assert.IsTrue(probeMiss.Row.Value.outcome == TrialOutcomes.Probe && probeMiss.Penalised && probeMiss.PenaltyTimeoutSeconds == 2 && double.IsNaN(probeMiss.PenaltyLevelDbfs));
        Assert.IsTrue(tails.Penalised && tails.PenaltyTimeoutSeconds == 4 && tails.PenaltyLevelDbfs == -35);
    }

    [TestMethod]
    public void APenalisedTrialFlashesItsKindsFlashAndLogsItsBrightness()
    {
        // penalty.<kind>.flash (lib-open-rigs PenaltyFlash): the outcome carries its kind's flash for PlayPenaltyFlash, the
        // row its brightness (0 when none plays or not penalised).
        var task = With(Fixture.Condition(1));
        task.Penalty.Incorrect.Flash.Brightness = 0.5;
        task.Penalty.Incorrect.Flash.RateHz = 5;
        var t = Fixture.Draw(task, 1)[0];
        var wrong = TrialOutcome.At(t.target == Choice.Left ? Choice.Right : Choice.Left, t, new TrialTimes { Now = 1 });
        var miss = TrialOutcome.At(Choice.NoResponse, t, new TrialTimes { Now = 1 });
        var correct = TrialOutcome.At(t.target, t, new TrialTimes { Now = 1 });
        Assert.IsTrue(wrong.PenaltyFlash.Plays && wrong.PenaltyFlash.Brightness == 0.5 && wrong.PenaltyFlash.RateHz == 5 && wrong.Row.Value.penaltyFlashBrightness == 0.5);
        Assert.IsTrue(miss.Penalised && !miss.PenaltyFlash.Plays && miss.Row.Value.penaltyFlashBrightness == 0, "penalty.noResponse's flash: brightness 0");
        Assert.IsTrue(correct.Rewarded && !correct.PenaltyFlash.Plays && correct.Row.Value.penaltyFlashBrightness == 0);
        var flashes = new TrialRules(Fixture.Text(task), Fixture.Speakers(), false, path => new ZapitConditionsFile(2, ZapitConditions.MaximumRampDownSeconds)).PenaltyFlashes;
        Assert.IsTrue(flashes.Plays && flashes.ShortestTimeoutSeconds == 4 && flashes.Problems.Count == 0);
        // Go/no-go: penalty.noResponse null, so only penalty.incorrect's flash is drawn; none plays at brightness 0.
        Assert.IsFalse(new TrialRules(Fixture.Text(Fixture.Task("task-discrimination-go-no-go.yml", 4)), Fixture.Speakers(), false, path => new ZapitConditionsFile(2, ZapitConditions.MaximumRampDownSeconds)).PenaltyFlashes.Plays);
    }

    [TestMethod]
    public void TheMaxPenaltyLevelCountsAMissesLevelAndAConditionMaySetIt()
    {
        // A condition sets penalty.noResponse.sound.levelDbfs: louder than every choice's, it is the buffer's level, named.
        var task = With(Fixture.Condition(1), Fixture.Condition(1, "penalty.noResponse.sound.levelDbfs", -30.0), Fixture.Condition(0, "penalty.noResponse.sound.levelDbfs", -20.0));
        var rules = new TrialRules(Fixture.Text(task), Fixture.Speakers(), false, path => new ZapitConditionsFile(2, ZapitConditions.MaximumRampDownSeconds));
        Assert.AreEqual(-30.0, rules.MaxPenaltyLevelDbfs, "the weight-0 condition's -20 is never drawn");
        var draw = new TrialDraw(Fixture.Text(task), Fixture.Speakers(), path => new ZapitConditionsFile(2, ZapitConditions.MaximumRampDownSeconds));
        StringAssert.StartsWith(draw.MaxPenaltyLevel().Where, "trialConditions[1]");
        foreach (var t in Fixture.Draw(task, 40).Where(x => x.trialConditionId == 1))
            Assert.AreEqual(-30.0, TrialOutcome.At(Choice.NoResponse, t, new TrialTimes { Now = 1 }).PenaltyLevelDbfs);
        // Left-right's file: both kinds at -35.
        Assert.AreEqual(-35.0, new TrialRules(Fixture.Text(Fixture.Task("task-discrimination-left-right.yml", 4)), Fixture.Speakers(), false, path => new ZapitConditionsFile(2, ZapitConditions.MaximumRampDownSeconds)).MaxPenaltyLevelDbfs);
    }

    [TestMethod]
    public void GoNoGoRefusesAMissesCostAndTheOthersNeedOne()
    {
        Func<UclOpenCoenDomeDecisionMakingTaskParameters, string> refusal = parameters =>
            Fixture.Refusal(() => new TrialDraw(Fixture.Text(parameters), Fixture.Speakers(), path => new ZapitConditionsFile(2, ZapitConditions.MaximumRampDownSeconds)));
        var goNoGo = Fixture.Task("task-discrimination-go-no-go.yml", 4);
        Assert.IsNull(goNoGo.Penalty.NoResponse);
        goNoGo.Penalty.NoResponse = Fixture.Task("task-discrimination-left-right.yml", 4).Penalty.NoResponse;
        StringAssert.Contains(refusal(goNoGo), "penalty.noResponse must be null in discriminationGoNoGo");
        // A task that penalises a miss needs penalty.noResponse (lib-open-rigs Penalties.Problems).
        var missing = Fixture.Task("task-localization.yml", 4);
        missing.Penalty.NoResponse = null;
        StringAssert.Contains(refusal(missing), "penalty.noResponse is null, but this task penalises a miss");
        var task = Fixture.Task("task-discrimination-left-right.yml", 4);
        task.Penalty.NoResponse.Sound.LevelDbfs = 0;
        task.Penalty.NoResponse.TimeoutSeconds = double.NaN;
        var said = refusal(task);
        StringAssert.Contains(said, "penalty.noResponse.sound.levelDbfs 0 is above -1 dBFS");
        StringAssert.Contains(said, "penalty.noResponse.timeoutSeconds must be a finite number, 0 or more");
        task = Fixture.Task("task-discrimination-left-right.yml", 4);
        task.Penalty.NoResponse.Sound.LevelDbfs = "off";
        StringAssert.Contains(refusal(task), "penalty.noResponse.sound.levelDbfs: A sound level is a number of dBFS RMS or mute, not off");
    }

    [TestMethod]
    public void TheWeightsAreHonouredAndEachTrialLogsItsCondition()
    {
        // Two visual conditions 3:1 with their own contrasts, and auditory 4, over a seeded run: each trial's trialConditionId
        // is its condition's index and its values that condition's.
        var task = With(
            Fixture.Condition(3, "visualStimulus.contrasts", Fixture.List(0.25)),
            Fixture.Condition(1, "visualStimulus.contrasts", Fixture.List(1.0)),
            Fixture.Condition(4, "visualStimulus.contrasts", Fixture.List(0.0), "target", "auditorySide"));
        var trials = Fixture.Draw(task, 4000);
        var weights = new[] { 3, 1, 4 };
        for (int i = 0; i < weights.Length; i++)
        {
            Assert.AreEqual(weights[i] / 8.0, trials.Count(t => t.trialConditionId == i) / 4000.0, 0.02, "condition " + i);
        }
        Assert.IsTrue(trials.All(t => t.trialConditionId == 0 ? t.visualContrast == 0.25 : t.trialConditionId == 1 ? t.visualContrast == 1 : t.trialType == "auditory"));
    }

    [TestMethod]
    public void AListInAScalarCellIsOnePickedPerTrial()
    {
        var trials = Fixture.Draw(With(Fixture.Condition(1, "reward.sizeUl", Fixture.List(2.0, 4.0))), 400);
        Assert.IsTrue(trials.All(t => t.rewardOfferedUl == 2 || t.rewardOfferedUl == 4));
        Assert.AreEqual(0.5, trials.Count(t => t.rewardOfferedUl == 2) / 400.0, 0.08);
        var built = Fixture.Check(With(Fixture.Condition(1, "reward.sizeUl", Fixture.List(2.0, 4.0), "autoRewardProbability", Fixture.List(0.0, 0.5, 1.0))), Fixture.Speakers());
        Assert.AreEqual(72, built[0].Combinations.Count, "every combination, the task's list fields' entries too");
    }

    [TestMethod]
    public void AzimuthZeroIsRefusedForTheGratingAndTheCentreForTheSound()
    {
        var task = Fixture.Task("task-localization.yml", 4);
        task.VisualStimulus.InitialAzimuthsDeg = new List<double> { -60, 0 };
        StringAssert.Contains(Refused(task), "visualStimulus.initialAzimuthsDeg must not hold 0");
        var refusal = Refused(With(Fixture.Condition(1, "visualStimulus.initialAzimuthsDeg", Fixture.List(60.0, 0.0))));
        StringAssert.Contains(refusal, "trialConditions[0] (audiovisualCoherent) with ");
        StringAssert.Contains(refusal, "visualStimulus.initialAzimuthsDeg must not hold 0");
        Assert.AreEqual("accepted", Refused(With(Fixture.Condition(1, "auditoryStimulus.initialAzimuthsDeg", Fixture.List(0.0)))), "a sound at the centre");
    }

    [TestMethod]
    public void EverySoundAzimuthNeedsItsNearestSpeakerOnItsSide()
    {
        // 30 is nearest the centre speaker: refused for a sound at task level and in a condition; a grating may start there.
        var task = Fixture.Task("task-localization.yml", 4);
        task.AuditoryStimulus.InitialAzimuthsDeg = new List<object> { -60.0, 30.0 };
        StringAssert.Contains(Refused(task), "nearest azimuth 30 (elevation 15) is 'centre', not on the right");
        StringAssert.Contains(Refused(With(Fixture.Condition(1, "auditoryStimulus.initialAzimuthsDeg", Fixture.List(60.0, -30.0)))), "rig.yml's speaker nearest azimuth -30");
        Assert.AreEqual("accepted", Refused(With(Fixture.Condition(1, "visualStimulus.initialAzimuthsDeg", Fixture.List(60.0, 30.0)))));
    }

    [TestMethod]
    public void AllPlaysTheSoundOnEverySpeaker()
    {
        // "all" plays on every speaker (speakerChannel AllSpeakersChannel), logged at azimuth and elevation NaN with its
        // auditoryLevelDbfs above -120, which a silent trial's NaN tells apart; paired with the grating's azimuths it keeps their pick.
        var all = Fixture.Condition(1, "visualStimulus.initialAzimuthsDeg", Fixture.List(-60.0, 60.0), "auditoryStimulus.initialAzimuthsDeg", Fixture.List("all", 60.0));
        all.Pairs = new List<List<string>> { new List<string> { "visualStimulus.initialAzimuthsDeg", "auditoryStimulus.initialAzimuthsDeg" } };
        var trials = Fixture.Draw(With(all), 200);
        foreach (var t in trials)
        {
            Assert.IsTrue(t.auditoryLevelDbfs > -120);
            if (t.visualInitialAzimuthDeg < 0) Assert.IsTrue(t.speakerChannel == SoundAzimuths.AllSpeakersChannel && double.IsNaN(t.auditoryInitialAzimuthDeg) && double.IsNaN(t.auditoryElevationDeg));
            else Assert.IsTrue(t.speakerChannel >= 0 && t.auditoryInitialAzimuthDeg > 0, "the right speaker nearest 60");
        }
        Assert.IsTrue(trials.Any(t => t.visualInitialAzimuthDeg < 0) && trials.Any(t => t.visualInitialAzimuthDeg > 0));
        StringAssert.Contains(Refused(With(Fixture.Condition(1, "auditoryStimulus.initialAzimuthsDeg", Fixture.List("left")))), "value left is neither a number nor all");
        Assert.AreEqual(-2, SoundAzimuths.AllSpeakersChannel);
    }

    [TestMethod]
    public void MoreThanTheCapOfCombinationsIsRefused()
    {
        // Every list field expands too: the task's two sound azimuths double the set's 16 x 32.
        var sizes = Fixture.List(Enumerable.Range(0, 16).Select(i => (object)(1 + i / 16.0)).ToArray());
        var probabilities = Fixture.List(Enumerable.Range(0, 32).Select(i => (object)(i / 32.0)).ToArray());
        Func<TrialCondition> condition = () => Fixture.Condition(1, "visualStimulus.contrasts", Fixture.List(1.0), "visualStimulus.initialAzimuthsDeg", Fixture.List(60.0),
            "auditoryStimulus.initialAzimuthsDeg", Fixture.List(-60.0, 60.0), "reward.sizeUl", sizes, "autoRewardProbability", probabilities);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.AreEqual(1024, Fixture.Check(With(condition()), Fixture.Speakers())[0].Combinations.Count);
        Console.WriteLine("Go's check and build at 1024 combinations: {0} ms", watch.ElapsedMilliseconds);
        probabilities.Add(1.0);
        StringAssert.Contains(Refused(With(condition())), "trialConditions[0] (audiovisualCoherent) expands into 1056 combinations, more than 1024 (x-max-combinations)");
        Assert.AreEqual(1024, TaskSchema.MaxCombinations);
    }

    [TestMethod]
    public void AnyFieldButASessionFieldMayBeSetAndItsValuesAreChecked()
    {
        Func<TrialCondition, string> refusal = c => Refused(With(c));
        Assert.AreEqual("accepted", refusal(Fixture.Condition(1, "trialStructure.itiMinSeconds", 2.0, "visualStimulus.orientationDeg", Fixture.List(0.0, 90.0))));
        StringAssert.Contains(refusal(Fixture.Condition(1, "taskType", "localization")), "sets or pairs taskType, a session field (x-session-fields: taskType)");
        // A set path that holds a session field (a map set whole) is refused as the field itself is.
        StringAssert.Contains(refusal(Fixture.Condition(1, "optogenetics", new Dictionary<object, object> { { "enabled", true } })),
            "sets or pairs optogenetics, a session field (x-session-fields: optogenetics.enabled)");
        Assert.AreEqual("accepted", refusal(Fixture.Condition(1, "penalty.incorrect.sound.levelDbfs", -50.0, "spouts.alwaysIn", true)), "condition-level since P19");
        StringAssert.Contains(refusal(Fixture.Condition(1, "colour.red", 1.0)), "sets or pairs colour.red, which is not a task field");
        StringAssert.Contains(refusal(Fixture.Condition(1, "optogenetics.laserOnProbability", 1.0)), "sets optogenetics.laserOnProbability with optogenetics off");
        StringAssert.Contains(refusal(Fixture.Condition(1, "reward.sizeUl", 99.0)), ": reward.sizeUl must be above 0 and at most");
        StringAssert.Contains(refusal(Fixture.Condition(1, "reward.sizeUl", Fixture.List(2.0, 99.0))), "reward.sizeUl 99, ");
        StringAssert.Contains(refusal(Fixture.Condition(1, "reward.sizeUl", Fixture.List(2.0, 99.0))), ": reward.sizeUl must be");
        StringAssert.Contains(refusal(Fixture.Condition(1, "visualStimulus.contrasts", Fixture.List(-1.0))), "contrasts must each be a finite number, 0 or more");
        Assert.AreEqual("accepted", refusal(Fixture.Condition(1, "visualStimulus.contrasts", Fixture.List(0.0), "target", "auditorySide")), "contrast 0: no grating");
    }

    [TestMethod]
    public void AConditionPinsTheLaser()
    {
        var task = With(Fixture.Condition(1, "optogenetics.laserOnProbability", 1.0), Fixture.Condition(1, "optogenetics.laserOnProbability", 0.0));
        task.Optogenetics.Enabled = true;
        task.Optogenetics.LaserPowerMw = 3;
        task.Optogenetics.TrialsBeforeLaser = 0;
        task.Optogenetics.LaserDurationSeconds = 0.8;
        var trials = Fixture.Draw(task, 200);
        Assert.IsTrue(trials.All(t => t.laserOn == (t.trialConditionId == 0)));
    }

    [TestMethod]
    public void TheSessionFieldsAndTheCapAreTheSchemas()
    {
        var schema = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(Fixture.Repo(), "src", "DataSchemas", "ucl_open_coen_dome_decision_making_task.json")));
        var parameters = schema["$defs"]["UclOpenCoenDomeDecisionMakingTaskParameters"];
        CollectionAssert.AreEqual(TaskSchema.SessionFields, parameters["x-session-fields"].ToObject<string[]>());
        Assert.AreEqual(TaskSchema.MaxCombinations, parameters["x-max-combinations"].ToObject<int>());
    }

    [TestMethod]
    public void TheConditionsSetRoundTrips()
    {
        // Sgen types set as object: read (ReadTask) it is a map of YAML-typed values, written back as YAML or as JSON (Settings_Task) as numbers.
        var text = File.ReadAllText(Path.Combine(Fixture.Repo(), "config", "task-localization.yml")).Replace(
            "      set: { target: visualSide, auditoryStimulus.initialAzimuthsDeg: [0] }",
            "      set: { target: visualSide, visualStimulus.contrasts: [0.5, 1], reward.sizeUl: [2, 3], visualStimulus.initialAzimuthsDeg: [60] }");
        var task = TaskYaml.Read<UclOpenCoenDomeDecisionMakingTaskLogic>(text).TaskParameters;
        var set = (IDictionary<object, object>)task.TrialConditions[0].Set;
        CollectionAssert.AreEqual(new object[] { "target", "visualStimulus.contrasts", "reward.sizeUl", "visualStimulus.initialAzimuthsDeg" }, set.Keys.ToArray());
        var again = TaskYaml.Read<UclOpenCoenDomeDecisionMakingTaskLogic>(Fixture.Text(task)).TaskParameters;
        StringAssert.Contains(Newtonsoft.Json.JsonConvert.SerializeObject(again.TrialConditions[0]),
            "\"set\":{\"target\":\"visualSide\",\"visualStimulus.contrasts\":[0.5,1],\"reward.sizeUl\":[2,3],\"visualStimulus.initialAzimuthsDeg\":[60]}");
        again.Curriculum = Fixture.Curriculum(4, "x");
        var built = Fixture.Check(again, Fixture.Speakers());
        // A list field's entries are one combination each, by path in alphabetical order, the first changing fastest (the
        // task's two sound azimuths, then the rewards, then the contrasts): the draw's chances unchanged.
        CollectionAssert.AreEqual(new[] { 2.0, 2.0, 3.0, 3.0, 2.0, 2.0, 3.0, 3.0 }, built[0].Combinations.Select(v => v.Task.Reward.SizeUl).ToArray());
        CollectionAssert.AreEqual(new[] { 0.5, 0.5, 0.5, 0.5, 1.0, 1.0, 1.0, 1.0 }, built[0].Combinations.Select(v => v.Task.VisualStimulus.Contrasts.Single()).ToArray());
        CollectionAssert.AreEqual(new[] { 60.0 }, built[0].Combinations[1].Task.VisualStimulus.InitialAzimuthsDeg);
        Assert.AreEqual(4, built[1].Combinations.Count, "the task's two lists of 2 azimuths");
        Assert.AreEqual(3.0, built[1].Combinations[0].Task.Reward.SizeUl);
        CollectionAssert.AreEqual(new[] { 0.0 }, built[1].Combinations[0].Task.VisualStimulus.Contrasts);
    }

    [TestMethod]
    public void AValueOfTheWrongTypeIsRefusedNamingTheCondition()
    {
        StringAssert.Contains(Refused(With(Fixture.Condition(1, "reward.sizeUl", "abc"))), "trialConditions[0] (audiovisualCoherent)");
        StringAssert.Contains(Refused(With(Fixture.Condition(1, "autoRewardProbability", Fixture.List("x", 0.5)))), "trialConditions[0] (audiovisualCoherent)");
        StringAssert.Contains(Refused(With(Fixture.Condition(1, "visualStimulus.initialAzimuthsDeg", "left"))), "trialConditions[0] (audiovisualCoherent)");
        StringAssert.Contains(Refused(With(Fixture.Condition(1, "visualStimulus.contrasts", Fixture.List()))), "trialConditions[0] (auditory)");
    }

    [TestMethod]
    public void OneConditionAZeroAmongOthersAndTheWeightCapAreDrawnAsWritten()
    {
        var one = Fixture.Draw(With(Fixture.Condition(1)), 30);
        Assert.IsTrue(one.All(t => t.trialConditionId == 0 && t.trialType == "audiovisualCoherent"));
        // Weight 0 among others: its index is never logged, though its set is still checked at Go.
        var zero = With(Fixture.Condition(0), Fixture.Condition(2), Fixture.Condition(1));
        Assert.IsTrue(Fixture.Draw(zero, 300).All(t => t.trialConditionId != 0));
        StringAssert.Contains(Refused(With(Fixture.Condition(0, "visualStimulus.contrasts", Fixture.List(-1.0)), Fixture.Condition(1))), "trialConditions[0] (auditory)");
        Assert.AreEqual("accepted", Refused(With(Fixture.Condition(TrialConditions.MaxWeight), Fixture.Condition(TrialConditions.MaxWeight))));
        Assert.IsTrue(Fixture.Draw(With(Fixture.Condition(TrialConditions.MaxWeight), Fixture.Condition(TrialConditions.MaxWeight)), 40).Any(t => t.trialConditionId == 1));
    }

    [TestMethod]
    public void ManyConditionsAreEachDrawnAndTheirWeightsSumWithoutOverflow()
    {
        var many = Enumerable.Range(0, 120).Select(i => i % 2 == 0 ? AtAzimuth("visual", 60.0) : AtAzimuth("auditory", 60.0)).ToArray();
        for (int i = 0; i < many.Length; i++) ((IDictionary<object, object>)many[i].Set).Add("reward.sizeUl", 1.0 + i % 3);
        var trials = Fixture.Draw(With(many), 3000);
        Assert.IsTrue(trials.All(t => t.trialType == (t.trialConditionId % 2 == 0 ? "visual" : "auditory") && t.rewardOfferedUl == 1.0 + t.trialConditionId % 3));
        Assert.AreEqual(120, trials.Select(t => t.trialConditionId).Distinct().Count());
        // 2200 conditions of the cap's weight sum past int, which the draw cannot take.
        var heavy = Enumerable.Range(0, 2200).Select(i => Fixture.Condition(TrialConditions.MaxWeight)).ToArray();
        StringAssert.Contains(Refused(With(heavy)), "weights add to more than 2147483647");
    }
}
