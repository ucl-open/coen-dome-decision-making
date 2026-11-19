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
/// The auditory stimuli of an experiment: the task's set of stimulus waveforms, each already equalised for every
/// speaker with its filter (OpenRigs.Audio's SpeakerSet, from rig.yml), so a trial's stimulus is a lookup with no
/// filtering at stimulus onset.
/// </summary>
public class AuditoryStimulusSet
{
    /// <summary>Number of waveforms in the set, filtered_pink_pulse_train_0.wav to _99.wav.</summary>
    public const int WaveformCount = 100;

    /// <summary>Pulse rate the set was generated at, in Hz; the grating flickers at it too.</summary>
    public const double PulseRateHz = 8;

    /// <summary>Half the pulse period: the grating's flicker is on for one, off for the next.</summary>
    public TimeSpan FlickerHalfPeriod
    {
        get { return TimeSpan.FromSeconds(0.5 / PulseRateHz); }
    }

    readonly Mat[] raw;
    readonly Dictionary<int, Mat[]> equalised;
    readonly double loudestDbfs = double.NaN;

    internal AuditoryStimulusSet(Mat[] raw, SpeakerSet speakers)
    {
        this.raw = raw;
        Speakers = speakers;
        LevelDbfs = SetLevelDbfs(raw, speakers.SampleRate);
        equalised = speakers.SpeakerChannels.ToDictionary(channel => channel, channel => raw.Select(waveform => speakers.Equalise(waveform, channel)).ToArray());
    }

    AuditoryStimulusSet(AuditoryStimulusSet set, double loudestDbfs)
    {
        raw = set.raw;
        equalised = set.equalised;
        Speakers = set.Speakers;
        LevelDbfs = set.LevelDbfs;
        this.loudestDbfs = loudestDbfs;
    }

    /// <summary>The set's level as stored, in dBFS RMS (OpenRigs.Audio SoundLevel's rule, silent stretches left out): the
    /// power mean of its waveforms' levels, which agree within 0.01 dB (-43.22). A trial's auditoryStimulus.levelDbfs is
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
    /// The set checked for the loudest sound the task's conditions can draw (auditoryStimulus.levelDbfs, one gain for the
    /// set): every equalised copy at that level checked against the lab's -1 dBFS peak, so a sound too loud stops the
    /// workflow at Go, naming the condition, the waveform and the speaker, not at the trial that plays it. A trial may then
    /// play at that level or less (Buffer), on one speaker or all ("all": each channel its own speaker's copy, so this
    /// check holds for it too).
    /// </summary>
    public AuditoryStimulusSet AtLoudest(LoudestSound loudest)
    {
        var gain = Gain(loudest.LevelDbfs);
        foreach (var speaker in Speakers.Speakers)
        {
            for (int i = 0; i < raw.Length && gain > 0; i++)
            {
                using (var played = new Mat(1, raw[i].Cols, Depth.F32, 1))
                {
                    CV.ConvertScale(equalised[speaker.Channel][i], played, gain, 0);
                    SoundLevel.CheckPeak(played, "Auditory stimulus " + i + " on speaker '" + speaker.Name + "' at " +
                        loudest.LevelDbfs.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " dBFS (auditoryStimulus.levelDbfs, " + loudest.Where + ")");
                }
            }
        }
        return new AuditoryStimulusSet(this, loudest.LevelDbfs);
    }

    /// <summary>The speakers the set is equalised for.</summary>
    public SpeakerSet Speakers { get; private set; }

    /// <summary>The waveform a trial plays on a channel, equalised for that speaker, before its level.</summary>
    public Mat Stimulus(int index, int channel)
    {
        if (index < 0 || index >= raw.Length)
        {
            throw new InvalidOperationException(
                "Trial asks for auditory waveform " + index + ", but the set has " + raw.Length + " (0 to " + (raw.Length - 1) + ").");
        }
        Mat[] waveforms;
        if (!equalised.TryGetValue(channel, out waveforms))
        {
            throw new InvalidOperationException("Trial asks for mixer channel " + channel + ", which is no rig.yml speaker's.");
        }
        return waveforms[index];
    }

    /// <summary>The mixer's buffer for a trial: its waveform equalised for the speaker on its channel (SoundAzimuths.AllSpeakersChannel:
    /// on every speaker's channel, each its own equalised copy), at its level (the set's one gain), with its sync copy. Each channel's
    /// row is one speaker's equalised copy, so AtLoudest's check of every speaker covers a sound on all of them.</summary>
    public Mat Buffer(int index, int channel, double levelDbfs)
    {
        if (!(levelDbfs <= loudestDbfs)) throw new InvalidOperationException("Trial asks for auditory level " + levelDbfs + " dBFS, not at or below the " + loudestDbfs + " the set was checked for at Go (LevelAuditoryStimulusSet).");
        var amplitude = Gain(levelDbfs);
        var channels = channel == SoundAzimuths.AllSpeakersChannel ? Speakers.SpeakerChannels : new[] { channel };
        var buffer = Speakers.Layout(Stimulus(index, channels[0]), new[] { channels[0] }, amplitude, false, true);
        foreach (var other in channels.Skip(1))
        {
            using (var row = buffer.GetRow(other)) CV.ConvertScale(Stimulus(index, other), row, amplitude, 0);
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
        FilePrefix = "filtered_pink_pulse_train_";
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
[Description("The auditory stimulus set checked for the loudest sound the task's conditions can draw (TrialRules.Loudest: the highest auditoryStimulus.levelDbfs, the set measured as one): stops the workflow, naming the condition, waveform and speaker, if any would peak at -1 dBFS or more after equalisation. Input is the stimulus set and the loudest sound.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class LevelAuditoryStimulusSet
{
    public IObservable<AuditoryStimulusSet> Process(IObservable<Tuple<AuditoryStimulusSet, LoudestSound>> source)
    {
        return source.Select(input => input.Item1.AtLoudest(input.Item2));
    }
}

[Combinator]
[Description("The mixer's buffer for a trial's sound: the stimulus set's waveform at the trial's auditorySoundFileIndex, equalised for its speaker, on that speaker's channel (a trial at \"all\": every speaker's, each its own copy), at its auditoryLevelDbfs (one gain for the set), with its sync copy on rig.yml's syncChannel at syncGain (OpenRigs.Audio SpeakerSet.Layout). Input is the trial and the set at its level (LevelAuditoryStimulusSet), as WithLatestFrom pairs them.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class SelectAuditoryStimulus
{
    public IObservable<Mat> Process(IObservable<Tuple<Trial, AuditoryStimulusSet>> source)
    {
        return source.Select(input => input.Item2.Buffer(input.Item1.auditorySoundFileIndex, input.Item1.speakerChannel, input.Item1.auditoryLevelDbfs));
    }
}
