using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class BreadcrumbLevelMapTests
    {
        [Test]
        public void Debug_is_1_on_android_and_4_on_ios()
        {
            Assert.That(BreadcrumbLevelMap.TryParse("debug", out var level), Is.True);
            Assert.That(BreadcrumbLevelMap.ToAndroid(level), Is.EqualTo(1));
            Assert.That(BreadcrumbLevelMap.ToIos(level), Is.EqualTo(4));
        }

        [Test]
        public void Fatal_is_5_on_android_and_error_on_ios()
        {
            Assert.That(BreadcrumbLevelMap.TryParse("fatal", out var level), Is.True);
            Assert.That(BreadcrumbLevelMap.ToAndroid(level), Is.EqualTo(5));
            Assert.That(BreadcrumbLevelMap.ToIos(level), Is.EqualTo(1));
        }

        [Test]
        public void Unknown_name_is_rejected()
        {
            Assert.That(BreadcrumbLevelMap.TryParse("verbose", out _), Is.False);
            Assert.That(BreadcrumbLevelMap.TryParse("Debug", out _), Is.False);
        }
    }
}
