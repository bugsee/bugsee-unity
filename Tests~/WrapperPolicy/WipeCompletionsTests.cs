using System;
using System.Collections.Generic;
using Bugsee.Contracts.Lifecycle;
using Bugsee.WrapperPolicy;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class WipeCompletionsTests
    {
        static void Run(Action<bool> result, bool wiped) => result(wiped);

        [Test]
        public void A_delete_of_the_same_scope_joins_the_wipe_and_both_are_answered_once()
        {
            var wipes = new WipeCompletions();
            var answers = new List<string>();
            wipes.Begin(1, DeleteScope.CaptureOnly, ok => answers.Add("a:" + ok));
            Assert.That(wipes.TryJoin(1, DeleteScope.CaptureOnly, ok => answers.Add("b:" + ok)), Is.True);

            wipes.Settle(1, true, Run);
            wipes.Settle(1, false, Run);
            Assert.That(answers, Is.EqualTo(new[] { "a:True", "b:True" }));
        }

        [Test]
        public void A_delete_of_another_scope_does_not_join()
        {
            var wipes = new WipeCompletions();
            wipes.Begin(1, DeleteScope.CaptureAndIntermediate, null);
            Assert.That(wipes.TryJoin(1, DeleteScope.Everything, ok => { }), Is.False);
            Assert.That(wipes.TryJoin(1, DeleteScope.CaptureOnly, ok => { }), Is.False);
            Assert.That(wipes.TryJoin(2, DeleteScope.CaptureAndIntermediate, ok => { }), Is.False, "no such wipe");
        }

        [Test]
        public void A_report_after_the_hard_deadline_answers_nobody_twice()
        {
            var wipes = new WipeCompletions();
            var answers = new List<bool>();
            wipes.Begin(1, DeleteScope.Everything, answers.Add);
            wipes.Settle(1, false, Run); // the bridge gives up
            wipes.Settle(1, true, Run);  // the SDK reports late
            Assert.That(answers, Is.EqualTo(new[] { false }));
            Assert.That(wipes.TryJoin(1, DeleteScope.Everything, answers.Add), Is.False, "a finished wipe is not joined");
        }

        [Test]
        public void A_stop_token_settles_nothing()
        {
            new WipeCompletions().Settle(5, false, Run);
        }

        [Test]
        public void A_wipe_without_a_result_is_settled_quietly()
        {
            var wipes = new WipeCompletions();
            wipes.Begin(1, DeleteScope.Everything, null);
            wipes.TryJoin(1, DeleteScope.Everything, null);
            wipes.Settle(1, true, Run);
        }
    }
}
