using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Audio;
using OpenRigs.Trials;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

// The speakers' filters are loaded and checked by OpenRigs.Audio (its own tests); these cover the stimulus set.
[TestClass]
public class AuditoryStimulusSetTests
{
    static readonly double[] Symmetric = { 0.01, -0.05, 0.2, 1.1, 0.2, -0.05, 0.01 };
    string rig;
    short[][] waves;

    [TestInitialize]
    public void Write()
    {
        rig = Path.Combine(Path.GetTempPath(), "dome-decision-making-audio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(rig, "sounds"));
        Directory.CreateDirectory(Path.Combine(rig, "calibration"));
        var random = new Random(1);
        waves = new short[AuditoryStimulusSet.WaveformCount][];
        for (int i = 0; i < waves.Length; i++)
        {
            waves[i] = Enumerable.Range(0, 480).Select(_ => (short)random.Next(short.MinValue, short.MaxValue + 1)).ToArray();
            Wave(i, waves[i], 48000);
        }
        File.WriteAllText(Path.Combine(rig, "calibration", "sym.json"),
            "{\"calibratedAt\":\"2026-11-04T00:00:00Z\",\"calibratedBy\":\"test\",\"machineName\":\"THIS-RIG\",\"notes\":\"\",\"sampleRate\":48000,\"taps\":[" +
            string.Join(",", Symmetric.Select(t => t.ToString("R", CultureInfo.InvariantCulture))) + "]}");
    }

    [TestCleanup]
    public void Remove()
    {
        Directory.Delete(rig, true);
    }

    void Wave(int index, short[] samples, int rate)
    {
        using (var w = new BinaryWriter(File.Create(Path.Combine(rig, "sounds", "filtered_pink_pulse_train_" + index + ".wav"))))
        {
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + 2 * samples.Length); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(2 * samples.Length);
            foreach (var s in samples) w.Write(s);
        }
    }

    SpeakerSet Speakers(bool sync = true)
    {
        var speakers = "";
        foreach (var s in new[] { Tuple.Create("left", -90, 0), Tuple.Create("right", 90, 1), Tuple.Create("centre", 0, 2) })
        {
            speakers += "    " + s.Item1 + ":\n      position: {azimuth: " + s.Item2 + ", elevation: 15.0, radius: 1.0}\n      channel: " + s.Item3 +
                "\n      filter: calibration/sym.json\n";
        }
        var path = Path.Combine(rig, "rig.yml");
        File.WriteAllText(path, "speakerArray:\n  device:\n    sampleRate: 48000\n    channelCount: 32\n  speakers:\n" + speakers +
            (sync ? "  syncChannel: 31\n" : "") + "  penaltySoundChannel: 2\n  backgroundNoise: ../shared/sounds/background_noise.wav\n  penaltySound: ../shared/sounds/penalty_tone.wav\n");
        return SpeakerSet.Load(path, "THIS-RIG");
    }

    AuditoryStimulusSet Load(SpeakerSet speakers)
    {
        return AuditoryStimulusSet.Load(speakers, Path.Combine(rig, "sounds"), "filtered_pink_pulse_train_");
    }

    [TestMethod]
    public void A_relative_stimulus_folder_is_refused()
    {
        try
        {
            AuditoryStimulusSet.Load(Speakers(), "sounds", "filtered_pink_pulse_train_");
            Assert.Fail("a relative folder was taken");
        }
        catch (ArgumentException ex)
        {
            StringAssert.Contains(ex.Message, "full path");
        }
    }

    [TestMethod]
    public void The_workflow_reads_the_stimuli_from_its_own_sounds_folder()
    {
        Assert.AreEqual("sounds", new LoadAuditoryStimulusSet().Folder);
    }

    static float[] Samples(OpenCV.Net.Mat m)
    {
        var values = new float[m.Cols];
        Marshal.Copy(m.Data, values, 0, m.Cols);
        return values;
    }

    [TestMethod]
    public void EqualisingIsTheFiltersCentredConvolution()
    {
        var set = Load(Speakers());
        var x = waves[7].Select(s => s / 32768.0).ToArray();
        var got = Samples(set.Stimulus(7, 2));
        for (int n = 0; n < x.Length; n++)
        {
            double want = 0;
            for (int j = 0; j < Symmetric.Length; j++)
            {
                int k = n + j - 3;
                if (k >= 0 && k < x.Length) want += Symmetric[j] * x[k];
            }
            Assert.AreEqual(want, got[n], 1e-5);
        }
    }

    [TestMethod]
    public void A_channel_with_no_speaker_is_refused()
    {
        StringAssert.Contains(Fixture.Refusal(() => Load(Speakers()).Stimulus(7, 99)), "mixer channel 99");
    }

    [TestMethod]
    public void EveryUnusableStimulusIsRefusedByName()
    {
        Func<Action, string> refusal = Fixture.Refusal;
        StringAssert.Contains(refusal(() => Load(Speakers(false))), "no syncChannel");
        Wave(5, waves[5], 44100);
        StringAssert.Contains(refusal(() => Load(Speakers())), "filtered_pink_pulse_train_5.wav");
        File.Delete(Path.Combine(rig, "sounds", "filtered_pink_pulse_train_42.wav"));
        StringAssert.Contains(refusal(() => Load(Speakers())), "filtered_pink_pulse_train_42.wav");
    }

    [TestMethod]
    public void TheBackgroundNoiseAndThePenaltySoundAreTheLabs()
    {
        // One background noise and one penalty sound for every dome task, in RigConfigs\shared\sounds\ beside the rig's
        // folder, named by rig.yml's speakerArray.backgroundNoise and speakerArray.penaltySound; the task keeps neither.
        var shared = Path.GetFullPath(Path.Combine(rig, "..", "shared", "sounds"));
        Assert.AreEqual(Path.Combine(shared, "background_noise.wav"), Speakers().BackgroundNoiseFile);
        Assert.AreEqual(Path.Combine(shared, "penalty_tone.wav"), Speakers().PenaltySoundFile);
    }

    [TestMethod]
    public void TheSetsOneGainPutsItsFilesAtLevelDbfs()
    {
        // The set's level as stored is the power mean of its waveforms' levels (SoundLevel's rule); a trial's levelDbfs is one
        // gain on every stored sample, so the set as played sits at it. The repo's own 100 files measure -43.22 dBFS, their
        // silent gaps left out (OpenRigs.Audio Wave.Read reads a 16-bit 0 as exactly 0), so levelDbfs -40 is 3.22 dB louder.
        var set = Load(Speakers());
        var level = set.LevelDbfs;
        var gain = set.Gain(-40);
        Assert.AreEqual(Math.Pow(10, (-40 - level) / 20), gain, 1e-12);
        var raw = Enumerable.Range(0, AuditoryStimulusSet.WaveformCount).Select(i => OpenRigs.Audio.Wave.Read(Path.Combine(rig, "sounds", "filtered_pink_pulse_train_" + i + ".wav"), 48000)).ToList();
        var scaled = raw.Select(w => { var m = new OpenCV.Net.Mat(1, w.Cols, OpenCV.Net.Depth.F32, 1); OpenCV.Net.CV.ConvertScale(w, m, gain, 0); return m; }).ToList();
        Assert.AreEqual(-40, AuditoryStimulusSet.SetLevelDbfs(scaled, 48000), 1e-6, "the set as played is at its levelDbfs");
        Assert.AreEqual(0, set.Gain(double.NaN), "mute");
        var repo = Path.Combine(Fixture.Repo(), "src", "sounds");
        var files = Enumerable.Range(0, AuditoryStimulusSet.WaveformCount).Select(i => OpenRigs.Audio.Wave.Read(Path.Combine(repo, "filtered_pink_pulse_train_" + i + ".wav"), 48000)).ToList();
        Assert.AreEqual(-43.22, AuditoryStimulusSet.SetLevelDbfs(files, 48000), 0.005, "the repo's set as stored, silent gaps left out");
    }

    [TestMethod]
    public void EachStimulusPlaysAtItsLevelUpToTheMaxLevelCheckedAtGo()
    {
        // A trial's level scales the equalised waveform by the set's one gain, up to the highest level the conditions can draw, whose
        // every equalised copy is checked under the -1 dBFS peak at Go, naming the condition and the speaker.
        var set = Load(Speakers());
        Func<Action, string> refusal = Fixture.Refusal;
        StringAssert.Contains(refusal(() => set.Buffer(7, 2, -40)), "not at or below the NaN the set was checked for at Go");
        var loud = set.LevelDbfs - 20;
        var checkedSet = set.AtMaxLevel(new MaxSoundLevel { LevelDbfs = loud, Where = "trialConditions[1] (auditory)" });
        StringAssert.Contains(refusal(() => checkedSet.Buffer(7, 2, loud + 1)), "the " + loud + " the set was checked for at Go");
        var equalised = Samples(set.Stimulus(7, 2));
        foreach (var level in new[] { loud, loud - 6 })
        {
            var buffer = checkedSet.Buffer(7, 2, level);
            using (var row = buffer.GetRow(2))
            using (var copy = new OpenCV.Net.Mat(1, buffer.Cols, OpenCV.Net.Depth.F32, 1))
            {
                OpenCV.Net.CV.Copy(row, copy);
                var played = Samples(copy);
                for (int n = 0; n < played.Length; n += 37) Assert.AreEqual(equalised[n] * set.Gain(level), played[n], 1e-6);
            }
        }
        // At "all" every speaker's channel plays its own equalised copy at the level: the per-speaker check above covers it.
        var everywhere = checkedSet.Buffer(7, SoundAzimuths.AllSpeakersChannel, loud);
        foreach (var channel in set.Speakers.SpeakerChannels)
        {
            var own = Samples(set.Stimulus(7, channel));
            using (var row = everywhere.GetRow(channel))
            using (var copy = new OpenCV.Net.Mat(1, everywhere.Cols, OpenCV.Net.Depth.F32, 1))
            {
                OpenCV.Net.CV.Copy(row, copy);
                var played = Samples(copy);
                for (int n = 0; n < played.Length; n += 37) Assert.AreEqual(own[n] * set.Gain(loud), played[n], 1e-6);
            }
        }
        StringAssert.Contains(refusal(() => set.AtMaxLevel(new MaxSoundLevel { LevelDbfs = -2, Where = "trialConditions[2] (visual) with auditoryStimulus.levelDbfs -2" })),
            "on speaker 'left' at -2 dBFS (auditoryStimulus.levelDbfs, trialConditions[2] (visual) with auditoryStimulus.levelDbfs -2) peaks at");
    }
}
