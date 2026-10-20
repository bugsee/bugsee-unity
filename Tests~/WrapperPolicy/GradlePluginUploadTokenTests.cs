using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    /// <summary>
    /// The Gradle plugin's own mapping and native uploads skip in a Unity build: its token resolution reads only the
    /// extension (<c>appToken(...)</c>), <c>bugsee.properties</c> and the manifest, never the environment, and a Unity app has
    /// the token only at runtime (<c>Bugsee.Launch</c>). For an Export Project the launcher therefore reads
    /// <c>BUGSEE_APP_TOKEN</c> from the environment of the Gradle build, which keeps the token out of every file of the
    /// project. Real builds of this are in Documentation~/index.md.
    /// </summary>
    public class GradlePluginUploadTokenTests
    {
        const GradleMavenInjection.GradleDsl Groovy = GradleMavenInjection.GradleDsl.Groovy;
        const GradleMavenInjection.GradleDsl Kotlin = GradleMavenInjection.GradleDsl.Kotlin;
        const string Version = "4.0.8";

        static string Read(string set, string name)
        {
            return File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", set, name));
        }

        static string Source(string relative)
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, "package.json")))
                    return File.ReadAllText(Path.Combine(dir, relative));
                dir = Directory.GetParent(dir)?.FullName;
            }

            throw new InvalidOperationException("Could not locate repo root (package.json).");
        }

        static int Count(string text, string needle) => Regex.Matches(text, Regex.Escape(needle)).Count;

        static readonly string[] UnitySets = { "Unity6000.0.84f1-default", "Unity6000.0.84f1-sample-templates" };

        const string KotlinRoot =
            "plugins {\n" +
            "    id(\"com.android.application\") version \"9.0.0\" apply false\n" +
            "}\n";

        const string KotlinLauncher =
            "plugins {\n" +
            "    id(\"com.android.application\")\n" +
            "}\n" +
            "\n" +
            "android {\n" +
            "    namespace = \"com.example.game\"\n" +
            "}\n";

        [TestCaseSource(nameof(UnitySets))]
        public void Without_the_option_the_block_is_what_it_always_was(string set)
        {
            var patch = GradlePluginInjection.Apply(
                Read(set, "root.build.gradle"), Groovy, Read(set, "launcher.build.gradle"), Groovy, Version);

            Assert.That(patch.Launcher, Does.Contain(
                "bugsee {\n    // App token is supplied at runtime via Bugsee.Launch.\n    ndk {\n        enabled = true\n    }\n} // bugsee:gradle-ndk"));
            Assert.That(patch.Launcher, Does.Not.Contain("getenv"));
        }

        [TestCaseSource(nameof(UnitySets))]
        public void The_option_makes_the_launcher_read_the_token_from_the_Gradle_environment(string set)
        {
            var patch = GradlePluginInjection.Apply(
                Read(set, "root.build.gradle"), Groovy, Read(set, "launcher.build.gradle"), Groovy, Version, gradleUploads: true);

            Assert.That(patch.Applied, Is.True);
            Assert.That(patch.Launcher, Does.Contain("apply plugin: 'com.bugsee.android.gradle' // bugsee:gradle-plugin\n\nbugsee {\n"));
            Assert.That(patch.Launcher, Does.Contain("appToken(System.getenv('BUGSEE_APP_TOKEN'))"));
            Assert.That(patch.Launcher, Does.Contain("System.getenv('BUGSEE_NO_SYMBOL_UPLOAD') == null"), "the Editor's opt-out turns the plugin's uploads off too");
            Assert.That(patch.Launcher, Does.Contain("endpoint = System.getenv('BUGSEE_ENDPOINT')"));
            Assert.That(patch.Launcher, Does.Contain("cliPath = System.getenv('BUGSEE_CLI_PATH')"));
            Assert.That(patch.Launcher, Does.Contain("    ndk {\n        enabled = true\n        useMergedNativeLibs = false\n    }\n} // bugsee:gradle-ndk"),
                "the merge step's stripped Unity libraries must not be sent: they would block the real symbol tables");
            Assert.That(patch.GradleUploadsApplied, Is.True);
            Assert.That(Count(patch.Launcher, "bugsee {"), Is.EqualTo(1));
        }

        [Test]
        public void The_token_is_only_ever_read_from_the_environment_never_written()
        {
            // Every place the token could land in text: the Groovy and Kotlin blocks, with and without the option. The only
            // argument appToken() ever gets is the environment lookup, so no token (whatever its value) is stored in the project.
            foreach (bool kotlin in new[] { false, true })
            {
                foreach (bool uploads in new[] { false, true })
                {
                    string text = string.Join("\n", GradlePluginInjection.BlockLines(kotlin, uploads));
                    foreach (Match call in Regex.Matches(text, @"appToken\(.*\)"))
                    {
                        Assert.That(call.Value, Does.Match(kotlin
                            ? @"^appToken\(it\)$"
                            : @"^appToken\(System\.getenv\('BUGSEE_APP_TOKEN'\)\)$"));
                    }

                    Assert.That(text, Does.Not.Contain("app_token"), "bugsee.properties is never written");
                    Assert.That(Regex.IsMatch(text, @"['""][0-9a-f]{16,}['""]", RegexOptions.IgnoreCase), Is.False, "no literal key-like value");
                }
            }

            Assert.That(string.Join("\n", GradlePluginInjection.BlockLines(false, false)), Does.Not.Contain("appToken"));
        }

        [TestCaseSource(nameof(UnitySets))]
        public void A_second_run_changes_nothing_and_switching_the_option_replaces_the_block_in_place(string set)
        {
            string root = Read(set, "root.build.gradle");
            var once = GradlePluginInjection.Apply(root, Groovy, Read(set, "launcher.build.gradle"), Groovy, Version, gradleUploads: true);
            var twice = GradlePluginInjection.Apply(once.Root, Groovy, once.Launcher, Groovy, Version, gradleUploads: true);
            Assert.That(twice.Launcher, Is.EqualTo(once.Launcher));

            // The same generated launcher, patched again by a later build that is not an export (an append export reuses the files).
            var off = GradlePluginInjection.Apply(once.Root, Groovy, once.Launcher, Groovy, Version);
            Assert.That(off.Launcher, Does.Not.Contain("getenv"));
            Assert.That(Count(off.Launcher, "bugsee {"), Is.EqualTo(1));
            Assert.That(Count(off.Launcher, "bugsee:gradle-ndk"), Is.EqualTo(1));

            var on = GradlePluginInjection.Apply(off.Root, Groovy, off.Launcher, Groovy, Version, gradleUploads: true);
            Assert.That(on.Launcher, Is.EqualTo(once.Launcher));
        }

        [Test]
        public void A_block_of_an_earlier_package_version_is_upgraded_when_the_option_is_on()
        {
            string root = Read("Unity6000.0.84f1-default", "root.build.gradle");
            string launcher = Read("Unity6000.0.84f1-default", "launcher.build.gradle");
            string old = GradlePluginInjection.Apply(root, Groovy, launcher, Groovy, Version).Launcher;

            var patch = GradlePluginInjection.Apply(root, Groovy, old, Groovy, Version, gradleUploads: true);

            Assert.That(patch.Launcher, Does.Contain("appToken(System.getenv('BUGSEE_APP_TOKEN'))"));
            Assert.That(Count(patch.Launcher, "bugsee {"), Is.EqualTo(1));
            Assert.That(patch.Launcher, Does.Not.Contain("App token is supplied at runtime"));
        }

        [Test]
        public void A_project_owned_bugsee_block_is_left_as_written()
        {
            string root = Read("Unity6000.0.84f1-default", "root.build.gradle");
            string launcher = Read("Unity6000.0.84f1-default", "launcher.build.gradle") + "\nbugsee {\n    debug = true\n}\n";

            var patch = GradlePluginInjection.Apply(root, Groovy, launcher, Groovy, Version, gradleUploads: true);

            Assert.That(patch.Launcher, Does.Contain("bugsee {\n    debug = true\n}"));
            Assert.That(patch.Launcher, Does.Not.Contain("getenv"));
            Assert.That(patch.Applied, Is.True);
            Assert.That(patch.GradleUploadsApplied, Is.False, "the Editor must say the lines are missing, not that uploads will happen");
            Assert.That(GradlePluginInjection.UploadsInstructions(false), Does.Contain("appToken(System.getenv('BUGSEE_APP_TOKEN'))"));
            Assert.That(GradlePluginInjection.UploadsInstructions(false), Does.Not.Contain("bugsee:gradle-ndk"));
            Assert.That(GradlePluginInjection.UploadsInstructions(true), Does.Contain("useMergedNativeLibs.set(false)"));
            Assert.That(Count(patch.Launcher, "bugsee {"), Is.EqualTo(1));
        }

        [Test]
        public void A_plugin_the_project_declared_itself_is_left_alone_and_reported_as_not_wired()
        {
            string root = Read("Unity6000.0.84f1-default", "root.build.gradle");
            string launcher = Read("Unity6000.0.84f1-default", "launcher.build.gradle")
                .Replace("apply plugin: 'com.android.application'", "apply plugin: 'com.android.application'\napply plugin: 'com.bugsee.android.gradle'");

            var patch = GradlePluginInjection.Apply(root, Groovy, launcher, Groovy, Version, gradleUploads: true);

            Assert.That(patch.Launcher, Is.EqualTo(launcher));
            Assert.That(patch.GradleUploadsApplied, Is.False);
        }

        [Test]
        public void Kotlin_launchers_get_the_same_lines_in_Kotlin_syntax()
        {
            var patch = GradlePluginInjection.Apply(KotlinRoot, Kotlin, KotlinLauncher, Kotlin, Version, gradleUploads: true);

            Assert.That(patch.Applied, Is.True);
            Assert.That(patch.Launcher, Does.Contain(
                "bugsee {\n" +
                "    // No token is stored here: when Gradle runs, the plugin's uploads read BUGSEE_APP_TOKEN (and BUGSEE_ENDPOINT, BUGSEE_CLI_PATH) from the environment.\n" +
                "    if (System.getenv(\"BUGSEE_NO_SYMBOL_UPLOAD\") == null) {\n" +
                "        System.getenv(\"BUGSEE_APP_TOKEN\")?.takeIf { it.isNotEmpty() }?.let { appToken(it) }\n" +
                "    }\n" +
                "    System.getenv(\"BUGSEE_ENDPOINT\")?.takeIf { it.isNotEmpty() }?.let { endpoint.set(it) }\n" +
                "    System.getenv(\"BUGSEE_CLI_PATH\")?.takeIf { java.io.File(it).isFile }?.let { cliPath.set(it) }\n" +
                "    ndk {\n" +
                "        enabled.set(true)\n" +
                "        useMergedNativeLibs.set(false)\n" +
                "    }\n" +
                "} // bugsee:gradle-ndk"));
            Assert.That(patch.Launcher, Does.Not.Contain("System.getenv('"), "no Groovy quoting in a Kotlin script");

            var twice = GradlePluginInjection.Apply(patch.Root, Kotlin, patch.Launcher, Kotlin, Version, gradleUploads: true);
            Assert.That(twice.Launcher, Is.EqualTo(patch.Launcher));
        }

        [Test]
        public void The_Editor_asks_for_the_plugin_uploads_only_for_an_Export_Project()
        {
            string setup = Source("Editor/BugseeAndroidGradleSetup.cs");

            Assert.That(setup, Does.Contain("gradleUploads: EditorUserBuildSettings.exportAsGoogleAndroidProject"));
        }
    }
}
