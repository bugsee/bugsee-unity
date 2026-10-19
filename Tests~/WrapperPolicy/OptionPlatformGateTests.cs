using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Bugsee.Contracts.Options;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class OptionPlatformGateTests
    {
        // wrapper_info is added after the platform gate, so Android-only keys are still stripped first.
        const string ForIosJsonCall = "ToJsonObject(WithWrapperInfo(OptionPlatformGate.ForIos(options)))";

        static string FindRepoRoot()
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, "package.json")))
                    return dir;
                var parent = Directory.GetParent(dir);
                dir = parent?.FullName;
            }

            throw new InvalidOperationException("Could not locate repo root (package.json).");
        }

        static string RepoFile(string relative)
        {
            return Path.GetFullPath(Path.Combine(FindRepoRoot(), relative));
        }

        static HashSet<string> ExpectedAndroidOnlyKeysFromOptionsContract()
        {
            var expected = new HashSet<string>();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
            foreach (var field in typeof(Options).GetFields(flags))
            {
                if (field.FieldType != typeof(string))
                    continue;
                var name = field.Name;
                if (name.StartsWith("DetectAndReportExit", StringComparison.Ordinal)
                    && !string.Equals(name, nameof(Options.DetectAndReportExit), StringComparison.Ordinal))
                    expected.Add((string)field.GetValue(null));
            }

            expected.Add(Options.ReportingTriggerByNotification);
            expected.Add(Options.ReportingTriggerByBroadcast);
            expected.Add(Options.CaptureLogsUseAllSources);
            expected.Add(Options.CaptureRespectFlagSecure);
            expected.Add(Options.CaptureVideoMode);
            expected.Add(Options.CaptureVideoUsingCustomMuxer);
            expected.Add(Options.CaptureVideoSecureScrolling);
            expected.Add(Options.CaptureVideoFullscreenRememberUserDecision);
            expected.Add(Options.CaptureVideoFullscreenKeepRunning);
            expected.Add(Options.ReportHandlerCallbackTimeout);
            expected.Add(Options.PerformanceUploadMode);
            expected.Add(Options.ReportProcessingInProcess);
            expected.Add(Options.WebViewDomainAllowlist);
            return expected;
        }

        [Test]
        public void Ios_launch_drops_android_exit_and_trigger_keys()
        {
            var options = new Dictionary<string, object>
            {
                [Options.CaptureVideo] = true,
                [Options.DetectAndReportExit] = false,
                [Options.DetectAndReportExitLowMemory] = true,
                [Options.ReportingTriggerByNotification] = true,
                [Options.Endpoint] = "https://example.test"
            };

            var ios = OptionPlatformGate.ForIos(options);

            Assert.That(ios.ContainsKey(Options.CaptureVideo), Is.True);
            Assert.That(ios.ContainsKey(Options.Endpoint), Is.True);
            Assert.That(ios.ContainsKey(Options.DetectAndReportExit), Is.True);
            Assert.That(ios[Options.DetectAndReportExit], Is.EqualTo(false));
            Assert.That(ios.ContainsKey(Options.DetectAndReportExitLowMemory), Is.False);
            Assert.That(ios.ContainsKey(Options.ReportingTriggerByNotification), Is.False);
        }

        [Test]
        public void Null_input_returns_empty_dictionary()
        {
            var ios = OptionPlatformGate.ForIos(null);
            Assert.That(ios, Is.Not.Null);
            Assert.That(ios.Count, Is.EqualTo(0));
        }

        [Test]
        public void Empty_input_returns_empty_dictionary()
        {
            var ios = OptionPlatformGate.ForIos(new Dictionary<string, object>());
            Assert.That(ios.Count, Is.EqualTo(0));
        }

        [Test]
        public void Every_android_only_constant_is_dropped()
        {
            var expected = ExpectedAndroidOnlyKeysFromOptionsContract();
            Assert.That(OptionPlatformGate.AndroidOnly, Is.EquivalentTo(expected));

            foreach (var key in expected)
            {
                var options = new Dictionary<string, object>
                {
                    [key] = true,
                    [Options.CaptureVideo] = false
                };

                var ios = OptionPlatformGate.ForIos(options);

                Assert.That(ios.ContainsKey(key), Is.False, "expected Android-only key to be removed: " + key);
                Assert.That(ios.ContainsKey(Options.CaptureVideo), Is.True);
                Assert.That(ios[Options.CaptureVideo], Is.EqualTo(false));
            }
        }

        [Test]
        public void Debug_and_unknown_keys_pass_through_with_copied_values()
        {
            var sentinel = new object();
            var options = new Dictionary<string, object>
            {
                [Options.Debug] = true,
                ["com.bugsee.option.custom.unknown"] = sentinel
            };

            var ios = OptionPlatformGate.ForIos(options);

            Assert.That(ios[Options.Debug], Is.EqualTo(true));
            Assert.That(ios["com.bugsee.option.custom.unknown"], Is.SameAs(sentinel));
        }

        [Test]
        public void Input_dictionary_is_not_mutated()
        {
            var options = new Dictionary<string, object>
            {
                [Options.DetectAndReportExit] = true,
                [Options.ReportingTriggerByBroadcast] = false,
                [Options.Endpoint] = "https://example.test"
            };

            var copyCount = options.Count;
            _ = OptionPlatformGate.ForIos(options);

            Assert.That(options.Count, Is.EqualTo(copyCount));
            Assert.That(options.ContainsKey(Options.DetectAndReportExit), Is.True);
            Assert.That(options.ContainsKey(Options.ReportingTriggerByBroadcast), Is.True);
        }

        [Test]
        public void IosBridge_launch_and_relaunch_call_ForIos_before_json()
        {
            string text = File.ReadAllText(RepoFile("Runtime/Platform/IOS/IOSBridge.cs"));
            Assert.That(
                CountOccurrences(text, "_bugsee_launch(appToken, " + ForIosJsonCall + ","),
                Is.EqualTo(1),
                "Launch must JSON-encode OptionPlatformGate.ForIos(options)");
            Assert.That(
                CountOccurrences(text, "_bugsee_relaunch(" + ForIosJsonCall + ","),
                Is.EqualTo(1),
                "Relaunch must JSON-encode OptionPlatformGate.ForIos(options)");
        }

        static int CountOccurrences(string text, string pattern)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(pattern))
                return 0;
            var count = 0;
            var index = 0;
            while (true)
            {
                index = text.IndexOf(pattern, index, StringComparison.Ordinal);
                if (index < 0)
                    return count;
                count++;
                index += pattern.Length;
            }
        }

        [Test]
        public void Android_launch_drops_ios_only_keys_and_keeps_shared_and_android_keys()
        {
            var options = new Dictionary<string, object>
            {
                [Options.CaptureCrashHandlerType] = 1,
                [Options.CaptureVideo] = true,
                [Options.CaptureVideoMode] = 2,
                [Options.CaptureVideoSecureScrolling] = true,
                [Options.PerformanceUploadMode] = 1
            };

            var android = OptionPlatformGate.ForAndroid(options);

            Assert.That(android.ContainsKey(Options.CaptureCrashHandlerType), Is.False);
            Assert.That(android.ContainsKey(Options.CaptureVideo), Is.True);
            Assert.That(android.ContainsKey(Options.CaptureVideoMode), Is.True);
            Assert.That(android.ContainsKey(Options.CaptureVideoSecureScrolling), Is.True);
            Assert.That(android.ContainsKey(Options.PerformanceUploadMode), Is.True);
            Assert.That(options.ContainsKey(Options.CaptureCrashHandlerType), Is.True, "input must not be mutated");
            Assert.That(OptionPlatformGate.IosOnly, Is.EquivalentTo(new[] { Options.CaptureCrashHandlerType }));
        }

        [Test]
        public void Ios_launch_drops_the_keys_ios_does_not_know_but_keeps_crash_handler_type()
        {
            var options = new Dictionary<string, object>
            {
                [Options.CaptureCrashHandlerType] = 1,
                [Options.CaptureVideoSecureScrolling] = true,
                [Options.CaptureVideoFullscreenRememberUserDecision] = true,
                [Options.CaptureVideoFullscreenKeepRunning] = true,
                [Options.ReportHandlerCallbackTimeout] = 5,
                [Options.PerformanceUploadMode] = 1
            };

            var ios = OptionPlatformGate.ForIos(options);

            Assert.That(ios.Keys, Is.EquivalentTo(new[] { Options.CaptureCrashHandlerType }));
        }

        [Test]
        public void Android_gate_handles_null_and_empty()
        {
            Assert.That(OptionPlatformGate.ForAndroid(null).Count, Is.EqualTo(0));
            Assert.That(OptionPlatformGate.ForAndroid(new Dictionary<string, object>()).Count, Is.EqualTo(0));
        }

        [Test]
        public void AndroidBridge_launch_and_relaunch_call_ForAndroid_before_the_java_map()
        {
            string text = File.ReadAllText(RepoFile("Runtime/Platform/Android/AndroidBridge.cs"));
            Assert.That(CountOccurrences(text, "AndroidOptionsMapper.ToJavaMap(OptionPlatformGate.ForAndroid(options))"), Is.EqualTo(2));
            Assert.That(CountOccurrences(text, "ToJavaMap("), Is.EqualTo(2), "every map handed to native must be gated");
        }
    }
}
