using System.Collections.Generic;
using Bugsee.Contracts.Options;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    /// <summary>
    /// The typed launch options of the Android 7.4.2 keys that had no constant (#96): each property writes its key with
    /// the wire type the Android SDK reads, the Android-only ones are kept off an iOS launch, and the two keys both
    /// SDKs know (<c>MaxDataSize</c>, <c>NotifyFlushDelay</c>) reach both.
    /// </summary>
    public class AndroidLaunchOptionsTests
    {
        static Dictionary<string, object> AndroidOnly() => new AndroidLaunchOptions
        {
            MaxPendingReports = 7,
            MaxPendingReportAge = 3,
            DetectAndReportExitLowMemoryBackgroundAsError = true,
            DetectAndReportHangSampling = false,
            DetectAndReportAnrSampling = false,
            DetectAndReportCrashNdk = false,
            CrashNdkIncludeTombstoneLogs = true,
        }.ToDictionary();

        [Test]
        public void Every_android_only_property_writes_its_key_with_the_wire_type()
        {
            var d = AndroidOnly();
            Assert.That(d[Options.MaxPendingReports], Is.EqualTo(7).And.TypeOf<int>());
            Assert.That(d[Options.MaxPendingReportAge], Is.EqualTo(3).And.TypeOf<int>());
            Assert.That(d[Options.DetectAndReportExitLowMemoryBackgroundAsError], Is.EqualTo(true));
            Assert.That(d[Options.DetectAndReportHangSampling], Is.EqualTo(false));
            Assert.That(d[Options.DetectAndReportAnrSampling], Is.EqualTo(false));
            Assert.That(d[Options.DetectAndReportCrashNdk], Is.EqualTo(false));
            Assert.That(d[Options.CrashNdkIncludeTombstoneLogs], Is.EqualTo(true));
            Assert.That(d, Has.Count.EqualTo(7));
        }

        [Test]
        public void The_keys_are_the_android_sdk_keys()
        {
            Assert.That(Options.MaxDataSize, Is.EqualTo("com.bugsee.option.config.max-data-size"));
            Assert.That(Options.NotifyFlushDelay, Is.EqualTo("com.bugsee.option.config.notify-flush-delay"));
            Assert.That(Options.MaxPendingReports, Is.EqualTo("com.bugsee.option.config.max-pending-reports"));
            Assert.That(Options.MaxPendingReportAge, Is.EqualTo("com.bugsee.option.config.max-pending-report-age"));
            Assert.That(Options.DetectAndReportExitLowMemoryBackgroundAsError, Is.EqualTo("com.bugsee.option.detect.exit.bg_low_memory_as_error"));
            Assert.That(Options.DetectAndReportHangSampling, Is.EqualTo("com.bugsee.option.detect.hang.sampling"));
            Assert.That(Options.DetectAndReportAnrSampling, Is.EqualTo("com.bugsee.option.detect.anr.sampling"));
            Assert.That(Options.DetectAndReportCrashNdk, Is.EqualTo("com.bugsee.option.detect.crash-ndk"));
            Assert.That(Options.CrashNdkIncludeTombstoneLogs, Is.EqualTo("com.bugsee.option.crash-ndk.tombstone-logs"));
        }

        [Test]
        public void Android_only_keys_are_dropped_from_an_ios_launch_and_kept_for_android()
        {
            var d = AndroidOnly();
            Assert.That(OptionPlatformGate.ForIos(d), Is.Empty, "an Android-only typed option reached the iOS launch");
            Assert.That(OptionPlatformGate.ForAndroid(d).Count, Is.EqualTo(d.Count));
            Assert.That(OptionPlatformGate.AndroidOnly, Is.SupersetOf(d.Keys));
        }

        [Test]
        public void The_shared_data_limit_and_flush_delay_reach_both_platforms()
        {
            foreach (BugseeLaunchOptions o in new BugseeLaunchOptions[]
            {
                new AndroidLaunchOptions { MaxDataSize = 64, NotifyFlushDelay = 250 },
                new IOSLaunchOptions { MaxDataSize = 64, NotifyFlushDelay = 250 },
            })
            {
                var d = o.ToDictionary();
                Assert.That(d[Options.MaxDataSize], Is.EqualTo(64).And.TypeOf<int>());
                Assert.That(d[Options.NotifyFlushDelay], Is.EqualTo(250).And.TypeOf<int>());
                Assert.That(OptionPlatformGate.ForIos(d), Has.Count.EqualTo(2));
                Assert.That(OptionPlatformGate.ForAndroid(d), Has.Count.EqualTo(2));
            }
        }

        [Test]
        public void The_leak_module_keys_stay_gated_off_ios_although_options_has_no_constant()
        {
            var d = new Dictionary<string, object>
            {
                ["com.bugsee.option.detect.memory_leaks"] = true,
                ["com.bugsee.option.detect.thread_leaks"] = true,
            };
            Assert.That(OptionPlatformGate.ForIos(d), Is.Empty);
        }
    }
}
