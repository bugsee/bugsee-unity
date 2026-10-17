using System.Collections.Generic;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class LaunchOptionsWireTests
    {
        [Test]
        public void Narrow_numbers_widen_so_a_long_or_double_cast_works()
        {
            var map = LaunchOptionsWire.Normalize(new Dictionary<string, object>
            {
                { "i", 5 }, { "s", (short)6 }, { "b", (byte)7 }, { "f", 0.5f }, { "l", 9L }, { "d", 1.25 },
                { "t", true }, { "str", "x" }, { "n", null }, { "u", ulong.MaxValue },
            });
            Assert.That((long)map["i"], Is.EqualTo(5L));
            Assert.That((long)map["s"], Is.EqualTo(6L));
            Assert.That((long)map["b"], Is.EqualTo(7L));
            Assert.That((double)map["f"], Is.EqualTo(0.5));
            Assert.That(map["l"], Is.EqualTo(9L));
            Assert.That(map["d"], Is.EqualTo(1.25));
            Assert.That(map["t"], Is.EqualTo(true));
            Assert.That(map["str"], Is.EqualTo("x"));
            Assert.That(map["n"], Is.Null);
            Assert.That(map["u"], Is.TypeOf<double>());
        }

        [Test]
        public void Nested_maps_and_lists_are_widened_too()
        {
            var map = LaunchOptionsWire.Normalize(new Dictionary<string, object>
            {
                { "m", new Dictionary<string, object> { { "k", 1 } } },
                { "l", new List<object> { 2, 0.5f, "s" } },
            });
            Assert.That(((Dictionary<string, object>)map["m"])["k"], Is.EqualTo(1L));
            var list = (List<object>)map["l"];
            Assert.That(list[0], Is.EqualTo(2L));
            Assert.That(list[1], Is.EqualTo(0.5));
            Assert.That(list[2], Is.EqualTo("s"));
        }

        [Test]
        public void Internal_dollar_keys_are_left_out()
        {
            var map = LaunchOptionsWire.Normalize(new Dictionary<string, object>
            {
                { "$$ENDPOINT", "x" }, { "$$DEBUG", true }, { "$$WRAPPER", "w" }, { "keep", 1 }, { "a$$b", 2 },
            });
            Assert.That(map.Keys, Is.EquivalentTo(new[] { "keep", "a$$b" }));
        }

        [Test]
        public void Null_options_stay_null()
        {
            Assert.That(LaunchOptionsWire.Normalize(null), Is.Null);
        }
    }
}
