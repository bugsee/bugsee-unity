using System.Collections.Generic;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class JsonReadTests
    {
        [Test]
        public void Reads_scalars_with_their_kind()
        {
            var map = JsonRead.Object("{\"s\":\"a\\n\\u00e9\\\"\",\"t\":true,\"f\":false,\"n\":null,\"i\":-12,\"d\":1.5,\"e\":1e3,\"big\":18446744073709551615}");
            Assert.That(map["s"], Is.EqualTo("a\n\u00e9\""));
            Assert.That(map["t"], Is.EqualTo(true));
            Assert.That(map["f"], Is.EqualTo(false));
            Assert.That(map["n"], Is.Null);
            Assert.That(map["i"], Is.EqualTo(-12L));
            Assert.That(map["d"], Is.EqualTo(1.5));
            Assert.That(map["e"], Is.EqualTo(1000.0));
            Assert.That(map["big"], Is.EqualTo(18446744073709551615.0));
        }

        [Test]
        public void Reads_nested_containers()
        {
            var map = JsonRead.Object(" { \"w\" : { \"type\":\"UNITY\", \"xs\":[1, \"b\", [true]] }, \"empty\":{}, \"none\":[] } ");
            var w = (Dictionary<string, object>)map["w"];
            Assert.That(w["type"], Is.EqualTo("UNITY"));
            var xs = (List<object>)w["xs"];
            Assert.That(xs[0], Is.EqualTo(1L));
            Assert.That(xs[1], Is.EqualTo("b"));
            Assert.That(((List<object>)xs[2])[0], Is.EqualTo(true));
            Assert.That(((Dictionary<string, object>)map["empty"]).Count, Is.EqualTo(0));
            Assert.That(((List<object>)map["none"]).Count, Is.EqualTo(0));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("[]")]
        [TestCase("1")]
        [TestCase("{")]
        [TestCase("{\"a\":}")]
        [TestCase("{\"a\":1,}")]
        [TestCase("{\"a\":1} x")]
        [TestCase("{\"a\":\"unterminated}")]
        [TestCase("{\"a\":\"\\q\"}")]
        [TestCase("{\"a\":\"\\u12\"}")]
        [TestCase("{\"a\":-}")]
        [TestCase("{\"a\":01}")]
        [TestCase("{\"a\":1.}")]
        [TestCase("{\"a\":.5}")]
        [TestCase("{\"a\":1e}")]
        [TestCase("{\"a\":+1}")]
        [TestCase("{\"a\":--1}")]
        public void Malformed_input_is_null_not_an_exception(string json)
        {
            Assert.That(JsonRead.Object(json), Is.Null);
        }

        [Test]
        public void Deep_nesting_is_refused_instead_of_overflowing()
        {
            string deep = new string('[', 500) + new string(']', 500);
            Assert.That(JsonRead.Object("{\"a\":" + deep + "}"), Is.Null);
        }

        [Test]
        public void Round_trips_what_JsonWire_writes()
        {
            var source = new Dictionary<string, object>
            {
                { "text", "line\n\"q\" \u2603" }, { "n", 3 }, { "x", 0.25 }, { "on", true },
                { "inner", new Dictionary<string, object> { { "k", "v" } } },
            };
            var back = JsonRead.Object(JsonWire.Object(source));
            Assert.That(back["text"], Is.EqualTo("line\n\"q\" \u2603"));
            Assert.That(back["n"], Is.EqualTo(3L));
            Assert.That(back["x"], Is.EqualTo(0.25));
            Assert.That(back["on"], Is.EqualTo(true));
            Assert.That(((Dictionary<string, object>)back["inner"])["k"], Is.EqualTo("v"));
        }
    }
}
