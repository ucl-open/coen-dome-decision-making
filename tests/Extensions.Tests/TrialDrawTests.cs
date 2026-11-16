using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using OpenRigs.Devices;
using OpenRigs.Devices.Rigs;
using UclOpenCoenDomeDecisionMakingTask;
using YamlDotNet.Serialization;

static class Fixture
{
    public static string Repo([CallerFilePath] string here = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here), "..", ".."));
    }

    public static UclOpenCoenDomeDecisionMakingTaskParameters Task(string file)
    {
        var text = File.ReadAllText(Path.Combine(Repo(), "config", file));
        var task = new DeserializerBuilder().Build().Deserialize<UclOpenCoenDomeDecisionMakingTaskLogic>(text).TaskParameters;
        task.RngSeed = 1;
        return task;
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
        var draw = new TrialDraw(task, Speakers(), stage, path => 2);
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
        var trials = Fixture.Draw(Fixture.Task("task-localization.yml"), 4, 500);
        foreach (var t in trials)
        {
            Assert.IsTrue(t.audioEnabled && t.spoutMovements, t.trialType);
            if (t.trialType == "visual" || t.trialType == "audiovisualConflict" || t.trialType == "audiovisualCoherent")
                Assert.AreEqual(t.visualAzimuthDeg > 0 ? "Right" : "Left", t.target, t.trialType);
            if (t.trialType == "auditory" || t.trialType == "audiovisualCoherent")
                Assert.AreEqual(t.audioAzimuthDeg > 0 ? "Right" : "Left", t.target, t.trialType);
            if (t.trialType == "audiovisualConflict")
                Assert.AreEqual(t.visualAzimuthDeg > 0, t.audioAzimuthDeg < 0);
            if (t.trialType == "visual" || t.trialType == "neutral")
                Assert.AreEqual(2, t.speakerChannel, "the centre speaker");
        }
        CollectionAssert.AreEquivalent(TrialDraw.TypeNames, trials.Select(t => t.trialType).Distinct().ToArray());
    }

    [TestMethod]
    public void EachSoundPlaysOnTheNearestSpeaker()
    {
        foreach (var t in Fixture.Draw(Fixture.Task("task-localization.yml"), 4, 300))
        {
            var speaker = Fixture.Speakers().Speakers.Values.Single(s => s.Channel == t.speakerChannel);
            Assert.AreEqual(speaker.Position.Azimuth, t.audioAzimuthDeg);
            Assert.AreEqual(speaker.Position.Elevation, t.audioElevationDeg);
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
            var task = Fixture.Task("task-discrimination-left-right.yml");
            task.LeftModality = modality;
            foreach (var t in Fixture.Draw(task, 3, 500))
            {
                if (t.trialType == "visual") Assert.IsTrue(!t.audioEnabled && t.visualContrast > 0 && t.target == (modality == Modality.Visual ? "Left" : "Right"));
                if (t.trialType == "auditory") Assert.IsTrue(t.audioEnabled && t.visualContrast == 0 && t.target == (modality == Modality.Auditory ? "Left" : "Right"));
                if (t.trialType == "neutral") Assert.IsTrue(!t.audioEnabled && double.IsNaN(t.audioAzimuthDeg) && t.speakerChannel == -1);
            }
        }
    }

    [TestMethod]
    public void DiscriminationHabituationAutoRewardsAtTheLeftSpoutWithTheSpoutsIn()
    {
        var task = Fixture.Task("task-discrimination-left-right.yml");
        Assert.IsTrue(Fixture.Draw(task, 0, 50).All(t => t.autoReward && t.target == "Left" && !t.spoutMovements && t.trialType == "visual"));
        task.TrainingStages[2].AutoRewardProbability = 1;
        Assert.IsTrue(Fixture.Draw(task, 2, 300).All(t => t.autoReward), "left-right: either spout's answer may be auto-rewarded");
    }

    [TestMethod]
    public void GoNoGoAnswersTheModalityAtTheLeftSpout()
    {
        foreach (var modality in new[] { Modality.Visual, Modality.Auditory })
        {
            var task = Fixture.Task("task-discrimination-go-no-go.yml");
            task.GoModality = modality;
            task.TrainingStages[3].AutoRewardProbability = 1;
            foreach (var t in Fixture.Draw(task, 3, 600))
            {
                var expected = t.trialType == "audiovisualCoherent" || t.trialType == "audiovisualConflict" || t.trialType == "neutral" ? Answer.Probe
                    : (t.trialType == "visual" && modality == Modality.Visual) || (t.trialType == "auditory" && modality == Modality.Auditory) ? Answer.Go
                    : Answer.NoGo;   // the other modality
                Assert.AreEqual(expected, t.target, t.trialType);
                Assert.AreEqual(expected == Answer.Go, t.autoReward, "auto-reward only on Go");
                Assert.AreEqual(expected == Answer.Go, t.rewardSizeUl > 0, "a reward on offer only on Go");
                Assert.IsFalse(t.bothSpouts);
                Assert.AreEqual(LickSpoutCommand.LeftIn, SpoutsIn.Command(t));
            }
        }
    }

    [TestMethod]
    public void GoNoGoRefusesBothSpouts()
    {
        var task = Fixture.Task("task-discrimination-go-no-go.yml");
        task.TrainingStages[2].BothSpoutsProbability = 0.5;
        StringAssert.Contains(Fixture.Refusal(() => TrialDraw.CheckTask(task, 2, Fixture.Speakers())), "bothSpoutsProbability 0 in discriminationGoNoGo");
    }

    [TestMethod]
    public void TheWeightsAreByName()
    {
        var task = Fixture.Task("task-localization.yml");
        var w = task.TrainingStages[0].TrialWeights;
        Assert.AreEqual(0, w.Visual + w.Auditory + w.AudiovisualConflict + w.Neutral);
        Assert.IsTrue(Fixture.Draw(task, 0, 100).All(t => t.trialType == "audiovisualCoherent"));
    }

    [TestMethod]
    public void EachTrialTypeIsDrawnAtRandomByWeight()
    {
        // Stage 3: visual 10, auditory 10, coherent 5, conflict 5, neutral 1, each new trial drawn independently.
        var trials = Fixture.Draw(Fixture.Task("task-discrimination-left-right.yml"), 3, 6200);
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
        var task = Fixture.Task("task-localization.yml");
        task.RngSeed = 7;
        var trials = Fixture.Draw(task, 4, 400, t => "Incorrect");
        for (int i = 1; i < trials.Count; i++)
        {
            var before = trials[i - 1];
            bool repeats = before.repeatCount < task.Repeats.MaxRepeats &&
                (before.trialType == "visual" || before.trialType == "auditory" || before.trialType == "audiovisualCoherent");
            Assert.AreEqual(repeats ? before.repeatCount + 1 : 0, trials[i].repeatCount, before.trialType);
            if (repeats) Assert.AreEqual(before.visualAzimuthDeg, trials[i].visualAzimuthDeg);
        }
        Assert.IsTrue(trials.Any(t => t.repeatCount == task.Repeats.MaxRepeats));
        Assert.IsTrue(Fixture.Draw(task, 4, 100, t => "NoResponse").Skip(1).Any(t => t.repeatCount > 0 && (t.trialType == "neutral" || t.trialType == "audiovisualConflict")));
    }

    [TestMethod]
    public void OnNoResponseRepeatsAfterANoResponseAlone()
    {
        // onNoResponse names its outcome (16 Nov 2026): a NoResponse repeats the trial, an Incorrect never does.
        var task = Fixture.Task("task-localization.yml");
        task.RngSeed = 7;
        var r = task.Repeats.RulesPerType;
        r.Visual = r.Auditory = r.AudiovisualCoherent = r.AudiovisualConflict = r.Neutral = RepeatRule.OnNoResponse;
        Assert.IsTrue(Fixture.Draw(task, 4, 100, t => "Incorrect").All(t => t.repeatCount == 0));
        Assert.IsTrue(Fixture.Draw(task, 4, 100, t => "NoResponse").Any(t => t.repeatCount > 0));
    }

    [TestMethod]
    public void EachTrialDrawsItsOwnInterval()
    {
        var task = Fixture.Task("task-discrimination-left-right.yml");
        var trials = Fixture.Draw(task, 3, 300, t => t.trialNumber % 3 == 0 ? "Incorrect" : "Correct");
        for (int i = 0; i < trials.Count; i++) Assert.AreEqual(i + 1, trials[i].trialNumber);
        // What Gerion's mice got (Pip, 13 Nov 2026): 1.5 to 2.0 s from the outcome (after any timeout) to the stimulus.
        Assert.IsTrue(trials.All(t => t.itiSeconds >= 1.5 && t.itiSeconds <= 2.0));
        Assert.IsTrue(trials.Max(t => t.itiSeconds) > 1.95 && trials.Min(t => t.itiSeconds) < 1.55);
        var repeats = Fixture.Draw(Fixture.Task("task-localization.yml"), 4, 300, t => "Incorrect").Where(t => t.repeatCount > 0).ToList();
        Assert.IsTrue(repeats.Count > 10 && repeats.Select(t => t.itiSeconds).Distinct().Count() == repeats.Count, "a repeat draws its own");
    }

    [TestMethod]
    public void TheDiscriminationFileIsWhatGerionsMiceGot()
    {
        var s = Fixture.Task("task-discrimination-left-right.yml");
        Assert.AreEqual(1.5, s.TrialStructure.ItiMinSeconds);
        Assert.AreEqual(2.0, s.TrialStructure.ItiMaxSeconds);
        Assert.AreEqual(1.5, s.TrialStructure.DrinkSeconds);
        Assert.AreEqual(4.0, s.Penalty.TimeoutSeconds);   // incorrect or a miss: 5.5 to 6.0 s outcome to stimulus
    }

    [TestMethod]
    public void OptogeneticsSendsAConditionOnEveryTrial()
    {
        var task = Fixture.Task("task-localization.yml");
        task.Optogenetics.UseZapit = true;
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
        Assert.IsTrue(Fixture.Draw(Fixture.Task("task-discrimination-left-right.yml"), 3, 100).All(t => t.laserCondition == 0 && !t.laserOn && t.laserPowerMw == 0));
    }

    [TestMethod]
    public void ARepeatOfTheSameSeedDrawsTheSameTrials()
    {
        var a = Fixture.Draw(Fixture.Task("task-localization.yml"), 4, 50);
        var b = Fixture.Draw(Fixture.Task("task-localization.yml"), 4, 50);
        CollectionAssert.AreEqual(a.Select(t => t.visualPhaseRad).ToList(), b.Select(t => t.visualPhaseRad).ToList());
    }

    [TestMethod]
    public void TheDrawRefusesATaskItCannotRun()
    {
        var speakers = Fixture.Speakers();
        Func<UclOpenCoenDomeDecisionMakingTaskParameters, int, string> refusal = (parameters, stage) =>
            Fixture.Refusal(() => new TrialDraw(parameters, speakers, stage, path => 2));
        StringAssert.Contains(refusal(Fixture.Task("task-localization.yml"), 5), "rows 0 to 4");
        StringAssert.Contains(refusal(Fixture.Task("task-localization.yml"), -1), "rows 0 to 4");

        var task = Fixture.Task("task-localization.yml");
        task.TrainingStages[4].Contrasts.Clear();
        StringAssert.Contains(refusal(task, 4), "needs contrasts");

        task = Fixture.Task("task-localization.yml");
        task.AudioStimulus.SpeakerLocations.RemoveAll(l => l.Azimuth > 0);
        StringAssert.Contains(refusal(task, 4), "none on the right");

        task = Fixture.Task("task-localization.yml");
        task.VisualStimulus.FixedAzimuthsDeg = new List<double> { -60 };
        StringAssert.Contains(refusal(task, 4), "no right azimuth");

        task = Fixture.Task("task-localization.yml");
        task.TrialStructure.StimulusSeconds = 2;
        StringAssert.Contains(refusal(task, 4), "stimulusSeconds");

        task = Fixture.Task("task-localization.yml");
        task.TrialStructure.ResponseWindowSeconds = 5;
        task.TrialStructure.StimulusSeconds = task.TrialStructure.ItiMinSeconds + 0.5;
        StringAssert.Contains(refusal(task, 4), "longer than itiMinSeconds");

        task = Fixture.Task("task-localization.yml");
        speakers.Speakers.Remove("right");
        StringAssert.Contains(refusal(task, 4), "is 'centre', not on the right");

        task = Fixture.Task("task-discrimination-left-right.yml");
        task.Optogenetics.UseZapit = true;
        task.Optogenetics.LaserPowerMw = null;
        task.Optogenetics.LaserDurationSeconds = 0.8;
        StringAssert.Contains(Fixture.Refusal(() => new TrialDraw(task, Fixture.Speakers(), 4, path => 2)), "laserPowerMw");
    }

    [TestMethod]
    public void TheStageIsCheckedAtGoBeforeTheFirstTrial()
    {
        StringAssert.Contains(Fixture.Refusal(() => TrialDraw.CheckTask(Fixture.Task("task-localization.yml"), 5, Fixture.Speakers())), "rows 0 to 4");
        Assert.AreEqual("Stage 4", TrialDraw.CheckTask(Fixture.Task("task-discrimination-left-right.yml"), 4, Fixture.Speakers()).Label);
    }

    [TestMethod]
    public void AnAuditoryTargetOnAVisualOnlyStageIsRefusedWithTheFix()
    {
        // The discriminations' stages 0 and 1 are visual only: with leftModality auditory every trial would be a right one.
        var task = Fixture.Task("task-discrimination-left-right.yml");
        task.LeftModality = Modality.Auditory;
        var refusal = Fixture.Refusal(() => TrialDraw.CheckTask(task, 0, Fixture.Speakers()));
        StringAssert.Contains(refusal, "leftModality auditory no trials");
        var goNoGo = Fixture.Task("task-discrimination-go-no-go.yml");
        goNoGo.GoModality = Modality.Auditory;
        StringAssert.Contains(Fixture.Refusal(() => TrialDraw.CheckTask(goNoGo, 0, Fixture.Speakers())), "goModality auditory no trials");
        StringAssert.Contains(refusal, "swap the auditory and visual weights on the form");
        var w = task.TrainingStages[0].TrialWeights;
        w.Auditory = w.Visual;
        w.Visual = 0;
        Assert.IsTrue(Fixture.Draw(task, 0, 50).All(t => t.trialType == "auditory" && t.target == "Left"));
        Assert.AreEqual("accepted", Fixture.Refusal(() => TrialDraw.CheckTask(Fixture.Task("task-localization.yml"), 0, Fixture.Speakers())));
    }

    [TestMethod]
    public void ALaserThatWouldDelayTheNextArmIsRefused()
    {
        // Discrimination: 0 open loop + 1.5 s interval - 0.5 s ramp-down - 0.2 s reply margin = 0.8 s.
        var task = Fixture.Task("task-discrimination-left-right.yml");
        task.Optogenetics.UseZapit = true;
        task.Optogenetics.LaserPowerMw = 3;
        Assert.AreEqual(0.8, TrialDraw.LongestLaserSeconds(task), 1e-9);
        StringAssert.Contains(Fixture.Refusal(() => TrialDraw.CheckTask(task, 4, Fixture.Speakers())), "laserDurationSeconds (2.5 s) must be at most 0.8 s");
        task.Optogenetics.LaserDurationSeconds = 0.8;
        Assert.AreEqual("accepted", Fixture.Refusal(() => TrialDraw.CheckTask(task, 4, Fixture.Speakers())));
        task.Optogenetics.LaserDurationSeconds = 2.5;
        task.Optogenetics.UseZapit = false;
        Assert.AreEqual("accepted", Fixture.Refusal(() => TrialDraw.CheckTask(task, 4, Fixture.Speakers())));
    }

    [TestMethod]
    public void ALargeRewardOverTheCapIsRefusedAtGo()
    {
        // DeliverReward refuses a reward over its MaxRewardUl (lib's MAX_REWARD_UL) mid-session; Go refuses it first.
        var task = Fixture.Task("task-localization.yml");
        task.Reward.LargeRewardProbability = 0.2;
        task.Reward.LargeRewardMultiplier = 3;
        task.TrainingStages[4].RewardSizeUl = 8;
        var refusal = Fixture.Refusal(() => TrialDraw.CheckTask(task, 4, Fixture.Speakers()));
        StringAssert.Contains(refusal, "stage '" + task.TrainingStages[4].Label + "' rewardSizeUl (8 ul) x reward.largeRewardMultiplier (3) is 24 ul, more than 20 ul");
        task.TrainingStages[4].RewardSizeUl = OpenRigs.Core.LoadRewardLine.DefaultMaxRewardUl / 3;
        Assert.AreEqual("accepted", Fixture.Refusal(() => TrialDraw.CheckTask(task, 4, Fixture.Speakers())));
        task.TrainingStages[4].RewardSizeUl = 8;
        task.Reward.LargeRewardProbability = 0;
        Assert.AreEqual("accepted", Fixture.Refusal(() => TrialDraw.CheckTask(task, 4, Fixture.Speakers())));
    }

    [TestMethod]
    public void TheSignedLeadIsRefusedOutsideTheIntervalAndTheResponseWindow()
    {
        Func<double, string> refusal = lead =>
        {
            var task = Fixture.Task("task-discrimination-left-right.yml");   // itiMinSeconds 1.5, response window 0 + 1.5 s
            task.Optogenetics.LaserToStimulusSeconds = lead;
            return Fixture.Refusal(() => TrialDraw.CheckTask(task, 4, Fixture.Speakers()));
        };
        foreach (var ok in new[] { 1.3, 0.5, 0.0, -0.5, -1.5 }) Assert.AreEqual("accepted", refusal(ok), ok.ToString());
        StringAssert.Contains(refusal(1.31), "reply margin must be at most trialStructure.itiMinSeconds");
        StringAssert.Contains(refusal(-1.51), "after the response window");
    }

    [TestMethod]
    public void TheQuietWaitsAreRefusedNegativeOrHalfSet()
    {
        var task = Fixture.Task("task-localization.yml");
        task.TrainingStages[4].WheelQuietSeconds = 1;
        StringAssert.Contains(Fixture.Refusal(() => TrialDraw.CheckTask(task, 4, Fixture.Speakers())), "are both above 0 (the wheel's quiet wait) or both 0 (off)");
        task.TrainingStages[4].WheelQuietCm = 2;
        Assert.AreEqual("accepted", Fixture.Refusal(() => TrialDraw.CheckTask(task, 4, Fixture.Speakers())));
        task.TrainingStages[4].LickQuietSeconds = -1;
        StringAssert.Contains(Fixture.Refusal(() => TrialDraw.CheckTask(task, 4, Fixture.Speakers())), "lickQuietSeconds is -1, not from 0 to 10 s");
        // A NaN or infinite time would hold the trial start for ever, with no error.
        task.TrainingStages[4].LickQuietSeconds = double.PositiveInfinity;
        StringAssert.Contains(Fixture.Refusal(() => TrialDraw.CheckTask(task, 4, Fixture.Speakers())), "lickQuietSeconds is ");
        task = Fixture.Task("task-localization.yml");
        task.TrainingStages[4].WheelQuietSeconds = double.NaN;
        StringAssert.Contains(Fixture.Refusal(() => TrialDraw.CheckTask(task, 4, Fixture.Speakers())), "wheelQuietSeconds is NaN, not from 0 to 10 s");
        task = Fixture.Task("task-localization.yml");
        task.TrialStructure.ItiMaxSeconds = double.PositiveInfinity;
        StringAssert.Contains(Fixture.Refusal(() => TrialDraw.CheckTask(task, 4, Fixture.Speakers())), "itiMaxSeconds must be a finite number, 0 or more");
        task = Fixture.Task("task-localization.yml");
        task.Penalty.TimeoutSeconds = double.NaN;
        task.Optogenetics.LaserToStimulusSeconds = double.NaN;
        var refusal = Fixture.Refusal(() => TrialDraw.CheckTask(task, 4, Fixture.Speakers()));
        StringAssert.Contains(refusal, "penalty.timeoutSeconds must be a finite number, 0 or more");
        StringAssert.Contains(refusal, "laserToStimulusSeconds must be a finite number");
        task = Fixture.Task("task-localization.yml");
        task.TrialStructure.DrinkSeconds = 3.5;
        StringAssert.Contains(Fixture.Refusal(() => TrialDraw.CheckTask(task, 4, Fixture.Speakers())), "below drinkSeconds");
    }

    [TestMethod]
    public void AQuietWaitAboveItsMaximumIsRefusedAtGo()
    {
        var cases = new Dictionary<string, Action<TrainingStage>>
        {
            { "lickQuietSeconds is 10.5, not from 0 to 10 s", s => s.LickQuietSeconds = 10.5 },
            { "wheelQuietSeconds is 11, not from 0 to 10 s", s => { s.WheelQuietSeconds = 11; s.WheelQuietCm = 1; } },
            { "wheelQuietCm is 21, not from 0 to 20 cm", s => { s.WheelQuietSeconds = 1; s.WheelQuietCm = 21; } },
        };
        foreach (var c in cases)
        {
            var task = Fixture.Task("task-localization.yml");
            c.Value(task.TrainingStages[4]);
            StringAssert.Contains(Fixture.Refusal(() => TrialDraw.CheckTask(task, 4, Fixture.Speakers())), c.Key);
        }
        var atMax = Fixture.Task("task-localization.yml");
        atMax.TrainingStages[4].LickQuietSeconds = 10;
        atMax.TrainingStages[4].WheelQuietSeconds = 10;
        atMax.TrainingStages[4].WheelQuietCm = 20;
        Assert.AreEqual("accepted", Fixture.Refusal(() => TrialDraw.CheckTask(atMax, 4, Fixture.Speakers())));
    }

    [TestMethod]
    public void AStageWithNoWeightANegativeWeightOrAReversedIntervalIsRefused()
    {
        // Bonsai reads the task file without pydantic: a stage of zero weights would draw no type at the first trial.
        var task = Fixture.Task("task-localization.yml");
        var w = task.TrainingStages[4].TrialWeights;
        w.Visual = w.Auditory = w.AudiovisualCoherent = w.AudiovisualConflict = w.Neutral = 0;
        StringAssert.Contains(Fixture.Refusal(() => TrialDraw.CheckTask(task, 4, Fixture.Speakers())), "trialWeights of 0 or more, one above 0");
        task = Fixture.Task("task-localization.yml");
        task.TrainingStages[4].TrialWeights.Neutral = -1;
        StringAssert.Contains(Fixture.Refusal(() => TrialDraw.CheckTask(task, 4, Fixture.Speakers())), "trialWeights of 0 or more, one above 0");
        task = Fixture.Task("task-localization.yml");
        task.TrialStructure.ItiMaxSeconds = task.TrialStructure.ItiMinSeconds - 0.1;
        StringAssert.Contains(Fixture.Refusal(() => TrialDraw.CheckTask(task, 4, Fixture.Speakers())), "itiMaxSeconds is below itiMinSeconds");
    }
}
