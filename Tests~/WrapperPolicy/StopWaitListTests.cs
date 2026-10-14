using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class StopWaitListTests
    {
        static void Run(Action waiter) => waiter();

        static bool TryJoin(StopWaitList list, Action waiter, int generation, Action dropped = null)
        {
            var joined = list.TryJoin(waiter, generation, out var cancelled, dropped);
            cancelled?.Invoke(false);
            return joined;
        }

        static bool TryDeferLaunch(StopWaitList list, Action<bool> launch)
        {
            var deferred = list.TryDeferLaunch(launch, out var cancelled);
            cancelled?.Invoke(false);
            return deferred;
        }

        [Test]
        public void Nothing_to_join_before_a_stop_is_in_flight()
        {
            var list = new StopWaitList();
            Assert.That(TryJoin(list, () => { }, 0), Is.False);
        }

        [Test]
        public void A_second_stop_joins_the_one_in_flight_and_both_run_on_its_report()
        {
            var list = new StopWaitList();
            var ran = new List<string>();
            var token = list.Begin(() => ran.Add("first"), 1);

            Assert.That(TryJoin(list, () => ran.Add("second"), 1), Is.True);
            Assert.That(TryJoin(list, null, 1), Is.True, "a Stop without a completion still must not issue another native stop");

            Assert.That(list.Complete(token, () => 1, Run), Is.False);
            Assert.That(ran, Is.EqualTo(new[] { "first", "second" }));
        }

        [Test]
        public void After_the_report_the_next_stop_issues_its_own()
        {
            var list = new StopWaitList();
            var token = list.Begin(null, 1);
            list.Complete(token, () => 1, Run);
            Assert.That(TryJoin(list, () => { }, 1), Is.False);
        }

        [Test]
        public void A_stop_issued_before_a_launch_is_not_joined_after_it()
        {
            var list = new StopWaitList();
            list.Begin(null, 1);
            Assert.That(TryJoin(list, () => { }, 2), Is.False);
        }

        [Test]
        public void Waiters_are_dropped_when_a_launch_happened_before_the_report()
        {
            var list = new StopWaitList();
            var ran = false;
            var token = list.Begin(() => ran = true, 1);

            Assert.That(list.Complete(token, () => 2, Run), Is.True);
            Assert.That(ran, Is.False);
        }

        [Test]
        public void A_launch_made_by_an_earlier_waiter_drops_the_later_ones()
        {
            // Stop(() => Launch(token)); Stop(completion); -- the second completion belongs to the session that
            // stopped and must not act on the one the first just launched.
            var list = new StopWaitList();
            var generation = 1;
            var ran = new List<string>();
            var token = list.Begin(() => { ran.Add("launch"); generation++; }, 1);
            Assert.That(TryJoin(list, () => ran.Add("second"), 1), Is.True);

            Assert.That(list.Complete(token, () => generation, Run), Is.True);
            Assert.That(ran, Is.EqualTo(new[] { "launch" }));
        }

        [Test]
        public void A_joined_delete_runs_ahead_of_a_completion_that_launches()
        {
            // Stop(() => Launch(token)); DeleteCollectedDataOnDevice(); -- the Launch is a later request than the
            // Delete, so the wipe runs first and is not skipped, as on Android.
            var list = new StopWaitList();
            var generation = 1;
            var ran = new List<string>();
            var token = list.Begin(() => { ran.Add("launch"); generation++; }, 1);
            Assert.That(list.TryJoin(() => ran.Add("delete"), 1, out var cancelled, null, first: true), Is.True);
            Assert.That(cancelled, Is.Null);

            Assert.That(list.Complete(token, () => generation, Run), Is.False);
            Assert.That(ran, Is.EqualTo(new[] { "delete", "launch" }));
        }

        [Test]
        public void A_delete_is_not_dropped_when_a_launch_happened_before_the_report()
        {
            var list = new StopWaitList();
            var ran = false;
            var token = list.Begin(() => ran = true, 1, null, first: true);

            Assert.That(list.Complete(token, () => 2, Run), Is.False);
            Assert.That(ran, Is.True);
        }

        // Native side of a delete, as the bridge does it: a waiter of the stop begins a wipe in flight.
        static int BeginWipe(StopWaitList list) => list.Begin(null, 1, null, holdsLaunch: true);

        [Test]
        public void A_launch_after_a_delete_waits_for_the_wipe_to_be_reported_not_the_stop()
        {
            // Delete(); Launch(); with a stop in flight: the launch runs after the wipe has finished, which the
            // SDK reports later than the stop (it clears its files on a background thread).
            var list = new StopWaitList();
            var ran = new List<string>();
            var wipe = 0;
            var stop = list.Begin(() => { ran.Add("wipe starts"); wipe = BeginWipe(list); }, 1, null, first: true);
            Assert.That(TryDeferLaunch(list, run => ran.Add(run ? "launch" : "cancelled")), Is.True);

            list.Complete(stop, () => 1, Run);
            Assert.That(ran, Is.EqualTo(new[] { "wipe starts" }), "the stop's report must not release the launch");

            list.Complete(wipe, () => 1, Run);
            Assert.That(ran, Is.EqualTo(new[] { "wipe starts", "launch" }));
        }

        [Test]
        public void A_launch_after_a_delete_on_a_stopped_sdk_waits_for_the_wipe()
        {
            var list = new StopWaitList();
            var ran = new List<string>();
            var wipe = BeginWipe(list);

            Assert.That(TryDeferLaunch(list, run => ran.Add(run ? "launch" : "cancelled")), Is.True);
            Assert.That(ran, Is.Empty);
            list.Complete(wipe, () => 1, Run);
            Assert.That(ran, Is.EqualTo(new[] { "launch" }));
        }

        // What BeginNativeDelete does: no second wipe while one is in flight.
        static int BeginWipeOnce(StopWaitList list, List<string> ran)
        {
            if (list.TryCoalesceWipe()) { ran.Add("coalesced"); return 0; }
            ran.Add("wipe");
            return BeginWipe(list);
        }

        [Test]
        public void Two_deletes_sharing_a_stop_start_one_wipe_and_the_launch_waits_for_it()
        {
            // Delete(); Delete(); Launch(); while the SDK runs.
            var list = new StopWaitList();
            var ran = new List<string>();
            var wipe = 0;
            var stop = list.Begin(() => wipe = BeginWipeOnce(list, ran), 1, null, first: true);
            Assert.That(list.TryJoin(() => BeginWipeOnce(list, ran), 1, out _, null, first: true), Is.True);
            TryDeferLaunch(list, run => ran.Add(run ? "launch" : "cancelled"));

            list.Complete(stop, () => 1, Run);
            Assert.That(ran, Is.EqualTo(new[] { "wipe", "coalesced" }), "launch stays held");
            list.Complete(wipe, () => 1, Run);
            Assert.That(ran, Is.EqualTo(new[] { "wipe", "coalesced", "launch" }));
        }

        [Test]
        public void Without_coalescing_two_first_waiters_would_release_the_launch_at_the_later_wipe()
        {
            var list = new StopWaitList();
            var ran = new List<string>();
            int w1 = 0, w2 = 0;
            var stop = list.Begin(() => w1 = BeginWipe(list), 1, null, first: true);
            list.TryJoin(() => w2 = BeginWipe(list), 1, out _, null, first: true);
            TryDeferLaunch(list, run => ran.Add(run ? "launch" : "cancelled"));
            list.Complete(stop, () => 1, Run);

            list.Complete(w2, () => 1, Run);
            Assert.That(ran, Is.EqualTo(new[] { "launch" }), "the launch ran while the first wipe was still going");
            Assert.That(w1, Is.Not.EqualTo(w2));
        }

        [Test]
        public void A_delete_on_a_stopped_sdk_right_after_a_delete_coalesces_into_its_wipe()
        {
            // Already stopped: the first delete begins its wipe at once; the second finds it in flight.
            var list = new StopWaitList();
            var ran = new List<string>();
            var wipe = BeginWipeOnce(list, ran);
            Assert.That(BeginWipeOnce(list, ran), Is.EqualTo(0));
            TryDeferLaunch(list, run => ran.Add(run ? "launch" : "cancelled"));

            list.Complete(wipe, () => 1, Run);
            Assert.That(ran, Is.EqualTo(new[] { "wipe", "coalesced", "launch" }));
        }

        [Test]
        public void A_delete_after_a_deferred_launch_does_not_coalesce_and_cancels_it()
        {
            // Delete(); Launch(); Delete(); -- the last delete is newer than the launch.
            var list = new StopWaitList();
            var ran = new List<string>();
            var wipe = BeginWipe(list);
            TryDeferLaunch(list, run => ran.Add(run ? "launch" : "cancelled"));

            Assert.That(list.TryCoalesceWipe(), Is.False);
            Assert.That(list.TryJoin(() => ran.Add("second wipe"), 1, out var cancelled, null, first: true), Is.True);
            cancelled?.Invoke(false);
            list.Complete(wipe, () => 1, Run);
            Assert.That(ran, Is.EqualTo(new[] { "cancelled", "second wipe" }));
        }

        [Test]
        public void A_stop_in_flight_is_not_a_wipe_to_coalesce_into()
        {
            var list = new StopWaitList();
            list.Begin(() => { }, 1);
            Assert.That(list.TryCoalesceWipe(), Is.False);
        }

        [Test]
        public void A_newer_request_still_cancels_a_launch_held_behind_a_wipe()
        {
            var list = new StopWaitList();
            var ran = new List<string>();
            var wipe = BeginWipe(list);
            TryDeferLaunch(list, run => ran.Add(run ? "launch" : "cancelled"));
            TryDeferLaunch(list, run => ran.Add(run ? "launch2" : "cancelled2"));

            list.Complete(wipe, () => 1, Run);
            Assert.That(ran, Is.EqualTo(new[] { "cancelled", "launch2" }));
        }

        [Test]
        public void A_wipe_the_bridge_gives_up_on_still_releases_the_launch()
        {
            var list = new StopWaitList();
            var ran = new List<string>();
            var wipe = BeginWipe(list);
            TryDeferLaunch(list, run => ran.Add(run ? "launch" : "cancelled"));

            list.Complete(wipe, () => 1, Run); // the deadline calls Complete too
            list.Complete(wipe, () => 1, Run); // a late report is ignored
            Assert.That(ran, Is.EqualTo(new[] { "launch" }));
        }

        [Test]
        public void A_dropped_waiter_runs_its_dropped_action_so_nothing_waits_forever()
        {
            var list = new StopWaitList();
            var generation = 1;
            var ran = new List<string>();
            var token = list.Begin(() => generation++, 1);
            TryJoin(list, () => ran.Add("ran"), 1, () => ran.Add("dropped"));

            list.Complete(token, () => generation, Run);
            Assert.That(ran, Is.EqualTo(new[] { "dropped" }));
        }

        [Test]
        public void A_waiter_can_issue_the_next_stop()
        {
            var list = new StopWaitList();
            var next = 0;
            var token = list.Begin(() => next = list.Begin(null, 1), 1);
            list.Complete(token, () => 1, Run);
            Assert.That(list.IsPending(next), Is.True);
            Assert.That(TryJoin(list, () => { }, 1), Is.True);
        }

        [Test]
        public void A_report_runs_its_waiters_once()
        {
            var list = new StopWaitList();
            var count = 0;
            var token = list.Begin(() => count++, 1);
            list.Complete(token, () => 1, Run);
            list.Complete(token, () => 1, Run);
            Assert.That(count, Is.EqualTo(1));
            Assert.That(list.IsPending(token), Is.False);
        }

        [Test]
        public void Waiters_stay_queued_until_reported()
        {
            var list = new StopWaitList();
            var token = list.Begin(() => { }, 1);
            Assert.That(list.IsPending(token), Is.True);
            Assert.That(TryJoin(list, () => { }, 1), Is.True);
        }

        [Test]
        public void A_late_report_of_an_older_stop_does_not_release_the_newer_one()
        {
            var list = new StopWaitList();
            var older = list.Begin(null, 1);
            var newer = list.Begin(null, 2);
            list.Complete(older, () => 2, Run);
            Assert.That(TryJoin(list, () => { }, 2), Is.True);
            Assert.That(list.IsPending(newer), Is.True);
        }

        // The soft deadline runs a Stop's own completion (held outside the list, see OneShotCallbacks) and leaves
        // the stop in flight: a Launch that completion makes, or any later one, still waits, and a further Stop
        // joins instead of issuing a second native stop.
        [Test]
        public void Before_the_hard_deadline_the_stop_stays_in_flight_for_launches_and_stops()
        {
            var list = new StopWaitList();
            var ran = new List<string>();
            var token = list.Begin(null, 1);

            // What a completion run at the soft deadline does: Stop(() => Launch(t)).
            Assert.That(TryDeferLaunch(list, run => ran.Add("launch " + run)), Is.True);
            Assert.That(ran, Is.Empty, "no launch while the native stop may still be running");
            Assert.That(TryDeferLaunch(list, run => ran.Add("later launch " + run)), Is.True);
            Assert.That(TryJoin(list, () => ran.Add("nested stop"), 1), Is.True, "no second native stop");
            Assert.That(list.IsPending(token), Is.True);

            list.Complete(token, () => 1, Run); // the report, or the hard deadline
            Assert.That(ran, Is.EqualTo(new[] { "launch False", "later launch False", "nested stop" }));
        }

        // Giving up on a stop the SDK never reports (the hard deadline) is the same Complete call, made by the
        // bridge's timer.
        [Test]
        public void Giving_up_on_a_stop_runs_its_waiters_frees_later_calls_and_ignores_the_late_report()
        {
            var list = new StopWaitList();
            var count = 0;
            var token = list.Begin(() => count++, 1);
            TryJoin(list, () => count++, 1);

            list.Complete(token, () => 1, Run); // the deadline
            Assert.That(count, Is.EqualTo(2), "awaiting code must not hang");
            Assert.That(TryJoin(list, () => { }, 1), Is.False, "a later Stop issues its own native stop");
            Assert.That(TryDeferLaunch(list, _ => { }), Is.False, "a later Launch runs now");

            list.Complete(token, () => 1, Run); // the SDK's late report
            Assert.That(count, Is.EqualTo(2));
        }

        [Test]
        public void A_launch_runs_now_when_no_stop_is_in_flight()
        {
            var list = new StopWaitList();
            Assert.That(TryDeferLaunch(list, _ => Assert.Fail("not deferred")), Is.False);
        }

        [Test]
        public void A_launch_during_a_stop_runs_after_it_and_after_its_waiters()
        {
            var list = new StopWaitList();
            var ran = new List<string>();
            var token = list.Begin(() => ran.Add("stop"), 1);
            Assert.That(TryDeferLaunch(list, run => ran.Add("launch " + run)), Is.True);
            Assert.That(ran, Is.Empty);

            list.Complete(token, () => 1, Run);
            Assert.That(ran, Is.EqualTo(new[] { "stop", "launch True" }));
        }

        [Test]
        public void Of_two_launches_during_a_stop_the_latest_wins()
        {
            var list = new StopWaitList();
            var ran = new List<string>();
            var token = list.Begin(null, 1);
            TryDeferLaunch(list, run => ran.Add("A " + run));
            Assert.That(ran, Is.Empty);
            TryDeferLaunch(list, run => ran.Add("B " + run));
            Assert.That(ran, Is.EqualTo(new[] { "A False" }), "the earlier launch is cancelled at once, not at the report");

            list.Complete(token, () => 1, Run);
            Assert.That(ran, Is.EqualTo(new[] { "A False", "B True" }));
        }

        [Test]
        public void A_stop_after_a_deferred_launch_cancels_it()
        {
            var list = new StopWaitList();
            var ran = new List<string>();
            var token = list.Begin(null, 1);
            TryDeferLaunch(list, run => ran.Add("launch " + run));
            Assert.That(TryJoin(list, () => ran.Add("stop"), 1), Is.True);
            Assert.That(ran, Is.EqualTo(new[] { "launch False" }));

            list.Complete(token, () => 1, Run);
            Assert.That(ran, Is.EqualTo(new[] { "launch False", "stop" }));
        }

        [Test]
        public void A_stop_before_a_deferred_launch_does_not_cancel_it()
        {
            var list = new StopWaitList();
            var ran = new List<string>();
            var token = list.Begin(null, 1);
            TryJoin(list, () => ran.Add("delete"), 1);
            TryDeferLaunch(list, run => ran.Add("launch " + run));

            list.Complete(token, () => 1, Run);
            Assert.That(ran, Is.EqualTo(new[] { "delete", "launch True" }));
        }

        [Test]
        public void A_waiter_that_launches_or_stops_again_cancels_the_deferred_launch()
        {
            // Stop(() => Launch(a)); Launch(b); -- Launch(a) is called last, when the stop finishes, so it wins.
            var list = new StopWaitList();
            var generation = 1;
            var ran = new List<string>();
            var token = list.Begin(() =>
            {
                Assert.That(TryDeferLaunch(list, _ => Assert.Fail("nothing in flight")), Is.False);
                generation++;
                ran.Add("a");
            }, 1);
            TryDeferLaunch(list, run => ran.Add("b " + run));

            list.Complete(token, () => generation, Run);
            Assert.That(ran, Is.EqualTo(new[] { "a", "b False" }));

            // Stop(() => Stop()); Launch(b); -- the second Stop is newer than the deferred launch.
            ran.Clear();
            var second = 0;
            token = list.Begin(() => second = list.Begin(null, generation), generation);
            TryDeferLaunch(list, run => ran.Add("b " + run));
            list.Complete(token, () => generation, Run);
            Assert.That(ran, Is.EqualTo(new[] { "b False" }));
            Assert.That(list.IsPending(second), Is.True);
        }
    }
}
