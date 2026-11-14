using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Reactive.Subjects;
using System.Threading.Tasks;

/// <summary>OnNextTick starts the trial loop on the Harp clock's thread with no lock held (BLUE-OPTO-2,
/// 14 Nov 2026: WithLatestTimestamp ran the first draw under a lock the clock needs, and the clock stopped).</summary>
[TestClass]
public class OnNextTickTests
{
    [TestMethod]
    public void A_value_waits_for_the_next_tick_and_is_stamped_with_it()
    {
        var source = new Subject<string>();
        var clock = new Subject<double>();
        var output = new List<Bonsai.Harp.Timestamped<string>>();
        using (new OnNextTick().Process(source, clock).Subscribe(output.Add))
        {
            clock.OnNext(1.000);
            source.OnNext("folder");
            Assert.AreEqual(0, output.Count, "given before a tick");
            clock.OnNext(1.001);
            clock.OnNext(1.002);
            Assert.AreEqual(1, output.Count);
            Assert.AreEqual("folder", output[0].Value);
            Assert.AreEqual(1.001, output[0].Seconds, 1e-9);
        }
    }

    [TestMethod]
    public void The_value_is_given_with_no_lock_held()
    {
        var source = new Subject<string>();
        var clock = new Subject<double>();
        bool otherThreadGotThrough = false;
        using (new OnNextTick().Process(source, clock).Subscribe(value =>
        {
            if (value.Value != "first") return;
            // What follows the first value (the first draw) may wait on another thread that feeds the
            // operator: with a lock held it would never get through.
            otherThreadGotThrough = Task.Run(() => source.OnNext("second")).Wait(2000);
        }))
        {
            source.OnNext("first");
            clock.OnNext(5.0);
            Assert.IsTrue(otherThreadGotThrough);
        }
    }
}
