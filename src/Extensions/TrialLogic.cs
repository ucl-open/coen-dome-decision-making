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
    public double itiSeconds;          // the interval drawn before this trial (the last trial's), NaN on the first
    public double itiStartTime;        // Harp time that interval began, NaN on the first
    public double nextItiSeconds;      // the interval drawn for after this trial
    public double waitAfterDrinkSeconds; // nextItiSeconds less drinkSeconds, at least 0: the rest of the interval
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

    public static TrialEnd First()
    {
        return new TrialEnd { Outcome = null, ItiStartTime = double.NaN };
    }
}

[Combinator]
[Description("Starts the trial loop: one TrialEnd with no outcome for each input.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class StartTrials
{
    public IObservable<TrialEnd> Process<TSource>(IObservable<TSource> source)
    {
        return source.Select(_ => TrialEnd.First());
    }
}

[Combinator]
[Description("Ends a trial: its outcome, stamped with the Harp time its inter-trial interval begins.")]
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
[Description("Draws each trial from the task file and rig.yml's speakers. Input: the last trial's end (StartTrials before the first), the task and the speaker array. Checks the task at the first trial and stops the workflow on a stage past the table or a side with no stimulus or speaker.")]
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
        if (stage < 0 || stage >= task.TrainingStages.Count)
        {
            throw new InvalidOperationException(string.Format(
                "TrainingStage is {0}, but the task file's trainingStages has rows 0 to {1}.", stage, task.TrainingStages.Count - 1));
        }
        this.stage = task.TrainingStages[stage];
        Check(task, this.stage, speakers);
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

    // Bonsai reads the task file without pydantic, so the rules the draw needs are checked again here.
    static void Check(UclOpenCoenDomeDecisionMakingTaskParameters task, TrainingStage stage, DomeSpeakerArray speakers)
    {
        var problems = new List<string>();
        var weights = Weights(stage);
        if (weights.Any(w => w < 0) || weights.Sum() <= 0) problems.Add("stage '" + stage.Label + "' needs trialWeights of 0 or more, one above 0");
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
        if (problems.Count > 0) throw new InvalidOperationException("The task cannot be run: " + string.Join("; ", problems) + ".");
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
        trial.itiSeconds = last == null ? double.NaN : last.nextItiSeconds;
        trial.itiStartTime = last == null ? double.NaN : end.ItiStartTime;
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
        var structure = task.TrialStructure;
        var trial = new Trial();
        trial.autoReward = random.NextDouble() < stage.AutoRewardProbability;
        trial.spoutMovements = stage.SpoutMovements;
        trial.bothSpouts = random.NextDouble() < stage.BothSpoutsProbability;
        var large = random.NextDouble() < task.Reward.LargeRewardProbability;
        trial.rewardSizeUl = large ? stage.RewardSizeUl * task.Reward.LargeRewardMultiplier : stage.RewardSizeUl;
        trial.nextItiSeconds = Uniform(structure.ItiMinSeconds, structure.ItiMaxSeconds);
        trial.waitAfterDrinkSeconds = Math.Max(0, trial.nextItiSeconds - structure.DrinkSeconds);
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
[Description("A trial's Zapit StartStimulus: its condition, the laser on or off (galvos and masking light either way), the task's power on laser-on trials, timed for the laser's duration.")]
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
        if (trial.laserCondition < 1) throw new InvalidOperationException("Trial " + trial.trialNumber + " has no Zapit condition.");
        return new ZapitCommand
        {
            Command = ZapitCommandType.StartStimulus,
            ConditionNumber = trial.laserCondition,
            LaserOn = trial.laserOn,
            HardwareTriggered = hardwareTriggered,
            Logging = true,
            StimDuration = (float)durationSeconds,
            LaserPower = trial.laserOn ? (float?)trial.laserPowerMw : null,
        };
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

[Combinator]
[Description("A Behavior board write that sets (Set true) or clears the input line, such as ZapitTriggerLine.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class LineMessage
{
    [Description("True sets the line high, false clears it.")]
    public bool Set { get; set; }

    public IObservable<HarpMessage> Process(IObservable<Harp.Behavior.DigitalOutputs> source)
    {
        var set = Set;
        return source.Select(line => set
            ? Harp.Behavior.OutputSet.FromPayload(MessageType.Write, line)
            : Harp.Behavior.OutputClear.FromPayload(MessageType.Write, line));
    }
}
