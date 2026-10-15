using System.Collections.Generic;
using Bugsee.Contracts.Apm;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class SpanPolicyTests
    {
        [Test]
        public void Status_names_match_the_android_enum_constants()
        {
            Assert.That(SpanPolicy.AndroidStatusName(SpanStatus.Ok), Is.EqualTo("OK"));
            Assert.That(SpanPolicy.AndroidStatusName(SpanStatus.Error), Is.EqualTo("ERROR"));
            Assert.That(SpanPolicy.AndroidStatusName(SpanStatus.Timeout), Is.EqualTo("TIMEOUT"));
            Assert.That(SpanPolicy.AndroidStatusName(SpanStatus.Cancelled), Is.EqualTo("CANCELLED"));
            Assert.That(SpanPolicy.AndroidStatusName(SpanStatus.DeadlineExceeded), Is.EqualTo("DEADLINE_EXCEEDED"));
            Assert.That(SpanPolicy.AndroidStatusName(SpanStatus.Unknown), Is.EqualTo("UNKNOWN"));
            Assert.That(SpanPolicy.AndroidStatusName((SpanStatus)42), Is.EqualTo("UNKNOWN"));
        }

        [Test]
        public void Status_values_are_the_ios_wire_values()
        {
            Assert.That((int)SpanStatus.Ok, Is.EqualTo(0));
            Assert.That((int)SpanStatus.Error, Is.EqualTo(1));
            Assert.That((int)SpanStatus.Timeout, Is.EqualTo(2));
            Assert.That((int)SpanStatus.Cancelled, Is.EqualTo(3));
            Assert.That((int)SpanStatus.DeadlineExceeded, Is.EqualTo(4));
            Assert.That((int)SpanStatus.Unknown, Is.EqualTo(5));
            Assert.That(SpanPolicy.Normalize((SpanStatus)(-1)), Is.EqualTo(SpanStatus.Unknown));
        }

        [Test]
        public void Description_null_crosses_as_empty()
        {
            Assert.That(SpanPolicy.Description(null), Is.EqualTo(""));
            Assert.That(SpanPolicy.Description("x"), Is.EqualTo("x"));
        }

        [Test]
        public void CleanAttributes_keeps_accepted_entries_and_reports_the_rest()
        {
            var rejected = new List<string>();
            var clean = SpanPolicy.CleanAttributes(new Dictionary<string, object>
            {
                ["s"] = "v",
                ["u"] = (uint)3,
                ["bad"] = new object(),
                ["nan"] = double.NaN
            }, rejected);
            Assert.That(clean.Count, Is.EqualTo(2));
            Assert.That(clean["u"], Is.EqualTo(3L));
            Assert.That(rejected.Count, Is.EqualTo(2));
        }

        [Test]
        public void CleanAttributes_is_null_when_nothing_is_left()
        {
            Assert.That(SpanPolicy.CleanAttributes(null), Is.Null);
            Assert.That(SpanPolicy.CleanAttributes(new Dictionary<string, object>()), Is.Null);
            Assert.That(SpanPolicy.CleanAttributes(new Dictionary<string, object> { ["x"] = new object() }), Is.Null);
        }
    }
}
