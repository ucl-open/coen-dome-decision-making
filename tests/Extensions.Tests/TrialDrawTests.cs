using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using OpenRigs.Devices;
using OpenRigs.Devices.Rigs;
using OpenRigs.Trials;
using UclOpenCoenDomeDecisionMakingTask;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

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
        public double Auto, Both;
        public double[] Contrasts;
        public bool Repeats, Moves = true;
    }

    const string Coherent = "{ visual: 0, auditory: 0, audiovisualCoherent: 10, audiovisualConflict: 0, neutral: 0 }";
    static readonly Dictionary<string, Stage[]> Stages = new Dictionary<string, Stage[]>
    {
        { "task-localization.yml", new[] {
            new Stage { Label = "Habituation", Weights = Coherent, Auto = 0, Both = 0.5, Contrasts = new[] { 3.0 }, Repeats = false },
            new Stage { Label = "Coherent Only", Weights = Coherent, Auto = 0, Both = 1, Contrasts = new[] { 3.0 }, Repeats = true },
            new Stage { Label = "Auditory Added", Weights = "{ visual: 0, auditory: 10, audiovisualCoherent: 10, audiovisualConflict: 0, neutral: 0 }", Auto = 0, Both = 1, Contrasts = new[] { 0.25, 0.5, 1.0 }, Repeats = true },
            new Stage { Label = "Visual Added", Weights = "{ visual: 10, auditory: 10, audiovisualCoherent: 10, audiovisualConflict: 0, neutral: 0 }", Auto = 0, Both = 1, Contrasts = new[] { 0.25, 0.5, 1.0 }, Repeats = true },
            new Stage { Label = "Conflict Added", Auto = 0, Both = 1, Contrasts = new[] { 0.25, 0.5, 1.0 }, Repeats = true } } },
        { "task-discrimination-go-no-go.yml", Discrimination("go", "noGo", 0) },
        { "task-discrimination-left-right.yml", Discrimination("left", "right", 1) },
    };

    static Stage[] Discrimination(string first, string second, double both)
    {
        Func<int, int, string> weights = (a, b) => string.Format("{{ {0}: 10, {1}: {2}, audiovisualCoherent: {3}, audiovisualConflict: 0, neutral: 0 }}", first, second, a, b);
        return new[] {
            new Stage { Label = "Habituation", Weights = weights(0, 0), Auto = 1, Both = both, Contrasts = new[] { 1.0 }, Moves = false },
            new Stage { Label = "Only", Weights = weights(0, 0), Auto = 1, Both = both, Contrasts = new[] { 1.0 } },
            new Stage { Label = "Added", Weights = weights(10, 10), Auto = 0, Both = both, Contrasts = new[] { 0.5, 1.0 } },
            new Stage { Label = "Probes Added", Auto = 0, Both = both, Contrasts = new[] { 0.5, 1.0 } },
            new Stage { Label = "Wide Contrasts", Auto = 0, Both = both, Contrasts = new[] { 0.5, 1.0, 2.0, 3.0 } } };
    }

    // The task file as app-rig-control writes it at launch for a curriculum stage: the stage's values over the file's, and
    // curriculum filled.
    public static UclOpenCoenDomeDecisionMakingTaskParameters Task(string file, int stage)
    {
        var text = File.ReadAllText(Path.Combine(Repo(), "config", file));
        var task = new DeserializerBuilder().Build().Deserialize<UclOpenCoenDomeDecisionMakingTaskLogic>(text).TaskParameters;
        Assert.IsNull(task.Curriculum, "the repo's task files leave curriculum empty");
        var s = Stages[file][stage];
        if (s.Weights != null) task.TrialConditions = Conditions(task.TrialConditions, s.Weights);
        task.AutoRewardProbability = s.Auto;
        task.BothSpoutsProbability = s.Both;
        task.Contrasts = s.Contrasts.ToList();
        task.Repeats.RepeatOn = s.Repeats ? RepeatOn.NotCorrect : RepeatOn.Never;
        task.SpoutMovements = s.Moves;
        task.Curriculum = Curriculum(stage, s.Label);
        task.RngSeed = 1;
        return task;
    }

    /// <summary>A well formed curriculum for a stage (id, code S0nA, a fixed version, path A).</summary>
    public static Curriculum Curriculum(int id, string label)
    {
        return new Curriculum { Id = id, Code = "S0" + id + "A", Version = "k7f2", Path = "A", Label = label, Sha256 = "test" };
    }

    // A stage's trial mix: the file's conditions weighted by label.
    static List<TrialCondition> Conditions(List<TrialCondition> conditions, string weights)
    {
        var byLabel = new DeserializerBuilder().Build().Deserialize<Dictionary<string, int>>(weights);
        foreach (var c in conditions) c.Weight = byLabel[TrialDraw.Name(c.TrialType)];
        return conditions;
    }

    /// <summary>The trialType of that name.</summary>
    public static TrialType TrialType(string name)
    {
        return Enum.GetValues(typeof(TrialType)).Cast<TrialType>().Single(l => TrialDraw.Name(l) == name);
    }

    /// <summary>A condition of a label and weight with these condition-level values.</summary>
    public static TrialCondition Condition(string label, int weight, params object[] pathsAndValues)
    {
        var set = new Dictionary<object, object>();
        for (int i = 0; i < pathsAndValues.Length; i += 2) set.Add(pathsAndValues[i], pathsAndValues[i + 1]);
        return new TrialCondition { TrialType = TrialType(label), Weight = weight, Set = set, Pairs = new List<List<string>>() };
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
        task.TrialConditions.Single(c => TrialDraw.Name(c.TrialType) == label).Weight = weight;
    }

    // Every condition not of these labels weighted 0.
    public static void Only(UclOpenCoenDomeDecisionMakingTaskParameters task, params string[] labels)
    {
        foreach (var c in task.TrialConditions.Where(c => !labels.Contains(TrialDraw.Name(c.TrialType)))) c.Weight = 0;
    }

    /// <summary>The task file's text as rig-control writes it, from the task (the draw and the Go check read the text).</summary>
    public static string Text(UclOpenCoenDomeDecisionMakingTaskParameters task)
    {
        return new SerializerBuilder().Build().Serialize(new UclOpenCoenDomeDecisionMakingTaskLogic { TaskParameters = task });
    }

    /// <summary>TrialDraw.CheckTask on the task's text: its conditions as built, or the refusal thrown.</summary>
    public static List<TrialConditionDraw> Check(UclOpenCoenDomeDecisionMakingTaskParameters task, EqualisedSpeakerArray speakers)
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

    // Trials drawn one after another, each last one ending as outcome says (Correct by default).
    public static List<Trial> Draw(UclOpenCoenDomeDecisionMakingTaskParameters task, int count, Func<Trial, string> outcome = null)
    {
        var draw = new TrialDraw(Text(task), Speakers(), path => 2);
        var trials = new List<Trial>();
        string last = null;
        for (int i = 0; i < count; i++)
        {
            var trial = draw.Next(last);
            trials.Add(trial);
            last = outcome == null ? "Correct" : outcome(trial);
        }
        return trials;
    }

    public static string Refusal(Action action)
    {
        try { action(); }
        catch (InvalidOperationException e) { return e.Message; }
        return "accepted";
    }

    // Each old trial type's draw (TrialDraw.New before the trial-type split, 18 Nov 2026) on the test speakers (left -90,
    // right 90, centre 0), by task file: grating on, its azimuth, the speaker's azimuth (NaN silent), the target, both
    // spouts (Signature). The converted files draw exactly these per trialType, and every one of them.
    public static readonly Dictionary<string, Dictionary<string, string[]>> OldDraws = new Dictionary<string, Dictionary<string, string[]>>
    {
        { "task-localization.yml", new Dictionary<string, string[]> {
            { "visual", new[] { "on -60 0 Left", "on 60 0 Right" } },
            { "auditory", new[] { "off NaN -90 Left", "off NaN 90 Right" } },
            { "audiovisualCoherent", new[] { "on -60 -90 Left", "on 60 90 Right" } },
            { "audiovisualConflict", new[] { "on -60 90 Random both", "on 60 -90 Random both" } },
            { "neutral", new[] { "off NaN 0 Random both" } } } },
        { "task-discrimination-go-no-go.yml", new Dictionary<string, string[]> {
            { "go", new[] { "on -60 NaN Go", "on 60 NaN Go" } },
            { "noGo", new[] { "off NaN -90 NoGo", "off NaN 90 NoGo" } },
            { "audiovisualCoherent", new[] { "on -60 -90 NoReward", "on 60 90 NoReward" } },
            { "audiovisualConflict", new[] { "on -60 90 NoReward", "on 60 -90 NoReward" } },
            { "neutral", new[] { "off NaN NaN NoReward" } } } },
        { "task-discrimination-left-right.yml", new Dictionary<string, string[]> {
            { "left", new[] { "on -60 NaN Left", "on 60 NaN Left" } },
            { "right", new[] { "off NaN -90 Right", "off NaN 90 Right" } },
            { "audiovisualCoherent", new[] { "on -60 -90 Random both", "on 60 90 Random both" } },
            { "audiovisualConflict", new[] { "on -60 90 Random both", "on 60 -90 Random both" } },
            { "neutral", new[] { "off NaN NaN Random both" } } } },
    };

    /// <summary>A drawn trial as OldDraws writes it.</summary>
    public static string Signature(Trial t)
    {
        return Signature(t.visualContrast, t.visualAzimuthDeg, t.auditoryAzimuthDeg, t.target, t.bothSpoutsOffered);
    }

    /// <summary>A logged row as OldDraws writes it.</summary>
    public static string Signature(TrialRow r)
    {
        return Signature(r.visualContrast, r.visualAzimuthDeg, r.auditoryAzimuthDeg, r.target, r.bothSpoutsOffered);
    }

    static string Signature(double contrast, double visualAzimuth, double auditoryAzimuth, string target, bool both)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return string.Format(inv, "{0} {1} {2} {3}{4}", contrast > 0 ? "on" : "off", visualAzimuth,
            double.IsNaN(auditoryAzimuth) ? "NaN" : auditoryAzimuth.ToString(inv), target, target == Target.Random ? (both ? " both" : " one") : "");
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
            Assert.IsTrue(t.spoutMovements && t.auditoryAmplitude == 1, t.trialType);
            if (t.trialType == "visual" || t.trialType == "audiovisualCoherent")
                Assert.AreEqual(t.visualAzimuthDeg > 0 ? "Right" : "Left", t.target, t.trialType);
            if (t.trialType == "auditory" || t.trialType == "audiovisualCoherent")
                Assert.AreEqual(t.auditoryAzimuthDeg > 0 ? "Right" : "Left", t.target, t.trialType);
            if (t.trialType == "audiovisualConflict")
                Assert.AreEqual(t.visualAzimuthDeg > 0, t.auditoryAzimuthDeg < 0);
            if (t.trialType == "visual" || t.trialType == "neutral")
                Assert.AreEqual(2, t.speakerChannel, "the centre speaker");
        }
        CollectionAssert.AreEquivalent(new[] { "visual", "auditory", "audiovisualCoherent", "audiovisualConflict", "neutral" }, trials.Select(t => t.trialType).Distinct().ToArray());
    }

    [TestMethod]
    public void TheConvertedFilesDrawWhatEachOldTrialTypeDrew()
    {
        foreach (var file in Fixture.OldDraws)
        {
            var task = Fixture.Task(file.Key, 4);
            task.BothSpoutsProbability = file.Key.Contains("go-no-go") ? 0 : 0.5;   // both spouts only where the target forces them
            var drawn = Fixture.Draw(task, 3000).GroupBy(t => t.trialType).ToDictionary(g => g.Key, g => g.Select(Fixture.Signature).Distinct().OrderBy(s => s).ToArray());
            CollectionAssert.AreEquivalent(file.Value.Keys.ToList(), drawn.Keys.ToList(), file.Key);
            foreach (var type in file.Value) CollectionAssert.AreEqual(type.Value.OrderBy(s => s).ToArray(), drawn[type.Key], file.Key + " " + type.Key);
        }
    }

    [TestMethod]
    public void LocalizationConflictKeepsItsStimuliAndIsAProbe()
    {
        // A random-reward probe: the grating and sound on opposite sides, no side to answer, its coin
        // heads about half the time (ATrialWithNoRightAnswerRewardsOrPenalisesEitherChoiceOnACoin scores it).
        var task = Fixture.Task("task-localization.yml", 4);
        Fixture.Only(task, "audiovisualConflict");
        var trials = Fixture.Draw(task, 600);
        Assert.IsTrue(trials.All(t => t.trialType == "audiovisualConflict" && (t.visualAzimuthDeg > 0) == (t.auditoryAzimuthDeg < 0) && t.target == Target.Random));
        Assert.AreEqual(0.5, trials.Count(t => t.probeHeads) / 600.0, 0.07);
    }

    [TestMethod]
    public void EachSoundPlaysOnTheNearestSpeaker()
    {
        foreach (var t in Fixture.Draw(Fixture.Task("task-localization.yml", 4), 300))
        {
            var speaker = Fixture.Speakers().Speakers.Values.Single(s => s.Channel == t.speakerChannel);
            Assert.AreEqual(speaker.Position.Azimuth, t.auditoryAzimuthDeg);
            Assert.AreEqual(speaker.Position.Elevation, t.auditoryElevationDeg);
        }
        var nearest = TrialDraw.NearestSpeaker(Fixture.Speakers(), -60, 15);
        Assert.AreEqual("left", nearest.Key);
        Assert.AreEqual("centre", TrialDraw.NearestSpeaker(Fixture.Speakers(), 30, 15).Key);
    }

    [TestMethod]
    public void DiscriminationAnswersTheModality()
    {
        foreach (var t in Fixture.Draw(Fixture.Task("task-discrimination-left-right.yml", 3), 500))
        {
            if (t.trialType == "left") Assert.IsTrue(t.auditoryAmplitude == 0 && t.visualContrast > 0 && t.target == "Left");
            if (t.trialType == "right") Assert.IsTrue(t.auditoryAmplitude > 0 && t.visualContrast == 0 && t.target == "Right");
            if (t.trialType == "neutral") Assert.IsTrue(t.auditoryAmplitude == 0 && double.IsNaN(t.auditoryAzimuthDeg) && t.speakerChannel == -1);
        }
    }

    [TestMethod]
    public void DiscriminationHabituationAutoRewardsAtTheLeftSpoutWithTheSpoutsIn()
    {
        Assert.IsTrue(Fixture.Draw(Fixture.Task("task-discrimination-left-right.yml", 0), 50).All(t => t.autoReward && t.target == "Left" && !t.spoutMovements && t.trialType == "left"));
        var task = Fixture.Task("task-discrimination-left-right.yml", 2);
        task.AutoRewardProbability = 1;
        var stage2 = Fixture.Draw(task, 300);
        Assert.IsTrue(stage2.Where(t => t.target != Target.Random).All(t => t.autoReward), "left-right: either spout's answer may be auto-rewarded");
        Assert.IsTrue(stage2.Where(t => t.target == Target.Random).All(t => !t.autoReward), "a probe never is");
    }

    [TestMethod]
    public void GoNoGoAnswersTheRoleAtTheLeftSpout()
    {
        var task = Fixture.Task("task-discrimination-go-no-go.yml", 3);
        task.AutoRewardProbability = 1;
        foreach (var t in Fixture.Draw(task, 600))
        {
            var expected = t.trialType == "go" ? Target.Go : t.trialType == "noGo" ? Target.NoGo : Target.NoReward;
            Assert.AreEqual(expected, t.target, t.trialType);
            Assert.AreEqual(expected == Target.Go, t.autoReward, "auto-reward only on Go");
            Assert.AreEqual(expected == Target.Go, t.rewardOfferedUl > 0, "a reward on offer only on Go");
            Assert.IsFalse(t.bothSpoutsOffered);
            Assert.AreEqual(LickSpoutCommand.LeftOnly, SpoutsInCommand.Command(t));
        }
    }

    [TestMethod]
    public void AnAuditoryGoMouseIsItsConditionsSettings()
    {
        // goModality is gone: the go condition shows the sound alone, the noGo condition the grating alone.
        var task = Fixture.Task("task-discrimination-go-no-go.yml", 2);
        Fixture.Weight(task, "audiovisualCoherent", 0);
        task.TrialConditions[0].Set = new Dictionary<object, object> { { "target", "go" }, { "contrasts", Fixture.List(0.0) } };
        task.TrialConditions[1].Set = new Dictionary<object, object> { { "target", "noGo" }, { "auditoryStimulus.amplitude", 0.0 } };
        var trials = Fixture.Draw(task, 400);
        Assert.IsTrue(trials.All(t => t.trialType == "go" ? t.target == Target.Go && t.auditoryAmplitude > 0 && t.visualContrast == 0 : t.target == Target.NoGo && t.auditoryAmplitude == 0 && t.visualContrast > 0));
        Assert.AreEqual(0.5, trials.Count(t => t.trialType == "go") / 400.0, 0.08, "go 10, noGo 10");
    }

    [TestMethod]
    public void GoNoGoRefusesBothSpouts()
    {
        var task = Fixture.Task("task-discrimination-go-no-go.yml", 2);
        task.BothSpoutsProbability = 0.5;
        StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers())), "bothSpoutsProbability must be 0 in discriminationGoNoGo");
    }

    [TestMethod]
    public void ATaskWithoutSpoutMovementsNeedsBothSpouts()
    {
        // The SpoutsIn cue's LeftOnly or RightOnly draws the other spout back, never returned without spout movements.
        var task = Fixture.Task("task-localization.yml", 0);
        task.SpoutMovements = false;
        task.BothSpoutsProbability = 0.5;
        StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers())), "bothSpoutsProbability must be 1 with spoutMovements false");
        Fixture.Check(Fixture.Task("task-discrimination-go-no-go.yml", 0), Fixture.Speakers());   // go/no-go's one spout: allowed
    }

    [TestMethod]
    public void TheWeightsAreByTrialType()
    {
        var task = Fixture.Task("task-localization.yml", 0);
        Assert.AreEqual(0, task.TrialConditions.Where(c => c.TrialType != TrialType.AudiovisualCoherent).Sum(c => c.Weight));
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
        var trials = Fixture.Draw(task, 400, t => "Incorrect");
        for (int i = 1; i < trials.Count; i++)
        {
            var before = trials[i - 1];
            bool repeats = before.repeatCount < task.Repeats.MaxRepeats &&
                (before.trialType == "visual" || before.trialType == "auditory" || before.trialType == "audiovisualCoherent");
            Assert.AreEqual(repeats ? before.repeatCount + 1 : 0, trials[i].repeatCount, before.trialType);
            if (repeats) Assert.IsTrue(before.visualAzimuthDeg.Equals(trials[i].visualAzimuthDeg) && before.trialCondition == trials[i].trialCondition);
        }
        Assert.IsTrue(trials.Any(t => t.repeatCount == task.Repeats.MaxRepeats));
        // Only a trial with a right answer repeats: never a conflict or neutral one (repeatOn never, x-rules).
        var noResponse = Fixture.Draw(task, 400, t => "NoResponse");
        Assert.IsTrue(noResponse.Any(t => t.trialType == "neutral") && noResponse.Any(t => t.trialType == "audiovisualConflict"));
        Assert.IsFalse(noResponse.Any(t => t.repeatCount > 0 && (t.trialType == "neutral" || t.trialType == "audiovisualConflict")));
        var leftRight = Fixture.Task("task-discrimination-left-right.yml", 3);
        leftRight.Repeats.RepeatOn = RepeatOn.NotCorrect;
        var lr = Fixture.Draw(leftRight, 400, t => "Incorrect");
        Assert.IsTrue(lr.Any(t => t.repeatCount > 0 && (t.trialType == "left" || t.trialType == "right")));
        Assert.IsFalse(lr.Any(t => t.repeatCount > 0 && (t.trialType == "audiovisualCoherent" || t.trialType == "audiovisualConflict" || t.trialType == "neutral")));
        // A condition's own rule: the left condition never repeats, the right does.
        ((IDictionary<object, object>)leftRight.TrialConditions[0].Set)["repeats.repeatOn"] = "never";
        lr = Fixture.Draw(leftRight, 400, t => "Incorrect");
        Assert.IsTrue(lr.Any(t => t.repeatCount > 0 && t.trialType == "right") && !lr.Any(t => t.repeatCount > 0 && t.trialType == "left"));
    }

    [TestMethod]
    public void NoResponseRepeatsAfterANoResponseAlone()
    {
        // noResponse names its outcome: a NoResponse repeats the trial, an Incorrect never does.
        var task = Fixture.Task("task-localization.yml", 4);
        task.RngSeed = 7;
        task.Repeats.RepeatOn = RepeatOn.NoResponse;
        Assert.IsTrue(Fixture.Draw(task, 100, t => "Incorrect").All(t => t.repeatCount == 0));
        Assert.IsTrue(Fixture.Draw(task, 100, t => "NoResponse").Any(t => t.repeatCount > 0));
    }

    [TestMethod]
    public void EachTrialDrawsItsOwnInterval()
    {
        var task = Fixture.Task("task-discrimination-left-right.yml", 3);
        var trials = Fixture.Draw(task, 300, t => t.trialNumber % 3 == 0 ? "Incorrect" : "Correct");
        for (int i = 0; i < trials.Count; i++) Assert.AreEqual(i + 1, trials[i].trialNumber);
        // What Gerion's mice got: 1.5 to 2.0 s from the outcome (after any timeout) to the stimulus.
        Assert.IsTrue(trials.All(t => t.itiSeconds >= 1.5 && t.itiSeconds <= 2.0));
        Assert.IsTrue(trials.Max(t => t.itiSeconds) > 1.95 && trials.Min(t => t.itiSeconds) < 1.55);
        var repeats = Fixture.Draw(Fixture.Task("task-localization.yml", 4), 300, t => "Incorrect").Where(t => t.repeatCount > 0).ToList();
        Assert.IsTrue(repeats.Count > 10 && repeats.Select(t => t.itiSeconds).Distinct().Count() == repeats.Count, "a repeat draws its own");
    }

    [TestMethod]
    public void TheDiscriminationFileIsWhatGerionsMiceGot()
    {
        var s = Fixture.Task("task-discrimination-left-right.yml", 0);
        Assert.AreEqual(1.5, s.TrialStructure.ItiMinSeconds);
        Assert.AreEqual(2.0, s.TrialStructure.ItiMaxSeconds);
        Assert.AreEqual(1.5, s.TrialStructure.DrinkSeconds);
        Assert.AreEqual(4.0, s.Penalty.TimeoutSeconds);   // incorrect or a miss: 5.5 to 6.0 s outcome to stimulus
    }

    [TestMethod]
    public void OptogeneticsSendsAConditionOnEveryTrial()
    {
        var task = Fixture.Task("task-localization.yml", 4);
        task.Optogenetics.Enabled = true;
        task.Optogenetics.TrialsBeforeLaser = 20;
        task.Optogenetics.LaserOnProbability = 0.5;
        var trials = Fixture.Draw(task, 300, t => "NoResponse");
        Assert.IsTrue(trials.All(t => t.laserCondition == 1 || t.laserCondition == 2));
        Assert.IsTrue(trials.Take(20).All(t => !t.laserOn));
        Assert.IsTrue(trials.All(t => t.laserPowerMw == (t.laserOn ? 3.0 : 0)));
        Assert.IsTrue(trials.Any(t => t.laserOn) && trials.Skip(20).Any(t => !t.laserOn));
        Assert.IsTrue(trials.Where(t => t.repeatCount > 0).All(t => !t.laserOn), "noLaserOnRepeats");
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
            Fixture.Refusal(() => new TrialDraw(Fixture.Text(parameters), speakers, path => 2));

        var task = Fixture.Task("task-localization.yml", 4);
        task.Contrasts.Clear();
        StringAssert.Contains(refusal(task), "contrasts must each be a finite number, 0 or more, at least one");

        task = Fixture.Task("task-localization.yml", 4);
        task.AuditoryStimulus.Amplitude = -1;
        StringAssert.Contains(refusal(task), "auditoryStimulus.amplitude must be a finite number, 0 or more");

        task = Fixture.Task("task-localization.yml", 4);
        task.VisualStimulus.InitialAzimuthsDeg = new List<double> { -60, 200 };
        StringAssert.Contains(refusal(task), "initialAzimuthsDeg value 200 is outside [-180, 180]");

        task = Fixture.Task("task-localization.yml", 4);
        task.TrialStructure.StimulusSeconds = 2;
        StringAssert.Contains(refusal(task), "stimulusSeconds");

        task = Fixture.Task("task-localization.yml", 4);
        task.TrialStructure.ResponseWindowSeconds = 5;
        task.TrialStructure.StimulusSeconds = task.TrialStructure.ItiMinSeconds + 0.5;
        StringAssert.Contains(refusal(task), "longer than itiMinSeconds");

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
        StringAssert.Contains(Fixture.Refusal(() => new TrialDraw(Fixture.Text(task), Fixture.Speakers(), path => 2)), "laserPowerMw");
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
        Assert.IsTrue(Fixture.Draw(Fixture.Task("task-discrimination-left-right.yml", 0), 50).All(t => t.trialType == "left" && t.target == "Left"), "left-right stage 0: left only");
        Assert.IsTrue(Fixture.Draw(Fixture.Task("task-discrimination-go-no-go.yml", 1), 50).All(t => t.trialType == "go" && t.target == Target.Go), "go/no-go stage 1: go only");
        var goNoGo = Fixture.Task("task-discrimination-go-no-go.yml", 2);
        Fixture.Weight(goNoGo, "audiovisualCoherent", 0);
        var stage2 = Fixture.Draw(goNoGo, 400);
        Assert.IsTrue(stage2.All(t => t.trialType == "go" ? t.target == Target.Go && t.visualContrast > 0 && t.auditoryAmplitude == 0 : t.trialType == "noGo" && t.target == Target.NoGo && t.auditoryAmplitude > 0));
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
        Assert.IsTrue(Fixture.Draw(goNoGo, 50).All(t => t.trialType == "go" && t.target == Target.Go));
    }

    [TestMethod]
    public void ALaserThatWouldDelayTheNextArmIsRefused()
    {
        // Discrimination: 0 open loop + 1.5 s interval - 0.5 s ramp-down - 0.2 s reply margin = 0.8 s.
        var task = Fixture.Task("task-discrimination-left-right.yml", 4);
        task.Optogenetics.Enabled = true;
        task.Optogenetics.LaserPowerMw = 3;
        Assert.AreEqual(0.8, TrialDraw.LongestLaserSeconds(task), 1e-9);
        StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers())), "laserDurationSeconds (2.5 s) must be at most 0.8 s");
        task.Optogenetics.LaserDurationSeconds = 0.8;
        Assert.AreEqual("accepted", Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers())));
        task.Optogenetics.LaserDurationSeconds = 2.5;
        task.Optogenetics.Enabled = false;
        Assert.AreEqual("accepted", Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers())));
    }

    [TestMethod]
    public void ALargeRewardOverTheCapIsRefusedAtGo()
    {
        // A RewardValve piece refuses a reward over its MaxRewardUl (lib's MAX_REWARD_UL) mid-session; Go refuses it first.
        var task = Fixture.Task("task-localization.yml", 4);
        task.Reward.LargeRewardProbability = 0.2;
        task.Reward.LargeRewardMultiplier = 3;
        task.Reward.RewardSizeUl = 8;
        var refusal = Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers()));
        StringAssert.Contains(refusal, "reward.rewardSizeUl (8 ul) x reward.largeRewardMultiplier (3) is 24 ul, more than 20 ul");
        task.Reward.RewardSizeUl = OpenRigs.Core.LoadRewardLine.DefaultMaxRewardUl / 3;
        Assert.AreEqual("accepted", Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers())));
        task.Reward.RewardSizeUl = 8;
        task.Reward.LargeRewardProbability = 0;
        Assert.AreEqual("accepted", Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers())));
    }

    [TestMethod]
    public void TheSignedLeadIsRefusedOutsideTheIntervalAndTheResponseWindow()
    {
        Func<double, string> refusal = lead =>
        {
            var task = Fixture.Task("task-discrimination-left-right.yml", 4);   // itiMinSeconds 1.5, response window 0 + 1.5 s
            task.Optogenetics.LaserToStimulusSeconds = lead;
            return Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers()));
        };
        foreach (var ok in new[] { 1.3, 0.5, 0.0, -0.5, -1.5 }) Assert.AreEqual("accepted", refusal(ok), ok.ToString());
        StringAssert.Contains(refusal(1.31), "reply margin must be at most trialStructure.itiMinSeconds");
        StringAssert.Contains(refusal(-1.51), "after the response window");
    }

    [TestMethod]
    public void TheQuietWaitsAreRefusedNegativeOrHalfSet()
    {
        var task = Fixture.Task("task-localization.yml", 4);
        task.TrialStructure.WheelQuietSeconds = 1;
        StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers())), "are both above 0 (the wheel's quiet wait) or both 0 (off)");
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
        task.Penalty.TimeoutSeconds = double.NaN;
        task.Optogenetics.LaserToStimulusSeconds = double.NaN;
        var refusal = Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers()));
        StringAssert.Contains(refusal, "penalty.timeoutSeconds must be a finite number, 0 or more");
        StringAssert.Contains(refusal, "laserToStimulusSeconds must be a finite number");
        task = Fixture.Task("task-localization.yml", 4);
        task.TrialStructure.DrinkSeconds = 3.5;
        StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers())), "below drinkSeconds");
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
            { "trialConditions[0] (visual): its set is not a map", s => first(s).Children[new YamlScalarNode("set")] = new YamlScalarNode("5") },
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
    // Localization stage 4 with these conditions.
    static UclOpenCoenDomeDecisionMakingTaskParameters With(params TrialCondition[] conditions)
    {
        var task = Fixture.Task("task-localization.yml", 4);
        task.TrialConditions = conditions.ToList();
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
        if (label == "visual") return Fixture.Condition(label, 1, "target", "visualSide", "visualStimulus.initialAzimuthsDeg", position, "auditoryStimulus.initialAzimuthsDeg", Fixture.List(0.0));
        if (label == "auditory") return Fixture.Condition(label, 1, "target", "auditorySide", "contrasts", Fixture.List(0.0), "auditoryStimulus.initialAzimuthsDeg", position);
        return Fixture.Paired(Fixture.Condition(label, 1, "target", "visualSide", "visualStimulus.initialAzimuthsDeg", position, "auditoryStimulus.initialAzimuthsDeg", position));
    }

    [TestMethod]
    public void TheAzimuthsSignGivesTheSideAndTheTarget()
    {
        foreach (var azimuth in new[] { 60.0, -75.0 })
        {
            foreach (var type in new[] { "visual", "auditory", "audiovisualCoherent" })
            {
                var trials = Fixture.Draw(With(AtAzimuth(type, azimuth)), 40);
                var side = azimuth > 0 ? "Right" : "Left";
                Assert.IsTrue(trials.All(t => t.target == side && t.trialType == type), type);
                if (type != "auditory") Assert.IsTrue(trials.All(t => t.visualAzimuthDeg == azimuth && t.visualElevationDeg == 15), "the grating starts there exactly");
                if (type != "visual") Assert.IsTrue(trials.All(t => t.auditoryAzimuthDeg == (azimuth > 0 ? 90 : -90)), "the nearest speaker");
                if (type == "visual") Assert.IsTrue(trials.All(t => t.auditoryAzimuthDeg == 0), "the centre sound");
            }
        }
        // Several listed: one picked per trial, both sides drawn.
        var mixed = Fixture.Draw(With(Fixture.Condition("visual", 1, "visualStimulus.initialAzimuthsDeg", Fixture.List(-60.0, 60.0))), 200);
        Assert.AreEqual(0.5, mixed.Count(t => t.target == "Right") / 200.0, 0.1);
        Assert.IsTrue(mixed.All(t => (t.visualAzimuthDeg > 0) == (t.target == "Right")));
    }

    [TestMethod]
    public void ANoGratingTrialLogsItsAzimuthAndElevationAsNaNAndRendersAtZero()
    {
        var none = Fixture.Draw(With(Fixture.Condition("auditory", 1, "contrasts", Fixture.List(0.0))), 20);
        Assert.IsTrue(none.All(t => t.visualContrast == 0 && double.IsNaN(t.visualAzimuthDeg) && double.IsNaN(t.visualElevationDeg)), "logged NaN, contrast 0");
        Assert.IsTrue(none.All(t => t.gratingAzimuthDeg == 0 && t.gratingElevationDeg == 0), "the renderer never gets NaN");
        var shown = Fixture.Draw(With(Fixture.Condition("visual", 1, "contrasts", Fixture.List(1.0))), 20);
        Assert.IsTrue(shown.All(t => t.gratingAzimuthDeg == t.visualAzimuthDeg && t.gratingElevationDeg == t.visualElevationDeg && !double.IsNaN(t.visualAzimuthDeg)));
    }

    [TestMethod]
    public void PairedListsShareOnePickAndUnpairedOnesDrawIndependently()
    {
        var both = Fixture.List(-60.0, 60.0);
        var paired = Fixture.Draw(With(Fixture.Paired(Fixture.Condition("audiovisualConflict", 1, "target", "random", "bothSpoutsProbability", 1.0, "repeats.repeatOn", "never",
            "visualStimulus.initialAzimuthsDeg", both, "auditoryStimulus.initialAzimuthsDeg", Fixture.List(60.0, -60.0)))), 200);
        Assert.IsTrue(paired.All(t => (t.visualAzimuthDeg > 0) == (t.auditoryAzimuthDeg < 0) && t.target == Target.Random && t.bothSpoutsOffered));
        Assert.AreEqual(0.5, paired.Count(t => t.visualAzimuthDeg > 0) / 200.0, 0.1);
        var unpaired = Fixture.Draw(With(Fixture.Condition("audiovisualCoherent", 1, "visualStimulus.initialAzimuthsDeg", both, "auditoryStimulus.initialAzimuthsDeg", both)), 400);
        Assert.AreEqual(0.5, unpaired.Count(t => (t.visualAzimuthDeg > 0) == (t.auditoryAzimuthDeg > 0)) / 400.0, 0.08, "independent");
        // Contrast paired with amplitude: the brighter grating with the louder sound.
        var levels = Fixture.Condition("audiovisualCoherent", 1, "contrasts", Fixture.List(0.25, 1.0), "auditoryStimulus.amplitude", Fixture.List(0.5, 1.0));
        levels.Pairs = new List<List<string>> { new List<string> { "contrasts", "auditoryStimulus.amplitude" } };
        var drawn = Fixture.Draw(With(levels), 200);
        Assert.IsTrue(drawn.All(t => t.visualContrast == 0.25 ? t.auditoryAmplitude == 0.5 : t.auditoryAmplitude == 1.0));
        Assert.IsTrue(drawn.Any(t => t.auditoryAmplitude == 0.5) && drawn.Any(t => t.auditoryAmplitude == 1.0));
        Assert.AreEqual(8, Fixture.Check(With(levels), Fixture.Speakers())[0].Combinations.Count, "the pair's 2 by the task's two lists of 2 azimuths");
        levels.Set = new Dictionary<object, object> { { "contrasts", Fixture.List(0.25, 0.5, 1.0) }, { "auditoryStimulus.amplitude", Fixture.List(0.5, 1.0) } };
        StringAssert.Contains(Refused(With(levels)), "trialConditions[0] (audiovisualCoherent): pairs [contrasts, auditoryStimulus.amplitude] have lists of lengths 3, 2");
        levels.Pairs = new List<List<string>> { new List<string> { "contrasts" } };
        StringAssert.Contains(Refused(With(levels)), "each pairs group needs two paths or more");
        levels.Pairs = new List<List<string>> { new List<string> { "contrasts", "rngSeed" } };
        StringAssert.Contains(Refused(With(levels)), "sets or pairs rngSeed, a session field (x-session-fields: rngSeed)");
    }

    [TestMethod]
    public void TheLoudestSoundIsTheLargestAmplitudeAWeightedConditionDraws()
    {
        var rules = new TrialRules(Fixture.Text(With(Fixture.Condition("auditory", 1, "auditoryStimulus.amplitude", Fixture.List(0.5, 2.0)), Fixture.Condition("visual", 0, "auditoryStimulus.amplitude", 3.0))),
            Fixture.Speakers(), false, path => 2);
        Assert.AreEqual(2.0, rules.Loudest.Amplitude);
        StringAssert.StartsWith(rules.Loudest.Where, "trialConditions[0] (auditory) with auditoryStimulus.amplitude 2, ");
    }

    [TestMethod]
    public void TheWeightsAreHonouredAndEachTrialLogsItsCondition()
    {
        // Two visual conditions 3:1 with their own contrasts, and auditory 4, over a seeded run: each trial's trialCondition
        // is its condition's index and its values that condition's.
        var task = With(
            Fixture.Condition("visual", 3, "contrasts", Fixture.List(0.25)),
            Fixture.Condition("visual", 1, "contrasts", Fixture.List(1.0)),
            Fixture.Condition("auditory", 4));
        var trials = Fixture.Draw(task, 4000);
        var weights = new[] { 3, 1, 4 };
        for (int i = 0; i < weights.Length; i++)
        {
            Assert.AreEqual(weights[i] / 8.0, trials.Count(t => t.trialCondition == i) / 4000.0, 0.02, "condition " + i);
        }
        Assert.IsTrue(trials.All(t => t.trialCondition == 0 ? t.visualContrast == 0.25 : t.trialCondition == 1 ? t.visualContrast == 1 : t.trialType == "auditory"));
    }

    [TestMethod]
    public void AListInAScalarCellIsOnePickedPerTrial()
    {
        var trials = Fixture.Draw(With(Fixture.Condition("visual", 1, "reward.rewardSizeUl", Fixture.List(2.0, 4.0))), 400);
        Assert.IsTrue(trials.All(t => t.rewardOfferedUl == 2 || t.rewardOfferedUl == 4));
        Assert.AreEqual(0.5, trials.Count(t => t.rewardOfferedUl == 2) / 400.0, 0.08);
        var built = Fixture.Check(With(Fixture.Condition("visual", 1, "reward.rewardSizeUl", Fixture.List(2.0, 4.0), "autoRewardProbability", Fixture.List(0.0, 0.5, 1.0))), Fixture.Speakers());
        Assert.AreEqual(72, built[0].Combinations.Count, "every combination, the task's list fields' entries too");
    }

    [TestMethod]
    public void AzimuthZeroIsRefusedForTheGratingAndTheCentreForTheSound()
    {
        var task = Fixture.Task("task-localization.yml", 4);
        task.VisualStimulus.InitialAzimuthsDeg = new List<double> { -60, 0 };
        StringAssert.Contains(Refused(task), "visualStimulus.initialAzimuthsDeg must not hold 0");
        var refusal = Refused(With(Fixture.Condition("auditory", 1, "visualStimulus.initialAzimuthsDeg", Fixture.List(60.0, 0.0))));
        StringAssert.Contains(refusal, "trialConditions[0] (auditory) with ");
        StringAssert.Contains(refusal, "visualStimulus.initialAzimuthsDeg must not hold 0");
        Assert.AreEqual("accepted", Refused(With(Fixture.Condition("visual", 1, "auditoryStimulus.initialAzimuthsDeg", Fixture.List(0.0)))), "a sound at the centre");
    }

    [TestMethod]
    public void EverySoundAzimuthNeedsItsNearestSpeakerOnItsSide()
    {
        // 30 is nearest the centre speaker: refused for a sound at task level and in a condition; a grating may start there.
        var task = Fixture.Task("task-localization.yml", 4);
        task.AuditoryStimulus.InitialAzimuthsDeg = new List<object> { -60.0, 30.0 };
        StringAssert.Contains(Refused(task), "nearest azimuth 30 (elevation 15) is 'centre', not on the right");
        StringAssert.Contains(Refused(With(Fixture.Condition("auditory", 1, "auditoryStimulus.initialAzimuthsDeg", Fixture.List(60.0, -30.0)))), "rig.yml's speaker nearest azimuth -30");
        Assert.AreEqual("accepted", Refused(With(Fixture.Condition("visual", 1, "visualStimulus.initialAzimuthsDeg", Fixture.List(60.0, 30.0)))));
    }

    [TestMethod]
    public void AllPlaysTheSoundOnEverySpeaker()
    {
        // "all" plays on every speaker (speakerChannel AllSpeakersChannel), logged at azimuth and elevation NaN with its
        // amplitude above 0, which a silent trial's 0 tells apart; paired with the grating's azimuths it keeps their pick.
        var all = Fixture.Condition("audiovisualCoherent", 1, "visualStimulus.initialAzimuthsDeg", Fixture.List(-60.0, 60.0), "auditoryStimulus.initialAzimuthsDeg", Fixture.List("all", 60.0));
        all.Pairs = new List<List<string>> { new List<string> { "visualStimulus.initialAzimuthsDeg", "auditoryStimulus.initialAzimuthsDeg" } };
        var trials = Fixture.Draw(With(all), 200);
        foreach (var t in trials)
        {
            Assert.IsTrue(t.auditoryAmplitude > 0);
            if (t.visualAzimuthDeg < 0) Assert.IsTrue(t.speakerChannel == Trial.AllSpeakersChannel && double.IsNaN(t.auditoryAzimuthDeg) && double.IsNaN(t.auditoryElevationDeg));
            else Assert.IsTrue(t.speakerChannel >= 0 && t.auditoryAzimuthDeg > 0, "the right speaker nearest 60");
        }
        Assert.IsTrue(trials.Any(t => t.visualAzimuthDeg < 0) && trials.Any(t => t.visualAzimuthDeg > 0));
        StringAssert.Contains(Refused(With(Fixture.Condition("visual", 1, "auditoryStimulus.initialAzimuthsDeg", Fixture.List("left")))), "value left is neither a number nor all");
        Assert.AreEqual(-2, Trial.AllSpeakersChannel);
    }

    [TestMethod]
    public void MoreThanTheCapOfCombinationsIsRefused()
    {
        // Every list field expands too: the task's two sound azimuths double the set's 16 x 32.
        var sizes = Fixture.List(Enumerable.Range(0, 16).Select(i => (object)(1 + i / 16.0)).ToArray());
        var probabilities = Fixture.List(Enumerable.Range(0, 32).Select(i => (object)(i / 32.0)).ToArray());
        Func<TrialCondition> condition = () => Fixture.Condition("visual", 1, "contrasts", Fixture.List(1.0), "visualStimulus.initialAzimuthsDeg", Fixture.List(60.0),
            "auditoryStimulus.initialAzimuthsDeg", Fixture.List(-60.0, 60.0), "reward.rewardSizeUl", sizes, "autoRewardProbability", probabilities);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.AreEqual(1024, Fixture.Check(With(condition()), Fixture.Speakers())[0].Combinations.Count);
        Console.WriteLine("Go's check and build at 1024 combinations: {0} ms", watch.ElapsedMilliseconds);
        probabilities.Add(1.0);
        StringAssert.Contains(Refused(With(condition())), "trialConditions[0] (visual) expands into 1056 combinations, more than 1024 (x-max-combinations)");
        Assert.AreEqual(1024, TaskSchema.MaxCombinations);
    }

    [TestMethod]
    public void AnyFieldButASessionFieldMayBeSetAndItsValuesAreChecked()
    {
        Func<TrialCondition, string> refusal = c => Refused(With(c));
        Assert.AreEqual("accepted", refusal(Fixture.Condition("visual", 1, "trialStructure.itiMinSeconds", 4.0, "visualStimulus.orientationDeg", Fixture.List(0.0, 90.0))));
        StringAssert.Contains(refusal(Fixture.Condition("visual", 1, "backgroundNoise.levelDbfs", -40.0)), "sets or pairs backgroundNoise.levelDbfs, a session field (x-session-fields: backgroundNoise)");
        // A set path that holds a session field (a map set whole) is refused as the field itself is.
        StringAssert.Contains(refusal(Fixture.Condition("visual", 1, "penalty.sound", new Dictionary<object, object> { { "enabled", true }, { "levelDbfs", -40.0 } })),
            "sets or pairs penalty.sound, a session field (x-session-fields: penalty.sound.levelDbfs)");
        StringAssert.Contains(refusal(Fixture.Condition("visual", 1, "colour.red", 1.0)), "sets or pairs colour.red, which is not a task field");
        StringAssert.Contains(refusal(Fixture.Condition("visual", 1, "optogenetics.laserOnProbability", 1.0)), "sets optogenetics.laserOnProbability with optogenetics off");
        StringAssert.Contains(refusal(Fixture.Condition("visual", 1, "reward.rewardSizeUl", 99.0)), ": reward.rewardSizeUl must be above 0 and at most");
        StringAssert.Contains(refusal(Fixture.Condition("visual", 1, "reward.rewardSizeUl", Fixture.List(2.0, 99.0))), "reward.rewardSizeUl 99, ");
        StringAssert.Contains(refusal(Fixture.Condition("visual", 1, "reward.rewardSizeUl", Fixture.List(2.0, 99.0))), ": reward.rewardSizeUl must be");
        StringAssert.Contains(refusal(Fixture.Condition("visual", 1, "contrasts", Fixture.List(-1.0))), "contrasts must each be a finite number, 0 or more");
        Assert.AreEqual("accepted", refusal(Fixture.Condition("auditory", 1, "contrasts", Fixture.List(0.0), "target", "auditorySide")), "contrast 0: no grating");
    }

    [TestMethod]
    public void AConditionPinsTheLaser()
    {
        var task = With(Fixture.Condition("visual", 1, "optogenetics.laserOnProbability", 1.0), Fixture.Condition("auditory", 1, "optogenetics.laserOnProbability", 0.0));
        task.Optogenetics.Enabled = true;
        task.Optogenetics.LaserPowerMw = 3;
        task.Optogenetics.TrialsBeforeLaser = 0;
        task.Optogenetics.LaserDurationSeconds = 1;
        var trials = Fixture.Draw(task, 200);
        Assert.IsTrue(trials.All(t => t.laserOn == (t.trialType == "visual")));
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
            "      set: { contrasts: [0.5, 1], reward.rewardSizeUl: [2, 3], visualStimulus.initialAzimuthsDeg: [60] }");
        var task = TrialDraw.Read(text).TaskParameters;
        var set = (IDictionary<object, object>)task.TrialConditions[0].Set;
        CollectionAssert.AreEqual(new object[] { "contrasts", "reward.rewardSizeUl", "visualStimulus.initialAzimuthsDeg" }, set.Keys.ToArray());
        var again = TrialDraw.Read(Fixture.Text(task)).TaskParameters;
        StringAssert.Contains(Newtonsoft.Json.JsonConvert.SerializeObject(again.TrialConditions[0]),
            "\"set\":{\"contrasts\":[0.5,1],\"reward.rewardSizeUl\":[2,3],\"visualStimulus.initialAzimuthsDeg\":[60]}");
        again.Curriculum = Fixture.Curriculum(4, "x");
        var built = Fixture.Check(again, Fixture.Speakers());
        // A list field's entries are one combination each, by path, the first changing fastest (the task's two sound azimuths,
        // then the contrasts, then the rewards): the draw's chances unchanged.
        CollectionAssert.AreEqual(new[] { 2.0, 2.0, 2.0, 2.0, 3.0, 3.0, 3.0, 3.0 }, built[0].Combinations.Select(v => v.Task.Reward.RewardSizeUl).ToArray());
        CollectionAssert.AreEqual(new[] { 0.5, 0.5, 1.0, 1.0, 0.5, 0.5, 1.0, 1.0 }, built[0].Combinations.Select(v => v.Task.Contrasts.Single()).ToArray());
        CollectionAssert.AreEqual(new[] { 60.0 }, built[0].Combinations[1].Task.VisualStimulus.InitialAzimuthsDeg);
        Assert.AreEqual(4, built[1].Combinations.Count, "the task's two lists of 2 azimuths");
        Assert.AreEqual(3.0, built[1].Combinations[0].Task.Reward.RewardSizeUl);
        CollectionAssert.AreEqual(new[] { 0.0 }, built[1].Combinations[0].Task.Contrasts);
    }

    [TestMethod]
    public void AValueOfTheWrongTypeIsRefusedNamingTheCondition()
    {
        StringAssert.Contains(Refused(With(Fixture.Condition("visual", 1, "reward.rewardSizeUl", "abc"))), "trialConditions[0] (visual)");
        StringAssert.Contains(Refused(With(Fixture.Condition("visual", 1, "autoRewardProbability", Fixture.List("x", 0.5)))), "trialConditions[0] (visual)");
        StringAssert.Contains(Refused(With(Fixture.Condition("visual", 1, "visualStimulus.initialAzimuthsDeg", "left"))), "trialConditions[0] (visual)");
        StringAssert.Contains(Refused(With(Fixture.Condition("visual", 1, "contrasts", Fixture.List()))), "trialConditions[0] (visual)");
    }

    [TestMethod]
    public void OneConditionAZeroAmongOthersAndTheWeightCapAreDrawnAsWritten()
    {
        var one = Fixture.Draw(With(Fixture.Condition("neutral", 1)), 30);
        Assert.IsTrue(one.All(t => t.trialCondition == 0 && t.trialType == "neutral"));
        // Weight 0 among others: its index is never logged, though its set is still checked at Go.
        var zero = With(Fixture.Condition("visual", 0), Fixture.Condition("auditory", 2), Fixture.Condition("neutral", 1));
        Assert.IsTrue(Fixture.Draw(zero, 300).All(t => t.trialCondition != 0));
        StringAssert.Contains(Refused(With(Fixture.Condition("visual", 0, "contrasts", Fixture.List(-1.0)), Fixture.Condition("auditory", 1))), "trialConditions[0] (visual)");
        Assert.AreEqual("accepted", Refused(With(Fixture.Condition("visual", TrialDraw.MaxWeight), Fixture.Condition("auditory", TrialDraw.MaxWeight))));
        Assert.IsTrue(Fixture.Draw(With(Fixture.Condition("visual", TrialDraw.MaxWeight), Fixture.Condition("auditory", TrialDraw.MaxWeight)), 40).Any(t => t.trialCondition == 1));
    }

    [TestMethod]
    public void ManyConditionsAreEachDrawnAndTheirWeightsSumWithoutOverflow()
    {
        var many = Enumerable.Range(0, 120).Select(i => Fixture.Condition(i % 2 == 0 ? "visual" : "auditory", 1, "reward.rewardSizeUl", 1.0 + i % 3)).ToArray();
        var trials = Fixture.Draw(With(many), 3000);
        Assert.IsTrue(trials.All(t => t.trialType == (t.trialCondition % 2 == 0 ? "visual" : "auditory") && t.rewardOfferedUl == 1.0 + t.trialCondition % 3));
        Assert.AreEqual(120, trials.Select(t => t.trialCondition).Distinct().Count());
        // 2200 conditions of the cap's weight sum past int, which the draw cannot take.
        var heavy = Enumerable.Range(0, 2200).Select(i => Fixture.Condition("visual", TrialDraw.MaxWeight)).ToArray();
        StringAssert.Contains(Refused(With(heavy)), "weights add to more than 2147483647");
    }
}
