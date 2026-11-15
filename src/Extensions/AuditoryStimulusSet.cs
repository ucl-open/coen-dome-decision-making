using Bonsai;
using OpenCV.Net;
using OpenRigs.Audio;
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

    internal AuditoryStimulusSet(Mat[] raw, SpeakerSet speakers)
    {
        this.raw = raw;
        Speakers = speakers;
        equalised = speakers.SpeakerChannels.ToDictionary(channel => channel, channel => raw.Select(waveform => speakers.Equalise(waveform, channel)).ToArray());
    }

    /// <summary>The speakers the set is equalised for.</summary>
    public SpeakerSet Speakers { get; private set; }

    /// <summary>The mixer channel of every speaker in rig.yml, in rig.yml's order.</summary>
    public int[] SpeakerChannels
    {
        get { return Speakers.SpeakerChannels; }
    }

    /// <summary>The waveform a trial plays on a channel, equalised for that speaker.</summary>
    public Mat Stimulus(int index, int channel)
    {
        if (index < 0 || index >= raw.Length)
        {
            throw new InvalidOperationException(
                "Trial asks for auditory waveform " + index + ", but the set has " + raw.Length + " (0 to " + (raw.Length - 1) + ").");
        }
        Mat[] waveforms;
        return equalised.TryGetValue(channel, out waveforms) ? waveforms[index] : raw[index];
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
[Description("Loads the auditory stimulus set at Start: the 100 waveforms in <Folder>, the task's own and the same on every rig (src\\LocalFiles\\AuditoryStimuli), each equalised for every rig.yml speaker (OpenRigs.Audio LoadSpeakers, which checks the filters). Stops the workflow, naming each file, if rig.yml has no syncChannel or any waveform is missing or not mono 16-bit PCM at the audio device's rate. Input: the speakers.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class LoadAuditoryStimulusSet
{
    public LoadAuditoryStimulusSet()
    {
        Folder = "LocalFiles\\AuditoryStimuli";
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
[Description("The mixer's buffer for a trial's sound: the stimulus set's waveform at the trial's audioWaveformIndex, equalised for its speaker, on that speaker's channel, with its sync copy on rig.yml's syncChannel at syncGain (OpenRigs.Audio SpeakerSet.Buffer). Input is the stimulus set and the trial.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class SelectAuditoryStimulus
{
    public IObservable<Mat> Process(IObservable<Tuple<AuditoryStimulusSet, Trial>> source)
    {
        return source.Select(input => input.Item1.Speakers.Buffer(
            input.Item1.Stimulus(input.Item2.audioWaveformIndex, input.Item2.speakerChannel), new[] { input.Item2.speakerChannel }, 1, false, true));
    }
}
