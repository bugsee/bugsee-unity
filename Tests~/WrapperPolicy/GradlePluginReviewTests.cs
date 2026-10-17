using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    /// <summary>
    /// Regressions found reviewing the Gradle plugin injection: a second run must never change what the first wrote, in any
    /// file shape; repositories are appended, not prepended, and a repository that already serves the plugin is recognised;
    /// comments and strings are not code for any scanner.
    /// </summary>
    public class GradlePluginReviewTests
    {
        const GradleMavenInjection.GradleDsl Groovy = GradleMavenInjection.GradleDsl.Groovy;
        const GradleMavenInjection.GradleDsl Kotlin = GradleMavenInjection.GradleDsl.Kotlin;
        const string Version = "4.0.8";

        static string Read(string set, string name) =>
            File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", set, name));

        const string UnityRoot = "plugins {\n    id 'com.android.application' version '9.0.0' apply false\n}\n";
        const string BuildscriptRootGroovy = "buildscript {\n    repositories {\n        google()\n        mavenCentral()\n    }\n    dependencies {\n        classpath 'com.android.tools.build:gradle:8.7.2'\n    }\n}\n";
        const string BuildscriptRootKotlin = "buildscript {\n    repositories {\n        google()\n        mavenCentral()\n    }\n    dependencies {\n        classpath(\"com.android.tools.build:gradle:8.7.2\")\n    }\n}\n";
        const string KotlinRoot = "plugins {\n    id(\"com.android.application\") version \"9.0.0\" apply false\n}\n";
        const string CatalogRoot = "plugins {\n    alias(libs.plugins.android.application) apply false\n}\n";
        const string GroovyPluginsLauncher = "plugins {\n    id 'com.android.application'\n}\n\nandroid {\n}\n";
        const string KotlinPluginsLauncher = "plugins {\n    id(\"com.android.application\")\n}\n\nandroid {\n}\n";
        const string CatalogLauncher = "plugins {\n    alias(libs.plugins.android.application)\n}\n\nandroid {\n}\n";

        static IEnumerable<TestCaseData> Shapes()
        {
            string unityLauncher = Read("Unity6000.0.84f1-default", "launcher.build.gradle");
            string unityRoot = Read("Unity6000.0.84f1-default", "root.build.gradle");
            yield return new TestCaseData("unity groovy", unityRoot, Groovy, unityLauncher, Groovy);
            yield return new TestCaseData("unity root, plugins launcher", UnityRoot, Groovy, GroovyPluginsLauncher, Groovy);
            yield return new TestCaseData("buildscript root, unity launcher", BuildscriptRootGroovy, Groovy, unityLauncher, Groovy);
            yield return new TestCaseData("buildscript root, plugins launcher", BuildscriptRootGroovy, Groovy, GroovyPluginsLauncher, Groovy);
            yield return new TestCaseData("kotlin buildscript root, kotlin launcher", BuildscriptRootKotlin, Kotlin, KotlinPluginsLauncher, Kotlin);
            yield return new TestCaseData("kotlin buildscript root, groovy launcher", BuildscriptRootKotlin, Kotlin, unityLauncher, Groovy);
            yield return new TestCaseData("kotlin", KotlinRoot, Kotlin, KotlinPluginsLauncher, Kotlin);
            yield return new TestCaseData("catalog alias", CatalogRoot, Kotlin, CatalogLauncher, Kotlin);
            yield return new TestCaseData("groovy root, kotlin launcher", UnityRoot, Groovy, KotlinPluginsLauncher, Kotlin);
            yield return new TestCaseData("foreign bugsee block, unity launcher", unityRoot, Groovy, unityLauncher + "\nbugsee {\n    debug = true\n}\n", Groovy);
            yield return new TestCaseData("foreign bugsee block right after the anchor", unityRoot, Groovy,
                unityLauncher.Replace("apply plugin: 'com.android.application'\n", "apply plugin: 'com.android.application'\n\nbugsee {\n    debug = true\n}\n"), Groovy);
            yield return new TestCaseData("foreign bugsee block, kotlin launcher", KotlinRoot, Kotlin, KotlinPluginsLauncher + "\nbugsee {\n    debug.set(true)\n}\n", Kotlin);
            yield return new TestCaseData("odd anchor spacing is left to the block path", UnityRoot, Groovy, "plugins {\n    id 'com.android.application' version '9.0.0'\n}\n", Groovy);
            yield return new TestCaseData("brace on the first line", UnityRoot, Groovy, "plugins { id 'com.android.application'\n}\n\nandroid {\n}\n", Groovy);
        }

        [TestCaseSource(nameof(Shapes))]
        public void A_second_and_a_third_run_equal_the_first_in_every_shape(
            string name, string root, GradleMavenInjection.GradleDsl rootDsl, string launcher, GradleMavenInjection.GradleDsl launcherDsl)
        {
            var once = GradlePluginInjection.Apply(root, rootDsl, launcher, launcherDsl, Version);
            Assert.That(once.Applied, Is.True, name);
            var twice = GradlePluginInjection.Apply(once.Root, rootDsl, once.Launcher, launcherDsl, Version);
            var thrice = GradlePluginInjection.Apply(twice.Root, rootDsl, twice.Launcher, launcherDsl, Version);

            Assert.That(twice.Root, Is.EqualTo(once.Root), name);
            Assert.That(twice.Launcher, Is.EqualTo(once.Launcher), name);
            Assert.That(thrice.Root, Is.EqualTo(once.Root), name);
            Assert.That(thrice.Launcher, Is.EqualTo(once.Launcher), name);
            Assert.That(twice.Applied, Is.True, name);
        }

        [TestCaseSource(nameof(Shapes))]
        public void A_new_plugin_version_replaces_the_old_one_in_every_shape(
            string name, string root, GradleMavenInjection.GradleDsl rootDsl, string launcher, GradleMavenInjection.GradleDsl launcherDsl)
        {
            var once = GradlePluginInjection.Apply(root, rootDsl, launcher, launcherDsl, Version);
            var bumped = GradlePluginInjection.Apply(once.Root, rootDsl, once.Launcher, launcherDsl, "4.1.0");

            string all = bumped.Root + bumped.Launcher;
            Assert.That(all, Does.Not.Contain("4.0.8"), name);
            // Every shape that writes a version writes exactly one marked line carrying the new one; the others write none.
            int withVersion = System.Text.RegularExpressions.Regex.Matches(all, @"com\.bugsee[^\n]*4\.1\.0[^\n]*// bugsee:gradle-plugin").Count;
            bool carriesVersion = (once.Root + once.Launcher).Contains("4.0.8");
            Assert.That(withVersion, Is.EqualTo(carriesVersion ? 1 : 0), name);
            Assert.That(GradlePluginInjection.Apply(bumped.Root, rootDsl, bumped.Launcher, launcherDsl, "4.1.0").Launcher, Is.EqualTo(bumped.Launcher), name);
        }

        [Test]
        public void A_marked_id_line_in_a_plugins_block_is_never_rewritten_as_an_apply_line()
        {
            // MarkedGradleBlock upgrades any line carrying the marker; with the other shape it must refuse.
            string launcher = "plugins {\n    id 'com.android.application'\n    id 'com.bugsee.android.gradle' // bugsee:gradle-plugin\n}\n\napply plugin: 'com.android.application'\n";

            Assert.Throws<InvalidOperationException>(() => MarkedGradleBlock.Apply(
                launcher, "apply plugin: 'com.android.application'", GradlePluginInjection.LauncherGroovyPatch()));
        }

        // ---- a bugsee block or a plugin application the project wrote itself ------------------------------

        [Test]
        public void A_launchers_own_bugsee_block_is_kept_and_the_apply_line_is_still_added_once()
        {
            string launcher = Read("Unity6000.0.84f1-default", "launcher.build.gradle") + "\nbugsee {\n    debug = true\n}\n";
            string root = Read("Unity6000.0.84f1-default", "root.build.gradle");

            var once = GradlePluginInjection.Apply(root, Groovy, launcher, Groovy, Version);
            var twice = GradlePluginInjection.Apply(once.Root, Groovy, once.Launcher, Groovy, Version);

            Assert.That(once.Applied, Is.True);
            Assert.That(once.Launcher, Does.Contain("apply plugin: 'com.bugsee.android.gradle' // bugsee:gradle-plugin"));
            Assert.That(once.Launcher, Does.Contain("bugsee {\n    debug = true\n}"));
            Assert.That(once.Launcher, Does.Not.Contain("bugsee:gradle-ndk"), "the project configures the plugin itself");
            Assert.That(System.Text.RegularExpressions.Regex.Matches(once.Launcher, @"^bugsee \{", System.Text.RegularExpressions.RegexOptions.Multiline).Count, Is.EqualTo(1));
            Assert.That(twice.Launcher, Is.EqualTo(once.Launcher));
        }

        [Test]
        public void An_own_bugsee_block_next_to_the_apply_line_is_not_swallowed_by_our_region()
        {
            string launcher = "apply plugin: 'com.android.application'\n\nbugsee {\n    debug = true\n}\n\nandroid {\n}\n";

            var once = GradlePluginInjection.Apply(UnityRoot, Groovy, launcher, Groovy, Version);
            var twice = GradlePluginInjection.Apply(once.Root, Groovy, once.Launcher, Groovy, Version);

            Assert.That(once.Launcher, Does.Contain("bugsee {\n    debug = true\n}"));
            Assert.That(once.Launcher, Does.Not.Contain("enabled = true"));
            Assert.That(twice.Launcher, Is.EqualTo(once.Launcher));
        }

        [TestCase("pluginManager.apply('com.bugsee.android.gradle')\n")]
        [TestCase("plugins.apply('com.bugsee.android.gradle')\n")]
        [TestCase("pluginManager.apply(\"com.bugsee.android.gradle\")\n")]
        public void A_plugin_applied_through_the_plugin_manager_is_the_projects_own_declaration(string line)
        {
            Assert.That(GradlePluginInjection.HasUnmarkedDeclaration(line), Is.True);
            string launcher = "apply plugin: 'com.android.application'\n" + line + "\nandroid {\n}\n";

            var patch = GradlePluginInjection.Apply(UnityRoot, Groovy, launcher, Groovy, Version);

            Assert.That(patch.Launcher, Is.EqualTo(launcher));
            Assert.That(patch.Root, Is.EqualTo(UnityRoot));
            Assert.That(patch.Applied, Is.True);
            Assert.That(patch.Message, Does.Contain("already declares"));
        }

        [Test]
        public void A_launchers_plugins_block_closing_on_the_entry_line_is_reported_not_misapplied()
        {
            var shared = GradlePluginInjection.Apply(UnityRoot, Groovy, "plugins {\n    id 'com.android.application' }\n", Groovy, Version);
            var first = GradlePluginInjection.Apply(UnityRoot, Groovy, "plugins { id 'com.android.application'\n}\n", Groovy, Version);

            Assert.That(shared.Applied, Is.False);
            Assert.That(shared.Message, Does.Contain("ends on the line of its last entry"));
            Assert.That(first.Applied, Is.True);
            Assert.That(first.Launcher, Does.Contain("    id 'com.bugsee.android.gradle' // bugsee:gradle-plugin\n}"));
        }

        [Test]
        public void Manual_instructions_use_the_root_files_dsl_for_the_root_edit()
        {
            var patch = GradlePluginInjection.Apply(
                KotlinRoot, Kotlin, "apply plugin: 'com.android.application'\n", Groovy, Version);

            Assert.That(patch.Applied, Is.False);
            Assert.That(patch.Message, Does.Contain("id(\"com.bugsee.android.gradle\") version \"4.0.8\" apply false in the root plugins"));
            Assert.That(patch.Message, Does.Contain("apply plugin: 'com.bugsee.android.gradle'"));
        }

        // ---- repositories ------------------------------------------------------------------------------------

        [Test]
        public void A_one_line_repositories_block_with_a_trailing_comment_is_edited_once_and_the_comment_kept()
        {
            string settings = "pluginManagement {\n    repositories { google() } // see {x}\n}\n";

            Assert.That(Plugins(settings, out string once), Is.EqualTo(GradleMavenInjection.RepositoryStatus.Inserted));
            Assert.That(once, Does.Contain("repositories { google(); mavenCentral() } // see {x}"));
            Assert.That(Plugins(once, out string twice), Is.EqualTo(GradleMavenInjection.RepositoryStatus.AlreadyPresent));
            Assert.That(twice, Is.EqualTo(once));
        }

        [Test]
        public void An_entry_on_the_closing_line_is_split_off_so_the_new_line_comes_last()
        {
            string settings = "pluginManagement {\n    repositories {\n        google() }\n}\n";

            Assert.That(Plugins(settings, out string once), Is.EqualTo(GradleMavenInjection.RepositoryStatus.Inserted));
            Assert.That(once, Is.EqualTo("pluginManagement {\n    repositories {\n        google()\n        mavenCentral() // bugsee:maven-repository\n    }\n}\n"));
            Assert.That(Plugins(once, out _), Is.EqualTo(GradleMavenInjection.RepositoryStatus.AlreadyPresent));
        }

        [Test]
        public void Kotlin_maven_shorthands_are_recognised_by_url()
        {
            string[] shorthands =
            {
                "        maven(\"https://plugins.gradle.org/m2/\")\n",
                "        maven(url = \"https://plugins.gradle.org/m2\")\n",
                "        maven(\"https://repo.maven.apache.org/maven2\")\n",
                "        maven(url = uri(\"https://repo1.maven.org/maven2\"))\n",
            };
            foreach (string repository in shorthands)
            {
                Assert.That(Plugins(Settings("        google()\n" + repository), out _),
                    Is.EqualTo(GradleMavenInjection.RepositoryStatus.AlreadyPresent), repository);
            }

            Assert.That(Plugins(Settings("        maven(\"https://nexus.corp/x\")\n"), out _), Is.EqualTo(GradleMavenInjection.RepositoryStatus.Inserted));
        }

        // The review's pattern: nested alternations and quantifiers over the group name; the engine backtracks for seconds.
        const string CatastrophicFilter = "includeGroupByRegex('(com|c|co|com\\\\.|.*)*bugsee(x|y|.*)*z')";

        static string ProxyWithFilter(string filter) =>
            Settings("        maven {\n            url 'https://nexus.corp/proxy'\n            content { " + filter + " }\n        }\n");

        [Test]
        public void A_catastrophic_filter_regex_is_cut_off_and_read_as_matching_for_that_input()
        {
            string settings = ProxyWithFilter(CatastrophicFilter);
            var watch = System.Diagnostics.Stopwatch.StartNew();

            var status = Plugins(settings, out string updated);

            watch.Stop();
            // Documented fallback: a regex that cannot be evaluated in time counts as matching for that input (here the long
            // marker group, which is the slow one). The other group is evaluated normally and does not match (the pattern needs a
            // trailing z), so the proxy is not taken to include both Bugsee groups and Central is appended. Without the 500 ms limit
            // the same call spends seconds in the engine first (see Without_the_timeout_the_same_pattern_takes_several_seconds).
            Assert.That(status, Is.EqualTo(GradleMavenInjection.RepositoryStatus.Inserted));
            Assert.That(updated, Does.Contain("mavenCentral() // bugsee:maven-repository"));
            Assert.That(watch.ElapsedMilliseconds, Is.GreaterThan(400), "the unevaluable input is cut off by the limit, not skipped");
            Assert.That(watch.ElapsedMilliseconds, Is.LessThan(UnboundedMilliseconds() / 2), "bounded evaluation must be far quicker than the unbounded one");
        }

        // The cost of the same pattern evaluated without a limit, on the strings the filter code feeds it.
        static long UnboundedMilliseconds()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            foreach (string input in new[] { "com.bugsee.android.gradle", "com.bugsee" })
            {
                System.Text.RegularExpressions.Regex.IsMatch(
                    input, "^(?:(com|c|co|com\\.|.*)*bugsee(x|y|.*)*z)$",
                    System.Text.RegularExpressions.RegexOptions.None, System.Text.RegularExpressions.Regex.InfiniteMatchTimeout);
            }

            return watch.ElapsedMilliseconds;
        }

        [Test]
        public void Without_the_timeout_the_same_pattern_takes_several_seconds()
        {
            // Guards the test above against becoming vacuous: if the engine ever evaluates this pattern quickly, the timeout test proves nothing.
            Assert.That(UnboundedMilliseconds(), Is.GreaterThan(2000));
        }

        [Test]
        public void An_AGP_range_is_an_unknown_version_and_the_dot_version_form_is_read()
        {
            Assert.That(GradleTemplateDefaults.TryGetAgpVersion("id 'com.android.application' version '8.+' apply false", out _, out _), Is.False);
            Assert.That(GradleTemplateDefaults.TryGetAgpVersion("classpath 'com.android.tools.build:gradle:8.+'", out _, out _), Is.False);
            Assert.That(GradleTemplateDefaults.TryGetAgpVersion(
                "id(\"com.android.application\").version(\"9.0.0\").apply(false)", out int major, out int minor), Is.True);
            Assert.That((major, minor), Is.EqualTo((9, 0)));
        }

        static GradleMavenInjection.RepositoryStatus Plugins(string settings, out string updated) =>
            GradleMavenInjection.EnsurePluginRepositories(settings, out updated);

        static string Settings(string repositories) =>
            "pluginManagement {\n    repositories {\n" + repositories + "    }\n}\n";

        [Test]
        public void Maven_central_is_appended_after_a_corporate_repository_not_put_ahead_of_it()
        {
            string settings = Settings("        maven { url 'https://nexus.corp/plugins' }\n        google()\n");

            Assert.That(Plugins(settings, out string updated), Is.EqualTo(GradleMavenInjection.RepositoryStatus.Inserted));
            Assert.That(updated, Does.Contain("        google()\n        mavenCentral() // bugsee:maven-repository\n    }"));
            Assert.That(updated.IndexOf("nexus.corp"), Is.LessThan(updated.IndexOf("mavenCentral()")));
        }

        [Test]
        public void A_one_line_repositories_block_gets_it_at_the_end()
        {
            string settings = "pluginManagement {\n    repositories { google() }\n}\n";

            Assert.That(Plugins(settings, out string updated), Is.EqualTo(GradleMavenInjection.RepositoryStatus.Inserted));
            Assert.That(updated, Does.Contain("repositories { google(); mavenCentral() }"));
        }

        [TestCase("        maven { url 'https://plugins.gradle.org/m2/' }\n")]
        [TestCase("        maven { url = uri(\"https://plugins.gradle.org/m2\") }\n")]
        [TestCase("        maven { url 'https://repo.maven.apache.org/maven2' }\n")]
        [TestCase("        maven {\n            url 'https://nexus.corp/proxy'\n            content { includeGroupByRegex(\".*\") }\n        }\n")]
        [TestCase("        gradlePluginPortal()\n")]
        [TestCase("        mavenCentral()\n")]
        public void A_repository_that_already_serves_the_plugin_is_recognised(string repository)
        {
            string settings = Settings("        google()\n" + repository);

            Assert.That(Plugins(settings, out string updated), Is.EqualTo(GradleMavenInjection.RepositoryStatus.AlreadyPresent), repository);
            Assert.That(updated, Is.EqualTo(settings));
        }

        [TestCase("        maven {\n            url 'https://nexus.corp/proxy'\n            content { includeGroup('org.corp') }\n        }\n")]
        [TestCase("        maven { url 'https://nexus.corp/proxy' }\n")]
        [TestCase("        gradlePluginPortal { content { includeGroupByRegex('org\\\\.gradle.*') } }\n")]
        [TestCase("        exclusiveContent {\n            forRepository { gradlePluginPortal() }\n            filter { includeGroup('org.gradle.plugins') }\n        }\n")]
        public void A_repository_that_does_not_serve_the_plugin_is_not_taken_for_one(string repository)
        {
            Assert.That(Plugins(Settings(repository), out string updated), Is.EqualTo(GradleMavenInjection.RepositoryStatus.Inserted), repository);
            Assert.That(updated, Does.Contain("mavenCentral() // bugsee:maven-repository"));
        }

        [Test]
        public void A_portal_inside_an_exclusive_content_that_includes_the_bugsee_groups_serves_it()
        {
            string settings = Settings(
                "        exclusiveContent {\n            forRepository { gradlePluginPortal() }\n" +
                "            filter {\n                includeGroup('com.bugsee')\n                includeGroup('com.bugsee.android.gradle')\n            }\n        }\n");

            Assert.That(Plugins(settings, out _), Is.EqualTo(GradleMavenInjection.RepositoryStatus.AlreadyPresent));
        }

        [Test]
        public void Dependency_repositories_still_get_maven_central_first()
        {
            string settings = "dependencyResolutionManagement {\n    repositories {\n        google()\n    }\n}\n";

            Assert.That(GradleMavenInjection.EnsureMavenCentral(settings, "dependencyResolutionManagement", out string updated),
                Is.EqualTo(GradleMavenInjection.RepositoryStatus.Inserted));
            Assert.That(updated, Does.Contain("    repositories {\n        mavenCentral() // bugsee:maven-repository\n        google()"));
        }

        [Test]
        public void With_repository_changes_forbidden_a_buildscript_root_that_needs_one_is_not_patched_but_one_that_does_not_is()
        {
            string needs = "buildscript {\n    repositories {\n        google()\n    }\n    dependencies {\n        classpath 'com.android.tools.build:gradle:8.7.2'\n    }\n}\n";
            string launcher = Read("Unity6000.0.84f1-default", "launcher.build.gradle");

            var refused = GradlePluginInjection.Apply(needs, Groovy, launcher, Groovy, Version, allowRepositoryInjection: false);
            var allowed = GradlePluginInjection.Apply(BuildscriptRootGroovy, Groovy, launcher, Groovy, Version, allowRepositoryInjection: false);
            var done = GradlePluginInjection.Apply(needs, Groovy, launcher, Groovy, Version);

            Assert.That(refused.Applied, Is.False);
            Assert.That(refused.Root, Is.EqualTo(needs));
            Assert.That(refused.Launcher, Is.EqualTo(launcher));
            Assert.That(allowed.Applied, Is.True);
            Assert.That(allowed.AddedBuildscriptRepository, Is.False);
            Assert.That(done.Applied, Is.True);
            Assert.That(done.AddedBuildscriptRepository, Is.True);
            Assert.That(done.Root, Does.Contain("        google()\n        mavenCentral() // bugsee:maven-repository\n    }"));
        }

        // ---- comments, strings, lexer -----------------------------------------------------------------------

        [Test]
        public void A_commented_out_AGP_version_does_not_win_over_the_real_pin()
        {
            string root = "plugins {\n    // id 'com.android.application' version '9.0.0' apply false\n    /* id 'com.android.application' version '9.1.0' */\n" +
                          "    id 'com.android.application' version '7.4.2' apply false\n}\n";

            Assert.That(GradleTemplateDefaults.TryGetAgpVersion(root, out int major, out int minor), Is.True);
            Assert.That((major, minor), Is.EqualTo((7, 4)));
            Assert.That(GradleTemplateDefaults.AgpMeetsBugseePlugin(root), Is.False);
            Assert.That(GradleTemplateDefaults.AgpSupportsBuildIdBlock(root), Is.True);
            Assert.That(GradleTemplateDefaults.PinnedAgpMajor(root), Is.EqualTo(7));
        }

        [Test]
        public void A_commented_out_pin_alone_is_an_unknown_version()
        {
            Assert.That(GradleTemplateDefaults.TryGetAgpVersion("// classpath 'com.android.tools.build:gradle:8.7.2'\n", out _, out _), Is.False);
        }

        [TestCase("'''")]
        [TestCase("\"\"\"")]
        public void A_multi_line_string_holding_plugin_block_text_is_not_a_block(string quote)
        {
            string launcher = "plugins {\n    id 'com.android.application'\n}\n\ndef note = " + quote + "\nplugins {\n    something\n" + quote + "\n\nandroid {\n}\n";

            var patch = GradlePluginInjection.Apply(BuildscriptRootGroovy, Groovy, launcher, Groovy, Version);

            Assert.That(patch.Applied, Is.True);
            Assert.That(patch.Launcher, Does.StartWith(
                "plugins {\n    id 'com.android.application'\n    id 'com.bugsee.android.gradle' // bugsee:gradle-plugin\n}\n"));
            Assert.That(patch.Launcher, Does.Contain("something"));
            Assert.That(GradlePluginInjection.Apply(BuildscriptRootGroovy, Groovy, patch.Launcher, Groovy, Version).Launcher, Is.EqualTo(patch.Launcher));
        }

        [Test]
        public void A_single_quoted_triple_string_does_not_hide_the_anchor_or_the_block_after_it()
        {
            string launcher = "def note = '''\napply plugin: 'com.android.application'\n'''\n\napply plugin: 'com.android.application'\n";

            var patch = GradlePluginInjection.Apply(BuildscriptRootGroovy, Groovy, launcher, Groovy, Version);

            Assert.That(patch.Applied, Is.True);
            Assert.That(patch.Launcher, Does.EndWith("apply plugin: 'com.android.application'\napply plugin: 'com.bugsee.android.gradle' // bugsee:gradle-plugin\n\nbugsee {\n    // App token is supplied at runtime via Bugsee.Launch.\n    ndk {\n        enabled = true\n    }\n} // bugsee:gradle-ndk\n"));
        }

        [Test]
        public void A_Kotlin_raw_string_holding_plugin_block_text_is_not_a_block()
        {
            string launcher = "val note = \"\"\"\nplugins {\n\"\"\"\n\nplugins {\n    id(\"com.android.application\")\n}\n";

            var patch = GradlePluginInjection.Apply(KotlinRoot, Kotlin, launcher, Kotlin, Version);

            Assert.That(patch.Applied, Is.True);
            Assert.That(patch.Launcher, Does.Contain("    id(\"com.android.application\")\n    id(\"com.bugsee.android.gradle\") version \"4.0.8\" // bugsee:gradle-plugin\n}"));
        }

        [Test]
        public void The_build_id_block_anchor_ignores_android_blocks_inside_strings_and_comments()
        {
            string launcher = "def note = '''\nandroid {\n'''\n/*\nandroid {\n*/\nandroid {\n}\n";

            string result = GradleBuildIdBlock.Apply(launcher, out BuildIdBlockStatus status);

            Assert.That(status, Is.EqualTo(BuildIdBlockStatus.Applied));
            Assert.That(result.IndexOf("// bugsee:build-id begin"), Is.GreaterThan(result.IndexOf("*/")));
        }

        [Test]
        public void Strings_that_only_name_the_plugin_are_not_a_declaration()
        {
            Assert.That(GradlePluginInjection.HasUnmarkedDeclaration("plugins.withId('com.bugsee.android.gradle') { println 'x' }\n"), Is.False);
            Assert.That(GradlePluginInjection.HasUnmarkedDeclaration("println 'applying com.bugsee.android.gradle'\n"), Is.False);
            Assert.That(GradlePluginInjection.HasUnmarkedDeclaration("def x = '''\nid 'com.bugsee.android.gradle'\n'''\n"), Is.False);
            Assert.That(GradlePluginInjection.HasUnmarkedDeclaration("id 'com.bugsee.android.gradle' version '4.0.5'\n"), Is.True);
            Assert.That(GradlePluginInjection.HasUnmarkedDeclaration("id(\"com.bugsee.android.gradle\")\n"), Is.True);
            Assert.That(GradlePluginInjection.HasUnmarkedDeclaration("apply(plugin = \"com.bugsee.android.gradle\")\n"), Is.True);
            Assert.That(GradlePluginInjection.HasUnmarkedDeclaration("apply plugin: 'com.bugsee.android.gradle'\n"), Is.True);
            Assert.That(GradlePluginInjection.HasUnmarkedDeclaration("classpath(\"com.bugsee:bugsee-android-gradle-plugin:4.0.5\")\n"), Is.True);
        }

        // ---- catalog ------------------------------------------------------------------------------------------

        const string Catalog = "[versions]\nagp = \"8.7.2\"\n[plugins]\nandroid-application = { id = \"com.android.application\", version.ref = \"agp\" }\n";

        [Test]
        public void Only_references_to_the_libs_catalog_are_resolved_against_libs_versions_toml()
        {
            Assert.That(GradleVersionCatalog.ResolveAgpVersion("alias(libs.plugins.android.application)", Catalog), Is.EqualTo("8.7.2"));
            Assert.That(GradleVersionCatalog.ResolveAgpVersion("alias(androidx.plugins.android.application)", Catalog), Is.Null);
            Assert.That(GradleVersionCatalog.ResolveAgpVersion("alias(androidx.plugins.android.application)", Catalog, "androidx"), Is.EqualTo("8.7.2"));
        }

        [Test]
        public void The_table_form_and_single_quoted_literals_are_read()
        {
            string table = "[versions]\nagp = '8.3.0'\n\n[plugins.android-application]\nid = 'com.android.application'\nversion.ref = 'agp'\n\n[plugins.android-library]\nid = \"com.android.library\"\nversion.ref = \"agp\"\n";

            Assert.That(GradleVersionCatalog.ResolveAgpVersion("alias(libs.plugins.android.application)", table), Is.EqualTo("8.3.0"));
            Assert.That(GradleVersionCatalog.ResolveAgpVersion("alias(libs.plugins.android.library)", table), Is.EqualTo("8.3.0"));
        }

        [Test]
        public void A_camelCase_alias_is_resolved_and_recognised_in_the_launcher()
        {
            string catalog = "[versions]\nagp = \"9.0.0\"\n[plugins]\nandroidApplication = { id = \"com.android.application\", version.ref = \"agp\" }\n";
            string launcher = "plugins {\n    alias(libs.plugins.androidApplication)\n}\n\nandroid {\n}\n";

            Assert.That(GradleVersionCatalog.ResolveAgpVersion("alias(libs.plugins.androidApplication) apply false", catalog), Is.EqualTo("9.0.0"));
            var patch = GradlePluginInjection.Apply(
                "plugins {\n    alias(libs.plugins.androidApplication) apply false\n}\n", Kotlin, launcher, Kotlin, Version);
            Assert.That(patch.Applied, Is.True);
            Assert.That(patch.Launcher, Does.Contain("alias(libs.plugins.androidApplication)\n    id(\"com.bugsee.android.gradle\")"));
        }

        [Test]
        public void The_manual_lines_fit_the_root_shape_of_a_kotlin_project_with_a_plugins_block()
        {
            string apply = "apply(plugin = \"com.android.application\")\n\nandroid {\n}\n";

            var patch = GradlePluginInjection.Apply(KotlinRoot, Kotlin, apply, Kotlin, Version);

            Assert.That(patch.Applied, Is.False);
            Assert.That(patch.Message, Does.Contain("two edits"));
            Assert.That(patch.Message, Does.Contain("apply false in the root plugins { } block"));
            Assert.That(patch.Message, Does.Contain("configure<com.bugsee.android.gradle.BugseePluginExtension>"));
            Assert.That(patch.Message, Does.Not.Contain("classpath("));
        }
    }
}
