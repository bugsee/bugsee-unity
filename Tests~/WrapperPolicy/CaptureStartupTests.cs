using System;
using System.Collections.Generic;
using Bugsee.Contracts.Options;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class CaptureStartupTests
    {
        // A fake main thread: actions run inline when "on main", otherwise they wait in a queue until Drain().
        sealed class Fake
        {
            public bool OnMain = true;
            public readonly Queue<Action> Queued = new Queue<Action>();
            public int Enables, Disables;
            public bool EnableResult = true;
            public bool EnableThrows;
            public readonly List<Dictionary<string, object>> Relaunches = new List<Dictionary<string, object>>();
            public readonly List<string> Warnings = new List<string>();

            public void RunOnMain(Action a) { if (OnMain) a(); else Queued.Enqueue(a); }
            public void Drain() { while (Queued.Count > 0) Queued.Dequeue()(); }

            public void Start(bool startPusher, Dictionary<string, object> options)
            {
                CaptureStartup.Run(startPusher, options,
                    () => { Enables++; if (EnableThrows) throw new InvalidOperationException("boom"); return EnableResult; },
                    () => Disables++,
                    o => Relaunches.Add(new Dictionary<string, object>(o)),
                    Warnings.Add,
                    RunOnMain);
            }
        }

        static Dictionary<string, object> Direct() =>
            new Dictionary<string, object> { { Options.CaptureVideoMode, (int)VideoMode.DirectBuffers }, { "other", 1 } };

        [Test]
        public void Successful_start_on_the_main_thread_does_not_relaunch()
        {
            var f = new Fake();
            f.Start(true, Direct());
            Assert.That(f.Enables, Is.EqualTo(1));
            Assert.That(f.Relaunches, Is.Empty);
            Assert.That(f.Warnings, Is.Empty);
        }

        [Test]
        public void Off_the_main_thread_nothing_runs_until_the_main_thread_takes_it()
        {
            var f = new Fake { OnMain = false };
            f.Start(true, Direct());
            Assert.That(f.Enables, Is.EqualTo(0));
            f.Drain();
            Assert.That(f.Enables, Is.EqualTo(1));
        }

        [Test]
        public void A_start_that_fails_after_being_queued_still_relaunches_with_the_default_capture()
        {
            var f = new Fake { OnMain = false, EnableResult = false };
            f.Start(true, Direct());
            Assert.That(f.Relaunches, Is.Empty, "not before the queued start ran");
            f.Drain();
            Assert.That(f.Relaunches, Has.Count.EqualTo(1));
            Assert.That(f.Relaunches[0][Options.CaptureVideoMode], Is.EqualTo((int)VideoMode.V2));
            Assert.That(f.Relaunches[0]["other"], Is.EqualTo(1));
            Assert.That(f.Warnings, Has.Count.EqualTo(1));
        }

        [Test]
        public void A_start_that_throws_is_treated_as_a_failed_start()
        {
            var f = new Fake { EnableThrows = true };
            f.Start(true, Direct());
            Assert.That(f.Relaunches, Has.Count.EqualTo(1));
            Assert.That(f.Relaunches[0][Options.CaptureVideoMode], Is.EqualTo((int)VideoMode.V2));
            Assert.That(f.Warnings.Count, Is.EqualTo(2));
        }

        [Test]
        public void A_launch_that_does_not_want_the_pusher_stops_it_and_never_relaunches()
        {
            var f = new Fake();
            f.Start(false, new Dictionary<string, object> { { Options.CaptureVideoMode, (int)VideoMode.V2 } });
            Assert.That(f.Disables, Is.EqualTo(1));
            Assert.That(f.Enables, Is.EqualTo(0));
            Assert.That(f.Relaunches, Is.Empty);
        }

        [Test]
        public void A_pusher_that_stops_itself_later_relaunches_with_the_default_capture()
        {
            var relaunches = new List<Dictionary<string, object>>();
            var warnings = new List<string>();
            CaptureStartup.OnPusherFailed(Direct(), o => relaunches.Add(new Dictionary<string, object>(o)), warnings.Add);
            Assert.That(relaunches, Has.Count.EqualTo(1));
            Assert.That(relaunches[0][Options.CaptureVideoMode], Is.EqualTo((int)VideoMode.V2));
            Assert.That(warnings, Has.Count.EqualTo(1));
        }

        [Test]
        public void Fallback_with_null_options_still_names_the_default_mode()
        {
            var f = new Fake { EnableResult = false };
            f.Start(true, null);
            Assert.That(f.Relaunches[0][Options.CaptureVideoMode], Is.EqualTo((int)VideoMode.V2));
        }
    }
}
