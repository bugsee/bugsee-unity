using System;
using System.Collections.Generic;
using Bugsee.Contracts.Options;
using Bugsee.WrapperPolicy;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    /// <summary>
    /// The iOS-only typed launch options (#97): each property writes its key with the wire type the iOS SDK reads
    /// (bool, int, string; enums as their SDK int), and the platform gate keeps them out of an Android launch.
    /// </summary>
    public class IosLaunchOptionsTests
    {
        static Dictionary<string, object> Typed()
        {
            var o = new IOSLaunchOptions
            {
                CrashHandlerType = CrashHandlerType.Mach,
                CrashSignals = CrashSignals.Abrt | CrashSignals.Trap,
                CaptureOnDeviceSymbolication = true,
                CrashSwiftAsyncStackTraces = false,
                CrashFillEmptyExceptionStacks = true,
                CaptureWebSocket = false,
                CaptureAVPlayer = true,
                CaptureSampleBufferDisplayLayer = true,
                CaptureCameraPreview = true,
                CaptureBluetoothStatus = true,
                CaptureDiskSpace = true,
                CaptureDeviceNetworkNames = true,
                CaptureVideoPrivacyBlur = false,
                CaptureVideoMinFrameRate = 5,
                CaptureVideoMaxFrameRate = 24,
                DataEncryption = DataEncryptionMode.OnDevice,
                BuildTarget = "Game",
                BuildType = "qa",
                ReportingUIStyle = ReportingUIStyle.Dusk,
            };
            return o.ToDictionary();
        }

        [Test]
        public void Every_property_writes_its_key_with_the_wire_type()
        {
            var d = Typed();
            Assert.That(d[Options.CaptureCrashHandlerType], Is.EqualTo(2).And.TypeOf<int>());
            Assert.That(d[Options.CaptureCrashSignals], Is.EqualTo(1 | 32).And.TypeOf<int>());
            Assert.That(d[Options.CaptureOnDeviceSymbolication], Is.EqualTo(true));
            Assert.That(d[Options.DetectCrashSwiftAsyncStackTraces], Is.EqualTo(false));
            Assert.That(d[Options.DetectCrashFillEmptyExceptionStacks], Is.EqualTo(true));
            Assert.That(d[Options.CaptureWebSocket], Is.EqualTo(false));
            Assert.That(d[Options.CaptureAVPlayer], Is.EqualTo(true));
            Assert.That(d[Options.CaptureSampleBufferDisplayLayer], Is.EqualTo(true));
            Assert.That(d[Options.CaptureCameraPreview], Is.EqualTo(true));
            Assert.That(d[Options.CaptureBluetoothStatus], Is.EqualTo(true));
            Assert.That(d[Options.CaptureDiskSpace], Is.EqualTo(true));
            Assert.That(d[Options.CaptureDeviceNetworkNames], Is.EqualTo(true));
            Assert.That(d[Options.CaptureVideoPrivacyBlur], Is.EqualTo(false));
            Assert.That(d[Options.CaptureVideoMinFrameRate], Is.EqualTo(5).And.TypeOf<int>());
            Assert.That(d[Options.CaptureVideoMaxFrameRate], Is.EqualTo(24).And.TypeOf<int>());
            Assert.That(d[Options.DataEncryption], Is.EqualTo(1).And.TypeOf<int>());
            Assert.That(d[Options.BuildTarget], Is.EqualTo("Game"));
            Assert.That(d[Options.BuildType], Is.EqualTo("qa"));
            Assert.That(d[Options.ReportingUIStyle], Is.EqualTo(1).And.TypeOf<int>());
        }

        [Test]
        public void Every_typed_key_is_gated_off_Android_and_kept_for_iOS()
        {
            var d = Typed();
            Assert.That(OptionPlatformGate.ForAndroid(d), Is.Empty, "an iOS-only typed option reached the Android launch");
            Assert.That(OptionPlatformGate.ForIos(d).Count, Is.EqualTo(d.Count));
            Assert.That(OptionPlatformGate.IosOnly, Is.SupersetOf(d.Keys));
        }

        [Test]
        public void A_null_string_removes_the_key()
        {
            var o = new IOSLaunchOptions { BuildTarget = "x" };
            o.BuildTarget = null;
            Assert.That(o.ToDictionary(), Is.Empty);
        }

        [Test]
        public void The_enum_values_are_the_sdk_values()
        {
            Assert.That((int)CrashSignals.Default, Is.EqualTo(128));
            Assert.That((int)CrashSignals.All, Is.EqualTo(63));
            Assert.That((int)DataEncryptionMode.EndToEnd, Is.EqualTo(2));
            Assert.That((int)ReportingUIStyle.System, Is.EqualTo(3));
            Assert.That((int)ReportingUIStyle.BasedOnStatusBar, Is.EqualTo(2));
        }

        [Test]
        public void The_builder_helpers_write_the_same_wire_values()
        {
            var d = new OptionsBuilder().SetCrashHandlerType(CrashHandlerType.BSD).SetCrashSignals(CrashSignals.All)
                .SetDataEncryption(DataEncryptionMode.EndToEnd).SetReportingUIStyle(ReportingUIStyle.Light).ToDictionary();
            Assert.That(d[Options.CaptureCrashHandlerType], Is.EqualTo(1));
            Assert.That(d[Options.CaptureCrashSignals], Is.EqualTo(63));
            Assert.That(d[Options.DataEncryption], Is.EqualTo(2));
            Assert.That(d[Options.ReportingUIStyle], Is.EqualTo(0));
            Assert.That(OptionPlatformGate.ForAndroid(d), Is.Empty);
        }
    }
}
