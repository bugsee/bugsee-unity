using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    /// <summary>
    /// How the Bugsee Gradle plugin is applied to the generated launcher in every file shape that occurs: Unity's own
    /// Groovy files (checked in from real editors under Fixtures), a root that declares AGP through
    /// <c>buildscript { classpath }</c>, and Kotlin DSL files. Unity generates no .kts file itself (its
    /// GradleTemplates folder and the Android extension only know *.gradle names), so the Kotlin and buildscript
    /// fixtures are RECONSTRUCTED from what a project converting the generated files writes; the Unity 6 Kotlin set is the
    /// converted form of the real Unity6000.0.84f1-default files.
    /// </summary>
    public class GradlePluginInjectionTests
    {
        const GradleMavenInjection.GradleDsl Groovy = GradleMavenInjection.GradleDsl.Groovy;
        const GradleMavenInjection.GradleDsl Kotlin = GradleMavenInjection.GradleDsl.Kotlin;
        const string Version = "4.0.8";

        static readonly string[] UnitySets =
        {
            "Unity6000.0.84f1-default", "Unity6000.0.84f1-sample-templates",
            "Unity2022.3.62f3-default", "Unity2022.3.62f3-export",
        };

        static string Read(string set, string name)
        {
            return File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", set, name));
        }

        static int Count(string text, string needle)
        {
            return Regex.Matches(text, Regex.Escape(needle)).Count;
        }

        // Kotlin launcher the way Android Studio writes it: AGP in the plugins block.
        const string KotlinLauncherPluginsBlock =
            "plugins {\n" +
            "    id(\"com.android.application\")\n" +
            "}\n" +
            "\n" +
            "dependencies {\n" +
            "    implementation(project(\":unityLibrary\"))\n" +
            "}\n" +
            "\n" +
            "android {\n" +
            "    namespace = \"com.example.game\"\n" +
            "}\n";

        // The literal conversion of Unity's `apply plugin: 'com.android.application'`.
        const string KotlinLauncherApplyPlugin =
            "import java.util.Properties\n" +
            "\n" +
            "apply(plugin = \"com.android.application\")\n" +
            "\n" +
            "android {\n" +
            "    namespace = \"com.example.game\"\n" +
            "}\n";

        const string KotlinRoot =
            "plugins {\n" +
            "    id(\"com.android.application\") version \"9.0.0\" apply false\n" +
            "    id(\"com.android.library\") version \"9.0.0\" apply false\n" +
            "}\n";

        const string BuildscriptRoot =
            "buildscript {\n" +
            "    repositories {\n" +
            "        google()\n" +
            "        mavenCentral()\n" +
            "    }\n" +
            "    dependencies {\n" +
            "        classpath 'com.android.tools.build:gradle:8.7.2'\n" +
            "    }\n" +
            "}\n" +
            "\n" +
            "task clean(type: Delete) {\n" +
            "    delete rootProject.buildDir\n" +
            "}\n";

        // ---- Unity's own Groovy files: nothing changes -------------------------------------------------------

        [TestCaseSource(nameof(UnitySets))]
        public void Unity_groovy_layouts_get_the_root_declaration_and_the_launcher_apply_line(string set)
        {
            string root = Read(set, "root.build.gradle");
            string launcher = Read(set, "launcher.build.gradle");

            var patch = GradlePluginInjection.Apply(root, Groovy, launcher, Groovy, Version);

            Assert.That(patch.Applied, Is.True);
            Assert.That(patch.Message, Is.Null);
            Assert.That(patch.Root, Is.EqualTo(MarkedGradleBlock.Apply(root, "plugins {", GradlePluginInjection.RootPluginLine(Version))));
            Assert.That(patch.Launcher, Is.EqualTo(MarkedGradleBlock.Apply(
                launcher, "apply plugin: 'com.android.application'", GradlePluginInjection.LauncherGroovyPatch())));
            Assert.That(patch.Root, Does.Contain("id 'com.bugsee.android.gradle' version '4.0.8' apply false // bugsee:gradle-plugin"));
            Assert.That(patch.Launcher, Does.Contain("apply plugin: 'com.bugsee.android.gradle' // bugsee:gradle-plugin"));
            Assert.That(patch.Launcher, Does.Not.Contain("plugins {"));
        }

        [TestCaseSource(nameof(UnitySets))]
        public void A_second_run_over_Unity_groovy_layouts_changes_nothing(string set)
        {
            var once = GradlePluginInjection.Apply(
                Read(set, "root.build.gradle"), Groovy, Read(set, "launcher.build.gradle"), Groovy, Version);
            var twice = GradlePluginInjection.Apply(once.Root, Groovy, once.Launcher, Groovy, Version);

            Assert.That(twice.Applied, Is.True);
            Assert.That(twice.Root, Is.EqualTo(once.Root));
            Assert.That(twice.Launcher, Is.EqualTo(once.Launcher));
        }

        // ---- a root without a plugins block ------------------------------------------------------------------

        [Test]
        public void A_root_without_a_plugins_block_cannot_take_the_marked_root_line()
        {
            // What the injector used to do with such a root: refuse it, then still patch the launcher with an
            // apply line nothing declared, which fails the Gradle build ("Plugin with id ... not found").
            Assert.Throws<InvalidOperationException>(() =>
                MarkedGradleBlock.Apply(BuildscriptRoot, "plugins {", GradlePluginInjection.RootPluginLine(Version)));
        }

        [TestCaseSource(nameof(UnitySets))]
        public void A_buildscript_root_gets_the_plugin_on_its_classpath_and_the_launcher_applies_it(string set)
        {
            string launcher = Read(set, "launcher.build.gradle");

            var patch = GradlePluginInjection.Apply(BuildscriptRoot, Groovy, launcher, Groovy, Version);

            Assert.That(patch.Applied, Is.True);
            Assert.That(patch.NeedsPluginRepository, Is.False, "a classpath entry resolves from the buildscript repositories");
            Assert.That(patch.Root, Does.Contain(
                "    dependencies {\n        classpath 'com.bugsee:bugsee-android-gradle-plugin:4.0.8' // bugsee:gradle-plugin\n        classpath 'com.android.tools.build:gradle:8.7.2'\n"));
            Assert.That(patch.Launcher, Is.EqualTo(MarkedGradleBlock.Apply(
                launcher, "apply plugin: 'com.android.application'", GradlePluginInjection.LauncherGroovyPatch())));
            Assert.That(patch.Launcher, Does.Not.Contain("plugins {"));

            var again = GradlePluginInjection.Apply(patch.Root, Groovy, patch.Launcher, Groovy, Version);
            Assert.That(again.Root, Is.EqualTo(patch.Root));
            Assert.That(again.Launcher, Is.EqualTo(patch.Launcher));
            Assert.That(Count(again.Root, "bugsee:gradle-plugin"), Is.EqualTo(1));
        }

        [Test]
        public void A_buildscript_root_gets_maven_central_and_a_version_bump_replaces_the_classpath_line()
        {
            string root = "buildscript {\n    repositories {\n        google()\n    }\n    dependencies {\n        classpath 'com.android.tools.build:gradle:8.7.2'\n    }\n}\n";
            string launcher = Read("Unity6000.0.84f1-default", "launcher.build.gradle");

            var once = GradlePluginInjection.Apply(root, Groovy, launcher, Groovy, Version);
            var bumped = GradlePluginInjection.Apply(once.Root, Groovy, once.Launcher, Groovy, "4.1.0");

            Assert.That(once.Root, Does.Contain("        mavenCentral() // bugsee:maven-repository\n        google()"));
            Assert.That(bumped.Root, Does.Contain("bugsee-android-gradle-plugin:4.1.0'"));
            Assert.That(bumped.Root, Does.Not.Contain("4.0.8"));
            Assert.That(bumped.Launcher, Does.Contain("apply plugin: 'com.bugsee.android.gradle' // bugsee:gradle-plugin"));
        }

        [Test]
        public void A_buildscript_without_repositories_is_given_a_repositories_block()
        {
            string root = "buildscript {\n    dependencies {\n        classpath 'com.android.tools.build:gradle:8.7.2'\n    }\n}\n";

            var patch = GradlePluginInjection.Apply(root, Groovy, Read("Unity6000.0.84f1-default", "launcher.build.gradle"), Groovy, Version);

            Assert.That(patch.Applied, Is.True);
            Assert.That(patch.Root, Does.Contain("    repositories {\n        mavenCentral() // bugsee:maven-repository\n    }"));
        }

        [Test]
        public void A_Kotlin_buildscript_root_gets_a_Kotlin_classpath_line_and_the_launcher_entry_has_no_version()
        {
            string root = "buildscript {\n    repositories {\n        google()\n        mavenCentral()\n    }\n    dependencies {\n        classpath(\"com.android.tools.build:gradle:8.7.2\")\n    }\n}\n";

            var patch = GradlePluginInjection.Apply(root, Kotlin, KotlinLauncherPluginsBlock, Kotlin, Version);

            Assert.That(patch.Root, Does.Contain("        classpath(\"com.bugsee:bugsee-android-gradle-plugin:4.0.8\") // bugsee:gradle-plugin\n"));
            Assert.That(patch.Launcher, Does.Contain("    id(\"com.bugsee.android.gradle\") // bugsee:gradle-plugin\n"));
            Assert.That(patch.Launcher, Does.Not.Contain("version"));
        }

        [Test]
        public void A_launcher_with_its_own_plugins_block_gets_the_entry_after_the_existing_ones()
        {
            string launcher = "plugins {\n    id 'com.android.application'\n}\n\nandroid {\n}\n";

            var patch = GradlePluginInjection.Apply(BuildscriptRoot, Groovy, launcher, Groovy, Version);

            Assert.That(patch.Launcher, Does.StartWith(
                "plugins {\n    id 'com.android.application'\n    id 'com.bugsee.android.gradle' // bugsee:gradle-plugin\n}\n\nbugsee {"));
            Assert.That(Count(patch.Launcher, "plugins {"), Is.EqualTo(1));
        }

        [Test]
        public void A_groovy_launcher_without_the_apply_line_still_gets_a_root_declaration_and_a_versionless_entry()
        {
            string root = Read("Unity6000.0.84f1-default", "root.build.gradle");
            string launcher = "plugins {\n    id 'com.android.application'\n}\n\nandroid {\n}\n";

            var patch = GradlePluginInjection.Apply(root, Groovy, launcher, Groovy, Version);

            Assert.That(patch.Applied, Is.True);
            Assert.That(patch.Root, Does.Contain("id 'com.bugsee.android.gradle' version '4.0.8' apply false // bugsee:gradle-plugin"));
            Assert.That(patch.Launcher, Does.Contain("    id 'com.bugsee.android.gradle' // bugsee:gradle-plugin\n"));
            Assert.That(patch.Launcher, Does.Not.Contain("version '4.0.8'"), "the root already declared the version");
            Assert.That(patch.NeedsPluginRepository, Is.True);
        }

        [Test]
        public void The_plugin_is_never_applied_before_AGP_so_a_plugins_block_without_AGP_is_not_used()
        {
            // The plugin looks the AGP extension up while it is applied; ahead of AGP it logs "AndroidComponentsExtension
            // not found" and does nothing, which would look like a working setup.
            string launcher = "plugins {\n    id 'org.jetbrains.kotlin.android'\n}\n\napply plugin: \"com.android.application\"\n";

            var patch = GradlePluginInjection.Apply(BuildscriptRoot, Groovy, launcher, Groovy, Version);

            Assert.That(patch.Applied, Is.False);
            Assert.That(patch.Launcher, Is.EqualTo(launcher));
            Assert.That(patch.Root, Is.EqualTo(BuildscriptRoot), "no declaration nothing applies");
            Assert.That(patch.IsWarning, Is.True);
        }

        [Test]
        public void A_launcher_that_applies_AGP_with_apply_and_no_plugins_block_is_left_with_instructions_in_both_dsls()
        {
            var kotlin = GradlePluginInjection.Apply(KotlinRoot, Kotlin, KotlinLauncherApplyPlugin, Kotlin, Version);
            var groovy = GradlePluginInjection.Apply(
                KotlinRoot, Kotlin, "apply plugin: 'com.android.application'\n\nandroid {\n}\n", Groovy, Version);

            Assert.That(kotlin.Applied, Is.False);
            Assert.That(kotlin.Launcher, Is.EqualTo(KotlinLauncherApplyPlugin));
            Assert.That(kotlin.Root, Is.EqualTo(KotlinRoot));
            Assert.That(kotlin.IsWarning, Is.True);
            Assert.That(kotlin.Message, Does.Contain("apply(plugin = \"com.bugsee.android.gradle\")"));
            Assert.That(kotlin.Message, Does.Contain("classpath(\"com.bugsee:bugsee-android-gradle-plugin:4.0.8\")"));
            Assert.That(groovy.Applied, Is.False);
            Assert.That(groovy.Message, Does.Contain("apply plugin: 'com.bugsee.android.gradle'"));
            Assert.That(groovy.Message, Does.Contain("classpath 'com.bugsee:bugsee-android-gradle-plugin:4.0.8'"));
        }

        // ---- Kotlin DSL --------------------------------------------------------------------------------------

        [Test]
        public void Kotlin_launcher_with_a_plugins_block_gets_the_plugin_with_its_version_and_the_ndk_block()
        {
            var patch = GradlePluginInjection.Apply(KotlinRoot, Kotlin, KotlinLauncherPluginsBlock, Kotlin, Version);

            Assert.That(patch.Applied, Is.True);
            Assert.That(patch.Root, Is.EqualTo(KotlinRoot));
            Assert.That(patch.Launcher, Does.StartWith(
                "plugins {\n" +
                "    id(\"com.android.application\")\n" +
                "    id(\"com.bugsee.android.gradle\") version \"4.0.8\" // bugsee:gradle-plugin\n" +
                "}\n" +
                "\n" +
                "bugsee {\n" +
                "    // App token is supplied at runtime via Bugsee.Launch.\n" +
                "    ndk {\n" +
                "        enabled.set(true)\n" +
                "    }\n" +
                "} // bugsee:gradle-ndk\n" +
                "\n" +
                "dependencies {"));
            Assert.That(patch.Launcher, Does.Not.Contain("'"), "no Groovy quoting in a Kotlin script");
        }

        [Test]
        public void A_second_Kotlin_run_changes_nothing_and_a_new_plugin_version_replaces_the_line()
        {
            var once = GradlePluginInjection.Apply(KotlinRoot, Kotlin, KotlinLauncherPluginsBlock, Kotlin, Version);
            var twice = GradlePluginInjection.Apply(KotlinRoot, Kotlin, once.Launcher, Kotlin, Version);
            var bumped = GradlePluginInjection.Apply(KotlinRoot, Kotlin, once.Launcher, Kotlin, "4.1.0");

            Assert.That(twice.Launcher, Is.EqualTo(once.Launcher));
            Assert.That(bumped.Launcher, Does.Contain("version \"4.1.0\" // bugsee:gradle-plugin"));
            Assert.That(bumped.Launcher, Does.Not.Contain("4.0.8"));
            Assert.That(Count(bumped.Launcher, "bugsee:gradle-plugin"), Is.EqualTo(1));
            Assert.That(Count(bumped.Launcher, "bugsee:gradle-ndk"), Is.EqualTo(1));
        }

        [Test]
        public void A_Kotlin_launcher_that_names_AGP_by_catalog_alias_gets_the_entry_after_it()
        {
            string launcher = "plugins {\n    alias(libs.plugins.android.application)\n}\n\nandroid {\n}\n";

            var patch = GradlePluginInjection.Apply(KotlinRoot, Kotlin, launcher, Kotlin, Version);

            Assert.That(patch.Launcher, Does.StartWith(
                "plugins {\n    alias(libs.plugins.android.application)\n    id(\"com.bugsee.android.gradle\") version \"4.0.8\" // bugsee:gradle-plugin\n}\n"));
        }

        [Test]
        public void A_Kotlin_root_that_declares_the_plugin_itself_leaves_the_version_to_the_root()
        {
            string root = KotlinRoot.Replace(
                "}\n", "    id(\"com.bugsee.android.gradle\") version \"4.0.5\" apply false\n}\n");

            var patch = GradlePluginInjection.Apply(root, Kotlin, KotlinLauncherPluginsBlock, Kotlin, Version);

            Assert.That(patch.Root, Is.EqualTo(root), "the project's own declaration is never rewritten");
            Assert.That(patch.Launcher, Does.Contain("    id(\"com.bugsee.android.gradle\") // bugsee:gradle-plugin\n"));
            Assert.That(patch.Launcher, Does.Not.Contain("version \"4.0.8\""));
        }

        [Test]
        public void A_launcher_that_applies_the_plugin_itself_is_left_as_written()
        {
            string launcher = "plugins {\n    id(\"com.android.application\")\n    id(\"com.bugsee.android.gradle\") version \"4.0.5\"\n}\n";

            var patch = GradlePluginInjection.Apply(KotlinRoot, Kotlin, launcher, Kotlin, Version);

            Assert.That(patch.Applied, Is.True);
            Assert.That(patch.Launcher, Is.EqualTo(launcher));
            Assert.That(patch.Message, Does.Contain("already declares"));
            Assert.That(patch.IsWarning, Is.False);
        }

        [Test]
        public void A_launcher_with_its_own_bugsee_block_keeps_it()
        {
            string launcher = KotlinLauncherPluginsBlock + "\nbugsee {\n    ndk {\n        enabled.set(false)\n    }\n}\n";

            var patch = GradlePluginInjection.Apply(KotlinRoot, Kotlin, launcher, Kotlin, Version);

            Assert.That(Count(patch.Launcher, "bugsee {"), Is.EqualTo(1));
            Assert.That(patch.Launcher, Does.Contain("enabled.set(false)"));
        }

        [Test]
        public void A_one_line_plugins_block_is_not_edited_and_the_user_gets_the_lines_to_add()
        {
            string launcher = "plugins { id(\"com.android.application\") }\n\nandroid {\n}\n";

            var patch = GradlePluginInjection.Apply(KotlinRoot, Kotlin, launcher, Kotlin, Version);

            Assert.That(patch.Applied, Is.False);
            Assert.That(patch.Launcher, Is.EqualTo(launcher));
            Assert.That(patch.IsWarning, Is.True);
            Assert.That(patch.Message, Does.Contain("id(\"com.bugsee.android.gradle\") version \"4.0.8\""));
            Assert.That(patch.Message, Does.Contain("launcher/build.gradle.kts"));
            Assert.That(patch.Message, Does.Contain("enabled.set(true)"));
        }

        [Test]
        public void A_mixed_Groovy_root_and_Kotlin_launcher_declares_in_the_root_and_the_launcher_applies_it()
        {
            string root = Read("Unity6000.0.84f1-default", "root.build.gradle");

            var patch = GradlePluginInjection.Apply(root, Groovy, KotlinLauncherPluginsBlock, Kotlin, Version);

            Assert.That(patch.Applied, Is.True);
            Assert.That(patch.Root, Does.Contain("id 'com.bugsee.android.gradle' version '4.0.8' apply false // bugsee:gradle-plugin"));
            Assert.That(patch.Launcher, Does.Contain("    id(\"com.bugsee.android.gradle\") // bugsee:gradle-plugin\n"));
            Assert.That(patch.Launcher, Does.Not.Contain("version"));
        }

        // ---- the converted Unity 6000.0.84f1 project (Fixtures/Unity6000.0.84f1-kotlin-dsl*) -----------------------
        // These are the files the Unity 6 build was run on after converting its generated Groovy files to Kotlin DSL
        // (Gradle 9.3.1, AGP 9.0.0; the build succeeded and the APK carried the plugin's bugsee_build_id.properties).

        [TestCase("Unity6000.0.84f1-kotlin-dsl")]
        [TestCase("Unity6000.0.84f1-kotlin-dsl-catalog")]
        public void The_converted_Unity_6_project_gets_the_plugin_in_the_launcher_and_settings_need_nothing(string set)
        {
            string root = Read(set, "root.build.gradle.kts");
            string launcher = Read(set, "launcher.build.gradle.kts");
            string catalog = File.Exists(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", set, "libs.versions.toml"))
                ? Read(set, "libs.versions.toml") : null;

            Assert.That(GradleTemplateDefaults.AgpMeetsBugseePlugin(root, catalog), Is.True);
            var patch = GradlePluginInjection.Apply(root, Kotlin, launcher, Kotlin, Version);

            Assert.That(patch.Applied, Is.True);
            Assert.That(patch.Root, Is.EqualTo(root));
            Assert.That(patch.Launcher, Does.Contain("    id(\"com.bugsee.android.gradle\") version \"4.0.8\" // bugsee:gradle-plugin\n}\n\nbugsee {"));
            Assert.That(GradlePluginInjection.Apply(patch.Root, Kotlin, patch.Launcher, Kotlin, Version).Launcher, Is.EqualTo(patch.Launcher));
            Assert.That(GradleTemplateDefaults.EnsureReleaseDebugSymbols(launcher, Kotlin), Is.EqualTo(launcher), "debugSymbolLevel is already set");

            string settings = Read(set, "settings.gradle.kts");
            Assert.That(GradleMavenInjection.EnsurePluginRepositories(settings, out _), Is.EqualTo(GradleMavenInjection.RepositoryStatus.AlreadyPresent));
            var repositories = GradleMavenInjection.EnsureProjectRepositories(settings, root);
            Assert.That(repositories.Mode, Is.EqualTo(GradleMavenInjection.RepositoriesMode.PreferSettings));
            Assert.That(repositories.SettingsStatus, Is.EqualTo(GradleMavenInjection.RepositoryStatus.AlreadyPresent));
        }

        [Test]
        public void The_catalog_alias_root_is_an_unknown_AGP_without_its_catalog()
        {
            string root = Read("Unity6000.0.84f1-kotlin-dsl-catalog", "root.build.gradle.kts");

            Assert.That(GradleTemplateDefaults.AgpMeetsBugseePlugin(root), Is.False);
            Assert.That(GradleTemplateDefaults.AgpMeetsBugseePlugin(
                root, Read("Unity6000.0.84f1-kotlin-dsl-catalog", "libs.versions.toml")), Is.True);
        }

        [Test]
        public void A_legacy_marked_plugin_line_from_an_earlier_package_version_is_still_upgraded_in_place()
        {
            string root = Read("Unity6000.0.84f1-default", "root.build.gradle");
            string legacyRoot = root.Replace(
                "    id 'com.android.library' version '9.0.0' apply false\n",
                "    id 'com.android.library' version '9.0.0' apply false\n    // Bugsee Gradle plugin\n    id 'com.bugsee.android.gradle' version '4.0.2' apply false\n");

            var patch = GradlePluginInjection.Apply(legacyRoot, Groovy, Read("Unity6000.0.84f1-default", "launcher.build.gradle"), Groovy, Version);

            Assert.That(patch.Root, Does.Contain("version '4.0.8' apply false // bugsee:gradle-plugin"));
            Assert.That(patch.Root, Does.Not.Contain("4.0.2"));
            Assert.That(Count(patch.Root, "com.bugsee.android.gradle"), Is.EqualTo(1));
        }

        [Test]
        public void Comments_that_mention_the_plugin_do_not_count_as_a_declaration()
        {
            Assert.That(GradlePluginInjection.HasUnmarkedDeclaration("// id 'com.bugsee.android.gradle' version '4'\nplugins {\n}\n"), Is.False);
            Assert.That(GradlePluginInjection.HasUnmarkedDeclaration("classpath 'com.bugsee:bugsee-android-gradle-plugin:4.0.8'"), Is.True);
            Assert.That(GradlePluginInjection.HasUnmarkedDeclaration("apply plugin: 'com.bugsee.android.gradle'"), Is.True);
            Assert.That(GradlePluginInjection.HasUnmarkedDeclaration(null), Is.False);
        }

        [Test]
        public void Windows_line_endings_are_kept()
        {
            string launcher = KotlinLauncherPluginsBlock.Replace("\n", "\r\n");

            var patch = GradlePluginInjection.Apply(KotlinRoot, Kotlin, launcher, Kotlin, Version);

            Assert.That(Regex.IsMatch(patch.Launcher, "(?<!\r)\n"), Is.False);
            Assert.That(patch.Launcher, Does.Contain("// bugsee:gradle-plugin\r\n}"));
        }

        [Test]
        public void Manual_instructions_are_in_the_dsl_of_the_launcher_file()
        {
            Assert.That(GradlePluginInjection.ManualInstructions(Groovy, Version),
                Does.Contain("id 'com.bugsee.android.gradle' version '4.0.8'").And.Contain("launcher/build.gradle,").And.Contain("enabled = true"));
            Assert.That(GradlePluginInjection.ManualInstructions(Kotlin, Version),
                Does.Contain("id(\"com.bugsee.android.gradle\") version \"4.0.8\"").And.Contain("launcher/build.gradle.kts").And.Contain("enabled.set(true)"));
        }
    }
}
