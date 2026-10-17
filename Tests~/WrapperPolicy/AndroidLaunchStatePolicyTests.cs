using System;
using System.Collections.Generic;
using Bugsee.Contracts.Lifecycle;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    // Models the Android 7.4.1 SDK (BugseeInternal) where it matters to the wrapper:
    // - launch(): Launching/Launched -> no-op, callback(true) inline, token and options ignored; Stopping -> callback(false)
    //   inline; otherwise -> Launching, callback later.
    // - relaunch(): only when Launched (-> Stopping inside the call, stop then continuation launch(), always calls back);
    //   otherwise it logs and returns WITHOUT a callback.
    // - stop(r): Stopped -> r at once; Stopping -> r joins; otherwise -> Stopping and r when stopped. Joiners run before
    //   a relaunch's continuation launch().
    sealed class FakeAndroidSdk : IAndroidLifecycleNative
    {
        public BugseeStatus Current = BugseeStatus.Stopped;
        public readonly List<string> Calls = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public Action<bool> PendingLaunch;
        public Action<bool> PendingRelaunch;
        public Action<bool> PendingDelete;
        public readonly List<Action> StopJoiners = new List<Action>();
        public bool HooksInstalled;
        public IDictionary<string, object> HookOptions;
        public string RunningToken;
        // Runs inside relaunch() before the SDK checks its status: another thread stopping the SDK in the gap.
        public Action BeforeRelaunchCheck;

        public BugseeStatus Status() => Current;

        public void Launch(string appToken, IDictionary<string, object> options, Action<bool> done)
        {
            Calls.Add("launch:" + appToken);
            if (AndroidLaunchStatePolicy.IsRunning(Current)) { done(true); return; }
            if (Current == BugseeStatus.Stopping) { done(false); return; }
            if (string.IsNullOrEmpty(appToken)) { done(false); return; }
            Current = BugseeStatus.Launching;
            RunningToken = appToken;
            PendingLaunch = done;
        }

        public void Relaunch(IDictionary<string, object> options, Action<bool> done)
        {
            Calls.Add("relaunch");
            BeforeRelaunchCheck?.Invoke();
            if (Current != BugseeStatus.Launched) return; // no callback, ever
            Current = BugseeStatus.Stopping;
            PendingRelaunch = done;
        }

        public void Stop(Action done)
        {
            Calls.Add("stop");
            if (Current == BugseeStatus.Stopped) { done(); return; }
            if (Current != BugseeStatus.Stopping) Current = BugseeStatus.Stopping;
            StopJoiners.Add(done);
        }

        public void DeleteCollectedData(Action<bool> done)
        {
            Calls.Add("delete:" + Current);
            PendingDelete = done;
        }

        public void InstallHooks(IDictionary<string, object> options) { HooksInstalled = true; HookOptions = options; }
        public void UninstallHooks() => HooksInstalled = false;
        public Func<TimeSpan> Clock = () => TimeSpan.Zero;
        public readonly List<TimeSpan> ScheduledTicks = new List<TimeSpan>();
        public void ScheduleTick(TimeSpan delay) => ScheduledTicks.Add(Clock() + delay);
        public void Warn(string message) => Warnings.Add(message);
        public void Error(Exception exception) => throw exception;

        // The SDK finishes a stop: STOPPED, joiners, then a relaunch's continuation launch().
        public void FinishStop()
        {
            Current = BugseeStatus.Stopped;
            var joiners = StopJoiners.ToArray();
            StopJoiners.Clear();
            foreach (var j in joiners) j();
            if (PendingRelaunch != null)
            {
                var relaunchDone = PendingRelaunch;
                PendingRelaunch = null;
                if (Current != BugseeStatus.Stopped) { relaunchDone(false); return; }
                Current = BugseeStatus.Launching;
                PendingLaunch = relaunchDone;
            }
        }

        // Capture started (or not): Launching -> Launched and callback(true); stopped meanwhile -> callback(false).
        public void FinishLaunch()
        {
            var done = PendingLaunch;
            PendingLaunch = null;
            if (Current == BugseeStatus.Launching) { Current = BugseeStatus.Launched; done(true); }
            else done(false);
        }

        public void FinishDelete(bool ok)
        {
            var done = PendingDelete;
            PendingDelete = null;
            done(ok);
        }
    }

    public class AndroidLaunchStatePolicyTests
    {
        FakeAndroidSdk _sdk;
        TimeSpan _now;
        AndroidLifecycleMachine _machine;
        static readonly IDictionary<string, object> OptionsA = new Dictionary<string, object> { ["a"] = 1 };
        static readonly IDictionary<string, object> OptionsB = new Dictionary<string, object> { ["b"] = 2 };

        [SetUp]
        public void SetUp()
        {
            _sdk = new FakeAndroidSdk();
            _now = TimeSpan.Zero;
            _sdk.Clock = () => _now;
            _machine = new AndroidLifecycleMachine(_sdk, () => _now);
        }

        void Launched(string token = "t1", IDictionary<string, object> options = null)
        {
            _machine.Launch(token, options ?? OptionsA);
            _sdk.FinishLaunch();
            Assert.That(_sdk.Current, Is.EqualTo(BugseeStatus.Launched));
            _sdk.Calls.Clear();
        }

        // Time passes, but Tick runs only when a tick the machine scheduled falls due (as in AndroidBridge).
        void AdvanceFiringScheduledTicks(TimeSpan to)
        {
            while (true)
            {
                var due = _sdk.ScheduledTicks.FindAll(t => t <= to);
                if (due.Count == 0) break;
                due.Sort();
                _sdk.ScheduledTicks.Remove(due[0]);
                _now = due[0] > _now ? due[0] : _now;
                _machine.Tick();
            }
            _now = to;
        }

        void Elapse(TimeSpan by)
        {
            _now += by;
            _machine.Tick();
        }

        [TestCase(BugseeStatus.Launched, true)]
        [TestCase(BugseeStatus.Launching, false)]
        [TestCase(BugseeStatus.Stopping, false)]
        [TestCase(BugseeStatus.Stopped, false)]
        public void Relaunch_is_accepted_only_when_launched(BugseeStatus status, bool accepted)
        {
            Assert.That(AndroidLaunchStatePolicy.RelaunchAccepted(status), Is.EqualTo(accepted));
        }

        [TestCase(BugseeStatus.Stopped, BugseeStatus.Launching, true)]
        [TestCase(BugseeStatus.Stopped, BugseeStatus.Stopped, false)]
        [TestCase(BugseeStatus.Stopping, BugseeStatus.Stopping, false)]
        [TestCase(BugseeStatus.Launching, BugseeStatus.Launching, false)]
        [TestCase(BugseeStatus.Launched, BugseeStatus.Launched, false)]
        public void Only_a_launch_from_not_running_starts_a_session(BugseeStatus before, BugseeStatus after, bool started)
        {
            Assert.That(AndroidLaunchStatePolicy.LaunchStartedSession(before, after), Is.EqualTo(started));
        }

        [Test]
        public void Launch_reports_the_sdk_result_and_installs_the_hooks()
        {
            bool? result = null;
            _machine.Launch("t1", OptionsA, ok => result = ok);
            Assert.That(_sdk.HooksInstalled, Is.True, "installed before launch()");
            _sdk.FinishLaunch();
            Assert.That(result, Is.True);
            Assert.That(_sdk.HookOptions, Is.SameAs(OptionsA));
        }

        [Test]
        public void Refused_launch_removes_the_hooks_and_reports_false()
        {
            bool? result = null;
            _machine.Launch("", OptionsA, ok => result = ok);
            Assert.That(result, Is.False);
            Assert.That(_sdk.HooksInstalled, Is.False);
        }

        // (A) relaunch() refused because another thread stopped the SDK between the wrapper's status check and the
        // call: no callback ever. The claim must not block a pending delete forever.
        [Test]
        public void Relaunch_refused_after_a_concurrent_stop_releases_its_claim_by_the_deadline()
        {
            Launched();
            bool? relaunched = null, deleted = null;
            _sdk.BeforeRelaunchCheck = () => _sdk.Current = BugseeStatus.Stopping;
            _machine.Relaunch(OptionsB, ok => relaunched = ok);
            _machine.DeleteCollectedData(ok => deleted = ok);
            Assert.That(_sdk.Calls, Does.Not.Contain("delete:Stopped"), "waits for the relaunch result");

            _sdk.FinishStop(); // the other thread's stop completes; the SDK never calls the relaunch back
            Elapse(AndroidLifecycleMachine.OperationTimeout - TimeSpan.FromMilliseconds(1));
            Assert.That(relaunched, Is.Null);
            Elapse(TimeSpan.FromMilliseconds(1));

            Assert.That(relaunched, Is.False, "the soft deadline answers the caller with a warning");
            Assert.That(_sdk.Warnings, Has.Some.Contains("within 10 s"));
            Assert.That(_sdk.Calls, Does.Not.Contain("delete:Stopped"), "but keeps the claim: a continuation may still come");
            Elapse(AndroidLifecycleMachine.HardTimeout - AndroidLifecycleMachine.OperationTimeout);
            Assert.That(_sdk.Calls, Does.Contain("delete:Stopped"), "released by the hard deadline");
            _sdk.FinishDelete(true);
            Assert.That(deleted, Is.True);
        }

        [Test]
        public void Relaunch_refused_while_still_launched_releases_its_claim_at_once()
        {
            // Stopped and launched again by someone else around the call: relaunch() refused, the SDK is Launched.
            Launched("t1", OptionsA);
            bool? relaunched = null;
            var machine = new AndroidLifecycleMachine(new RefusingRelaunchSdk(_sdk), () => _now);
            machine.Relaunch(OptionsB, ok => relaunched = ok);
            Assert.That(relaunched, Is.False, "no deadline needed");
            Assert.That(_sdk.HookOptions, Is.Not.SameAs(OptionsB), "the hooks keep the running session's options");
        }

        // relaunch() that returns without a callback while the SDK stays Launched (refused, running again).
        sealed class RefusingRelaunchSdk : IAndroidLifecycleNative
        {
            readonly FakeAndroidSdk _inner;
            public RefusingRelaunchSdk(FakeAndroidSdk inner) { _inner = inner; }
            public BugseeStatus Status() => _inner.Status();
            public void Launch(string t, IDictionary<string, object> o, Action<bool> d) => _inner.Launch(t, o, d);
            public void Relaunch(IDictionary<string, object> o, Action<bool> d) => _inner.Calls.Add("relaunch");
            public void Stop(Action d) => _inner.Stop(d);
            public void DeleteCollectedData(Action<bool> d) => _inner.DeleteCollectedData(d);
            public void InstallHooks(IDictionary<string, object> o) => _inner.InstallHooks(o);
            public void UninstallHooks() => _inner.UninstallHooks();
            public void ScheduleTick(TimeSpan d) { }
            public void Warn(string m) => _inner.Warn(m);
            public void Error(Exception e) => throw e;
        }

        [Test]
        public void Accepted_relaunch_keeps_its_claim_until_the_sdk_reports()
        {
            Launched();
            bool? relaunched = null, deleted = null;
            _machine.Relaunch(OptionsB, ok => relaunched = ok);
            _machine.DeleteCollectedData(ok => deleted = ok);
            _sdk.FinishStop(); // joiners, then the continuation launch()
            Assert.That(_sdk.Calls, Does.Not.Contain("delete:Stopped"), "never between the relaunch's stop and launch()");
            _sdk.FinishLaunch();
            Assert.That(relaunched, Is.True);
            // Now the delete stops the relaunched session and deletes.
            Assert.That(_sdk.Current, Is.EqualTo(BugseeStatus.Stopping));
            _sdk.FinishStop();
            Assert.That(_sdk.Calls, Does.Contain("delete:Stopped"));
            _sdk.FinishDelete(true);
            Assert.That(deleted, Is.True);
        }

        [Test]
        public void Late_callback_after_the_deadline_is_ignored()
        {
            Launched();
            bool? relaunched = null;
            _machine.Relaunch(OptionsB, ok => relaunched = ok);
            Elapse(AndroidLifecycleMachine.OperationTimeout);
            Assert.That(relaunched, Is.False);
            bool? launched = null;
            _machine.Launch("t2", OptionsA, ok => launched = ok); // waits for the relaunch's stop, then launches
            _sdk.FinishStop(); // the relaunch's continuation now sees Launching and reports false, long after its reset
            _sdk.FinishLaunch();
            Assert.That(relaunched, Is.False, "reported once");
            Assert.That(launched, Is.True, "the stale false did not settle the newer launch");
            Assert.That(_sdk.HooksInstalled, Is.True);
        }

        [Test]
        public void Stop_completion_runs_with_a_warning_when_the_sdk_never_finishes()
        {
            Launched();
            var stopped = 0;
            _machine.Stop(() => stopped++);
            Elapse(AndroidLifecycleMachine.OperationTimeout);
            Assert.That(stopped, Is.EqualTo(1));
            Assert.That(_sdk.Warnings, Has.Some.Contains("Stop did not complete"));
            _sdk.FinishStop(); // late
            Assert.That(stopped, Is.EqualTo(1));
        }

        [Test]
        public void Stop_result_is_true_when_the_sdk_finishes_and_runs_once()
        {
            Launched();
            var results = new List<bool>();
            _machine.Stop(null, results.Add);
            Assert.That(results, Is.Empty);
            _sdk.FinishStop();
            Assert.That(results, Is.EqualTo(new[] { true }));
            Elapse(AndroidLifecycleMachine.HardTimeout);
            Assert.That(results, Is.EqualTo(new[] { true }), "no late deadline answer");
        }

        [Test]
        public void A_stop_that_joins_a_slow_stop_has_its_own_soft_deadline()
        {
            Launched();
            var plain = 0;
            _machine.Stop(() => plain++); // t=0, the SDK never reports
            AdvanceFiringScheduledTicks(TimeSpan.FromSeconds(12));
            Assert.That(plain, Is.EqualTo(1), "the first Stop was answered at 10 s");

            var results = new List<bool>();
            _machine.Stop(null, results.Add); // joins at t=12
            AdvanceFiringScheduledTicks(TimeSpan.FromSeconds(21));
            Assert.That(results, Is.Empty, "not before its own 10 s");
            AdvanceFiringScheduledTicks(TimeSpan.FromSeconds(22.1));
            Assert.That(results, Is.EqualTo(new[] { false }), "answered 10 s after the join, not at the hard deadline");
        }

        [Test]
        public void A_waiter_joined_just_before_the_hard_reset_is_not_told_the_stop_finished()
        {
            Launched();
            _machine.Stop();
            AdvanceFiringScheduledTicks(TimeSpan.FromSeconds(55));
            var results = new List<bool>();
            _machine.Stop(null, results.Add);
            AdvanceFiringScheduledTicks(AndroidLifecycleMachine.HardTimeout + TimeSpan.FromSeconds(1));
            Assert.That(results, Is.EqualTo(new[] { false }));
        }

        [Test]
        public void A_stop_that_joins_just_before_the_first_ones_deadline_still_gets_its_own_10_seconds()
        {
            Launched();
            _machine.Stop(); // t=0
            AdvanceFiringScheduledTicks(TimeSpan.FromSeconds(9));
            var results = new List<bool>();
            _machine.Stop(null, results.Add); // joins at t=9
            AdvanceFiringScheduledTicks(TimeSpan.FromSeconds(10.5));
            Assert.That(results, Is.Empty, "the first stop's deadline does not answer it");
            AdvanceFiringScheduledTicks(TimeSpan.FromSeconds(19.1));
            Assert.That(results, Is.EqualTo(new[] { false }));
        }

        [Test]
        public void Launch_without_a_callback_is_answered_true_at_the_soft_deadline_when_the_sdk_is_launching()
        {
            bool? launched = null;
            var answers = 0;
            _machine.Launch("t1", OptionsA, ok => { launched = ok; answers++; });
            Assert.That(_sdk.Current, Is.EqualTo(BugseeStatus.Launching));
            Elapse(AndroidLifecycleMachine.OperationTimeout);
            Assert.That(launched, Is.True, "the SDK accepted it; only the callback is missing");
            Assert.That(_sdk.Warnings, Has.Some.Contains("reported as started"));
            _sdk.FinishLaunch();
            Assert.That(answers, Is.EqualTo(1), "answered once");
        }

        [Test]
        public void Launch_is_answered_false_at_the_soft_deadline_when_the_sdk_is_not_running()
        {
            bool? launched = null;
            _machine.Launch("t1", OptionsA, ok => launched = ok);
            _sdk.Current = BugseeStatus.Stopped; // e.g. stopped by something else; no callback
            Elapse(AndroidLifecycleMachine.OperationTimeout);
            Assert.That(launched, Is.False);
            Assert.That(_sdk.Warnings, Has.Some.Contains("reported as failed"));
        }

        [Test]
        public void Relaunch_is_answered_true_at_the_soft_deadline_when_its_continuation_is_launching()
        {
            Launched();
            bool? relaunched = null;
            _machine.Relaunch(OptionsB, ok => relaunched = ok);
            _sdk.FinishStop(); // the continuation launch() now runs and never reports
            Assert.That(_sdk.Current, Is.EqualTo(BugseeStatus.Launching));
            Elapse(AndroidLifecycleMachine.OperationTimeout);
            Assert.That(relaunched, Is.True);
        }

        [Test]
        public void Delete_is_always_answered_false_at_the_soft_deadline()
        {
            bool? deleted = null;
            _machine.DeleteCollectedData(ok => deleted = ok);
            _sdk.Current = BugseeStatus.Launched; // irrelevant for a delete
            Elapse(AndroidLifecycleMachine.OperationTimeout);
            Assert.That(deleted, Is.False);
        }

        [Test]
        public void Stop_result_during_a_pending_launch_is_true_when_the_sdk_reports_the_stop_not_false_at_the_deadline()
        {
            Launched();
            _machine.Stop(); // settle: Stopped
            _sdk.FinishStop();
            var launched = new List<bool>();
            var results = new List<bool>();
            _machine.Launch("t2", OptionsA, launched.Add); // callback stays pending
            AdvanceFiringScheduledTicks(TimeSpan.FromSeconds(1));
            _machine.Stop(null, results.Add);
            _sdk.FinishStop(); // the SDK reports the stop; FinishStop is deferred behind the launch
            Assert.That(results, Is.EqualTo(new[] { true }));
            AdvanceFiringScheduledTicks(TimeSpan.FromSeconds(30));
            Assert.That(results, Is.EqualTo(new[] { true }), "no TimeoutException later, no double completion");
        }

        [Test]
        public void Stop_result_during_a_relaunch_is_true_once_in_both_orders()
        {
            // Relaunch first, then Stop.
            Launched();
            var results = new List<bool>();
            _machine.Relaunch(OptionsB);
            _machine.Stop(null, results.Add);
            _sdk.FinishStop(); // joiners (ours), then the relaunch continuation launch()
            Assert.That(results, Is.EqualTo(new[] { true }));
            AdvanceFiringScheduledTicks(TimeSpan.FromSeconds(30));
            _sdk.FinishLaunch(); // resurrected, stopped again
            _sdk.FinishStop();
            Assert.That(results, Is.EqualTo(new[] { true }), "answered once");
            Assert.That(_sdk.Current, Is.EqualTo(BugseeStatus.Stopped));

            // Stop first, then Relaunch (Relaunch is accepted only while Launched, so the SDK is stopping: refused).
            SetUp();
            Launched();
            results.Clear();
            _machine.Stop(null, results.Add);
            _machine.Relaunch(OptionsB);
            _sdk.FinishStop();
            Assert.That(results, Is.EqualTo(new[] { true }));
            AdvanceFiringScheduledTicks(TimeSpan.FromSeconds(30));
            Assert.That(results, Is.EqualTo(new[] { true }));
        }

        [Test]
        public void A_plain_stop_completion_still_waits_for_the_launch_result_while_the_result_is_already_answered()
        {
            Launched();
            _machine.Relaunch(OptionsB);
            var plain = 0;
            var results = new List<bool>();
            _machine.Stop(() => plain++, results.Add);
            _sdk.FinishStop();
            Assert.That(results, Is.EqualTo(new[] { true }));
            Assert.That(plain, Is.EqualTo(0), "the continuation may still launch");
            _sdk.FinishLaunch();
            _sdk.FinishStop();
            Assert.That(plain, Is.EqualTo(1));
            Assert.That(results, Is.EqualTo(new[] { true }));
        }

        [Test]
        public void A_stop_result_that_joins_after_the_sdk_reported_the_stop_is_true_at_once()
        {
            Launched();
            _machine.Stop();
            _sdk.FinishStop();
            var launched = new List<bool>();
            _machine.Launch("t2", OptionsA, launched.Add); // callback stays pending, so FinishStop is not deferred...
            _machine.Stop(); // a Stop intent while the launch is pending; native stop starts
            _sdk.FinishStop(); // reported; FinishStop waits for the launch result
            var results = new List<bool>();
            _machine.Stop(null, results.Add); // joins after NativeDone
            Assert.That(results, Is.EqualTo(new[] { true }), "no 10 s wait");
            var second = new List<bool>();
            _machine.Stop(null, second.Add);
            Assert.That(second, Is.EqualTo(new[] { true }), "a second StopAsync too");
            AdvanceFiringScheduledTicks(TimeSpan.FromSeconds(30));
            Assert.That(results, Is.EqualTo(new[] { true }));
            Assert.That(second, Is.EqualTo(new[] { true }));
        }

        [Test]
        public void A_plain_completion_joining_after_the_sdk_reported_the_stop_still_waits_for_the_launch_result()
        {
            Launched();
            _machine.Relaunch(OptionsB);
            _machine.Stop();
            _sdk.FinishStop(); // reported; the continuation launch() is pending
            var plain = 0;
            var results = new List<bool>();
            _machine.Stop(() => plain++, results.Add);
            Assert.That(results, Is.EqualTo(new[] { true }));
            Assert.That(plain, Is.EqualTo(0));
            _sdk.FinishLaunch();
            _sdk.FinishStop();
            Assert.That(plain, Is.EqualTo(1));
            Assert.That(results, Is.EqualTo(new[] { true }));
        }

        [Test]
        public void Stop_result_is_true_at_once_when_already_stopped()
        {
            var results = new List<bool>();
            _machine.Stop(null, results.Add);
            Assert.That(results, Is.EqualTo(new[] { true }));
        }

        [Test]
        public void Stop_result_is_false_at_the_soft_deadline_and_not_repeated_by_the_late_stop()
        {
            Launched();
            var results = new List<bool>();
            var completions = 0;
            _machine.Stop(() => completions++, results.Add);
            Elapse(AndroidLifecycleMachine.OperationTimeout);
            Assert.That(results, Is.EqualTo(new[] { false }));
            Assert.That(completions, Is.EqualTo(1));
            _sdk.FinishStop();
            Assert.That(results, Is.EqualTo(new[] { false }));
        }

        [Test]
        public void A_launch_after_the_stop_does_not_drop_the_stop_result()
        {
            Launched();
            var results = new List<bool>();
            bool? launched = null;
            _machine.Stop(null, results.Add);
            _machine.Launch("t2", OptionsA, ok => launched = ok); // waits for the stop
            _sdk.FinishStop();
            Assert.That(results, Is.EqualTo(new[] { true }));
            _sdk.FinishLaunch();
            Assert.That(launched, Is.True);
        }

        [Test]
        public void A_joining_stop_result_and_a_plain_stop_both_answer_on_one_native_stop()
        {
            Launched();
            var results = new List<bool>();
            var plain = 0;
            _machine.Stop(() => plain++);
            _machine.Stop(null, results.Add);
            _sdk.FinishStop();
            Assert.That(plain, Is.EqualTo(1));
            Assert.That(results, Is.EqualTo(new[] { true }));
            Assert.That(_sdk.Calls.FindAll(c => c == "stop"), Has.Count.EqualTo(1));
        }

        [Test]
        public void Delete_reports_false_when_its_stop_is_slow_and_is_abandoned_at_the_hard_deadline()
        {
            Launched();
            var answers = new List<bool>();
            _machine.DeleteCollectedData(answers.Add);
            Elapse(AndroidLifecycleMachine.OperationTimeout);
            Assert.That(answers, Is.EqualTo(new[] { false }), "answered at the soft deadline");
            _sdk.FinishStop(); // late, before the hard deadline: the delete still runs
            Assert.That(_sdk.Calls, Does.Contain("delete:Stopped"));
            _sdk.FinishDelete(true);
            Assert.That(answers, Is.EqualTo(new[] { false }), "answered once");

            SetUp();
            Launched();
            answers.Clear();
            _machine.DeleteCollectedData(answers.Add);
            Elapse(AndroidLifecycleMachine.HardTimeout);
            Assert.That(answers, Is.EqualTo(new[] { false }));
            _sdk.FinishStop(); // after the hard reset: nobody runs a delete any more
            Assert.That(_sdk.Calls, Does.Not.Contain("delete:Stopped"));
        }

        // (B) A Launch during a Stop waits for it instead of being refused by the SDK.
        [Test]
        public void Launch_during_a_stop_runs_when_the_stop_completes()
        {
            Launched();
            var order = new List<string>();
            _machine.Stop(() => order.Add("stopped"));
            _machine.Launch("t2", OptionsB, ok => order.Add("launched:" + ok));
            Assert.That(_sdk.Calls, Does.Not.Contain("launch:t2"), "not while Stopping");
            _sdk.FinishStop();
            Assert.That(_sdk.Calls, Does.Contain("launch:t2"));
            _sdk.FinishLaunch();
            Assert.That(order, Is.EqualTo(new[] { "stopped", "launched:True" }));
            Assert.That(_sdk.RunningToken, Is.EqualTo("t2"));
            Assert.That(_sdk.HookOptions, Is.SameAs(OptionsB));
        }

        [Test]
        public void Latest_intent_wins_among_queued_requests()
        {
            Launched();
            var results = new List<string>();
            _machine.Stop();
            _machine.Launch("t2", OptionsA, ok => results.Add("t2:" + ok));
            _machine.Launch("t3", OptionsA, ok => results.Add("t3:" + ok));
            _machine.Relaunch(OptionsB, ok => results.Add("relaunch:" + ok));
            Assert.That(results, Is.EqualTo(new[] { "t2:False" }), "superseded at once");
            _sdk.FinishStop();
            _sdk.FinishLaunch();
            Assert.That(_sdk.Calls, Is.EqualTo(new[] { "stop", "launch:t3" }));
            Assert.That(_sdk.HookOptions, Is.SameAs(OptionsB), "the relaunch's options fold into the queued launch");
            Assert.That(results, Is.EqualTo(new[] { "t2:False", "t3:True", "relaunch:True" }));
        }

        [Test]
        public void Stop_drops_queued_launches_but_not_a_delete()
        {
            Launched();
            bool? launched = null, deleted = null;
            _machine.DeleteCollectedData(ok => deleted = ok);
            _machine.Launch("t2", OptionsA, ok => launched = ok);
            _machine.Stop();
            Assert.That(launched, Is.False);
            _sdk.FinishStop();
            _sdk.FinishDelete(true);
            Assert.That(deleted, Is.True);
            Assert.That(_sdk.Calls, Does.Not.Contain("launch:t2"));
            Assert.That(_sdk.Current, Is.EqualTo(BugseeStatus.Stopped));
        }

        // A Launch never cancels a delete requested before it: the delete runs first, so it cannot wipe the new
        // session and is never silently skipped.
        [Test]
        public void Delete_then_launch_deletes_first()
        {
            Launched();
            bool? deleted = null;
            _machine.DeleteCollectedData(ok => deleted = ok);
            _machine.Launch("t2", OptionsA);
            _sdk.FinishStop();
            Assert.That(_sdk.Calls, Is.EqualTo(new[] { "stop", "delete:Stopped" }));
            _sdk.FinishDelete(true);
            Assert.That(deleted, Is.True);
            Assert.That(_sdk.Calls, Is.EqualTo(new[] { "stop", "delete:Stopped", "launch:t2" }));
        }

        // Review 2, finding 1: Stop during a Relaunch. 7.4.1 stop() only joins the relaunch's stop and the continuation
        // launch()es anyway; the stale result must not re-install the hooks, and the session is stopped again.
        [Test]
        public void Stop_during_relaunch_sticks()
        {
            Launched();
            bool? relaunched = null;
            var stopped = 0;
            var order = new List<string>();
            _machine.Relaunch(OptionsB, ok => { relaunched = ok; order.Add("relaunch"); });
            _machine.Stop(() => { stopped++; order.Add("stop"); });
            Assert.That(_sdk.HooksInstalled, Is.False);

            _sdk.FinishStop(); // joiners (our stop), then the continuation launch()
            Assert.That(stopped, Is.EqualTo(0), "the continuation may still launch");
            _sdk.FinishLaunch(); // the resurrected session reports true
            Assert.That(relaunched, Is.True);
            Assert.That(_sdk.HooksInstalled, Is.False, "a stale result never installs the hooks");
            Assert.That(_sdk.Current, Is.EqualTo(BugseeStatus.Stopping), "stopped again");
            _sdk.FinishStop();
            Assert.That(stopped, Is.EqualTo(1));
            Assert.That(_sdk.Current, Is.EqualTo(BugseeStatus.Stopped));
            Assert.That(order, Is.EqualTo(new[] { "relaunch", "stop" }));
        }

        // Review 3: the relaunch's stop outlives the soft deadline, Stop was requested meanwhile, and the 7.4.1
        // continuation then launch()es anyway. Waiters are answered at the deadline; the late result is still stopped.
        [TestCase(10)]
        [TestCase(61)]  // after the hard reset: the Stop intent is still honoured
        public void Stop_during_a_slow_relaunch_sticks_after_the_deadline(int seconds)
        {
            Launched();
            bool? relaunched = null;
            var stopped = 0;
            _machine.Relaunch(OptionsB, ok => relaunched = ok);
            _machine.Stop(() => stopped++);
            Elapse(TimeSpan.FromSeconds(seconds));
            Assert.That(relaunched, Is.False);
            Assert.That(stopped, Is.EqualTo(1), "the Stop waiter is answered, with a warning");

            _sdk.Calls.Clear();
            _sdk.FinishStop();   // finally stopped; the continuation launch()es: Launching
            _sdk.FinishLaunch(); // late true
            Assert.That(_sdk.Calls, Is.EqualTo(new[] { "stop" }), "the resurrected session is stopped again");
            Assert.That(_sdk.HooksInstalled, Is.False, "never given the hooks");
            _sdk.FinishStop();
            Assert.That(_sdk.Current, Is.EqualTo(BugseeStatus.Stopped));
            Assert.That(relaunched, Is.False, "answered once");
            Assert.That(stopped, Is.EqualTo(1), "answered once");
        }

        // Review 4: the caller launches from the Stop completion, which the soft deadline runs while the relaunch's
        // stop is still going. That Launch must wait for the real stop and the continuation, not start (refused)
        // and clear the Stop intent so the continuation's session is taken as wanted.
        [Test]
        public void Launch_from_a_soft_timed_out_stop_completion_waits_for_the_relaunch_continuation()
        {
            Launched("t1", OptionsA);
            bool? launched = null;
            _machine.Relaunch(OptionsB);
            _machine.Stop(() => _machine.Launch("t2", OptionsA, ok => launched = ok));
            Elapse(AndroidLifecycleMachine.OperationTimeout);
            Assert.That(_sdk.Calls, Does.Not.Contain("launch:t2"), "deferred: the SDK is still stopping");

            _sdk.FinishStop();   // stopped; the continuation launch()es the relaunch
            _sdk.FinishLaunch(); // its true: stale, so stopped again
            Assert.That(_sdk.Calls, Does.Not.Contain("launch:t2"));
            Assert.That(_sdk.HooksInstalled, Is.False);
            _sdk.FinishStop();   // now really stopped: the deferred Launch runs
            Assert.That(_sdk.Calls, Does.Contain("launch:t2"));
            _sdk.FinishLaunch();
            Assert.That(launched, Is.True);
            Assert.That(_sdk.RunningToken, Is.EqualTo("t2"));
            Assert.That(_sdk.HookOptions, Is.SameAs(OptionsA));
        }

        [Test]
        public void Launch_from_a_soft_timed_out_stop_completion_waits_for_the_stop()
        {
            Launched("t1", OptionsA);
            bool? launched = null;
            _machine.Stop(() => _machine.Launch("t2", OptionsA, ok => launched = ok));
            Elapse(AndroidLifecycleMachine.OperationTimeout);
            Assert.That(_sdk.Calls, Does.Not.Contain("launch:t2"), "not into Stopping, where 7.4.1 refuses it");
            _sdk.FinishStop();
            _sdk.FinishLaunch();
            Assert.That(launched, Is.True);
            Assert.That(_sdk.RunningToken, Is.EqualTo("t2"));
        }

        [Test]
        public void Launch_from_a_stop_completion_after_the_hard_reset_does_not_clear_the_stop_intent()
        {
            Launched("t1", OptionsA);
            bool? launched = null;
            _machine.Relaunch(OptionsB);
            _machine.Stop(() => _machine.Launch("t2", OptionsA, ok => launched = ok));
            Elapse(TimeSpan.FromSeconds(61));
            // Hard reset: the Launch is no longer held back, and 7.4.1 refuses it while still Stopping.
            Assert.That(launched, Is.False);

            _sdk.FinishStop();   // the continuation launch()es anyway
            _sdk.FinishLaunch(); // late true after the reset: the Stop intent still stands
            Assert.That(_sdk.Current, Is.EqualTo(BugseeStatus.Stopping), "stopped again");
            Assert.That(_sdk.HooksInstalled, Is.False);
            _sdk.FinishStop();
            Assert.That(_sdk.Current, Is.EqualTo(BugseeStatus.Stopped));
        }

        [Test]
        public void Slow_relaunch_without_a_stop_gets_its_hooks_when_it_finally_launches()
        {
            Launched("t1", OptionsA);
            _machine.Relaunch(OptionsB);
            Elapse(AndroidLifecycleMachine.OperationTimeout);
            _sdk.FinishStop();
            _sdk.FinishLaunch();
            Assert.That(_sdk.Current, Is.EqualTo(BugseeStatus.Launched), "not stopped: no Stop was requested");
            Assert.That(_sdk.HooksInstalled, Is.True);
            Assert.That(_sdk.HookOptions, Is.SameAs(OptionsB));
        }

        // Review 2, finding 2: launch() while already running is a 7.4.1 no-op (true, token ignored). It must not
        // cancel or skip a delete waiting on a relaunch, and the new token must be applied after it.
        [Test]
        public void Launch_during_a_relaunch_does_not_skip_a_waiting_delete()
        {
            Launched();
            bool? deleted = null;
            _machine.Relaunch(OptionsB);
            _machine.DeleteCollectedData(ok => deleted = ok);
            _sdk.FinishStop(); // continuation launch(): Launching
            _machine.Launch("t2", OptionsA);
            Assert.That(_sdk.Calls, Does.Not.Contain("launch:t2"), "not into the relaunch's Launching");
            _sdk.FinishLaunch();
            _sdk.FinishStop(); // the delete's stop
            Assert.That(_sdk.Calls, Does.Contain("delete:Stopped"));
            _sdk.FinishDelete(true);
            Assert.That(deleted, Is.True);
            Assert.That(_sdk.RunningToken, Is.EqualTo("t2"));
        }

        [Test]
        public void Launch_when_already_running_warns_and_keeps_the_running_hooks()
        {
            Launched("t1", OptionsA);
            bool? result = null;
            _machine.Launch("t2", OptionsB, ok => result = ok);
            Assert.That(result, Is.True, "what the SDK reports");
            Assert.That(_sdk.RunningToken, Is.EqualTo("t1"));
            Assert.That(_sdk.HookOptions, Is.SameAs(OptionsA));
            Assert.That(_sdk.Warnings, Has.Some.Contains("already running"));
        }

        [Test]
        public void Launch_joins_a_stop_the_wrapper_did_not_start()
        {
            Launched();
            _sdk.Current = BugseeStatus.Stopping; // e.g. stopped from Java
            _machine.Launch("t2", OptionsA);
            Assert.That(_sdk.Calls, Is.EqualTo(new[] { "stop" }), "joins, does not launch into Stopping");
            _sdk.FinishStop();
            Assert.That(_sdk.Calls, Is.EqualTo(new[] { "stop", "launch:t2" }));
        }

        [Test]
        public void Relaunch_when_not_launched_reports_false_without_calling_the_sdk()
        {
            bool? result = null;
            _machine.Relaunch(OptionsB, ok => result = ok);
            Assert.That(result, Is.False);
            Assert.That(_sdk.Calls, Is.Empty);
        }
    }
}
