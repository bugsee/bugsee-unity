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
    }
}
