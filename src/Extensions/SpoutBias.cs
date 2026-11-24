using Bonsai;
using OpenRigs.Devices;
using OpenRigs.Trials;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using UclOpenCoenDomeDecisionMakingTask;
using Event = OpenRigs.Trials.TrialEvent<global::Trial, global::DecisionUpdate, global::TrialOutcome>;

/// <summary>The spouts' bias correction (spouts.biasCorrection), one for the experiment: the last trialsPerSide counted
/// results of left-target and of right-target trials. Once both are full, after each counted response, the left fraction
/// correct less the right's above threshold moves the base one step toward the mouse's left (+ on BaseTransverse), below
/// minus threshold toward its right, never past maxSteps in all either way; after a move both buffers empty.</summary>
public class SpoutBias
{
    readonly int trialsPerSide;
    readonly double threshold;
    readonly int step;
    readonly int maxSteps;
    readonly Queue<bool> left = new Queue<bool>();
    readonly Queue<bool> right = new Queue<bool>();
    int steps;

    public SpoutBias(BiasCorrection settings, int step)
    {
        if (step <= 0) throw new ArgumentOutOfRangeException("step", step, "CheckTaskAtGo.SpoutBiasStep must be above 0");
        trialsPerSide = settings.TrialsPerSide;
        threshold = settings.Threshold;
        this.step = step;
        maxSteps = settings.MaxSteps;
    }

    /// <summary>The sum of the moves so far (the spoutBiasSteps column), + toward the mouse's left.</summary>
    public int Steps { get { return steps; } }

    /// <summary>The settings' problems, each named; and on in go/no-go, which has one spout.</summary>
    public static IEnumerable<string> Problems(BiasCorrection settings, TaskType taskType)
    {
        if (settings == null) { yield return "spouts.biasCorrection must be given"; yield break; }
        if (settings.TrialsPerSide < 1) yield return "spouts.biasCorrection.trialsPerSide (" + settings.TrialsPerSide + ") must be 1 or more";
        if (!(settings.Threshold > 0 && settings.Threshold <= 1)) yield return "spouts.biasCorrection.threshold (" + settings.Threshold + ") must be above 0 and at most 1";
        if (settings.MaxSteps < 1) yield return "spouts.biasCorrection.maxSteps (" + settings.MaxSteps + ") must be 1 or more";
        if (settings.Enabled && taskType == TaskType.DiscriminationGoNoGo) yield return "spouts.biasCorrection.enabled is for two spouts: discriminationGoNoGo uses the left spout alone";
    }

    /// <summary>Whether the rule counts a finished trial: a left or right target, both spouts offered, a left or right choice, not a repeat, the laser off.</summary>
    public static bool Counts(TrialRow row)
    {
        return (row.target == TargetWords.Left || row.target == TargetWords.Right)
            && !row.singleSpoutOffered
            && (row.choice == Choice.Left || row.choice == Choice.Right)
            && row.repeatCount == 0
            && row.laserPowerMw == 0;
    }

    /// <summary>The move a finished trial sets off, or null.</summary>
    public SpoutMove Respond(TrialRow row)
    {
        if (!Counts(row)) return null;
        var buffer = row.target == TargetWords.Left ? left : right;
        buffer.Enqueue(row.choice == row.target);
        if (buffer.Count > trialsPerSide) buffer.Dequeue();
        if (left.Count < trialsPerSide || right.Count < trialsPerSide) return null;
        var bias = Correct(left) - Correct(right);
        var wanted = bias > threshold ? step : bias < -threshold ? -step : 0;
        var moved = Math.Max(-maxSteps, Math.Min(maxSteps, steps + wanted)) - steps;
        if (moved == 0) return null;
        steps += moved;
        left.Clear();
        right.Clear();
        return new SpoutMove { Axis = SpoutAxis.BaseTransverse, Steps = moved };
    }

    static double Correct(Queue<bool> results)
    {
        return results.Count(correct => correct) / (double)results.Count;
    }
}

[Combinator]
[Description("The spouts' bias moves (TrialOutcome.BiasMove), for LickSpoutStage.Moves: after an unrewarded outcome at once, after a rewarded one at the next SpoutsOut cue, so the spout never shifts while the mouse drinks.")]
[WorkflowElementCategory(ElementCategory.Combinator)]
public class SpoutBiasMoves
{
    public IObservable<SpoutMove> Process(IObservable<Event> source)
    {
        return Observable.Defer(() =>
        {
            SpoutMove held = null;
            return source.SelectMany(e =>
            {
                SpoutMove send = null;
                if (e.Kind == TrialEventKind.Outcome && e.Outcome.BiasMove != null)
                {
                    if (held != null) throw new InvalidOperationException("A bias move was still held for a SpoutsOut cue at trial " + e.Outcome.Row.Value.trialNumber + "'s outcome.");
                    if (e.Outcome.Rewarded) held = e.Outcome.BiasMove;
                    else send = e.Outcome.BiasMove;
                }
                else if (e.Kind == TrialEventKind.Cue && e.Cue == OpenRigs.Trials.Spouts.OutCue && held != null)
                {
                    send = held;
                    held = null;
                }
                return send == null ? Enumerable.Empty<SpoutMove>() : new[] { send };
            });
        });
    }
}
