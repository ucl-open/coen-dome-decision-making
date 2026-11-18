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

    // The share's training pipeline stages of each task file (config\training-pipelines), as the task file's rows were
    // before they moved there: the ticked fields only, a stage's trialWeights set whole, null left to the task file.
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

    // The task file as app-rig-control writes it at launch for a pipeline stage: the stage's values over the file's, and
    // trainingPipeline filled.
    public static UclOpenCoenDomeDecisionMakingTaskParameters Task(string file, int stage)
    {
        var text = File.ReadAllText(Path.Combine(Repo(), "config", file));
        var task = new DeserializerBuilder().Build().Deserialize<UclOpenCoenDomeDecisionMakingTaskLogic>(text).TaskParameters;
        Assert.IsNull(task.TrainingPipeline, "the repo's task files leave trainingPipeline empty");
        var s = Stages[file][stage];
        if (s.Weights != null) task.TrialConditions = Conditions(s.Weights);
        task.AutoRewardProbability = s.Auto;
        task.BothSpoutsProbability = s.Both;
        task.Contrasts = s.Contrasts.ToList();
        task.AllowRepeats = s.Repeats;
        task.SpoutMovements = s.Moves;
        task.TrainingPipeline = new TrainingPipeline { Id = stage, Label = s.Label, Sha256 = "test" };
        task.RngSeed = 1;
        return task;
    }

    // A stage's trial mix as its conditions, one per type, no condition-level values.
    static List<TrialCondition> Conditions(string weights)
    {
        return new DeserializerBuilder().Build().Deserialize<Dictionary<string, int>>(weights)
            .Select(w => new TrialCondition { Type = w.Key, Weight = w.Value, Set = new Dictionary<object, object>() }).ToList();
    }

    /// <summary>A condition of a type and weight with these condition-level values.</summary>
    public static TrialCondition Condition(string type, int weight, params object[] pathsAndValues)
    {
        var set = new Dictionary<object, object>();
        for (int i = 0; i < pathsAndValues.Length; i += 2) set.Add(pathsAndValues[i], pathsAndValues[i + 1]);
        return new TrialCondition { Type = type, Weight = weight, Set = set };
    }

    /// <summary>A list value for a condition's cell.</summary>
    public static List<object> List(params object[] values)
    {
        return values.ToList();
    }

    public static void Weight(UclOpenCoenDomeDecisionMakingTaskParameters task, string type, int weight)
    {
        task.TrialConditions.Single(c => c.Type == type).Weight = weight;
    }

    // Every condition not of these types weighted 0.
    public static void Only(UclOpenCoenDomeDecisionMakingTaskParameters task, params string[] types)
    {
        foreach (var c in task.TrialConditions.Where(c => !types.Contains(c.Type))) c.Weight = 0;
    }

    /// <summary>The task file's text as rig-control writes it, from the task (the draw and the Go check read the text).</summary>
    public static string Text(UclOpenCoenDomeDecisionMakingTaskParameters task)
    {
        return new SerializerBuilder().Build().Serialize(new UclOpenCoenDomeDecisionMakingTaskLogic { TaskParameters = task });
    }

    /// <summary>TrialDraw.CheckTask on the task's text: its conditions as built, or the refusal thrown.</summary>
    public static List<TrialConditionDraw> Check(UclOpenCoenDomeDecisionMakingTaskParameters task, int stage, EqualisedSpeakerArray speakers)
    {
        UclOpenCoenDomeDecisionMakingTaskParameters read;
        return TrialDraw.CheckTask(Text(task), stage, speakers, out read);
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
    public static List<Trial> Draw(UclOpenCoenDomeDecisionMakingTaskParameters task, int stage, int count, Func<Trial, string> outcome = null)
    {
        var draw = new TrialDraw(Text(task), Speakers(), stage, path => 2);
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
}

[TestClass]
public class TrialDrawTests
{
    [TestMethod]
    public void LocalizationAnswersTheStimulusSide()
    {
        var trials = Fixture.Draw(Fixture.Task("task-localization.yml", 4), 4, 500);
        foreach (var t in trials)
        {
            Assert.IsTrue(t.auditoryOn && t.spoutMovements, t.trialType);
            if (t.trialType == "visual" || t.trialType == "audiovisualCoherent")
                Assert.AreEqual(t.visualAzimuthDeg > 0 ? "Right" : "Left", t.target, t.trialType);
            if (t.trialType == "auditory" || t.trialType == "audiovisualCoherent")
                Assert.AreEqual(t.auditoryAzimuthDeg > 0 ? "Right" : "Left", t.target, t.trialType);
            if (t.trialType == "audiovisualConflict")
                Assert.AreEqual(t.visualAzimuthDeg > 0, t.auditoryAzimuthDeg < 0);
            if (t.trialType == "visual" || t.trialType == "neutral")
                Assert.AreEqual(2, t.speakerChannel, "the centre speaker");
        }
        CollectionAssert.AreEquivalent(TrialDraw.TypeNames, trials.Select(t => t.trialType).Distinct().ToArray());
    }

    [TestMethod]
    public void LocalizationConflictKeepsItsStimuliAndIsAProbe()
    {
        // A random-reward probe: the grating and sound on opposite sides, no side to answer, its coin
        // heads about half the time (ATrialWithNoRightAnswerRewardsOrPenalisesEitherChoiceOnACoin scores it).
        var task = Fixture.Task("task-localization.yml", 4);
        Fixture.Only(task, "audiovisualConflict");
        var trials = Fixture.Draw(task, 4, 600);
        Assert.IsTrue(trials.All(t => t.trialType == "audiovisualConflict" && (t.visualAzimuthDeg > 0) == (t.auditoryAzimuthDeg < 0) && t.target == Target.Random));
        Assert.AreEqual(0.5, trials.Count(t => t.probeHeads) / 600.0, 0.07);
    }

    [TestMethod]
    public void EachSoundPlaysOnTheNearestSpeaker()
    {
        foreach (var t in Fixture.Draw(Fixture.Task("task-localization.yml", 4), 4, 300))
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
        foreach (var modality in new[] { Modality.Visual, Modality.Auditory })
        {
            var task = Fixture.Task("task-discrimination-left-right.yml", 3);
            task.LeftModality = modality;
            foreach (var t in Fixture.Draw(task, 3, 500))
            {
                if (t.trialType == "visual") Assert.IsTrue(!t.auditoryOn && t.visualContrast > 0 && t.target == (modality == Modality.Visual ? "Left" : "Right"));
                if (t.trialType == "auditory") Assert.IsTrue(t.auditoryOn && t.visualContrast == 0 && t.target == (modality == Modality.Auditory ? "Left" : "Right"));
                if (t.trialType == "neutral") Assert.IsTrue(!t.auditoryOn && double.IsNaN(t.auditoryAzimuthDeg) && t.speakerChannel == -1);
            }
        }
    }

    [TestMethod]
    public void DiscriminationHabituationAutoRewardsAtTheLeftSpoutWithTheSpoutsIn()
    {
        Assert.IsTrue(Fixture.Draw(Fixture.Task("task-discrimination-left-right.yml", 0), 0, 50).All(t => t.autoReward && t.target == "Left" && !t.spoutMovements && t.trialType == "visual"));
        var task = Fixture.Task("task-discrimination-left-right.yml", 2);
        task.AutoRewardProbability = 1;
        var stage2 = Fixture.Draw(task, 2, 300);
        Assert.IsTrue(stage2.Where(t => t.target != Target.Random).All(t => t.autoReward), "left-right: either spout's answer may be auto-rewarded");
        Assert.IsTrue(stage2.Where(t => t.target == Target.Random).All(t => !t.autoReward), "a probe never is");
    }

    [TestMethod]
    public void GoNoGoAnswersTheModalityAtTheLeftSpout()
    {
        foreach (var modality in new[] { Modality.Visual, Modality.Auditory })
        {
            var task = Fixture.Task("task-discrimination-go-no-go.yml", 3);
            task.GoModality = modality;
            task.AutoRewardProbability = 1;
            foreach (var t in Fixture.Draw(task, 3, 600))
            {
                var expected = t.trialType == "audiovisualCoherent" || t.trialType == "audiovisualConflict" || t.trialType == "neutral" ? Target.NoReward
                    : (t.trialType == "visual" && modality == Modality.Visual) || (t.trialType == "auditory" && modality == Modality.Auditory) ? Target.Go
                    : Target.NoGo;   // the other modality
                Assert.AreEqual(expected, t.target, t.trialType);
                Assert.AreEqual(expected == Target.Go, t.autoReward, "auto-reward only on Go");
                Assert.AreEqual(expected == Target.Go, t.rewardOfferedUl > 0, "a reward on offer only on Go");
                Assert.IsFalse(t.bothSpoutsOffered);
                Assert.AreEqual(LickSpoutCommand.LeftOnly, SpoutsInCommand.Command(t));
            }
        }
    }

    [TestMethod]
    public void GoNoGoRefusesBothSpouts()
    {
        var task = Fixture.Task("task-discrimination-go-no-go.yml", 2);
        task.BothSpoutsProbability = 0.5;
        StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, 2, Fixture.Speakers())), "bothSpoutsProbability must be 0 in discriminationGoNoGo");
    }

    [TestMethod]
    public void ATaskWithoutSpoutMovementsNeedsBothSpouts()
    {
        // The SpoutsIn cue's LeftOnly or RightOnly draws the other spout back, never returned without spout movements.
        var task = Fixture.Task("task-localization.yml", 0);
        task.SpoutMovements = false;
        task.BothSpoutsProbability = 0.5;
        StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, 0, Fixture.Speakers())), "bothSpoutsProbability must be 1 with spoutMovements false");
        Fixture.Check(Fixture.Task("task-discrimination-go-no-go.yml", 0), 0, Fixture.Speakers());   // go/no-go's one spout: allowed
    }

    [TestMethod]
    public void TheTypeNamesAreNavigationSAndTheDrawSConstants()
    {
        // The draw names its trial types, coen-dome-navigation's names, never bare numbers.
        CollectionAssert.AreEqual(new[] { "visual", "auditory", "audiovisualCoherent", "audiovisualConflict", "neutral" }, TrialDraw.TypeNames);
        CollectionAssert.AreEqual(new[] { "go", "noGo", "audiovisualCoherent", "audiovisualConflict", "neutral" }, TrialDraw.Keys(TaskType.DiscriminationGoNoGo));
        Assert.IsTrue(TrialDraw.RandomRewardProbe(TaskType.Localization, TrialDraw.Neutral));
        Assert.IsFalse(TrialDraw.RandomRewardProbe(TaskType.Localization, TrialDraw.AudiovisualCoherent));
        Assert.IsTrue(TrialDraw.RandomRewardProbe(TaskType.DiscriminationLeftRight, TrialDraw.AudiovisualCoherent));
        Assert.IsFalse(TrialDraw.RandomRewardProbe(TaskType.DiscriminationGoNoGo, TrialDraw.Neutral));
    }

    [TestMethod]
    public void TheWeightsAreByName()
    {
        var task = Fixture.Task("task-localization.yml", 0);
        Assert.AreEqual(0, task.TrialConditions.Where(c => c.Type != "audiovisualCoherent").Sum(c => c.Weight));
        Assert.IsTrue(Fixture.Draw(task, 0, 100).All(t => t.trialType == "audiovisualCoherent"));
    }

    [TestMethod]
    public void EachTrialTypeIsDrawnAtRandomByWeight()
    {
        // Stage 3: visual 10, auditory 10, coherent 5, conflict 5, neutral 1, each new trial drawn independently.
        var trials = Fixture.Draw(Fixture.Task("task-discrimination-left-right.yml", 3), 3, 6200);
        var weights = new Dictionary<string, double> { { "visual", 10 }, { "auditory", 10 }, { "audiovisualCoherent", 5 }, { "audiovisualConflict", 5 }, { "neutral", 1 } };
        foreach (var w in weights)
        {
            Assert.AreEqual(w.Value / 31, trials.Count(t => t.trialType == w.Key) / 6200.0, 0.015, w.Key);
        }
        var runs = trials.Zip(trials.Skip(1), (a, b) => a.trialType == b.trialType).Count(x => x) / 6199.0;
        Assert.AreEqual(weights.Values.Sum(v => (v / 31) * (v / 31)), runs, 0.02);   // independent: no queue
    }

    [TestMethod]
    public void RepeatsFollowTheRulesByName()
    {
        var task = Fixture.Task("task-localization.yml", 4);
        task.RngSeed = 7;
        var trials = Fixture.Draw(task, 4, 400, t => "Incorrect");
        for (int i = 1; i < trials.Count; i++)
        {
            var before = trials[i - 1];
            bool repeats = before.repeatCount < task.Repeats.MaxRepeats &&
                (before.trialType == "visual" || before.trialType == "auditory" || before.trialType == "audiovisualCoherent");
            Assert.AreEqual(repeats ? before.repeatCount + 1 : 0, trials[i].repeatCount, before.trialType);
            if (repeats) Assert.IsTrue(before.visualAzimuthDeg == trials[i].visualAzimuthDeg && before.trialCondition == trials[i].trialCondition);
        }
        Assert.IsTrue(trials.Any(t => t.repeatCount == task.Repeats.MaxRepeats));
        // Only a trial with a right answer repeats: never a conflict or neutral one.
        var noResponse = Fixture.Draw(task, 4, 400, t => "NoResponse");
        Assert.IsTrue(noResponse.Any(t => t.trialType == "neutral") && noResponse.Any(t => t.trialType == "audiovisualConflict"));
        Assert.IsFalse(noResponse.Any(t => t.repeatCount > 0 && (t.trialType == "neutral" || t.trialType == "audiovisualConflict")));
        var leftRight = Fixture.Task("task-discrimination-left-right.yml", 3);
        leftRight.AllowRepeats = true;
        var lr = Fixture.Draw(leftRight, 3, 400, t => "Incorrect");
        Assert.IsTrue(lr.Any(t => t.repeatCount > 0 && (t.trialType == "visual" || t.trialType == "auditory")));
        Assert.IsFalse(lr.Any(t => t.repeatCount > 0 && (t.trialType == "audiovisualCoherent" || t.trialType == "audiovisualConflict" || t.trialType == "neutral")));
    }

    [TestMethod]
    public void OnNoResponseRepeatsAfterANoResponseAlone()
    {
        // onNoResponse names its outcome: a NoResponse repeats the trial, an Incorrect never does.
        var task = Fixture.Task("task-localization.yml", 4);
        task.RngSeed = 7;
        var r = task.Repeats.RulesPerType;
        r.Visual = r.Auditory = r.AudiovisualCoherent = RepeatRule.OnNoResponse;
        Assert.IsTrue(Fixture.Draw(task, 4, 100, t => "Incorrect").All(t => t.repeatCount == 0));
        Assert.IsTrue(Fixture.Draw(task, 4, 100, t => "NoResponse").Any(t => t.repeatCount > 0));
    }

    [TestMethod]
    public void EachTrialDrawsItsOwnInterval()
    {
        var task = Fixture.Task("task-discrimination-left-right.yml", 3);
        var trials = Fixture.Draw(task, 3, 300, t => t.trialNumber % 3 == 0 ? "Incorrect" : "Correct");
        for (int i = 0; i < trials.Count; i++) Assert.AreEqual(i + 1, trials[i].trialNumber);
        // What Gerion's mice got: 1.5 to 2.0 s from the outcome (after any timeout) to the stimulus.
        Assert.IsTrue(trials.All(t => t.itiSeconds >= 1.5 && t.itiSeconds <= 2.0));
        Assert.IsTrue(trials.Max(t => t.itiSeconds) > 1.95 && trials.Min(t => t.itiSeconds) < 1.55);
        var repeats = Fixture.Draw(Fixture.Task("task-localization.yml", 4), 4, 300, t => "Incorrect").Where(t => t.repeatCount > 0).ToList();
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
        var trials = Fixture.Draw(task, 4, 300, t => "NoResponse");
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
        Assert.IsTrue(Fixture.Draw(Fixture.Task("task-discrimination-left-right.yml", 3), 3, 100).All(t => t.laserCondition == 0 && !t.laserOn && t.laserPowerMw == 0));
    }

    [TestMethod]
    public void ARepeatOfTheSameSeedDrawsTheSameTrials()
    {
        var a = Fixture.Draw(Fixture.Task("task-localization.yml", 4), 4, 50);
        var b = Fixture.Draw(Fixture.Task("task-localization.yml", 4), 4, 50);
        CollectionAssert.AreEqual(a.Select(t => t.visualPhaseRad).ToList(), b.Select(t => t.visualPhaseRad).ToList());
    }

    [TestMethod]
    public void TheDrawRefusesATaskItCannotRun()
    {
        var speakers = Fixture.Speakers();
        Func<UclOpenCoenDomeDecisionMakingTaskParameters, int, string> refusal = (parameters, stage) =>
            Fixture.Refusal(() => new TrialDraw(Fixture.Text(parameters), speakers, stage, path => 2));
        StringAssert.Contains(refusal(Fixture.Task("task-localization.yml", 4), 3), "TrainingStage is 3, but the task file's values are training pipeline stage 4 (Conflict Added)");

        var task = Fixture.Task("task-localization.yml", 4);
        task.Contrasts.Clear();
        StringAssert.Contains(refusal(task, 4), "contrasts must each be above 0");

        task = Fixture.Task("task-localization.yml", 4);
        task.VisualStimulus.StartAzimuthsDeg = new List<double> { -60, 200 };
        StringAssert.Contains(refusal(task, 4), "startAzimuthsDeg value 200 is outside [-180, 180]");

        task = Fixture.Task("task-localization.yml", 4);
        task.TrialStructure.StimulusSeconds = 2;
        StringAssert.Contains(refusal(task, 4), "stimulusSeconds");

        task = Fixture.Task("task-localization.yml", 4);
        task.TrialStructure.ResponseWindowSeconds = 5;
        task.TrialStructure.StimulusSeconds = task.TrialStructure.ItiMinSeconds + 0.5;
        StringAssert.Contains(refusal(task, 4), "longer than itiMinSeconds");

        task = Fixture.Task("task-localization.yml", 4);
        task.TrialStructure.ResponseWindowSeconds = null;
        StringAssert.Contains(refusal(task, 4), "responseWindowSeconds must be given");

        task = Fixture.Task("task-localization.yml", 4);
        speakers.Speakers.Remove("right");
        StringAssert.Contains(refusal(task, 4), "is 'centre', not on the right");

        task = Fixture.Task("task-discrimination-left-right.yml", 4);
        task.Optogenetics.Enabled = true;
        task.Optogenetics.LaserPowerMw = null;
        task.Optogenetics.LaserDurationSeconds = 0.8;
        StringAssert.Contains(Fixture.Refusal(() => new TrialDraw(Fixture.Text(task), Fixture.Speakers(), 4, path => 2)), "laserPowerMw");
    }

    [TestMethod]
    public void TheTrainingPipelineStageIsCheckedAtGoBeforeTheFirstTrial()
    {
        // trainingPipeline (app-rig-control's at launch) must name TrainingStage's stage: one stage's values never run as another's.
        var task = Fixture.Task("task-localization.yml", 4);
        Assert.AreEqual("accepted", Fixture.Refusal(() => Fixture.Check(task, 4, Fixture.Speakers())));
        StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, 5, Fixture.Speakers())), "TrainingStage is 5, but the task file's values are training pipeline stage 4 (Conflict Added)");
        task.TrainingPipeline.Sha256 = "";
        StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, 4, Fixture.Speakers())), "trainingPipeline is empty");
        task.TrainingPipeline = null;
        StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, 4, Fixture.Speakers())), "trainingPipeline is empty");
    }

    [TestMethod]
    public void TheDiscriminationsDrawByRoleForEitherModality()
    {
        // The stages set the mix by role; goModality or leftModality decides which stimulus fills it.
        foreach (var modality in new[] { Modality.Visual, Modality.Auditory })
        {
            string mine = modality == Modality.Visual ? "visual" : "auditory", other = modality == Modality.Visual ? "auditory" : "visual";
            var leftRight = Fixture.Task("task-discrimination-left-right.yml", 0);
            leftRight.LeftModality = modality;
            Assert.IsTrue(Fixture.Draw(leftRight, 0, 50).All(t => t.trialType == mine && t.target == "Left"), "left-right stage 0: left only");
            var goNoGo = Fixture.Task("task-discrimination-go-no-go.yml", 1);
            goNoGo.GoModality = modality;
            Assert.IsTrue(Fixture.Draw(goNoGo, 1, 50).All(t => t.trialType == mine && t.target == Target.Go), "go/no-go stage 1: go only");
            goNoGo = Fixture.Task("task-discrimination-go-no-go.yml", 2);
            goNoGo.GoModality = modality;
            Fixture.Weight(goNoGo, "audiovisualCoherent", 0);
            var stage2 = Fixture.Draw(goNoGo, 2, 400);
            Assert.IsTrue(stage2.All(t => t.trialType == mine ? t.target == Target.Go : t.trialType == other && t.target == Target.NoGo));
            Assert.AreEqual(0.5, stage2.Count(t => t.trialType == mine) / 400.0, 0.08, "go 10, noGo 10");
            leftRight = Fixture.Task("task-discrimination-left-right.yml", 2);
            leftRight.LeftModality = modality;
            var built = Fixture.Check(leftRight, 2, Fixture.Speakers());
            Assert.AreEqual(other, built.Single(c => c.Type == "right").TrialType, "right fills the other modality's place");
            Assert.AreEqual(mine, built.Single(c => c.Type == "left").TrialType);
        }
        Assert.AreEqual("accepted", Fixture.Refusal(() => Fixture.Check(Fixture.Task("task-localization.yml", 0), 0, Fixture.Speakers())));
    }

    [TestMethod]
    public void TheDrawTakesTheTasksConditionWeights()
    {
        // The weights edited for a launch (Setup) are the draw's.
        var task = Fixture.Task("task-localization.yml", 4);
        Fixture.Only(task, "neutral");
        Fixture.Weight(task, "neutral", 1);
        Assert.IsTrue(Fixture.Draw(task, 4, 50).All(t => t.trialType == "neutral"));
        var goNoGo = Fixture.Task("task-discrimination-go-no-go.yml", 3);
        goNoGo.GoModality = Modality.Auditory;
        Fixture.Only(goNoGo, "go");
        Assert.IsTrue(Fixture.Draw(goNoGo, 3, 50).All(t => t.trialType == "auditory" && t.target == Target.Go));
    }

    [TestMethod]
    public void AGoNoGoTaskWithNoGoTrialsOrForeignKeysIsRefused()
    {
        var task = Fixture.Task("task-discrimination-go-no-go.yml", 2);
        Fixture.Weight(task, "go", 0);
        StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, 2, Fixture.Speakers())), "trialConditions has no Go trials: no go condition has a weight above 0");
        task = Fixture.Task("task-discrimination-go-no-go.yml", 3);
        Fixture.Weight(task, "go", 0);
        StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, 3, Fixture.Speakers())), "has no Go trials");
        task = Fixture.Task("task-discrimination-go-no-go.yml", 0);
        task.TrialConditions.Add(Fixture.Condition("visual", 5));
        StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, 0, Fixture.Speakers())),
            "trialConditions[5] (visual): its type is not one of go, noGo, audiovisualCoherent, audiovisualConflict, neutral in discriminationGoNoGo");
        task = Fixture.Task("task-discrimination-left-right.yml", 0);
        task.Repeats.RulesPerType.Left = null;
        StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, 0, Fixture.Speakers())), "repeats.rulesPerType must give exactly left, right");
        task = Fixture.Task("task-localization.yml", 0);
        task.TrialConditions.Add(Fixture.Condition("go", 1));
        StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, 0, Fixture.Speakers())), "trialConditions[5] (go): its type is not one of visual, auditory");
    }

    [TestMethod]
    public void ALaserThatWouldDelayTheNextArmIsRefused()
    {
        // Discrimination: 0 open loop + 1.5 s interval - 0.5 s ramp-down - 0.2 s reply margin = 0.8 s.
        var task = Fixture.Task("task-discrimination-left-right.yml", 4);
        task.Optogenetics.Enabled = true;
        task.Optogenetics.LaserPowerMw = 3;
        Assert.AreEqual(0.8, TrialDraw.LongestLaserSeconds(task), 1e-9);
        StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, 4, Fixture.Speakers())), "laserDurationSeconds (2.5 s) must be at most 0.8 s");
        task.Optogenetics.LaserDurationSeconds = 0.8;
        Assert.AreEqual("accepted", Fixture.Refusal(() => Fixture.Check(task, 4, Fixture.Speakers())));
        task.Optogenetics.LaserDurationSeconds = 2.5;
        task.Optogenetics.Enabled = false;
        Assert.AreEqual("accepted", Fixture.Refusal(() => Fixture.Check(task, 4, Fixture.Speakers())));
    }

    [TestMethod]
    public void ALargeRewardOverTheCapIsRefusedAtGo()
    {
        // A RewardValve piece refuses a reward over its MaxRewardUl (lib's MAX_REWARD_UL) mid-session; Go refuses it first.
        var task = Fixture.Task("task-localization.yml", 4);
        task.Reward.LargeRewardProbability = 0.2;
        task.Reward.LargeRewardMultiplier = 3;
        task.Reward.RewardSizeUl = 8;
        var refusal = Fixture.Refusal(() => Fixture.Check(task, 4, Fixture.Speakers()));
        StringAssert.Contains(refusal, "reward.rewardSizeUl (8 ul) x reward.largeRewardMultiplier (3) is 24 ul, more than 20 ul");
        task.Reward.RewardSizeUl = OpenRigs.Core.LoadRewardLine.DefaultMaxRewardUl / 3;
        Assert.AreEqual("accepted", Fixture.Refusal(() => Fixture.Check(task, 4, Fixture.Speakers())));
        task.Reward.RewardSizeUl = 8;
        task.Reward.LargeRewardProbability = 0;
        Assert.AreEqual("accepted", Fixture.Refusal(() => Fixture.Check(task, 4, Fixture.Speakers())));
    }

    [TestMethod]
    public void TheSignedLeadIsRefusedOutsideTheIntervalAndTheResponseWindow()
    {
        Func<double, string> refusal = lead =>
        {
            var task = Fixture.Task("task-discrimination-left-right.yml", 4);   // itiMinSeconds 1.5, response window 0 + 1.5 s
            task.Optogenetics.LaserToStimulusSeconds = lead;
            return Fixture.Refusal(() => Fixture.Check(task, 4, Fixture.Speakers()));
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
        StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, 4, Fixture.Speakers())), "are both above 0 (the wheel's quiet wait) or both 0 (off)");
        task.TrialStructure.WheelQuietCm = 2;
        Assert.AreEqual("accepted", Fixture.Refusal(() => Fixture.Check(task, 4, Fixture.Speakers())));
        task.TrialStructure.LickQuietSeconds = -1;
        StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, 4, Fixture.Speakers())), "trialStructure.lickQuietSeconds is -1, not from 0 to 10 s");
        // A NaN or infinite time would hold the trial start for ever, with no error.
        task.TrialStructure.LickQuietSeconds = double.PositiveInfinity;
        StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, 4, Fixture.Speakers())), "lickQuietSeconds is ");
        task = Fixture.Task("task-localization.yml", 4);
        task.TrialStructure.WheelQuietSeconds = double.NaN;
        StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, 4, Fixture.Speakers())), "wheelQuietSeconds is NaN, not from 0 to 10 s");
        task = Fixture.Task("task-localization.yml", 4);
        task.TrialStructure.ItiMaxSeconds = double.PositiveInfinity;
        StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, 4, Fixture.Speakers())), "trialStructure.itiMaxSeconds is ");
        task = Fixture.Task("task-localization.yml", 4);
        task.Penalty.TimeoutSeconds = double.NaN;
        task.Optogenetics.LaserToStimulusSeconds = double.NaN;
        var refusal = Fixture.Refusal(() => Fixture.Check(task, 4, Fixture.Speakers()));
        StringAssert.Contains(refusal, "penalty.timeoutSeconds must be a finite number, 0 or more");
        StringAssert.Contains(refusal, "laserToStimulusSeconds must be a finite number");
        task = Fixture.Task("task-localization.yml", 4);
        task.TrialStructure.DrinkSeconds = 3.5;
        StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, 4, Fixture.Speakers())), "below drinkSeconds");
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
            StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, 4, Fixture.Speakers())), "trialStructure." + c.Key);
        }
        var atMax = Fixture.Task("task-localization.yml", 4);
        atMax.TrialStructure.LickQuietSeconds = 10;
        atMax.TrialStructure.WheelQuietSeconds = 10;
        atMax.TrialStructure.WheelQuietCm = 20;
        Assert.AreEqual("accepted", Fixture.Refusal(() => Fixture.Check(atMax, 4, Fixture.Speakers())));
    }

    [TestMethod]
    public void NoWeightANegativeWeightOrAReversedIntervalIsRefused()
    {
        // Bonsai reads the task file without pydantic: zero weights would draw no type at the first trial.
        var task = Fixture.Task("task-localization.yml", 4);
        Fixture.Only(task);
        StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, 4, Fixture.Speakers())), "trialConditions needs a condition with a weight above 0");
        task = Fixture.Task("task-localization.yml", 3);
        Fixture.Weight(task, "neutral", -1);
        StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, 3, Fixture.Speakers())), "trialConditions[4] (neutral): its weight is below 0");
        task.TrialConditions.Clear();
        StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, 3, Fixture.Speakers())), "trialConditions needs a condition");
        task = Fixture.Task("task-localization.yml", 4);
        task.TrialStructure.ItiMaxSeconds = task.TrialStructure.ItiMinSeconds - 0.1;
        StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, 4, Fixture.Speakers())), "trialStructure.itiMaxSeconds (");
        task = Fixture.Task("task-localization.yml", 4);
        task.AutoRewardProbability = 1.5;
        StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, 4, Fixture.Speakers())), "autoRewardProbability is 1.5, not from 0 to 1");
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
            StringAssert.Contains(Fixture.Refusal(() => TrialDraw.CheckTask(writer.ToString(), 4, Fixture.Speakers(), out read)), c.Key);
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
        return Fixture.Refusal(() => Fixture.Check(task, 4, Fixture.Speakers()));
    }

    [TestMethod]
    public void TheAzimuthsSignGivesTheSideAndTheTarget()
    {
        foreach (var azimuth in new[] { 60.0, -75.0 })
        {
            foreach (var type in new[] { "visual", "auditory", "audiovisualCoherent" })
            {
                var trials = Fixture.Draw(With(Fixture.Condition(type, 1, "visualStimulus.startAzimuthsDeg", Fixture.List(azimuth))), 4, 40);
                var side = azimuth > 0 ? "Right" : "Left";
                Assert.IsTrue(trials.All(t => t.target == side && t.trialType == type), type);
                if (type != "auditory") Assert.IsTrue(trials.All(t => t.visualAzimuthDeg == azimuth && t.visualElevationDeg == 15), "the grating starts there exactly");
                if (type != "visual") Assert.IsTrue(trials.All(t => t.auditoryAzimuthDeg == (azimuth > 0 ? 90 : -90)), "the nearest speaker");
            }
        }
        // Several listed: one picked per trial, both sides drawn.
        var mixed = Fixture.Draw(With(Fixture.Condition("visual", 1, "visualStimulus.startAzimuthsDeg", Fixture.List(-60.0, 60.0))), 4, 200);
        Assert.AreEqual(0.5, mixed.Count(t => t.target == "Right") / 200.0, 0.1);
        Assert.IsTrue(mixed.All(t => (t.visualAzimuthDeg > 0) == (t.target == "Right")));
    }

    [TestMethod]
    public void AConflictTrialsSoundIsMirroredAndItIsAProbe()
    {
        var trials = Fixture.Draw(With(Fixture.Condition("audiovisualConflict", 1, "visualStimulus.startAzimuthsDeg", Fixture.List(60.0))), 4, 50);
        Assert.IsTrue(trials.All(t => t.visualAzimuthDeg == 60 && t.auditoryAzimuthDeg == -90 && t.target == Target.Random));
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
        var trials = Fixture.Draw(task, 4, 4000);
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
        var trials = Fixture.Draw(With(Fixture.Condition("visual", 1, "reward.rewardSizeUl", Fixture.List(2.0, 4.0))), 4, 400);
        Assert.IsTrue(trials.All(t => t.rewardOfferedUl == 2 || t.rewardOfferedUl == 4));
        Assert.AreEqual(0.5, trials.Count(t => t.rewardOfferedUl == 2) / 400.0, 0.08);
        var built = Fixture.Check(With(Fixture.Condition("visual", 1, "reward.rewardSizeUl", Fixture.List(2.0, 4.0), "autoRewardProbability", Fixture.List(0.0, 0.5, 1.0))), 4, Fixture.Speakers());
        Assert.AreEqual(6, built[0].Combinations.Count, "every combination");
    }

    [TestMethod]
    public void AzimuthZeroIsRefused()
    {
        var task = Fixture.Task("task-localization.yml", 4);
        task.VisualStimulus.StartAzimuthsDeg = new List<double> { -60, 0 };
        StringAssert.Contains(Refused(task), "visualStimulus.startAzimuthsDeg must not hold 0");
        StringAssert.Contains(Refused(With(Fixture.Condition("auditory", 1, "visualStimulus.startAzimuthsDeg", Fixture.List(60.0, 0.0)))),
            "trialConditions[0] (auditory): visualStimulus.startAzimuthsDeg must not hold 0");
    }

    [TestMethod]
    public void EveryListedAzimuthNeedsItsNearestSpeakerOnItsSide()
    {
        // 30 is nearest the centre speaker: refused at task level and in a condition, its mirror too.
        var task = Fixture.Task("task-localization.yml", 4);
        task.VisualStimulus.StartAzimuthsDeg = new List<double> { -60, 30 };
        StringAssert.Contains(Refused(task), "nearest azimuth 30 (elevation 15) is 'centre', not on the right");
        StringAssert.Contains(Refused(With(Fixture.Condition("visual", 1, "visualStimulus.startAzimuthsDeg", Fixture.List(60.0, 30.0)))),
            "trialConditions[0] (visual): rig.yml's speaker nearest azimuth -30");
    }

    [TestMethod]
    public void MoreThanTheCapOfCombinationsIsRefused()
    {
        var sizes = Fixture.List(1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0);
        var probabilities = Fixture.List(0.0, 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7);
        Assert.AreEqual("accepted", Refused(With(Fixture.Condition("visual", 1, "reward.rewardSizeUl", sizes, "autoRewardProbability", probabilities))), "64");
        probabilities.Add(0.8);
        StringAssert.Contains(Refused(With(Fixture.Condition("visual", 1, "reward.rewardSizeUl", sizes, "autoRewardProbability", probabilities))),
            "trialConditions[0] (visual) expands into 72 combinations, more than 64");
        Assert.AreEqual(64, TrialDraw.MaxCombinations);
    }

    [TestMethod]
    public void OnlyAConditionFieldOfTheTypeMayBeSetAndItsValuesAreChecked()
    {
        Func<TrialCondition, string> refusal = c => Refused(With(c));
        StringAssert.Contains(refusal(Fixture.Condition("visual", 1, "trialStructure.itiMinSeconds", 4.0)), "sets trialStructure.itiMinSeconds, which is not a condition field of visual");
        StringAssert.Contains(refusal(Fixture.Condition("auditory", 1, "contrasts", Fixture.List(1.0))), "sets contrasts, which is not a condition field of auditory");
        StringAssert.Contains(refusal(Fixture.Condition("neutral", 1, "visualStimulus.elevationDeg", 10.0)), "not a condition field of neutral");
        StringAssert.Contains(refusal(Fixture.Condition("visual", 1, "optogenetics.laserOnProbability", 1.0)), "sets optogenetics.laserOnProbability with optogenetics off");
        StringAssert.Contains(refusal(Fixture.Condition("visual", 1, "reward.rewardSizeUl", 99.0)), "trialConditions[0] (visual): reward.rewardSizeUl must be above 0 and at most");
        StringAssert.Contains(refusal(Fixture.Condition("visual", 1, "reward.rewardSizeUl", Fixture.List(2.0, 99.0))), "trialConditions[0] (visual) with reward.rewardSizeUl 99: reward.rewardSizeUl must be");
        StringAssert.Contains(refusal(Fixture.Condition("visual", 1, "contrasts", Fixture.List(0.0))), "trialConditions[0] (visual): contrasts must each be above 0");
    }

    [TestMethod]
    public void AConditionPinsTheLaser()
    {
        var task = With(Fixture.Condition("visual", 1, "optogenetics.laserOnProbability", 1.0), Fixture.Condition("auditory", 1, "optogenetics.laserOnProbability", 0.0));
        task.Optogenetics.Enabled = true;
        task.Optogenetics.LaserPowerMw = 3;
        task.Optogenetics.TrialsBeforeLaser = 0;
        task.Optogenetics.LaserDurationSeconds = 1;
        var trials = Fixture.Draw(task, 4, 200);
        Assert.IsTrue(trials.All(t => t.laserOn == (t.trialType == "visual")));
    }

    [TestMethod]
    public void TheConditionFieldsAreTheSchemas()
    {
        var schema = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(Fixture.Repo(), "src", "DataSchemas", "ucl_open_coen_dome_decision_making_task.json")));
        var fields = (Newtonsoft.Json.Linq.JObject)schema["$defs"]["UclOpenCoenDomeDecisionMakingTaskParameters"]["x-conditionFields"];
        CollectionAssert.AreEquivalent(TrialDraw.ConditionFields.Keys.ToList(), fields.Properties().Select(p => p.Name).ToList());
        foreach (var field in TrialDraw.ConditionFields) CollectionAssert.AreEqual(field.Value, fields[field.Key].ToObject<string[]>(), field.Key);
    }

    [TestMethod]
    public void TheConditionsSetRoundTrips()
    {
        // Sgen types set as object: read from YAML it is a map, written back as YAML or as JSON (Settings_Task) the same.
        var text = File.ReadAllText(Path.Combine(Fixture.Repo(), "config", "task-localization.yml")).Replace(
            "    - { type: visual, weight: 10, set: {} }",
            "    - { type: visual, weight: 10, set: { contrasts: [0.5, 1], reward.rewardSizeUl: [2, 3], visualStimulus.startAzimuthsDeg: [60] } }");
        var task = TrialDraw.Read(text).TaskParameters;
        var set = (IDictionary<object, object>)task.TrialConditions[0].Set;
        CollectionAssert.AreEqual(new object[] { "contrasts", "reward.rewardSizeUl", "visualStimulus.startAzimuthsDeg" }, set.Keys.ToArray());
        var again = TrialDraw.Read(Fixture.Text(task)).TaskParameters;
        StringAssert.Contains(Newtonsoft.Json.JsonConvert.SerializeObject(again.TrialConditions[0]),
            "\"set\":{\"contrasts\":[\"0.5\",\"1\"],\"reward.rewardSizeUl\":[\"2\",\"3\"],\"visualStimulus.startAzimuthsDeg\":[\"60\"]}");
        again.TrainingPipeline = new TrainingPipeline { Id = 4, Label = "x", Sha256 = "y" };
        var built = Fixture.Check(again, 4, Fixture.Speakers());
        CollectionAssert.AreEqual(new[] { 2.0, 3.0 }, built[0].Combinations.Select(v => v.Task.Reward.RewardSizeUl).ToArray());
        CollectionAssert.AreEqual(new[] { 0.5, 1.0 }, built[0].Combinations[0].Task.Contrasts);
        CollectionAssert.AreEqual(new[] { 60.0 }, built[0].Combinations[1].Task.VisualStimulus.StartAzimuthsDeg);
        Assert.AreEqual(1, built[1].Combinations.Count);
        Assert.AreEqual(3.0, built[1].Combinations[0].Task.Reward.RewardSizeUl);
    }

    [TestMethod]
    public void AValueOfTheWrongTypeIsRefusedNamingTheCondition()
    {
        StringAssert.Contains(Refused(With(Fixture.Condition("visual", 1, "reward.rewardSizeUl", "abc"))), "trialConditions[0] (visual)");
        StringAssert.Contains(Refused(With(Fixture.Condition("visual", 1, "autoRewardProbability", Fixture.List("x", 0.5)))), "trialConditions[0] (visual)");
        StringAssert.Contains(Refused(With(Fixture.Condition("visual", 1, "visualStimulus.startAzimuthsDeg", "left"))), "trialConditions[0] (visual)");
        StringAssert.Contains(Refused(With(Fixture.Condition("visual", 1, "contrasts", Fixture.List()))), "trialConditions[0] (visual)");
    }

    [TestMethod]
    public void OneConditionAZeroAmongOthersAndTheWeightCapAreDrawnAsWritten()
    {
        var one = Fixture.Draw(With(Fixture.Condition("neutral", 1)), 4, 30);
        Assert.IsTrue(one.All(t => t.trialCondition == 0 && t.trialType == "neutral"));
        // Weight 0 among others: its index is never logged, though its set is still checked at Go.
        var zero = With(Fixture.Condition("visual", 0), Fixture.Condition("auditory", 2), Fixture.Condition("neutral", 1));
        Assert.IsTrue(Fixture.Draw(zero, 4, 300).All(t => t.trialCondition != 0));
        StringAssert.Contains(Refused(With(Fixture.Condition("visual", 0, "contrasts", Fixture.List(0.0)), Fixture.Condition("auditory", 1))), "trialConditions[0] (visual)");
        Assert.AreEqual("accepted", Refused(With(Fixture.Condition("visual", TrialDraw.MaxWeight), Fixture.Condition("auditory", TrialDraw.MaxWeight))));
        Assert.IsTrue(Fixture.Draw(With(Fixture.Condition("visual", TrialDraw.MaxWeight), Fixture.Condition("auditory", TrialDraw.MaxWeight)), 4, 40).Any(t => t.trialCondition == 1));
    }

    [TestMethod]
    public void ManyConditionsAreEachDrawnAndTheirWeightsSumWithoutOverflow()
    {
        var many = Enumerable.Range(0, 120).Select(i => Fixture.Condition(i % 2 == 0 ? "visual" : "auditory", 1, "reward.rewardSizeUl", 1.0 + i % 3)).ToArray();
        var trials = Fixture.Draw(With(many), 4, 3000);
        Assert.IsTrue(trials.All(t => t.trialType == (t.trialCondition % 2 == 0 ? "visual" : "auditory") && t.rewardOfferedUl == 1.0 + t.trialCondition % 3));
        Assert.AreEqual(120, trials.Select(t => t.trialCondition).Distinct().Count());
        // 2200 conditions of the cap's weight sum past int, which the draw cannot take.
        var heavy = Enumerable.Range(0, 2200).Select(i => Fixture.Condition("visual", TrialDraw.MaxWeight)).ToArray();
        StringAssert.Contains(Refused(With(heavy)), "weights add to more than 2147483647");
    }
}
