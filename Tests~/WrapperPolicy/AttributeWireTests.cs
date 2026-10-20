using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class AttributeWireTests
    {
        [Test]
        public void Tagged_scalars_decode_to_their_kind()
        {
            Assert.That(AttributeWire.Decode("s:1"), Is.EqualTo("1"));
            Assert.That(AttributeWire.Decode("s:"), Is.EqualTo(""));
            Assert.That(AttributeWire.Decode("s:a:b"), Is.EqualTo("a:b"));
            Assert.That(AttributeWire.Decode("i:1"), Is.EqualTo(1L));
            Assert.That(AttributeWire.Decode("i:-7"), Is.EqualTo(-7L));
            Assert.That(AttributeWire.Decode("i:9007199254740993"), Is.EqualTo(9007199254740993L));
            Assert.That(AttributeWire.Decode("d:1.5"), Is.EqualTo(1.5d));
            Assert.That(AttributeWire.Decode("d:1"), Is.EqualTo(1d).And.TypeOf<double>());
            Assert.That(AttributeWire.Decode("b:1"), Is.EqualTo(true));
            Assert.That(AttributeWire.Decode("b:0"), Is.EqualTo(false));
        }

        [Test]
        public void The_string_one_the_number_one_and_true_stay_distinct()
        {
            Assert.That(AttributeWire.Decode("s:1"), Is.TypeOf<string>());
            Assert.That(AttributeWire.Decode("i:1"), Is.TypeOf<long>());
            Assert.That(AttributeWire.Decode("b:1"), Is.TypeOf<bool>());
        }

        [Test]
        public void Containers_stay_json_text_and_unknown_text_is_returned_as_is()
        {
            Assert.That(AttributeWire.Decode("j:{\"a\":1}"), Is.EqualTo("{\"a\":1}"));
            Assert.That(AttributeWire.Decode(null), Is.Null);
            Assert.That(AttributeWire.Decode("plain"), Is.EqualTo("plain"));
            Assert.That(AttributeWire.Decode("x:1"), Is.EqualTo("x:1"));
            Assert.That(AttributeWire.Decode("b:2"), Is.EqualTo("b:2"));
            Assert.That(AttributeWire.Decode("i:abc"), Is.EqualTo("i:abc"));
        }

        [Test]
        public void Integers_above_signed_64_bit_decode_as_double()
        {
            Assert.That(AttributeWire.Decode("i:18446744073709551615"), Is.EqualTo(18446744073709551615d));
        }

        [Test]
        public void Numeric_types_without_a_java_box_map_to_ones_with_one()
        {
            Assert.That(AttributeWire.ToBoxable((sbyte)-3), Is.EqualTo(-3).And.TypeOf<int>());
            Assert.That(AttributeWire.ToBoxable((ushort)65535), Is.EqualTo(65535).And.TypeOf<int>());
            Assert.That(AttributeWire.ToBoxable(4000000000u), Is.EqualTo(4000000000L).And.TypeOf<long>());
            Assert.That(AttributeWire.ToBoxable(1UL), Is.EqualTo(1L).And.TypeOf<long>());
            Assert.That(AttributeWire.ToBoxable(ulong.MaxValue), Is.TypeOf<double>());
            Assert.That(AttributeWire.ToBoxable(1.5m), Is.EqualTo(1.5d).And.TypeOf<double>());
            Assert.That(AttributeWire.ToBoxable(7), Is.EqualTo(7).And.TypeOf<int>());
            Assert.That(AttributeWire.ToBoxable("x"), Is.EqualTo("x"));
            Assert.That(AttributeWire.ToBoxable(null), Is.Null);
        }

        [Test]
        public void Decimal_input_reads_back_as_double_on_the_ios_decode_path()
        {
            // The SDK stores 1.0 as a double and the bridge tags it d: (see BridgeTests "wholedouble").
            Assert.That(AttributeWire.ToJson(1m), Does.Contain("."));
            Assert.That(AttributeWire.Decode("d:1"), Is.TypeOf<double>());
            Assert.That(AttributeWire.Decode("d:10"), Is.EqualTo(10d).And.TypeOf<double>());
        }

        [Test]
        public void Whole_floating_values_are_sent_with_a_decimal_point_so_they_stay_floating()
        {
            Assert.That(AttributeWire.ToJson(1d), Is.EqualTo("1.0"));
            Assert.That(AttributeWire.ToJson(1f), Is.EqualTo("1.0"));
            Assert.That(AttributeWire.ToJson(1.5d), Is.EqualTo("1.5"));
            Assert.That(AttributeWire.ToJson(1e20d), Does.Contain("E"));
            Assert.That(AttributeWire.ToJson(1m), Is.EqualTo("1.0"));
            Assert.That(AttributeWire.ToJson(10m), Is.EqualTo("10.0"));
            Assert.That(AttributeWire.ToJson(1.5m), Is.EqualTo("1.5"));
            Assert.That(AttributeWire.ToJson(3), Is.EqualTo("3"));
            Assert.That(AttributeWire.ToJson(true), Is.EqualTo("true"));
            Assert.That(AttributeWire.ToJson("1"), Is.EqualTo("\"1\""));
        }

        [Test]
        public void Wire_text_is_culture_invariant()
        {
            var saved = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                Assert.That((1.5).ToString(CultureInfo.CurrentCulture), Is.EqualTo("1,5"));
                Assert.That(AttributeWire.Decode("d:1.5"), Is.EqualTo(1.5d));
                Assert.That(AttributeWire.Decode("i:-1234567"), Is.EqualTo(-1234567L));
                Assert.That(AttributeWire.ToJson(1.5d), Is.EqualTo("1.5"));
                Assert.That(AttributeWire.ToJson(2d), Is.EqualTo("2.0"));
            }
            finally
            {
                CultureInfo.CurrentCulture = saved;
            }
        }

        enum Level : short { Low = 3 }

        static List<object> Items(int count, string item)
        {
            var list = new List<object>();
            for (var i = 0; i < count; i++) list.Add(item);
            return list;
        }

        [Test]
        public void Trace_container_is_the_json_android_receives_as_text()
        {
            var map = new Dictionary<string, object> { { "k", 1 }, { "n", new List<object> { true, null, "x" } }, { "skip", null } };
            Assert.That(AttributeWire.ToTraceJson(map), Is.EqualTo("{\"k\":1,\"n\":[true,null,\"x\"]}"));
            Assert.That(AttributeWire.ToNative(map), Is.EqualTo(AttributeWire.ToTraceJson(map)));
            Assert.That(AttributeWire.ToNative(new[] { 1, 2 }), Is.EqualTo(AttributeWire.ToTraceJson(new[] { 1, 2 })));
        }

        [Test]
        public void Trace_container_is_the_same_bounded_valid_json_on_both_platforms_at_the_cap()
        {
            // Each item is 11 units (quotes and a comma), so the cap falls on an item edge or inside one.
            foreach (var count in new[] { 100, 743, 744, 745, 5000 })
            {
                var list = Items(count, "abcdefgh");
                var ios = AttributeWire.ToTraceJson(list);
                Assert.That(AttributeWire.ToNative(list), Is.EqualTo(ios), "count " + count);
                Assert.That(ios.Length, Is.LessThanOrEqualTo(AttributeWire.MaxContainerJsonLength));
                var items = JsonDocument.Parse(ios).RootElement; // valid JSON, never cut mid-token
                Assert.That(items.GetArrayLength(), Is.EqualTo(Math.Min(count, 744)), "count " + count);
            }
        }

        [Test]
        public void Trace_container_with_an_emoji_at_the_cap_is_never_a_lone_surrogate()
        {
            for (var pad = 8170; pad < 8195; pad++)
            {
                var list = new List<object> { new string('a', pad), "\U0001F600\U0001F600\U0001F600" };
                var ios = AttributeWire.ToTraceJson(list);
                Assert.That(AttributeWire.ToNative(list), Is.EqualTo(ios), "pad " + pad);
                Assert.That(ios.Length, Is.LessThanOrEqualTo(AttributeWire.MaxContainerJsonLength));
                JsonDocument.Parse(ios);
                for (var i = 0; i < ios.Length; i++)
                {
                    if (char.IsHighSurrogate(ios[i])) Assert.That(char.IsLowSurrogate(ios[++i]), "pad " + pad);
                    else Assert.That(char.IsLowSurrogate(ios[i]), Is.False, "pad " + pad);
                }
            }
        }

        [Test]
        public void Trace_scalars_match_android_values()
        {
            Assert.That(AttributeWire.ToTraceJson(null), Is.EqualTo("null"));
            Assert.That(AttributeWire.ToTraceJson("a\"b"), Is.EqualTo("\"a\\\"b\""));
            Assert.That(AttributeWire.ToTraceJson(true), Is.EqualTo("true"));
            Assert.That(AttributeWire.ToTraceJson(5), Is.EqualTo("5"));
            Assert.That(AttributeWire.ToTraceJson(1.0f), Is.EqualTo("1.0"));
            Assert.That(AttributeWire.ToNative(1.0f), Is.EqualTo(1.0).And.TypeOf<double>());
            Assert.That(AttributeWire.ToTraceJson(Level.Low), Is.EqualTo("3"));
            Assert.That(AttributeWire.ToNative(Level.Low), Is.EqualTo(3));
            Assert.That(AttributeWire.ToTraceJson(new byte[] { 1, 255 }), Is.EqualTo("\"Af8=\""));
            Assert.That(AttributeWire.ToNative(new byte[] { 1, 255 }), Is.EqualTo("Af8="));
            var when = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            Assert.That(AttributeWire.ToTraceJson(when), Is.EqualTo("\"2020-01-02T03:04:05.0000000Z\""));
            Assert.That(AttributeWire.ToNative(when), Is.EqualTo("2020-01-02T03:04:05.0000000Z"));
        }

        [Test]
        public void Trace_json_does_not_depend_on_culture()
        {
            var saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Assert.That(AttributeWire.ToTraceJson(new List<object> { 1.5 }), Is.EqualTo("[1.5]"));
                Assert.That(AttributeWire.ToTraceJson(1.5f), Is.EqualTo("1.5"));
            }
            finally { Thread.CurrentThread.CurrentCulture = saved; }
        }
    }
}
