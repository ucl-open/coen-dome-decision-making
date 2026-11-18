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
    readonly double largest;

    internal AuditoryStimulusSet(Mat[] raw, SpeakerSet speakers)
    {
        this.raw = raw;
        Speakers = speakers;
        equalised = speakers.SpeakerChannels.ToDictionary(channel => channel, channel => raw.Select(waveform => speakers.Equalise(waveform, channel)).ToArray());
    }

    AuditoryStimulusSet(AuditoryStimulusSet set, double largest)
    {
        raw = set.raw;
        equalised = set.equalised;
        Speakers = set.Speakers;
        this.largest = largest;
    }

    /// <summary>
    /// The set checked for the loudest sound the task's conditions can draw (auditoryStimulus.amplitude, 1 the waveform as
    /// stored): every equalised copy at that amplitude checked against the lab's -1 dBFS peak, so a sound too loud stops the
    /// workflow at Go, naming the condition, the waveform and the speaker, not at the trial that plays it. A trial may then
    /// play at that amplitude or less (Buffer).
    /// </summary>
    public AuditoryStimulusSet AtLoudest(LoudestSound loudest)
    {
        foreach (var speaker in Speakers.Speakers)
        {
            for (int i = 0; i < raw.Length && loudest.Amplitude > 0; i++)
            {
                using (var played = new Mat(1, raw[i].Cols, Depth.F32, 1))
                {
                    CV.ConvertScale(equalised[speaker.Channel][i], played, loudest.Amplitude, 0);
                    SoundLevel.CheckPeak(played, "Auditory stimulus " + i + " on speaker '" + speaker.Name + "' at amplitude " +
                        loudest.Amplitude.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " (auditoryStimulus.amplitude, " + loudest.Where + ")");
                }
            }
        }
        return new AuditoryStimulusSet(this, loudest.Amplitude);
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

    /// <summary>The mixer's buffer for a trial: its waveform equalised for the speaker on its channel, scaled by its
    /// amplitude, with its sync copy.</summary>
    public Mat Buffer(int index, int channel, double amplitude)
    {
        if (!(amplitude <= largest)) throw new InvalidOperationException("Trial asks for auditory amplitude " + amplitude + ", above the " + largest + " the set was checked for at Go (LevelAuditoryStimulusSet).");
        return Speakers.Layout(Stimulus(index, channel), new[] { channel }, amplitude, false, true);
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
[Description("The auditory stimulus set checked for the loudest sound the task's conditions can draw (TrialRules.Loudest: the largest auditoryStimulus.amplitude, 1 the waveform as stored): stops the workflow, naming the condition, waveform and speaker, if any would peak at -1 dBFS or more after equalisation. Input is the stimulus set and the loudest sound.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class LevelAuditoryStimulusSet
{
    public IObservable<AuditoryStimulusSet> Process(IObservable<Tuple<AuditoryStimulusSet, LoudestSound>> source)
    {
        return source.Select(input => input.Item1.AtLoudest(input.Item2));
    }
}

[Combinator]
[Description("The mixer's buffer for a trial's sound: the stimulus set's waveform at the trial's auditorySoundFileIndex, equalised for its speaker, on that speaker's channel, scaled by its auditoryAmplitude, with its sync copy on rig.yml's syncChannel at syncGain (OpenRigs.Audio SpeakerSet.Layout). Input is the set at its level (LevelAuditoryStimulusSet) and the trial.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class SelectAuditoryStimulus
{
    public IObservable<Mat> Process(IObservable<Tuple<AuditoryStimulusSet, Trial>> source)
    {
        return source.Select(input => input.Item1.Buffer(input.Item2.auditorySoundFileIndex, input.Item2.speakerChannel, input.Item2.auditoryAmplitude));
    }
}
