using System;
using System.Collections.Generic;
using Bugsee.Contracts.Options;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class CaptureStartupTests
    {
        // A fake native SDK and main thread. The native SDK keeps the video mode of the session that is running.
        sealed class Fake
        {
            public bool OnMain = true;
            public readonly Queue<Action> Queued = new Queue<Action>();
            public bool Running;
            public VideoMode? NativeMode;          // what getLaunchOptions reports (null = unreadable)
            public bool EnableResult = true;
            public bool EnableThrows;
            public bool PusherOn;
            public bool Latest = true;
            public readonly PusherLaunchState State = new PusherLaunchState();
            public int Enables, Disables;
            public readonly List<Dictionary<string, object>> Relaunches = new List<Dictionary<string, object>>();
            public readonly List<string> Warnings = new List<string>();

            public void RunOnMain(Action a) { if (OnMain) a(); else Queued.Enqueue(a); }
            public void Drain() { while (Queued.Count > 0) Queued.Dequeue()(); }

            public void Result(bool accepted, bool startRequested, Dictionary<string, object> options)
            {
                CaptureStartup.OnLaunchResult(accepted, startRequested, options, State, () => Latest,
                    () => Running,
                    () => NativeMode,
                    () => { Enables++; if (EnableThrows) throw new InvalidOperationException("boom"); PusherOn = EnableResult; return EnableResult; },
                    () => { Disables++; PusherOn = false; },
                    o => Relaunches.Add(new Dictionary<string, object>(o)),
                    Warnings.Add,
                    RunOnMain);
            }
        }

        static Dictionary<string, object> Mode(VideoMode m) =>
            new Dictionary<string, object> { { Options.CaptureVideoMode, (int)m }, { "other", 1 } };

        [Test]
        public void Accepted_direct_buffers_launch_starts_the_pusher_without_a_relaunch()
        {
            var f = new Fake { Running = true, NativeMode = VideoMode.DirectBuffers };
            f.Result(true, true, Mode(VideoMode.DirectBuffers));
            Assert.That(f.PusherOn, Is.True);
            Assert.That(f.Relaunches, Is.Empty);
            Assert.That(f.Warnings, Is.Empty);
        }

        [Test]
        public void Accepted_launch_with_an_unreadable_mode_follows_the_request()
        {
            var on = new Fake { Running = true, NativeMode = null };
            on.Result(true, true, Mode(VideoMode.DirectBuffers));
            Assert.That(on.PusherOn, Is.True);
            var off = new Fake { Running = true, NativeMode = null, PusherOn = true };
            off.Result(true, false, Mode(VideoMode.V2));
            Assert.That(off.PusherOn, Is.False);
        }

        [Test]
        public void A_launch_the_sdk_ignored_while_running_keeps_the_pusher_of_the_running_session()
        {
            // Running in DirectBuffers with the pusher on; another Launch asks for V2 and the SDK ignores it: it still
            // reports DirectBuffers, so the pusher must stay.
            var f = new Fake { Running = true, NativeMode = VideoMode.DirectBuffers, PusherOn = true };
            f.Result(true, false, Mode(VideoMode.V2));
            Assert.That(f.PusherOn, Is.True);
            Assert.That(f.Disables, Is.EqualTo(0));
        }

        [Test]
        public void A_launch_the_sdk_ignored_while_running_does_not_start_a_pusher_for_a_default_session()
        {
            var f = new Fake { Running = true, NativeMode = VideoMode.V2 };
            f.Result(true, true, Mode(VideoMode.DirectBuffers));
            Assert.That(f.PusherOn, Is.False);
            Assert.That(f.Enables, Is.EqualTo(0));
            Assert.That(f.Relaunches, Is.Empty);
        }

        [Test]
        public void A_rejected_request_while_the_sdk_runs_leaves_the_pusher_alone()
        {
            var f = new Fake { Running = true, NativeMode = VideoMode.DirectBuffers, PusherOn = true };
            f.Result(false, false, Mode(VideoMode.V2));
            Assert.That(f.PusherOn, Is.True);
            Assert.That(f.Disables, Is.EqualTo(0));
            Assert.That(f.Relaunches, Is.Empty);
        }

        [Test]
        public void A_rejected_request_with_nothing_running_stops_a_stale_pusher()
        {
            var f = new Fake { Running = false, PusherOn = true };
            f.Result(false, true, Mode(VideoMode.DirectBuffers));
            Assert.That(f.PusherOn, Is.False);
            Assert.That(f.Relaunches, Is.Empty);
        }

        [Test]
        public void Async_result_runs_on_the_main_thread_only_when_it_arrives_there()
        {
            var f = new Fake { OnMain = false, Running = true, NativeMode = VideoMode.DirectBuffers };
            f.Result(true, true, Mode(VideoMode.DirectBuffers));
            Assert.That(f.Enables, Is.EqualTo(0));
            f.Drain();
            Assert.That(f.PusherOn, Is.True);
        }

        [Test]
        public void A_start_that_fails_after_being_queued_still_relaunches_with_the_default_capture()
        {
            var f = new Fake { OnMain = false, Running = true, NativeMode = VideoMode.DirectBuffers, EnableResult = false };
            f.Result(true, true, Mode(VideoMode.DirectBuffers));
            Assert.That(f.Relaunches, Is.Empty, "not before the queued result ran");
            f.Drain();
            Assert.That(f.Relaunches, Has.Count.EqualTo(1));
            Assert.That(f.Relaunches[0][Options.CaptureVideoMode], Is.EqualTo((int)VideoMode.V2));
            Assert.That(f.Relaunches[0]["other"], Is.EqualTo(1));
            Assert.That(f.Warnings, Has.Count.EqualTo(1));
        }

        [Test]
        public void A_start_that_throws_is_treated_as_a_failed_start()
        {
            var f = new Fake { Running = true, NativeMode = VideoMode.DirectBuffers, EnableThrows = true };
            f.Result(true, true, Mode(VideoMode.DirectBuffers));
            Assert.That(f.Relaunches, Has.Count.EqualTo(1));
            Assert.That(f.Warnings.Count, Is.EqualTo(2));
        }

        [Test]
        public void Accepted_default_capture_launch_stops_the_pusher()
        {
            var f = new Fake { Running = true, NativeMode = VideoMode.V2, PusherOn = true };
            f.Result(true, false, Mode(VideoMode.V2));
            Assert.That(f.PusherOn, Is.False);
        }

        [Test]
        public void The_fallback_relaunch_runs_after_the_launch_was_accepted_not_while_it_is_still_launching()
        {
            // The relaunch is requested from the accepted-result callback, i.e. when the SDK is launched; an enable
            // failure never issues it before that result exists.
            var f = new Fake { Running = true, NativeMode = VideoMode.DirectBuffers, EnableResult = false };
            Assert.That(f.Relaunches, Is.Empty);
            f.Result(true, true, Mode(VideoMode.DirectBuffers));
            Assert.That(f.Relaunches, Has.Count.EqualTo(1));
        }

        [Test]
        public void A_pusher_that_stops_itself_later_relaunches_with_the_options_of_the_accepted_launch()
        {
            var f = new Fake { Running = true, NativeMode = VideoMode.DirectBuffers };
            f.Result(true, true, Mode(VideoMode.DirectBuffers));
            var relaunches = new List<Dictionary<string, object>>();
            CaptureStartup.OnPusherFailed(f.State, () => true, o => relaunches.Add(new Dictionary<string, object>(o)), f.Warnings.Add);
            Assert.That(relaunches, Has.Count.EqualTo(1));
            Assert.That(relaunches[0][Options.CaptureVideoMode], Is.EqualTo((int)VideoMode.V2));
            Assert.That(relaunches[0]["other"], Is.EqualTo(1));
        }

        [Test]
        public void A_pusher_failure_after_the_session_stopped_does_not_relaunch_with_old_options()
        {
            var f = new Fake { Running = true, NativeMode = VideoMode.DirectBuffers };
            f.Result(true, true, Mode(VideoMode.DirectBuffers));
            f.State.OnStopped();
            var relaunches = new List<Dictionary<string, object>>();
            CaptureStartup.OnPusherFailed(f.State, () => true, relaunches.Add, f.Warnings.Add);
            Assert.That(relaunches, Is.Empty);
        }

        [Test]
        public void A_later_accepted_launch_that_is_not_direct_buffers_clears_the_old_options()
        {
            var f = new Fake { Running = true, NativeMode = VideoMode.DirectBuffers };
            f.Result(true, true, Mode(VideoMode.DirectBuffers));
            f.NativeMode = VideoMode.V2;
            f.Result(true, false, new Dictionary<string, object> { { Options.CaptureVideoMode, (int)VideoMode.V2 } });
            Assert.That(f.State.Active, Is.False);
            var relaunches = new List<Dictionary<string, object>>();
            CaptureStartup.OnPusherFailed(f.State, () => true, relaunches.Add, f.Warnings.Add);
            Assert.That(relaunches, Is.Empty);
        }

        [Test]
        public void A_relaunch_without_options_that_keeps_direct_buffers_still_gets_the_fallback()
        {
            // Relaunch(null) keeps the running DirectBuffers; the capture policy had nothing to decide (startRequested false),
            // but the native SDK reports DirectBuffers, so the pusher is started, and if it cannot start the session falls back.
            var f = new Fake { Running = true, NativeMode = VideoMode.DirectBuffers, EnableResult = false };
            f.Result(true, false, null);
            Assert.That(f.Relaunches, Has.Count.EqualTo(1));
            Assert.That(f.Relaunches[0][Options.CaptureVideoMode], Is.EqualTo((int)VideoMode.V2));
        }

        [Test]
        public void A_relaunch_without_options_falls_back_with_the_options_of_the_earlier_launch()
        {
            var f = new Fake { Running = true, NativeMode = VideoMode.DirectBuffers };
            f.Result(true, true, Mode(VideoMode.DirectBuffers));
            f.EnableResult = false;
            f.Result(true, false, null);
            Assert.That(f.Relaunches, Has.Count.EqualTo(1));
            Assert.That(f.Relaunches[0]["other"], Is.EqualTo(1));
        }

        [Test]
        public void The_fallback_does_not_replace_the_options_of_a_waiting_newer_launch()
        {
            var f = new Fake { Running = true, NativeMode = VideoMode.DirectBuffers, EnableResult = false, Latest = false };
            f.Result(true, true, Mode(VideoMode.DirectBuffers));
            Assert.That(f.Relaunches, Is.Empty);
            Assert.That(f.Warnings, Has.Count.EqualTo(2).Or.Count.EqualTo(1));
            // and a pusher that stops itself while a newer request waits leaves it to that request
            var g = new Fake { Running = true, NativeMode = VideoMode.DirectBuffers };
            g.Result(true, true, Mode(VideoMode.DirectBuffers));
            var relaunches = new List<Dictionary<string, object>>();
            CaptureStartup.OnPusherFailed(g.State, () => false, relaunches.Add, g.Warnings.Add);
            Assert.That(relaunches, Is.Empty);
        }

        [Test]
        public void An_exception_while_reading_the_effective_mode_is_survived()
        {
            var warnings = new List<string>();
            var state = new PusherLaunchState();
            bool enabled = false;
            CaptureStartup.OnLaunchResult(true, true, Mode(VideoMode.DirectBuffers), state, () => true, () => true,
                () => throw new InvalidOperationException("jni"), () => { enabled = true; return true; }, () => { }, o => { }, warnings.Add, a => a());
            Assert.That(enabled, Is.True, "falls back to what the request asked for");
            Assert.That(warnings, Has.Count.EqualTo(1));
        }

        // The pusher fails while a user request is waiting: the fallback is deferred, never dropped, and decided by the
        // request's answer.
        Fake FailedWhileWaiting()
        {
            var f = new Fake { Running = true, NativeMode = VideoMode.DirectBuffers };
            f.Result(true, true, Mode(VideoMode.DirectBuffers));
            f.Latest = false;
            CaptureStartup.OnPusherFailed(f.State, () => f.Latest, o => f.Relaunches.Add(new Dictionary<string, object>(o)), f.Warnings.Add);
            Assert.That(f.Relaunches, Is.Empty);
            Assert.That(f.State.FallbackPending, Is.True);
            return f;
        }

        [Test]
        public void Pusher_failed_during_a_waiting_launch_the_sdk_ignored_still_gets_the_fallback()
        {
            var f = FailedWhileWaiting();
            f.Latest = true;
            f.Result(false, false, Mode(VideoMode.V2)); // ignored: SDK already running
            Assert.That(f.Relaunches, Has.Count.EqualTo(1));
            Assert.That(f.Relaunches[0][Options.CaptureVideoMode], Is.EqualTo((int)VideoMode.V2));
            Assert.That(f.Relaunches[0]["other"], Is.EqualTo(1), "on top of the options of the session that was running");
            Assert.That(f.State.FallbackPending, Is.False);
        }

        [Test]
        public void Pusher_failed_during_a_waiting_relaunch_that_was_refused_still_gets_the_fallback()
        {
            var f = FailedWhileWaiting();
            f.Latest = true;
            f.Result(false, true, Mode(VideoMode.DirectBuffers)); // refused
            Assert.That(f.Relaunches, Has.Count.EqualTo(1));
            Assert.That(f.Relaunches[0][Options.CaptureVideoMode], Is.EqualTo((int)VideoMode.V2));
        }

        [Test]
        public void Pusher_failed_during_a_waiting_launch_that_was_accepted_with_the_default_mode_drops_the_fallback()
        {
            var f = FailedWhileWaiting();
            f.Latest = true;
            f.NativeMode = VideoMode.V2;
            f.Result(true, false, Mode(VideoMode.V2));
            Assert.That(f.Relaunches, Is.Empty);
            Assert.That(f.State.FallbackPending, Is.False);
            Assert.That(f.Enables, Is.EqualTo(1), "only the original start: the pusher is not restarted");
        }

        [Test]
        public void Pusher_failed_during_a_waiting_launch_accepted_with_direct_buffers_goes_to_the_default_capture_with_the_new_options()
        {
            var f = FailedWhileWaiting();
            f.Latest = true;
            f.Result(true, true, new Dictionary<string, object> { { Options.CaptureVideoMode, (int)VideoMode.DirectBuffers }, { "other", 2 } });
            Assert.That(f.Relaunches, Has.Count.EqualTo(1));
            Assert.That(f.Relaunches[0][Options.CaptureVideoMode], Is.EqualTo((int)VideoMode.V2));
            Assert.That(f.Relaunches[0]["other"], Is.EqualTo(2));
            Assert.That(f.Enables, Is.EqualTo(1));
        }

        [Test]
        public void A_deferred_fallback_waits_while_yet_another_request_is_still_pending()
        {
            var f = FailedWhileWaiting();
            f.Latest = false; // a second request is queued behind the first
            f.Result(false, false, Mode(VideoMode.V2));
            Assert.That(f.Relaunches, Is.Empty);
            Assert.That(f.State.FallbackPending, Is.True);
            f.Latest = true;
            f.Result(false, false, Mode(VideoMode.V2));
            Assert.That(f.Relaunches, Has.Count.EqualTo(1));
        }

        [Test]
        public void A_start_failure_with_a_newer_request_waiting_is_deferred_not_dropped()
        {
            var f = new Fake { Running = true, NativeMode = VideoMode.DirectBuffers, EnableResult = false, Latest = false };
            f.Result(true, true, Mode(VideoMode.DirectBuffers));
            Assert.That(f.State.FallbackPending, Is.True);
            f.Latest = true;
            f.Result(false, false, Mode(VideoMode.V2));
            Assert.That(f.Relaunches, Has.Count.EqualTo(1));
        }

        [Test]
        public void A_launch_accepted_with_direct_buffers_but_not_the_latest_keeps_its_options_for_the_owed_fallback()
        {
            var f = FailedWhileWaiting();
            // request A is answered (accepted, DirectBuffers, options "other"=2) but request B is still waiting
            f.Latest = false;
            f.Result(true, true, new Dictionary<string, object> { { Options.CaptureVideoMode, (int)VideoMode.DirectBuffers }, { "other", 2 } });
            Assert.That(f.Relaunches, Is.Empty);
            Assert.That(f.State.FallbackPending, Is.True);
            // B is ignored: the fallback runs on top of A's options, not the first session's
            f.Latest = true;
            f.Result(false, false, Mode(VideoMode.V2));
            Assert.That(f.Relaunches, Has.Count.EqualTo(1));
            Assert.That(f.Relaunches[0]["other"], Is.EqualTo(2));
        }

        [Test]
        public void Stopping_the_session_cancels_a_deferred_fallback()
        {
            var f = FailedWhileWaiting();
            f.State.OnStopped();
            Assert.That(f.State.FallbackPending, Is.False);
        }
    }
}
