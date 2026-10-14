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
    }
}
