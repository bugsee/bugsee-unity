using Bugsee.Contracts.Options;
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

        [TestCase("debug", 1, 4)]
        [TestCase("info", 2, 3)]
        [TestCase("warning", 3, 2)]
        [TestCase("error", 4, 1)]
        [TestCase("fatal", 5, 1)]
        public void All_wire_names_map_to_android_and_ios_ints(string name, int android, int ios)
        {
            Assert.That(BreadcrumbLevelMap.TryParse(name, out var level), Is.True);
            Assert.That(BreadcrumbLevelMap.ToAndroid(level), Is.EqualTo(android));
            Assert.That(BreadcrumbLevelMap.ToIos(level), Is.EqualTo(ios));
        }

        [Test]
        public void Error_and_fatal_both_map_to_ios_error()
        {
            Assert.That(BreadcrumbLevelMap.TryParse("error", out var error), Is.True);
            Assert.That(BreadcrumbLevelMap.TryParse("fatal", out var fatal), Is.True);
            Assert.That(BreadcrumbLevelMap.ToIos(error), Is.EqualTo(1));
            Assert.That(BreadcrumbLevelMap.ToIos(fatal), Is.EqualTo(1));
            Assert.That(BreadcrumbLevelMap.ToIos(error), Is.EqualTo(BreadcrumbLevelMap.ToIos(fatal)));
        }

        [Test]
        public void LogLevel_error_maps_to_android_error_not_info()
        {
            Assert.That(BreadcrumbLevelMap.TryFromLogLevel(LogLevel.Error, out var level), Is.True);
            Assert.That(level, Is.EqualTo(BreadcrumbLevelName.Error));
            Assert.That(BreadcrumbLevelMap.ToAndroid(level), Is.EqualTo(4));
            Assert.That(BreadcrumbLevelMap.ToIos(level), Is.EqualTo(1));
        }

        [Test]
        public void LogLevel_verbose_maps_to_debug_wire()
        {
            Assert.That(BreadcrumbLevelMap.TryFromLogLevel(LogLevel.Verbose, out var level), Is.True);
            Assert.That(level, Is.EqualTo(BreadcrumbLevelName.Debug));
            Assert.That(BreadcrumbLevelMap.ToAndroid(level), Is.EqualTo(1));
            Assert.That(BreadcrumbLevelMap.ToIos(level), Is.EqualTo(4));
        }

        [Test]
        public void Unknown_name_is_rejected()
        {
            Assert.That(BreadcrumbLevelMap.TryParse("verbose", out _), Is.False);
            Assert.That(BreadcrumbLevelMap.TryParse("Debug", out _), Is.False);
        }

        [Test]
        public void Invalid_breadcrumb_level_name_fails_closed_on_android()
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(() =>
                BreadcrumbLevelMap.ToAndroid((BreadcrumbLevelName)42));
        }

        [Test]
        public void Invalid_breadcrumb_level_name_fails_closed_on_ios()
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(() =>
                BreadcrumbLevelMap.ToIos((BreadcrumbLevelName)42));
        }
    }
}
