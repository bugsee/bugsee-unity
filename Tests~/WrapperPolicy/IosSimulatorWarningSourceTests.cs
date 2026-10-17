using System.IO;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class IosSimulatorWarningSourceTests
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

                dir = Directory.GetParent(dir)?.FullName;
            }

            Assert.Fail("Could not find repo root (package.json)");
            return null;
        }

        // bugsee-cocoa files no managed exception on the simulator or Mac Catalyst; the bridge says so once, at launch.
        [Test]
        public void Bridge_warns_once_at_launch_and_relaunch_on_simulator_and_catalyst()
        {
            string text = File.ReadAllText(Path.Combine(FindRepoRoot(), "Plugins", "iOS", "BugseeUnityBridge.mm"));
            int warn = text.IndexOf("static void BugseeWarnIfExceptionsNotFiled(void)");
            Assert.That(warn, Is.GreaterThan(0));
            string body = text.Substring(warn, text.IndexOf("extern \"C\" {", warn) - warn);
            Assert.That(body, Does.Contain("#if TARGET_OS_SIMULATOR || TARGET_OS_MACCATALYST"));
            Assert.That(body, Does.Contain("dispatch_once"));
            Assert.That(body, Does.Contain("not filed here"));

            foreach (var entry in new[] { "void _bugsee_launch(", "void _bugsee_relaunch(" })
            {
                int start = text.IndexOf(entry);
                Assert.That(start, Is.GreaterThan(0), entry);
                int open = text.IndexOf("{", start);
                Assert.That(text.Substring(open, 120), Does.Contain("BugseeWarnIfExceptionsNotFiled();"), entry);
            }
        }
    }
}
