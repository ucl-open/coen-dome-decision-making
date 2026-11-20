using Bonsai;
using OpenCV.Net;
using OpenRigs.Audio;
using OpenRigs.Trials;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reactive.Linq;

/// <summary>
/// The auditory stimuli of an experiment: the task's set of pulses, each one file of one 50 ms pulse of noise, already
/// equalised for every speaker with its filter (OpenRigs.Audio's SpeakerSet, from rig.yml) with room for the filter's
/// ringing either side; a trial's sound is one period, its pulse then silence to 1/stimulus.rateHz seconds (Period), which
/// the mixer loops from the stimulus until it stops it (the GratingOff cue, or the response with stimulus.endsAtResponse).
/// </summary>
public class AuditoryStimulusSet
{
    /// <summary>Number of waveforms in the set, filtered_pink_pulse_0.wav to _99.wav.</summary>
    public const int WaveformCount = 100;

    /// <summary>Each file's length, one pulse, in seconds (task.py PULSE_SECONDS); the grating is on for as long at each pulse.</summary>
    public const double PulseSeconds = 0.05;

    readonly Mat[] raw;
    readonly Dictionary<int, float[][]> equalised;   // each pulse padded by pad zeros either side, then equalised
    readonly int pad;
    readonly double maxLevelDbfs = double.NaN;

    internal AuditoryStimulusSet(Mat[] raw, SpeakerSet speakers)
    {
        this.raw = raw;
        Speakers = speakers;
        LevelDbfs = SetLevelDbfs(raw, speakers.SampleRate);
        pad = speakers.SpeakerChannels.Max(channel => FilterHalfLength(speakers, channel));
        equalised = speakers.SpeakerChannels.ToDictionary(channel => channel, channel => raw.Select(pulse =>
        {
            using (var padded = Mat.Zeros(1, pulse.Cols + 2 * pad, Depth.F32, 1))
            {
                using (var middle = padded.GetSubRect(new Rect(pad, 0, pulse.Cols, 1))) CV.Convert(pulse, middle);
                using (var filtered = speakers.Equalise(padded, channel)) return Samples(filtered);
            }
        }).ToArray());
    }

    AuditoryStimulusSet(AuditoryStimulusSet set, double maxLevelDbfs)
    {
        raw = set.raw;
        equalised = set.equalised;
        pad = set.pad;
        Speakers = set.Speakers;
        LevelDbfs = set.LevelDbfs;
        this.maxLevelDbfs = maxLevelDbfs;
    }

    /// <summary>How far a speaker's filter spreads a sample either way: its response to one sample, measured.</summary>
    static int FilterHalfLength(SpeakerSet speakers, int channel)
    {
        const int probe = 1 << 16;
        using (var impulse = Mat.Zeros(1, probe, Depth.F32, 1))
        {
            impulse.SetReal(0, probe / 2, 1);
            using (var response = speakers.Equalise(impulse, channel))
            {
                var r = Samples(response);
                int first = Array.FindIndex(r, v => v != 0), last = Array.FindLastIndex(r, v => v != 0);
                return first < 0 ? 0 : Math.Max(probe / 2 - first, last - probe / 2);
            }
        }
    }

    static float[] Samples(Mat row)
    {
        var samples = new float[row.Cols];
        using (var copy = new Mat(1, row.Cols, Depth.F32, 1))
        {
            CV.Convert(row, copy);
            System.Runtime.InteropServices.Marshal.Copy(copy.Data, samples, 0, samples.Length);
        }
        return samples;
    }

    /// <summary>A period's length in samples at the speakers' rate, 1/rateHz seconds rounded to the nearest sample.</summary>
    public int PeriodSamples(double rateHz)
    {
        return (int)Math.Round(Speakers.SampleRate / rateHz);
    }

    /// <summary>
    /// One period of a trial's sound on a channel before its level, which the mixer loops (OpenRigs.Audio SpeakerMixer's
    /// LoopStimulusSounds) until the stimulus ends: its pulse, equalised for that speaker, at the period's start, then silence
    /// to 1/rateHz seconds, the filter's ringing either side wrapped round the period's ends, so the loop is the filtered
    /// train with no seam.
    /// </summary>
    public Mat Period(int index, int channel, double rateHz)
    {
        var pulse = Pulse(index, channel);
        int samples = PeriodSamples(rateHz);
        var period = new float[samples];
        for (int j = 0; j < pulse.Length; j++) period[((j - pad) % samples + samples) % samples] += pulse[j];
        var mat = new Mat(1, samples, Depth.F32, 1);
        System.Runtime.InteropServices.Marshal.Copy(period, 0, mat.Data, samples);
        return mat;
    }

    /// <summary>The set's level as stored, in dBFS RMS (OpenRigs.Audio SoundLevel's rule, silent stretches left out): the
    /// power mean of its waveforms' levels, which agree within 0.01 dB (-43.22). A trial's auditoryStimulus.initialLevelDbfs is
    /// one gain over the whole set: every waveform keeps its place relative to the others.</summary>
    public double LevelDbfs { get; private set; }

    /// <summary>The power mean of the waveforms' levels (each SoundLevel.RmsDbfs), in dBFS RMS.</summary>
    public static double SetLevelDbfs(IList<Mat> waveforms, int sampleRate)
    {
        var power = waveforms.Average(w => Math.Pow(10, SoundLevel.RmsDbfs(w, sampleRate) / 10));
        return 10 * Math.Log10(power);
    }

    /// <summary>The gain on the stored samples that puts the set at levelDbfs; 0 at NaN (mute).</summary>
    public double Gain(double levelDbfs)
    {
        return double.IsNaN(levelDbfs) ? 0 : Math.Pow(10, (levelDbfs - LevelDbfs) / 20);
    }

    /// <summary>
    /// The set checked for the highest sound level the task's conditions can draw (auditoryStimulus.initialLevelDbfs, one gain for the
    /// set): every pulse's period at each rate the conditions can draw (stimulus.rateHz) at that level checked against the
    /// lab's -1 dBFS peak, the neighbouring pulses' filter ringing wrapped in, so a sound too loud stops the workflow at Go,
    /// naming the condition, the waveform, the rate and the speaker, not at the trial that plays it. A trial may then play
    /// at that level or less (Buffer), on one speaker or all ("all": each channel its own speaker's copy, so this check
    /// holds for it too).
    /// </summary>
    public AuditoryStimulusSet AtMaxLevel(MaxSoundLevel maxLevel, IEnumerable<double> ratesHz)
    {
        var gain = Gain(maxLevel.LevelDbfs);
        foreach (var speaker in Speakers.Speakers)
        {
            foreach (var rate in ratesHz)
            {
                for (int i = 0; i < raw.Length && gain > 0; i++)
                {
                    using (var train = Period(i, speaker.Channel, rate))
                    using (var played = new Mat(1, train.Cols, Depth.F32, 1))
                    {
                        CV.ConvertScale(train, played, gain, 0);
                        SoundLevel.CheckPeak(played, "Auditory stimulus " + i + " at " + rate.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) +
                            " Hz on speaker '" + speaker.Name + "' at " + maxLevel.LevelDbfs.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) +
                            " dBFS (auditoryStimulus.initialLevelDbfs, " + maxLevel.Where + ")");
                    }
                }
            }
        }
        return new AuditoryStimulusSet(this, maxLevel.LevelDbfs);
    }

    /// <summary>The speakers the set is equalised for.</summary>
    public SpeakerSet Speakers { get; private set; }

    /// <summary>The samples either side of a pulse, as Pulse returns it, that hold its speaker filter's ringing (the longest filter's).</summary>
    public int FilterPad { get { return pad; } }

    /// <summary>A pulse equalised for the speaker on a channel, with the filter's ringing either side (FilterPad samples each).</summary>
    public float[] Pulse(int index, int channel)
    {
        if (index < 0 || index >= raw.Length)
        {
            throw new InvalidOperationException(
                "Trial asks for auditory waveform " + index + ", but the set has " + raw.Length + " (0 to " + (raw.Length - 1) + ").");
        }
        float[][] pulses;
        if (!equalised.TryGetValue(channel, out pulses))
        {
            throw new InvalidOperationException("Trial asks for mixer channel " + channel + ", which is no rig.yml speaker's.");
        }
        return pulses[index];
    }

    /// <summary>The mixer's looped buffer for a trial: one period (Period) equalised for the speaker on its channel
    /// (SoundAzimuths.AllSpeakersChannel: on every speaker's channel, each its own equalised copy), at its level (the set's one
    /// gain), with its sync copy, so the sync channel is high while the loop plays. Each channel's row is one speaker's period,
    /// so AtMaxLevel's check of every speaker covers a sound on all of them.</summary>
    public Mat Buffer(int index, int channel, double levelDbfs, double rateHz)
    {
        if (!(levelDbfs <= maxLevelDbfs)) throw new InvalidOperationException("Trial asks for auditory level " + levelDbfs + " dBFS, not at or below the " + maxLevelDbfs + " the set was checked for at Go (LevelAuditoryStimulusSet).");
        var gain = Gain(levelDbfs);
        var channels = channel == SoundAzimuths.AllSpeakersChannel ? Speakers.SpeakerChannels : new[] { channel };
        Mat buffer;
        using (var train = Period(index, channels[0], rateHz)) buffer = Speakers.Layout(train, new[] { channels[0] }, gain, false, true);
        foreach (var other in channels.Skip(1))
        {
            using (var train = Period(index, other, rateHz))
            using (var row = buffer.GetRow(other)) CV.ConvertScale(train, row, gain, 0);
        }
        return buffer;
    }

    /// <summary>
    /// Reads the set from stimulusDirectory (a full path) for the speakers, failing with a message naming every file
    /// that is missing or unusable; public so a check can run it without a workflow.
    /// </summary>
    public static AuditoryStimulusSet Load(SpeakerSet speakers, string stimulusDirectory, string filePrefix)
    {
        var problems = new List<string>();
        if (!speakers.SyncChannel.HasValue) problems.Add("rig.yml's speakerArray has no syncChannel, the channel each stimulus's sync copy plays on");

        if (!Path.IsPathRooted(stimulusDirectory)) throw new ArgumentException("The auditory stimuli's folder must be a full path: " + stimulusDirectory);
        string directory = Path.GetFullPath(stimulusDirectory);
        var files = Enumerable.Range(0, WaveformCount).Select(i => Path.Combine(directory, filePrefix + i + ".wav")).ToArray();
        var missing = files.Where(file => !File.Exists(file)).ToArray();
        if (missing.Length > 0)
        {
            problems.Add(missing.Length + " of the " + WaveformCount + " auditory stimuli are missing from " + directory + ", among them " +
                string.Join(", ", missing.Take(3).Select(Path.GetFileName)) + ". The set is " + filePrefix + "0.wav to " + filePrefix + (WaveformCount - 1) + ".wav");
        }

        var raw = new Mat[WaveformCount];
        if (missing.Length == 0)
        {
            for (int i = 0; i < WaveformCount; i++)
            {
                try
                {
                    raw[i] = Wave.Read(files[i], speakers.SampleRate);
                    int expected = (int)Math.Round(PulseSeconds * speakers.SampleRate);
                    if (raw[i].Cols != expected) problems.Add(Path.GetFileName(files[i]) + " is " + raw[i].Cols + " samples, not one " + PulseSeconds * 1000 + " ms pulse (" + expected + ")");
                }
                catch (InvalidDataException ex)
                {
                    problems.Add(Path.GetFileName(files[i]) + " " + ex.Message);
                }
            }
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException("The auditory stimuli cannot be loaded: " + string.Join("; ", problems) + ".");
        }
        return new AuditoryStimulusSet(raw, speakers);
    }
}

[Combinator]
[Description("Loads the auditory stimulus set at Start: the 100 waveforms in <Folder>, the task's own and the same on every rig (src\\sounds), each equalised for every rig.yml speaker (OpenRigs.Audio LoadSpeakers, which checks the filters). Stops the workflow, naming each file, if rig.yml has no syncChannel or any waveform is missing or not mono 16-bit PCM at the audio device's rate. Input: the speakers.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class LoadAuditoryStimulusSet
{
    public LoadAuditoryStimulusSet()
    {
        Folder = "sounds";
        FilePrefix = "filtered_pink_pulse_";
    }

    [Description("The folder of the stimulus waveforms, relative to the workflow's folder (src).")]
    public string Folder { get; set; }

    [Description("The waveforms' file name before their index: <FilePrefix>0.wav and on.")]
    public string FilePrefix { get; set; }

    public IObservable<AuditoryStimulusSet> Process(IObservable<SpeakerSet> source)
    {
        return source.Select(speakers => AuditoryStimulusSet.Load(speakers, Path.GetFullPath(Folder), FilePrefix));
    }
}

[Combinator]
[Description("The auditory stimulus set checked for the highest sound level the task's conditions can draw (TrialRules.MaxStimulusLevel: the highest auditoryStimulus.initialLevelDbfs, the set measured as one), every pulse's period at each stimulus.rateHz they can draw (TrialRules.StimulusRatesHz): stops the workflow, naming the condition, waveform, rate and speaker, if any would peak at -1 dBFS or more after equalisation. Input is the stimulus set and the rules.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class LevelAuditoryStimulusSet
{
    public IObservable<AuditoryStimulusSet> Process(IObservable<Tuple<AuditoryStimulusSet, TrialRules>> source)
    {
        return source.Select(input => input.Item1.AtMaxLevel(input.Item2.MaxStimulusLevel, input.Item2.StimulusRatesHz));
    }
}

[Combinator]
[Description("The mixer's looped buffer for a trial's sound: the stimulus set's pulse at the trial's auditorySoundFileIndex, equalised for its speaker, then silence to 1/stimulus.rateHz seconds (AuditoryStimulusSet.Period), on that speaker's channel (a trial at \"all\": every speaker's, each its own copy), at its auditoryInitialLevelDbfs (one gain for the set), with its sync copy on rig.yml's syncChannel at syncGain (OpenRigs.Audio SpeakerSet.Layout). Input is the trial and the set at its level (LevelAuditoryStimulusSet), as WithLatestFrom pairs them.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class SelectAuditoryStimulus
{
    public IObservable<Mat> Process(IObservable<Tuple<Trial, AuditoryStimulusSet>> source)
    {
        return source.Select(input =>
        {
            var trial = input.Item1;
            return input.Item2.Buffer(trial.auditorySoundFileIndex, trial.speakerChannel, trial.auditoryInitialLevelDbfs, trial.Stimulus.RateHz);
        });
    }
}
