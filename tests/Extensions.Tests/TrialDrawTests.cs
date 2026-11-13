using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using UclOpenCoenDomeDecisionMaking;
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

    public static DomeSpeakerArray Speakers()
    {
        var array = new DomeSpeakerArray();
        array.Speakers = new Dictionary<string, Speaker>();
        array.Speakers.Add("left", Speaker(-90, 15, 0));
        array.Speakers.Add("right", Speaker(90, 15, 1));
        array.Speakers.Add("centre", Speaker(0, 15, 2));
        return array;
    }

    public static Speaker Speaker(double azimuth, double elevation, int channel)
    {
        var position = new UclOpenCoenDomeDecisionMaking.SphericalPosition();
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
        var end = TrialEnd.First();
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
    public void EachTrialCarriesTheIntervalBeforeIt()
    {
        var task = Fixture.Task("task-discrimination.yml");
        var trials = Fixture.Draw(task, 3, 50);
        Assert.IsTrue(double.IsNaN(trials[0].itiSeconds) && double.IsNaN(trials[0].itiStartTime));
        for (int i = 1; i < trials.Count; i++)
        {
            Assert.AreEqual(trials[i - 1].nextItiSeconds, trials[i].itiSeconds);
            Assert.AreEqual(100 + i - 1, trials[i].itiStartTime);
            Assert.AreEqual(i + 1, trials[i].trialNumber);
        }
        foreach (var t in trials)
        {
            Assert.IsTrue(t.nextItiSeconds >= 1.5 && t.nextItiSeconds <= 2.5, "Gerion's 1.5 to 2.5 s");
            Assert.AreEqual(Math.Max(0, t.nextItiSeconds - 2.0), t.waitAfterDrinkSeconds, 1e-12);
        }
    }

    [TestMethod]
    public void OptogeneticsSendsAConditionOnEveryTrial()
    {
        var task = Fixture.Task("task-localization.yml");
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
        StringAssert.Contains(Fixture.Refusal(() => new TrialDraw(task, Fixture.Speakers(), 4, path => 2)), "laserPowerMw");
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
    public void ALaserOffTrialSendsItsConditionAtZeroPower()
    {
        var trial = new Trial { trialNumber = 3, laserCondition = 2, laserOn = false };
        var off = ZapitTrialCommand.Build(trial, true, 1.5);
        Assert.IsTrue(off.ConditionNumber == 2 && off.LaserOn == false && off.LaserPower == null && off.HardwareTriggered == true && off.StimDuration == 1.5f);
        trial.laserOn = true;
        trial.laserPowerMw = 3;
        Assert.AreEqual(3f, ZapitTrialCommand.Build(trial, false, 1.5).LaserPower);
        trial.laserCondition = 0;
        StringAssert.Contains(Fixture.Refusal(() => ZapitTrialCommand.Build(trial, false, 1.5)), "no Zapit condition");
    }

    [TestMethod]
    public void TheTrialGoesOnOnlyOnceZapitArmedItAsAsked()
    {
        var trial = new Trial { trialNumber = 21, laserCondition = 2, laserOn = true };
        CheckZapitArmed.Check(Reply(2, 1, false), trial);
        StringAssert.Contains(Fixture.Refusal(() => CheckZapitArmed.Check(Reply(1, 1, false), trial)), "armed condition 1");
        StringAssert.Contains(Fixture.Refusal(() => CheckZapitArmed.Check(Reply(2, 0, false), trial)), "laserOn 0");
        StringAssert.Contains(Fixture.Refusal(() => CheckZapitArmed.Check(Reply(2, 1, true), trial)), "refused");
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
        var trial = new Trial { target = "Left", autoReward = false, itiSeconds = 3, itiStartTime = 50 };
        var incorrect = CreateTrialRow.Build("Right", 10, trial, 9, 4, true);
        Assert.IsTrue(incorrect.outcome == "Incorrect" && incorrect.punishSound && incorrect.punishTimeoutSeconds == 4 && incorrect.choiceTime == 10);
        Assert.IsTrue(incorrect.itiSeconds == 3 && incorrect.itiStartTime == 50 && incorrect.stimulusOnsetTime == 9);
        var miss = CreateTrialRow.Build("NoResponse", 10, trial, 9, 4, true);
        Assert.IsTrue(miss.outcome == "NoResponse" && !miss.punishSound && miss.punishTimeoutSeconds == 4 && double.IsNaN(miss.choiceTime) && !miss.autoRewarded);
        trial.autoReward = true;
        var rewardedMiss = CreateTrialRow.Build("NoResponse", 10, trial, 9, 4, true);
        Assert.IsTrue(rewardedMiss.autoRewarded && rewardedMiss.punishTimeoutSeconds == 0);
        var correct = CreateTrialRow.Build("Left", 10, trial, 9, 4, true);
        Assert.IsTrue(correct.outcome == "Correct" && !correct.punishSound && correct.punishTimeoutSeconds == 0 && !correct.autoRewarded);
        Assert.IsFalse(CreateTrialRow.Build("Right", 10, trial, 9, 4, false).punishSound);
    }

    [TestMethod]
    public void TheRowsColumnsAreTheTrialColumnsFile()
    {
        var listed = File.ReadAllLines(Path.Combine(Fixture.Repo(), "schemas", "trial-columns.yml"))
            .Where(line => line.Length > 0 && char.IsLetter(line[0]) && line.EndsWith(":")).Select(line => line.TrimEnd(':')).ToList();
        var fields = new List<string> { "Seconds" };
        fields.AddRange(typeof(TrialRow).GetFields().Select(f => f.Name));
        CollectionAssert.AreEqual(listed, fields);
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
        Assert.IsTrue(c.Rewarded && c.Reward.Item1 == "Right" && c.Reward.Item2 == 3 && c.SpoutCommand == OpenRigs.Devices.LickSpoutCommand.RightIn);
        Assert.IsTrue(c.MoveSpouts && c.WithdrawAfterDrink && !c.ErrorSound && c.TimeoutSeconds == 0);
    }

    [TestMethod]
    public void AnErrorDrawsBothSpoutsBackWithTheSoundAndTimeout()
    {
        var c = Of("Left", false, true);
        Assert.IsTrue(!c.Rewarded && c.Reward == null && c.SpoutCommand == OpenRigs.Devices.LickSpoutCommand.SpoutsOut && c.ErrorSound && c.TimeoutSeconds == 4);
        var miss = Of("NoResponse", false, true);
        Assert.IsTrue(!miss.Rewarded && !miss.ErrorSound && miss.TimeoutSeconds == 4);
        var auto = Of("NoResponse", true, false);
        Assert.IsTrue(auto.Rewarded && auto.Reward.Item1 == "Right" && auto.TimeoutSeconds == 0 && !auto.MoveSpouts && !auto.WithdrawAfterDrink);
    }

    [TestMethod]
    public void TheSpoutsComeInAsTheTrialSays()
    {
        Assert.AreEqual(OpenRigs.Devices.LickSpoutCommand.SpoutsIn, SpoutsIn.Command(new Trial { bothSpouts = true, target = "Left" }));
        Assert.AreEqual(OpenRigs.Devices.LickSpoutCommand.LeftIn, SpoutsIn.Command(new Trial { bothSpouts = false, target = "Left" }));
        Assert.AreEqual(OpenRigs.Devices.LickSpoutCommand.RightIn, SpoutsIn.Command(new Trial { bothSpouts = false, target = "Right" }));
    }
}
