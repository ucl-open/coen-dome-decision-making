using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Audio;
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
        Directory.CreateDirectory(Path.Combine(rig, "calibration", "AuditoryStimuli"));
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
        using (var w = new BinaryWriter(File.Create(Path.Combine(rig, "calibration", "AuditoryStimuli", "filtered_pink_pulse_train_" + index + ".wav"))))
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
            (sync ? "  syncChannel: 31\n" : "") + "  penaltySoundChannel: 2\n  backgroundNoise: ../shared/sounds/background_noise.wav\n");
        return SpeakerSet.Load(path, "THIS-RIG");
    }

    AuditoryStimulusSet Load(SpeakerSet speakers)
    {
        return AuditoryStimulusSet.Load(speakers, Path.Combine(rig, "calibration", "AuditoryStimuli"), "filtered_pink_pulse_train_");
    }

    [TestMethod]
    public void A_relative_stimulus_folder_is_refused()
    {
        try
        {
            AuditoryStimulusSet.Load(Speakers(), "calibration\\AuditoryStimuli", "filtered_pink_pulse_train_");
            Assert.Fail("a relative folder was taken");
        }
        catch (ArgumentException ex)
        {
            StringAssert.Contains(ex.Message, "full path");
        }
    }

    [TestMethod]
    public void The_workflow_reads_the_stimuli_from_its_own_LocalFiles()
    {
        Assert.AreEqual("LocalFiles\\AuditoryStimuli", new LoadAuditoryStimulusSet().Folder);
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
        var x = waves[7].Select(s => (s + 32768.0) * (2.0 / 65535.0) - 1.0).ToArray();
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
        File.Delete(Path.Combine(rig, "calibration", "AuditoryStimuli", "filtered_pink_pulse_train_42.wav"));
        StringAssert.Contains(refusal(() => Load(Speakers())), "filtered_pink_pulse_train_42.wav");
    }

    [TestMethod]
    public void TheBackgroundNoiseAndThePenaltySoundAreTheLabs()
    {
        // Pip, 16 Nov 2026: one background noise for every dome task, RigConfigs\shared\sounds\background_noise.wav beside
        // the rig's folder, named by rig.yml's speakerArray.backgroundNoise; the task keeps none.
        var expected = Path.GetFullPath(Path.Combine(rig, "..", "shared", "sounds", "background_noise.wav"));
        Assert.AreEqual(expected, Speakers().BackgroundNoiseFile);
        Assert.AreEqual(32, PenaltySound.Buffer(Speakers(), -44.6).Rows);
    }

    [TestMethod]
    public void EachStimulusPlaysAtTheTasksLevelOfItsUnequalisedSelf()
    {
        var set = Load(Speakers());
        Func<Action, string> refusal = Fixture.Refusal;
        StringAssert.Contains(refusal(() => set.Buffer(7, 2)), "no level yet");
        var levelled = set.AtLevel(-47.2);
        var buffer = levelled.Buffer(7, 2);
        var unequalised = OpenRigs.Audio.Wave.Read(Path.Combine(rig, "calibration", "AuditoryStimuli", "filtered_pink_pulse_train_7.wav"), 48000);
        var gain = SoundLevel.Gain(unequalised, -47.2);
        var equalised = Samples(set.Stimulus(7, 2));
        using (var row = buffer.GetRow(2))
        using (var copy = new OpenCV.Net.Mat(1, buffer.Cols, OpenCV.Net.Depth.F32, 1))
        {
            OpenCV.Net.CV.Copy(row, copy);
            var played = Samples(copy);
            for (int n = 0; n < played.Length; n += 37) Assert.AreEqual(equalised[n] * gain, played[n], 1e-6);
        }
        Assert.AreEqual(-47.2, SoundLevel.RmsDbfs(unequalised) + 20 * Math.Log10(gain), 1e-9);
        StringAssert.Contains(refusal(() => set.AtLevel(-3)), "on speaker 'left' at -3 dBFS RMS (audioStimulus.levelDbfs) peaks at");
    }
}
