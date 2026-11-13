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
        Assert.IsTrue(trials.All(t => t.leadSeconds == 0.5 && t.triggerAfterGoSeconds == 0 && double.IsNaN(t.goTime)));
    }

    [TestMethod]
    public void TheDiscriminationFileIsWhatGerionsMiceGot()
    {
        var s = Fixture.Task("task-discrimination.yml");
        Assert.AreEqual(1.5, s.TrialStructure.ItiMinSeconds);
        Assert.AreEqual(2.0, s.TrialStructure.ItiMaxSeconds);
        Assert.AreEqual(1.5, s.TrialStructure.DrinkSeconds);
        Assert.AreEqual(4.0, s.Punishment.TimeoutSeconds);   // incorrect or a miss: 5.5 to 6.0 s outcome to stimulus
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
        StringAssert.Contains(Fixture.Refusal(() => TrialDraw.CheckTask(task, 4, Fixture.Speakers())), "both above 0");
        task.TrainingStages[4].WheelQuietCm = 2;
        Assert.AreEqual("accepted", Fixture.Refusal(() => TrialDraw.CheckTask(task, 4, Fixture.Speakers())));
        task.TrainingStages[4].LickQuietSeconds = -1;
        StringAssert.Contains(Fixture.Refusal(() => TrialDraw.CheckTask(task, 4, Fixture.Speakers())), "negative");
        task = Fixture.Task("task-localization.yml");
        task.TrialStructure.DrinkSeconds = 3.5;
        StringAssert.Contains(Fixture.Refusal(() => TrialDraw.CheckTask(task, 4, Fixture.Speakers())), "below drinkSeconds");
    }
}

/// <summary>Go and the stimulus and trigger timed from it (Pip, 13 Nov 2026), on a stand-in Harp clock of 1 ms ticks.</summary>
[TestClass]
public class GoTests
{
    static Trial Trial(double lead, double lick = 0, double wheelSeconds = 0, double wheelCm = 0)
    {
        return new Trial { trialNumber = 1, itiStartTime = 10, itiSeconds = 2, leadSeconds = Math.Max(lead, 0), triggerAfterGoSeconds = Math.Max(-lead, 0),
                           lickQuietSeconds = lick, wheelQuietSeconds = wheelSeconds, wheelQuietCm = wheelCm };
    }

    // Ticks the clock from 10 s; each step's action runs at its time. Returns the trial at Go.
    static Trial Run(Trial trial, Dictionary<double, Action<GoWaiter>> steps = null)
    {
        var waiter = new GoWaiter();
        waiter.Wheel(0);
        waiter.Clock(10);
        waiter.Start(trial);
        for (int ms = 10001; ms < 30000; ms++)
        {
            var now = ms / 1000.0;
            var go = waiter.Clock(now);
            if (go != null) return go;
            Action<GoWaiter> step;
            if (steps != null && steps.TryGetValue(now, out step)) step(waiter);   // a lick or wheel sample stamped with this tick
        }
        throw new AssertFailedException("no Go");
    }

    [TestMethod]
    public void WithTheWaitsOffTheStimulusIsExactlyTheIntervalAfterItsStart()
    {
        foreach (var lead in new[] { 0.5, 0.0, -0.5 })
        {
            var go = Run(Trial(lead));
            Assert.AreEqual(12 - Math.Max(lead, 0), go.goTime, 1e-12);
            Assert.AreEqual(0, go.quietWaitSeconds, 1e-12);
            Assert.AreEqual(12, go.goTime + go.leadSeconds, 1e-12, "the stimulus: no time added by Zapit or the waits");
            Assert.AreEqual(12, SecondsUntil.Seconds(go, TrialMoment.Stimulus, go.goTime) + go.goTime, 1e-12);
            // The trigger: stimulus - laserToStimulusSeconds, before, with or after it.
            Assert.AreEqual(12 - lead, SecondsUntil.Seconds(go, TrialMoment.LaserTrigger, go.goTime) + go.goTime, 1e-12);
        }
    }

    [TestMethod]
    public void ALickRestartsTheLickWaitAndTheLeadStaysExact()
    {
        var steps = new Dictionary<double, Action<GoWaiter>> { { 11.0, w => w.Lick(true) }, { 11.2, w => w.Lick(false) } };
        var quiet = Run(Trial(0.5, 1.0));                // quiet from 10 s: Go at 11.5, the interval's end less the lead
        Assert.AreEqual(11.5, quiet.goTime, 1e-12);
        var licked = Run(Trial(0.5, 1.0), steps);         // licked at 11.0 (the clock's last tick): Go at 12.0
        Assert.AreEqual(12.0, licked.goTime, 1e-9);
        Assert.AreEqual(0.5, licked.quietWaitSeconds, 1e-9);
        Assert.AreEqual(12.5, licked.goTime + licked.leadSeconds, 1e-9);
        steps[11.4] = w => w.Lick(true);                  // a later lick restarts it again
        Assert.AreEqual(12.4, Run(Trial(0.5, 1.0), steps).goTime, 1e-9);
    }

    [TestMethod]
    public void TheWheelsTotalDistanceRestartsTheWheelWait()
    {
        // Rocking forward and back 0.6 cm a time: net distance 0, total past 1 cm on the second swing.
        var steps = new Dictionary<double, Action<GoWaiter>> { { 11.0, w => w.Wheel(0.6) }, { 11.3, w => w.Wheel(0.0) } };
        var rocked = Run(Trial(0.5, 0, 1.0, 1.0), steps);
        Assert.AreEqual(12.3, rocked.goTime, 1e-9);
        Assert.AreEqual(0.8, rocked.quietWaitSeconds, 1e-9);
        steps.Remove(11.3);                               // 0.6 cm alone stays within 1 cm
        Assert.AreEqual(11.5, Run(Trial(0.5, 0, 1.0, 1.0), steps).goTime, 1e-9);
        // Each wait restarts on its own; Go needs both.
        steps[11.3] = w => w.Wheel(0.0);
        steps[11.8] = w => w.Lick(true);
        Assert.AreEqual(12.8, Run(Trial(0.5, 1.0, 1.0, 1.0), steps).goTime, 1e-9);
    }

    [TestMethod]
    public void AMomentAlreadyPastStopsTheWorkflow()
    {
        var go = Run(Trial(-0.5));
        StringAssert.Contains(Fixture.Refusal(() => SecondsUntil.Seconds(go, TrialMoment.LaserTrigger, go.goTime + 0.6)), "laser trigger was due");
    }

    [TestMethod]
    public void TheTriggerPassesOnlyOnceZapitArmedIt()
    {
        var trigger = new System.Reactive.Subjects.Subject<Bonsai.Harp.Timestamped<Trial>>();
        var armed = new System.Reactive.Subjects.Subject<OpenRigs.Devices.Zapit.ZapitReply>();
        var passed = new List<Trial>();
        Exception error = null;
        new RequireZapitArmed().Process(trigger, armed).Subscribe(passed.Add, e => error = e);
        var trial = new Trial { trialNumber = 7 };
        armed.OnNext(new OpenRigs.Devices.Zapit.ZapitReply());
        trigger.OnNext(new Bonsai.Harp.Timestamped<Trial>(trial, 5));
        Assert.AreEqual(1, passed.Count);

        var late = new System.Reactive.Subjects.Subject<Bonsai.Harp.Timestamped<Trial>>();
        new RequireZapitArmed().Process(late, new System.Reactive.Subjects.Subject<OpenRigs.Devices.Zapit.ZapitReply>()).Subscribe(_ => { }, e => error = e);
        late.OnNext(new Bonsai.Harp.Timestamped<Trial>(trial, 5));
        StringAssert.Contains(error.Message, "had not answered trial 7's StartStimulus by its trigger");
    }

    [TestMethod]
    public void TheLaserWaitsForItsOwnTrialsGo()
    {
        var goes = new System.Reactive.Subjects.BehaviorSubject<Trial>(new Trial { trialNumber = 3 });
        var found = new List<Trial>();
        new TrialGoFor().Process(System.Reactive.Linq.Observable.Return(new Trial { trialNumber = 4 }), goes).Subscribe(found.Add);
        Assert.AreEqual(0, found.Count);
        goes.OnNext(new Trial { trialNumber = 4 });
        goes.OnNext(new Trial { trialNumber = 4 });
        Assert.AreEqual(1, found.Count);
    }

    [TestMethod]
    public void ATrialDrawnWhileAnotherWaitsStopsTheWorkflow()
    {
        var waiter = new GoWaiter();
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
    public void ALaserOffTrialSendsItsConditionAtZeroPower()
    {
        var trial = new Trial { trialNumber = 3, laserCondition = 2, laserOn = false };
        var off = ZapitTrialCommand.Build(trial, true, 1.5);
        Assert.IsTrue(off.ConditionNumber == 2 && off.LaserOn == false && off.LaserPower == 0f && off.HardwareTriggered == true && off.StimDuration == 1.5f);
        trial.laserOn = true;
        trial.laserPowerMw = 3;
        Assert.AreEqual(3f, ZapitTrialCommand.Build(trial, false, 1.5).LaserPower);
        trial.laserCondition = 0;
        StringAssert.Contains(Fixture.Refusal(() => ZapitTrialCommand.Build(trial, false, 1.5)), "1 to 254");
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
    public void TheRowLogsTheConditionAndPowerSent()
    {
        var trial = new Trial { target = "Left", laserCondition = 2, laserOn = true, laserPowerMw = 3 };
        var row = CreateTrialRow.Build("Left", 10, trial, 9, 4, true);
        Assert.IsTrue(row.laserCondition == 2 && row.laserPowerMw == 3);
        trial.laserOn = false;
        trial.laserPowerMw = 0;
        Assert.AreEqual(0, CreateTrialRow.Build("Left", 10, trial, 9, 4, true).laserPowerMw);
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
