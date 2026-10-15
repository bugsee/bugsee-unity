using System.IO;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    /// <summary>
    /// The injector over Gradle files Unity actually generated. Fixtures/Unity6000.0.84f1 is an exported (not built)
    /// Android Gradle project of the sample from the real Unity 6000.0.84f1 editor with default Gradle templates,
    /// minus Bugsee's own edits. No Unity 2022 LTS export is checked in: that editor could not be installed (#48).
    /// </summary>
    public class UnityGeneratedLayoutTests
    {
        const string Dir = "Fixtures/Unity6000.0.84f1/";
        static readonly string[] Artifacts = { "bugsee-android", "bugsee-android-ndk" };

        static string Read(string name)
        {
            return File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, Dir, name));
        }

        [Test]
        public void Library_dependencies_land_in_the_top_level_block_and_nothing_else_changes()
        {
            string raw = Read("unityLibrary.build.gradle");
            var result = GradleMavenInjection.ApplyDependencies(raw, "com.bugsee", Artifacts, "7.4.1");
            var again = GradleMavenInjection.ApplyDependencies(result.Text, "com.bugsee", Artifacts, "7.4.1");

            Assert.That(result.Text, Does.Contain("    implementation 'com.bugsee:bugsee-android:7.4.1'"));
            Assert.That(result.Text, Does.Contain("    implementation 'com.bugsee:bugsee-android-ndk:7.4.1'"));
            Assert.That(again.Text, Is.EqualTo(result.Text));
            int begin = result.Text.IndexOf("bugsee:maven-dependencies begin", System.StringComparison.Ordinal);
            Assert.That(result.Text.LastIndexOf("dependencies {", begin, System.StringComparison.Ordinal), Is.GreaterThanOrEqualTo(0));
            Assert.That(result.Text.Substring(0, begin).Contains("android {"), Is.False, "before the android block");
        }

        [Test]
        public void Settings_already_resolve_from_maven_central_so_nothing_is_added()
        {
            string settings = Read("settings.gradle");

            Assert.That(GradleMavenInjection.GetRepositoriesMode(settings),
                Is.EqualTo(GradleMavenInjection.RepositoriesMode.PreferSettings));
            var status = GradleMavenInjection.EnsureMavenCentral(settings, "dependencyResolutionManagement", out string updated);
            Assert.That(status, Is.EqualTo(GradleMavenInjection.RepositoryStatus.AlreadyPresent));
            Assert.That(updated, Is.EqualTo(settings));
        }
    }
}
