using System;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class AttributePolicyTests
    {
        [Test]
        public void Long_max_is_accepted_and_two_to_the_63_is_rejected_without_echoing_it()
        {
            var ok = AttributePolicy.Evaluate("lives", long.MaxValue);
            Assert.That(ok.Accepted, Is.True);

            double tooBig = 9223372036854775808d;
            var bad = AttributePolicy.Evaluate("lives", tooBig);
            Assert.That(bad.Accepted, Is.False);
            Assert.That(bad.Error, Does.Contain("lives"));
            Assert.That(bad.Error, Does.Not.Contain(tooBig.ToString()));
        }

        [Test]
        public void Non_finite_and_unsupported_types_are_rejected()
        {
            var nan = AttributePolicy.Evaluate("n", double.NaN);
            Assert.That(nan.Accepted, Is.False);
            Assert.That(nan.Error, Does.Contain("9223372036854775808"));
            Assert.That(AttributePolicy.Evaluate("when", DateTime.UtcNow).Accepted, Is.False);
        }

        [Test]
        public void Unsigned_and_decimal_integers_inside_signed_64_bit_range_are_accepted()
        {
            Assert.That(AttributePolicy.Evaluate("wave", 3u).Accepted, Is.True);
            Assert.That(AttributePolicy.Evaluate("id", 1UL).Accepted, Is.True);
            Assert.That(AttributePolicy.Evaluate("score", 1m).Accepted, Is.True);
            Assert.That(AttributePolicy.Evaluate("big", ulong.MaxValue).Accepted, Is.False);
        }

        [Test]
        public void Empty_attribute_name_is_rejected()
        {
            Assert.That(AttributePolicy.Evaluate("", "x").Accepted, Is.False);
            Assert.That(AttributePolicy.Evaluate(null, true).Accepted, Is.False);
        }

        [Test]
        public void String_over_1024_utf16_units_is_rejected()
        {
            var bad = AttributePolicy.Evaluate("note", new string('a', 1025));
            Assert.That(bad.Accepted, Is.False);
            Assert.That(bad.Error, Does.Contain("1024"));
            Assert.That(bad.Error, Does.Not.Contain("aaaa"));
        }

        [Test]
        public void Empty_user_identifier_clears_on_set_and_get()
        {
            Assert.That(UserIdentifierPolicy.ForSet(null), Is.Null);
            Assert.That(UserIdentifierPolicy.ForSet(""), Is.Null);
            Assert.That(UserIdentifierPolicy.ForSet("ada"), Is.EqualTo("ada"));
            Assert.That(UserIdentifierPolicy.ForGet(""), Is.Null);
        }
    }
}
