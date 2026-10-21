using System;
using System.Collections.Generic;
using Bugsee.Contracts.Lifecycle;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    // The iOS bridge's delete path without the native SDK: Delete -> join the stop -> begin the wipe, as IOSBridge composes them.
    public class NativeWipeGateTests
    {
        sealed class Fake
        {
            public readonly StopWaitList Waiters = new StopWaitList();
            public readonly WipeCompletions Results = new WipeCompletions();
            public readonly NativeWipeGate Gate;
            public readonly List<string> Log = new List<string>();
            public readonly Dictionary<DeleteScope, int> Tokens = new Dictionary<DeleteScope, int>();
            public int InFlight;
            public int MaxInFlight;

            public Fake() { Gate = new NativeWipeGate(Waiters, Results); }

            static void Run(Action a) => a();

            // IOSBridge.BeginNativeDelete
            public void BeginNativeDelete(DeleteScope scope, Action<bool> done)
            {
                if (Gate.TryJoinOrQueue(scope, done, () => BeginNativeDelete(scope, done))) return;
                var token = Gate.Begin(scope, done, 1);
                Tokens[scope] = token;
                InFlight++;
                MaxInFlight = Math.Max(MaxInFlight, InFlight);
                Log.Add("wipe:" + scope);
            }

            // The SDK reports the wipe.
            public void Report(DeleteScope scope, bool wiped)
            {
                InFlight--;
                var token = Tokens[scope];
                Results.Settle(token, wiped, (cb, v) => cb(v));
                Waiters.Complete(token, () => 1, Run);
            }

            // IOSBridge.DeleteCollectedData on the main thread, the SDK running: both Deletes join the native stop.
            public void Delete(DeleteScope scope, Action<bool> done, int stop)
            {
                if (Gate.TryJoinOrQueue(scope, done, () => BeginNativeDelete(scope, done))) return;
                if (!Waiters.TryJoin(() => BeginNativeDelete(scope, done), 1, out var cancelled, null, first: true)) throw new InvalidOperationException("no stop");
                cancelled?.Invoke(false);
            }
        }

        [Test]
        public void Two_scopes_joining_one_stop_wipe_one_after_the_other_and_the_launch_waits_for_the_last()
        {
            var f = new Fake();
            var answers = new List<string>();
            var stop = f.Waiters.Begin(null, 1);
            f.Delete(DeleteScope.CaptureOnly, ok => answers.Add("narrow:" + ok), stop);
            f.Delete(DeleteScope.Everything, ok => answers.Add("wide:" + ok), stop);
            f.Waiters.TryDeferLaunch(run => f.Log.Add(run ? "launch" : "cancelled"), out _);

            f.Waiters.Complete(stop, () => 1, a => a());
            Assert.That(f.Log, Is.EqualTo(new[] { "wipe:CaptureOnly" }), "the wide wipe waits for the narrow one");
            Assert.That(f.InFlight, Is.EqualTo(1));

            f.Report(DeleteScope.CaptureOnly, true);
            Assert.That(answers, Is.EqualTo(new[] { "narrow:True" }));
            Assert.That(f.Log, Is.EqualTo(new[] { "wipe:CaptureOnly", "wipe:Everything" }), "the launch is not released by the first wipe");

            f.Report(DeleteScope.Everything, false);
            Assert.That(answers, Is.EqualTo(new[] { "narrow:True", "wide:False" }));
            Assert.That(f.Log, Is.EqualTo(new[] { "wipe:CaptureOnly", "wipe:Everything", "launch" }));
            Assert.That(f.MaxInFlight, Is.EqualTo(1), "never two native wipes at once");
        }

        [Test]
        public void The_same_scope_while_a_wipe_runs_shares_it()
        {
            var f = new Fake();
            var answers = new List<string>();
            f.BeginNativeDelete(DeleteScope.CaptureAndIntermediate, ok => answers.Add("a:" + ok));
            f.BeginNativeDelete(DeleteScope.CaptureAndIntermediate, ok => answers.Add("b:" + ok));
            Assert.That(f.Log, Is.EqualTo(new[] { "wipe:CaptureAndIntermediate" }), "the second shares the wipe");
            f.Report(DeleteScope.CaptureAndIntermediate, true);
            Assert.That(answers, Is.EqualTo(new[] { "a:True", "b:True" }));
        }

        [Test]
        public void A_launch_requested_after_both_deletes_still_waits_for_the_second_wipe()
        {
            var f = new Fake();
            f.BeginNativeDelete(DeleteScope.CaptureOnly, null);
            f.BeginNativeDelete(DeleteScope.Everything, null); // queued behind the first
            f.Waiters.TryDeferLaunch(run => f.Log.Add(run ? "launch" : "cancelled"), out _);
            f.Report(DeleteScope.CaptureOnly, true);
            Assert.That(f.Log, Is.EqualTo(new[] { "wipe:CaptureOnly", "wipe:Everything" }));
            f.Report(DeleteScope.Everything, true);
            Assert.That(f.Log.GetRange(2, 1), Is.EqualTo(new[] { "launch" }));
        }

        [Test]
        public void A_launch_waiting_before_the_second_delete_is_cancelled_by_it()
        {
            var f = new Fake();
            f.BeginNativeDelete(DeleteScope.CaptureOnly, null);
            f.Waiters.TryDeferLaunch(run => f.Log.Add(run ? "launch" : "cancelled"), out _);
            f.BeginNativeDelete(DeleteScope.Everything, null); // requested after the launch
            Assert.That(f.Log, Is.EqualTo(new[] { "wipe:CaptureOnly", "cancelled" }));
            f.Report(DeleteScope.CaptureOnly, true);
            f.Report(DeleteScope.Everything, true);
            Assert.That(f.Log, Is.EqualTo(new[] { "wipe:CaptureOnly", "cancelled", "wipe:Everything" }));
        }

        [Test]
        public void Nothing_in_flight_means_the_caller_begins_its_own()
        {
            var f = new Fake();
            Assert.That(f.Gate.TryJoinOrQueue(DeleteScope.Everything, null, () => { }), Is.False);
        }

        [Test]
        public void A_wipe_given_up_on_runs_the_queued_one_and_answers_false_once()
        {
            var f = new Fake();
            var answers = new List<string>();
            f.BeginNativeDelete(DeleteScope.CaptureOnly, ok => answers.Add("a:" + ok));
            f.BeginNativeDelete(DeleteScope.Everything, ok => answers.Add("b:" + ok));
            f.Report(DeleteScope.CaptureOnly, false); // the hard deadline and the SDK's late report both end here once
            Assert.That(answers, Is.EqualTo(new[] { "a:False" }));
            Assert.That(f.Log, Is.EqualTo(new[] { "wipe:CaptureOnly", "wipe:Everything" }));
        }
    }
}
