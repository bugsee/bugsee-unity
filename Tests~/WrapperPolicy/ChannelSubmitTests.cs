using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class ChannelSubmitTests
    {
        [Test]
        public void LogSourceForHostDebug_is_custom_98()
        {
            Assert.That(ChannelSubmit.LogSourceForHostDebug(), Is.EqualTo(98));
        }

        [Test]
        public void NetworkRequiresFiltering_is_true()
        {
            Assert.That(ChannelSubmit.NetworkRequiresFiltering(), Is.True);
        }
    }
}
