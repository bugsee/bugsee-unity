using System;
using System.Collections.Generic;
using Bugsee.Contracts.Options;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class ReportHandlerDeadlineTests
    {
        static IReadOnlyDictionary<string, object> Launch(object seconds) =>
            new Dictionary<string, object> { [Options.ReportHandlerCallbackTimeout] = seconds };

        [Test]
        public void Budget_follows_the_option_in_seconds_and_defaults_to_the_sdk_default()
        {
            Assert.That(ReportHandlerDeadline.BudgetMillis(null, true), Is.EqualTo(30000));
            Assert.That(ReportHandlerDeadline.BudgetMillis(new Dictionary<string, object>(), true), Is.EqualTo(30000));
            Assert.That(ReportHandlerDeadline.BudgetMillis(Launch(5L), true), Is.EqualTo(5000));
            Assert.That(ReportHandlerDeadline.BudgetMillis(Launch(120), true), Is.EqualTo(120000));
            Assert.That(ReportHandlerDeadline.BudgetMillis(Launch("45"), true), Is.EqualTo(45000));
            Assert.That(ReportHandlerDeadline.BudgetMillis(Launch("soon"), true), Is.EqualTo(30000));
        }

        [Test]
        public void A_disabled_timer_still_ends_at_the_sdk_chain_cap()
        {
            Assert.That(ReportHandlerDeadline.BudgetMillis(Launch(0L), true), Is.EqualTo(60000));
            Assert.That(ReportHandlerDeadline.BudgetMillis(Launch(-3L), true), Is.EqualTo(60000));
        }

        [Test]
        public void An_option_the_platform_does_not_read_is_ignored()
        {
            Assert.That(ReportHandlerDeadline.BudgetMillis(Launch(5L), false), Is.EqualTo(30000));
        }

        sealed class Clock { public long Now; }

        static LateReplyGuard NewGuard(Clock clock, List<bool> finished, long budget = 30000) =>
            new LateReplyGuard(budget, () => clock.Now, finished.Add);

        [Test]
        public void A_reply_in_time_applies_the_edits_once()
        {
            var clock = new Clock();
            var finished = new List<bool>();
            var guard = NewGuard(clock, finished);

            Assert.That(guard.TryBegin(), Is.True);
            clock.Now = 29999;
            guard.Done();
            guard.Done();

            Assert.That(finished, Is.EqualTo(new[] { true }));
        }

        [Test]
        public void A_handler_that_would_start_after_the_sdk_moved_on_is_skipped_and_the_report_completes_unchanged()
        {
            var clock = new Clock();
            var finished = new List<bool>();
            var guard = NewGuard(clock, finished);

            clock.Now = 30000; // the Unity loop was paused until now
            Assert.That(guard.TryBegin(), Is.False);
            guard.Done();

            Assert.That(finished, Is.EqualTo(new[] { false }), "completed once, edits not applied");
        }

        [Test]
        public void A_late_reply_is_acknowledged_without_applying_the_edits()
        {
            var clock = new Clock();
            var finished = new List<bool>();
            var guard = NewGuard(clock, finished);

            Assert.That(guard.TryBegin(), Is.True);
            clock.Now = 45000; // the handler answered after the SDK carried on
            guard.Done();

            Assert.That(finished, Is.EqualTo(new[] { false }));
        }

        [Test]
        public void When_applying_the_edits_fails_the_report_still_completes_unchanged_once()
        {
            var calls = new List<bool>();
            var guard = new LateReplyGuard(30000, LateReplyGuard.StopwatchMillis, apply =>
            {
                calls.Add(apply);
                if (apply) throw new InvalidOperationException("the result could not be built");
            });

            Assert.Throws<InvalidOperationException>(() => guard.Done());
            guard.Abandon();

            Assert.That(calls, Is.EqualTo(new[] { true, false }), "the failed apply is followed by an unchanged completion");
        }

        [Test]
        public void A_failing_handler_completes_unchanged_and_a_later_reply_does_nothing()
        {
            var clock = new Clock();
            var finished = new List<bool>();
            var guard = NewGuard(clock, finished);

            guard.Abandon();
            guard.Done();

            Assert.That(finished, Is.EqualTo(new[] { false }));
        }

        [Test]
        public void A_handler_that_never_answers_is_abandoned_once_after_the_deadline_and_a_late_reply_does_nothing()
        {
            var finished = new List<bool>();
            var guard = new LateReplyGuard(30, LateReplyGuard.StopwatchMillis, finished.Add);
            using (var abandoned = new System.Threading.ManualResetEventSlim())
            {
                guard.ArmTimeout(30, run => { run(); abandoned.Set(); });
                Assert.That(abandoned.Wait(5000), Is.True, "the timer fires without a reply");
            }

            guard.Done();
            guard.Abandon();
            Assert.That(finished, Is.EqualTo(new[] { false }), "completed once, edits not applied");
        }

        [Test]
        public void A_reply_stops_the_timer_so_it_cannot_abandon_afterwards()
        {
            var finished = new List<bool>();
            var guard = new LateReplyGuard(1000, LateReplyGuard.StopwatchMillis, finished.Add);
            var marshalled = 0;
            guard.ArmTimeout(0, run => { System.Threading.Interlocked.Increment(ref marshalled); run(); });

            guard.Done();
            System.Threading.Thread.Sleep(2000); // past the timer's due time, so only the cancellation keeps it quiet

            Assert.That(finished, Is.EqualTo(new[] { true }));
            Assert.That(marshalled, Is.EqualTo(0));
        }

        [Test]
        public void Racing_reply_skip_and_abandon_complete_exactly_once()
        {
            for (var round = 0; round < 200; round++)
            {
                var completions = 0;
                var guard = new LateReplyGuard(0, LateReplyGuard.StopwatchMillis,
                    _ => System.Threading.Interlocked.Increment(ref completions));
                guard.ArmTimeout(0, run => run());
                var start = new System.Threading.ManualResetEventSlim();
                var threads = new System.Threading.Thread[4];
                for (var i = 0; i < threads.Length; i++)
                {
                    var which = i;
                    threads[i] = new System.Threading.Thread(() =>
                    {
                        start.Wait();
                        if (which == 0) guard.Done();
                        else if (which == 1) guard.TryBegin();
                        else if (which == 2) guard.Abandon();
                        else guard.Done();
                    });
                    threads[i].Start();
                }
                start.Set();
                foreach (var t in threads) t.Join();
                System.Threading.Thread.Sleep(2);
                Assert.That(completions, Is.EqualTo(1), "round " + round);
            }
        }

        [Test]
        public void A_log_filter_reply_is_wanted_only_inside_the_sdks_window_and_a_filter_without_one_always()
        {
            var clock = new Clock();
            var log = new FilterReplyWindow(FilterReplyWindow.LogFilterSdkTimeoutMs, () => clock.Now);
            var network = new FilterReplyWindow(0, () => clock.Now);

            clock.Now = 9999;
            Assert.That(log.IsOpen, Is.True);
            clock.Now = 10000;
            Assert.That(log.IsOpen, Is.False, "the SDK has dropped the event and may have reused its callback");
            clock.Now = 3600000;
            Assert.That(network.IsOpen, Is.True, "the SDK never times it out: withholding the reply would lose the event");
        }

        [Test]
        public void An_abandoned_handler_is_still_abandoned_when_nothing_refers_to_its_guard()
        {
            var finished = new List<bool>();
            using (var abandoned = new System.Threading.ManualResetEventSlim())
            {
                Arm(finished, abandoned);
                for (var i = 0; i < 3; i++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
                Assert.That(abandoned.Wait(5000), Is.True);
            }

            Assert.That(finished, Is.EqualTo(new[] { false }));
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void Arm(List<bool> finished, System.Threading.ManualResetEventSlim abandoned)
        {
            var guard = new LateReplyGuard(50, LateReplyGuard.StopwatchMillis, finished.Add);
            guard.ArmTimeout(50, run => { run(); abandoned.Set(); });
        }

        // FilterReplyWindow.LogFilterSdkTimeoutMs (10 s) is the Android SDK's own log-filter timeout, read from these releases.
        // A newer SDK may change or drop it; re-read FILTER_CALLBACK_TIMEOUT_MS in its log provider, then add the version here.
        static readonly string[] VerifiedAndroidSdkVersions = { "7.4.1", "7.4.2" };

        [Test]
        public void The_log_filter_window_is_rechecked_when_the_pinned_android_sdk_changes()
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (!string.IsNullOrEmpty(dir) && !System.IO.File.Exists(System.IO.Path.Combine(dir, "package.json")))
                dir = System.IO.Directory.GetParent(dir)?.FullName;
            Assert.That(dir, Is.Not.Null.And.Not.Empty, "repo root");

            var line = System.Linq.Enumerable.First(
                System.IO.File.ReadAllLines(System.IO.Path.Combine(dir, "Tools~", "versions.env")),
                l => l.StartsWith("ANDROID_SDK_VERSION=", StringComparison.Ordinal));
            var version = line.Substring(line.IndexOf('=') + 1).Trim('"');

            Assert.That(VerifiedAndroidSdkVersions, Does.Contain(version),
                "Android SDK " + version + " is pinned. Re-check the log filter timeout (10 s) that FilterReplyWindow.LogFilterSdkTimeoutMs "
                + "mirrors, and whether the network filter now has one, then add the version to VerifiedAndroidSdkVersions.");
        }
    }
}
