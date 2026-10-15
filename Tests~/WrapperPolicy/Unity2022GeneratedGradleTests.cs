using System.IO;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    /// <summary>
    /// The AGP rule and the release symbol patch over Gradle files a real Unity 2022.3.62f3 editor generated
    /// (APK build and Export Project; AGP 7.4.2, Gradle 7.5.1), minus Bugsee's own edits.
    /// </summary>
    public class Unity2022GeneratedGradleTests
    {
        static readonly string[] Sets = { "Unity2022.3.62f3-default", "Unity2022.3.62f3-export" };

        static string Read(string set, string name)
        {
            return File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", set, name));
        }

        [TestCaseSource(nameof(Sets))]
        public void Generated_root_pins_AGP_7_4_so_the_Bugsee_Gradle_plugin_is_not_applied(string set)
        {
            string root = Read(set, "root.build.gradle");

            Assert.That(GradleTemplateDefaults.TryGetAgpVersion(root, out int major, out int minor), Is.True);
            Assert.That((major, minor), Is.EqualTo((7, 4)));
            Assert.That(GradleTemplateDefaults.AgpMeetsBugseePlugin(root), Is.False);
        }

        [TestCaseSource(nameof(Sets))]
        public void Release_symbol_level_is_added_to_the_generated_launcher_once(string set)
        {
            string raw = Read(set, "launcher.build.gradle");

            string once = GradleTemplateDefaults.EnsureReleaseDebugSymbols(raw);
            string twice = GradleTemplateDefaults.EnsureReleaseDebugSymbols(once);

            Assert.That(raw, Does.Not.Contain("debugSymbolLevel"), "Unity 2022.3 sets none itself");
            Assert.That(once, Does.Contain("        release {\n            ndk {\n                debugSymbolLevel 'FULL'\n            }\n"));
            Assert.That(twice, Is.EqualTo(once));
        }
    }
}
