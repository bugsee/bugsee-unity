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
        const string ForIosJsonCall = "ToJsonObject(OptionPlatformGate.ForIos(options))";

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
                if (name.StartsWith("DetectAndReportExit", StringComparison.Ordinal))
                    expected.Add((string)field.GetValue(null));
            }

            expected.Add(Options.ReportingTriggerByNotification);
            expected.Add(Options.ReportingTriggerByBroadcast);
            return expected;
        }

        [Test]
        public void Ios_launch_drops_android_exit_and_trigger_keys()
        {
            var options = new Dictionary<string, object>
            {
                [Options.CaptureVideo] = true,
                [Options.DetectAndReportExit] = true,
                [Options.ReportingTriggerByNotification] = true,
                [Options.Endpoint] = "https://example.test"
            };

            var ios = OptionPlatformGate.ForIos(options);

            Assert.That(ios.ContainsKey(Options.CaptureVideo), Is.True);
            Assert.That(ios.ContainsKey(Options.Endpoint), Is.True);
            Assert.That(ios.ContainsKey(Options.DetectAndReportExit), Is.False);
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
                CountOccurrences(text, "_bugsee_launch(appToken, " + ForIosJsonCall + ")"),
                Is.EqualTo(1),
                "Launch must JSON-encode OptionPlatformGate.ForIos(options)");
            Assert.That(
                CountOccurrences(text, "_bugsee_relaunch(" + ForIosJsonCall + ")"),
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
        public void AndroidBridge_does_not_reference_option_platform_gate()
        {
            string text = File.ReadAllText(RepoFile("Runtime/Platform/Android/AndroidBridge.cs"));
            Assert.That(text, Does.Not.Contain("OptionPlatformGate"));
        }
    }
}
