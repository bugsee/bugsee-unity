using System.IO;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    /// <summary>
    /// The templates are the ones Unity ships (copied from Hub/Editor/&lt;version&gt;/PlaybackEngines/AndroidPlayer/Tools/GradleTemplates).
    /// A hand-written copy that pinned AGP 7.4.2 broke Unity 6000.0.84f1: its Gradle 9.3.1 has no DependencyHandler.module().
    /// </summary>
    public class GradleTemplateDefaultsTests
    {
        static string Fixture(string unity, string name) =>
            File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "UnityTemplates", unity, name));

        [TestCase("2021.3.45f1", 7)]
        [TestCase("6000.0.84f1", 9)]
        public void Unity_default_base_template_pins_its_own_agp_major(string unity, int major)
        {
            Assert.That(GradleTemplateDefaults.PinnedAgpMajor(Fixture(unity, "baseProjectTemplate.gradle")), Is.EqualTo(major));
        }

        [Test]
        public void A_template_pinning_another_agp_major_than_unitys_default_is_reported()
        {
            string unity6 = Fixture("6000.0.84f1", "baseProjectTemplate.gradle");
            string unity2021 = Fixture("2021.3.45f1", "baseProjectTemplate.gradle");

            Assert.That(GradleTemplateDefaults.PinsDifferentAgpMajor(unity2021, unity6), Is.True);
            Assert.That(GradleTemplateDefaults.PinsDifferentAgpMajor(unity6, unity6), Is.False);
            Assert.That(GradleTemplateDefaults.PinsDifferentAgpMajor(unity2021, unity2021), Is.False);
            Assert.That(GradleTemplateDefaults.PinsDifferentAgpMajor("plugins {\n}\n", unity6), Is.False);
        }

        [Test]
        public void Classpath_style_pin_is_read()
        {
            Assert.That(GradleTemplateDefaults.PinnedAgpMajor("classpath 'com.android.tools.build:gradle:8.3.0'"), Is.EqualTo(8));
            Assert.That(GradleTemplateDefaults.PinnedAgpMajor(null), Is.EqualTo(-1));
        }

        [Test]
        public void Release_debug_symbols_are_added_to_the_2021_launcher_once()
        {
            string template = Fixture("2021.3.45f1", "launcherTemplate.gradle");
            string once = GradleTemplateDefaults.EnsureReleaseDebugSymbols(template);

            Assert.That(once, Does.Contain("        release {\n            ndk {\n                debugSymbolLevel 'FULL'\n            }\n            minifyEnabled"));
            Assert.That(GradleTemplateDefaults.EnsureReleaseDebugSymbols(once), Is.EqualTo(once));
            Assert.That(once.Split('{').Length, Is.EqualTo(once.Split('}').Length));
        }

        [Test]
        public void Unity_6_launcher_already_sets_a_symbol_level_and_is_left_alone()
        {
            string template = Fixture("6000.0.84f1", "launcherTemplate.gradle");
            Assert.That(GradleTemplateDefaults.EnsureReleaseDebugSymbols(template), Is.EqualTo(template));
        }

        static string Legacy(string name) =>
            File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "LegacyPackage", name));

        // Fixtures: the files the package generated before this change (Unity 2021.3 default + AGP 7.4.2 pin,
        // with and without the marked plugin lines).
        [TestCase("base-no-plugin.gradle")]
        [TestCase("base-with-plugin.gradle")]
        public void Base_templates_written_by_earlier_package_versions_are_recognised(string name)
        {
            Assert.That(GradleTemplateDefaults.IsLegacyPackageBaseTemplate(Legacy(name)), Is.True);
            Assert.That(GradleTemplateDefaults.IsLegacyPackageBaseTemplate(Legacy(name).Replace("\n", "\r\n")), Is.True);
        }

        [Test]
        public void A_user_authored_or_unity_6_base_template_is_never_recognised_as_ours()
        {
            Assert.That(GradleTemplateDefaults.IsLegacyPackageBaseTemplate(Legacy("user-authored-base.gradle")), Is.False);
            Assert.That(GradleTemplateDefaults.IsLegacyPackageBaseTemplate(Fixture("6000.0.84f1", "baseProjectTemplate.gradle")), Is.False);
            Assert.That(GradleTemplateDefaults.IsLegacyPackageBaseTemplate(null), Is.False);
        }

        [TestCase("launcher-no-plugin.gradle")]
        [TestCase("launcher-with-plugin.gradle")]
        public void Launcher_templates_written_by_earlier_package_versions_are_recognised(string name)
        {
            Assert.That(GradleTemplateDefaults.IsLegacyPackageLauncherTemplate(Legacy(name)), Is.True);
        }

        [Test]
        public void Unity_default_launcher_templates_are_not_recognised_as_ours()
        {
            Assert.That(GradleTemplateDefaults.IsLegacyPackageLauncherTemplate(Fixture("2021.3.45f1", "launcherTemplate.gradle")), Is.False);
            Assert.That(GradleTemplateDefaults.IsLegacyPackageLauncherTemplate(Fixture("6000.0.84f1", "launcherTemplate.gradle")), Is.False);
        }

        [TestCase("7.4.2", false)]
        [TestCase("8.5.2", false)]
        [TestCase("8.6.0", true)]
        [TestCase("8.10.1", true)]
        [TestCase("9.0.0", true)]
        public void Bugsee_plugin_is_gated_on_the_pinned_agp_version(string agp, bool expected)
        {
            string root = "plugins {\n    id 'com.android.application' version '" + agp + "' apply false\n}\n";
            Assert.That(GradleTemplateDefaults.AgpMeetsBugseePlugin(root), Is.EqualTo(expected));
        }

        [Test]
        public void Unknown_agp_does_not_enable_the_plugin()
        {
            Assert.That(GradleTemplateDefaults.AgpMeetsBugseePlugin("plugins {\n}\n"), Is.False);
            Assert.That(GradleTemplateDefaults.AgpMeetsBugseePlugin(Fixture("6000.0.84f1", "baseProjectTemplate.gradle")), Is.True);
            Assert.That(GradleTemplateDefaults.AgpMeetsBugseePlugin(Fixture("2021.3.45f1", "baseProjectTemplate.gradle")), Is.False);
        }

        [Test]
        public void Package_editor_code_does_not_pin_an_agp_version_or_write_a_template_by_hand()
        {
            string editor = File.ReadAllText(Path.GetFullPath(Path.Combine(
                TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "..", "Editor", "BugseeAndroidGradleSetup.cs")));

            Assert.That(editor, Does.Not.Contain("com.android.application' version"));
            Assert.That(editor, Does.Not.Contain("StringBuilder"));
            Assert.That(editor, Does.Not.Contain("File.Copy"));
            Assert.That(editor, Does.Contain("IPostGenerateGradleAndroidProject"));
        }
    }
}
