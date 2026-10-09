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
    }
}
