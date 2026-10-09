using System;
using System.IO;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class MarkedGradleBlockTests
    {
        const string Anchor = "plugins {";
        const string Marked = "    id 'com.bugsee.android.gradle' version '4.0.8' apply false // bugsee:gradle-plugin";
        const string LauncherAnchor = "apply plugin: 'com.android.application'";

        static string SampleLauncherTemplatePath =>
            Path.GetFullPath(Path.Combine(
                TestContext.CurrentContext.TestDirectory,
                "..", "..", "..", "..", "..",
                "Samples~", "BugseeMiniGame", "Assets", "Plugins", "Android", "launcherTemplate.gradle"));

        static string LauncherPatch =>
            "apply plugin: 'com.bugsee.android.gradle' // bugsee:gradle-plugin\n\n" +
            "bugsee {\n" +
            "    // App token is supplied at runtime via Bugsee.Launch.\n" +
            "    ndk {\n" +
            "        enabled = true\n" +
            "    }\n" +
            "} // bugsee:gradle-ndk";

        [Test]
        public void Second_apply_is_a_no_op_and_a_commented_anchor_is_refused()
        {
            string once = MarkedGradleBlock.Apply(Anchor + "\n}\n", Anchor, Marked);
            string twice = MarkedGradleBlock.Apply(once, Anchor, Marked);
            Assert.That(twice, Is.EqualTo(once));

            Assert.Throws<InvalidOperationException>(() =>
                MarkedGradleBlock.Apply("plugins { /*\n}\n", Anchor, Marked));
        }

        [Test]
        public void Legacy_bugsee_marker_upgrades_without_duplicating_at_anchor()
        {
            const string legacy =
                Anchor + "\n    // Bugsee Gradle plugin\n    id 'com.bugsee.android.gradle' version '4.0.8' apply false\n}\n";
            string once = MarkedGradleBlock.Apply(legacy, Anchor, Marked);
            Assert.That(once, Does.Contain("// bugsee:gradle-plugin"));
            Assert.That(once, Does.Not.Contain("// Bugsee Gradle plugin"));
            Assert.That(once.IndexOf("com.bugsee.android.gradle", StringComparison.Ordinal),
                Is.EqualTo(once.LastIndexOf("com.bugsee.android.gradle", StringComparison.Ordinal)));

            string twice = MarkedGradleBlock.Apply(once, Anchor, Marked);
            Assert.That(twice, Is.EqualTo(once));
        }

        [Test]
        public void Marker_line_is_upserted_when_version_changes()
        {
            const string older =
                Anchor + "\n    id 'com.bugsee.android.gradle' version '4.0.7' apply false // bugsee:gradle-plugin\n}\n";
            const string newer =
                "    id 'com.bugsee.android.gradle' version '4.0.8' apply false // bugsee:gradle-plugin";
            string once = MarkedGradleBlock.Apply(older, Anchor, newer);
            Assert.That(once, Does.Contain("version '4.0.8'"));
            Assert.That(once, Does.Not.Contain("version '4.0.7'"));
        }

        [Test]
        public void Commented_out_marker_is_replaced_with_active_patch()
        {
            const string commented =
                LauncherAnchor + "\n// apply plugin: 'com.bugsee.android.gradle' // bugsee:gradle-plugin\n";
            string once = MarkedGradleBlock.Apply(commented, LauncherAnchor, LauncherPatch);
            Assert.That(once, Does.Contain("enabled = true"));
            Assert.That(once, Does.Not.Contain("// apply plugin: 'com.bugsee.android.gradle'"));
        }

        [Test]
        public void Sample_launcher_gets_plugin_and_ndk_block_idempotently()
        {
            Assume.That(File.Exists(SampleLauncherTemplatePath), Is.True, "sample launcher template missing");
            string sample = File.ReadAllText(SampleLauncherTemplatePath);
            string once = MarkedGradleBlock.Apply(sample, LauncherAnchor, LauncherPatch);
            Assert.That(once, Does.Contain(MarkedGradleBlock.NdkMarkerComment));
            Assert.That(once, Does.Contain("enabled = true"));

            string twice = MarkedGradleBlock.Apply(once, LauncherAnchor, LauncherPatch);
            Assert.That(twice, Is.EqualTo(once));
        }

        [Test]
        public void Plugin_only_launcher_patch_adds_ndk_block()
        {
            const string pluginOnly =
                LauncherAnchor + "\napply plugin: 'com.bugsee.android.gradle' // bugsee:gradle-plugin\n";
            string once = MarkedGradleBlock.Apply(pluginOnly, LauncherAnchor, LauncherPatch);
            Assert.That(once, Does.Contain(MarkedGradleBlock.NdkMarkerComment));
            string twice = MarkedGradleBlock.Apply(once, LauncherAnchor, LauncherPatch);
            Assert.That(twice, Is.EqualTo(once));
        }

        [Test]
        public void Split_ndk_block_does_not_delete_intervening_gradle()
        {
            const string split =
                LauncherAnchor + "\n" +
                "apply plugin: 'com.bugsee.android.gradle' // bugsee:gradle-plugin\n\n" +
                "dependencies {\n    implementation project(':unityLibrary')\n}\n\n" +
                "android {\n    compileSdkVersion 34\n}\n\n" +
                "bugsee {\n    ndk { enabled = true }\n} // bugsee:gradle-ndk\n";
            string twice = MarkedGradleBlock.Apply(split, LauncherAnchor, LauncherPatch);
            Assert.That(twice, Does.Contain("dependencies {"));
            Assert.That(twice, Does.Contain("android {"));
            Assert.That(twice, Does.Contain(MarkedGradleBlock.NdkMarkerComment));
            Assert.That(MarkedGradleBlock.Apply(twice, LauncherAnchor, LauncherPatch), Is.EqualTo(twice));
        }

        [Test]
        public void Legacy_launcher_with_unmarked_ndk_block_upgrades_without_duplicating_ndk()
        {
            const string legacyLauncher =
                LauncherAnchor + "\n" +
                "// Bugsee Gradle plugin\n" +
                "apply plugin: 'com.bugsee.android.gradle'\n\n" +
                "bugsee {\n    ndk { enabled = true }\n}\n\n" +
                "dependencies {\n    implementation project(':unityLibrary')\n}\n";
            string once = MarkedGradleBlock.Apply(legacyLauncher, LauncherAnchor, LauncherPatch);
            Assert.That(once, Does.Contain(MarkedGradleBlock.NdkMarkerComment));
            Assert.That(once.IndexOf("bugsee {", StringComparison.Ordinal),
                Is.EqualTo(once.LastIndexOf("bugsee {", StringComparison.Ordinal)));
            string twice = MarkedGradleBlock.Apply(once, LauncherAnchor, LauncherPatch);
            Assert.That(twice, Is.EqualTo(once));
        }
    }
}
