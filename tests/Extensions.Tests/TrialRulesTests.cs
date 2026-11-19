using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Devices;
using OpenRigs.Devices.Rigs;
using OpenRigs.Devices.Zapit;
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
        Loop = new TrialLoop<Trial, DecisionUpdate, TrialOutcome>(rules, true);
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
    // lead), the stimulus at 12 and, with no open loop, its response window 12 to 13.5. Stage 4 takes the task's weights,
    // here scored trials alone in the two-spout tasks, so every trial has a side to answer (the probes' own test below).
    public static TrialRules Make(Action<UclOpenCoenDomeDecisionMakingTaskParameters> edit = null, bool hardwareTriggered = true, string file = "task-discrimination-left-right.yml", int stage = 4)
    {
        var task = Fixture.Task(file, stage);
        task.TrialStructure.ItiMinSeconds = 2;
        task.TrialStructure.ItiMaxSeconds = 2;
        if (task.TaskType != TaskType.DiscriminationGoNoGo)
        {
            Fixture.Weight(task, "audiovisualConflict", 0);
            Fixture.Weight(task, "neutral", 0);
            if (task.TaskType == TaskType.DiscriminationLeftRight) Fixture.Weight(task, "audiovisualCoherent", 0);
        }
        if (edit != null) edit(task);
        return new TrialRules(Fixture.Text(task), Fixture.Speakers(), hardwareTriggered, path => 2);
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
    public static Trial Made(string target, double rewardOfferedUl, bool spoutsStayIn = false)
    {
        var task = Fixture.Task("task-localization.yml", 4);
        task.SpoutsStayIn = spoutsStayIn;
        var trial = new TrialDraw(Fixture.Text(task), Fixture.Speakers(), path => 2).Next(null);
        trial.target = target;
        trial.rewardOfferedUl = rewardOfferedUl;
        trial.autoReward = trial.probeHeads = trial.singleSpoutOffered = false;
        return trial;
    }

    public static ZapitReply Armed(Trial trial, bool error = false)
    {
        var reply = new ZapitReply();
        reply.Command = ZapitCommandType.StartStimulus;
        reply.Value = trial.laserCondition;
        reply.LaserOn = trial.laserOn ? 1 : 0;
        reply.IsError = error;
        return reply;
    }
}

[TestClass]
public class ResponseRuleTests
{
    [TestMethod]
    public void TheChoiceIsTheSideWhoseNetLicksReachTheThreshold()
    {
        // thresholdLicks 3: left, right, left, left leaves the count at 2; the fifth lick, a left, reaches 3 at 12.5 s.
        Action<UclOpenCoenDomeDecisionMakingTaskParameters> three = t => { t.Response.ThresholdLicks = 3; t.Response.AzimuthShiftPerLickDeg = 10; };
        var run = new Harness(Rules.Make(three));
        foreach (var lick in new[] { Tuple.Create(12.1, 0), Tuple.Create(12.2, 1), Tuple.Create(12.3, 0), Tuple.Create(12.4, 0), Tuple.Create(12.5, 0) })
        {
            var spout = lick.Item2;
            run.At(lick.Item1, h => h.Lick(spout));
        }
        run.RunTo(13);
        var updates = run.Of(TrialEventKind.Update).Select(e => e.Update).ToList();
        CollectionAssert.AreEqual(new[] { 0, 1, 0, 1, 2, 3 }.Select(n => run.First.visualInitialAzimuthDeg + 10 * n).ToList(), updates.Select(u => u.GratingAzimuthDeg).ToList(), "the stimulus resets it; each lick in the window moves it azimuthShiftPerLickDeg");
        Assert.AreEqual(1, run.Outcomes.Count);
        Assert.AreEqual(Choice.Left, run.Row().choice);
        Assert.AreEqual(12.5, run.Row().outcomeTime, 1e-12);
        Assert.AreEqual(12.5, run.Outcomes[0].Row.Seconds, 1e-12, "the row is stamped with its outcome");

        var right = new Harness(Rules.Make(three));
        foreach (var t in new[] { 12.1, 12.2, 12.3 }) right.At(t, h => h.Lick(1));
        right.RunTo(13);
        Assert.AreEqual(Choice.Right, right.Row().choice);
        Assert.AreEqual(12.3, right.Row().outcomeTime, 1e-12);
        Assert.AreEqual(right.First.visualInitialAzimuthDeg - 30, right.Of(TrialEventKind.Update).Last().Update.GratingAzimuthDeg, 1e-9);
    }

    [TestMethod]
    public void ALickBeforeTheResponseWindowIsNotCounted()
    {
        // responseDelaySeconds 0.5: the window opens at 12.5 s. Licks in the interval, the lead and the response delay count for nothing.
        var run = new Harness(Rules.Make(t => t.TrialStructure.ResponseDelaySeconds = 0.5));
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
        // thresholdLicks 2, one lick: the window (0.5 s open loop, then 1.5 s) ends with no choice at 14 s.
        var run = new Harness(Rules.Make(t => { t.TrialStructure.ResponseDelaySeconds = 0.5; t.Response.ThresholdLicks = 2; }));
        run.At(13.0, h => h.Lick(0)).RunTo(13.999);
        Assert.AreEqual(0, run.Outcomes.Count);
        run.RunTo(18.5);
        var row = run.Row();
        Assert.IsTrue(row.choice == Choice.NoResponse && row.outcome == TrialOutcomes.NoResponse && row.outcomeTime == 14.0);
        Assert.IsTrue(row.penalised && row.penaltyTimeoutSeconds == 4, "a miss: the sound and the timeout");
        Assert.AreEqual(18.0, run.Of(TrialEventKind.Iti)[1].Seconds, 1e-9, "the next interval at the outcome plus the timeout");
    }

    [TestMethod]
    public void ARepeatFollowsTheOutcome()
    {
        // Localization stage 4 with every type that has a right answer repeated on failure: a wrong answer repeats the trial, a right one draws anew.
        var run = new Harness(Rules.Make(t => t.Repeats.RepeatOn = RepeatOn.NotCorrect, file: "task-localization.yml"));
        run.RunTo(12.05);
        var wrong = run.Spout(false);
        run.At(12.1, h => h.Lick(wrong)).RunTo(16.2);   // Incorrect at 12.1: the timeout, then trial 2's interval
        run.RunTo(18.0);
        var starts = run.Of(TrialEventKind.Start).Select(e => e.Trial).ToList();
        Assert.AreEqual(2, starts.Count);
        Assert.AreEqual(TrialOutcomes.Incorrect, run.Row().outcome);
        Assert.IsTrue(starts[1].trialNumber == 2 && starts[1].repeatCount == 1 && starts[1].visualInitialAzimuthDeg.Equals(starts[0].visualInitialAzimuthDeg)
                      && starts[1].target == starts[0].target && starts[1].trialType == starts[0].trialType);
        var right = starts[1].target == TargetWords.Left ? 0 : 1;
        run.At(18.2, h => h.Lick(right)).RunTo(21);
        Assert.AreEqual(TrialOutcomes.Correct, run.Row(1).outcome);
        Assert.AreEqual(0, run.Of(TrialEventKind.Start)[2].Trial.repeatCount, "after a correct answer, a new draw");
    }

    [TestMethod]
    public void TheLoopRunsTheDrawsTrialsInOrder()
    {
        // The same seed draws the same trials through the loop as TrialDraw alone, every trial a miss.
        var run = new Harness(new TrialRules(Fixture.Text(Fixture.Task("task-discrimination-left-right.yml", 3)), Fixture.Speakers(), true, path => 2));
        run.RunTo(200);
        var looped = run.Of(TrialEventKind.Start).Select(e => e.Trial).ToList();
        var drawn = Fixture.Draw(Fixture.Task("task-discrimination-left-right.yml", 3), looped.Count, t => Choice.NoResponse);
        Assert.IsTrue(looped.Count >= 20);
        CollectionAssert.AreEqual(drawn.Select(t => t.visualPhaseRad).ToList(), looped.Select(t => t.visualPhaseRad).ToList());
        CollectionAssert.AreEqual(drawn.Select(t => t.trialType + t.target + t.itiSeconds + t.auditorySoundFileIndex).ToList(),
                                  looped.Select(t => t.trialType + t.target + t.itiSeconds + t.auditorySoundFileIndex).ToList());
    }

    [TestMethod]
    public void ATrialWithNoRightAnswerRewardsOrPenalisesEitherChoiceOnACoin()
    {
        // Random-reward probes: left-right's two-modality and neutral trials, localization's conflict and
        // neutral. Both spouts in; a choice at either is rewarded there on heads, penalised as an incorrect choice on tails;
        // a miss as a scored trial's miss; target Random, outcome Probe; the offer kept heads or tails.
        var times = new TrialTimes { Now = 1 };
        foreach (var file in new[] { "task-discrimination-left-right.yml", "task-localization.yml" })
        {
            var task = Fixture.Task(file, 4);
            var all = Fixture.Draw(task, 4000);
            var probes = all.Where(t => t.target == TargetWords.Random).ToList();
            var kinds = file.Contains("left-right") ? new[] { "audiovisualCoherent", "audiovisualConflict", "neutral" } : new[] { "audiovisualConflict", "neutral" };
            CollectionAssert.AreEquivalent(kinds, probes.Select(t => t.trialType).Distinct().ToArray(), file);
            Assert.IsTrue(all.Where(t => t.target != TargetWords.Random).All(t => !kinds.Contains(t.trialType)), file);
            Assert.IsTrue(probes.All(t => !t.singleSpoutOffered && !t.autoReward && SpoutCommands.SpoutsIn(t) == LickSpoutCommand.SpoutsIn), file);
            int rewarded = 0, penalised = 0;
            for (int i = 0; i < probes.Count; i++)
            {
                var t = probes[i];
                var choice = i % 2 == 0 ? Choice.Left : Choice.Right;
                var o = TrialOutcome.Of(TrialRow.Build(choice, t, times, 4), t);
                Assert.AreEqual(TrialOutcomes.Probe, o.Row.Value.outcome);
                Assert.AreNotEqual(o.Rewarded, o.Penalised, "rewarded or penalised, never both or neither");
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
                    penalised++;
                    Assert.IsTrue(!t.probeHeads && o.Reward == null && o.PenaltyTimeoutSeconds == 4 && o.SpoutCommand == LickSpoutCommand.SpoutsOut);
                }
                // A miss: exactly a scored miss (a penalty in these tasks), whatever the coin.
                var miss = TrialOutcome.Of(TrialRow.Build(Choice.NoResponse, t, times, 4), t);
                var scored = all.First(s => s.target != TargetWords.Random && !s.autoReward);
                var scoredMiss = TrialOutcome.Of(TrialRow.Build(Choice.NoResponse, scored, times, 4), scored);
                Assert.IsTrue(miss.Row.Value.outcome == TrialOutcomes.Probe && !miss.Rewarded && !miss.Row.Value.rewarded && miss.Row.Value.penalised, "no choice, nothing paid");
                Assert.AreEqual(t.rewardOfferedUl, miss.Row.Value.rewardOfferedUl, "the offer kept on a miss");
                Assert.IsTrue(miss.Penalised == scoredMiss.Penalised && miss.PenaltyTimeoutSeconds == scoredMiss.PenaltyTimeoutSeconds && scoredMiss.Penalised, file);
            }
            Assert.AreEqual(0.5, rewarded / (double)probes.Count, 0.06, file + ": about half rewarded");
            Assert.IsTrue(probes.Count > 200 && rewarded + penalised == probes.Count);
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
            return TrialOutcome.Of(TrialRow.Build(choice, t, times, 4), t);
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
        run.At(12.2, h => h.Lick(1)).RunTo(12.3);
        Assert.AreEqual(0, run.Outcomes.Count, "a right lick is not counted");
        run.At(12.4, h => h.Lick(0)).RunTo(12.5);
        Assert.AreEqual(Choice.Go, run.Row(0).choice);
        Assert.AreEqual(trial.target == TargetWords.Go ? TrialOutcomes.Correct : trial.target == TargetWords.NoGo ? TrialOutcomes.Incorrect : TrialOutcomes.Probe, run.Row(0).outcome);
    }
}

[TestClass]
public class CueTests
{
    [TestMethod]
    public void TheGratingOffComesFromTheStimulusSpoutsOutFromTheNextItiAndSpoutsInIsDroppedAtTheOutcome()
    {
        // visualStimulusSeconds 1.5, stimulusToSpoutsInSeconds 0.5, outcomeToSpoutsOutSeconds 1.5; a correct lick at 12.2 s, no timeout.
        var run = new Harness(Rules.Make());
        run.RunTo(12.05);
        var right = run.Spout(true);
        run.At(12.2, h => h.Lick(right)).RunTo(16);
        var cues = run.Of(TrialEventKind.Cue).Where(e => e.Trial.trialNumber == 1).ToDictionary(e => e.Cue, e => e.Seconds);
        Assert.IsFalse(cues.ContainsKey(TrialRules.SpoutsInCue), "the outcome came at 12.2 s, before its 12.5 s: dropped, so the spouts are not sent in after the answer");
        Assert.AreEqual(13.5, cues[TrialRules.GratingOffCue], 1e-12, "after the outcome: the grating stays its visualStimulusSeconds");
        Assert.AreEqual(13.7, cues[TrialRules.SpoutsOutCue], 1e-9, "outcomeToSpoutsOutSeconds into the interval after the outcome");
        Assert.AreEqual(12.0, run.Of(TrialEventKind.Stimulus)[0].Seconds, 1e-12);
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
        Assert.AreEqual(12.5, cues[TrialRules.SpoutsInCue], 1e-12);
    }

    [TestMethod]
    public void AfterATimeoutSpoutsOutComesOutcomeToSpoutsOutSecondsIntoTheNextIti()
    {
        var run = new Harness(Rules.Make());
        run.RunTo(12.05);
        var wrong = run.Spout(false);
        run.At(12.2, h => h.Lick(wrong)).RunTo(18);
        Assert.AreEqual(17.7, run.Of(TrialEventKind.Cue).Single(e => e.Cue == TrialRules.SpoutsOutCue).Seconds, 1e-9);
    }

    [TestMethod]
    public void AStayInStageStillHasItsSpoutsOutCueButItsOutcomesKeepTheSpoutsIn()
    {
        // The cue is the loop's, every trial; whether the spouts go out is SpoutCommands' (the coming trial's spoutsStayIn).
        var run = new Harness(Rules.Make(stage: 0)).RunTo(25);
        Assert.IsTrue(run.Outcomes.Count >= 2);
        Assert.IsTrue(run.Of(TrialEventKind.Cue).Any(e => e.Cue == TrialRules.SpoutsOutCue));
        Assert.IsTrue(run.Outcomes.All(o => o.SpoutsStayIn));
    }

    [TestMethod]
    public void AtOutcomeToSpoutsOutZeroTheNextTrialIsDrawnBeforeTheSpoutsOutCue()
    {
        // SpoutsOut is due at the next interval's start; the loop draws that interval's trial first (its Iti event), so
        // SpoutCommands' coming trial is the one whose spoutsStayIn decides, with a timeout or without.
        foreach (var correct in new[] { true, false })
        {
            var run = new Harness(Rules.Make(t => t.TrialStructure.OutcomeToSpoutsOutSeconds = 0));
            run.RunTo(12.05);
            var spout = run.Spout(correct);
            run.At(12.2, h => h.Lick(spout)).RunTo(20);
            var cue = run.Events.FindIndex(e => e.Kind == TrialEventKind.Cue && e.Cue == TrialRules.SpoutsOutCue && e.Trial.trialNumber == 1);
            var iti = run.Events.FindIndex(e => e.Kind == TrialEventKind.Iti && e.Trial.trialNumber == 2);
            Assert.IsTrue(iti >= 0 && cue > iti, correct ? "no timeout" : "after a timeout");
            Assert.AreEqual(run.Events[iti].Seconds, run.Events[cue].Seconds, 0.0011, "on the tick that starts the interval or the next");
        }
    }
}

/// <summary>SpoutCommands: every spout move from the trial events, as LickSpoutStage.Commands takes them.</summary>
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
        CollectionAssert.AreEqual(new[] { LickSpoutCommand.SpoutsOut, SpoutCommands.SpoutsIn(run.First),
            right == 0 ? LickSpoutCommand.LeftOnly : LickSpoutCommand.RightOnly, LickSpoutCommand.SpoutsOut }, commands.Take(4).ToArray());
        var outs = run.Of(TrialEventKind.Cue).Count(e => e.Cue == TrialRules.SpoutsOutCue);
        Assert.AreEqual(1 + outs, commands.Count(c => c == LickSpoutCommand.SpoutsOut));
        CollectionAssert.AreEqual(commands.ToArray(), Commands(run).ToArray(), "each subscription starts at its own trial 1");
    }

    [TestMethod]
    public void WhenTheSpoutsStayInOnlyTheSpoutsInCueMovesThem()
    {
        // Habituation (stage 0): both spouts every trial, sent in at each SpoutsIn cue and never drawn out.
        var run = new Harness(Rules.Make(stage: 0)).RunTo(25);
        var spoutsIn = run.Of(TrialEventKind.Cue).Where(e => e.Cue == TrialRules.SpoutsInCue).ToList();
        Assert.IsTrue(spoutsIn.Count >= 2);
        CollectionAssert.AreEqual(spoutsIn.Select(e => SpoutCommands.SpoutsIn(e.Trial)).ToArray(), Commands(run).ToArray());
        Assert.IsTrue(spoutsIn.All(e => SpoutCommands.SpoutsIn(e.Trial) == LickSpoutCommand.SpoutsIn));
    }

    [TestMethod]
    public void ATrialWhoseSpoutsStayInBetweenMovingOnesIsNeverWithdrawn()
    {
        // Left-right stage 4, the left condition moving and the right staying in (its set's spoutsStayIn), every trial a miss:
        // a stay-in trial's outcome moves nothing and its SpoutsIn sends both; each SpoutsOut cue draws the spouts out only
        // when the trial drawn at that interval's start moves.
        var run = new Harness(Rules.Make(t => ((IDictionary<object, object>)t.TrialConditions[1].Set)["spoutsStayIn"] = true)).RunTo(150);
        var moves = new List<Tuple<Event, LickSpoutCommand>>();
        Event current = null;
        var events = new Subject<Event>();
        new SpoutCommands().Process(events).Subscribe(c => moves.Add(Tuple.Create(current, c)));
        foreach (var e in run.Events) { current = e; events.OnNext(e); }
        var trials = run.Of(TrialEventKind.Start).Select(e => e.Trial).ToList();
        Assert.IsTrue(trials.Count > 10 && trials.Any(t => t.SpoutsStayIn) && trials.Any(t => !t.SpoutsStayIn));
        Assert.IsTrue(Enumerable.Range(1, trials.Count - 2).Any(i => trials[i].SpoutsStayIn && !trials[i - 1].SpoutsStayIn && !trials[i + 1].SpoutsStayIn), "a stay-in trial between moving ones");
        foreach (var e in run.Of(TrialEventKind.Outcome))
        {
            var made = moves.Where(m => m.Item1 == e).Select(m => m.Item2).ToList();
            CollectionAssert.AreEqual(e.Trial.SpoutsStayIn ? new LickSpoutCommand[0] : new[] { LickSpoutCommand.SpoutsOut }, made, "trial " + e.Trial.trialNumber);
        }
        foreach (var e in run.Of(TrialEventKind.Cue).Where(e => e.Cue == TrialRules.SpoutsInCue))
        {
            CollectionAssert.AreEqual(new[] { e.Trial.SpoutsStayIn ? LickSpoutCommand.SpoutsIn : SpoutCommands.SpoutsIn(e.Trial) }, moves.Where(m => m.Item1 == e).Select(m => m.Item2).ToList());
        }
        Trial coming = null;
        foreach (var e in run.Events)
        {
            if (e.Kind == TrialEventKind.Iti) coming = e.Trial;
            if (e.Kind == TrialEventKind.Cue && e.Cue == TrialRules.SpoutsOutCue)
                Assert.AreEqual(!coming.SpoutsStayIn, moves.Any(m => m.Item1 == e && m.Item2 == LickSpoutCommand.SpoutsOut), "SpoutsOut before trial " + coming.trialNumber);
        }
        Assert.AreEqual(trials[0].SpoutsStayIn ? 0 : 1, moves.Count(m => m.Item1.Kind == TrialEventKind.Iti), "trial 1's interval start alone, unless it stays in");
    }

    [TestMethod]
    public void GoNoGoSendsTheLeftSpoutInEveryTrial()
    {
        var run = new Harness(Rules.Make(file: "task-discrimination-go-no-go.yml")).RunTo(25);
        var spoutsIn = run.Of(TrialEventKind.Cue).Where(e => e.Cue == TrialRules.SpoutsInCue).ToList();
        Assert.IsTrue(spoutsIn.Count >= 2 && spoutsIn.All(e => SpoutCommands.SpoutsIn(e.Trial) == LickSpoutCommand.LeftOnly));
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
        var rules = Rules.Make(Rules.Optogenetics);
        Assert.IsTrue(rules.Laser.HardwareTriggered && rules.Laser.End == LaserEnd.AfterDuration);
        Assert.AreEqual(ZapitConditions.MaximumRampDownSeconds, rules.Laser.RampDownSeconds);
        var run = new Harness(rules);
        Assert.AreEqual(10.0, run.Of(TrialEventKind.Arm)[0].Seconds, "armed as the interval starts");
        Assert.AreEqual(0.8, LaserTiming.DurationOf(run.Of(TrialEventKind.Arm)[0].Trial), "each trial's own laserDurationSeconds");
        run.Loop.OnZapitReply(Rules.Armed(run.Of(TrialEventKind.Arm)[0].Trial));
        run.RunTo(12.05);
        Assert.AreEqual(11.5, run.Of(TrialEventKind.Trigger)[0].Seconds, 1e-12, "laserToStimulusSeconds before the stimulus");
        var right = run.Spout(true);
        run.At(12.1, h => h.Lick(right)).RunTo(13);
        Assert.AreEqual(12.3, run.Of(TrialEventKind.TriggerOff)[0].Seconds, 1e-9, "laserDurationSeconds after the trigger, outcome or not");
        var row = run.Row();
        Assert.IsTrue(row.laserCondition > 0 && row.laserPowerMw == 3 && row.laserTriggerTime == 11.5);
        Assert.AreEqual(12.8, run.Of(TrialEventKind.Arm)[1].Seconds, 1e-9, "trial 2's interval began at 12.1; Zapit is free 0.5 s after the line fell");
    }

    [TestMethod]
    public void ATrialAnsweredBeforeItsTriggerHasNoLaserAndItsRowSaysSo()
    {
        // The trigger 0.5 s after the stimulus (12.5 s); a lick at 12.1 s answers first.
        var run = new Harness(Rules.Make(t => { Rules.Optogenetics(t); t.Optogenetics.LaserToStimulusSeconds = -0.5; }, hardwareTriggered: false));
        Assert.AreEqual(0, run.Of(TrialEventKind.Arm).Count, "without hardware triggering the StartStimulus goes at the trigger");
        run.RunTo(12.05).At(12.1, h => h.Lick(0)).RunTo(13);
        Assert.AreEqual(0, run.Of(TrialEventKind.Trigger).Count);
        Assert.AreEqual(0, run.Row().laserPowerMw);
        Assert.IsTrue(double.IsNaN(run.Row().laserTriggerTime));
    }

    [TestMethod]
    public void ZapitNotArmedByTheTriggerStopsTheWorkflow()
    {
        var run = new Harness(Rules.Make(Rules.Optogenetics));
        StringAssert.Contains(Fixture.Refusal(() => run.RunTo(12)), "had not answered trial 1's StartStimulus by its trigger");
    }

    [TestMethod]
    public void TheTrialGoesOnOnlyOnceZapitArmedItAsAsked()
    {
        Func<Action<ZapitReply>, string> refusal = change =>
        {
            var run = new Harness(Rules.Make(Rules.Optogenetics));
            var trial = run.Of(TrialEventKind.Arm)[0].Trial;
            var reply = Rules.Armed(trial);
            change(reply);
            return Fixture.Refusal(() => run.Loop.OnZapitReply(reply));
        };
        Assert.AreEqual("accepted", refusal(r => { }));
        StringAssert.Contains(refusal(r => r.Value = r.Value + 1), "StartStimulus (condition ");
        StringAssert.Contains(refusal(r => r.LaserOn = 0), "laserOn=0");
        StringAssert.Contains(refusal(r => r.IsError = true), "error=True");
    }

    [TestMethod]
    public void WithoutOptogeneticsNoTrialIsArmedOrTriggered()
    {
        var rules = Rules.Make();
        Assert.IsNull(rules.Laser);
        var run = new Harness(rules).RunTo(30);
        Assert.IsTrue(run.Outcomes.Count >= 3 && run.Of(TrialEventKind.Arm).Count == 0 && run.Of(TrialEventKind.Trigger).Count == 0);
        Assert.IsTrue(run.Outcomes.All(o => o.Row.Value.laserPowerMw == 0 && double.IsNaN(o.Row.Value.laserTriggerTime)));
    }
}

[TestClass]
public class TrialRowTests
{
    static TrialTimes Times(double now)
    {
        return new TrialTimes { ItiStartTime = 50, ItiSeconds = 3, QuietWaitSeconds = 0.2, TrialStartTime = 8.5, StimulusOnsetTime = 9, LaserTriggerTime = double.NaN, Now = now };
    }

    [TestMethod]
    public void TheRowSaysWhatTheOutcomeCost()
    {
        var trial = new Trial { target = TargetWords.Left, autoReward = false, itiSeconds = 3 };
        var incorrect = TrialRow.Build(Choice.Right, trial, Times(10), 4);
        Assert.IsTrue(incorrect.outcome == TrialOutcomes.Incorrect && incorrect.penalised && incorrect.penaltyTimeoutSeconds == 4 && incorrect.outcomeTime == 10);
        Assert.IsTrue(incorrect.itiSeconds == 3 && incorrect.itiStartTime == 50 && incorrect.quietWaitSeconds == 0.2 && incorrect.trialStartTime == 8.5 && incorrect.stimulusOnsetTime == 9);
        // A miss: the outcome at the end of the response window, and the penalty sound with its timeout.
        var miss = TrialRow.Build(Choice.NoResponse, trial, Times(10), 4);
        Assert.IsTrue(miss.outcome == TrialOutcomes.NoResponse && miss.penalised && miss.penaltyTimeoutSeconds == 4 && !miss.rewarded);
        trial.autoReward = true;
        var rewardedMiss = TrialRow.Build(Choice.NoResponse, trial, Times(10), 4);
        Assert.IsTrue(rewardedMiss.outcome == TrialOutcomes.Auto && rewardedMiss.autoReward && rewardedMiss.rewarded && rewardedMiss.penaltyTimeoutSeconds == 0 && !rewardedMiss.penalised, "an auto-rewarded miss: outcome auto, autoReward and rewarded");
        var correct = TrialRow.Build(Choice.Left, trial, Times(10), 4);
        Assert.AreEqual(TrialOutcomes.Incorrect, TrialRow.Build(Choice.Right, trial, Times(10), 4).outcome, "a side chosen on an auto-reward trial keeps its outcome");
        var goTrial = new Trial { target = TargetWords.Go, autoReward = true };
        Assert.AreEqual(TrialOutcomes.Auto, TrialRow.Build(Choice.NoResponse, goTrial, Times(10), 4).outcome, "go/no-go: an auto-rewarded go miss");
        Assert.AreEqual(TrialOutcomes.Correct, TrialRow.Build(Choice.Go, goTrial, Times(10), 4).outcome, "go/no-go: a licked auto-reward go trial");
        Assert.IsTrue(correct.outcome == TrialOutcomes.Correct && !correct.penalised && correct.penaltyTimeoutSeconds == 0 && correct.rewarded);
    }

    [TestMethod]
    public void TheRowLogsTheConditionAndThePowerFired()
    {
        var trial = new Trial { target = TargetWords.Left, laserCondition = 2, laserOn = true, laserPowerMw = 3 };
        var times = Times(10);
        times.LaserTriggerTime = 9.25;
        var row = TrialRow.Build(Choice.Left, trial, times, 4);
        Assert.IsTrue(row.laserCondition == 2 && row.laserPowerMw == 3 && row.laserTriggerTime == 9.25);
        var answered = TrialRow.Build(Choice.Left, trial, Times(10), 4);
        Assert.IsTrue(answered.laserCondition == 2 && answered.laserPowerMw == 0 && double.IsNaN(answered.laserTriggerTime), "answered before its trigger");
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
        return TrialOutcome.Of(TrialRow.Build(choice, trial, new TrialTimes { Now = 1 }, 4), trial);
    }

    [TestMethod]
    public void ACorrectChoiceIsRewardedAtTheTargetWithTheOtherSpoutDrawnBack()
    {
        var c = Of(Choice.Right, false, true);
        Assert.IsTrue(c.Rewarded && c.Reward.Item1 == "right" && c.Reward.Item2 == 3 && c.SpoutCommand == LickSpoutCommand.RightOnly);
        Assert.IsTrue(!c.SpoutsStayIn && !c.Penalised && c.PenaltyTimeoutSeconds == 0);
    }

    [TestMethod]
    public void AnErrorDrawsBothSpoutsBackWithTheSoundAndTimeout()
    {
        var c = Of(Choice.Left, false, true);
        Assert.IsTrue(!c.Rewarded && c.Reward == null && c.SpoutCommand == LickSpoutCommand.SpoutsOut && c.Penalised && c.PenaltyTimeoutSeconds == 4);
        var miss = Of(Choice.NoResponse, false, true);  // the sound comes with every timeout
        Assert.IsTrue(!miss.Rewarded && miss.Penalised && miss.PenaltyTimeoutSeconds == 4);
        var auto = Of(Choice.NoResponse, true, false);
        Assert.IsTrue(auto.Rewarded && auto.Reward.Item1 == "right" && auto.PenaltyTimeoutSeconds == 0 && !auto.Penalised && auto.SpoutsStayIn);
        Assert.IsTrue(!c.Row.Value.rewarded && c.Row.Value.penalised && c.Row.Value.rewardOfferedUl == 3, "an error: offered, penalised");
        Assert.IsTrue(!miss.Row.Value.rewarded && miss.Row.Value.penalised && miss.Row.Value.rewardOfferedUl == 3, "a miss keeps the offer");
        Assert.IsTrue(auto.Row.Value.rewarded && !auto.Row.Value.penalised);
    }

    [TestMethod]
    public void ARandomTrialsCoinDecidesTheRowsRewardedAndPenalised()
    {
        // Random: heads rewards a choice at the spout chosen, tails penalises it, a miss is a scored
        // miss; the offer is on the row every time, and the valve's command is the row's rewarded.
        Func<string, bool, TrialOutcome> score = (choice, up) =>
        {
            var t = Rules.Made(TargetWords.Random, 3);
            t.probeHeads = up;
            return TrialOutcome.Of(TrialRow.Build(choice, t, new TrialTimes { Now = 1 }, 4), t);
        };
        var heads = score(Choice.Left, true);
        Assert.IsTrue(heads.Row.Value.rewarded && !heads.Row.Value.penalised && heads.Reward.Equals(Tuple.Create("left", 3.0)) && heads.SpoutCommand == LickSpoutCommand.LeftOnly);
        var tails = score(Choice.Right, false);
        Assert.IsTrue(!tails.Row.Value.rewarded && tails.Row.Value.penalised && tails.Reward == null && tails.PenaltyTimeoutSeconds == 4 && tails.Penalised);
        foreach (var coin in new[] { true, false })
        {
            var miss = score(Choice.NoResponse, coin);
            Assert.IsTrue(!miss.Row.Value.rewarded && miss.Row.Value.penalised && miss.Reward == null && miss.PenaltyTimeoutSeconds == 4);
        }
        foreach (var o in new[] { heads, tails, score(Choice.NoResponse, true) })
            Assert.IsTrue(o.Row.Value.outcome == TrialOutcomes.Probe && o.Row.Value.rewardOfferedUl == 3 && o.Row.Value.target == TargetWords.Random);
    }

    [TestMethod]
    public void TheSpoutsComeInAsTheTrialSays()
    {
        Assert.AreEqual(LickSpoutCommand.SpoutsIn, SpoutCommands.SpoutsIn(new Trial { singleSpoutOffered = false, target = TargetWords.Left }));
        Assert.AreEqual(LickSpoutCommand.LeftOnly, SpoutCommands.SpoutsIn(new Trial { singleSpoutOffered = true, target = TargetWords.Left }));
        Assert.AreEqual(LickSpoutCommand.RightOnly, SpoutCommands.SpoutsIn(new Trial { singleSpoutOffered = true, target = TargetWords.Right }));
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
        Assert.IsTrue(timing.LaserToStimulusSeconds == 0.5 && !timing.Laser && timing.ResponseDelaySeconds == 0 && timing.ResponseWindowSeconds == 1.5);
        CollectionAssert.AreEqual(new[] { TrialRules.GratingOffCue, TrialRules.SpoutsInCue, TrialRules.SpoutsOutCue }, timing.Cues.Select(c => c.Name).ToList());
    }

    [TestMethod]
    public void RunTrialsRunsTheLoopOnTheWorkflowsStreams()
    {
        var rules = new BehaviorSubject<TrialRules>(Rules.Make());
        var clock = new Subject<double>();
        var events = new List<Event>();
        new RunTrials().Process(rules, clock, new Subject<double>(), new Subject<bool>(), new Subject<bool>(), new Subject<ZapitReply>()).Subscribe(events.Add);
        for (int ms = 10000; ms <= 12000; ms++) clock.OnNext(ms / 1000.0);
        CollectionAssert.AreEqual(new[] { TrialEventKind.Iti, TrialEventKind.Start, TrialEventKind.Stimulus, TrialEventKind.Update },
                                  events.Select(e => e.Kind).Take(4).ToList());
    }
}
