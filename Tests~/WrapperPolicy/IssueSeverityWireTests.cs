using Bugsee.Contracts.Options;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class IssueSeverityWireTests
    {
        [Test]
        public void Zero_is_unset()
        {
            Assert.That(IssueSeverityWire.TryFromWire(0, out _), Is.False);
        }

        [Test]
        public void One_is_VeryLow_and_four_is_Critical()
        {
            Assert.That(IssueSeverityWire.TryFromWire(1, out var low), Is.True);
            Assert.That(low, Is.EqualTo(IssueSeverity.VeryLow));
            Assert.That(IssueSeverityWire.TryFromWire(4, out var critical), Is.True);
            Assert.That(critical, Is.EqualTo(IssueSeverity.Critical));
        }
    }
}
