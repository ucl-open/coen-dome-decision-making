using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Devices;
using OpenRigs.Trials;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using UclOpenCoenDomeDecisionMakingTask;
using Event = OpenRigs.Trials.TrialEvent<global::Trial, global::DecisionUpdate, global::TrialOutcome>;

/// <summary>The spouts' bias correction (spouts.biasCorrection, SpoutBias): refused where it cannot run, inert when off,
/// and when on each outcome's move, each row's spoutBiasSteps and when SpoutBiasMoves sends the move.</summary>
[TestClass]
public class SpoutBiasTests
{
    static void On(UclOpenCoenDomeDecisionMakingTaskParameters task, int trialsPerSide, int maxSteps)
    {
        var bias = task.Spouts.BiasCorrection;
        bias.Enabled = true;
        bias.TrialsPerSide = trialsPerSide;
        bias.Threshold = 0.3;
        bias.MaxSteps = maxSteps;
    }

    static TrialRow Row(string target, string choice)
    {
        return new TrialRow { target = target, choice = choice };
    }

    // A mouse that answers every trial at the left spout just after its response window opens: every left trial
    // correct (rewarded), every right trial incorrect (unrewarded).
    static Harness LeftLicker(TrialRules rules, double seconds)
    {
        var run = new Harness(rules);
        var answered = 0;
        while (run.Now < seconds)
        {
            run.RunTo(run.Now + 0.001);
            var stimuli = run.Of(TrialEventKind.Stimulus);
            for (; answered < stimuli.Count; answered++)
            {
                var trial = stimuli[answered].Trial;
                var at = Math.Round((stimuli[answered].Seconds + trial.values.TrialStructure.StimulusToResponseWindowSeconds + 0.1) * 1000) / 1000.0;
                run.At(at, h => { for (int i = 0; i < trial.values.Response.ThresholdLicks; i++) h.Lick(0); });
            }
        }
        return run;
    }

    // Each move SpoutBiasMoves sends, with the event it sends it at.
    static List<Tuple<Event, SpoutMove>> Sent(Harness run)
    {
        var sent = new List<Tuple<Event, SpoutMove>>();
        Event current = null;
        var events = new Subject<Event>();
        new SpoutBiasMoves().Process(events).Subscribe(m => sent.Add(Tuple.Create(current, m)));
        foreach (var e in run.Events) { current = e; events.OnNext(e); }
        return sent;
    }

    [TestMethod]
    public void ItsSettingsAreRefusedOutsideTheirBoundsAtGo()
    {
        // Checked with the correction off too: the curriculum may turn it on.
        var task = Fixture.Task("task-discrimination-left-right.yml", 4);
        var bias = task.Spouts.BiasCorrection;
        bias.TrialsPerSide = 0;
        bias.Threshold = 1.5;
        bias.MaxSteps = 0;
        var refusal = Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers()));
        StringAssert.Contains(refusal, "spouts.biasCorrection.trialsPerSide (0) must be 1 or more");
        StringAssert.Contains(refusal, "spouts.biasCorrection.threshold (1.5) must be above 0 and at most 1");
        StringAssert.Contains(refusal, "spouts.biasCorrection.maxSteps (0) must be 1 or more");
        task = Fixture.Task("task-discrimination-left-right.yml", 4);
        task.Spouts.BiasCorrection.Threshold = 0;
        StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers())), "spouts.biasCorrection.threshold (0) must be above 0 and at most 1");
        Assert.AreEqual("accepted", Fixture.Refusal(() => Fixture.Check(Fixture.Task("task-discrimination-left-right.yml", 4), Fixture.Speakers())), "the files' defaults");
    }

    [TestMethod]
    public void GoNoGoRefusesItOn()
    {
        // One spout: there is no other side to bring nearer. Off, go/no-go runs as ever.
        var task = Fixture.Task("task-discrimination-go-no-go.yml", 4);
        Assert.AreEqual("accepted", Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers())));
        task.Spouts.BiasCorrection.Enabled = true;
        StringAssert.Contains(Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers())), "spouts.biasCorrection.enabled is for two spouts: discriminationGoNoGo uses the left spout alone");
        foreach (var file in new[] { "task-localization.yml", "task-discrimination-left-right.yml" })
        {
            task = Fixture.Task(file, 4);
            task.Spouts.BiasCorrection.Enabled = true;
            Assert.AreEqual("accepted", Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers())), file);
        }
    }

    [TestMethod]
    public void TheRuleMovesOnceBothSidesAreFullThenStartsAgain()
    {
        var bias = new SpoutBias(new BiasCorrection { Enabled = true, TrialsPerSide = 3, Threshold = 0.3, MaxSteps = 500 }, 50);
        for (int i = 0; i < 3; i++) Assert.IsNull(bias.Respond(Row(TargetWords.Left, Choice.Left)), "the right side is empty");
        Assert.IsNull(bias.Respond(Row(TargetWords.Right, Choice.Left)));
        Assert.IsNull(bias.Respond(Row(TargetWords.Right, Choice.Left)));
        var move = bias.Respond(Row(TargetWords.Right, Choice.Left));
        Assert.IsTrue(move.Axis == SpoutAxis.BaseTransverse && move.Steps == 50, "1 - 0 above 0.3: toward the mouse's left");
        Assert.AreEqual(50, bias.Steps);
        // Both buffers emptied: the next response alone moves nothing.
        Assert.IsNull(bias.Respond(Row(TargetWords.Right, Choice.Left)));
        Assert.IsNull(bias.Respond(Row(TargetWords.Left, Choice.Left)));
        // Refilled right-handed: back the other way.
        Assert.IsNull(bias.Respond(Row(TargetWords.Left, Choice.Right)));
        Assert.IsNull(bias.Respond(Row(TargetWords.Left, Choice.Right)));
        Assert.IsNull(bias.Respond(Row(TargetWords.Right, Choice.Right)));
        Assert.AreEqual(-50, bias.Respond(Row(TargetWords.Right, Choice.Right)).Steps, "left 1/3 correct, right 3/3");
        Assert.AreEqual(0, bias.Steps);
        // A difference of exactly threshold or less moves nothing: 2/3 against 1/3 is not above 0.34.
        var even = new SpoutBias(new BiasCorrection { Enabled = true, TrialsPerSide = 3, Threshold = 0.34, MaxSteps = 500 }, 50);
        foreach (var choice in new[] { Choice.Left, Choice.Left, Choice.Right }) even.Respond(Row(TargetWords.Left, choice));
        foreach (var choice in new[] { Choice.Right, Choice.Left, Choice.Left }) Assert.IsNull(even.Respond(Row(TargetWords.Right, choice)));
    }

    [TestMethod]
    public void TheRuleCountsOnlySideTrialsAnsweredWithBothSpoutsUnrepeatedAndLaserOff()
    {
        Assert.IsTrue(SpoutBias.Counts(Row(TargetWords.Left, Choice.Right)));
        Assert.IsFalse(SpoutBias.Counts(Row(TargetWords.NoReward, Choice.Right)), "a probe");
        Assert.IsFalse(SpoutBias.Counts(Row(TargetWords.Left, Choice.NoResponse)), "a miss");
        Assert.IsFalse(SpoutBias.Counts(new TrialRow { target = TargetWords.Left, choice = Choice.Left, singleSpoutOffered = true }), "one spout");
        Assert.IsFalse(SpoutBias.Counts(new TrialRow { target = TargetWords.Left, choice = Choice.Left, repeatCount = 1 }), "a repeat");
        Assert.IsFalse(SpoutBias.Counts(new TrialRow { target = TargetWords.Left, choice = Choice.Left, laserPowerMw = 3 }), "the laser on");
    }

    [TestMethod]
    public void AStepOfZeroIsRefused()
    {
        StringAssert.Contains(Assert.ThrowsException<ArgumentOutOfRangeException>(() => new SpoutBias(new BiasCorrection(), 0)).Message, "CheckTaskAtGo.SpoutBiasStep must be above 0");
    }

    [TestMethod]
    public void OffItSendsNothing()
    {
        // The task files' default: no move, every row 0, however biased the mouse.
        var run = LeftLicker(Rules.Make(), 400);
        Assert.IsTrue(run.Outcomes.Count > 20 && run.Outcomes.Any(o => o.Row.Value.choice == Choice.Left && o.Row.Value.target == TargetWords.Right), "a biased run");
        Assert.IsTrue(run.Outcomes.All(o => o.BiasMove == null && o.Row.Value.spoutBiasSteps == 0));
        Assert.AreEqual(0, Sent(run).Count);
    }

    [TestMethod]
    public void OnALeftLickingMouseStepsTheSpoutsToItsLeftUpToFurthestBias()
    {
        // 3 counted trials a side, the workflow's 50 steps a move, 170 at most: 50, 100, 150, then the last move held to 170.
        var run = LeftLicker(Rules.Make(t => On(t, 3, 170)), 1500);
        var outcomes = run.Outcomes;
        var made = outcomes.Where(o => o.BiasMove != null).ToList();
        CollectionAssert.AreEqual(new[] { 50, 50, 50, 20 }, made.Select(o => o.BiasMove.Steps).ToArray());
        Assert.IsTrue(made.All(o => o.BiasMove.Axis == SpoutAxis.BaseTransverse));
        CollectionAssert.AreEqual(new[] { 0, 50, 100, 150 }, made.Select(o => o.Row.Value.spoutBiasSteps).ToArray(), "each row the sum before its own move");
        Assert.AreEqual(170, outcomes.Last().Row.Value.spoutBiasSteps);

        // The same moves replayed through a fresh rule from the rows; between moves each side fills again from empty.
        var replay = new SpoutBias(new BiasCorrection { Enabled = true, TrialsPerSide = 3, Threshold = 0.3, MaxSteps = 170 }, 50);
        var since = new List<TrialRow>();
        foreach (var outcome in outcomes)
        {
            var row = outcome.Row.Value;
            Assert.AreEqual(replay.Steps, row.spoutBiasSteps, "trial " + row.trialNumber);
            var move = replay.Respond(row);
            Assert.AreEqual(outcome.BiasMove == null ? (int?)null : outcome.BiasMove.Steps, move == null ? (int?)null : move.Steps, "trial " + row.trialNumber);
            since.Add(row);
            if (move == null) continue;
            var counted = since.Where(SpoutBias.Counts).ToList();
            Assert.IsTrue(counted.Count(r => r.target == TargetWords.Left) >= 3 && counted.Count(r => r.target == TargetWords.Right) >= 3, "trial " + row.trialNumber);
            since.Clear();
        }

        // Sent after an unrewarded outcome at once, after a rewarded one at the next SpoutsOut cue; both happen here.
        var sent = Sent(run);
        Assert.AreEqual(made.Count, sent.Count);
        foreach (var outcome in made)
        {
            var at = run.Events.First(e => e.Kind == TrialEventKind.Outcome && e.Outcome == outcome);
            var expected = outcome.Rewarded ? run.Events.Skip(run.Events.IndexOf(at)).First(e => e.Kind == TrialEventKind.Cue && e.Cue == OpenRigs.Trials.Spouts.OutCue) : at;
            Assert.IsTrue(sent.Any(s => s.Item1 == expected && s.Item2 == outcome.BiasMove), "trial " + outcome.Row.Value.trialNumber + (outcome.Rewarded ? ", rewarded" : ", unrewarded"));
        }
        Assert.IsTrue(made.Any(o => o.Rewarded) && made.Any(o => !o.Rewarded), "both timings seen");
    }
}
