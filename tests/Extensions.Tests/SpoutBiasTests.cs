using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Devices;
using OpenRigs.Trials;
using System;
using System.Collections.Generic;
using System.Linq;
using UclOpenCoenDomeDecisionMakingTask;
using Event = OpenRigs.Trials.TrialEvent<global::Trial, global::DecisionUpdate, global::TrialOutcome>;

/// <summary>The spouts' bias correction (spouts.biasCorrection, TrialRules): refused where it cannot run, inert when off,
/// and when on each outcome's move, each row's spoutBiasSteps and when main.bonsai sends the move (the next Iti event).</summary>
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

    static SpoutMove Respond(TrialRules rules, string target, string choice)
    {
        return rules.CorrectBias(Row(target, choice));
    }

    // The sum of the moves so far, as the next row's spoutBiasSteps: an uncounted row changes nothing.
    static int Steps(TrialRules rules)
    {
        var row = Row(TargetWords.NoReward, Choice.NoResponse);
        rules.CorrectBias(row);
        return row.spoutBiasSteps;
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

    // The events main.bonsai sends a move at (BiasMoves: each Iti event whose Trial.BiasMove is set).
    static List<Event> Sent(Harness run)
    {
        return run.Of(TrialEventKind.Iti).Where(e => e.Trial.BiasMove != null).ToList();
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
        var bias = Rules.Make(t => On(t, 3, 500));
        for (int i = 0; i < 3; i++) Assert.IsNull(Respond(bias, TargetWords.Left, Choice.Left), "the right side is empty");
        Assert.IsNull(Respond(bias, TargetWords.Right, Choice.Left));
        Assert.IsNull(Respond(bias, TargetWords.Right, Choice.Left));
        var move = Respond(bias, TargetWords.Right, Choice.Left);
        Assert.IsTrue(move.Axis == SpoutAxis.BaseTransverse && move.Steps == 50, "1 - 0 above 0.3: toward the mouse's left");
        Assert.AreEqual(50, Steps(bias));
        // Both buffers emptied: the next response alone moves nothing.
        Assert.IsNull(Respond(bias, TargetWords.Right, Choice.Left));
        Assert.IsNull(Respond(bias, TargetWords.Left, Choice.Left));
        // Refilled right-handed: back the other way.
        Assert.IsNull(Respond(bias, TargetWords.Left, Choice.Right));
        Assert.IsNull(Respond(bias, TargetWords.Left, Choice.Right));
        Assert.IsNull(Respond(bias, TargetWords.Right, Choice.Right));
        Assert.AreEqual(-50, Respond(bias, TargetWords.Right, Choice.Right).Steps, "left 1/3 correct, right 3/3");
        Assert.AreEqual(0, Steps(bias));
        // A difference of exactly threshold or less moves nothing: 2/3 against 1/3 is not above 0.34.
        var even = Rules.Make(t => { On(t, 3, 500); t.Spouts.BiasCorrection.Threshold = 0.34; });
        foreach (var choice in new[] { Choice.Left, Choice.Left, Choice.Right }) Respond(even, TargetWords.Left, choice);
        foreach (var choice in new[] { Choice.Right, Choice.Left, Choice.Left }) Assert.IsNull(Respond(even, TargetWords.Right, choice));
    }

    [TestMethod]
    public void TheRuleCountsOnlySideTrialsAnsweredWithBothSpoutsUnrepeatedAndLaserOff()
    {
        Assert.IsTrue(TrialRules.CountsForBias(Row(TargetWords.Left, Choice.Right)));
        Assert.IsFalse(TrialRules.CountsForBias(Row(TargetWords.NoReward, Choice.Right)), "a probe");
        Assert.IsFalse(TrialRules.CountsForBias(Row(TargetWords.Left, Choice.NoResponse)), "a miss");
        Assert.IsFalse(TrialRules.CountsForBias(new TrialRow { target = TargetWords.Left, choice = Choice.Left, singleSpoutOffered = true }), "one spout");
        Assert.IsFalse(TrialRules.CountsForBias(new TrialRow { target = TargetWords.Left, choice = Choice.Left, repeatCount = 1 }), "a repeat");
        Assert.IsFalse(TrialRules.CountsForBias(new TrialRow { target = TargetWords.Left, choice = Choice.Left, laserPowerMw = 3 }), "the laser on");
    }

    [TestMethod]
    public void AStepOfZeroIsRefused()
    {
        StringAssert.Contains(Assert.ThrowsException<ArgumentOutOfRangeException>(() => new TrialRules(Fixture.Text(Fixture.Task("task-discrimination-left-right.yml", 4)), Fixture.Speakers(), true, null, 0)).Message, "CheckTaskAtGo.SpoutBiasStep must be above 0");
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
    public void WithTheSpoutsAlwaysInTheMovesStillGoAtTheIti()
    {
        var run = LeftLicker(Rules.Make(t => { On(t, 3, 170); t.Spouts.AlwaysIn = true; }), 1500);
        var made = run.Outcomes.Where(o => o.BiasMove != null).Select(o => o.BiasMove).ToList();
        Assert.IsTrue(made.Count > 0);
        CollectionAssert.AreEqual(made, Sent(run).Select(e => e.Trial.BiasMove).ToList());
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
        var replay = Rules.Make(t => On(t, 3, 170));
        var since = new List<TrialRow>();
        foreach (var outcome in outcomes)
        {
            var row = outcome.Row.Value;
            var steps = row.spoutBiasSteps;
            var move = replay.CorrectBias(row);
            Assert.AreEqual(steps, row.spoutBiasSteps, "trial " + row.trialNumber);
            Assert.AreEqual(outcome.BiasMove == null ? (int?)null : outcome.BiasMove.Steps, move == null ? (int?)null : move.Steps, "trial " + row.trialNumber);
            since.Add(row);
            if (move == null) continue;
            var counted = since.Where(TrialRules.CountsForBias).ToList();
            Assert.IsTrue(counted.Count(r => r.target == TargetWords.Left) >= 3 && counted.Count(r => r.target == TargetWords.Right) >= 3, "trial " + row.trialNumber);
            since.Clear();
        }

        // Each sent at the next Iti event after its outcome, as the next interval begins, rewarded or not.
        var sent = Sent(run);
        Assert.AreEqual(made.Count, sent.Count);
        foreach (var outcome in made)
        {
            var at = run.Events.IndexOf(run.Events.First(e => e.Kind == TrialEventKind.Outcome && e.Outcome == outcome));
            var iti = run.Events.Skip(at).First(e => e.Kind == TrialEventKind.Iti);
            Assert.AreSame(outcome.BiasMove, iti.Trial.BiasMove, "trial " + outcome.Row.Value.trialNumber + (outcome.Rewarded ? ", rewarded" : ", unrewarded"));
        }
        Assert.IsTrue(made.Any(o => o.Rewarded) && made.Any(o => !o.Rewarded), "rewarded and unrewarded moves seen");
    }
}
