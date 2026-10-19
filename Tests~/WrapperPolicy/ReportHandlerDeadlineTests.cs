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
            var guard = new LateReplyGuard(20, LateReplyGuard.StopwatchMillis, finished.Add);
            var marshalled = 0;
            guard.ArmTimeout(20, run => { System.Threading.Interlocked.Increment(ref marshalled); run(); });

            guard.Done();
            System.Threading.Thread.Sleep(300);

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
    }
}
