using System.Globalization;
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
    }
}
