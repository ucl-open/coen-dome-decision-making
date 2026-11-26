using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Devices.Rigs;
using OpenRigs.Devices.Zapit;
using OpenRigs.Trials;
using OpenRigs.Zapit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.RegularExpressions;
using UclOpenCoenDomeDecisionMakingTask;

/// <summary>The two Zapit boxes (Pip, 26 Nov 2026: "two boxes, mutually exclusive, Zapit-Bonsai and Zapit-MATLAB"): neither is no
/// optogenetics, each alone picks its Zapit's path and timing, both are refused, an old file's enabled: true is Zapit-MATLAB, and
/// Go refuses a ticked Zapit the session does not have. The MATLAB cases go when MATLAB Zapit is retired.</summary>
[TestClass]
public class DualZapitTests
{
    // MATLAB Zapit's conditions file as ZapitConditions.Check reads it: two conditions, the lab's 0.25 s ramp-down.
    static readonly Func<string, ZapitConditionsFile> Conditions = path => new ZapitConditionsFile(new[] { "-0.7 1.5;-0.7 -1.5", "1.5 1;1.5 -1" }, 0.25);

    static UclOpenCoenDomeDecisionMakingTaskParameters Task(bool bonsai, bool matlab, double laserSeconds = 0.8)
    {
        var task = Fixture.Task("task-discrimination-left-right.yml", 4);
        if (bonsai || matlab) Rules.Optogenetics(task);
        task.Optogenetics.ZapitBonsai = bonsai;
        task.Optogenetics.ZapitMatlab = matlab;
        task.Optogenetics.LaserDurationSeconds = laserSeconds;
        return task;
    }

    static LabRig Rig(string device, bool hardwareTriggered = true)
    {
        var zapit = new OpenRigs.Devices.Rigs.Zapit { Host = "127.0.0.1", Port = 1488, HardwareTriggered = hardwareTriggered, Device = device };
        return new LabRig { SpeakerArray = Fixture.Speakers(), Zapit = zapit };
    }

    [TestMethod]
    public void NeitherBoxIsNoZapit()
    {
        var task = Task(false, false);
        var rules = new TrialRules(Fixture.Text(task), Fixture.Speakers(), null);
        Assert.IsNull(rules.Laser, "no laser timing: neither Zapit piece is given the rules");
        Assert.IsFalse(rules.ZapitBonsai || rules.ZapitMatlab);
        var trial = rules.Draw(null);
        Assert.AreEqual(0, trial.laserCondition);
        // Go with neither needs no Zapit at all, whatever the rig has or opened.
        Assert.IsNotNull(TrialRules.Create(Fixture.Text(task), new LabRig { SpeakerArray = Fixture.Speakers() }, null, false));
        Assert.IsNotNull(TrialRules.Create(Fixture.Text(task), Rig("Dev1"), null, true));
        // The shipped task files tick neither.
        foreach (var file in new[] { "task-localization.yml", "task-discrimination-go-no-go.yml", "task-discrimination-left-right.yml" })
        {
            var shipped = Fixture.Task(file, 0).Optogenetics;
            Assert.IsFalse(shipped.ZapitBonsai || shipped.ZapitMatlab, file);
        }
    }

    [TestMethod]
    public void ZapitBonsaiAloneTakesZapitDaqsPathAndTiming()
    {
        var bank = Fixture.Bank();
        var rules = new TrialRules(Fixture.Text(Task(true, false)), Fixture.Speakers(), bank);
        Assert.IsTrue(rules.ZapitBonsai && !rules.ZapitMatlab);
        Assert.IsTrue(rules.Laser.HardwareTriggered, "ZapitDaq always waits for the trigger line");
        Assert.AreEqual(bank.LongestRampSeconds, rules.Laser.RampDownSeconds, 1e-9);
        Assert.IsTrue(rules.Draw(null).laserCondition > 0);
        // Its limit: the trial's end plus itiMinSeconds less what a ZapitDaq trial outlasts its laser and the arm time.
        var task = Task(true, false);
        Assert.AreEqual(TrialDraw.TrialEndSeconds(task.TrialStructure) + task.TrialStructure.ItiMinSeconds - ZapitDaqTrial.FreeAfterLaserSeconds(bank) - ZapitDaqTrial.ArmSeconds,
            TrialDraw.MaxLaserSeconds(task, ZapitDaqTrial.FreeAfterLaserSeconds(bank)), 1e-9);
        StringAssert.Contains(Fixture.Refusal(() => new TrialRules(Fixture.Text(Task(true, false)), Fixture.Speakers(), null)), "Zapit-Bonsai is ticked, but no Zapit bank was loaded");
        // The loop reads ZapitDaq's events, not MATLAB Zapit's replies.
        var events = new Subject<ZapitDaqEvent>();
        var replies = new Subject<ZapitReply>();
        new RunTrials().Process(new BehaviorSubject<TrialRules>(rules), new Subject<double>(), new Subject<double>(), new Subject<bool>(), new Subject<bool>(), events, replies).Subscribe();
        Assert.IsTrue(events.HasObservers && !replies.HasObservers);
    }

    [TestMethod]
    public void ZapitMatlabAloneTakesMatlabZapitsPathAndTiming()
    {
        var rules = new TrialRules(Fixture.Text(Task(false, true)), Fixture.Speakers(), false, Conditions);
        Assert.IsTrue(rules.ZapitMatlab && !rules.ZapitBonsai);
        Assert.IsFalse(rules.Laser.HardwareTriggered, "rig.yml's zapit.hardwareTriggered, as main had it");
        Assert.AreEqual(0.25, rules.Laser.RampDownSeconds, 1e-9);
        Assert.IsTrue(new TrialRules(Fixture.Text(Task(false, true)), Fixture.Speakers(), true, Conditions).Laser.HardwareTriggered);
        var trial = rules.Draw(null);
        Assert.IsTrue(trial.laserCondition >= 1 && trial.laserCondition <= 2, "a condition of the conditions file");
        // Its limit: 4.55 s with the shipped files at the lab's 0.25 s ramp-down (5 s to the next trigger less 0.25 s and the 0.2 s reply margin).
        var shipped = Fixture.Task("task-localization.yml", 0);
        Assert.AreEqual(4.55, TrialDraw.MatlabMaxLaserSeconds(shipped, 0.25), 1e-9);
        var limit = TrialDraw.MatlabMaxLaserSeconds(Task(false, true), 0.25);
        Assert.AreEqual("accepted", Fixture.Refusal(() => new TrialRules(Fixture.Text(Task(false, true, limit)), Fixture.Speakers(), true, Conditions)));
        StringAssert.Contains(Fixture.Refusal(() => new TrialRules(Fixture.Text(Task(false, true, limit + 0.01)), Fixture.Speakers(), true, Conditions)),
            "the Zapit conditions file's 0.25 s ramp-down - 0.2 s reply margin");
        // A Zapit-MATLAB session needs no bank, and the loop reads MATLAB Zapit's replies (its stop reply among them).
        var events = new Subject<ZapitDaqEvent>();
        var replies = new Subject<ZapitReply>();
        new RunTrials().Process(new BehaviorSubject<TrialRules>(rules), new Subject<double>(), new Subject<double>(), new Subject<bool>(), new Subject<bool>(), events, replies).Subscribe();
        Assert.IsTrue(replies.HasObservers && !events.HasObservers);
    }

    [TestMethod]
    public void BothBoxesAreRefused()
    {
        var both = Fixture.Text(Task(true, true));
        StringAssert.Contains(Fixture.Refusal(() => new TrialRules(both, Fixture.Speakers(), Fixture.Bank())), TrialDraw.BothZapits);
        StringAssert.Contains(TrialDraw.BothZapits, "optogenetics.zapitBonsai (Zapit-Bonsai) and optogenetics.zapitMatlab (Zapit-MATLAB)");
        StringAssert.Contains(Fixture.Refusal(() => TrialRules.Create(both, Rig("Dev1"), Fixture.Bank(), true)), TrialDraw.BothZapits);
    }

    // An old task file: optogenetics.enabled in place of the two boxes.
    static string Old(UclOpenCoenDomeDecisionMakingTaskParameters task, string enabled)
    {
        var text = Fixture.Text(task);
        text = Regex.Replace(text, @"zapitBonsai: (true|false)", "enabled: " + enabled);
        return Regex.Replace(text, @"\r?\n *zapitMatlab: (true|false)", "");
    }

    [TestMethod]
    public void AnOldFilesEnabledTrueIsZapitMatlab()
    {
        var on = Old(Task(false, true), "true");
        StringAssert.Contains(on, "enabled: true");
        Assert.IsFalse(on.Contains("zapit" + "Matlab"), "the old file has neither box");
        var read = new ReadTask().Process(Observable.Return(on)).Wait().TaskParameters.Optogenetics;
        Assert.IsTrue(read.ZapitMatlab && !read.ZapitBonsai);
        var rules = new TrialRules(on, Fixture.Speakers(), true, Conditions);
        Assert.IsTrue(rules.ZapitMatlab && rules.Laser != null);
        var off = new ReadTask().Process(Observable.Return(Old(Task(false, false), "false"))).Wait().TaskParameters.Optogenetics;
        Assert.IsFalse(off.ZapitMatlab || off.ZapitBonsai);
        Assert.IsNull(new TrialRules(Old(Task(false, false), "false"), Fixture.Speakers(), null).Laser);
        // enabled beside either box is refused.
        var mixed = Fixture.Text(Task(false, true)).Replace("zapitBonsai: false", "zapitBonsai: false\n    enabled: true");
        StringAssert.Contains(Fixture.Refusal(() => OldOptogeneticsSwitch.Upgrade(mixed)), "enabled, the old switch, beside zapitBonsai or zapitMatlab");
        // A file with the boxes is passed on as it is.
        var current = Fixture.Text(Task(false, false));
        Assert.AreSame(current, OldOptogeneticsSwitch.Upgrade(current));
    }

    [TestMethod]
    public void GoRefusesATickedZapitTheSessionHasNot()
    {
        var bonsai = Fixture.Text(Task(true, false));
        var matlab = Fixture.Text(Task(false, true));
        // Zapit-Bonsai on a rig with no native block (MATLAB Zapit's host and port alone), or none at all.
        StringAssert.Contains(Fixture.Refusal(() => TrialRules.Create(bonsai, Rig(null), Fixture.Bank(), false)), TrialRules.NoNativeZapit);
        StringAssert.Contains(Fixture.Refusal(() => TrialRules.Create(bonsai, new LabRig { SpeakerArray = Fixture.Speakers() }, Fixture.Bank(), false)), TrialRules.NoNativeZapit);
        StringAssert.Contains(TrialRules.NoNativeZapit, "tick Zapit-MATLAB instead, or fill in rig.yml's zapit block");
        // A native rig whose ZapitDaq stayed closed: Zapit-MATLAB was ticked at launch.
        StringAssert.Contains(Fixture.Refusal(() => TrialRules.Create(bonsai, Rig("Dev1"), Fixture.Bank(), false)), "launch again with Zapit-Bonsai ticked");
        Assert.IsTrue(TrialRules.Create(bonsai, Rig("Dev1"), Fixture.Bank(), true).ZapitBonsai);
        // Zapit-MATLAB while ZapitDaq holds the NI card and the camera.
        StringAssert.Contains(Fixture.Refusal(() => TrialRules.Create(matlab, Rig("Dev1"), null, true)), "ZapitDaq holds the NI card and the camera MATLAB Zapit needs");
        Assert.IsNull(TrialRules.ZapitSessionProblem(Task(false, true).Optogenetics, Rig(null), false));
    }

    // CheckTaskAtGo takes ZapitRig, ConnectForTaskZapitDaq's AsyncSubject: a value when ZapitDaq opened at launch, none when not.
    [TestMethod]
    public void CheckTaskAtGoReadsWhetherZapitDaqOpened()
    {
        Func<string, LabRig, IObservable<OpenRigs.Devices.Rigs.Zapit>, IObservable<ZapitBank>, string> go = (text, rig, opened, bank) =>
            Fixture.Refusal(() => new CheckTaskAtGo().Process(Observable.Return(Tuple.Create(text, rig)), bank, opened).Wait());
        var closed = Observable.Empty<OpenRigs.Devices.Rigs.Zapit>();
        var open = Observable.Return(new OpenRigs.Devices.Rigs.Zapit { Device = "Dev1" });
        Assert.AreEqual("accepted", go(Fixture.Text(Task(true, false)), Rig("Dev1"), open, Observable.Return(Fixture.Bank())));
        StringAssert.Contains(go(Fixture.Text(Task(true, false)), Rig(null), closed, Observable.Never<ZapitBank>()), TrialRules.NoNativeZapit);
        StringAssert.Contains(go(Fixture.Text(Task(false, true)), Rig("Dev1"), open, Observable.Never<ZapitBank>()), "ZapitDaq holds the NI card");
        Assert.AreEqual("accepted", go(Fixture.Text(Task(false, false)), Rig("Dev1"), open, Observable.Never<ZapitBank>()));
    }
}
