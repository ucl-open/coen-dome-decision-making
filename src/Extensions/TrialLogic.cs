using Bonsai;
using Bonsai.Harp;
using OpenRigs.Devices.Zapit;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using System.Xml.Serialization;
using UclOpenCoenDomeDecisionMaking;
using UclOpenCoenDomeDecisionMakingTask;

/// <summary>One trial as drawn: its fields are named as the trial log's columns where they are one.</summary>
public class Trial
{
    public int trialNumber;            // from 1, repeats counted
    public string trialType;           // TrialDraw.TypeNames: what the trial shows
    public string target;              // Left or Right
    public double rewardSizeUl;
    public bool bothSpouts;
    public int repeatCount;            // 0 for a new draw
    public double itiSeconds;          // the interval drawn for this trial: its start to the stimulus, with a quiet mouse
    public double itiStartTime;        // Harp time that interval began: the last outcome after any timeout (trial 1: the first draw)
    public double leadSeconds;         // Go to the stimulus: max(laserToStimulusSeconds, 0), in every experiment
    public double triggerAfterGoSeconds; // Go to the trigger: max(-laserToStimulusSeconds, 0); read only with optogenetics
    public double lickQuietSeconds;    // the stage's quiet waits (TrainingStage), 0: off
    public double wheelQuietSeconds;
    public double wheelQuietCm;
    public double goTime;              // Harp time of Go (WaitForGo): the trigger with a lead, the stimulus without; NaN before
    public double quietWaitSeconds;    // how long the quiet waits put Go after the interval allowed it; NaN before Go
    public double visualAzimuthDeg;
    public double visualElevationDeg;
    public double visualContrast;      // 0: grey screen
    public double visualPhaseRad;
    public bool audioEnabled;          // false: no sound (discrimination's visual and neutral trials)
    public int speakerChannel;         // the mixer channel of rig.yml's speaker nearest the drawn location; -1 when silent
    public double audioAzimuthDeg;     // that speaker's azimuth; NaN when silent
    public double audioElevationDeg;
    public int audioWaveformIndex;
    public int laserCondition;         // 0 without optogenetics; on every optogenetics trial, laser on or off
    public bool laserOn;
    public double laserPowerMw;        // 0 with the laser off
    public bool autoReward;
    public bool spoutMovements;
    public int rngSeed;                // the experiment's seed (TrialDraw.Seed), logged once as an RngSeed event

    public Trial Clone()
    {
        return (Trial)MemberwiseClone();
    }
}

/// <summary>How the last trial ended: its outcome and the Harp time its inter-trial interval began.</summary>
public class TrialEnd
{
    public string Outcome;      // Correct, Incorrect or NoResponse; null before the first trial
    public double ItiStartTime;

    public static TrialEnd First(double seconds)
    {
        return new TrialEnd { Outcome = null, ItiStartTime = seconds };
    }
}

[Combinator]
[Description("Starts the trial loop: one TrialEnd with no outcome for each input, the first trial's interval starting at the input's Harp time.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class StartTrials
{
    public IObservable<TrialEnd> Process<TSource>(IObservable<Timestamped<TSource>> source)
    {
        return source.Select(value => TrialEnd.First(value.Seconds));
    }
}

[Combinator]
[Description("Ends a trial at the start of the next interval: its outcome, stamped with the Harp time the interval begins (after any timeout), so the next trial is drawn and Zapit armed while the interval runs.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class EndTrial
{
    public IObservable<TrialEnd> Process(IObservable<Timestamped<string>> source)
    {
        return source.Select(value => new TrialEnd { Outcome = value.Value, ItiStartTime = value.Seconds });
    }
}

/// <summary>
/// Draws every trial of an experiment from the task file: the trial type by the stage's weights, repeats by the
/// stage's rules, the stimuli, the speaker that plays (rig.yml's nearest) and the Zapit condition. One instance draws
/// every trial, so one random stream and one repeat history serve the experiment.
/// </summary>
[Combinator]
[Description("Draws each trial from the task file and rig.yml's speakers, at the start of its interval. Input: the last trial's end (StartTrials before the first), the task and the speaker array. Checks the task at the first trial and stops the workflow on a stage past the table or a side with no stimulus or speaker.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class DrawTrial
{
    [Description("The task file's trainingStages row; a row past the end stops the workflow.")]
    public int TrainingStage { get; set; }

    public IObservable<Trial> Process(IObservable<Tuple<TrialEnd, UclOpenCoenDomeDecisionMakingTaskLogic, DomeSpeakerArray>> source)
    {
        return Observable.Defer(() =>
        {
            TrialDraw draw = null;
            return source.Select(input =>
            {
                if (draw == null) draw = new TrialDraw(input.Item2.TaskParameters, input.Item3, TrainingStage, null);
                return draw.Next(input.Item1);
            });
        });
    }
}

[Combinator]
[Description("Checks the task at Go, before the first trial: the stage is in the table and the draw's rules hold on rig.yml's speakers (TrialDraw.CheckTask). Input: the task, TrainingStage and the speaker array; stops the workflow naming every problem, else passes the task on.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class CheckTaskAtGo
{
    public IObservable<UclOpenCoenDomeDecisionMakingTaskLogic> Process(IObservable<Tuple<UclOpenCoenDomeDecisionMakingTaskLogic, int, DomeSpeakerArray>> source)
    {
        return source.Select(input =>
        {
            TrialDraw.CheckTask(input.Item1.TaskParameters, input.Item2, input.Item3);
            return input.Item1;
        });
    }
}

/// <summary>The draw's state and rules, apart from Bonsai so a test can run them.</summary>
public class TrialDraw
{
    public static readonly string[] TypeNames = { "visual", "auditory", "audiovisualCoherent", "audiovisualConflict", "neutral" };
    const double CentreAzimuth = 0, CentreElevation = 15;  // the sound of visual and neutral trials in localization

    readonly UclOpenCoenDomeDecisionMakingTaskParameters task;
    readonly DomeSpeakerArray speakers;
    readonly TrainingStage stage;
    readonly Random random;
    readonly int conditionCount;
    Trial last;
    int drawn;

    /// <summary>The seed the random stream started from: the task's rngSeed, or one drawn when it is null.</summary>
    public int Seed { get; private set; }

    public TrialDraw(UclOpenCoenDomeDecisionMakingTaskParameters task, DomeSpeakerArray speakers, int stage, Func<string, int> zapitConditionCount)
    {
        this.task = task;
        this.speakers = speakers;
        this.stage = CheckTask(task, stage, speakers);
        var optogenetics = task.Optogenetics;
        if (optogenetics.UseZapit)
        {
            if (!(optogenetics.LaserPowerMw > 0))
            {
                throw new InvalidOperationException("optogenetics.useZapit is on, so optogenetics.laserPowerMw must be a power in mW above 0.");
            }
            var path = optogenetics.ZapitConfigPath;
            conditionCount = zapitConditionCount != null ? zapitConditionCount(path) : ZapitConditions.Check(path);
        }
        Seed = task.RngSeed.HasValue ? (int)task.RngSeed.Value : Guid.NewGuid().GetHashCode();
        random = new Random(Seed);
    }

    /// <summary>The stage's row, once the stage is in the table and the task can be run on these speakers (CheckTask
    /// at Go, and the draw again at the first trial); throws naming every problem.</summary>
    public static TrainingStage CheckTask(UclOpenCoenDomeDecisionMakingTaskParameters task, int stage, DomeSpeakerArray speakers)
    {
        if (stage < 0 || stage >= task.TrainingStages.Count)
        {
            throw new InvalidOperationException(string.Format(
                "TrainingStage is {0}, but the task file's trainingStages has rows 0 to {1}.", stage, task.TrainingStages.Count - 1));
        }
        var row = task.TrainingStages[stage];
        Check(task, row, speakers);
        return row;
    }

    // Bonsai reads the task file without pydantic, so the rules the draw needs are checked again here.
    static void Check(UclOpenCoenDomeDecisionMakingTaskParameters task, TrainingStage stage, DomeSpeakerArray speakers)
    {
        var problems = new List<string>();
        var weights = Weights(stage);
        if (weights.Any(w => w < 0) || weights.Sum() <= 0) problems.Add("stage '" + stage.Label + "' needs trialWeights of 0 or more, one above 0");
        if (task.TaskType == TaskType.Discrimination)
        {
            // A target modality with no trials leaves only the other modality's: every one a distractor at the right spout.
            bool visualTarget = task.TargetModality == Modality.Visual;
            int target = visualTarget ? weights[0] : weights[1], other = visualTarget ? weights[1] : weights[0];
            if (target == 0 && other > 0)
            {
                string t = visualTarget ? "visual" : "auditory", o = visualTarget ? "auditory" : "visual";
                problems.Add(string.Format("stage '{0}' gives targetModality {1} no trials (trialWeights.{1} 0), so every {2} trial would be a distractor: swap the {1} and {2} weights on the form", stage.Label, t, o));
            }
        }
        if (stage.Contrasts == null || stage.Contrasts.Count == 0 || stage.Contrasts.Any(c => !(c > 0))) problems.Add("stage '" + stage.Label + "' needs contrasts, each above 0");
        var visual = task.VisualStimulus;
        foreach (var right in new[] { false, true })
        {
            var side = right ? "right" : "left";
            if (AzimuthCandidates(visual, right, true).Count == 0 && !HasRange(visual, right, true)) problems.Add("visualStimulus has no " + side + " azimuth within coherentAzimuthCapDeg");
            var locations = task.AudioStimulus.SpeakerLocations.Where(l => right ? l.Azimuth > 0 : l.Azimuth < 0).ToList();
            if (locations.Count == 0) problems.Add("audioStimulus.speakerLocations has none on the " + side);
            foreach (var location in locations)
            {
                var speaker = NearestSpeaker(speakers, location.Azimuth, location.Elevation);
                if (right ? speaker.Value.Position.Azimuth <= 0 : speaker.Value.Position.Azimuth >= 0)
                {
                    problems.Add(string.Format("rig.yml's speaker nearest the {0} location ({1}, {2}) is '{3}', not on the {0}", side, location.Azimuth, location.Elevation, speaker.Key));
                }
            }
        }
        if (task.AudioStimulus.SpeakerLocations.Any(l => l.Azimuth == 0)) problems.Add("audioStimulus.speakerLocations has an azimuth-0 entry; the centre sound is fixed in code");
        var structure = task.TrialStructure;
        if (structure.StimulusSeconds > structure.OpenLoopSeconds + structure.ClosedLoopSeconds) problems.Add("trialStructure.stimulusSeconds is longer than openLoopSeconds plus closedLoopSeconds");
        if (structure.ItiMaxSeconds < structure.ItiMinSeconds) problems.Add("trialStructure.itiMaxSeconds is below itiMinSeconds");
        if (structure.ItiMinSeconds < structure.DrinkSeconds) problems.Add(string.Format("trialStructure.itiMinSeconds ({0} s) is below drinkSeconds ({1} s): the spouts go out drinkSeconds into the interval, before the stimulus", structure.ItiMinSeconds, structure.DrinkSeconds));
        if (stage.LickQuietSeconds < 0 || stage.WheelQuietSeconds < 0 || stage.WheelQuietCm < 0) problems.Add("stage '" + stage.Label + "' has a negative lickQuietSeconds, wheelQuietSeconds or wheelQuietCm");
        if ((stage.WheelQuietSeconds > 0) != (stage.WheelQuietCm > 0)) problems.Add("stage '" + stage.Label + "' needs wheelQuietSeconds and wheelQuietCm both above 0 (the wheel's quiet wait) or both 0 (off)");
        var opto = task.Optogenetics;
        var lead = opto.LaserToStimulusSeconds;
        if (lead + ZapitReplyMarginSeconds > structure.ItiMinSeconds + 1e-9)
        {
            problems.Add(string.Format("optogenetics.laserToStimulusSeconds ({0} s) plus Zapit's {1} s reply margin must be at most trialStructure.itiMinSeconds ({2} s), the shortest interval", lead, ZapitReplyMarginSeconds, structure.ItiMinSeconds));
        }
        if (-lead > structure.OpenLoopSeconds + structure.ClosedLoopSeconds + 1e-9)
        {
            problems.Add(string.Format("optogenetics.laserToStimulusSeconds ({0} s) puts the trigger after the response window (openLoopSeconds + closedLoopSeconds, {1} s)", lead, structure.OpenLoopSeconds + structure.ClosedLoopSeconds));
        }
        var longest = LongestLaserSeconds(task);
        if (opto.UseZapit && opto.LaserDurationSeconds > longest + 1e-9)
        {
            problems.Add(string.Format("optogenetics.laserDurationSeconds ({0} s) must be at most {1} s (openLoopSeconds + itiMinSeconds - {2} s ramp-down - {3} s reply margin), so Zapit can be armed for the next trial in time", opto.LaserDurationSeconds, longest, ZapitRampDownSeconds, ZapitReplyMarginSeconds));
        }
        if (problems.Count > 0) throw new InvalidOperationException("The task cannot be run: " + string.Join("; ", problems) + ".");
    }

    /// <summary>How long before its trigger Zapit's armed reply must be in, in seconds (task.py ZAPIT_REPLY_MARGIN_SEC).</summary>
    public const double ZapitReplyMarginSeconds = 0.2;

    /// <summary>The longest ramp-down a conditions file may have, in seconds: Zapit refuses the next StartStimulus while a
    /// timed stimulus and its ramp-down play, so the next trial is armed only this long after the laser ends.</summary>
    public const double ZapitRampDownSeconds = ZapitConditions.MaximumRampDownMs / 1000;

    /// <summary>The longest laserDurationSeconds (task.py's same rule): from a trigger to the next is at least openLoopSeconds
    /// + itiMinSeconds (a response at once, no timeout, a quiet mouse), and in that time the laser, its ramp-down and Zapit's
    /// reply margin must fit before the next trigger.</summary>
    public static double LongestLaserSeconds(UclOpenCoenDomeDecisionMakingTaskParameters task)
    {
        var s = task.TrialStructure;
        return s.OpenLoopSeconds + s.ItiMinSeconds - ZapitRampDownSeconds - ZapitReplyMarginSeconds;
    }

    public static int[] Weights(TrainingStage stage)
    {
        var w = stage.TrialWeights;
        return new[] { w.Visual, w.Auditory, w.AudiovisualCoherent, w.AudiovisualConflict, w.Neutral };
    }

    static RepeatRule Rule(RepeatSettings repeats, int type)
    {
        var r = repeats.RulesPerType;
        return new[] { r.Visual, r.Auditory, r.AudiovisualCoherent, r.AudiovisualConflict, r.Neutral }[type];
    }

    /// <summary>The speaker in rig.yml at the smallest angle from the location.</summary>
    public static KeyValuePair<string, Speaker> NearestSpeaker(DomeSpeakerArray speakers, double azimuth, double elevation)
    {
        if (speakers == null || speakers.Speakers == null || speakers.Speakers.Count == 0)
        {
            throw new InvalidOperationException("rig.yml has no speakerArray.speakers to play the auditory stimuli on.");
        }
        return speakers.Speakers.OrderBy(s => Angle(azimuth, elevation, s.Value.Position.Azimuth, s.Value.Position.Elevation)).First();
    }

    static double Angle(double az1, double el1, double az2, double el2)
    {
        const double rad = Math.PI / 180;
        var cos = Math.Sin(el1 * rad) * Math.Sin(el2 * rad) + Math.Cos(el1 * rad) * Math.Cos(el2 * rad) * Math.Cos((az1 - az2) * rad);
        return Math.Acos(Math.Max(-1, Math.Min(1, cos)));
    }

    /// <summary>The next trial, after the last one ended as <paramref name="end"/>.</summary>
    public Trial Next(TrialEnd end)
    {
        Trial trial;
        if (last != null && end.Outcome != null && ShouldRepeat(end.Outcome))
        {
            trial = last.Clone();
            trial.repeatCount = last.repeatCount + 1;
            if (task.Optogenetics.NoLaserOnRepeats && trial.laserOn)
            {
                trial.laserOn = false;
                trial.laserPowerMw = 0;
            }
        }
        else
        {
            trial = New();
        }
        trial.trialNumber = ++drawn;
        trial.rngSeed = Seed;
        // Every trial, a repeat too, draws its own interval, from the end it is drawn at (Pip, 13 Nov 2026).
        var structure = task.TrialStructure;
        trial.itiSeconds = Uniform(structure.ItiMinSeconds, structure.ItiMaxSeconds);
        trial.itiStartTime = end.ItiStartTime;
        var lead = task.Optogenetics.LaserToStimulusSeconds;
        trial.leadSeconds = Math.Max(lead, 0);
        trial.triggerAfterGoSeconds = Math.Max(-lead, 0);
        trial.lickQuietSeconds = stage.LickQuietSeconds;
        trial.wheelQuietSeconds = stage.WheelQuietSeconds;
        trial.wheelQuietCm = stage.WheelQuietCm;
        trial.goTime = double.NaN;
        trial.quietWaitSeconds = double.NaN;
        last = trial;
        return trial;
    }

    bool ShouldRepeat(string outcome)
    {
        if (!stage.AllowRepeats || last.repeatCount >= task.Repeats.MaxRepeats) return false;
        var type = Array.IndexOf(TypeNames, last.trialType);
        switch (Rule(task.Repeats, type))
        {
            case RepeatRule.OnFailure: return outcome != "Correct";
            case RepeatRule.OnTimeout: return outcome == "NoResponse";
            default: return false;
        }
    }

    Trial New()
    {
        var trial = new Trial();
        trial.autoReward = random.NextDouble() < stage.AutoRewardProbability;
        trial.spoutMovements = stage.SpoutMovements;
        trial.bothSpouts = random.NextDouble() < stage.BothSpoutsProbability;
        var large = random.NextDouble() < task.Reward.LargeRewardProbability;
        trial.rewardSizeUl = large ? stage.RewardSizeUl * task.Reward.LargeRewardMultiplier : stage.RewardSizeUl;
        trial.visualPhaseRad = 2 * Math.PI * random.NextDouble();

        int type = WeightedType();
        trial.trialType = TypeNames[type];
        bool discrimination = task.TaskType == TaskType.Discrimination;
        // Localization plays a centre sound on visual and neutral trials; discrimination's are silent.
        bool showsSound = !discrimination || (type != 0 && type != 4);

        bool right;
        double soundAzimuth = CentreAzimuth, soundElevation = CentreElevation;
        switch (type)
        {
            case 0: // the grating alone; in localization a centre sound
                right = random.NextDouble() >= 0.5;
                SetVisual(trial, right, false);
                break;
            case 1: // the sound alone
                right = random.NextDouble() >= 0.5;
                PickLocation(right, out soundAzimuth, out soundElevation);
                break;
            case 2: // coherent: both on one side, the grating capped
                right = random.NextDouble() >= 0.5;
                SetVisual(trial, right, true);
                PickLocation(right, out soundAzimuth, out soundElevation);
                break;
            case 3: // conflict: the grating's side is the answer, the sound on the other
                right = random.NextDouble() >= 0.5;
                SetVisual(trial, right, false);
                PickLocation(!right, out soundAzimuth, out soundElevation);
                break;
            default: // neutral: grey screen, a random answer
                right = random.NextDouble() >= 0.5;
                break;
        }
        trial.target = right ? "Right" : "Left";
        if (discrimination)
        {
            // The answer is the modality: the target modality's trials at the left spout, the other's at the right;
            // coherent and conflict trials show both, so their side stays as drawn.
            if (type == 0 || type == 1)
            {
                bool shownIsTarget = (type == 0) == (task.TargetModality == Modality.Visual);
                trial.target = shownIsTarget ? "Left" : "Right";
            }
            if (trial.target != "Left") trial.autoReward = false;  // auto-reward only at the target modality's spout
        }

        trial.audioEnabled = showsSound;
        if (showsSound)
        {
            var speaker = NearestSpeaker(speakers, soundAzimuth, soundElevation).Value;
            trial.speakerChannel = speaker.Channel;
            trial.audioAzimuthDeg = speaker.Position.Azimuth;
            trial.audioElevationDeg = speaker.Position.Elevation;
        }
        else
        {
            trial.speakerChannel = -1;
            trial.audioAzimuthDeg = double.NaN;
            trial.audioElevationDeg = double.NaN;
        }

        var optogenetics = task.Optogenetics;
        if (optogenetics.UseZapit)
        {
            trial.laserCondition = random.Next(conditionCount) + 1;
            trial.laserOn = drawn >= optogenetics.TrialsBeforeLaser && random.NextDouble() < optogenetics.LaserOnProbability;
            trial.laserPowerMw = trial.laserOn ? optogenetics.LaserPowerMw.Value : 0;
        }
        // Drawn last, so adding it changed no earlier draw; a repeat plays the waveform of the trial it repeats.
        trial.audioWaveformIndex = random.Next(AuditoryStimulusSet.WaveformCount);
        return trial;
    }

    int WeightedType()
    {
        var weights = Weights(stage);
        int draw = random.Next(weights.Sum());
        for (int type = 0; type < weights.Length; type++)
        {
            draw -= weights[type];
            if (draw < 0) return type;
        }
        throw new InvalidOperationException("No trial type drawn.");
    }

    void SetVisual(Trial trial, bool right, bool capped)
    {
        var visual = task.VisualStimulus;
        var fixedAzimuths = AzimuthCandidates(visual, right, capped);
        if (visual.FixedAzimuthsDeg != null && visual.FixedAzimuthsDeg.Count > 0)
        {
            trial.visualAzimuthDeg = fixedAzimuths[random.Next(fixedAzimuths.Count)];
        }
        else
        {
            double cap = capped ? visual.CoherentAzimuthCapDeg : 180;
            trial.visualAzimuthDeg = right
                ? Uniform(Math.Max(visual.AzimuthMinDeg, 0), Math.Min(visual.AzimuthMaxDeg, cap))
                : Uniform(Math.Max(visual.AzimuthMinDeg, -cap), Math.Min(visual.AzimuthMaxDeg, 0));
        }
        trial.visualElevationDeg = Uniform(visual.ElevationMinDeg, visual.ElevationMaxDeg);
        trial.visualContrast = stage.Contrasts[random.Next(stage.Contrasts.Count)];
    }

    static List<double> AzimuthCandidates(VisualStimulus visual, bool right, bool capped)
    {
        var values = visual.FixedAzimuthsDeg ?? new List<double>();
        return values.Where(a => (right ? a > 0 : a < 0) && (!capped || Math.Abs(a) <= visual.CoherentAzimuthCapDeg)).ToList();
    }

    static bool HasRange(VisualStimulus visual, bool right, bool capped)
    {
        if (visual.FixedAzimuthsDeg != null && visual.FixedAzimuthsDeg.Count > 0) return false;
        double cap = capped ? visual.CoherentAzimuthCapDeg : 180;
        return right ? Math.Max(visual.AzimuthMinDeg, 0) < Math.Min(visual.AzimuthMaxDeg, cap)
                     : Math.Max(visual.AzimuthMinDeg, -cap) < Math.Min(visual.AzimuthMaxDeg, 0);
    }

    void PickLocation(bool right, out double azimuth, out double elevation)
    {
        var candidates = task.AudioStimulus.SpeakerLocations.Where(l => right ? l.Azimuth > 0 : l.Azimuth < 0).ToList();
        var picked = candidates[random.Next(candidates.Count)];
        azimuth = picked.Azimuth;
        elevation = picked.Elevation;
    }

    double Uniform(double low, double high)
    {
        return low + random.NextDouble() * (high - low);
    }
}

[Combinator]
[Description("A trial's Zapit StartStimulus: its condition, the laser on or off (galvos and masking light either way), the trial's laserPowerMw (0 mW on laser-off trials), timed for the laser's duration.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class ZapitTrialCommand
{
    [Description("Whether the Zapit DAQ waits for the ZapitTrigger line (rig.yml's zapit.hardwareTriggered).")]
    public bool HardwareTriggered { get; set; }

    [Description("How long the stimulus lasts, in seconds (optogenetics.laserDurationSeconds).")]
    public double DurationSeconds { get; set; }

    public IObservable<ZapitCommand> Process(IObservable<Trial> source)
    {
        return source.Select(trial => Build(trial, HardwareTriggered, DurationSeconds));
    }

    public static ZapitCommand Build(Trial trial, bool hardwareTriggered, double durationSeconds)
    {
        return ZapitTrial.Command(trial.trialNumber, trial.laserCondition, trial.laserOn, trial.laserPowerMw, hardwareTriggered, durationSeconds);
    }
}

[Combinator]
[Description("Checks Zapit's reply to a trial's StartStimulus (input: the reply and the trial) and stops the workflow unless Zapit armed that condition with the laser as the trial asked.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class CheckZapitArmed
{
    public IObservable<ZapitReply> Process(IObservable<Tuple<ZapitReply, Trial>> source)
    {
        return source.Select(input =>
        {
            Check(input.Item1, input.Item2);
            return input.Item1;
        });
    }

    public static void Check(ZapitReply reply, Trial trial)
    {
        if (reply.IsError)
        {
            throw new InvalidOperationException(string.Format(
                "Zapit refused trial {0}'s StartStimulus (condition {1}): {2}. Is the conditions file the task names loaded, is Zapit calibrated, and is the DAQ working?",
                trial.trialNumber, trial.laserCondition, reply));
        }
        int laserOn = trial.laserOn ? 1 : 0;
        if (reply.Value != trial.laserCondition || reply.LaserOn != laserOn)
        {
            throw new InvalidOperationException(string.Format(
                "Zapit armed condition {0} with laserOn {1}, but trial {2} asked for condition {3} with laserOn {4}.",
                reply.Value, reply.LaserOn, trial.trialNumber, trial.laserCondition, laserOn));
        }
    }
}

/// <summary>
/// A trial's quiet waits (Pip, 13 Nov 2026), on the Harp clock, counted from the interval's start: Go needs every
/// spout released for lickSeconds (a held contact blocks Go), and the wheel's distance (forward and back added)
/// within wheelCm over the last wheelSeconds; Go is the first moment at or after earliest at which both hold. With both
/// off Go is earliest. coen-dome-navigation's Corridor.cs has the same class: change both together.
/// </summary>
public class QuietWait
{
    readonly double earliest, lickSeconds, wheelSeconds, wheelCm;
    readonly Queue<KeyValuePair<double, double>> moves = new Queue<KeyValuePair<double, double>>();  // (Harp time, cm) in the window
    double lickSince, wheelSince, lastWheel, recentCm;
    bool held;

    public QuietWait(double intervalStart, double earliest, double lickSeconds, double wheelSeconds, double wheelCm, double wheelAtStart, bool lickHeld)
    {
        this.earliest = earliest;
        this.lickSeconds = lickSeconds;
        this.wheelSeconds = wheelSeconds;
        this.wheelCm = wheelCm;
        lickSince = wheelSince = intervalStart;
        lastWheel = wheelAtStart;
        held = lickHeld;
    }

    /// <summary>A spout touched or released at Harp time now (held: any spout still touched) restarts the lick wait.</summary>
    public void Lick(bool anyHeld, double now)
    {
        held = anyHeld;
        lickSince = Math.Max(lickSince, now);
    }

    /// <summary>The wheel's distance in cm at Harp time now: once the travel within the last wheelSeconds passes wheelCm,
    /// the wait restarts at the latest move that still takes it past.</summary>
    public void Wheel(double cm, double now)
    {
        var moved = double.IsNaN(lastWheel) ? 0 : Math.Abs(cm - lastWheel);
        lastWheel = cm;
        if (!(wheelSeconds > 0)) return;
        while (moves.Count > 0 && moves.Peek().Key <= now - wheelSeconds) recentCm -= moves.Dequeue().Value;
        if (moved == 0) return;
        moves.Enqueue(new KeyValuePair<double, double>(now, moved));
        recentCm += moved;
        while (recentCm > wheelCm)
        {
            var oldest = moves.Dequeue();
            recentCm -= oldest.Value;
            wheelSince = Math.Max(wheelSince, oldest.Key);
        }
    }

    /// <summary>When Go falls if nothing more restarts a wait (infinity while a spout is held): exact, not rounded to a clock tick.</summary>
    public double GoTime
    {
        get
        {
            if (lickSeconds > 0 && held) return double.PositiveInfinity;
            return Math.Max(earliest, Math.Max(lickSeconds > 0 ? lickSince + lickSeconds : 0, wheelSince + wheelSeconds));
        }
    }

    public double Earliest { get { return earliest; } }
}

/// <summary>Holds one trial at a time until its Go; apart from Bonsai so a test can run it.</summary>
public class GoWaiter
{
    Trial trial;
    QuietWait wait;
    double now = double.NaN, wheel = double.NaN;
    readonly bool[] touched = new bool[2];   // each spout's lick detector: touched now

    /// <summary>A trial drawn at its interval's start begins waiting: Go no sooner than the interval's end less its lead.</summary>
    public void Start(Trial next)
    {
        if (trial != null)
        {
            throw new InvalidOperationException(string.Format("Trial {0} was drawn while trial {1} still waited for its Go.", next.trialNumber, trial.trialNumber));
        }
        trial = next;
        wait = new QuietWait(next.itiStartTime, next.itiStartTime + next.itiSeconds - next.leadSeconds, next.lickQuietSeconds, next.wheelQuietSeconds, next.wheelQuietCm, wheel, touched.Any(t => t));
    }

    /// <summary>A spout's lick detector changed (spout 0 left, 1 right): touched or released.</summary>
    public void Lick(int spout, bool lick)
    {
        if (touched[spout] == lick) return;
        touched[spout] = lick;
        if (trial != null && !double.IsNaN(now)) wait.Lick(touched.Any(t => t), now);
    }

    public void Wheel(double cm)
    {
        wheel = cm;
        if (trial != null && !double.IsNaN(now)) wait.Wheel(cm, now);
    }

    /// <summary>The Harp clock: the trial once its Go has come, with goTime and quietWaitSeconds set; else null.</summary>
    public Trial Clock(double seconds)
    {
        now = seconds;
        if (trial == null || seconds < wait.GoTime - 1e-9) return null;
        var go = trial;
        go.goTime = wait.GoTime;
        go.quietWaitSeconds = wait.GoTime - wait.Earliest;
        trial = null;
        return go;
    }
}

[Combinator]
[Description("Each trial at its Go: the interval's end less the lead (max(laserToStimulusSeconds, 0)), or later until the stage's quiet waits both hold (both spouts released for lickQuietSeconds; the wheel within wheelQuietCm over the last wheelQuietSeconds). Inputs: the trials, the Harp clock, the left and right lick states, the wheel's distance in cm. Sets the trial's goTime and quietWaitSeconds.")]
[WorkflowElementCategory(ElementCategory.Combinator)]
public class WaitForGo
{
    public IObservable<Trial> Process(IObservable<Trial> trials, IObservable<double> clock, IObservable<bool> lickLeft, IObservable<bool> lickRight, IObservable<double> wheelCm)
    {
        return Observable.Create<Trial>(observer =>
        {
            var gate = new object();
            var waiter = new GoWaiter();
            Action<Action> run = step =>
            {
                lock (gate)
                {
                    try { step(); }
                    catch (Exception error) { observer.OnError(error); }
                }
            };
            return new System.Reactive.Disposables.CompositeDisposable(
                wheelCm.Subscribe(cm => run(() => waiter.Wheel(cm)), observer.OnError),
                lickLeft.Subscribe(lick => run(() => waiter.Lick(0, lick)), observer.OnError),
                lickRight.Subscribe(lick => run(() => waiter.Lick(1, lick)), observer.OnError),
                trials.Subscribe(trial => run(() => waiter.Start(trial)), observer.OnError),
                clock.Subscribe(seconds => run(() =>
                {
                    var go = waiter.Clock(seconds);
                    if (go != null) observer.OnNext(go);
                }), observer.OnError));
        });
    }
}

/// <summary>A moment of a trial timed from its Go.</summary>
public enum TrialMoment
{
    Stimulus,       // goTime + leadSeconds
    LaserTrigger    // goTime + triggerAfterGoSeconds
}

[Combinator]
[Description("The seconds from now (the input's Harp time) to the trial's stimulus or laser trigger, for a HarpDelay's DueSeconds: (trial, seconds). A moment up to ClockStepSeconds past (Go is seen on the first clock tick after it) comes at once; one further past stops the workflow, rather than shift it.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class SecondsUntil
{
    /// <summary>How far past a moment may be and still come at once: Go is exact, but is seen on the first tick of the
    /// 1 kHz Harp clock after it, so a moment at Go (the trigger with a lead, the stimulus without) is up to a tick late.</summary>
    public const double ClockStepSeconds = 0.01;

    public TrialMoment Moment { get; set; }

    public IObservable<Tuple<Trial, double>> Process(IObservable<Timestamped<Trial>> source)
    {
        var moment = Moment;
        return source.Select(value => Tuple.Create(value.Value, Seconds(value.Value, moment, value.Seconds)));
    }

    public static double Seconds(Trial trial, TrialMoment moment, double now)
    {
        var due = trial.goTime + (moment == TrialMoment.Stimulus ? trial.leadSeconds : trial.triggerAfterGoSeconds);
        if (double.IsNaN(due) || due < now - ClockStepSeconds)
        {
            throw new InvalidOperationException(string.Format("Trial {0}'s {1} was due at {2} s on the Harp clock, but its wait began at {3} s.", trial.trialNumber, moment == TrialMoment.Stimulus ? "stimulus" : "laser trigger", due, now));
        }
        return Math.Max(0, due - now);
    }
}

[Combinator]
[Description("The Go of the input trial (first input, one trial) from the trials' Gos (second input, a subject that replays the latest): the one with its trialNumber, once.")]
[WorkflowElementCategory(ElementCategory.Combinator)]
public class TrialGoFor
{
    public IObservable<Trial> Process(IObservable<Trial> trial, IObservable<Trial> goes)
    {
        return trial.Take(1).SelectMany(t => goes.Where(go => go.trialNumber == t.trialNumber).Take(1));
    }
}

[Combinator]
[Description("Passes the trial on at its trigger (first input) only if Zapit has armed it by then (second input: CheckZapitArmed's reply); stops the workflow otherwise, rather than shift the trigger.")]
[WorkflowElementCategory(ElementCategory.Combinator)]
public class RequireZapitArmed
{
    public IObservable<Trial> Process(IObservable<Timestamped<Trial>> trigger, IObservable<ZapitReply> armed)
    {
        return Observable.Create<Trial>(observer =>
        {
            var gate = new object();
            bool isArmed = false;
            var armedSubscription = armed.Subscribe(_ => { lock (gate) isArmed = true; }, observer.OnError);
            var triggerSubscription = trigger.Subscribe(value =>
            {
                bool ok;
                lock (gate) ok = isArmed;
                if (ok) observer.OnNext(value.Value);
                else observer.OnError(new InvalidOperationException(string.Format(
                    "Zapit had not answered trial {0}'s StartStimulus by its trigger at {1} s on the Harp clock, so the workflow stopped rather than move the trigger. Is Zapit running and answering quickly?",
                    value.Value.trialNumber, value.Seconds)));
            }, observer.OnError, observer.OnCompleted);
            return new System.Reactive.Disposables.CompositeDisposable(armedSubscription, triggerSubscription);
        });
    }
}

[Combinator]
[Description("Passes the trial on at its laser trigger (first input) unless its row (second input: TrialRow) came before the trigger was due: a trial answered before a trigger after its stimulus gets no laser (Pip, 13 Nov 2026), as in coen-dome-navigation. Completes with the trigger.")]
[WorkflowElementCategory(ElementCategory.Combinator)]
public class UnlessAnswered
{
    public IObservable<Timestamped<Trial>> Process(IObservable<Timestamped<Trial>> trigger, IObservable<Timestamped<TrialRow>> rows)
    {
        return Observable.Create<Timestamped<Trial>>(observer =>
        {
            var gate = new object();
            TrialRow row = null;
            double rowSeconds = double.NaN;
            var rowSubscription = rows.Subscribe(value => { lock (gate) { row = value.Value; rowSeconds = value.Seconds; } }, observer.OnError);
            var triggerSubscription = trigger.Subscribe(value =>
            {
                bool skipped;
                lock (gate) skipped = row != null && row.trialNumber == value.Value.trialNumber && LaserSkipped(value.Value, rowSeconds);
                if (!skipped) observer.OnNext(value);
            }, observer.OnError, observer.OnCompleted);
            return new System.Reactive.Disposables.CompositeDisposable(rowSubscription, triggerSubscription);
        });
    }

    /// <summary>Whether a trial whose outcome came at Harp time outcomeSeconds gets no laser: its trigger was due later.</summary>
    public static bool LaserSkipped(Trial trial, double outcomeSeconds)
    {
        return outcomeSeconds < trial.goTime + trial.triggerAfterGoSeconds;
    }
}

/// <summary>What a trial's outcome sets off: the reward, the spouts, the error sound and the timeout.</summary>
public class Consequence
{
    public string Outcome;
    public bool Rewarded;
    public Tuple<string, double> Reward;   // (side, microlitres) for DeliverReward; null without a reward
    public bool MoveSpouts;
    public OpenRigs.Devices.LickSpoutCommand SpoutCommand;
    public bool ErrorSound;
    public double TimeoutSeconds;
    public bool WithdrawAfterDrink;       // the stage moves the spouts: they go out once the mouse has drunk

    /// <summary>A correct choice or an auto-rewarded miss is rewarded at the target, the other spout drawn back; an
    /// error or a plain miss draws both back, with the row's error sound and timeout. A stage without spout
    /// movements leaves the spouts where they are.</summary>
    public static Consequence Of(TrialRow row, Trial trial)
    {
        var c = new Consequence { Outcome = row.outcome, ErrorSound = row.punishSound, TimeoutSeconds = row.punishTimeoutSeconds };
        c.Rewarded = row.outcome == "Correct" || row.autoRewarded;
        c.Reward = c.Rewarded ? Tuple.Create(trial.target, trial.rewardSizeUl) : null;
        c.MoveSpouts = trial.spoutMovements;
        c.SpoutCommand = !c.Rewarded ? OpenRigs.Devices.LickSpoutCommand.SpoutsOut
            : trial.target == "Left" ? OpenRigs.Devices.LickSpoutCommand.LeftIn : OpenRigs.Devices.LickSpoutCommand.RightIn;
        c.WithdrawAfterDrink = trial.spoutMovements;
        return c;
    }
}

[Combinator]
[Description("What the trial's outcome sets off (Consequence.Of), from its row and the trial.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class TrialConsequence
{
    public IObservable<Consequence> Process(IObservable<Tuple<Timestamped<TrialRow>, Trial>> source)
    {
        return source.Select(value => Consequence.Of(value.Item1.Value, value.Item2));
    }
}

[Combinator]
[Description("Where the spouts go once the stimulus is on: both in on a both-spouts trial, else the target's alone.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class SpoutsIn
{
    public IObservable<OpenRigs.Devices.LickSpoutCommand> Process(IObservable<Trial> source)
    {
        return source.Select(Command);
    }

    public static OpenRigs.Devices.LickSpoutCommand Command(Trial trial)
    {
        if (trial.bothSpouts) return OpenRigs.Devices.LickSpoutCommand.SpoutsIn;
        return trial.target == "Left" ? OpenRigs.Devices.LickSpoutCommand.LeftIn : OpenRigs.Devices.LickSpoutCommand.RightIn;
    }
}
