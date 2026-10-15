using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class GradleBuildIdBlockTests
    {
        static string Template(string unity, string name) =>
            File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "UnityTemplates", unity, name));

        [TestCase("2021.3.45f1")]
        [TestCase("2022.3.62f3")]
        public void The_block_goes_before_the_android_block_after_the_application_plugin(string unity)
        {
            var launcher = Template(unity, "launcherTemplate.gradle");

            var patched = GradleBuildIdBlock.Apply(launcher, out var status);

            Assert.That(status, Is.EqualTo(BuildIdBlockStatus.Applied));
            int apply = patched.IndexOf("apply plugin: 'com.android.application'");
            int block = patched.IndexOf(GradleBuildIdBlock.Begin);
            int end = patched.IndexOf(GradleBuildIdBlock.End);
            int android = patched.IndexOf("\nandroid {");
            Assert.That(apply, Is.GreaterThanOrEqualTo(0));
            Assert.That(block, Is.GreaterThan(apply));
            Assert.That(end, Is.GreaterThan(block));
            Assert.That(android, Is.GreaterThan(end));
            Assert.That(patched.Replace(GradleBuildIdBlock.Snippet() + "\n\n", ""), Is.EqualTo(launcher), "only the block is added");
        }

        [Test]
        public void Applying_twice_changes_nothing()
        {
            var once = GradleBuildIdBlock.Apply(Template("2022.3.62f3", "launcherTemplate.gradle"), out _);

            var twice = GradleBuildIdBlock.Apply(once, out var status);

            Assert.That(status, Is.EqualTo(BuildIdBlockStatus.AlreadyCurrent));
            Assert.That(twice, Is.EqualTo(once));
        }

        [Test]
        public void A_block_from_an_earlier_package_version_is_replaced_in_place()
        {
            var launcher = Template("2022.3.62f3", "launcherTemplate.gradle");
            var once = GradleBuildIdBlock.Apply(launcher, out _);
            var stale = once.Replace("bugsee.build_id=", "bugsee.old_id=");

            var fixedUp = GradleBuildIdBlock.Apply(stale, out var status);

            Assert.That(status, Is.EqualTo(BuildIdBlockStatus.Applied));
            Assert.That(fixedUp, Is.EqualTo(once));
        }

        [Test]
        public void A_launcher_without_a_top_level_android_block_is_left_alone()
        {
            const string text = "apply plugin: 'com.android.application'\n// android {\n/* android {\n*/\ndependencies {\n    android {\n    }\n}\n";

            var patched = GradleBuildIdBlock.Apply(text, out var status);

            Assert.That(status, Is.EqualTo(BuildIdBlockStatus.NoAnchor));
            Assert.That(patched, Is.EqualTo(text));
        }

        [Test]
        public void Crlf_launchers_keep_their_line_endings()
        {
            var crlf = Template("2022.3.62f3", "launcherTemplate.gradle").Replace("\r\n", "\n").Replace("\n", "\r\n");

            var patched = GradleBuildIdBlock.Apply(crlf, out _);

            Assert.That(patched.Replace("\r\n", ""), Does.Not.Contain("\n"));
        }

        [Test]
        public void The_block_never_fails_the_build_and_uses_only_api_present_in_agp_7_4()
        {
            var snippet = GradleBuildIdBlock.Snippet();

            Assert.That(snippet, Does.Contain("project.extensions.findByName('androidComponents') != null"));
            Assert.That(snippet, Does.Contain("catch (Throwable t)"));
            Assert.That(snippet, Does.Contain("variant.artifacts.get(com.android.build.api.artifact.SingleArtifact.OBFUSCATION_MAPPING_FILE.INSTANCE)"));
            Assert.That(snippet, Does.Not.Contain("toListenTo"), "AGP 7.4.2 has no toListenTo");
            Assert.That(snippet, Does.Not.Contain("toTransform"), "transforming ASSETS on AGP 7.4.2 drops every library asset, Unity's game data included");
            Assert.That(snippet, Does.Contain("variant.sources.assets.addGeneratedSourceDirectory(asset"));
            Assert.That(snippet, Does.Contain("bugsee_build_id.properties"));
            Assert.That(snippet, Does.Contain("bugsee.build_id=${id}"));
            Assert.That(snippet.Split('\n').Count(l => l.StartsWith("import ")), Is.Zero);
        }

        [TestCase("id 'com.android.application' version '7.4.2' apply false", true)]
        [TestCase("id 'com.android.application' version '7.1.2' apply false", false)]
        [TestCase("id 'com.android.application' version '8.3.0' apply false", true)]
        [TestCase("id 'com.android.application' version '8.6.0' apply false", false)]
        [TestCase("id 'com.android.application' version '9.0.0' apply false", false)]
        [TestCase("classpath 'com.android.tools.build:gradle:4.0.1'", false)]
        [TestCase("no agp here", false)]
        public void The_block_is_for_agp_7_4_up_to_the_plugins_own_minimum(string root, bool expected)
        {
            Assert.That(GradleTemplateDefaults.AgpSupportsBuildIdBlock(root), Is.EqualTo(expected));
        }
    }
}
