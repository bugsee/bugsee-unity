using System.Collections.Generic;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class NotifyPolicyTests
    {
        [TestCase(null, false)]
        [TestCase("", false)]
        [TestCase(" ", true)]
        [TestCase("Deploy done", true)]
        public void Title_is_required(string title, bool expected)
        {
            Assert.That(NotifyPolicy.HasTitle(title), Is.EqualTo(expected));
        }

        [Test]
        public void Null_body_is_empty_on_both_platforms()
        {
            Assert.That(NotifyPolicy.Body(null), Is.EqualTo(""));
            Assert.That(NotifyPolicy.Body("b"), Is.EqualTo("b"));
        }

        [Test]
        public void Null_or_empty_fields_are_no_fields()
        {
            Assert.That(NotifyPolicy.CleanFields(null), Is.Null);
            Assert.That(NotifyPolicy.CleanFields(new Dictionary<string, string>()), Is.Null);
            Assert.That(NotifyPolicy.ToWireMap(null), Is.Null);
        }

        [Test]
        public void Null_values_are_dropped_and_the_rest_kept()
        {
            var clean = NotifyPolicy.CleanFields(new Dictionary<string, string> { { "a", "1" }, { "b", null } });
            Assert.That(clean.Count, Is.EqualTo(1));
            Assert.That(clean["a"], Is.EqualTo("1"));
            Assert.That(NotifyPolicy.CleanFields(new Dictionary<string, string> { { "b", null } }), Is.Null);
        }

        [Test]
        public void Wire_map_is_the_json_the_ios_bridge_sends()
        {
            var map = NotifyPolicy.ToWireMap(new Dictionary<string, string> { { "env", "prod" }, { "skip", null } });
            Assert.That(JsonWire.Object(map), Is.EqualTo("{\"env\":\"prod\"}"));
        }
    }
}
