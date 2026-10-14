using System;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class OneShotCallbacksTests
    {
        [Test]
        public void A_callback_is_taken_once_by_its_token()
        {
            var callbacks = new OneShotCallbacks<bool>();
            var got = false;
            var token = callbacks.Register(value => got = value);
            Assert.That(token, Is.Not.EqualTo(0), "0 stands for no callback on the native side");

            Assert.That(callbacks.TryTake(token, out var callback), Is.True);
            callback(true);
            Assert.That(got, Is.True);
            Assert.That(callbacks.TryTake(token, out _), Is.False, "the SDK's answer and the timeout cannot both run it");
        }

        [Test]
        public void Tokens_are_distinct_and_unknown_ones_find_nothing()
        {
            var callbacks = new OneShotCallbacks<bool>();
            var a = callbacks.Register(_ => { });
            var b = callbacks.Register(_ => { });
            Assert.That(a, Is.Not.EqualTo(b));
            Assert.That(callbacks.TryTake(b + 100, out _), Is.False);
            Assert.That(callbacks.TryTake(a, out _), Is.True);
            Assert.That(callbacks.TryTake(b, out _), Is.True);
        }

        [Test]
        public void A_stop_completion_runs_once_whether_the_soft_deadline_or_the_report_comes_first()
        {
            var callbacks = new OneShotCallbacks<bool>();
            var answers = new System.Collections.Generic.List<bool>();
            var token = callbacks.Register(answers.Add);

            // Soft deadline first: the caller is answered (false = not reported yet)...
            if (callbacks.TryTake(token, out var early)) early(false);
            // ...and the SDK's late report, or a drop by a later Launch, finds nothing.
            if (callbacks.TryTake(token, out var late)) late(true);
            Assert.That(answers, Is.EqualTo(new[] { false }));
        }

        [Test]
        public void A_null_callback_is_rejected()
        {
            Assert.Throws<ArgumentNullException>(() => new OneShotCallbacks<bool>().Register(null));
        }
    }
}
