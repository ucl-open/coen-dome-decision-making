using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Devices;
using OpenRigs.Devices.Rigs;
using OpenRigs.Zapit;
using OpenRigs.Trials;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using UclOpenCoenDomeDecisionMakingTask;
using Event = OpenRigs.Trials.TrialEvent<global::Trial, global::DecisionUpdate, global::TrialOutcome>;

/// <summary>TrialRules run by lib-open-rigs' TrialLoop on a stand-in Harp clock of 1 ms ticks from 10 s.</summary>
class Harness
{
    public readonly TrialLoop<Trial, DecisionUpdate, TrialOutcome> Loop;
    public readonly List<Event> Events = new List<Event>();
    readonly Dictionary<double, Action<Harness>> steps = new Dictionary<double, Action<Harness>>();
    int ms = 10000;

    public Harness(TrialRules rules)
    {
        Loop = new TrialLoop<Trial, DecisionUpdate, TrialOutcome>(rules, true, true);   // ZapitDaq arms the laser trials
        Loop.OnWheel(0);
        Events.AddRange(Loop.OnClock(Now));
    }

    public double Now { get { return ms / 1000.0; } }

    /// <summary>A step run just after the tick at its time: a lick or wheel sample timed at that tick.</summary>
    public Harness At(double seconds, Action<Harness> step)
    {
        Action<Harness> before;
        steps[seconds] = steps.TryGetValue(seconds, out before) ? before + step : step;
        return this;
    }

    public void Lick(int spout)
    {
        Events.AddRange(Loop.OnLick(spout, true));
        Events.AddRange(Loop.OnLick(spout, false));
    }

    public void Touch(int spout, bool touched)
    {
        Events.AddRange(Loop.OnLick(spout, touched));
    }

    public void Wheel(double cm)
    {
        Events.AddRange(Loop.OnWheel(cm));
    }

    public Harness RunTo(double seconds)
    {
        while (Now < seconds - 1e-9)
        {
            ms++;
            Events.AddRange(Loop.OnClock(Now));
            Action<Harness> step;
            if (steps.TryGetValue(Now, out step)) step(this);
        }
        return this;
    }

    public List<Event> Of(TrialEventKind kind)
    {
        return Events.Where(e => e.Kind == kind).ToList();
    }

    public List<TrialOutcome> Outcomes { get { return Of(TrialEventKind.Outcome).Select(e => e.Outcome).ToList(); } }

    public TrialRow Row(int index = 0)
    {
        return Outcomes[index].Row.Value;
    }

    public Trial First { get { return Of(TrialEventKind.Start)[0].Trial; } }

    // The lick that answers the first trial right (0 left, 1 right) or wrong.
    public int Spout(bool correct)
    {
        return (First.target == TargetWords.Left) == correct ? 0 : 1;
    }
}

static class Rules
{
    // Discrimination stage 4 with every interval exactly 2 s: trial 1's interval from 10 s, its start at 11.5 (the 0.5 s
    // lead), the stimulus at 12 and its response window 12.5 to 14.5 (stimulusToResponseWindowSeconds 0.5, the spouts' arrival), where every trial ends. Stage 4 takes the task's weights,
    // here scored trials alone in the two-spout tasks, so every trial has a side to answer (the probes' own test below).
    public static TrialRules Make(Action<UclOpenCoenDomeDecisionMakingTaskParameters> edit = null, string file = "task-discrimination-left-right.yml", int stage = 4,
        double earlyStopLeadSeconds = 0)
    {
        var task = Fixture.Task(file, stage);
        task.TrialStructure.ItiMinSeconds = 2;
        task.TrialStructure.ItiMaxSeconds = 2;
        task.Optogenetics.LaserToStimulusSeconds = 0.5;   // the 0.5 s lead the times here assume (the files' is 0.1)
        if (task.TaskType != TaskType.DiscriminationGoNoGo)
        {
            Fixture.Weight(task, "audiovisualConflict", 0);
            Fixture.Weight(task, "neutral", 0);
            if (task.TaskType == TaskType.DiscriminationLeftRight) Fixture.Weight(task, "audiovisualCoherent", 0);
        }
        if (edit != null) edit(task);
        return new TrialRules(Fixture.Text(task), Fixture.Speakers(), Fixture.Bank(), earlyStopLeadSeconds);
    }

    public static void Optogenetics(UclOpenCoenDomeDecisionMakingTaskParameters task)
    {
        task.Optogenetics.Enabled = true;
        task.Optogenetics.LaserPowerMw = 3;
        task.Optogenetics.LaserDurationSeconds = 0.8;
        task.Optogenetics.TrialsBeforeLaser = 0;
        task.Optogenetics.LaserOnProbability = 1;
    }

    // A trial as drawn (localization stage 4's first, so the rules have its combination's values: penalty -45 dBFS) with
    // this target and offer, its spouts staying in or not, no coin, no auto-reward, both spouts.
    public static Trial Made(string target, double rewardOfferedUl, bool alwaysIn = false)
    {
        var task = Fixture.Task("task-localization.yml", 4);
        task.Spouts.AlwaysIn = alwaysIn;
        var trial = new TrialDraw(Fixture.Text(task), Fixture.Speakers(), Fixture.Bank()).Next(null);
        trial.target = target;
        trial.rewardOfferedUl = rewardOfferedUl;
        trial.autoReward = trial.probeHeads = trial.singleSpoutOffered = false;
        return trial;
    }

    // ZapitDaq's Armed event for the trial, as asked, at the power it asked for.
    public static ZapitDaqEvent Armed(Trial trial)
    {
        return new ZapitDaqEvent
        {
            Kind = ZapitDaqEventKind.Armed,
            Trial = trial.trialNumber,
            Condition = trial.laserCondition,
            LaserOn = trial.laserOn,
            PowerMw = trial.laserPowerMw,
            CappedPowerMw = trial.laserPowerMw,
            TrialSeconds = trial.values.Optogenetics.LaserDurationSeconds + Fixture.RampDownSeconds,   // whole 0.1 s cycles and the ramp
        };
    }
}

[TestClass]
public class ResponseRuleTests
{
    [TestMethod]
    public void TheChoiceIsTheSideWhoseNetLicksReachTheThreshold()
    {
        // thresholdLicks 3: left, right, left, left leaves the count at 2; the fifth lick, a left, reaches 3 at 13.0 s.
        Action<UclOpenCoenDomeDecisionMakingTaskParameters> three = t => { t.Response.ThresholdLicks = 3; t.Response.AzimuthShiftPerLickDeg = 10; };
        var run = new Harness(Rules.Make(three));
        foreach (var lick in new[] { Tuple.Create(12.6, 0), Tuple.Create(12.7, 1), Tuple.Create(12.8, 0), Tuple.Create(12.9, 0), Tuple.Create(13.0, 0) })
        {
            var spout = lick.Item2;
            run.At(lick.Item1, h => h.Lick(spout));
        }
        run.RunTo(13.5);
        var updates = run.Of(TrialEventKind.Update).Select(e => e.Update).ToList();
        CollectionAssert.AreEqual(new[] { 0, 1, 0, 1, 2, 3 }.Select(n => run.First.visualInitialAzimuthDeg + 10 * n).ToList(), updates.Select(u => u.GratingAzimuthDeg).ToList(), "the stimulus resets it; each lick in the window moves it azimuthShiftPerLickDeg");
        Assert.AreEqual(1, run.Outcomes.Count);
        Assert.AreEqual(Choice.Left, run.Row().choice);
        Assert.AreEqual(13.0, run.Row().outcomeTime, 1e-12);
        Assert.AreEqual(13.0, run.Outcomes[0].Row.Seconds, 1e-12, "the row is stamped with its outcome");

        var right = new Harness(Rules.Make(three));
        foreach (var t in new[] { 12.6, 12.7, 12.8 }) right.At(t, h => h.Lick(1));
        right.RunTo(13.5);
        Assert.AreEqual(Choice.Right, right.Row().choice);
        Assert.AreEqual(12.8, right.Row().outcomeTime, 1e-12);
        Assert.AreEqual(right.First.visualInitialAzimuthDeg - 30, right.Of(TrialEventKind.Update).Last().Update.GratingAzimuthDeg, 1e-9);
    }

    [TestMethod]
    public void ALickBeforeTheResponseWindowIsNotCounted()
    {
        // stimulusToResponseWindowSeconds 0.5: the window opens at 12.5 s. Licks in the interval, the lead and the time before the window count for nothing.
        var run = new Harness(Rules.Make(t => t.TrialStructure.StimulusToResponseWindowSeconds = 0.5));
        foreach (var t in new[] { 11.0, 11.7, 12.2, 12.499 }) run.At(t, h => h.Lick(0));
        run.At(12.5, h => h.Lick(1));
        run.RunTo(13);
        Assert.AreEqual(2, run.Of(TrialEventKind.Update).Count, "the stimulus's, then the 12.5 s lick's");
        Assert.AreEqual(Choice.Right, run.Row().choice);
        Assert.AreEqual(12.5, run.Row().outcomeTime, 1e-12);
    }

    [TestMethod]
    public void NoResponseAtTheWindowsEnd()
    {
        // thresholdLicks 2, one lick: the window (0.5 s open loop, then 2.0 s) ends with no choice at 14.5 s.
        var run = new Harness(Rules.Make(t => { t.TrialStructure.StimulusToResponseWindowSeconds = 0.5; t.Response.ThresholdLicks = 2; }));
        run.At(13.0, h => h.Lick(0)).RunTo(14.499);
        Assert.AreEqual(0, run.Outcomes.Count);
        run.RunTo(19.0);
        var row = run.Row();
        Assert.IsTrue(row.choice == Choice.NoResponse && row.outcome == TrialOutcomes.NoResponse && row.outcomeTime == 14.5);
        Assert.IsTrue(row.penalised && row.penaltyTimeoutSeconds == 4, "a miss: the sound and the timeout");
        Assert.AreEqual(18.5, run.Of(TrialEventKind.Iti)[1].Seconds, 1e-9, "the next interval at the trial's end (a miss: the outcome) plus the timeout");
    }

    [TestMethod]
    public void ALickGivesTheOutcomeButTheTrialRunsToTheWindowsEnd()
    {
        // The task as shipped (responseWindowAlwaysFull true, the default the files leave out): the window 12.5 to 14.5 s.
        // A lick at 12.6 s is the outcome; the trial ends at 14.5 s, rewarded or not, and the next interval starts then
        // plus any timeout (penalty.incorrect 4 s), so a trial lasts the same whatever the mouse does.
        Assert.IsTrue(Fixture.Task("task-discrimination-left-right.yml", 4).TrialStructure.ResponseWindowAlwaysFull);
        foreach (var correct in new[] { true, false })
        {
            var run = new Harness(Rules.Make());
            run.RunTo(12.05);
            var spout = run.Spout(correct);
            run.At(12.6, h => h.Lick(spout)).RunTo(26);
            var first = run.Row();
            Assert.AreEqual(12.6, first.outcomeTime, 1e-12, "the lick is the outcome");
            Assert.AreEqual(14.5, first.trialEndTime, 1e-9, "the trial ends at the window's end");
            Assert.AreEqual(correct ? 0 : 4, first.penaltyTimeoutSeconds);
            Assert.AreEqual(first.trialEndTime + first.penaltyTimeoutSeconds, run.Row(1).itiStartTime, 1e-9, "the next interval at the trial's end plus its timeout");
        }
    }

    [TestMethod]
    public void ARepeatFollowsTheOutcome()
    {
        // Localization stage 4 with every type that has a right answer repeated on failure: a wrong answer repeats the trial, a right one draws anew.
        var run = new Harness(Rules.Make(t => t.Repeats.RepeatOn = RepeatOn.NotCorrect, file: "task-localization.yml"));
        run.RunTo(12.05);
        var wrong = run.Spout(false);
        run.At(12.6, h => h.Lick(wrong)).RunTo(18.6);   // Incorrect at 12.6: the window runs to 14.5, the timeout, then trial 2's interval
        run.RunTo(20.5);
        var starts = run.Of(TrialEventKind.Start).Select(e => e.Trial).ToList();
        Assert.AreEqual(2, starts.Count);
        Assert.AreEqual(TrialOutcomes.Incorrect, run.Row().outcome);
        Assert.IsTrue(starts[1].trialNumber == 2 && starts[1].repeatCount == 1 && starts[1].visualInitialAzimuthDeg.Equals(starts[0].visualInitialAzimuthDeg)
                      && starts[1].target == starts[0].target && starts[1].trialType == starts[0].trialType);
        var right = starts[1].target == TargetWords.Left ? 0 : 1;
        run.At(21.2, h => h.Lick(right)).RunTo(25);   // rewarded: trial 3's 2 s interval from the window's end, 23.0 s
        Assert.AreEqual(TrialOutcomes.Correct, run.Row(1).outcome);
        Assert.AreEqual(0, run.Of(TrialEventKind.Start)[2].Trial.repeatCount, "after a correct answer, a new draw");
    }

    [TestMethod]
    public void TheLoopRunsTheDrawsTrialsInOrder()
    {
        // The same seed draws the same trials through the loop as TrialDraw alone, every trial a miss.
        var run = new Harness(new TrialRules(Fixture.Text(Fixture.Task("task-discrimination-left-right.yml", 3)), Fixture.Speakers(), Fixture.Bank()));
        run.RunTo(200);
        var looped = run.Of(TrialEventKind.Start).Select(e => e.Trial).ToList();
        var drawn = Fixture.Draw(Fixture.Task("task-discrimination-left-right.yml", 3), looped.Count, t => Choice.NoResponse);
        Assert.IsTrue(looped.Count >= 20);
        CollectionAssert.AreEqual(drawn.Select(t => t.visualPhaseRad).ToList(), looped.Select(t => t.visualPhaseRad).ToList());
        CollectionAssert.AreEqual(drawn.Select(t => t.trialType + t.target + t.itiSeconds + t.auditorySoundFileIndex).ToList(),
                                  looped.Select(t => t.trialType + t.target + t.itiSeconds + t.auditorySoundFileIndex).ToList());
    }

    [TestMethod]
    public void ANoRewardTrialInLocalizationAndLeftRightOffersNothingAndCostsNothing()
    {
        // The files' probes: both spouts in, nothing on offer, never auto-rewarded, outcome Probe, never rewarded or
        // penalised whatever the choice (left, right or a miss), even when penaliseRandomProbes is on.
        var times = new TrialTimes { Now = 1 };
        foreach (var file in new[] { "task-discrimination-left-right.yml", "task-localization.yml" })
        foreach (var penaliseRandomProbes in new[] { false, true })
        {
            var task = Fixture.Task(file, 4);
            task.AutoRewardProbability = 1;
            task.PenaliseRandomProbes = penaliseRandomProbes;
            task.Spouts.SingleSpoutProbability = 1;
            var probes = Fixture.Draw(task, 2000).Where(t => t.target == TargetWords.NoReward).ToList();
            Assert.IsTrue(probes.Count > 50, file);
            Assert.IsTrue(probes.All(t => !t.singleSpoutOffered && !t.autoReward && t.rewardOfferedUl == 0 && t.SpoutsInCommand == LickSpoutCommand.SpoutsIn), file);
            foreach (var t in probes.Take(60))
                foreach (var choice in new[] { Choice.Left, Choice.Right, Choice.NoResponse })
                {
                    var o = TrialOutcome.At(choice, t, times);
                    var row = o.Row.Value;
                    Assert.AreEqual(TrialOutcomes.Probe, row.outcome, file + choice);
                    Assert.IsTrue(!o.Rewarded && o.Reward == null && !o.Penalised && o.PenaltyTimeoutSeconds == 0, file + choice);
                    Assert.IsTrue(!row.rewarded && !row.penalised && row.rewardOfferedUl == 0 && !row.singleSpoutOffered && row.target == TargetWords.NoReward, file + choice);
                }
        }
    }

    [TestMethod]
    public void ATrialWithNoRightAnswerRewardsOrPenalisesEitherChoiceOnACoin()
    {
        // Random-reward probes: left-right's two-modality and neutral trials, localization's conflict and
        // neutral. Both spouts in; a choice at either is rewarded there on heads, unrewarded and unpenalised on tails
        // (penaliseRandomProbes false, the files'); a miss costs nothing; target Random, outcome Probe; the offer kept heads or tails.
        var times = new TrialTimes { Now = 1 };
        foreach (var file in new[] { "task-discrimination-left-right.yml", "task-localization.yml" })
        {
            var task = Fixture.Task(file, 4);
            Fixture.RandomProbes(task);   // the files' probes are noReward; a stage may choose random per condition
            var all = Fixture.Draw(task, 4000);
            var probes = all.Where(t => t.target == TargetWords.Random).ToList();
            var kinds = file.Contains("left-right") ? new[] { "audiovisualCoherent", "audiovisualConflict", "neutral" } : new[] { "audiovisualConflict", "neutral" };
            CollectionAssert.AreEquivalent(kinds, probes.Select(t => t.trialType).Distinct().ToArray(), file);
            Assert.IsTrue(all.Where(t => t.target != TargetWords.Random).All(t => !kinds.Contains(t.trialType)), file);
            Assert.IsTrue(probes.All(t => !t.singleSpoutOffered && !t.autoReward && t.SpoutsInCommand == LickSpoutCommand.SpoutsIn), file);
            int rewarded = 0, unrewarded = 0;
            for (int i = 0; i < probes.Count; i++)
            {
                var t = probes[i];
                var choice = i % 2 == 0 ? Choice.Left : Choice.Right;
                var o = TrialOutcome.At(choice, t, times);
                Assert.AreEqual(TrialOutcomes.Probe, o.Row.Value.outcome);
                Assert.IsFalse(o.Penalised, "a probe is not penalised");
                Assert.IsTrue(o.Row.Value.rewarded == o.Rewarded && o.Row.Value.penalised == o.Penalised, "the row says what the valve and the penalty did");
                Assert.IsTrue(t.rewardOfferedUl > 0 && o.Row.Value.rewardOfferedUl == t.rewardOfferedUl, "the offer, heads or tails");
                if (o.Rewarded)
                {
                    rewarded++;
                    Assert.AreEqual(Tuple.Create(choice == Choice.Left ? "left" : "right", t.rewardOfferedUl), o.Reward, "at the spout chosen");
                    Assert.IsTrue(t.probeHeads && o.PenaltyTimeoutSeconds == 0);
                }
                else
                {
                    unrewarded++;
                    Assert.IsTrue(!t.probeHeads && o.Reward == null && o.PenaltyTimeoutSeconds == 0 && o.SpoutCommand == LickSpoutCommand.SpoutsOut, "tails: no reward, no timeout");
                }
                // A miss: nothing paid and nothing charged, whatever the coin.
                var miss = TrialOutcome.At(Choice.NoResponse, t, times);
                Assert.IsTrue(miss.Row.Value.outcome == TrialOutcomes.Probe && !miss.Rewarded && !miss.Row.Value.rewarded && !miss.Penalised && !miss.Row.Value.penalised && miss.PenaltyTimeoutSeconds == 0, "no choice, nothing paid or charged");
                Assert.AreEqual(t.rewardOfferedUl, miss.Row.Value.rewardOfferedUl, "the offer kept on a miss");
            }
            Assert.AreEqual(0.5, rewarded / (double)probes.Count, 0.06, file + ": about half rewarded");
            Assert.IsTrue(probes.Count > 200 && rewarded + unrewarded == probes.Count);
        }
    }

    [TestMethod]
    public void GoNoGoScoresALickOrNone()
    {
        // Go: a lick Correct and rewarded at the left, none NoResponse unpenalised; NoGo: a lick Incorrect and penalised,
        // none Correct and unrewarded; a probe: Probe, neither (Song et al. 2017).
        var times = new TrialTimes { Now = 1 };
        Func<string, string, TrialOutcome> score = (target, choice) =>
        {
            var t = Rules.Made(target, target == TargetWords.Go ? 3 : 0);
            return TrialOutcome.At(choice, t, times);
        };
        var hit = score(TargetWords.Go, Choice.Go);
        Assert.AreEqual(TrialOutcomes.Correct, hit.Row.Value.outcome);
        Assert.AreEqual(hit.Row.Value.target, hit.Row.Value.choice, "a correct go: choice == target");
        Assert.AreEqual(Tuple.Create("left", 3.0), hit.Reward);
        Assert.AreEqual(LickSpoutCommand.LeftOnly, hit.SpoutCommand);
        var miss = score(TargetWords.Go, Choice.NoResponse);
        Assert.IsTrue(miss.Row.Value.outcome == TrialOutcomes.NoResponse && !miss.Rewarded && miss.PenaltyTimeoutSeconds == 0 && !miss.Penalised);
        Assert.IsTrue(hit.Row.Value.rewarded && !hit.Row.Value.penalised && hit.Row.Value.rewardOfferedUl == 3);
        Assert.IsTrue(!miss.Row.Value.rewarded && !miss.Row.Value.penalised && miss.Row.Value.rewardOfferedUl == 3, "a Go miss: offered, neither");
        var falseAlarm = score(TargetWords.NoGo, Choice.Go);
        Assert.IsTrue(falseAlarm.Row.Value.outcome == TrialOutcomes.Incorrect && !falseAlarm.Rewarded && falseAlarm.PenaltyTimeoutSeconds == 4 && falseAlarm.Penalised);
        Assert.IsTrue(!falseAlarm.Row.Value.rewarded && falseAlarm.Row.Value.penalised && falseAlarm.Row.Value.rewardOfferedUl == 0, "a false alarm: penalised, nothing offered");
        var rejection = score(TargetWords.NoGo, Choice.NoResponse);
        Assert.IsTrue(rejection.Row.Value.outcome == TrialOutcomes.Correct && !rejection.Rewarded && rejection.PenaltyTimeoutSeconds == 0 && rejection.Reward == null);
        Assert.IsTrue(!rejection.Row.Value.rewarded && !rejection.Row.Value.penalised);
        foreach (var choice in new[] { Choice.Go, Choice.NoResponse })
        {
            var probe = score(TargetWords.NoReward, choice);
            Assert.IsTrue(probe.Row.Value.outcome == TrialOutcomes.Probe && !probe.Rewarded && probe.PenaltyTimeoutSeconds == 0 && !probe.Penalised, choice);
            Assert.IsTrue(!probe.Row.Value.rewarded && !probe.Row.Value.penalised && probe.Row.Value.rewardOfferedUl == 0, choice);
        }
    }

    [TestMethod]
    public void GoNoGoCountsTheLeftSpoutAlone()
    {
        var run = new Harness(Rules.Make(file: "task-discrimination-go-no-go.yml"));
        run.RunTo(12.05);
        var trial = run.Of(TrialEventKind.Start)[0].Trial;
        run.At(12.6, h => h.Lick(1)).RunTo(12.7);
        Assert.AreEqual(0, run.Outcomes.Count, "a right lick is not counted");
        run.At(12.8, h => h.Lick(0)).RunTo(12.9);
        Assert.AreEqual(Choice.Go, run.Row(0).choice);
        Assert.AreEqual(trial.target == TargetWords.Go ? TrialOutcomes.Correct : trial.target == TargetWords.NoGo ? TrialOutcomes.Incorrect : TrialOutcomes.Probe, run.Row(0).outcome);
    }
}

[TestClass]
public class CueTests
{
    [TestMethod]
    public void TheStimulusOffComesFromTheStimulusSpoutsInFromTheStimulusAndSpoutsOutFromTheNextIti()
    {
        // stimulus.durationSeconds 1.5 (responseToStimulusOffSeconds null), spouts.stimulusToSpoutsInSeconds 0.5, spouts.rewardConsumptionSeconds 1.5; a correct lick at 12.5 s, as the spouts arrive, no timeout.
        var run = new Harness(Rules.Make());
        run.RunTo(12.05);
        var right = run.Spout(true);
        run.At(12.5, h => h.Lick(right)).RunTo(16.5);
        var cues = run.Of(TrialEventKind.Cue).Where(e => e.Trial.trialNumber == 1).ToDictionary(e => e.Cue, e => e.Seconds);
        Assert.AreEqual(12.5, cues[OpenRigs.Trials.Spouts.InCue], 1e-12, "the window opens as the spouts arrive, so no outcome can come before SpoutsIn: nothing to drop (the Go check refuses a window before the spouts)");
        Assert.AreEqual(13.5, run.Of(TrialEventKind.StimulusOff).Single(e => e.Trial.trialNumber == 1).Seconds, 0.0011, "after the outcome: the stimulus stays its durationSeconds");
        Assert.AreEqual(16.0, cues[OpenRigs.Trials.Spouts.OutCue], 1e-9, "spouts.rewardConsumptionSeconds into the interval, which starts at the trial's end, the window's (14.5 s)");
        Assert.AreEqual(12.0, run.Of(TrialEventKind.Stimulus)[0].Seconds, 1e-12);
    }

    [TestMethod]
    public void TheStimulusGoesOffOnceAtTheEarlierOfItsDurationAndTheResponsePlusResponseToStimulusOffSeconds()
    {
        // The stimulus from 12.0 s; a correct lick at 12.6 s. The loop's one StimulusOff event a trial (the grating hidden and
        // the sound stopped) comes at the row's stimulusOffTime (OpenRigs.Trials StimulusTiming).
        Func<double?, double?, Tuple<int, double, double>> ended = (after, duration) =>
        {
            var run = new Harness(Rules.Make(t => { t.Stimulus.ResponseToStimulusOffSeconds = after; t.Stimulus.DurationSeconds = duration; }));
            run.RunTo(12.05);
            var right = run.Spout(true);
            run.At(12.6, h => h.Lick(right)).RunTo(16);
            var outcome = run.Of(TrialEventKind.Outcome).Single(e => e.Trial.trialNumber == 1);
            Assert.AreEqual(12.6, outcome.Seconds, 0.002);
            var row = outcome.Outcome.Row.Value;
            var offs = run.Of(TrialEventKind.StimulusOff).Where(e => e.Trial.trialNumber == 1).ToList();
            Assert.AreEqual(row.stimulusOffTime, offs[0].Seconds, 0.0011, "the event at the row's stimulusOffTime");
            return Tuple.Create(offs.Count, row.stimulusOffTime - row.stimulusOnsetTime, row.outcomeTime - row.stimulusOnsetTime);
        };
        var early = ended(0, 1.5);
        Assert.AreEqual(1, early.Item1, "once a trial");
        Assert.AreEqual(early.Item3, early.Item2, 1e-12, "0: off at the outcome, before the stimulus's end at 13.5 s");
        var late = ended(0.3, 1.5);
        Assert.AreEqual(late.Item3 + 0.3, late.Item2, 1e-9, "0.3 s after the outcome");
        var full = ended(null, 1.5);
        Assert.AreEqual(1, full.Item1);
        Assert.AreEqual(1.5, full.Item2, 1e-9, "null: the stimulus runs its durationSeconds");
        var over = ended(0, 0.1);
        Assert.AreEqual(1, over.Item1, "one off, at the duration, none at the outcome");
        Assert.AreEqual(0.1, over.Item2, 1e-9, "off at its duration, before the outcome");
        var uncapped = ended(0.5, null);
        Assert.AreEqual(uncapped.Item3 + 0.5, uncapped.Item2, 1e-9, "no duration: off only after the response");
    }

    [TestMethod]
    public void TheSpoutsInComesWhenTheOutcomeIsLaterThanItsMoment()
    {
        // The same cue, an outcome at 12.7 s: SpoutsIn was due at 12.5 s, before it.
        var run = new Harness(Rules.Make());
        run.RunTo(12.05);
        var right = run.Spout(true);
        run.At(12.7, h => h.Lick(right)).RunTo(16);
        var cues = run.Of(TrialEventKind.Cue).Where(e => e.Trial.trialNumber == 1).ToDictionary(e => e.Cue, e => e.Seconds);
        Assert.AreEqual(12.5, cues[OpenRigs.Trials.Spouts.InCue], 1e-12);
    }

    [TestMethod]
    public void AfterATimeoutSpoutsOutComesRewardConsumptionSecondsIntoTheNextIti()
    {
        // An incorrect lick at 12.6 s: the trial ends at the window's end (14.5 s), penalty.incorrect.timeoutSeconds 4, so the
        // next interval at 18.5 s, SpoutsOut 1.5 s into it.
        var run = new Harness(Rules.Make());
        run.RunTo(12.05);
        var wrong = run.Spout(false);
        run.At(12.6, h => h.Lick(wrong)).RunTo(21);
        Assert.AreEqual(20.0, run.Of(TrialEventKind.Cue).Single(e => e.Cue == OpenRigs.Trials.Spouts.OutCue).Seconds, 1e-9);
    }

    [TestMethod]
    public void AStayInStageStillHasItsSpoutsOutCueButItsOutcomesKeepTheSpoutsIn()
    {
        // The cue is the loop's, every trial; whether the spouts go out is SpoutCommands' (the coming trial's spouts.alwaysIn).
        var run = new Harness(Rules.Make(stage: 0)).RunTo(25);
        Assert.IsTrue(run.Outcomes.Count >= 2);
        Assert.IsTrue(run.Of(TrialEventKind.Cue).Any(e => e.Cue == OpenRigs.Trials.Spouts.OutCue));
        Assert.IsTrue(run.Of(TrialEventKind.Outcome).All(e => e.Trial.SpoutsAlwaysIn));
    }

    [TestMethod]
    public void AtOutcomeToSpoutsOutZeroTheNextTrialIsDrawnBeforeTheSpoutsOutCue()
    {
        // SpoutsOut is due at the next interval's start; the loop draws that interval's trial first (its Iti event), so
        // SpoutCommands' coming trial is the one whose spouts.alwaysIn decides, with a timeout or without.
        foreach (var correct in new[] { true, false })
        {
            var run = new Harness(Rules.Make(t => t.Spouts.RewardConsumptionSeconds = 0));
            run.RunTo(12.05);
            var spout = run.Spout(correct);
            run.At(12.6, h => h.Lick(spout)).RunTo(20);
            var cue = run.Events.FindIndex(e => e.Kind == TrialEventKind.Cue && e.Cue == OpenRigs.Trials.Spouts.OutCue && e.Trial.trialNumber == 1);
            var iti = run.Events.FindIndex(e => e.Kind == TrialEventKind.Iti && e.Trial.trialNumber == 2);
            Assert.IsTrue(iti >= 0 && cue > iti, correct ? "no timeout" : "after a timeout");
            Assert.AreEqual(run.Events[iti].Seconds, run.Events[cue].Seconds, 0.0011, "on the tick that starts the interval or the next");
        }
    }
}

/// <summary>lib-open-rigs' SpoutCommands on this task's trials: every spout move from the trial events, as LickSpoutStage.Commands takes them.</summary>
[TestClass]
public class SpoutCommandsTests
{
    static IList<LickSpoutCommand> Commands(Harness run)
    {
        return new SpoutCommands().Process(run.Events.ToObservable()).ToList().Wait();
    }

    [TestMethod]
    public void MovingSpoutsStartOutAndEachOutcomeAndSpoutsOutMoveThem()
    {
        // Trial 1's interval from 10 s: the spouts out; SpoutsIn at 12.5 s; a correct lick at 12.7 s leaves the
        // rewarded spout alone in; SpoutsOut, 1.5 s into the next interval, draws it out. Trial 2's interval start moves nothing.
        var run = new Harness(Rules.Make());
        run.RunTo(12.05);
        var right = run.Spout(true);
        run.At(12.7, h => h.Lick(right)).RunTo(16);
        var commands = Commands(run);
        Assert.IsTrue(run.Of(TrialEventKind.Iti).Count >= 2);
        CollectionAssert.AreEqual(new[] { LickSpoutCommand.SpoutsOut, run.First.SpoutsInCommand,
            right == 0 ? LickSpoutCommand.LeftOnly : LickSpoutCommand.RightOnly, LickSpoutCommand.SpoutsOut }, commands.Take(4).ToArray());
        var outs = run.Of(TrialEventKind.Cue).Count(e => e.Cue == OpenRigs.Trials.Spouts.OutCue);
        Assert.AreEqual(1 + outs, commands.Count(c => c == LickSpoutCommand.SpoutsOut));
        CollectionAssert.AreEqual(commands.ToArray(), Commands(run).ToArray(), "each subscription starts at its own trial 1");
    }

    [TestMethod]
    public void WhenTheSpoutsStayInOnlyTheSpoutsInCueMovesThem()
    {
        // Habituation (stage 0): both spouts every trial, sent in at each SpoutsIn cue and never drawn out.
        var run = new Harness(Rules.Make(stage: 0)).RunTo(25);
        var spoutsIn = run.Of(TrialEventKind.Cue).Where(e => e.Cue == OpenRigs.Trials.Spouts.InCue).ToList();
        Assert.IsTrue(spoutsIn.Count >= 2);
        CollectionAssert.AreEqual(spoutsIn.Select(e => e.Trial.SpoutsInCommand).ToArray(), Commands(run).ToArray());
        Assert.IsTrue(spoutsIn.All(e => e.Trial.SpoutsInCommand == LickSpoutCommand.SpoutsIn));
    }

    [TestMethod]
    public void ATrialWhoseSpoutsStayInBetweenMovingOnesIsNeverWithdrawn()
    {
        // Left-right stage 4, the left condition moving and the right staying in (its set's spoutsStayIn), every trial a miss:
        // a stay-in trial's outcome moves nothing and its SpoutsIn sends both; each SpoutsOut cue draws the spouts out only
        // when the trial drawn at that interval's start moves.
        var run = new Harness(Rules.Make(t => ((IDictionary<object, object>)t.TrialConditions[1].Set)["spouts.alwaysIn"] = true)).RunTo(150);
        var moves = new List<Tuple<Event, LickSpoutCommand>>();
        Event current = null;
        var events = new Subject<Event>();
        new SpoutCommands().Process(events).Subscribe(c => moves.Add(Tuple.Create(current, c)));
        foreach (var e in run.Events) { current = e; events.OnNext(e); }
        var trials = run.Of(TrialEventKind.Start).Select(e => e.Trial).ToList();
        Assert.IsTrue(trials.Count > 10 && trials.Any(t => t.SpoutsAlwaysIn) && trials.Any(t => !t.SpoutsAlwaysIn));
        Assert.IsTrue(Enumerable.Range(1, trials.Count - 2).Any(i => trials[i].SpoutsAlwaysIn && !trials[i - 1].SpoutsAlwaysIn && !trials[i + 1].SpoutsAlwaysIn), "a stay-in trial between moving ones");
        foreach (var e in run.Of(TrialEventKind.Outcome))
        {
            var made = moves.Where(m => m.Item1 == e).Select(m => m.Item2).ToList();
            CollectionAssert.AreEqual(e.Trial.SpoutsAlwaysIn ? new LickSpoutCommand[0] : new[] { LickSpoutCommand.SpoutsOut }, made, "trial " + e.Trial.trialNumber);
        }
        foreach (var e in run.Of(TrialEventKind.Cue).Where(e => e.Cue == OpenRigs.Trials.Spouts.InCue))
        {
            CollectionAssert.AreEqual(new[] { e.Trial.SpoutsAlwaysIn ? LickSpoutCommand.SpoutsIn : e.Trial.SpoutsInCommand }, moves.Where(m => m.Item1 == e).Select(m => m.Item2).ToList());
        }
        Trial coming = null;
        foreach (var e in run.Events)
        {
            if (e.Kind == TrialEventKind.Iti) coming = e.Trial;
            if (e.Kind == TrialEventKind.Cue && e.Cue == OpenRigs.Trials.Spouts.OutCue)
                Assert.AreEqual(!coming.SpoutsAlwaysIn, moves.Any(m => m.Item1 == e && m.Item2 == LickSpoutCommand.SpoutsOut), "SpoutsOut before trial " + coming.trialNumber);
        }
        Assert.AreEqual(trials[0].SpoutsAlwaysIn ? 0 : 1, moves.Count(m => m.Item1.Kind == TrialEventKind.Iti), "trial 1's interval start alone, unless it stays in");
    }

    [TestMethod]
    public void GoNoGoSendsTheLeftSpoutInEveryTrial()
    {
        var run = new Harness(Rules.Make(file: "task-discrimination-go-no-go.yml")).RunTo(25);
        var spoutsIn = run.Of(TrialEventKind.Cue).Where(e => e.Cue == OpenRigs.Trials.Spouts.InCue).ToList();
        Assert.IsTrue(spoutsIn.Count >= 2 && spoutsIn.All(e => e.Trial.SpoutsInCommand == LickSpoutCommand.LeftOnly));
        Assert.IsFalse(Commands(run).Any(c => c == LickSpoutCommand.SpoutsIn || c == LickSpoutCommand.RightOnly));
    }
}

/// <summary>Each trial's start, from the interval's start at 10 s and its 2 s interval, on the stage's quiet waits (both spouts).</summary>
[TestClass]
public class TrialStartTests
{
    static Harness Run(double lead = 0.5, double lick = 0, double wheelSeconds = 0, double wheelCm = 0)
    {
        return new Harness(Rules.Make(t =>
        {
            t.Optogenetics.LaserToStimulusSeconds = lead;
            var s = t.TrialStructure;
            s.LickQuietSeconds = lick;
            s.WheelQuietSeconds = wheelSeconds;
            s.WheelQuietCm = wheelCm;
        }));
    }

    static double Start(Harness run)
    {
        run.RunTo(20);
        return run.Of(TrialEventKind.Start)[0].Seconds;
    }

    [TestMethod]
    public void WithTheWaitsOffTheStimulusIsExactlyTheIntervalAfterItsStart()
    {
        foreach (var lead in new[] { 0.5, 0.0, -0.5 })
        {
            var run = Run(lead);
            Assert.AreEqual(12 - Math.Max(lead, 0), Start(run), 1e-12);
            Assert.AreEqual(12, run.Of(TrialEventKind.Stimulus)[0].Seconds, 1e-12, "no time added by Zapit or the waits");
            Assert.AreEqual(10, run.Row().itiStartTime, 1e-12);
            Assert.AreEqual(0, run.Row().quietWaitSeconds, 1e-12);
        }
    }

    [TestMethod]
    public void ALickRestartsTheLickWaitAndTheLeadStaysExact()
    {
        Assert.AreEqual(11.5, Start(Run(0.5, 1.0)), 1e-12);   // quiet from 10 s: the interval's end less the lead
        var licked = Run(0.5, 1.0).At(11.0, h => h.Touch(0, true)).At(11.2, h => h.Touch(0, false));
        Assert.AreEqual(12.2, Start(licked), 1e-9);          // a second after the release
        Assert.AreEqual(0.7, licked.Row().quietWaitSeconds, 1e-9);
        Assert.AreEqual(12.7, licked.Of(TrialEventKind.Stimulus)[0].Seconds, 1e-9);
        var both = Run(0.5, 1.0).At(11.0, h => h.Touch(0, true)).At(11.2, h => h.Touch(0, false))
                                .At(11.4, h => h.Touch(1, true)).At(11.45, h => h.Touch(1, false));
        Assert.AreEqual(12.45, Start(both), 1e-9, "a later lick on the other spout restarts it again");
    }

    [TestMethod]
    public void AHeldContactHoldsTheTrialStartUntilReleased()
    {
        Assert.AreEqual(14.0, Start(Run(0.5, 1.0).At(10.5, h => h.Touch(0, true)).At(13.0, h => h.Touch(0, false))), 1e-9);
        Assert.AreEqual(11.5, Start(Run(0.5).At(10.5, h => h.Touch(0, true)).At(13.0, h => h.Touch(0, false))), 1e-9, "the lick wait off");
    }

    [TestMethod]
    public void TheWheelWaitCountsTheLastWheelQuietSeconds()
    {
        // Rocking forward and back 0.6 cm a time: net 0, 1.2 cm within a second, so the start a second after the first swing.
        var rocked = Run(0.5, 0, 1.0, 1.0).At(11.0, h => h.Wheel(0.6)).At(11.3, h => h.Wheel(0.0));
        Assert.AreEqual(12.0, Start(rocked), 1e-9);
        Assert.AreEqual(0.5, rocked.Row().quietWaitSeconds, 1e-9);
        Assert.AreEqual(11.5, Start(Run(0.5, 0, 1.0, 1.0).At(11.0, h => h.Wheel(0.6))), 1e-9, "0.6 cm alone stays within 1 cm");
        var both = Run(0.5, 1.0, 1.0, 1.0).At(11.0, h => h.Wheel(0.6)).At(11.3, h => h.Wheel(0.0))
                                         .At(11.8, h => h.Touch(0, true)).At(11.85, h => h.Touch(0, false));
        Assert.AreEqual(12.85, Start(both), 1e-9, "each wait restarts on its own; the start needs both");
    }

    [TestMethod]
    public void SlowFidgetingWithinTheThresholdNeverDelaysTheTrialStart()
    {
        var run = Run(0.5, 0, 1.0, 1.0);
        for (int i = 1; i <= 4; i++) { var cm = 0.3 * i; run.At(Math.Round(9.8 + 0.4 * i, 3), h => h.Wheel(cm)); }
        Assert.AreEqual(11.5, Start(run), 1e-9);
    }

    [TestMethod]
    public void TheChoicesAreTheWordsTheChoiceColumnRecords()
    {
        CollectionAssert.AreEqual(new[] { "left", "right", "go", "noResponse" }, new[] { Choice.Left, Choice.Right, Choice.Go, Choice.NoResponse });
    }

    [TestMethod]
    public void TheStimulusComesOnTheFirstTickAtOrAfterItsDueTime()
    {
        // A drawn interval is never a whole number of ticks: due at 12.0004 s, the stimulus is commanded on the 12.001 s tick.
        var run = new Harness(Rules.Make(t => t.TrialStructure.ItiMinSeconds = t.TrialStructure.ItiMaxSeconds = 2.0004));
        Assert.AreEqual(11.5004, Start(run), 1e-12);
        Assert.AreEqual(12.001, run.Of(TrialEventKind.Stimulus)[0].Seconds, 1e-12);
        Assert.AreEqual(12.001, run.Row().stimulusOnsetTime, 1e-12);
    }
}

[TestClass]
public class LaserTests
{
    [TestMethod]
    public void TheLaserRunsForItsDurationAndTheNextArmWaitsForTheRampDown()
    {
        // laserDurationSeconds 1.3 (within the 1.85 s this task allows: 0.5 + 0.2 s to the trial's end + itiMinSeconds 2, less
        // the 0.65 s a ZapitDaq trial outlasts its laser and the 0.2 s arm time) so the outcome, from 12.5 s, still comes
        // before the line falls; a 0.2 s window (the stimulus 0.5 s to fit it) ends the trial at 12.7 s, where trial 2's
        // interval starts, before Zapit is free.
        var rules = Rules.Make(t => { Rules.Optogenetics(t); t.Optogenetics.LaserDurationSeconds = 1.3; t.TrialStructure.ResponseWindowSeconds = 0.2; t.Stimulus.DurationSeconds = 0.5; });
        Assert.AreEqual(LaserEnd.AfterDuration, rules.Laser.End);
        Assert.AreEqual(Fixture.RampDownSeconds, rules.Laser.RampDownSeconds);
        var run = new Harness(rules);
        Assert.AreEqual(10.0, run.Of(TrialEventKind.Arm)[0].Seconds, "armed as the interval starts");
        Assert.AreEqual(1.3, LaserTiming.DurationOf(run.Of(TrialEventKind.Arm)[0].Trial), "each trial's own laserDurationSeconds");
        run.Loop.OnZapitEvent(Rules.Armed(run.Of(TrialEventKind.Arm)[0].Trial));
        run.RunTo(12.05);
        Assert.AreEqual(11.5, run.Of(TrialEventKind.Trigger)[0].Seconds, 1e-12, "laserToStimulusSeconds before the stimulus");
        run.Loop.OnZapitEvent(new ZapitDaqEvent { Kind = ZapitDaqEventKind.Started, Trial = run.Of(TrialEventKind.Arm)[0].Trial.trialNumber });   // the samples began: the row logs the power
        var right = run.Spout(true);
        run.At(12.6, h => h.Lick(right)).RunTo(13.5);
        Assert.AreEqual(12.8, run.Of(TrialEventKind.TriggerOff)[0].Seconds, 1e-9, "laserDurationSeconds after the trigger, outcome or not");
        var row = run.Row();
        Assert.IsTrue(row.laserCondition > 0 && row.laserPowerMw == 3 && row.laserTriggerTime == 11.5 && row.laserOn && row.laserStarted);
        Assert.AreEqual(13.35, run.Of(TrialEventKind.Arm)[1].Seconds, 1e-9, "trial 2's interval began at 12.7; Zapit is free once the whole trial (the 1.3 s laser and the 0.5 s ramp) has played, plus ZapitDaqTrial.FreeMarginSeconds");
    }

    [TestMethod]
    public void WithLaserEndStimulusOffTheLaserEndsWithTheStimulusCappedAtItsDuration()
    {
        // The stimulus off 0.1 s after the answer (no duration of its own); laserDurationSeconds 1.3 caps the laser at 12.8 s.
        // A lick at 12.6 s: the stimulus and the laser off at 12.7 s, the line once ZapitDaq's early stop has played (a scan
        // cycle, the ramp-down and a margin; earlyStopLeadSeconds 0 here) later. No lick: the cap stops the laser at 12.8 s,
        // before the stimulus goes off at the window's end.
        Action<UclOpenCoenDomeDecisionMakingTaskParameters> edit = t =>
        {
            Rules.Optogenetics(t);
            t.Optogenetics.LaserEnd = LaserEndMode.StimulusOff;
            t.Optogenetics.LaserDurationSeconds = 1.3;
            t.Stimulus.DurationSeconds = null;
            t.Stimulus.ResponseToStimulusOffSeconds = 0.1;
        };
        var hold = ZapitDaqTrial.FreeAfterLaserSeconds(Fixture.Bank());
        var rules = Rules.Make(edit);
        Assert.IsTrue(rules.Laser.End == LaserEnd.AtStimulusOff && rules.Laser.HardwareTriggered);
        Assert.AreEqual(1.3, rules.Laser.LaserCapSeconds, 1e-12, "the longest laserDurationSeconds, LaserBackstop's wait");
        Assert.AreEqual(hold, rules.Laser.TriggerHoldSeconds, 1e-12);
        var run = new Harness(rules);
        run.Loop.OnZapitEvent(Rules.Armed(run.Of(TrialEventKind.Arm)[0].Trial));
        run.RunTo(12.05);
        var right = run.Spout(true);
        run.At(12.6, h => h.Lick(right)).RunTo(13.5);
        Assert.AreEqual(12.7, run.Of(TrialEventKind.StimulusOff)[0].Seconds, 1e-9);
        Assert.AreEqual(12.7, run.Of(TrialEventKind.LaserOff)[0].Seconds, 1e-9, "the laser ends with the stimulus, after the outcome");
        Assert.AreEqual(12.7 + hold, run.Of(TrialEventKind.TriggerOff)[0].Seconds, 1e-9);
        Assert.AreEqual(0, run.Of(TrialEventKind.LaserCap).Count);

        var capped = new Harness(Rules.Make(edit));
        capped.Loop.OnZapitEvent(Rules.Armed(capped.Of(TrialEventKind.Arm)[0].Trial));
        capped.RunTo(14.7);
        Assert.AreEqual(12.8, capped.Of(TrialEventKind.LaserCap)[0].Seconds, 1e-9, "laserDurationSeconds after the trigger");
        Assert.AreEqual(0, capped.Of(TrialEventKind.LaserOff).Count, "stopped by the cap");
        Assert.AreEqual(12.8 + hold, capped.Of(TrialEventKind.TriggerOff)[0].Seconds, 1e-9);
    }

    [TestMethod]
    public void WithLaserEndStimulusOffATimedStimulusEndsTheLaserPastTheOutcome()
    {
        // The stimulus 0.7 s long (12 to 12.7 s), the cap 1.3 s (12.8 s; main's MATLAB test has a 1 s stimulus and a 1.6 s cap,
        // over the 1.3 s ZapitDaq allows here): a lick at 12.6 s answers, and the laser runs on to the stimulus off at 12.7 s.
        var rules = Rules.Make(t =>
        {
            Rules.Optogenetics(t);
            t.Optogenetics.LaserEnd = LaserEndMode.StimulusOff;
            t.Optogenetics.LaserDurationSeconds = 1.3;
            t.Stimulus.DurationSeconds = 0.7;
        });
        var hold = ZapitDaqTrial.FreeAfterLaserSeconds(Fixture.Bank());
        Assert.IsTrue(rules.Laser.UntilStopped, "the Arm carries no duration");
        var run = new Harness(rules);
        run.Loop.OnZapitEvent(Rules.Armed(run.Of(TrialEventKind.Arm)[0].Trial));
        run.RunTo(11.55);
        Assert.AreEqual(11.5, run.Of(TrialEventKind.Trigger)[0].Seconds, 1e-12);
        var right = run.Spout(true);
        run.At(12.6, h => h.Lick(right)).RunTo(13.7);
        Assert.AreEqual(12.6, run.Of(TrialEventKind.Outcome)[0].Seconds, 1e-9);
        Assert.AreEqual(12.7, run.Of(TrialEventKind.StimulusOff)[0].Seconds, 1e-9);
        Assert.AreEqual(12.7, run.Of(TrialEventKind.LaserOff)[0].Seconds, 1e-9, "the laser ends with the stimulus, after the outcome");
        Assert.AreEqual(12.7 + hold, run.Of(TrialEventKind.TriggerOff)[0].Seconds, 1e-9);
        Assert.AreEqual(0, run.Of(TrialEventKind.LaserCap).Count);
    }

    [TestMethod]
    public void WithLaserEndStimulusOffAsOnTheRigTheLaserEndsAtStimulusOffNotTheOutcome()
    {
        // The rig's session (25 Nov 2026): a 1.5 s stimulus 0.1 s after the trigger, auto-rewarded misses, the window in full to
        // 2.5 s after the onset, a 2 s cap, earlyStopLeadSeconds 0.07. Each trial's LaserOff, so its early stop, is at its
        // StimulusOff, a second before the outcome; the next trial is armed at the outcome. ZapitDaq's Stopped logged at the
        // outcome was that Arm ending a buffer NI had played to its end (lib-open-rigs: the Arm now ends the samples there).
        // With laserEnd duration nothing stops the laser early.
        Action<UclOpenCoenDomeDecisionMakingTaskParameters> rig = t =>
        {
            Rules.Optogenetics(t);
            t.TrialStructure.ItiMinSeconds = 2.5;
            t.TrialStructure.ItiMaxSeconds = 2.5;
            t.Optogenetics.LaserEnd = LaserEndMode.StimulusOff;
            t.Optogenetics.LaserDurationSeconds = 2;
            t.Optogenetics.LaserToStimulusSeconds = 0.1;
            t.Stimulus.DurationSeconds = 1.5;
            t.AutoRewardProbability = 1;
        };
        var run = new Harness(Rules.Make(rig, earlyStopLeadSeconds: 0.07));
        for (var n = 1; n <= 3; n++)
        {
            run.Loop.OnZapitEvent(Rules.Armed(run.Of(TrialEventKind.Arm)[n - 1].Trial));
            while (run.Of(TrialEventKind.Arm).Count == n) run.RunTo(run.Now + 0.001);
        }
        for (var i = 0; i < 3; i++)
        {
            var off = run.Of(TrialEventKind.StimulusOff)[i].Seconds;
            var outcome = run.Of(TrialEventKind.Outcome)[i].Seconds;
            Assert.AreEqual(run.Of(TrialEventKind.Stimulus)[i].Seconds + 1.5, off, 1e-9);
            Assert.AreEqual(off, run.Of(TrialEventKind.LaserOff)[i].Seconds, 1e-9, "the early stop at StimulusOff");
            Assert.AreEqual(off + 1, outcome, 1e-9, "not at the outcome");
            Assert.AreEqual(outcome + 0.001, run.Of(TrialEventKind.Arm)[i + 1].Seconds, 1e-9, "the next trial armed at the outcome");
        }
        Assert.AreEqual(0, run.Of(TrialEventKind.LaserCap).Count);

        var timed = new Harness(Rules.Make(t => { rig(t); t.Optogenetics.LaserEnd = LaserEndMode.Duration; }, earlyStopLeadSeconds: 0.07));
        timed.Loop.OnZapitEvent(Rules.Armed(timed.Of(TrialEventKind.Arm)[0].Trial));
        while (timed.Of(TrialEventKind.Arm).Count == 1) timed.RunTo(timed.Now + 0.001);
        Assert.AreEqual(1, timed.Of(TrialEventKind.StimulusOff).Count);
        Assert.AreEqual(0, timed.Of(TrialEventKind.LaserOff).Count + timed.Of(TrialEventKind.LaserCap).Count, "laserEnd duration: no early stop");
    }

    [TestMethod]
    public void WithLaserEndStimulusOffGoAddsTheEarlyStopLead()
    {
        // rig.yml's earlyStopLeadSeconds lengthens the hold and shortens the longest cap Go allows, under stimulusOff alone.
        Action<UclOpenCoenDomeDecisionMakingTaskParameters> off = t =>
        {
            Rules.Optogenetics(t);
            t.Optogenetics.LaserEnd = LaserEndMode.StimulusOff;
            t.Optogenetics.LaserDurationSeconds = 3.2;
            t.Stimulus.DurationSeconds = 0.6;
        };
        var free = ZapitDaqTrial.FreeAfterLaserSeconds(Fixture.Bank());
        Assert.AreEqual(free + 0.1, Rules.Make(off, earlyStopLeadSeconds: 0.1).Laser.TriggerHoldSeconds, 1e-12);
        // The limit is 0.5 s to the window + the 2 s window (always in full: the trial's end) + 2 s interval - free - lead - 0.2 s
        // arm: this lead (0.5 s) brings it to 3.15 s, under the 3.2 s cap.
        var lead = 0.5 + 2 + 2 - free - ZapitDaqTrial.ArmSeconds - 3.15;
        Assert.AreEqual(0.5, lead, 1e-9);
        Rules.Make(off, earlyStopLeadSeconds: lead - 0.1);
        StringAssert.Contains(Fixture.Refusal(() => Rules.Make(off, earlyStopLeadSeconds: lead)), "optogenetics.laserDurationSeconds (3.2 s) must be at most 3.15 s");
        Rules.Make(t => { Rules.Optogenetics(t); t.Optogenetics.LaserDurationSeconds = 3.2; t.Stimulus.DurationSeconds = 0.6; }, earlyStopLeadSeconds: lead);   // duration: no early stop, no lead
    }

    [TestMethod]
    public void WithTheTaskFilesTimingGoAcceptsTheirLaserNatively()
    {
        // The shipped task files as they are (0.5 s to the window, the 2 s window, itiMinSeconds 2.5, the trigger 0.1 s ahead,
        // a 2.5 s laser), optogenetics ticked: Go accepts them on the test bank (the limit 4.15 s, its 0.5 s ramp-down) with
        // laserEnd duration, and with stimulusOff and the lab's 0.08 s early-stop lead (4.07 s). Before the limit counted
        // from the window's end (2.15 s and 2.07 s here) both were refused.
        foreach (var file in new[] { "task-localization.yml", "task-discrimination-left-right.yml", "task-discrimination-go-no-go.yml" })
        {
            foreach (var end in new[] { LaserEndMode.Duration, LaserEndMode.StimulusOff })
            {
                var task = Fixture.Task(file, 1);
                task.Optogenetics.Enabled = true;
                task.Optogenetics.LaserPowerMw = task.Optogenetics.LaserPowerMw ?? 3;   // the discriminations' files leave it to the experimenter
                task.Optogenetics.LaserEnd = end;
                Assert.AreEqual(2.5, task.Optogenetics.LaserDurationSeconds);
                Assert.AreEqual("accepted", Fixture.Refusal(() => new TrialRules(Fixture.Text(task), Fixture.Speakers(), Fixture.Bank(), 0.08)), file + " " + end);
                task.Optogenetics.LaserDurationSeconds = end == LaserEndMode.Duration ? 4.2 : 4.1;
                StringAssert.Contains(Fixture.Refusal(() => new TrialRules(Fixture.Text(task), Fixture.Speakers(), Fixture.Bank(), 0.08)),
                    end == LaserEndMode.Duration ? "must be at most 4.15 s" : "must be at most 4.07 s", file + " " + end);
            }
        }
    }

    [TestMethod]
    public void WithLaserEndStimulusOffGoRefusesACapOrTriggerTheStimulusOffMakesPointless()
    {
        // The trigger 0.5 s before a 1 s stimulus: its off 1.5 s after the trigger, past a 1.4 s cap.
        StringAssert.Contains(Fixture.Refusal(() => Rules.Make(t =>
        {
            Rules.Optogenetics(t);
            t.Optogenetics.LaserEnd = LaserEndMode.StimulusOff;
            t.Optogenetics.LaserDurationSeconds = 1.4;
            t.Stimulus.DurationSeconds = 1.0;
        })), "optogenetics.laserDurationSeconds (1.4 s) is the laser's cap with optogenetics.laserEnd stimulusOff, but the stimulus goes off 1.5 s after the trigger (optogenetics.laserToStimulusSeconds 0.5 s + stimulus.durationSeconds 1 s)");
        // The trigger 1 s after the onset of a 1 s stimulus: already off.
        StringAssert.Contains(Fixture.Refusal(() => Rules.Make(t =>
        {
            Rules.Optogenetics(t);
            t.Optogenetics.LaserEnd = LaserEndMode.StimulusOff;
            t.Optogenetics.LaserToStimulusSeconds = -1.0;
            t.Stimulus.DurationSeconds = 1.0;
        })), "optogenetics.laserToStimulusSeconds (-1 s) triggers the laser 1 s after the stimulus onset, when the stimulus (stimulus.durationSeconds 1 s) is already off");
        // The same with a laser for its duration: no such rule.
        Rules.Make(t => { Rules.Optogenetics(t); t.Optogenetics.LaserDurationSeconds = 1.4; t.Stimulus.DurationSeconds = 1.0; });
        Rules.Make(t => { Rules.Optogenetics(t); t.Optogenetics.LaserToStimulusSeconds = -1.0; t.Stimulus.DurationSeconds = 1.0; });
    }

    [TestMethod]
    public void ATrialAnsweredBeforeItsTriggerHasNoLaserAndItsRowSaysSo()
    {
        // The trigger 0.9 s after the stimulus (12.9 s); a lick at 12.6 s answers first.
        var run = new Harness(Rules.Make(t => { Rules.Optogenetics(t); t.Optogenetics.LaserToStimulusSeconds = -0.9; }));
        run.Loop.OnZapitEvent(Rules.Armed(run.Of(TrialEventKind.Arm)[0].Trial));
        run.RunTo(12.05).At(12.6, h => h.Lick(0)).RunTo(13.5);
        Assert.AreEqual(0, run.Of(TrialEventKind.Trigger).Count);
        Assert.AreEqual(0, run.Row().laserPowerMw);
        Assert.IsTrue(double.IsNaN(run.Row().laserTriggerTime));
        Assert.IsTrue(run.Row().laserOn && !run.Row().laserStarted, "drawn on, never started");
    }

    [TestMethod]
    public void ZapitNotArmedByTheTriggerStopsTheWorkflow()
    {
        var run = new Harness(Rules.Make(Rules.Optogenetics));
        StringAssert.Contains(Fixture.Refusal(() => run.RunTo(12)), "ZapitDaq had not armed trial 1 by its trigger");
    }

    [TestMethod]
    public void TheTrialGoesOnOnlyOnceZapitArmedItAsAsked()
    {
        Func<Action<ZapitDaqEvent>, string> refusal = change =>
        {
            var run = new Harness(Rules.Make(Rules.Optogenetics));
            var trial = run.Of(TrialEventKind.Arm)[0].Trial;
            var reply = Rules.Armed(trial);
            change(reply);
            return Fixture.Refusal(() => run.Loop.OnZapitEvent(reply));
        };
        Assert.AreEqual("accepted", refusal(r => { }));
        StringAssert.Contains(refusal(r => r.Condition = r.Condition + 1), "asked for condition ");
        StringAssert.Contains(refusal(r => r.LaserOn = !r.LaserOn), "laser off) when trial 1 asked");
        StringAssert.Contains(refusal(r => r.Trial = 2), "ZapitDaq armed trial 2");
    }

    [TestMethod]
    public void WithoutOptogeneticsNoTrialIsArmedOrTriggered()
    {
        var rules = Rules.Make();
        Assert.IsNull(rules.Laser);
        var run = new Harness(rules).RunTo(35);
        Assert.IsTrue(run.Outcomes.Count >= 3 && run.Of(TrialEventKind.Arm).Count == 0 && run.Of(TrialEventKind.Trigger).Count == 0);
        Assert.IsTrue(run.Outcomes.All(o => o.Row.Value.laserPowerMw == 0 && double.IsNaN(o.Row.Value.laserTriggerTime)));
    }
}

[TestClass]
public class TrialRowTests
{
    static TrialTimes Times(double now)
    {
        return new TrialTimes { ItiStartTime = 50, ItiSeconds = 3, QuietWaitSeconds = 0.2, TrialStartTime = 8.5, StimulusOnsetTime = 9, StimulusOffTime = 10.5, LaserTriggerTime = double.NaN, Now = now };
    }

    [TestMethod]
    public void TheRowSaysWhatTheOutcomeCost()
    {
        var trial = new Trial { target = TargetWords.Left, autoReward = false, itiSeconds = 3, values = Fixture.Task("task-localization.yml", 4) };
        var incorrect = TrialRow.Build(Choice.Right, trial, Times(10), Penalties.Of(4, -35.0, 0, 10));
        Assert.IsTrue(incorrect.outcome == TrialOutcomes.Incorrect && incorrect.penalised && incorrect.penaltyTimeoutSeconds == 4 && incorrect.outcomeTime == 10);
        Assert.AreEqual(10.5, incorrect.stimulusOffTime, 1e-12, "the loop's StimulusOffTime (StimulusTiming), through SharedTrialColumns");
        Assert.IsTrue(incorrect.itiSeconds == 3 && incorrect.itiStartTime == 50 && incorrect.quietWaitSeconds == 0.2 && incorrect.trialStartTime == 8.5 && incorrect.stimulusOnsetTime == 9);
        // A miss: the outcome at the end of the response window, and the penalty sound with its timeout.
        var miss = TrialRow.Build(Choice.NoResponse, trial, Times(10), Penalties.Of(4, -35.0, 0, 10));
        Assert.IsTrue(miss.outcome == TrialOutcomes.NoResponse && miss.penalised && miss.penaltyTimeoutSeconds == 4 && !miss.rewarded);
        trial.autoReward = true;
        var rewardedMiss = TrialRow.Build(Choice.NoResponse, trial, Times(10), Penalties.Of(4, -35.0, 0, 10));
        Assert.IsTrue(rewardedMiss.outcome == TrialOutcomes.Auto && rewardedMiss.autoReward && rewardedMiss.rewarded && rewardedMiss.penaltyTimeoutSeconds == 0 && !rewardedMiss.penalised, "an auto-rewarded miss: outcome auto, autoReward and rewarded");
        var correct = TrialRow.Build(Choice.Left, trial, Times(10), Penalties.Of(4, -35.0, 0, 10));
        Assert.AreEqual(TrialOutcomes.Incorrect, TrialRow.Build(Choice.Right, trial, Times(10), Penalties.Of(4, -35.0, 0, 10)).outcome, "a side chosen on an auto-reward trial keeps its outcome");
        var goTrial = new Trial { target = TargetWords.Go, autoReward = true, values = trial.values };
        Assert.AreEqual(TrialOutcomes.Auto, TrialRow.Build(Choice.NoResponse, goTrial, Times(10), Penalties.Of(4, -35.0, 0, 10)).outcome, "go/no-go: an auto-rewarded go miss");
        Assert.AreEqual(TrialOutcomes.Correct, TrialRow.Build(Choice.Go, goTrial, Times(10), Penalties.Of(4, -35.0, 0, 10)).outcome, "go/no-go: a licked auto-reward go trial");
        Assert.IsTrue(correct.outcome == TrialOutcomes.Correct && !correct.penalised && correct.penaltyTimeoutSeconds == 0 && correct.rewarded);
    }

    [TestMethod]
    public void TheRowLogsTheConditionAndThePowerFired()
    {
        var trial = new Trial { target = TargetWords.Left, laserCondition = 2, laserPointsMm = "1.5 1;1.5 -1", laserOn = true, laserPowerMw = 3, values = Fixture.Task("task-localization.yml", 4) };
        var times = Times(10);
        times.LaserTriggerTime = 9.25;
        times.LaserPowerMw = 2.5;   // ZapitDaq's Armed CappedPowerMw: the 3 mW asked for, capped by the rig's ceiling
        times.LaserStarted = true;
        var row = TrialRow.Build(Choice.Left, trial, times, Penalties.Of(4, -35.0, 0, 10));
        Assert.IsTrue(row.laserCondition == 2 && row.laserPointsMm == "1.5 1;1.5 -1" && row.laserPowerMw == 2.5 && row.laserTriggerTime == 9.25 && row.laserOn && row.laserStarted);
        var answered = TrialRow.Build(Choice.Left, trial, Times(10), Penalties.Of(4, -35.0, 0, 10));
        Assert.IsTrue(answered.laserCondition == 2 && answered.laserPointsMm == "1.5 1;1.5 -1" && answered.laserPowerMw == 0 && double.IsNaN(answered.laserTriggerTime), "answered before its trigger");
    }

    [TestMethod]
    public void TheRowsColumnsAreTheTrialColumnsFile()
    {
        var listed = System.IO.File.ReadAllLines(System.IO.Path.Combine(Fixture.Repo(), "schemas", "trial-columns.yml"))
            .Where(line => line.Length > 0 && char.IsLetter(line[0]) && line.EndsWith(":")).Select(line => line.TrimEnd(':')).ToList();
        // The lab's CsvWriter writes the row's timestamp first, as harpTime (scripts\check-workflow.ps1 holds the real header).
        var fields = new List<string> { "harpTime" };
        fields.AddRange(typeof(TrialRow).GetFields().Select(f => f.Name));
        CollectionAssert.AreEqual(listed, fields);
    }
}

[TestClass]
public class ConsequenceTests
{
    static TrialOutcome Of(string choice, bool autoReward, bool movements)
    {
        var trial = Rules.Made(Choice.Right, 3, !movements);
        trial.autoReward = autoReward;
        return TrialOutcome.At(choice, trial, new TrialTimes { Now = 1 });
    }

    [TestMethod]
    public void ACorrectChoiceIsRewardedAtTheTargetWithTheOtherSpoutDrawnBack()
    {
        var c = Of(Choice.Right, false, true);
        Assert.IsTrue(c.Rewarded && c.Reward.Item1 == "right" && c.Reward.Item2 == 3 && c.SpoutCommand == LickSpoutCommand.RightOnly);
        Assert.IsTrue(!c.Penalised && c.PenaltyTimeoutSeconds == 0);
    }

    [TestMethod]
    public void AnErrorDrawsBothSpoutsBackWithTheSoundAndTimeout()
    {
        var c = Of(Choice.Left, false, true);
        Assert.IsTrue(!c.Rewarded && c.Reward == null && c.SpoutCommand == LickSpoutCommand.SpoutsOut && c.Penalised && c.PenaltyTimeoutSeconds == 4);
        var miss = Of(Choice.NoResponse, false, true);  // the sound comes with every timeout
        Assert.IsTrue(!miss.Rewarded && miss.Penalised && miss.PenaltyTimeoutSeconds == 4);
        var auto = Of(Choice.NoResponse, true, false);
        Assert.IsTrue(auto.Rewarded && auto.Reward.Item1 == "right" && auto.PenaltyTimeoutSeconds == 0 && !auto.Penalised);
        Assert.IsTrue(!c.Row.Value.rewarded && c.Row.Value.penalised && c.Row.Value.rewardOfferedUl == 3, "an error: offered, penalised");
        Assert.IsTrue(!miss.Row.Value.rewarded && miss.Row.Value.penalised && miss.Row.Value.rewardOfferedUl == 3, "a miss keeps the offer");
        Assert.IsTrue(auto.Row.Value.rewarded && !auto.Row.Value.penalised);
    }

    [TestMethod]
    public void ARandomTrialsCoinDecidesTheRowsRewardedAndPenalised()
    {
        // Random (penaliseRandomProbes false): heads rewards a choice at the spout chosen, tails leaves it unrewarded and
        // unpenalised, a miss costs nothing; the offer is on the row every time, and the valve's command is the row's rewarded.
        Func<string, bool, TrialOutcome> score = (choice, up) =>
        {
            var t = Rules.Made(TargetWords.Random, 3);
            t.probeHeads = up;
            return TrialOutcome.At(choice, t, new TrialTimes { Now = 1 });
        };
        var heads = score(Choice.Left, true);
        Assert.IsTrue(heads.Row.Value.rewarded && !heads.Row.Value.penalised && heads.Reward.Equals(Tuple.Create("left", 3.0)) && heads.SpoutCommand == LickSpoutCommand.LeftOnly);
        var tails = score(Choice.Right, false);
        Assert.IsTrue(!tails.Row.Value.rewarded && !tails.Row.Value.penalised && tails.Reward == null && tails.PenaltyTimeoutSeconds == 0 && !tails.Penalised);
        foreach (var coin in new[] { true, false })
        {
            var miss = score(Choice.NoResponse, coin);
            Assert.IsTrue(!miss.Row.Value.rewarded && !miss.Row.Value.penalised && miss.Reward == null && miss.PenaltyTimeoutSeconds == 0);
        }
        foreach (var o in new[] { heads, tails, score(Choice.NoResponse, true) })
            Assert.IsTrue(o.Row.Value.outcome == TrialOutcomes.Probe && o.Row.Value.rewardOfferedUl == 3 && o.Row.Value.target == TargetWords.Random);
    }

    [TestMethod]
    public void TheSpoutsComeInAsTheTrialSays()
    {
        Assert.AreEqual(LickSpoutCommand.SpoutsIn, (new Trial { singleSpoutOffered = false, target = TargetWords.Left }).SpoutsInCommand);
        Assert.AreEqual(LickSpoutCommand.LeftOnly, (new Trial { singleSpoutOffered = true, target = TargetWords.Left }).SpoutsInCommand);
        Assert.AreEqual(LickSpoutCommand.RightOnly, (new Trial { singleSpoutOffered = true, target = TargetWords.Right }).SpoutsInCommand);
    }
}

[TestClass]
public class CheckTaskAtGoTests
{
    [TestMethod]
    public void TheTaskIsCheckedOnceAtGoOnTheRigsSpeakers()
    {
        var rig = new LabRig { SpeakerArray = Fixture.Speakers() };
        var unfilled = Fixture.Task("task-localization.yml", 4);
        unfilled.Curriculum = null;
        StringAssert.Contains(Fixture.Refusal(() => TrialRules.Create(Fixture.Text(unfilled), rig, null)), "The task file's curriculum is empty");
        var rules = TrialRules.Create(Fixture.Text(Fixture.Task("task-discrimination-left-right.yml", 4)), rig, null);
        Assert.IsNull(rules.Laser, "no optogenetics");
        Assert.AreEqual("S04A-k7f2", rules.CurriculumStage);
        var timing = rules.Timing(rules.Draw(null));
        Assert.IsTrue(timing.LaserToStimulusSeconds == 0.1 && !timing.Laser && timing.ResponseDelaySeconds == 0.5 && timing.ResponseWindowSeconds == 2.0);
        CollectionAssert.AreEqual(new[] { OpenRigs.Trials.Spouts.InCue, OpenRigs.Trials.Spouts.OutCue }, timing.Cues.Select(c => c.Name).ToList());
        Assert.IsTrue(timing.StimulusDurationSeconds == 1.5 && timing.ResponseToStimulusOffSeconds == null, "stimulus.durationSeconds and responseToStimulusOffSeconds");
    }

    [TestMethod]
    public void RunTrialsRunsTheLoopOnTheWorkflowsStreams()
    {
        var rules = new BehaviorSubject<TrialRules>(Rules.Make());
        var clock = new Subject<double>();
        var events = new List<Event>();
        new RunTrials().Process(rules, clock, new Subject<double>(), new Subject<bool>(), new Subject<bool>(), new Subject<ZapitDaqEvent>()).Subscribe(events.Add);
        for (int ms = 10000; ms <= 12000; ms++) clock.OnNext(ms / 1000.0);
        CollectionAssert.AreEqual(new[] { TrialEventKind.Iti, TrialEventKind.Start, TrialEventKind.Stimulus, TrialEventKind.Update },
                                  events.Select(e => e.Kind).Take(4).ToList());
    }
}
