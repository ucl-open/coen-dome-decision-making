using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using UclOpenCoenDomeDecisionMaking;

[TestClass]
public class AuditoryStimulusSetTests
{
    static readonly double[] Symmetric = { 0.01, -0.05, 0.2, 1.1, 0.2, -0.05, 0.01 };
    string rig;
    short[][] waves;

    [TestInitialize]
    public void Write()
    {
        AuditoryStimulusSet.MachineName = "THIS-RIG";
        rig = Path.Combine(Path.GetTempPath(), "dome-decision-making-audio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(rig, "calibration", "AuditoryStimuli"));
        var random = new Random(1);
        waves = new short[AuditoryStimulusSet.WaveformCount][];
        for (int i = 0; i < waves.Length; i++)
        {
            waves[i] = Enumerable.Range(0, 480).Select(_ => (short)random.Next(short.MinValue, short.MaxValue + 1)).ToArray();
            Wave(i, waves[i], 48000);
        }
        Filter("sym", Symmetric, 48000, "THIS-RIG");
        Filter("asym", new[] { 1, 0.5, 0.25 }, 48000, "THIS-RIG");
        Filter("rate", Symmetric, 44100, "THIS-RIG");
        Filter("other", Symmetric, 48000, "OTHER-RIG");
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

    void Filter(string name, double[] taps, double rate, string machine)
    {
        var json = "{\"calibratedAt\":\"2026-11-04T00:00:00Z\",\"calibratedBy\":\"test\",\"machineName\":\"" + machine + "\",\"notes\":\"\",\"sampleRate\":" +
            rate.ToString(CultureInfo.InvariantCulture) + ",\"taps\":[" + string.Join(",", taps.Select(t => t.ToString("R", CultureInfo.InvariantCulture))) + "]}";
        File.WriteAllText(Path.Combine(rig, "calibration", name + ".json"), json);
    }

    static DomeSpeakerArray Speakers(string leftFilter)
    {
        var array = Fixture.Speakers();
        array.Device = new AudioDevice();
        array.Device.SampleRate = 48000;
        array.Speakers["left"].Filter = leftFilter;
        array.Speakers["right"].Filter = "calibration/sym.json";
        array.Speakers["centre"].Filter = "calibration/sym.json";
        return array;
    }

    AuditoryStimulusSet Load(DomeSpeakerArray speakers)
    {
        return AuditoryStimulusSet.Load(speakers, rig, "calibration\\AuditoryStimuli", "filtered_pink_pulse_train_");
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
        var set = Load(Speakers("calibration/sym.json"));
        Assert.AreEqual(480, set.WaveformLength);
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
    public void EveryUnusableFileIsRefusedByName()
    {
        Func<Action, string> refusal = Fixture.Refusal;
        StringAssert.Contains(refusal(() => Load(Speakers(null))), "'left' (channel 0) has no filter");
        StringAssert.Contains(refusal(() => Load(Speakers("calibration/asym.json"))), "not symmetric");
        StringAssert.Contains(refusal(() => Load(Speakers("calibration/rate.json"))), "designed for 44100 Hz");
        StringAssert.Contains(refusal(() => Load(Speakers("calibration/none.json"))), "filter not found");
        StringAssert.Contains(refusal(() => Load(Speakers("calibration/other.json"))), "measured on 'OTHER-RIG'");
        Wave(5, waves[5], 44100);
        StringAssert.Contains(refusal(() => Load(Speakers("calibration/sym.json"))), "filtered_pink_pulse_train_5.wav");
        File.Delete(Path.Combine(rig, "calibration", "AuditoryStimuli", "filtered_pink_pulse_train_42.wav"));
        StringAssert.Contains(refusal(() => Load(Speakers("calibration/sym.json"))), "filtered_pink_pulse_train_42.wav");
    }
}
