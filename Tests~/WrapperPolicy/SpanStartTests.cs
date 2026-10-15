using System;
using System.Collections.Generic;
using Bugsee.Contracts.Apm;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class SpanStartTests
    {
        sealed class Handle : ISpanHandle
        {
            public int Released;
            public string SpanId => "s";
            public string TraceId => "t";
            public void SetName(string name) { }
            public void SetDescription(string description) { }
            public void SetAttribute(string key, object value) { }
            public void SetStatus(SpanStatus status) { }
            public ISpanHandle StartChild(string operation, string description) => null;
            public void Finish(SpanStatus status) { }
            public void Release() => Released++;
        }

        [Test]
        public void A_throwing_native_start_warns_and_returns_an_inert_span()
        {
            var warnings = new List<string>();
            ISpan span = null;
            Assert.DoesNotThrow(() => span = SpanStart.Transaction(() => throw new InvalidOperationException("jni"), "op", warnings.Add));
            Assert.That(span, Is.InstanceOf<NoopSpan>());
            Assert.That(warnings.Count, Is.EqualTo(1));
            Assert.That(warnings[0], Does.Contain("jni"));
        }

        [Test]
        public void A_null_handle_is_inert_without_a_warning()
        {
            var warnings = new List<string>();
            Assert.That(SpanStart.Transaction(() => null, "op", warnings.Add), Is.InstanceOf<NoopSpan>());
            Assert.That(warnings, Is.Empty);
        }

        [Test]
        public void A_handle_is_wrapped()
        {
            var handle = new Handle();
            var span = SpanStart.Transaction(() => handle, "op", null);
            Assert.That(span, Is.InstanceOf<ManagedSpan>());
            Assert.That(span.SpanId, Is.EqualTo("s"));
        }

        [Test]
        public void A_throwing_ids_read_releases_the_handle_once_and_rethrows()
        {
            int released = 0;
            Assert.Throws<InvalidOperationException>(() =>
                SpanStart.ReadIds(() => "s", () => throw new InvalidOperationException("read"), () => released++, out _, out _));
            Assert.That(released, Is.EqualTo(1));
            released = 0;
            Assert.Throws<InvalidOperationException>(() =>
                SpanStart.ReadIds(() => throw new InvalidOperationException("read"), () => "t", () => released++, out _, out _));
            Assert.That(released, Is.EqualTo(1));
        }

        [Test]
        public void Ids_are_read_without_releasing_and_null_reads_as_empty()
        {
            int released = 0;
            SpanStart.ReadIds(() => null, () => "t", () => released++, out var span, out var trace);
            Assert.That(span, Is.EqualTo(""));
            Assert.That(trace, Is.EqualTo("t"));
            Assert.That(released, Is.EqualTo(0));
        }
    }
}
