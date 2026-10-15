using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    /// <summary>
    /// The injector over Gradle files real Unity editors generated (Unity 6000.0.84f1 exported, not built; Unity
    /// 2022.3.62f3 from an APK build and from Export Project), minus Bugsee's own edits. Fixture sets:
    /// <list type="bullet">
    /// <item>Unity6000.0.84f1-default: a bare project with no custom Gradle templates (AGP 9.0.0, Java 17 launcher).</item>
    /// <item>Unity6000.0.84f1-sample-templates: the BugseeMiniGame sample, whose root and launcher files come from
    /// the CUSTOM base/launcher templates the package writes into Assets/Plugins/Android (AGP 7.4.2, Java 11).</item>
    /// <item>Unity2022.3.62f3-default: the sample's generated project from an APK build, no custom templates
    /// (AGP 7.4.2, Gradle 7.5.1, Java 11 launcher; the root build.gradle is the base file). Unity 2022.3's launcher
    /// has no debugSymbolLevel, so the fixture is without the release <c>ndk { debugSymbolLevel 'FULL' }</c> block the package patches in.</item>
    /// <item>Unity2022.3.62f3-export: the same project from Export Project. Only unityLibrary differs: it carries
    /// the IL2CPP/NDK build tasks that Unity runs itself for an APK build.</item>
    /// </list>
    /// </summary>
    public class UnityGeneratedLayoutTests
    {
        static readonly string[] Sets =
        {
            "Unity6000.0.84f1-default", "Unity6000.0.84f1-sample-templates",
            "Unity2022.3.62f3-default", "Unity2022.3.62f3-export",
        };
        static readonly string[] Unity2022Sets = { "Unity2022.3.62f3-default", "Unity2022.3.62f3-export" };
        static readonly string[] Artifacts = { "bugsee-android", "bugsee-android-ndk" };

        // The anchors and payloads BugseeAndroidGradleSetup passes to MarkedGradleBlock.Apply.
        const string RootAnchor = "plugins {";
        const string RootLine = "    id 'com.bugsee.android.gradle' version '4.0.8' apply false // bugsee:gradle-plugin";
        const string LauncherAnchor = "apply plugin: 'com.android.application'";
        const string LauncherPatch =
            "apply plugin: 'com.bugsee.android.gradle' // bugsee:gradle-plugin\n\n" +
            "bugsee {\n" +
            "    // App token is supplied at runtime via Bugsee.Launch.\n" +
            "    ndk {\n" +
            "        enabled = true\n" +
            "    }\n" +
            "} // bugsee:gradle-ndk";

        static string Read(string set, string name)
        {
            return File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", set, name));
        }

        static int Count(string text, string needle)
        {
            return Regex.Matches(text, Regex.Escape(needle)).Count;
        }

        static GradleMavenInjection.DependencyResult Apply(string gradle)
        {
            return GradleMavenInjection.ApplyDependencies(gradle, "com.bugsee", Artifacts, "7.4.1");
        }

        [TestCaseSource(nameof(Sets))]
        public void Library_dependencies_land_in_the_top_level_block(string set)
        {
            string result = Apply(Read(set, "unityLibrary.build.gradle")).Text;

            int begin = result.IndexOf("// bugsee:maven-dependencies begin", System.StringComparison.Ordinal);
            Assert.That(begin, Is.GreaterThan(0));
            Assert.That(result.Substring(0, begin).TrimEnd().EndsWith("dependencies {"), Is.True, "first entry of dependencies");
            Assert.That(result, Does.Contain("    implementation 'com.bugsee:bugsee-android:7.4.1'"));
            Assert.That(result, Does.Contain("    implementation 'com.bugsee:bugsee-android-ndk:7.4.1'"));
        }

        [TestCaseSource(nameof(Sets))]
        public void Removing_the_injected_region_gives_back_the_original_text(string set)
        {
            string raw = Read(set, "unityLibrary.build.gradle");
            string result = Apply(raw).Text;

            string stripped = Regex.Replace(
                result, @"[ \t]*// bugsee:maven-dependencies begin.*?// bugsee:maven-dependencies end\r?\n", string.Empty, RegexOptions.Singleline);

            Assert.That(stripped, Is.EqualTo(raw));
        }

        [TestCaseSource(nameof(Sets))]
        public void A_second_run_changes_nothing_and_keeps_one_region(string set)
        {
            string once = Apply(Read(set, "unityLibrary.build.gradle")).Text;
            string twice = Apply(once).Text;

            Assert.That(twice, Is.EqualTo(once));
            Assert.That(Count(twice, "bugsee:maven-dependencies begin"), Is.EqualTo(1));
            Assert.That(Count(twice, "bugsee:maven-dependencies end"), Is.EqualTo(1));
            Assert.That(Count(twice, "bugsee-android:7.4.1"), Is.EqualTo(1));
        }

        [TestCaseSource(nameof(Sets))]
        public void Settings_already_resolve_from_maven_central_so_nothing_is_added(string set)
        {
            string settings = Read(set, "settings.gradle");
            string root = Read(set, "root.build.gradle");

            var result = GradleMavenInjection.EnsureProjectRepositories(settings, root);

            Assert.That(result.Mode, Is.EqualTo(GradleMavenInjection.RepositoriesMode.PreferSettings));
            Assert.That(result.SettingsStatus, Is.EqualTo(GradleMavenInjection.RepositoryStatus.AlreadyPresent));
            Assert.That(result.Found, Is.True);
            Assert.That(result.Settings, Is.EqualTo(settings));
            Assert.That(result.RootBuild, Is.EqualTo(root), "PREFER_SETTINGS: project repositories are ignored");
        }

        [TestCaseSource(nameof(Sets))]
        public void Without_maven_central_in_settings_it_is_inserted_in_settings_only(string set)
        {
            string settings = Read(set, "settings.gradle");
            string withoutCentral = Regex.Replace(
                settings, @"(dependencyResolutionManagement.*?)\n\s*mavenCentral\(\)", "$1", RegexOptions.Singleline);
            Assert.That(withoutCentral, Is.Not.EqualTo(settings), "fixture sanity");
            string root = Read(set, "root.build.gradle");

            var result = GradleMavenInjection.EnsureProjectRepositories(withoutCentral, root);

            Assert.That(result.SettingsStatus, Is.EqualTo(GradleMavenInjection.RepositoryStatus.Inserted));
            Assert.That(Count(result.Settings, "bugsee:maven-repository"), Is.EqualTo(1));
            Assert.That(result.RootBuild, Is.EqualTo(root));
        }

        [TestCaseSource(nameof(Sets))]
        public void Gradle_plugin_lines_go_into_the_root_plugins_block_once(string set)
        {
            string raw = Read(set, "root.build.gradle");

            string once = MarkedGradleBlock.Apply(raw, RootAnchor, RootLine);
            string twice = MarkedGradleBlock.Apply(once, RootAnchor, RootLine);

            Assert.That(once, Is.Not.EqualTo(raw));
            Assert.That(twice, Is.EqualTo(once));
            Assert.That(Count(once, "bugsee:gradle-plugin"), Is.EqualTo(1));
            int plugins = once.IndexOf("plugins {", System.StringComparison.Ordinal);
            int line = once.IndexOf("com.bugsee.android.gradle", System.StringComparison.Ordinal);
            int close = once.IndexOf("\n}", plugins, System.StringComparison.Ordinal);
            Assert.That(line, Is.InRange(plugins, close));
            Assert.That(once.Replace(RootLine + "\n", string.Empty), Is.EqualTo(raw));
        }

        [TestCaseSource(nameof(Sets))]
        public void Gradle_plugin_and_ndk_block_follow_the_launcher_application_plugin_once(string set)
        {
            string raw = Read(set, "launcher.build.gradle");

            string once = MarkedGradleBlock.Apply(raw, LauncherAnchor, LauncherPatch);
            string twice = MarkedGradleBlock.Apply(once, LauncherAnchor, LauncherPatch);

            Assert.That(twice, Is.EqualTo(once));
            Assert.That(Count(once, "bugsee:gradle-plugin"), Is.EqualTo(1));
            Assert.That(Count(once, "bugsee:gradle-ndk"), Is.EqualTo(1));
            Assert.That(once.IndexOf("com.bugsee.android.gradle", System.StringComparison.Ordinal),
                Is.GreaterThan(once.IndexOf(LauncherAnchor, System.StringComparison.Ordinal)));
            Assert.That(once.IndexOf("bugsee {", System.StringComparison.Ordinal),
                Is.LessThan(once.IndexOf("android {", System.StringComparison.Ordinal)));
        }

        [TestCaseSource(nameof(Unity2022Sets))]
        public void Unity_2022_3_pins_an_AGP_older_than_the_Bugsee_Gradle_plugin_needs(string set)
        {
            string root = Read(set, "root.build.gradle");

            Assert.That(root, Does.Contain("id 'com.android.application' version '7.4.2' apply false"));
            Assert.That(Read(set, "gradle-wrapper.properties"), Does.Contain("gradle-7.5.1-bin.zip"));
        }

        [Test]
        public void Unity_2022_3_export_library_keeps_the_native_build_tasks_the_apk_build_leaves_out()
        {
            string apk = Read("Unity2022.3.62f3-default", "unityLibrary.build.gradle");
            string export = Read("Unity2022.3.62f3-export", "unityLibrary.build.gradle");

            Assert.That(export, Does.Contain("def BuildIl2Cpp("));
            Assert.That(apk, Does.Not.Contain("def BuildIl2Cpp("));
            Assert.That(Apply(export).Text, Does.Contain("// bugsee:maven-dependencies begin"));
        }
    }
}
