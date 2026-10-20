using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class AndroidNativeValueTests
    {
        enum Level : short { Low = 3 }

        [Test]
        public void A_list_and_a_map_cross_as_json_not_a_type_name()
        {
            Assert.That(AttributeWire.ToNative(new List<object> { 1, "a", 2.5 }), Is.EqualTo("[1,\"a\",2.5]"));
            Assert.That(AttributeWire.ToNative(new Dictionary<string, object> { { "k", new[] { 1, 2 } } }), Is.EqualTo("{\"k\":[1,2]}"));
            Assert.That(AttributeWire.ToNative(new[] { "x" }), Is.EqualTo("[\"x\"]"));
        }

        [Test]
        public void A_large_container_is_cut_at_the_cap()
        {
            var big = new List<object>();
            for (int i = 0; i < 5000; i++) big.Add("abcdefgh");
            Assert.That(((string)AttributeWire.ToNative(big)).Length, Is.EqualTo(AttributeWire.MaxContainerJsonLength));
        }

        [Test]
        public void Scalars_keep_their_kind_and_the_odd_widths_are_mapped()
        {
            Assert.That(AttributeWire.ToNative(5), Is.EqualTo(5).And.TypeOf<int>());
            Assert.That(AttributeWire.ToNative(5L), Is.TypeOf<long>());
            Assert.That(AttributeWire.ToNative(1.5), Is.TypeOf<double>());
            Assert.That(AttributeWire.ToNative(true), Is.EqualTo(true));
            Assert.That(AttributeWire.ToNative("s"), Is.EqualTo("s"));
            Assert.That(AttributeWire.ToNative(null), Is.Null);
            Assert.That(AttributeWire.ToNative((ushort)7), Is.EqualTo(7).And.TypeOf<int>());
            Assert.That(AttributeWire.ToNative(7u), Is.TypeOf<long>());
            Assert.That(AttributeWire.ToNative(1.25m), Is.EqualTo(1.25).And.TypeOf<double>());
        }

        [Test]
        public void An_enum_crosses_by_value_and_other_types_as_invariant_text()
        {
            Assert.That(AttributeWire.ToNative(Level.Low), Is.EqualTo(3).And.TypeOf<short>());
            Assert.That(AttributeWire.ToNative('c'), Is.EqualTo("c"));
            Assert.That(AttributeWire.ToNative(new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc)), Is.EqualTo("2020-01-02T03:04:05.0000000Z"));
        }

        [Test]
        public void Culture_does_not_change_the_text()
        {
            var old = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Assert.That(AttributeWire.ToNative(new TimeSpan(1, 2, 3)), Is.EqualTo("01:02:03"));
                Assert.That(AttributeWire.ToNative(new List<object> { 1.5 }), Is.EqualTo("[1.5]"));
            }
            finally { Thread.CurrentThread.CurrentCulture = old; }
        }
    }
}
