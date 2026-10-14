using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class GradleMavenInjectionTests
    {
        const string Group = "com.bugsee";
        const string Version = "7.4.1";
        static readonly string[] Artifacts = { "bugsee-android", "bugsee-android-ndk" };

        const string CoreLine = "    implementation 'com.bugsee:bugsee-android:7.4.1'";
        const string NdkLine = "    implementation 'com.bugsee:bugsee-android-ndk:7.4.1'";

        // unityLibrary/build.gradle as Unity 2021.3.45 generates it from the default mainTemplate.gradle
        // (**DEPS** empty, so the closing brace follows the last dependency).
        const string Unity2021LibraryGradle =
            "apply plugin: 'com.android.library'\n" +
            "\n" +
            "\n" +
            "dependencies {\n" +
            "    implementation fileTree(dir: 'libs', include: ['*.jar'])\n" +
            "\n" +
            "}\n" +
            "\n" +
            "android {\n" +
            "    namespace \"com.unity3d.player\"\n" +
            "    buildTypes {\n" +
            "        release {\n" +
            "            minifyEnabled false\n" +
            "        }\n" +
            "    }\n" +
            "}\n";

        // Unity 6 adds a shared script and keeps the same dependencies block shape.
        const string Unity6LibraryGradle =
            "apply plugin: 'com.android.library'\n" +
            "apply from: '../shared/keepUnitySymbols.gradle'\n" +
            "\n" +
            "dependencies {\n" +
            "    implementation fileTree(dir: 'libs', include: ['*.jar'])\n" +
            "    implementation(name: 'some-plugin', ext:'aar')\n" +
            "}\n" +
            "\n" +
            "android {\n" +
            "    namespace \"com.unity3d.player\"\n" +
            "}\n";

        const string Unity2021Settings =
            "pluginManagement {\n" +
            "    repositories {\n" +
            "        \n" +
            "        gradlePluginPortal()\n" +
            "        google()\n" +
            "        mavenCentral()\n" +
            "    }\n" +
            "}\n" +
            "\n" +
            "include ':launcher', ':unityLibrary'\n" +
            "\n" +
            "\n" +
            "dependencyResolutionManagement {\n" +
            "    repositoriesMode.set(RepositoriesMode.PREFER_SETTINGS)\n" +
            "    repositories {\n" +
            "        \n" +
            "        google()\n" +
            "        mavenCentral()\n" +
            "        flatDir {\n" +
            "            dirs \"${project(':unityLibrary').projectDir}/libs\"\n" +
            "        }\n" +
            "    }\n" +
            "}";

        static GradleMavenInjection.DependencyResult Apply(string gradle)
        {
            return GradleMavenInjection.ApplyDependencies(gradle, Group, Artifacts, Version);
        }

        static int Count(string text, string needle)
        {
            return Regex.Matches(text, Regex.Escape(needle)).Count;
        }

        [Test]
        public void Unity_2021_layout_gets_both_dependencies_inside_the_top_level_block()
        {
            var result = Apply(Unity2021LibraryGradle);

            Assert.That(result.Status, Is.EqualTo(GradleMavenInjection.DependencyStatus.Injected));
            Assert.That(result.Warnings, Is.Empty);
            string expectedBlock =
                "dependencies {\n" +
                "    " + GradleMavenInjection.DependenciesBeginMarker + " (com.bugsee.unity)\n" +
                CoreLine + "\n" +
                NdkLine + "\n" +
                "    " + GradleMavenInjection.DependenciesEndMarker + "\n" +
                "    implementation fileTree(dir: 'libs', include: ['*.jar'])\n";
            Assert.That(result.Text, Does.Contain(expectedBlock));
            Assert.That(result.Text, Does.EndWith("}\n"));
            Assert.That(result.Text.Replace(expectedBlock, "dependencies {\n    implementation fileTree(dir: 'libs', include: ['*.jar'])\n"),
                Is.EqualTo(Unity2021LibraryGradle), "nothing else changes");
        }

        [Test]
        public void Second_apply_is_a_no_op()
        {
            string once = Apply(Unity6LibraryGradle).Text;
            var twice = Apply(once);

            Assert.That(twice.Status, Is.EqualTo(GradleMavenInjection.DependencyStatus.AlreadyInjected));
            Assert.That(twice.Text, Is.EqualTo(once));
            Assert.That(Count(once, "bugsee-android-ndk:"), Is.EqualTo(1));
            Assert.That(Count(once, "bugsee-android:"), Is.EqualTo(1));
        }

        [Test]
        public void Marked_region_is_upserted_when_the_pinned_version_changes()
        {
            string old = GradleMavenInjection.ApplyDependencies(Unity2021LibraryGradle, Group, Artifacts, "7.3.0").Text;
            var result = Apply(old);

            Assert.That(result.Status, Is.EqualTo(GradleMavenInjection.DependencyStatus.Injected));
            Assert.That(result.Text, Does.Contain(CoreLine).And.Contain(NdkLine));
            Assert.That(result.Text, Does.Not.Contain("7.3.0"));
            Assert.That(result.Text, Is.EqualTo(Apply(Unity2021LibraryGradle).Text));
        }

        [Test]
        public void Edm4u_declaration_is_kept_and_nothing_is_added()
        {
            string edm = Unity2021LibraryGradle.Replace(
                "    implementation fileTree(dir: 'libs', include: ['*.jar'])\n",
                "    implementation fileTree(dir: 'libs', include: ['*.jar'])\n" +
                "// Android Resolver Dependencies Start\n" +
                "    implementation 'com.bugsee:bugsee-android:7.4.1' // Packages/com.bugsee.unity/Editor/BugseeAndroidDependencies.xml\n" +
                "    implementation 'com.bugsee:bugsee-android-ndk:7.4.1' // Packages/com.bugsee.unity/Editor/BugseeAndroidDependencies.xml\n" +
                "// Android Resolver Dependencies End\n");
            var result = Apply(edm);

            Assert.That(result.Status, Is.EqualTo(GradleMavenInjection.DependencyStatus.DeclaredByProject));
            Assert.That(result.Text, Is.EqualTo(edm));
            Assert.That(result.Warnings, Is.Empty);
        }

        [Test]
        public void Project_declaration_with_another_version_is_kept_and_reported()
        {
            string custom = Unity2021LibraryGradle.Replace(
                "    implementation fileTree(dir: 'libs', include: ['*.jar'])\n",
                "    implementation fileTree(dir: 'libs', include: ['*.jar'])\n" +
                "    implementation \"com.bugsee:bugsee-android:7.3.0\"\n" +
                "    implementation group: 'com.bugsee', name: 'bugsee-android-ndk', version: '7.3.0'\n");
            var result = Apply(custom);

            Assert.That(result.Status, Is.EqualTo(GradleMavenInjection.DependencyStatus.DeclaredByProject));
            Assert.That(result.Text, Is.EqualTo(custom));
            Assert.That(result.Warnings, Has.Count.EqualTo(1));
            Assert.That(result.Warnings[0], Does.Contain("com.bugsee:bugsee-android:7.3.0").And.Contain("7.4.1"));
        }

        [Test]
        public void Project_declaring_only_the_core_artifact_is_warned_about_the_missing_ndk()
        {
            string custom = Unity2021LibraryGradle.Replace(
                "dependencies {\n",
                "dependencies {\n    implementation 'com.bugsee:bugsee-android:7.4.1'\n");
            var result = Apply(custom);

            Assert.That(result.Status, Is.EqualTo(GradleMavenInjection.DependencyStatus.DeclaredByProject));
            Assert.That(result.Text, Is.EqualTo(custom));
            Assert.That(result.Warnings, Has.Count.EqualTo(1));
            Assert.That(result.Warnings[0], Does.Contain("bugsee-android-ndk"));
        }

        [Test]
        public void Local_aar_in_plugins_counts_as_a_project_declaration()
        {
            string aar = Unity6LibraryGradle.Replace(
                "    implementation(name: 'some-plugin', ext:'aar')\n",
                "    implementation(name: 'bugsee-android-7.2.0', ext:'aar')\n" +
                "    implementation(name: 'bugsee-android-ndk-7.2.0', ext:'aar')\n");
            var result = Apply(aar);

            Assert.That(result.Status, Is.EqualTo(GradleMavenInjection.DependencyStatus.DeclaredByProject));
            Assert.That(result.Warnings, Has.Count.EqualTo(2));
            Assert.That(result.Warnings[0], Does.Contain("bugsee-android-7.2.0"));
        }

        [Test]
        public void Region_left_by_an_earlier_run_is_removed_when_the_project_now_declares_bugsee()
        {
            string injected = Apply(Unity2021LibraryGradle).Text;
            string withEdm = injected.Replace(
                "    implementation fileTree(dir: 'libs', include: ['*.jar'])\n",
                "    implementation fileTree(dir: 'libs', include: ['*.jar'])\n" +
                "    implementation 'com.bugsee:bugsee-android:7.4.1'\n" +
                "    implementation 'com.bugsee:bugsee-android-ndk:7.4.1'\n");
            var result = Apply(withEdm);

            Assert.That(result.Status, Is.EqualTo(GradleMavenInjection.DependencyStatus.DeclaredByProject));
            Assert.That(result.Text, Does.Not.Contain("bugsee:maven-dependencies"));
            Assert.That(Count(result.Text, "bugsee-android:"), Is.EqualTo(1));
            Assert.That(Count(result.Text, "bugsee-android-ndk:"), Is.EqualTo(1));
        }

        [Test]
        public void Commented_out_and_unrelated_bugsee_artifacts_are_not_project_declarations()
        {
            string gradle = Unity2021LibraryGradle.Replace(
                "dependencies {\n",
                "dependencies {\n" +
                "    // implementation 'com.bugsee:bugsee-android:7.0.0'\n" +
                "    /* implementation 'com.bugsee:bugsee-android-ndk:7.0.0' */\n" +
                "    implementation 'com.bugsee:bugsee-android-feedback:7.4.1'\n" +
                "    implementation 'org.example:my-bugsee-android:1.0'\n");
            var result = Apply(gradle);

            Assert.That(result.Status, Is.EqualTo(GradleMavenInjection.DependencyStatus.Injected));
            Assert.That(result.Text, Does.Contain(CoreLine).And.Contain(NdkLine));
        }

        [Test]
        public void Nested_dependencies_blocks_are_not_the_target()
        {
            string gradle =
                "buildscript {\n" +
                "    dependencies {\n" +
                "        classpath 'com.android.tools.build:gradle:7.4.2'\n" +
                "    }\n" +
                "}\n" +
                "apply plugin: 'com.android.library'\n" +
                "dependencies {\n" +
                "    implementation fileTree(dir: 'libs', include: ['*.jar'])\n" +
                "}\n";
            string text = Apply(gradle).Text;

            Assert.That(text, Does.Contain(
                "    }\n}\napply plugin: 'com.android.library'\ndependencies {\n    " +
                GradleMavenInjection.DependenciesBeginMarker));
        }

        [Test]
        public void Missing_or_single_line_dependencies_block_gets_a_block_of_its_own()
        {
            foreach (string gradle in new[]
                     {
                         "apply plugin: 'com.android.library'\n",
                         "apply plugin: 'com.android.library'\ndependencies { implementation 'a:b:1' }\n",
                     })
            {
                var result = Apply(gradle);
                Assert.That(result.Status, Is.EqualTo(GradleMavenInjection.DependencyStatus.Injected));
                Assert.That(result.Text, Does.StartWith(gradle));
                Assert.That(result.Text, Does.EndWith(
                    "\ndependencies {\n" +
                    "    " + GradleMavenInjection.DependenciesBeginMarker + " (com.bugsee.unity)\n" +
                    CoreLine + "\n" + NdkLine + "\n" +
                    "    " + GradleMavenInjection.DependenciesEndMarker + "\n" +
                    "}\n"));
                Assert.That(Apply(result.Text).Text, Is.EqualTo(result.Text));
            }
        }

        [Test]
        public void Crlf_files_stay_crlf()
        {
            string crlf = Unity2021LibraryGradle.Replace("\n", "\r\n");
            string text = Apply(crlf).Text;

            Assert.That(text.Replace("\r\n", string.Empty), Does.Not.Contain("\n"));
            Assert.That(text, Does.Contain(CoreLine + "\r\n"));
        }

        [Test]
        public void Unity_2021_settings_already_declare_maven_central()
        {
            var status = GradleMavenInjection.EnsureMavenCentral(
                Unity2021Settings, "dependencyResolutionManagement", out string updated);

            Assert.That(status, Is.EqualTo(GradleMavenInjection.RepositoryStatus.AlreadyPresent));
            Assert.That(updated, Is.EqualTo(Unity2021Settings));
        }

        [Test]
        public void Maven_central_in_plugin_management_does_not_count_for_project_repositories()
        {
            string settings = Unity2021Settings.Replace(
                "        google()\n        mavenCentral()\n        flatDir {",
                "        google()\n        // mavenCentral()\n        flatDir {");
            var status = GradleMavenInjection.EnsureMavenCentral(
                settings, "dependencyResolutionManagement", out string updated);

            Assert.That(status, Is.EqualTo(GradleMavenInjection.RepositoryStatus.Inserted));
            Assert.That(updated, Does.Contain(
                "    repositories {\n        mavenCentral() " + GradleMavenInjection.RepositoryMarker + "\n        \n        google()"));
            Assert.That(Count(updated, "mavenCentral()"), Is.EqualTo(3), "plugin repos + commented + inserted");

            var again = GradleMavenInjection.EnsureMavenCentral(
                updated, "dependencyResolutionManagement", out string twice);
            Assert.That(again, Is.EqualTo(GradleMavenInjection.RepositoryStatus.AlreadyPresent));
            Assert.That(twice, Is.EqualTo(updated));
        }

        [Test]
        public void Root_allprojects_layout_gets_maven_central()
        {
            const string root =
                "buildscript {\n" +
                "    repositories {\n" +
                "        google()\n" +
                "        mavenCentral()\n" +
                "    }\n" +
                "}\n" +
                "\n" +
                "allprojects {\n" +
                "    repositories {\n" +
                "        google()\n" +
                "        flatDir {\n" +
                "            dirs \"${project(':unityLibrary').projectDir}/libs\"\n" +
                "        }\n" +
                "    }\n" +
                "}\n";
            const string settings = "include ':launcher', ':unityLibrary'\n";

            Assert.That(
                GradleMavenInjection.EnsureMavenCentral(settings, "dependencyResolutionManagement", out _),
                Is.EqualTo(GradleMavenInjection.RepositoryStatus.BlockNotFound));
            var status = GradleMavenInjection.EnsureMavenCentral(root, "allprojects", out string updated);

            Assert.That(status, Is.EqualTo(GradleMavenInjection.RepositoryStatus.Inserted));
            Assert.That(updated, Does.Contain(
                "allprojects {\n    repositories {\n        mavenCentral() " + GradleMavenInjection.RepositoryMarker +
                "\n        google()\n"));
        }

        [Test]
        public void Maven_central_by_url_is_recognised()
        {
            const string root =
                "allprojects {\n" +
                "    repositories {\n" +
                "        maven { url 'https://repo.maven.apache.org/maven2' }\n" +
                "    }\n" +
                "}\n";
            Assert.That(
                GradleMavenInjection.EnsureMavenCentral(root, "allprojects", out _),
                Is.EqualTo(GradleMavenInjection.RepositoryStatus.AlreadyPresent));
        }
    }

    public class AndroidMavenDependenciesSourceTests
    {
        static string FindRepoRoot()
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, "package.json")))
                    return dir;
                dir = Directory.GetParent(dir)?.FullName;
            }

            throw new InvalidOperationException("Could not locate repo root (package.json).");
        }

        static string RepoFile(string relative)
        {
            return Path.GetFullPath(Path.Combine(FindRepoRoot(), relative));
        }

        [Test]
        public void Injector_is_a_post_generate_step_pinned_to_the_generated_version_constants()
        {
            string text = File.ReadAllText(RepoFile("Editor/BugseeAndroidMavenDependencies.cs"));

            Assert.That(text, Does.Contain(": IPostGenerateGradleAndroidProject"));
            Assert.That(text, Does.Contain("BugseePackageVersion.AndroidSdkVersion"));
            Assert.That(text, Does.Contain("BugseePackageVersion.AndroidMavenArtifact"));
            Assert.That(text, Does.Contain("BugseePackageVersion.AndroidNdkArtifact"));
            Assert.That(text, Does.Contain("GradleMavenInjection.ApplyDependencies("));
            Assert.That(text, Does.Contain("GradleMavenInjection.DslForFile(path)"));
            Assert.That(text, Does.Contain("GradleMavenInjection.EnsureProjectRepositories("));
            Assert.That(text, Does.Contain("\".gradle.kts\""));
            Assert.That(text, Does.Not.Contain("#if UNITY_ANDROID"));
        }

        [Test]
        public void Version_constants_match_versions_env()
        {
            string env = File.ReadAllText(RepoFile("Tools~/versions.env"));
            string constants = File.ReadAllText(RepoFile("Runtime/BugseePackageVersion.cs"));

            foreach (var (key, constant) in new[]
                     {
                         ("ANDROID_SDK_VERSION", "AndroidSdkVersion"),
                         ("ANDROID_MAVEN_GROUP", "AndroidMavenGroup"),
                         ("ANDROID_MAVEN_ARTIFACT", "AndroidMavenArtifact"),
                         ("ANDROID_NDK_ARTIFACT", "AndroidNdkArtifact"),
                     })
            {
                string value = Regex.Match(env, "^" + key + "=\"([^\"]+)\"", RegexOptions.Multiline).Groups[1].Value;
                Assert.That(value, Is.Not.Empty, key);
                Assert.That(constants, Does.Contain("public const string " + constant + " = \"" + value + "\";"));
            }
        }

        [Test]
        public void Package_does_not_depend_on_edm4u()
        {
            string package = File.ReadAllText(RepoFile("package.json"));
            Assert.That(package, Does.Not.Contain("external-dependency-manager"));
            Assert.That(File.Exists(RepoFile("Editor/BugseeAndroidDependencies.xml")), Is.False);

            string sample = File.ReadAllText(RepoFile("Samples~/BugseeMiniGame/Packages/manifest.json"));
            Assert.That(sample, Does.Not.Contain("external-dependency-manager"));
        }
    }
}
