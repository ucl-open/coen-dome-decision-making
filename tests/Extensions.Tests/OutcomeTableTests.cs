using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Devices.Zapit;
using OpenRigs.Trials;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UclOpenCoenDomeDecisionMakingTask;

/// <summary>What every choice earns in each task (localization and both discriminations), by this repo's C# (TrialRow.Outcome, Rewarded, Penalised,
/// which targets TrialDraw auto-rewards and the spout a side target resolves to), kept in tests\fixtures\outcomes.json. tests\test_trial_timeline.py holds the
/// trial timeline page's Python copy of these rules (trial_timeline.py) equal to it, so a change to the C# rules fails here
/// until the fixture is rewritten (OUTCOMES_REGEN=1), and then fails there until the page's copy follows.</summary>
[TestClass]
public class OutcomeTableTests
{
    const int Draws = 2000;   // trials drawn per task to see which targets are auto-rewarded

    static readonly string[] Files = { "task-discrimination-go-no-go.yml", "task-discrimination-left-right.yml", "task-localization.yml" };

    // The task file at its curriculum stage with every condition weighted (Fixture's "Probes Added", localization's "Conflict
    // Added"), auto-reward on every trial that can have it; random: the noReward probes made random-reward probes.
    static UclOpenCoenDomeDecisionMakingTaskParameters Task(string file, bool random, bool penaliseProbes)
    {
        var task = Fixture.Task(file, file == "task-localization.yml" ? 4 : 3);
        task.AutoRewardProbability = 1;
        task.PenaliseProbes = penaliseProbes;
        if (random) Fixture.RandomProbes(task);
        return task;
    }

    static string Json(object value)
    {
        return Newtonsoft.Json.JsonConvert.SerializeObject(value);
    }

    static string Table()
    {
        var lines = new List<string>();
        var possible = new List<string>();
        var sides = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Files)
        {
            // Which targets TrialDraw auto-rewards at autoRewardProbability 1: every trial of a target that can have it is.
            var seen = new SortedDictionary<string, bool>(StringComparer.Ordinal);
            foreach (var random in new[] { false, true })
            {
                if (random && file.Contains("go-no-go")) continue;   // go/no-go has no random-reward probe
                var draw = new TrialDraw(Fixture.Text(Task(file, random, false)), Fixture.Speakers(), path => new ZapitConditionsFile(2, ZapitConditions.MaximumRampDownSeconds));
                for (int n = 0; n < Draws; n++)
                {
                    var t = draw.Next(new TrialRow { outcome = TrialOutcomes.Correct, choice = Choice.Left });
                    seen[t.target] = seen.ContainsKey(t.target) ? seen[t.target] && t.autoReward : t.autoReward;
                    // A side target's spout from the combination's picked azimuths (the grating's or the sound's sign).
                    var named = t.values.Target.Value;
                    if (named == Target.VisualSide || named == Target.AuditorySide)
                        sides.Add("  {" + string.Join(", ", new[] {
                            "\"target\": " + Json(named == Target.VisualSide ? "visualSide" : "auditorySide"),
                            "\"visualAzimuthDeg\": " + Json(t.values.VisualStimulus.InitialAzimuthsDeg[0]),
                            "\"auditoryAzimuthDeg\": " + Json(t.values.AuditoryStimulus.InitialAzimuthsDeg[0]),
                            "\"resolved\": " + Json(t.target) }) + "}");
                }
            }
            var type = Task(file, false, false).TaskType;
            var taskType = type == TaskType.DiscriminationGoNoGo ? "discriminationGoNoGo" : type == TaskType.DiscriminationLeftRight ? "discriminationLeftRight" : "localization";
            possible.Add("  " + Json(taskType) + ": {" + string.Join(", ", seen.Select(e => Json(e.Key) + ": " + Json(e.Value))) + "}");

            // Every choice on every target (a side target as the draw resolves it, left or right), with and without auto-reward where the draw gives it, each coin side on a random
            // probe, with and without penaliseProbes.
            var goNoGo = taskType == "discriminationGoNoGo";
            var choices = goNoGo ? new[] { Choice.Go, Choice.NoResponse } : new[] { Choice.Left, Choice.Right, Choice.NoResponse };
            foreach (var target in seen.Keys)
                foreach (var penaliseProbes in new[] { false, true })
                {
                    var values = Task(file, target == TargetWords.Random, penaliseProbes);
                    foreach (var choice in choices)
                        foreach (var auto in seen[target] ? new[] { false, true } : new[] { false })
                            foreach (var heads in target == TargetWords.Random ? new bool?[] { true, false } : new bool?[] { null })
                            {
                                var trial = new Trial { target = target, autoReward = auto, probeHeads = heads ?? false, values = values };
                                var outcome = TrialRow.Outcome(choice, trial);
                                lines.Add("  {" + string.Join(", ", new[] {
                                    "\"taskType\": " + Json(taskType), "\"target\": " + Json(target), "\"choice\": " + Json(choice),
                                    "\"autoReward\": " + Json(auto), "\"heads\": " + Json(heads), "\"penaliseProbes\": " + Json(penaliseProbes),
                                    "\"outcome\": " + Json(outcome), "\"rewarded\": " + Json(TrialRow.Rewarded(outcome, choice, trial)),
                                    "\"penalised\": " + Json(TrialRow.Penalised(outcome, choice, trial)) }) + "}");
                            }
                }
        }
        return "{\n\"autoRewardPossible\": {\n" + string.Join(",\n", possible) + "\n},\n\"sideTargets\": [\n" + string.Join(",\n", sides) + "\n],\n\"outcomes\": [\n" + string.Join(",\n", lines) + "\n]\n}\n";
    }

    [TestMethod]
    public void TheOutcomeTableIsTheRules()
    {
        var file = Path.Combine(Fixture.Repo(), "tests", "fixtures", "outcomes.json");
        var table = Table();
        if (Environment.GetEnvironmentVariable("OUTCOMES_REGEN") == "1") File.WriteAllText(file, table);
        Assert.IsTrue(File.Exists(file), file + " is missing: run the C# tests with OUTCOMES_REGEN=1");
        Assert.AreEqual(File.ReadAllText(file).Replace("\r\n", "\n"), table,
            "The discrimination outcome rules differ from " + file + ": if the change is meant, rerun with OUTCOMES_REGEN=1, then make "
            + "src\\ucl_open_coen_dome_decision_making\\trial_timeline.py follow (tests\\test_trial_timeline.py) and run uv run trial-timeline");
    }
}
