using System.Collections.Generic;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class IosHostPolicyTests
    {
        [TestCase(0, 60, 30)]
        [TestCase(-1, 60, 30)]
        [TestCase(60, 60, 60)]
        [TestCase(120, 60, 60)]
        [TestCase(120, 120, 120)]
        [TestCase(30, 60, 30)]
        [TestCase(60, 0, 60)]
        public void The_capture_frame_rate_never_exceeds_what_the_screen_shows(int target, int refresh, int expected)
        {
            Assert.That(IosHostPolicy.CaptureFrameRate(target, refresh), Is.EqualTo(expected));
        }

        [TestCase(null, null)]
        [TestCase("", null)]
        [TestCase("dev", null)]
        [TestCase("a1b2c3d", "a1b2c3d")]
        public void The_dev_placeholder_is_not_a_wrapper_build(string build, string expected)
        {
            Assert.That(IosHostPolicy.WrapperBuild(build), Is.EqualTo(expected));
        }

        [Test]
        public void Only_a_map_with_a_type_is_a_usable_wrapper_info()
        {
            Assert.That(IosHostPolicy.IsUsableWrapperInfo(new Dictionary<string, object> { { "type", "UNITY" } }), Is.True);
            Assert.That(IosHostPolicy.IsUsableWrapperInfo(new Dictionary<string, object> { { "fps", 30 } }), Is.False);
            Assert.That(IosHostPolicy.IsUsableWrapperInfo(new Dictionary<string, object> { { "type", null } }), Is.False);
            // The SDK sends isEqualToString: to "type", so anything but a non-empty string is replaced.
            Assert.That(IosHostPolicy.IsUsableWrapperInfo(new Dictionary<string, object> { { "type", 1 } }), Is.False);
            Assert.That(IosHostPolicy.IsUsableWrapperInfo(new Dictionary<string, object> { { "type", true } }), Is.False);
            Assert.That(IosHostPolicy.IsUsableWrapperInfo(new Dictionary<string, object> { { "type", "" } }), Is.False);
            Assert.That(IosHostPolicy.IsUsableWrapperInfo("a string the SDK would index into"), Is.False);
            Assert.That(IosHostPolicy.IsUsableWrapperInfo(null), Is.False);
        }
    }
}
