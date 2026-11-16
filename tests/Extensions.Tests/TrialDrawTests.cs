using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
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
        var end = TrialEnd.First(99);
        for (int i = 0; i < count; i++)
        {
            var trial = draw.Next(end);
            trials.Add(trial);
            end = new TrialEnd { Outcome = outcome == null ? "Correct" : outcome(trial), ItiStartTime = 100 + i };
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
            var task = Fixture.Task("task-discrimination.yml");
            task.TargetModality = modality;
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
        var task = Fixture.Task("task-discrimination.yml");
        Assert.IsTrue(Fixture.Draw(task, 0, 50).All(t => t.autoReward && t.target == "Left" && !t.spoutMovements && t.trialType == "visual"));
        task.TrainingStages[2].AutoRewardProbability = 1;
        Assert.IsTrue(Fixture.Draw(task, 2, 300).All(t => t.autoReward == (t.target == "Left")));
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
        var trials = Fixture.Draw(Fixture.Task("task-discrimination.yml"), 3, 6200);
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
    public void EachTrialDrawsItsOwnIntervalFromTheEndItIsDrawnAt()
    {
        var task = Fixture.Task("task-discrimination.yml");
        var trials = Fixture.Draw(task, 3, 300, t => t.trialNumber % 3 == 0 ? "Incorrect" : "Correct");
        for (int i = 0; i < trials.Count; i++)
        {
            Assert.AreEqual(99 + i, trials[i].itiStartTime);   // trial 1: the first draw (StartTrials)
            Assert.AreEqual(i + 1, trials[i].trialNumber);
        }
        // What Gerion's mice got (Pip, 13 Nov 2026): 1.5 to 2.0 s from the outcome (after any timeout) to the stimulus.
        Assert.IsTrue(trials.All(t => t.itiSeconds >= 1.5 && t.itiSeconds <= 2.0));
        Assert.IsTrue(trials.Max(t => t.itiSeconds) > 1.95 && trials.Min(t => t.itiSeconds) < 1.55);
        var repeats = Fixture.Draw(Fixture.Task("task-localization.yml"), 4, 300, t => "Incorrect").Where(t => t.repeatCount > 0).ToList();
        Assert.IsTrue(repeats.Count > 10 && repeats.Select(t => t.itiSeconds).Distinct().Count() == repeats.Count, "a repeat draws its own");
        Assert.IsTrue(trials.All(t => t.leadSeconds == 0.5 && t.triggerAfterStartSeconds == 0 && double.IsNaN(t.startTime)));
    }

    [TestMethod]
    public void TheDiscriminationFileIsWhatGerionsMiceGot()
    {
        var s = Fixture.Task("task-discrimination.yml");
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
        Assert.IsTrue(Fixture.Draw(Fixture.Task("task-discrimination.yml"), 3, 100).All(t => t.laserCondition == 0 && !t.laserOn && t.laserPowerMw == 0));
    }

    [TestMethod]
    public void ARepeatOfTheSameSeedDrawsTheSameTrials()
    {
        var a = Fixture.Draw(Fixture.Task("task-localization.yml"), 4, 50);
        var b = Fixture.Draw(Fixture.Task("task-localization.yml"), 4, 50);
        CollectionAssert.AreEqual(a.Select(t => t.visualPhaseRad).ToList(), b.Select(t => t.visualPhaseRad).ToList());
    }

    [TestMethod]
    public void TheTaskIsRefusedAtTheFirstTrial()
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
        speakers.Speakers.Remove("right");
        StringAssert.Contains(refusal(task, 4), "is 'centre', not on the right");

        task = Fixture.Task("task-discrimination.yml");
        task.Optogenetics.UseZapit = true;
        task.Optogenetics.LaserPowerMw = null;
        task.Optogenetics.LaserDurationSeconds = 0.8;
        StringAssert.Contains(Fixture.Refusal(() => new TrialDraw(task, Fixture.Speakers(), 4, path => 2)), "laserPowerMw");
    }

    [TestMethod]
    public void TheStageIsCheckedAtGoBeforeTheFirstTrial()
    {
        StringAssert.Contains(Fixture.Refusal(() => TrialDraw.CheckTask(Fixture.Task("task-localization.yml"), 5, Fixture.Speakers())), "rows 0 to 4");
        Assert.AreEqual("Stage 4", TrialDraw.CheckTask(Fixture.Task("task-discrimination.yml"), 4, Fixture.Speakers()).Label);
    }

    [TestMethod]
    public void AnAuditoryTargetOnAVisualOnlyStageIsRefusedWithTheFix()
    {
        // Discrimination's stages 0 and 1 are visual only: with targetModality auditory every trial would be a distractor.
        var task = Fixture.Task("task-discrimination.yml");
        task.TargetModality = Modality.Auditory;
        var refusal = Fixture.Refusal(() => TrialDraw.CheckTask(task, 0, Fixture.Speakers()));
        StringAssert.Contains(refusal, "targetModality auditory no trials");
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
        var task = Fixture.Task("task-discrimination.yml");
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
            var task = Fixture.Task("task-discrimination.yml");   // itiMinSeconds 1.5, response window 0 + 1.5 s
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

/// <summary>The trial start and the stimulus and trigger timed from it (Pip, 13 Nov 2026), on a stand-in Harp clock of 1 ms ticks.</summary>
[TestClass]
public class TrialStartTests
{
    static Trial Trial(double lead, double lick = 0, double wheelSeconds = 0, double wheelCm = 0)
    {
        return new Trial { trialNumber = 1, itiStartTime = 10, itiSeconds = 2, leadSeconds = Math.Max(lead, 0), triggerAfterStartSeconds = Math.Max(-lead, 0),
                           lickQuietSeconds = lick, wheelQuietSeconds = wheelSeconds, wheelQuietCm = wheelCm };
    }

    // Ticks the clock from 10 s; each step's action runs at its time. Returns the trial at its start.
    static Trial Run(Trial trial, Dictionary<double, Action<TrialStartWaiter>> steps = null)
    {
        var waiter = new TrialStartWaiter();
        waiter.Wheel(0);
        waiter.Clock(10);
        waiter.Start(trial);
        for (int ms = 10001; ms < 30000; ms++)
        {
            var now = ms / 1000.0;
            var started = waiter.Clock(now);
            if (started != null) return started;
            Action<TrialStartWaiter> step;
            if (steps != null && steps.TryGetValue(now, out step)) step(waiter);   // a lick or wheel sample stamped with this tick
        }
        throw new AssertFailedException("no trial start");
    }

    [TestMethod]
    public void WithTheWaitsOffTheStimulusIsExactlyTheIntervalAfterItsStart()
    {
        foreach (var lead in new[] { 0.5, 0.0, -0.5 })
        {
            var started = Run(Trial(lead));
            Assert.AreEqual(12 - Math.Max(lead, 0), started.startTime, 1e-12);
            Assert.AreEqual(0, started.quietWaitSeconds, 1e-12);
            Assert.AreEqual(12, started.startTime + started.leadSeconds, 1e-12, "the stimulus: no time added by Zapit or the waits");
            Assert.AreEqual(12, SecondsUntil.Seconds(started, TrialMoment.Stimulus, started.startTime) + started.startTime, 1e-12);
            // The trigger: stimulus - laserToStimulusSeconds, before, with or after it.
            Assert.AreEqual(12 - lead, SecondsUntil.Seconds(started, TrialMoment.LaserTrigger, started.startTime) + started.startTime, 1e-12);
        }
    }

    [TestMethod]
    public void ALickRestartsTheLickWaitAndTheLeadStaysExact()
    {
        var steps = new Dictionary<double, Action<TrialStartWaiter>> { { 11.0, w => w.Lick(0, true) }, { 11.2, w => w.Lick(0, false) } };
        var quiet = Run(Trial(0.5, 1.0));                // quiet from 10 s: the start at 11.5, the interval's end less the lead
        Assert.AreEqual(11.5, quiet.startTime, 1e-12);
        var licked = Run(Trial(0.5, 1.0), steps);         // touched at 11.0, released at 11.2: the start a second after the release
        Assert.AreEqual(12.2, licked.startTime, 1e-9);
        Assert.AreEqual(0.7, licked.quietWaitSeconds, 1e-9);
        Assert.AreEqual(12.7, licked.startTime + licked.leadSeconds, 1e-9);
        steps[11.4] = w => w.Lick(1, true);               // a later lick on the other spout restarts it again
        steps[11.45] = w => w.Lick(1, false);
        Assert.AreEqual(12.45, Run(Trial(0.5, 1.0), steps).startTime, 1e-9);
    }

    [TestMethod]
    public void AHeldContactHoldsTheTrialStartUntilReleased()
    {
        // A paw on the left spout from 10.5 s to 13 s: no start while it is held, then a second after the release.
        var steps = new Dictionary<double, Action<TrialStartWaiter>> { { 10.5, w => w.Lick(0, true) }, { 13.0, w => w.Lick(0, false) } };
        Assert.AreEqual(14.0, Run(Trial(0.5, 1.0), steps).startTime, 1e-9);
        // With the lick wait off, a held contact does not matter.
        Assert.AreEqual(11.5, Run(Trial(0.5), steps).startTime, 1e-9);
    }

    [TestMethod]
    public void TheWheelWaitCountsTheLastWheelQuietSeconds()
    {
        // Rocking forward and back 0.6 cm a time: net distance 0, 1.2 cm within a second, so the start a second after the first swing.
        var steps = new Dictionary<double, Action<TrialStartWaiter>> { { 11.0, w => w.Wheel(0.6) }, { 11.3, w => w.Wheel(0.0) } };
        var rocked = Run(Trial(0.5, 0, 1.0, 1.0), steps);
        Assert.AreEqual(12.0, rocked.startTime, 1e-9);
        Assert.AreEqual(0.5, rocked.quietWaitSeconds, 1e-9);
        steps.Remove(11.3);                               // 0.6 cm alone stays within 1 cm
        Assert.AreEqual(11.5, Run(Trial(0.5, 0, 1.0, 1.0), steps).startTime, 1e-9);
        // Each wait restarts on its own; the start needs both.
        steps[11.3] = w => w.Wheel(0.0);
        steps[11.8] = w => w.Lick(0, true);
        steps[11.85] = w => w.Lick(0, false);
        Assert.AreEqual(12.85, Run(Trial(0.5, 1.0, 1.0, 1.0), steps).startTime, 1e-9);
    }

    [TestMethod]
    public void SlowFidgetingWithinTheThresholdNeverDelaysTheTrialStart()
    {
        // 0.3 cm every 0.4 s through the interval: 1.2 cm in all, past 1 cm, but never more than 0.9 cm within a second.
        var steps = new Dictionary<double, Action<TrialStartWaiter>>();
        for (int i = 1; i <= 4; i++) { var cm = 0.3 * i; steps[Math.Round(9.8 + 0.4 * i, 3)] = w => w.Wheel(cm); }
        Assert.AreEqual(11.5, Run(Trial(0.5, 0, 1.0, 1.0), steps).startTime, 1e-9);
    }

    [TestMethod]
    public void ATrialAnsweredBeforeItsTriggerHasNoLaserAndItsRowSaysSo()
    {
        Func<Trial> started = () => new Trial { trialNumber = 5, startTime = 20, triggerAfterStartSeconds = 0.4, laserCondition = 1, laserOn = true, laserPowerMw = 3 };
        var trigger = new System.Reactive.Subjects.Subject<Bonsai.Harp.Timestamped<Trial>>();
        var passed = new List<Trial>();
        new UnlessAnswered().Process(trigger).Subscribe(t => passed.Add(t.Value));

        // The trigger first: the laser fires and the row, built after, logs the power.
        var triggered = started();
        trigger.OnNext(new Bonsai.Harp.Timestamped<Trial>(triggered, 20.4));
        Assert.AreEqual(1, passed.Count, "not answered: the laser fires");
        Assert.AreEqual(3, CreateTrialRow.Build("Left", 20.5, triggered, 20, 0, false).laserPowerMw);
        Assert.AreEqual(20.4, CreateTrialRow.Build("Left", 20.5, triggered, 20, 0, false).laserTriggerTime, "the trigger's Harp time");

        // The outcome first: the row logs 0 mW and the trigger after it is dropped, whatever the Harp times.
        var answered = started();
        Assert.AreEqual(0, CreateTrialRow.Build("Left", 20.45, answered, 20, 0, false).laserPowerMw);
        Assert.IsTrue(double.IsNaN(CreateTrialRow.Build("Left", 20.45, answered, 20, 0, false).laserTriggerTime), "no trigger: NaN");
        trigger.OnNext(new Bonsai.Harp.Timestamped<Trial>(answered, 20.4));
        Assert.AreEqual(1, passed.Count, "answered before its trigger arrived: no laser");
        Assert.IsTrue(double.IsNaN(CreateTrialRow.Build("Left", 20.45, answered, 20, 0, false).laserTriggerTime), "a trigger after the answer is not logged");

        // A repeat is a clone, and decides afresh.
        var repeat = triggered.Clone();
        Assert.AreEqual(0, CreateTrialRow.Build("Left", 20.3, repeat, 20, 0, false).laserPowerMw);
        Assert.IsTrue(double.IsNaN(repeat.laserTriggerTime), "a clone forgets the trigger");
        Assert.AreEqual(3, CreateTrialRow.Build("Left", 20.5, triggered, 20, 0, false).laserPowerMw);
    }

    [TestMethod]
    public void AMomentAtTheTrialStartComesAtOnceThoughItIsSeenOnTheNextTick()
    {
        // A drawn interval is never a whole number of ticks: the start (11.5004 s) is seen on the 11.501 s tick.
        foreach (var lead in new[] { 0.5, 0.0 })
        {
            var trial = Trial(lead);
            trial.itiSeconds = 2.0004;
            var started = Run(trial);
            Assert.AreEqual(11.5004 - lead + 0.5, started.startTime, 1e-9);
            var moment = lead > 0 ? TrialMoment.LaserTrigger : TrialMoment.Stimulus;
            Assert.AreEqual(0, SecondsUntil.Seconds(started, moment, Math.Ceiling(started.startTime * 1000) / 1000), 1e-12);
        }
    }

    [TestMethod]
    public void AMomentAlreadyPastStopsTheWorkflow()
    {
        var started = Run(Trial(-0.5));
        StringAssert.Contains(Fixture.Refusal(() => SecondsUntil.Seconds(started, TrialMoment.LaserTrigger, started.startTime + 0.6)), "laser trigger was due");
    }

    [TestMethod]
    public void TheTriggerPassesOnlyOnceZapitArmedIt()
    {
        var trigger = new System.Reactive.Subjects.Subject<Bonsai.Harp.Timestamped<Trial>>();
        var armed = new System.Reactive.Subjects.Subject<OpenRigs.Devices.Zapit.ZapitReply>();
        var passed = new List<Trial>();
        Exception error = null;
        new RequireZapitArmed { HardwareTriggered = true }.Process(trigger, armed).Subscribe(passed.Add, e => error = e);
        var trial = new Trial { trialNumber = 7 };
        armed.OnNext(new OpenRigs.Devices.Zapit.ZapitReply());
        trigger.OnNext(new Bonsai.Harp.Timestamped<Trial>(trial, 5));
        Assert.AreEqual(1, passed.Count);

        var late = new System.Reactive.Subjects.Subject<Bonsai.Harp.Timestamped<Trial>>();
        new RequireZapitArmed { HardwareTriggered = true }.Process(late, new System.Reactive.Subjects.Subject<OpenRigs.Devices.Zapit.ZapitReply>()).Subscribe(_ => { }, e => error = e);
        late.OnNext(new Bonsai.Harp.Timestamped<Trial>(trial, 5));
        StringAssert.Contains(error.Message, "had not answered trial 7's StartStimulus by its trigger");
    }

    [TestMethod]
    public void WithoutHardwareTriggeringTheCommandGoesAtTheTriggerAndItsReplyIsChecked()
    {
        // Zapit starts the laser when a command reaches it on such a rig: never at the interval's start.
        var trial = new System.Reactive.Subjects.Subject<Trial>();
        var trigger = new System.Reactive.Subjects.Subject<Trial>();
        var sent = new List<OpenRigs.Devices.Zapit.ZapitCommand>();
        new OpenRigs.Devices.Zapit.ZapitTrialCommand { HardwareTriggered = false, DurationSeconds = 1 }.Process(trial, trigger).Subscribe(sent.Add);
        var laser = new Trial { trialNumber = 7, laserCondition = 1, laserOn = true, laserPowerMw = 3 };
        trial.OnNext(laser);
        Assert.AreEqual(0, sent.Count, "nothing at the interval's start");
        trigger.OnNext(laser);
        Assert.AreEqual(1, sent.Count);
        Assert.IsFalse(sent[0].HardwareTriggered);

        var hardware = new List<OpenRigs.Devices.Zapit.ZapitCommand>();
        var trial2 = new System.Reactive.Subjects.Subject<Trial>();
        new OpenRigs.Devices.Zapit.ZapitTrialCommand { HardwareTriggered = true, DurationSeconds = 1 }.Process(trial2, trigger).Subscribe(hardware.Add);
        trial2.OnNext(laser);
        trigger.OnNext(laser);
        Assert.AreEqual(1, hardware.Count, "a hardware-triggered rig is armed as the laser begins, not at the trigger");

        // The trigger passes before any reply, and the laser ends only once the reply has come.
        var at = new System.Reactive.Subjects.Subject<Bonsai.Harp.Timestamped<Trial>>();
        var armed = new System.Reactive.Subjects.Subject<OpenRigs.Devices.Zapit.ZapitReply>();
        var passed = new List<Trial>();
        bool completed = false;
        new RequireZapitArmed { HardwareTriggered = false }.Process(at, armed).Subscribe(passed.Add, () => completed = true);
        at.OnNext(new Bonsai.Harp.Timestamped<Trial>(laser, 5));
        at.OnCompleted();
        Assert.AreEqual(1, passed.Count);
        Assert.IsFalse(completed, "waits for the reply to the command the trigger sent");
        armed.OnNext(new OpenRigs.Devices.Zapit.ZapitReply());
        Assert.IsTrue(completed);
    }

    [TestMethod]
    public void TheLaserWaitsForItsOwnTrialsStart()
    {
        var starts = new System.Reactive.Subjects.BehaviorSubject<Trial>(new Trial { trialNumber = 3 });
        var found = new List<Trial>();
        new TrialStartFor().Process(System.Reactive.Linq.Observable.Return(new Trial { trialNumber = 4 }), starts).Subscribe(found.Add);
        Assert.AreEqual(0, found.Count);
        starts.OnNext(new Trial { trialNumber = 4 });
        starts.OnNext(new Trial { trialNumber = 4 });
        Assert.AreEqual(1, found.Count);
    }

    [TestMethod]
    public void ATrialDrawnWhileAnotherWaitsStopsTheWorkflow()
    {
        var waiter = new TrialStartWaiter();
        waiter.Clock(10);
        waiter.Start(Trial(0.5));
        StringAssert.Contains(Fixture.Refusal(() => waiter.Start(new Trial { trialNumber = 2 })), "still waited");
    }
}

[TestClass]
public class ZapitTests
{
    static OpenRigs.Devices.Zapit.ZapitReply Reply(int condition, int laserOn, bool error)
    {
        var reply = new OpenRigs.Devices.Zapit.ZapitReply();
        reply.Command = OpenRigs.Devices.Zapit.ZapitCommandType.StartStimulus;
        reply.Value = condition;
        reply.LaserOn = laserOn;
        reply.IsError = error;
        return reply;
    }

    [TestMethod]
    public void TheTrialGoesOnOnlyOnceZapitArmedItAsAsked()
    {
        var trial = new Trial { trialNumber = 21, laserCondition = 2, laserOn = true };
        CheckZapitArmed.Check(Reply(2, 1, false), trial);
        // The refusal is OpenRigs.Devices' ZapitTrial.CheckArmed, naming the trial and Zapit's reply.
        StringAssert.Contains(Fixture.Refusal(() => CheckZapitArmed.Check(Reply(1, 1, false), trial)), "trial 21's StartStimulus (condition 2, laserOn 1)");
        StringAssert.Contains(Fixture.Refusal(() => CheckZapitArmed.Check(Reply(1, 1, false), trial)), "value=1");
        StringAssert.Contains(Fixture.Refusal(() => CheckZapitArmed.Check(Reply(2, 0, false), trial)), "laserOn=0");
        StringAssert.Contains(Fixture.Refusal(() => CheckZapitArmed.Check(Reply(2, 1, true), trial)), "error=True");
        trial.laserOn = false;
        CheckZapitArmed.Check(Reply(2, 0, false), trial);
    }
}

[TestClass]
public class TrialRowTests
{
    [TestMethod]
    public void TheRowSaysWhatTheOutcomeCost()
    {
        var trial = new Trial { target = "Left", autoReward = false, itiSeconds = 3, itiStartTime = 50, startTime = 8.5 };
        var incorrect = CreateTrialRow.Build("Right", 10, trial, 9, 4, true);
        Assert.IsTrue(incorrect.outcome == "Incorrect" && incorrect.penaltySound && incorrect.penaltyTimeoutSeconds == 4 && incorrect.outcomeTime == 10);
        Assert.IsTrue(incorrect.itiSeconds == 3 && incorrect.itiStartTime == 50 && incorrect.trialStartTime == 8.5 && incorrect.stimulusOnsetTime == 9);
        // A miss: the outcome at the end of the response window, and the penalty sound with its timeout (Pip, 14 Nov 2026).
        var miss = CreateTrialRow.Build("NoResponse", 10, trial, 9, 4, true);
        Assert.IsTrue(miss.outcome == "NoResponse" && miss.penaltySound && miss.penaltyTimeoutSeconds == 4 && miss.outcomeTime == 10 && !miss.autoRewarded);
        trial.autoReward = true;
        var rewardedMiss = CreateTrialRow.Build("NoResponse", 10, trial, 9, 4, true);
        Assert.IsTrue(rewardedMiss.autoRewarded && rewardedMiss.penaltyTimeoutSeconds == 0 && !rewardedMiss.penaltySound);
        var correct = CreateTrialRow.Build("Left", 10, trial, 9, 4, true);
        Assert.IsTrue(correct.outcome == "Correct" && !correct.penaltySound && correct.penaltyTimeoutSeconds == 0 && !correct.autoRewarded);
        Assert.IsFalse(CreateTrialRow.Build("Right", 10, trial, 9, 4, false).penaltySound);
    }

    [TestMethod]
    public void TheRowLogsTheConditionAndPowerSent()
    {
        var trial = new Trial { target = "Left", laserCondition = 2, laserOn = true, laserPowerMw = 3 };
        Assert.IsTrue(trial.FireLaser(9.25), "its trigger came first");
        var row = CreateTrialRow.Build("Left", 10, trial, 9, 4, true);
        Assert.IsTrue(row.laserCondition == 2 && row.laserPowerMw == 3);
        Assert.AreEqual(9.25, row.laserTriggerTime, "the trigger's Harp time");
        trial.laserOn = false;
        trial.laserPowerMw = 0;
        Assert.AreEqual(0, CreateTrialRow.Build("Left", 10, trial, 9, 4, true).laserPowerMw);
    }

    [TestMethod]
    public void TheRowsColumnsAreTheTrialColumnsFile()
    {
        var listed = File.ReadAllLines(Path.Combine(Fixture.Repo(), "schemas", "trial-columns.yml"))
            .Where(line => line.Length > 0 && char.IsLetter(line[0]) && line.EndsWith(":")).Select(line => line.TrimEnd(':')).ToList();
        // The lab's CsvWriter writes the row's timestamp first, as harpTime (scripts\check-workflow.ps1 holds the real header).
        var fields = new List<string> { "harpTime" };
        fields.AddRange(typeof(TrialRow).GetFields().Select(f => f.Name));
        CollectionAssert.AreEqual(listed, fields);
    }
}

[TestClass]
public class TrialEndTests
{
    [TestMethod]
    public void TheNextIntervalStartsExactlyAtTheOutcomePlusItsTimeout()
    {
        // The lick at 10.0123 s; the timeout's delay ends on a later tick, which must not move the interval's start.
        var trial = new Trial { target = "Left" };
        var c = Consequence.Of(CreateTrialRow.Build("Right", 10.0123, trial, 9, 4, true), trial);
        Assert.AreEqual(14.0123, c.ItiStartTime, 1e-12);
        var ends = new List<TrialEnd>();
        new EndTrial().Process(System.Reactive.Linq.Observable.Return(Bonsai.Harp.Timestamped.Create(c, 14.027))).Subscribe(ends.Add);
        Assert.AreEqual(1, ends.Count);
        Assert.AreEqual("Incorrect", ends[0].Outcome);
        Assert.AreEqual(14.0123, ends[0].ItiStartTime, 1e-12);
        var correct = Consequence.Of(CreateTrialRow.Build("Left", 20.5, trial, 19, 4, true), trial);
        Assert.AreEqual(20.5, correct.ItiStartTime, 1e-12, "no timeout: the outcome itself");
    }
}

[TestClass]
public class ConsequenceTests
{
    static Consequence Of(string choice, bool autoReward, bool movements)
    {
        var trial = new Trial { target = "Right", rewardSizeUl = 3, autoReward = autoReward, spoutMovements = movements };
        return Consequence.Of(CreateTrialRow.Build(choice, 1, trial, 0, 4, true), trial);
    }

    [TestMethod]
    public void ACorrectChoiceIsRewardedAtTheTargetWithTheOtherSpoutDrawnBack()
    {
        var c = Of("Right", false, true);
        Assert.IsTrue(c.Rewarded && c.Reward.Item1 == "right" && c.Reward.Item2 == 3 && c.SpoutCommand == OpenRigs.Devices.LickSpoutCommand.RightIn);
        Assert.IsTrue(c.MoveSpouts && c.WithdrawAfterDrink && !c.PenaltySound && c.TimeoutSeconds == 0);
    }

    [TestMethod]
    public void AnErrorDrawsBothSpoutsBackWithTheSoundAndTimeout()
    {
        var c = Of("Left", false, true);
        Assert.IsTrue(!c.Rewarded && c.Reward == null && c.SpoutCommand == OpenRigs.Devices.LickSpoutCommand.SpoutsOut && c.PenaltySound && c.TimeoutSeconds == 4);
        var miss = Of("NoResponse", false, true);  // the sound comes with every timeout (Pip, 14 Nov 2026)
        Assert.IsTrue(!miss.Rewarded && miss.PenaltySound && miss.TimeoutSeconds == 4);
        var auto = Of("NoResponse", true, false);
        Assert.IsTrue(auto.Rewarded && auto.Reward.Item1 == "right" && auto.TimeoutSeconds == 0 && !auto.PenaltySound && !auto.MoveSpouts && !auto.WithdrawAfterDrink);
    }

    [TestMethod]
    public void TheSpoutsComeInAsTheTrialSays()
    {
        Assert.AreEqual(OpenRigs.Devices.LickSpoutCommand.SpoutsIn, SpoutsIn.Command(new Trial { bothSpouts = true, target = "Left" }));
        Assert.AreEqual(OpenRigs.Devices.LickSpoutCommand.LeftIn, SpoutsIn.Command(new Trial { bothSpouts = false, target = "Left" }));
        Assert.AreEqual(OpenRigs.Devices.LickSpoutCommand.RightIn, SpoutsIn.Command(new Trial { bothSpouts = false, target = "Right" }));
    }
}
