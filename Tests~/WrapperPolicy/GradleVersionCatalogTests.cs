using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    /// <summary>The AGP version through <c>gradle/libs.versions.toml</c> (reconstructed: Unity generates no version catalog).</summary>
    public class GradleVersionCatalogTests
    {
        const string Catalog =
            "[versions]\n" +
            "agp = \"8.7.2\" # Android Gradle plugin\n" +
            "kotlin = \"2.0.0\"\n" +
            "\n" +
            "[libraries]\n" +
            "android-gradle-plugin = { module = \"com.android.tools.build:gradle\", version.ref = \"agp\" }\n" +
            "okhttp = { module = \"com.squareup.okhttp3:okhttp\", version = \"4.12.0\" }\n" +
            "\n" +
            "[plugins]\n" +
            "android-application = { id = \"com.android.application\", version.ref = \"agp\" }\n" +
            "android-library = { id = \"com.android.library\", version.ref = \"agp\" }\n" +
            "kotlin-android = { id = \"org.jetbrains.kotlin.android\", version.ref = \"kotlin\" }\n";

        [Test]
        public void A_plugin_alias_resolves_through_a_version_ref()
        {
            string root = "plugins {\n    alias(libs.plugins.android.application) apply false\n    alias(libs.plugins.kotlin.android) apply false\n}\n";

            Assert.That(GradleTemplateDefaults.TryGetAgpVersion(root, Catalog, out int major, out int minor), Is.True);
            Assert.That((major, minor), Is.EqualTo((8, 7)));
            Assert.That(GradleTemplateDefaults.AgpMeetsBugseePlugin(root, Catalog), Is.True);
        }

        [Test]
        public void Without_the_catalog_an_alias_is_an_unknown_version()
        {
            string root = "plugins {\n    alias(libs.plugins.android.application) apply false\n}\n";

            Assert.That(GradleTemplateDefaults.TryGetAgpVersion(root, out _, out _), Is.False);
            Assert.That(GradleTemplateDefaults.TryGetAgpVersion(root, "[plugins]\nother = { id = \"x\", version = \"1\" }\n", out _, out _), Is.False);
        }

        [Test]
        public void A_literal_plugin_version_in_the_catalog_is_read()
        {
            string catalog = "[plugins]\nandroid-application = { id = \"com.android.application\", version = \"8.3.0\" }\n";
            string root = "plugins {\n    alias(libs.plugins.android.application) apply false\n}\n";

            Assert.That(GradleVersionCatalog.ResolveAgpVersion(root, catalog), Is.EqualTo("8.3.0"));
            Assert.That(GradleTemplateDefaults.AgpMeetsBugseePlugin(root, catalog), Is.False);
        }

        [Test]
        public void A_version_accessor_on_the_plugin_id_resolves()
        {
            string kotlin = "plugins {\n    id(\"com.android.application\") version libs.versions.agp.get() apply false\n}\n";
            string groovy = "plugins {\n    id 'com.android.application' version libs.versions.agp.get() apply false\n}\n";

            Assert.That(GradleVersionCatalog.ResolveAgpVersion(kotlin, Catalog), Is.EqualTo("8.7.2"));
            Assert.That(GradleVersionCatalog.ResolveAgpVersion(groovy, Catalog), Is.EqualTo("8.7.2"));
        }

        [Test]
        public void A_buildscript_classpath_library_resolves()
        {
            string kotlin = "buildscript {\n    dependencies {\n        classpath(libs.android.gradle.plugin)\n    }\n}\n";
            string groovy = "buildscript {\n    dependencies {\n        classpath libs.android.gradle.plugin\n    }\n}\n";

            Assert.That(GradleVersionCatalog.ResolveAgpVersion(kotlin, Catalog), Is.EqualTo("8.7.2"));
            Assert.That(GradleVersionCatalog.ResolveAgpVersion(groovy, Catalog), Is.EqualTo("8.7.2"));
        }

        [TestCase("android_application")]
        [TestCase("android.application")]
        [TestCase("android-application")]
        public void Catalog_key_separators_match_the_accessor_dots(string key)
        {
            string catalog = "[versions]\nagp = \"9.0.0\"\n[plugins]\n" + key + " = { id = \"com.android.application\", version.ref = \"agp\" }\n";

            Assert.That(GradleVersionCatalog.ResolveAgpVersion("alias(libs.plugins.android.application)", catalog), Is.EqualTo("9.0.0"));
        }

        [Test]
        public void A_rich_version_takes_its_first_string_and_junk_does_not_throw()
        {
            string catalog = "[versions]\nagp = { strictly = \"8.6.1\" }\n[plugins]\nandroid-application = { id = \"com.android.application\", version.ref = \"agp\" }\nbroken\n= x\n";

            Assert.That(GradleVersionCatalog.ResolveAgpVersion("alias(libs.plugins.android.application)", catalog), Is.EqualTo("8.6.1"));
            Assert.That(GradleVersionCatalog.ResolveAgpVersion(null, catalog), Is.Null);
            Assert.That(GradleVersionCatalog.ResolveAgpVersion("alias(libs.plugins.android.application)", ""), Is.Null);
        }

        [Test]
        public void The_catalog_plugin_and_gate_work_together_with_the_Kotlin_launcher_injection()
        {
            string root = "plugins {\n    alias(libs.plugins.android.application) apply false\n}\n";
            string launcher = "plugins {\n    alias(libs.plugins.android.application)\n}\n\nandroid {\n}\n";

            Assert.That(GradleTemplateDefaults.AgpMeetsBugseePlugin(root, Catalog), Is.True);
            var patch = GradlePluginInjection.Apply(
                root, GradleMavenInjection.GradleDsl.Kotlin, launcher, GradleMavenInjection.GradleDsl.Kotlin, "4.0.8");

            Assert.That(patch.Launcher, Does.Contain("    id(\"com.bugsee.android.gradle\") version \"4.0.8\" // bugsee:gradle-plugin\n"));
        }
    }
}
