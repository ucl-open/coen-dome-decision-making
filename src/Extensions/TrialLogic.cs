using Bonsai;
using Bonsai.Harp;
using OpenRigs.Core;
using OpenRigs.Devices;
using OpenRigs.Devices.Rigs;
using OpenRigs.Devices.Zapit;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using UclOpenCoenDomeDecisionMakingTask;

/// <summary>One trial as drawn: its fields are named as the trial log's columns where they are one.</summary>
public class Trial : IZapitTrial
{
    int IZapitTrial.TrialNumber { get { return trialNumber; } }
    int IZapitTrial.LaserCondition { get { return laserCondition; } }
    bool IZapitTrial.LaserOn { get { return laserOn; } }
    double IZapitTrial.LaserPowerMw { get { return laserPowerMw; } }

    public int trialNumber;            // from 1, repeats counted
    public string trialType;           // TrialDraw.TypeNames: what the trial shows
    public string target;              // Left or Right
    public double rewardSizeUl;
    public bool bothSpouts;
    public int repeatCount;            // 0 for a new draw
    public double itiSeconds;          // the interval drawn for this trial: its start to the stimulus, with a quiet mouse
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

[Combinator]
[Description("Checks the task at Go and gives the trials' rules (TrialRules, tested C#): the stage is in the table and the draw's rules hold on rig.yml's speakers (TrialDraw.CheckTask), and with optogenetics the Zapit conditions file. Input: the task, TrainingStage and rig.yml; stops the workflow naming every problem.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class CheckTaskAtGo
{
    public IObservable<TrialRules> Process(IObservable<Tuple<UclOpenCoenDomeDecisionMakingTaskLogic, int, LabRig>> source)
    {
        return source.Select(input => TrialRules.Create(input.Item1.TaskParameters, input.Item2, input.Item3, null));
    }
}

/// <summary>The draw's state and rules, apart from Bonsai so a test can run them.</summary>
public class TrialDraw
{
    public static readonly string[] TypeNames = { "visual", "auditory", "audiovisualCoherent", "audiovisualConflict", "neutral" };
    const double CentreAzimuth = 0, CentreElevation = 15;  // the sound of visual and neutral trials in localization

    readonly UclOpenCoenDomeDecisionMakingTaskParameters task;
    readonly EqualisedSpeakerArray speakers;
    readonly TrainingStage stage;
    readonly Random random;
    readonly int conditionCount;
    Trial last;
    int drawn;

    /// <summary>The seed the random stream started from: the task's rngSeed, or one drawn when it is null.</summary>
    public int Seed { get; private set; }

    /// <summary>The stage's row of the task file's trainingStages.</summary>
    public TrainingStage Stage { get { return stage; } }

    public TrialDraw(UclOpenCoenDomeDecisionMakingTaskParameters task, EqualisedSpeakerArray speakers, int stage, Func<string, int> zapitConditionCount)
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
        Seed = RngSeed.Resolve(task.RngSeed);
        random = new Random(Seed);
    }

    /// <summary>The stage's row, once the stage is in the table and the task can be run on these speakers; throws
    /// naming every problem.</summary>
    public static TrainingStage CheckTask(UclOpenCoenDomeDecisionMakingTaskParameters task, int stage, EqualisedSpeakerArray speakers)
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
    static void Check(UclOpenCoenDomeDecisionMakingTaskParameters task, TrainingStage stage, EqualisedSpeakerArray speakers)
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
        // A NaN or infinite time (YAML's .nan or .inf) would hold the trial start for ever, with no error.
        Action<string, double> finite = (name, value) =>
        {
            if (!(value >= 0) || double.IsInfinity(value)) problems.Add(name + " must be a finite number, 0 or more");
        };
        Action<string, double> positive = (name, value) =>
        {
            if (!(value > 0) || double.IsInfinity(value)) problems.Add(name + " must be a finite number above 0");
        };
        finite("trialStructure.itiMinSeconds", structure.ItiMinSeconds);
        finite("trialStructure.itiMaxSeconds", structure.ItiMaxSeconds);
        finite("trialStructure.drinkSeconds", structure.DrinkSeconds);
        finite("trialStructure.responseDelaySeconds", structure.ResponseDelaySeconds);
        positive("trialStructure.responseWindowSeconds", structure.ResponseWindowSeconds);
        finite("trialStructure.stimulusSeconds", structure.StimulusSeconds);
        finite("trialStructure.stimulusToSpoutsInSeconds", structure.StimulusToSpoutsInSeconds);
        finite("penalty.timeoutSeconds", task.Penalty.TimeoutSeconds);
        // The quiet waits, the lab's one check (OpenRigs.Core QuietWait: finite, 0 or more, at most 10 s and 20 cm, the
        // wheel's pair both set or both 0): a long wait and a licking mouse would hold the trial start with no message.
        foreach (var p in QuietWait.SettingsProblems(stage.LickQuietSeconds, stage.WheelQuietSeconds, stage.WheelQuietCm)) problems.Add("stage '" + stage.Label + "': " + p);
        if (double.IsNaN(task.Optogenetics.LaserToStimulusSeconds) || double.IsInfinity(task.Optogenetics.LaserToStimulusSeconds)) problems.Add("optogenetics.laserToStimulusSeconds must be a finite number");
        if (task.Optogenetics.UseZapit) positive("optogenetics.laserDurationSeconds", task.Optogenetics.LaserDurationSeconds);
        if (structure.StimulusSeconds > structure.ResponseDelaySeconds + structure.ResponseWindowSeconds) problems.Add("trialStructure.stimulusSeconds is longer than responseDelaySeconds plus responseWindowSeconds");
        if (structure.ItiMaxSeconds < structure.ItiMinSeconds) problems.Add("trialStructure.itiMaxSeconds is below itiMinSeconds");
        if (structure.ItiMinSeconds < structure.DrinkSeconds) problems.Add(string.Format("trialStructure.itiMinSeconds ({0} s) is below drinkSeconds ({1} s): the spouts are drawn back drinkSeconds into the interval, before the stimulus", structure.ItiMinSeconds, structure.DrinkSeconds));
        var opto = task.Optogenetics;
        var lead = opto.LaserToStimulusSeconds;
        var leadProblem = ZapitTrial.LeadProblem(lead, structure.ItiMinSeconds);
        if (leadProblem != null) problems.Add(leadProblem);
        if (-lead > structure.ResponseDelaySeconds + structure.ResponseWindowSeconds + 1e-9)
        {
            problems.Add(string.Format("optogenetics.laserToStimulusSeconds ({0} s) puts the trigger after the response window (responseDelaySeconds + responseWindowSeconds, {1} s)", lead, structure.ResponseDelaySeconds + structure.ResponseWindowSeconds));
        }
        var longest = LongestLaserSeconds(task);
        if (opto.UseZapit && opto.LaserDurationSeconds > longest + 1e-9)
        {
            problems.Add(string.Format("optogenetics.laserDurationSeconds ({0} s) must be at most {1} s (responseDelaySeconds + itiMinSeconds - {2} s ramp-down - {3} s reply margin), so Zapit can be armed for the next trial in time", opto.LaserDurationSeconds, longest, ZapitConditions.MaximumRampDownSeconds, ZapitTrial.ReplyMarginSeconds));
        }
        // DeliverReward refuses a reward over MaxRewardUl when it plans it, mid-session (task.py's same rule).
        var multiplier = task.Reward.LargeRewardMultiplier;
        if (task.Reward.LargeRewardProbability > 0 && multiplier > 1 && stage.RewardSizeUl * multiplier > LoadRewardLine.DefaultMaxRewardUl + 1e-9)
        {
            problems.Add(string.Format("stage '{0}' rewardSizeUl ({1} ul) x reward.largeRewardMultiplier ({2}) is {3} ul, more than {4} ul, the most one reward may ask for", stage.Label, stage.RewardSizeUl, multiplier, stage.RewardSizeUl * multiplier, LoadRewardLine.DefaultMaxRewardUl));
        }
        if (problems.Count > 0) throw new InvalidOperationException("The task cannot be run: " + string.Join("; ", problems) + ".");
    }

    /// <summary>The longest laserDurationSeconds (task.py's same rule): from a trigger to the next is at least responseDelaySeconds
    /// + itiMinSeconds (a response at once, no timeout, a quiet mouse), and in that time the laser, the longest ramp-down a
    /// conditions file may have (Zapit refuses the next StartStimulus until it has played) and Zapit's reply margin must fit
    /// before the next trigger.</summary>
    public static double LongestLaserSeconds(UclOpenCoenDomeDecisionMakingTaskParameters task)
    {
        var s = task.TrialStructure;
        return s.ResponseDelaySeconds + s.ItiMinSeconds - ZapitConditions.MaximumRampDownSeconds - ZapitTrial.ReplyMarginSeconds;
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
    public static KeyValuePair<string, Speaker> NearestSpeaker(EqualisedSpeakerArray speakers, double azimuth, double elevation)
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

    /// <summary>The next trial, after the last one's outcome (Correct, Incorrect or NoResponse; null before the first).</summary>
    public Trial Next(string lastOutcome)
    {
        Trial trial;
        if (last != null && lastOutcome != null && ShouldRepeat(lastOutcome))
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
        // Every trial, a repeat too, draws its own interval (Pip, 13 Nov 2026).
        var structure = task.TrialStructure;
        trial.itiSeconds = Uniform(structure.ItiMinSeconds, structure.ItiMaxSeconds);
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
            case RepeatRule.OnNoResponse: return outcome == "NoResponse";
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
            // drawn + 1: this trial's number, which Next sets once it is drawn.
            ZapitTrial.Draw(random, drawn + 1, conditionCount, optogenetics.TrialsBeforeLaser, optogenetics.LaserOnProbability, optogenetics.LaserPowerMw.Value,
                out trial.laserCondition, out trial.laserOn, out trial.laserPowerMw);
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

/// <summary>The response as it stands after a lick: the net licks (left +1, right -1) and where they have moved the grating.</summary>
public class DecisionUpdate
{
    public int DecisionVariable;
    public double GratingAzimuthDeg;
}

/// <summary>A trial's outcome: its row, and what it sets off (the reward, the spouts, the penalty sound, the timeout).</summary>
public class TrialOutcome : ITrialOutcome
{
    public Timestamped<TrialRow> Row;      // the BonsaiData_Trial row, stamped with the outcome's Harp time
    public bool Rewarded;
    public Tuple<string, double> Reward;   // (side, microlitres) for RewardRequests, the side as rig.yml's wiring names it (left, right); null without a reward
    public bool MoveSpouts;
    public LickSpoutCommand SpoutCommand;
    public bool PenaltySound;

    /// <summary>Gets the timeout after the trial, in seconds: the row's penaltyTimeoutSeconds.</summary>
    public double TimeoutSeconds { get; private set; }

    /// <summary>A correct choice or an auto-rewarded miss is rewarded at the target, the other spout drawn back; an
    /// error or a plain miss draws both back, with the row's penalty sound and timeout. A stage without spout
    /// movements leaves the spouts where they are.</summary>
    public static TrialOutcome Of(TrialRow row, Trial trial)
    {
        var o = new TrialOutcome { Row = new Timestamped<TrialRow>(row, row.outcomeTime), PenaltySound = row.penaltySound, TimeoutSeconds = row.penaltyTimeoutSeconds };
        o.Rewarded = row.outcome == "Correct" || row.autoRewarded;
        o.Reward = o.Rewarded ? Tuple.Create(trial.target.ToLowerInvariant(), trial.rewardSizeUl) : null;
        o.MoveSpouts = trial.spoutMovements;
        o.SpoutCommand = !o.Rewarded ? LickSpoutCommand.SpoutsOut
            : trial.target == "Left" ? LickSpoutCommand.LeftIn : LickSpoutCommand.RightIn;
        return o;
    }
}

/// <summary>
/// Decision-making's part of each trial, which lib-open-rigs' TrialLoop runs (the interval, the quiet waits, the lead,
/// the laser, the response window and the timeout are the loop's): the draw (TrialDraw), each trial's timing from the
/// task file, and the response rule. In the response window, responseDelaySeconds after the stimulus for responseWindowSeconds,
/// each lick adds 1 (left) or -1 (right) to the decision variable, which moves the grating degreesPerLick per lick from
/// its drawn azimuth; the choice is Left once it reaches thresholdLicks, Right once it reaches -thresholdLicks, else
/// NoResponse at the window's end. A lick before the window is not counted. The answer is the trial's target: on
/// audiovisualCoherent and audiovisualConflict trials the stimuli's side, the grating's side (TrialDraw).
/// </summary>
public class TrialRules : ITrialRules<Trial, DecisionUpdate, TrialOutcome>
{
    /// <summary>The cue that ends the grating, stimulusSeconds after the stimulus.</summary>
    public const string GratingOffCue = "GratingOff";
    /// <summary>The cue that brings the spouts in, stimulusToSpoutsInSeconds after the stimulus.</summary>
    public const string SpoutsInCue = "SpoutsIn";
    /// <summary>The cue that draws the spouts out, drinkSeconds into the next interval, on a stage with spout movements.</summary>
    public const string DrinkCue = "Drink";

    readonly UclOpenCoenDomeDecisionMakingTaskParameters task;
    readonly TrialDraw draw;
    readonly LaserTiming laser;
    int decision;   // the running trial's net licks in its window: +1 each left, -1 each right

    public TrialRules(UclOpenCoenDomeDecisionMakingTaskParameters task, int stage, EqualisedSpeakerArray speakers, bool hardwareTriggered, Func<string, int> zapitConditionCount)
    {
        this.task = task;
        draw = new TrialDraw(task, speakers, stage, zapitConditionCount);
        var optogenetics = task.Optogenetics;
        // The line held laserDurationSeconds from the trigger; the next trial armed once the longest ramp-down a conditions
        // file may have has played (Zapit refuses a StartStimulus until then).
        laser = optogenetics.UseZapit ? LaserTiming.ForDuration(hardwareTriggered, optogenetics.LaserDurationSeconds, ZapitConditions.MaximumRampDownSeconds) : null;
    }

    /// <summary>The rules for a task file's stage on a rig (its speakers, and Zapit's hardware triggering); throws naming every problem.</summary>
    public static TrialRules Create(UclOpenCoenDomeDecisionMakingTaskParameters task, int stage, LabRig rig, Func<string, int> zapitConditionCount)
    {
        return new TrialRules(task, stage, rig.SpeakerArray, rig.Zapit != null && rig.Zapit.HardwareTriggered, zapitConditionCount);
    }

    public LaserTiming Laser { get { return laser; } }

    public Trial Draw(TrialOutcome previous)
    {
        return draw.Next(previous == null ? null : previous.Row.Value.outcome);
    }

    public TrialTiming Timing(Trial trial)
    {
        var structure = task.TrialStructure;
        var stage = draw.Stage;
        var cues = new List<TrialCue>
        {
            new TrialCue(GratingOffCue, CueAnchor.Stimulus, structure.StimulusSeconds),
            new TrialCue(SpoutsInCue, CueAnchor.Stimulus, structure.StimulusToSpoutsInSeconds),
        };
        if (trial.spoutMovements) cues.Add(new TrialCue(DrinkCue, CueAnchor.NextInterval, structure.DrinkSeconds));
        return new TrialTiming
        {
            IntervalSeconds = trial.itiSeconds,
            LaserToStimulusSeconds = task.Optogenetics.LaserToStimulusSeconds,   // its lead in every experiment, so timing never reveals Zapit
            LickQuietSeconds = stage.LickQuietSeconds,
            WheelQuietSeconds = stage.WheelQuietSeconds,
            WheelQuietCm = stage.WheelQuietCm,
            Laser = trial.laserCondition > 0,                                    // every trial of an optogenetics experiment, laser on or off
            ResponseDelaySeconds = structure.ResponseDelaySeconds,
            ResponseSeconds = structure.ResponseWindowSeconds,
            Cues = cues,
        };
    }

    public void Start(Trial trial, TrialTimes times, double wheelCm)
    {
    }

    public void Stimulus(Trial trial, TrialTimes times, ICollection<DecisionUpdate> updates)
    {
        decision = 0;
        updates.Add(Update(trial));
    }

    public TrialOutcome Tick(Trial trial, TrialTimes times, ICollection<DecisionUpdate> updates)
    {
        return null;
    }

    public TrialOutcome Lick(Trial trial, int spout, TrialTimes times, ICollection<DecisionUpdate> updates)
    {
        if (!times.Responding) return null;
        decision += spout == 0 ? 1 : -1;
        updates.Add(Update(trial));
        var threshold = task.Response.ThresholdLicks;
        if (decision >= threshold) return Outcome(trial, "Left", times);
        if (decision <= -threshold) return Outcome(trial, "Right", times);
        return null;
    }

    public TrialOutcome Wheel(Trial trial, double cm, TrialTimes times, ICollection<DecisionUpdate> updates)
    {
        return null;
    }

    public TrialOutcome ResponseEnd(Trial trial, TrialTimes times)
    {
        return Outcome(trial, "NoResponse", times);
    }

    DecisionUpdate Update(Trial trial)
    {
        return new DecisionUpdate { DecisionVariable = decision, GratingAzimuthDeg = decision * task.Response.DegreesPerLick + trial.visualAzimuthDeg };
    }

    TrialOutcome Outcome(Trial trial, string choice, TrialTimes times)
    {
        var row = TrialRow.Build(choice, trial, times, task.Penalty.TimeoutSeconds, task.Penalty.Sound.Enabled);
        return TrialOutcome.Of(row, trial);
    }
}

[Combinator]
[Description("Runs the trials (lib-open-rigs TrialLoop on TrialRules). Inputs: the rules, the Harp clock, the wheel in cm, the left and right lick states, Zapit's replies.")]
[WorkflowElementCategory(ElementCategory.Combinator)]
public class RunTrials
{
    public IObservable<TrialEvent<Trial, DecisionUpdate, TrialOutcome>> Process(IObservable<TrialRules> rules, IObservable<double> clock,
        IObservable<double> wheelCm, IObservable<bool> lickLeft, IObservable<bool> lickRight, IObservable<ZapitReply> zapitReplies)
    {
        return TrialLoop.Observe(rules, clock, wheelCm, lickLeft, lickRight, zapitReplies);
    }
}

[Combinator]
[Description("Where the spouts move once the stimulus is on: both in on a both-spouts trial, else the target's alone.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class SpoutsIn
{
    public IObservable<LickSpoutCommand> Process(IObservable<Trial> source)
    {
        return source.Select(Command);
    }

    public static LickSpoutCommand Command(Trial trial)
    {
        if (trial.bothSpouts) return LickSpoutCommand.SpoutsIn;
        return trial.target == "Left" ? LickSpoutCommand.LeftIn : LickSpoutCommand.RightIn;
    }
}
