using System.IO;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class IosRegistrationSourceTests
    {
        static string FindRepoRoot()
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, "package.json")))
                {
                    return dir;
                }

                var parent = Directory.GetParent(dir)?.FullName;
                if (string.IsNullOrEmpty(parent) || parent == dir)
                {
                    break;
                }

                dir = parent;
            }

            Assert.Fail("Could not find repo root (package.json)");
            return null;
        }

        [Test]
        public void Wrapper_is_created_in_load_and_ensure_does_not_allocate()
        {
            string root = FindRepoRoot();
            string path = Path.Combine(root, "Plugins", "iOS", "BugseeUnityCallbacks.mm");
            string text = File.ReadAllText(path);
            Assert.That(text, Does.Contain("+ (void)load"));
            Assert.That(text, Does.Contain("[Bugsee setWrapper:"));
            int ensure = text.IndexOf("void _bugsee_ensure_wrapper");
            int next = text.IndexOf("void _bugsee_", ensure + 10);
            string body = text.Substring(ensure, next - ensure);
            if (body.Contains("BugseeUnityWrapper new"))
            {
                Assert.That(body.IndexOf("if (!gWrapper)"), Is.LessThan(body.IndexOf("BugseeUnityWrapper new")));
            }
        }

        [Test]
        public void Native_callbacks_encode_json_with_the_tested_JsonWire()
        {
            string text = File.ReadAllText(Path.Combine(FindRepoRoot(), "Runtime", "Platform", "IOS", "IosNativeCallbacks.cs"));
            Assert.That(text, Does.Not.Contain("static string JsonString("));
            Assert.That(text, Does.Contain("JsonWire.String("));
        }

        [Test]
        public void LogException_options_cross_to_the_native_bridge()
        {
            string text = File.ReadAllText(Path.Combine(FindRepoRoot(), "Runtime", "Platform", "IOS", "IOSBridge.cs"));
            Assert.That(text, Does.Contain("static extern void _bugsee_logException(string name, string reason, bool handled, string optionsJson);"));
            Assert.That(text, Does.Contain("ManagedExceptionPayload.ToJson(payload), true,\r\n") .Or.Contain("ManagedExceptionPayload.ToJson(payload), true,\n"));
            Assert.That(text, Does.Contain("ManagedExceptionPayload.ToJson(payload), false,\r\n").Or.Contain("ManagedExceptionPayload.ToJson(payload), false,\n"));
            Assert.That(System.Text.RegularExpressions.Regex.Matches(text, @"ToJsonObject\(options\)\);").Count, Is.EqualTo(2));
        }

        // The simulator SDK files no crash or exception report: the bridge says so once, natively and in the Unity log.
        [Test]
        public void Simulator_warning_is_guarded_once_only_and_reaches_the_unity_log()
        {
            string root = FindRepoRoot();
            string mm = File.ReadAllText(Path.Combine(root, "Plugins", "iOS", "BugseeUnityBridge.mm"));
            int warn = mm.IndexOf("static void BugseeWarnIfExceptionsNotFiled(void)");
            Assert.That(warn, Is.GreaterThan(0));
            string body = mm.Substring(warn, mm.IndexOf("\n}\n", warn) - warn);
            Assert.That(body, Does.Contain("#if TARGET_OS_SIMULATOR || TARGET_OS_MACCATALYST"));
            Assert.That(body, Does.Contain("dispatch_once"));
            Assert.That(body, Does.Contain("NSLog("));
            // The counter exists only for the tests: a device build must not define an unused variable.
            int counter = mm.IndexOf("static int gSimulatorWarningCount");
            Assert.That(mm.Substring(mm.LastIndexOf("#if", counter), counter - mm.LastIndexOf("#if", counter)),
                Does.StartWith("#if BUGSEE_UNITY_TESTS"));
            foreach (var entry in new[] { "void _bugsee_launch(", "void _bugsee_relaunch(" })
            {
                int start = mm.IndexOf(entry);
                Assert.That(start, Is.GreaterThan(0), entry);
                int open = mm.IndexOf("{\n", start);
                Assert.That(mm.Substring(open, mm.IndexOf("\n", open + 3) - open + 40),
                    Does.Contain("BugseeWarnIfExceptionsNotFiled();"), entry);
            }
            Assert.That(mm, Does.Match(@"int _bugsee_exceptions_filed\(void\)\s*\{\s*#if TARGET_OS_SIMULATOR \|\| TARGET_OS_MACCATALYST\s*return 0;"));

            string cs = File.ReadAllText(Path.Combine(root, "Runtime", "Platform", "IOS", "IOSBridge.cs"));
            Assert.That(cs, Does.Contain("static extern int _bugsee_exceptions_filed();"));
            int helper = cs.IndexOf("static void WarnOnceIfExceptionsNotFiled()");
            Assert.That(helper, Is.GreaterThan(0));
            string csBody = cs.Substring(helper, cs.IndexOf("\n        }\n", helper) - helper);
            Assert.That(csBody, Does.Contain("_exceptionsFiledWarned = true;"));
            Assert.That(csBody, Does.Contain("_bugsee_exceptions_filed() != 0"));
            Assert.That(csBody, Does.Contain("Debug.LogWarning(\"[Bugsee]"));
            foreach (var pair in new[] { ("void LaunchNow(", "_bugsee_launch("), ("void RelaunchNow(", "_bugsee_relaunch(") })
            {
                int method = cs.IndexOf(pair.Item1);
                Assert.That(method, Is.GreaterThan(0), pair.Item1);
                Assert.That(cs.IndexOf("WarnOnceIfExceptionsNotFiled();", method),
                    Is.LessThan(cs.IndexOf(pair.Item2, method)).And.GreaterThan(method), pair.Item1);
            }
        }
    }
}
