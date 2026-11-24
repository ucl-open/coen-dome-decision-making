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

/// <summary>The spouts' bias correction (spouts.biasCorrection, OpenRigs.Trials SpoutBias) as this task runs it: refused
/// where it cannot run, inert when off, and when on each outcome's move and each row's spoutBiasSteps.</summary>
[TestClass]
public class SpoutBiasTests
{
    static void Bias(UclOpenCoenDomeDecisionMakingTaskParameters task, int trialsPerSide, int stepSteps, int maxSteps, int newTrialsAfterMove)
    {
        var bias = task.Spouts.BiasCorrection;
        bias.Enabled = true;
        bias.TrialsPerSide = trialsPerSide;
        bias.Threshold = 0.3;
        bias.StepSteps = stepSteps;
        bias.MaxSteps = maxSteps;
        bias.NewTrialsAfterMove = newTrialsAfterMove;
    }

    // A mouse that answers every trial at the left spout, thresholdLicks licks just after its response window opens:
    // every left trial correct, every right trial incorrect, so the bias is +1 once both sides are counted.
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

    // Each event with the spout moves SpoutCommands makes at it.
    static List<Tuple<Event, LickSpoutCommand>> Moves(Harness run)
    {
        var moves = new List<Tuple<Event, LickSpoutCommand>>();
        Event current = null;
        var events = new Subject<Event>();
        new SpoutCommands().Process(events).Subscribe(c => moves.Add(Tuple.Create(current, c)));
        foreach (var e in run.Events) { current = e; events.OnNext(e); }
        return moves;
    }

    static bool IsBias(LickSpoutCommand command)
    {
        return command == LickSpoutCommand.BiasLeft || command == LickSpoutCommand.BiasRight || command == LickSpoutCommand.BiasReset;
    }

    [TestMethod]
    public void ItsSettingsAreRefusedOutsideTheirBoundsAtGo()
    {
        // SpoutBias.Problems, each named (task.py's pydantic bounds), with the correction off too: the curriculum may turn it on.
        var task = Fixture.Task("task-discrimination-left-right.yml", 4);
        var bias = task.Spouts.BiasCorrection;
        bias.TrialsPerSide = 0;
        bias.Threshold = 1.5;
        bias.MaxSteps = 1001;
        bias.NewTrialsAfterMove = 101;
        var refusal = Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers()));
        StringAssert.Contains(refusal, "spouts.biasCorrection.trialsPerSide (0) must be 1 or more");
        StringAssert.Contains(refusal, "spouts.biasCorrection.threshold (1.5) must be above 0 and at most 1");
        StringAssert.Contains(refusal, "spouts.biasCorrection.maxSteps (1001) must be 1 to 1000 steps");
        StringAssert.Contains(refusal, "spouts.biasCorrection.newTrialsAfterMove (101) must be 0 to 100");
        task = Fixture.Task("task-discrimination-left-right.yml", 4);
        task.Spouts.BiasCorrection.Threshold = 0;
        task.Spouts.BiasCorrection.StepSteps = 600;
        refusal = Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers()));
        StringAssert.Contains(refusal, "spouts.biasCorrection.threshold (0) must be above 0 and at most 1");
        StringAssert.Contains(refusal, "spouts.biasCorrection.stepSteps (600) is above maxSteps (500)");
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
        StringAssert.Contains(Fixture.Refusal(() => Rules.Make(t => t.Spouts.BiasCorrection.Enabled = true, file: "task-discrimination-go-no-go.yml")), "discriminationGoNoGo uses the left spout alone");
        foreach (var file in new[] { "task-localization.yml", "task-discrimination-left-right.yml" })
        {
            task = Fixture.Task(file, 4);
            task.Spouts.BiasCorrection.Enabled = true;
            Assert.AreEqual("accepted", Fixture.Refusal(() => Fixture.Check(task, Fixture.Speakers())), file);
        }
    }

    [TestMethod]
    public void OffItSendsNothing()
    {
        // The task files' default: no SpoutBias, no settings for the stage, no bias move, every row at offset 0, however biased the mouse.
        var rules = Rules.Make();
        Assert.IsNull(rules.SpoutBias);
        Assert.AreEqual(0, new SpoutBiasSettingsOf().Process(Observable.Return(rules)).ToList().Wait().Count);
        var run = LeftLicker(rules, 400);
        Assert.IsTrue(run.Outcomes.Count > 20 && run.Outcomes.Any(o => o.Row.Value.choice == Choice.Left && o.Row.Value.target == TargetWords.Right), "a biased run");
        Assert.IsTrue(run.Outcomes.All(o => o.SpoutBiasCommand == null && o.Row.Value.spoutBiasSteps == 0));
        Assert.IsFalse(Moves(run).Any(m => IsBias(m.Item2)));
    }

    [TestMethod]
    public void OnABiasedMouseMovesTheSpoutsToItsLeftUpToTheLimit()
    {
        // 3 counted trials a side, 100 steps a move, 250 at most, one new counted trial each side after a move: the left
        // licker's bias is +1 once both sides are full, so BiasLeft, then again once each side has a new trial, until the
        // offset reaches 250 (100, 200, then the last move held to 250; none after).
        var rules = Rules.Make(t => Bias(t, 3, 100, 250, 1));
        Assert.IsNotNull(rules.SpoutBias);
        var settings = new SpoutBiasSettingsOf().Process(Observable.Return(rules)).ToList().Wait();
        Assert.IsTrue(settings.Count == 1 && settings[0].StepSteps == 100 && settings[0].MaxSteps == 250, "the stage's step and limit, once at Go");
        var run = LeftLicker(rules, 600);
        var outcomes = run.Outcomes;
        Assert.IsTrue(outcomes.Count > 20);

        // The moves, replayed through a fresh rule from the rows: the same moves at the same trials.
        var replay = new SpoutBias(3, 0.3, 100, 250, 1);
        var offset = 0;
        foreach (var outcome in outcomes)
        {
            var row = outcome.Row.Value;
            Assert.AreEqual(offset, row.spoutBiasSteps, "trial " + row.trialNumber + "'s offset is the moves before it");
            Assert.AreEqual(replay.Respond(row), outcome.SpoutBiasCommand, "trial " + row.trialNumber);
            offset = replay.OffsetSteps;
        }
        var made = outcomes.Where(o => o.SpoutBiasCommand.HasValue).ToList();
        Assert.AreEqual(3, made.Count, "three moves, then the limit");
        Assert.IsTrue(made.All(o => o.SpoutBiasCommand == LickSpoutCommand.BiasLeft), "favouring the left: toward the mouse's left");
        Assert.AreEqual(250, rules.SpoutBias.OffsetSteps);
        Assert.AreEqual(250, outcomes.Last().Row.Value.spoutBiasSteps);
        CollectionAssert.AreEqual(new[] { 0, 100, 200 }, made.Select(o => o.Row.Value.spoutBiasSteps).ToArray(), "each moving trial ran at the offset before its move");
        // Nothing before both sides are full: the first move is no sooner than the third trial of each side.
        var first = outcomes.IndexOf(made[0]);
        var before = outcomes.Take(first + 1).Select(o => o.Row.Value).ToList();
        Assert.IsTrue(before.Count(r => r.target == TargetWords.Left) >= 3 && before.Count(r => r.target == TargetWords.Right) >= 3);

        // Sent by SpoutCommands: after an unrewarded outcome's own move, at once; after a rewarded one, at the next SpoutsOut cue.
        var moves = Moves(run);
        Assert.AreEqual(3, moves.Count(m => IsBias(m.Item2)));
        foreach (var outcome in made)
        {
            var at = run.Events.First(e => e.Kind == TrialEventKind.Outcome && e.Outcome == outcome);
            var sent = outcome.Rewarded ? run.Events.Skip(run.Events.IndexOf(at)).First(e => e.Kind == TrialEventKind.Cue && e.Cue == OpenRigs.Trials.Spouts.OutCue) : at;
            Assert.AreEqual(LickSpoutCommand.BiasLeft, moves.Last(m => m.Item1 == sent).Item2, "trial " + outcome.Row.Value.trialNumber + (outcome.Rewarded ? ", rewarded" : ", unrewarded"));
        }
    }

    [TestMethod]
    public void TheRuleCountsTheTrialsAsTheTaskLogsThem()
    {
        // The row is the rule's response: target, choice, single spout, repeat, and the laser on at a power above 0 (laserPowerMw,
        // 0 on a laser trial answered before its trigger).
        ISpoutBiasResponse row = new TrialRow { target = TargetWords.Right, choice = Choice.Left, singleSpoutOffered = true, repeatCount = 2, laserPowerMw = 3 };
        Assert.IsTrue(row.Target == "right" && row.Choice == "left" && row.SingleSpoutOffered && row.RepeatCount == 2 && row.LaserOn);
        Assert.IsFalse(((ISpoutBiasResponse)new TrialRow { laserPowerMw = 0 }).LaserOn);
        Assert.IsFalse(SpoutBias.Counts(row));
        Assert.IsTrue(SpoutBias.Counts(new TrialRow { target = TargetWords.Left, choice = Choice.Right }));
        Assert.IsFalse(SpoutBias.Counts(new TrialRow { target = TargetWords.NoReward, choice = Choice.Right }), "a probe");
        Assert.IsFalse(SpoutBias.Counts(new TrialRow { target = TargetWords.Left, choice = Choice.NoResponse }), "a miss");
    }
}
