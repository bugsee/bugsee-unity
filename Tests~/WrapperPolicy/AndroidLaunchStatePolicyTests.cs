using System;
using System.Collections.Generic;
using Bugsee.Contracts.Lifecycle;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    // Models the Android 7.4.2 SDK (BugseeInternal) where it matters to the wrapper. Every call calls back exactly once:
    // - launch(): Launching/Launched -> no-op, callback(true) inline, token and options ignored; Stopping -> callback(false)
    //   inline; empty token -> callback(false) inline; otherwise -> Launching, callback later (false when a stop() came
    //   meanwhile).
    // - relaunch(): a relaunch in flight (not cancelled; waiting for its launch, or launching) is JOINED: the caller gets
    //   its outcome, true when it launched. Otherwise only when Launched (-> Stopping inside the call, stop, then the
    //   continuation launch()); Launching/Stopping/Stopped -> callback(false) inline. The outcome is false when a stop()
    //   cancelled it, when something launched in the gap, or when its launch failed.
    // - stop(r): a relaunch that is stopping or has stopped and not launched yet is CANCELLED (the SDK stays stopped);
    //   Stopped -> r at once; Stopping -> r joins; otherwise -> Stopping and r when stopped. Joiners run before a
    //   relaunch's continuation. A stop() while the relaunch is launching is a plain stop and the launch calls back false.
    sealed class FakeAndroidSdk : IAndroidLifecycleNative
    {
        sealed class Flow
        {
            public readonly List<Action<bool>> Waiters = new List<Action<bool>>();
            public bool Cancelled, Launching;
        }

        public BugseeStatus Current = BugseeStatus.Stopped;
        public readonly List<string> Calls = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public Action<bool> PendingLaunch;
        public Action<bool> PendingDelete;
        public readonly List<Action> StopJoiners = new List<Action>();
        public bool HooksInstalled;
        public IDictionary<string, object> HookOptions;
        public string RunningToken;
        // Runs inside relaunch() before the SDK checks its status: another thread stopping the SDK in the gap.
        public Action BeforeRelaunchCheck;
        Flow _flow;
        // The gap between a relaunch's stop and its launch half: FinishStop leaves the continuation to RunContinuation.
        public bool HoldContinuation;
        // Errors the machine reports (a throwing callback) are collected instead of thrown.
        public bool CollectErrors;
        public readonly List<Exception> Errors = new List<Exception>();

        public bool RelaunchInFlight => _flow != null;

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
            if (_flow != null && !_flow.Cancelled && (!_flow.Launching || Current == BugseeStatus.Launching))
            {
                _flow.Waiters.Add(done);
                return;
            }
            if (Current != BugseeStatus.Launched) { done(false); return; }
            _flow = new Flow();
            _flow.Waiters.Add(done);
            Current = BugseeStatus.Stopping;
        }

        public void Stop(Action done)
        {
            Calls.Add("stop");
            if (_flow != null && !_flow.Launching && !_flow.Cancelled &&
                (Current == BugseeStatus.Stopping || Current == BugseeStatus.Stopped))
            {
                _flow.Cancelled = true;
                if (Current == BugseeStatus.Stopped) done(); else StopJoiners.Add(done);
                return;
            }
            if (Current == BugseeStatus.Stopped) { done(); return; }
            if (Current == BugseeStatus.Stopping) { StopJoiners.Add(done); return; }
            if (_flow != null) _flow.Cancelled = true;
            Current = BugseeStatus.Stopping;
            StopJoiners.Add(done);
        }

        public readonly List<DeleteScope> DeleteScopes = new List<DeleteScope>();

        public void DeleteCollectedData(DeleteScope scope, Action<bool> done)
        {
            DeleteScopes.Add(scope);
            Calls.Add("delete:" + Current);
            PendingDelete = done;
        }

        public void InstallHooks(IDictionary<string, object> options) { HooksInstalled = true; HookOptions = options; }
        public void UninstallHooks() => HooksInstalled = false;
        public Func<TimeSpan> Clock = () => TimeSpan.Zero;
        public readonly List<TimeSpan> ScheduledTicks = new List<TimeSpan>();
        public void ScheduleTick(TimeSpan delay) => ScheduledTicks.Add(Clock() + delay);
        public void Warn(string message) => Warnings.Add(message);
        public void Error(Exception exception)
        {
            if (!CollectErrors) throw exception;
            Errors.Add(exception);
        }

        // The SDK finishes a stop: STOPPED, joiners, then a relaunch's continuation (launch, or its outcome false).
        public void FinishStop()
        {
            Current = BugseeStatus.Stopped;
            var joiners = StopJoiners.ToArray();
            StopJoiners.Clear();
            foreach (var j in joiners) j();
            if (!HoldContinuation) RunContinuation();
        }

        public void RunContinuation()
        {
            var flow = _flow;
            if (flow == null || flow.Launching) return;
            if (flow.Cancelled || Current != BugseeStatus.Stopped) { EndFlow(flow, false); return; }
            flow.Launching = true;
            Current = BugseeStatus.Launching;
            PendingLaunch = ok => EndFlow(flow, ok);
        }

        void EndFlow(Flow flow, bool ok)
        {
            if (_flow == flow) _flow = null;
            flow.Cancelled = true;
            foreach (var w in flow.Waiters.ToArray()) w(ok);
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

        // relaunch() refused because another thread stopped the SDK between the wrapper's status check and the call:
        // 7.4.2 calls back false at once (7.4.1 never called back), so no deadline is involved and a waiting delete
        // is not held up.
        [Test]
        public void Relaunch_refused_after_a_concurrent_stop_is_answered_false_at_once()
        {
            Launched("t1", OptionsA);
            bool? relaunched = null, deleted = null;
            _sdk.BeforeRelaunchCheck = () => _sdk.Current = BugseeStatus.Stopping;
            _machine.Relaunch(OptionsB, ok => relaunched = ok);
            Assert.That(relaunched, Is.False, "the SDK's refusal, no deadline needed");
            Assert.That(_sdk.Warnings, Has.None.Contains("within 10 s"));
            Assert.That(_sdk.HooksInstalled, Is.False, "the refused relaunch's hooks are rolled back");
            _machine.DeleteCollectedData(ok => deleted = ok);
            _sdk.FinishStop(); // the other thread's stop completes
            Assert.That(_sdk.Calls, Does.Contain("delete:Stopped"));
            _sdk.FinishDelete(true);
            Assert.That(deleted, Is.True);
            Elapse(AndroidLifecycleMachine.HardTimeout);
            Assert.That(relaunched, Is.False, "answered once");
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
            _sdk.FinishStop(); // the relaunch's continuation launch()es: Launching
            _sdk.FinishLaunch();
            Assert.That(relaunched, Is.False, "reported once");
            Assert.That(_sdk.Current, Is.EqualTo(BugseeStatus.Launched));
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
            // Relaunch first, then Stop: the SDK cancels the relaunch and stays stopped.
            Launched();
            var results = new List<bool>();
            bool? relaunched = null;
            _machine.Relaunch(OptionsB, ok => relaunched = ok);
            _machine.Stop(null, results.Add);
            _sdk.FinishStop(); // joiners (ours), then the relaunch's outcome: cancelled
            Assert.That(results, Is.EqualTo(new[] { true }));
            Assert.That(relaunched, Is.False);
            AdvanceFiringScheduledTicks(TimeSpan.FromSeconds(30));
            Assert.That(results, Is.EqualTo(new[] { true }), "answered once");
            Assert.That(_sdk.Current, Is.EqualTo(BugseeStatus.Stopped));
            Assert.That(_sdk.Calls, Is.EqualTo(new[] { "relaunch", "stop" }), "no launch afterwards");

            // Stop first, then Relaunch (the SDK is stopping, so it is refused).
            SetUp();
            Launched();
            results.Clear();
            relaunched = null;
            _machine.Stop(null, results.Add);
            _machine.Relaunch(OptionsB, ok => relaunched = ok);
            _sdk.FinishStop();
            Assert.That(results, Is.EqualTo(new[] { true }));
            Assert.That(relaunched, Is.False);
            AdvanceFiringScheduledTicks(TimeSpan.FromSeconds(30));
            Assert.That(results, Is.EqualTo(new[] { true }));
        }

        [Test]
        public void A_plain_stop_completion_still_waits_for_a_launch_in_flight_to_report()
        {
            Launched();
            _machine.Stop();
            _sdk.FinishStop();
            bool? launched = null;
            _machine.Launch("t2", OptionsA, ok => launched = ok); // Launching, callback pending
            var plain = 0;
            var results = new List<bool>();
            _machine.Stop(() => plain++, results.Add);
            _sdk.FinishStop();
            Assert.That(results, Is.EqualTo(new[] { true }));
            Assert.That(plain, Is.EqualTo(0), "the launch has not reported yet");
            _sdk.FinishLaunch(); // the SDK was stopped meanwhile: the launch reports false
            Assert.That(launched, Is.False);
            Assert.That(plain, Is.EqualTo(1));
            Assert.That(results, Is.EqualTo(new[] { true }));
            Assert.That(_sdk.Current, Is.EqualTo(BugseeStatus.Stopped));
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
            _machine.Stop();
            _sdk.FinishStop();
            _machine.Launch("t2", OptionsA); // Launching, callback pending
            _machine.Stop();
            _sdk.FinishStop(); // reported; the launch result is pending
            var plain = 0;
            var results = new List<bool>();
            _machine.Stop(() => plain++, results.Add);
            Assert.That(results, Is.EqualTo(new[] { true }));
            Assert.That(plain, Is.EqualTo(0));
            _sdk.FinishLaunch();
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

        // Stop during a Relaunch's stop: 7.4.2 CANCELS the relaunch (7.4.1 launched again afterwards), so the SDK stays
        // stopped, the relaunch calls back false, nothing is stopped a second time, and the hooks stay off.
        [Test]
        public void Stop_during_relaunch_cancels_it()
        {
            Launched();
            bool? relaunched = null;
            var stopped = 0;
            var order = new List<string>();
            _machine.Relaunch(OptionsB, ok => { relaunched = ok; order.Add("relaunch"); });
            _machine.Stop(() => { stopped++; order.Add("stop"); });
            Assert.That(_sdk.HooksInstalled, Is.False);

            _sdk.FinishStop(); // joiners (our stop), then the relaunch's outcome
            Assert.That(relaunched, Is.False, "cancelled by the stop");
            Assert.That(stopped, Is.EqualTo(1));
            Assert.That(_sdk.HooksInstalled, Is.False, "a stale result never installs the hooks");
            Assert.That(_sdk.Current, Is.EqualTo(BugseeStatus.Stopped));
            Assert.That(_sdk.Calls, Is.EqualTo(new[] { "relaunch", "stop" }), "no launch, no second stop");
            Assert.That(order, Is.EqualTo(new[] { "relaunch", "stop" }));
        }

        // Stop while the relaunch is already launching is a plain stop: the launch reports false, the SDK stays stopped.
        [Test]
        public void Stop_during_the_launch_half_of_a_relaunch_is_a_plain_stop()
        {
            Launched();
            bool? relaunched = null;
            var stopped = 0;
            _machine.Relaunch(OptionsB, ok => relaunched = ok);
            _sdk.FinishStop(); // the continuation launch()es: Launching
            Assert.That(_sdk.Current, Is.EqualTo(BugseeStatus.Launching));
            _machine.Stop(() => stopped++);
            Assert.That(_sdk.Current, Is.EqualTo(BugseeStatus.Stopping));
            _sdk.FinishLaunch(); // reports false: stopped meanwhile
            Assert.That(relaunched, Is.False);
            _sdk.FinishStop();
            Assert.That(stopped, Is.EqualTo(1));
            Assert.That(_sdk.Current, Is.EqualTo(BugseeStatus.Stopped));
            Assert.That(_sdk.HooksInstalled, Is.False);
        }

        // The relaunch's stop outlives the soft deadline and Stop was requested meanwhile. Waiters are answered at the
        // deadline; the SDK, finally stopped, stays stopped (it cancelled the relaunch).
        [TestCase(10)]
        [TestCase(61)]  // after the hard reset
        public void Stop_during_a_slow_relaunch_stays_stopped_after_the_deadline(int seconds)
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
            _sdk.FinishStop();   // finally stopped; the relaunch was cancelled
            Assert.That(_sdk.Calls, Is.Empty, "nothing is launched or stopped again");
            Assert.That(_sdk.HooksInstalled, Is.False, "never given the hooks");
            Assert.That(_sdk.Current, Is.EqualTo(BugseeStatus.Stopped));
            Assert.That(relaunched, Is.False, "answered once");
            Assert.That(stopped, Is.EqualTo(1), "answered once");
        }

        // The caller launches from the Stop completion, which the soft deadline runs while the relaunch's stop is
        // still going. That Launch must wait for the real stop and the relaunch's outcome, not start (and be refused).
        [Test]
        public void Launch_from_a_soft_timed_out_stop_completion_waits_for_the_relaunch_outcome()
        {
            Launched("t1", OptionsA);
            bool? launched = null;
            _machine.Relaunch(OptionsB);
            _machine.Stop(() => _machine.Launch("t2", OptionsA, ok => launched = ok));
            Elapse(AndroidLifecycleMachine.OperationTimeout);
            Assert.That(_sdk.Calls, Does.Not.Contain("launch:t2"), "deferred: the SDK is still stopping");

            _sdk.FinishStop();   // stopped; the relaunch is cancelled; the deferred Launch runs
            Assert.That(_sdk.Calls, Does.Contain("launch:t2"));
            _sdk.FinishLaunch();
            Assert.That(launched, Is.True);
            Assert.That(_sdk.RunningToken, Is.EqualTo("t2"));
            Assert.That(_sdk.HookOptions, Is.SameAs(OptionsA));
            Assert.That(_sdk.HooksInstalled, Is.True);
        }

        [Test]
        public void Launch_from_a_soft_timed_out_stop_completion_waits_for_the_stop()
        {
            Launched("t1", OptionsA);
            bool? launched = null;
            _machine.Stop(() => _machine.Launch("t2", OptionsA, ok => launched = ok));
            Elapse(AndroidLifecycleMachine.OperationTimeout);
            Assert.That(_sdk.Calls, Does.Not.Contain("launch:t2"), "not into Stopping, where the SDK refuses it");
            _sdk.FinishStop();
            _sdk.FinishLaunch();
            Assert.That(launched, Is.True);
            Assert.That(_sdk.RunningToken, Is.EqualTo("t2"));
        }

        [Test]
        public void Launch_from_a_stop_completion_after_the_hard_reset_is_refused_by_the_still_stopping_sdk()
        {
            Launched("t1", OptionsA);
            bool? launched = null;
            _machine.Relaunch(OptionsB);
            _machine.Stop(() => _machine.Launch("t2", OptionsA, ok => launched = ok));
            Elapse(TimeSpan.FromSeconds(61));
            // Hard reset: the Launch is no longer held back, and the SDK refuses it while still Stopping.
            Assert.That(launched, Is.False);

            _sdk.FinishStop();   // the relaunch was cancelled; its late outcome is ignored
            Assert.That(_sdk.Current, Is.EqualTo(BugseeStatus.Stopped));
            Assert.That(_sdk.HooksInstalled, Is.False);
        }

        // A second Relaunch while one is in flight JOINS it (as the SDK does): one native relaunch, the joiner's options
        // are ignored with a warning, and both callers get the outcome.
        [Test]
        public void A_second_relaunch_joins_the_one_in_flight_and_its_options_are_ignored()
        {
            Launched("t1", OptionsA);
            bool? first = null, second = null;
            _machine.Relaunch(OptionsB, ok => first = ok);
            _machine.Relaunch(new Dictionary<string, object> { ["c"] = 3 }, ok => second = ok);
            Assert.That(_sdk.Calls, Is.EqualTo(new[] { "relaunch" }), "one native relaunch");
            Assert.That(_sdk.Warnings, Has.Some.Contains("IGNORED"));
            Assert.That(first, Is.Null);
            Assert.That(second, Is.Null);
            _sdk.FinishStop();
            _sdk.FinishLaunch();
            Assert.That(first, Is.True);
            Assert.That(second, Is.True);
            Assert.That(_sdk.HookOptions, Is.SameAs(OptionsB), "the first call's options apply");
        }

        [Test]
        public void A_joined_relaunch_gets_a_cancelled_outcome_too()
        {
            Launched();
            bool? first = null, second = null;
            _machine.Relaunch(OptionsB, ok => first = ok);
            _machine.Relaunch(null, ok => second = ok);
            Assert.That(_sdk.Warnings, Has.None.Contains("IGNORED"), "no options, nothing ignored");
            _machine.Stop();
            _sdk.FinishStop();
            Assert.That(first, Is.False);
            Assert.That(second, Is.False);
        }

        [Test]
        public void A_relaunch_after_a_stop_does_not_join_the_cancelled_one()
        {
            Launched();
            bool? first = null, second = null;
            _machine.Relaunch(OptionsB, ok => first = ok);
            _machine.Stop();
            _machine.Relaunch(OptionsA, ok => second = ok);
            _sdk.FinishStop();
            Assert.That(first, Is.False);
            Assert.That(second, Is.False, "refused: the SDK stayed stopped");
            Assert.That(_sdk.Calls, Is.EqualTo(new[] { "relaunch", "stop" }), "the second one never reached the SDK");
        }

        [Test]
        public void A_relaunch_that_joins_after_the_soft_deadline_is_answered_at_once()
        {
            Launched();
            _machine.Relaunch(OptionsB);
            Elapse(AndroidLifecycleMachine.OperationTimeout);
            bool? second = null;
            _machine.Relaunch(null, ok => second = ok);
            Assert.That(second, Is.False, "the soft deadline's answer: the SDK is Stopping, not running");
        }

        // A Launch requested during a relaunch's stop waits for it (the SDK would refuse it while Stopping); the
        // relaunch's session is running afterwards, so the Launch is the documented already-running no-op.
        [Test]
        public void Launch_during_a_relaunchs_stop_waits_and_then_finds_the_relaunched_session_running()
        {
            Launched("t1", OptionsA);
            bool? relaunched = null, launched = null;
            _machine.Relaunch(OptionsB, ok => relaunched = ok);
            _machine.Launch("t2", OptionsA, ok => launched = ok);
            Assert.That(_sdk.Calls, Is.EqualTo(new[] { "relaunch" }));
            _sdk.FinishStop();
            _sdk.FinishLaunch();
            Assert.That(relaunched, Is.True);
            Assert.That(launched, Is.True);
            Assert.That(_sdk.RunningToken, Is.EqualTo("t1"), "the relaunch's session; the Launch's token is ignored");
            Assert.That(_sdk.Warnings, Has.Some.Contains("already running"));
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

        // Review 2, finding 2: launch() while already running is a no-op (true, token ignored). It must not
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

        // A Delete queued behind a relaunch runs after it and before a later Relaunch, which therefore must not join.
        [Test]
        public void Relaunch_after_a_queued_delete_does_not_join_the_relaunch_in_flight()
        {
            Launched();
            bool? first = null, second = null, deleted = null;
            _machine.Relaunch(OptionsB, ok => first = ok);
            _machine.DeleteCollectedData(ok => deleted = ok);
            _machine.Relaunch(OptionsA, ok => second = ok);
            Assert.That(_sdk.Calls, Is.EqualTo(new[] { "relaunch" }));
            _sdk.FinishStop();
            _sdk.FinishLaunch();
            Assert.That(first, Is.True);
            Assert.That(second, Is.Null, "waits behind the delete");
            _sdk.FinishStop(); // the delete's stop
            _sdk.FinishDelete(true);
            Assert.That(deleted, Is.True);
            Assert.That(second, Is.False, "the SDK is stopped after the delete: refused, as before");
            Assert.That(_sdk.Calls, Is.EqualTo(new[] { "relaunch", "stop", "delete:Stopped" }));
        }

        [Test]
        public void A_relaunch_joins_while_the_relaunch_is_launching()
        {
            Launched();
            bool? first = null, second = null;
            _machine.Relaunch(OptionsB, ok => first = ok);
            _sdk.FinishStop(); // Launching
            _machine.Relaunch(OptionsA, ok => second = ok);
            Assert.That(_sdk.Calls, Is.EqualTo(new[] { "relaunch" }));
            _sdk.FinishLaunch();
            Assert.That(first, Is.True);
            Assert.That(second, Is.True);
        }

        [Test]
        public void Stop_in_the_gap_between_the_relaunchs_stop_and_its_launch_cancels_it()
        {
            Launched();
            _sdk.HoldContinuation = true;
            bool? relaunched = null;
            var stopped = 0;
            _machine.Relaunch(OptionsB, ok => relaunched = ok);
            _sdk.FinishStop(); // Stopped, the launch half has not run
            Assert.That(_sdk.Current, Is.EqualTo(BugseeStatus.Stopped));
            _machine.Stop(() => stopped++); // cancels the relaunch; the SDK is stopped, so it answers at once
            Assert.That(stopped, Is.EqualTo(0), "the machine still waits for the relaunch's result");
            _sdk.RunContinuation();
            Assert.That(relaunched, Is.False);
            Assert.That(stopped, Is.EqualTo(1));
            Assert.That(_sdk.Current, Is.EqualTo(BugseeStatus.Stopped));
            Assert.That(_sdk.HooksInstalled, Is.False);
        }

        // The relaunch is abandoned at the hard reset with a Launch queued behind it: the Launch joins the SDK's stop
        // (status Stopping), then launches, and the SDK skips the relaunch's launch half (superseded by that launch);
        // the relaunch's late false is ignored.
        [Test]
        public void Hard_reset_of_a_relaunch_lets_the_queued_launch_run_after_the_sdks_stop()
        {
            Launched("t1", OptionsA);
            bool? relaunched = null, launched = null;
            _machine.Relaunch(OptionsB, ok => relaunched = ok);
            _machine.Launch("t2", OptionsA, ok => launched = ok);
            Assert.That(_sdk.Calls, Is.EqualTo(new[] { "relaunch" }));
            Elapse(AndroidLifecycleMachine.HardTimeout);
            Assert.That(relaunched, Is.False);
            Assert.That(_sdk.Calls, Is.EqualTo(new[] { "relaunch", "stop" }), "joins the SDK's stop, no launch into Stopping");
            _sdk.FinishStop();   // the stop ends: the Launch runs, then the relaunch's launch half finds it launched
            Assert.That(_sdk.Calls, Does.Contain("launch:t2"));
            _sdk.FinishLaunch();
            Assert.That(launched, Is.True);
            Assert.That(relaunched, Is.False, "answered once");
            Assert.That(_sdk.RunningToken, Is.EqualTo("t2"));
            Assert.That(_sdk.HooksInstalled, Is.True);
        }

        [Test]
        public void A_throwing_completion_does_not_stop_the_others_or_the_machine()
        {
            Launched();
            _sdk.CollectErrors = true;
            bool? second = null;
            _machine.Relaunch(OptionsB, ok => throw new InvalidOperationException("boom"));
            _machine.Relaunch(null, ok => second = ok); // joins
            _sdk.FinishStop();
            _sdk.FinishLaunch();
            Assert.That(second, Is.True);
            Assert.That(_sdk.Errors, Has.Count.EqualTo(1));
            bool? next = null;
            _machine.Relaunch(OptionsA, ok => next = ok);
            _sdk.FinishStop();
            _sdk.FinishLaunch();
            Assert.That(next, Is.True, "the machine carries on");
        }

        [Test]
        public void The_joined_callback_runs_before_the_completion_and_only_for_a_join()
        {
            Launched();
            var order = new List<string>();
            _machine.Relaunch(OptionsB, ok => order.Add("first"), () => order.Add("first-joined"));
            _machine.Relaunch(OptionsA, ok => order.Add("second"), () => order.Add("second-joined"));
            _sdk.FinishStop();
            _sdk.FinishLaunch();
            Assert.That(order, Is.EqualTo(new[] { "second-joined", "first", "second" }));
        }

        [Test]
        public void Relaunch_when_not_launched_reports_false_without_calling_the_sdk()
        {
            bool? result = null;
            _machine.Relaunch(OptionsB, ok => result = ok);
            Assert.That(result, Is.False);
            Assert.That(_sdk.Calls, Is.Empty);
        }

        [Test]
        public void Delete_passes_its_scope_to_the_sdk_and_answers_with_its_result()
        {
            foreach (var scope in new[] { DeleteScope.CaptureOnly, DeleteScope.CaptureAndIntermediate, DeleteScope.Everything })
            {
                SetUp();
                bool? deleted = null;
                _machine.DeleteCollectedData(scope, ok => deleted = ok);
                Assert.That(_sdk.DeleteScopes, Is.EqualTo(new[] { scope }));
                Assert.That(deleted, Is.Null);
                _sdk.FinishDelete(scope != DeleteScope.CaptureOnly);
                Assert.That(deleted, Is.EqualTo(scope != DeleteScope.CaptureOnly));
            }
        }

        [Test]
        public void Delete_without_a_scope_is_everything()
        {
            _machine.DeleteCollectedData(ok => { });
            Assert.That(_sdk.DeleteScopes, Is.EqualTo(new[] { DeleteScope.Everything }));
        }

        [Test]
        public void Queued_deletes_of_one_scope_share_a_wipe_and_of_two_scopes_run_one_after_the_other()
        {
            Launched();
            var answers = new List<string>();
            _machine.DeleteCollectedData(DeleteScope.CaptureOnly, ok => answers.Add("a:" + ok)); // active, stopping first
            _machine.DeleteCollectedData(DeleteScope.Everything, ok => answers.Add("b:" + ok));
            _machine.DeleteCollectedData(DeleteScope.Everything, ok => answers.Add("c:" + ok));
            _machine.DeleteCollectedData(DeleteScope.CaptureOnly, ok => answers.Add("d:" + ok));
            _sdk.FinishStop();

            Assert.That(_sdk.DeleteScopes, Is.EqualTo(new[] { DeleteScope.CaptureOnly }), "the wider one waits its turn");
            _sdk.FinishDelete(true);
            Assert.That(answers, Is.EqualTo(new[] { "a:True" }));
            Assert.That(_sdk.DeleteScopes, Is.EqualTo(new[] { DeleteScope.CaptureOnly, DeleteScope.Everything }));
            _sdk.FinishDelete(false);
            Assert.That(answers, Is.EqualTo(new[] { "a:True", "b:False", "c:False" }), "b and c are one wipe");
            Assert.That(_sdk.DeleteScopes.Count, Is.EqualTo(3));
            _sdk.FinishDelete(true);
            Assert.That(answers[3], Is.EqualTo("d:True"));
        }

        [Test]
        public void A_narrow_delete_after_a_launch_wipes_that_session_with_its_own_scope()
        {
            // Delete(Everything); Launch; Delete(CaptureAndIntermediate): request order, each with its own scope.
            Launched();
            var answers = new List<string>();
            _machine.DeleteCollectedData(DeleteScope.Everything, ok => answers.Add("first:" + ok));
            _machine.Launch("t2", OptionsA);
            _machine.DeleteCollectedData(DeleteScope.CaptureAndIntermediate, ok => answers.Add("second:" + ok));
            _sdk.FinishStop();
            _sdk.FinishDelete(true);
            Assert.That(answers, Is.EqualTo(new[] { "first:True" }));
            Assert.That(_sdk.Calls, Does.Contain("launch:t2"));
            _sdk.FinishLaunch();
            _sdk.FinishStop();
            _sdk.FinishDelete(true);

            Assert.That(_sdk.DeleteScopes, Is.EqualTo(new[] { DeleteScope.Everything, DeleteScope.CaptureAndIntermediate }));
            Assert.That(answers, Is.EqualTo(new[] { "first:True", "second:True" }));
        }

        [Test]
        public void A_scoped_delete_is_answered_false_at_the_soft_deadline_and_a_late_result_is_ignored()
        {
            bool? deleted = null;
            _machine.DeleteCollectedData(DeleteScope.CaptureOnly, ok => deleted = ok);
            Elapse(AndroidLifecycleMachine.OperationTimeout);
            Assert.That(deleted, Is.False);
            _sdk.FinishDelete(true);
            Assert.That(deleted, Is.False, "answered once");
        }
    }
}
