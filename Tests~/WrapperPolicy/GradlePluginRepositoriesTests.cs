using System.IO;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    /// <summary>
    /// The Bugsee Gradle plugin (and its marker artifact) is published to Maven Central; Gradle's default plugin
    /// repository is the plugin portal. A settings file that lists its own pluginManagement repositories without
    /// either cannot resolve <c>id 'com.bugsee.android.gradle'</c>, which fails the build.
    /// </summary>
    public class GradlePluginRepositoriesTests
    {
        static GradleMavenInjection.RepositoryStatus Ensure(string settings, out string updated) =>
            GradleMavenInjection.EnsurePluginRepositories(settings, out updated);

        [TestCase("Unity6000.0.84f1-default")]
        [TestCase("Unity6000.0.84f1-sample-templates")]
        [TestCase("Unity2022.3.62f3-default")]
        public void Unitys_settings_already_resolve_plugins_from_the_portal_and_maven_central(string set)
        {
            string settings = File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", set, "settings.gradle"));

            Assert.That(Ensure(settings, out string updated), Is.EqualTo(GradleMavenInjection.RepositoryStatus.AlreadyPresent));
            Assert.That(updated, Is.EqualTo(settings));
        }

        [Test]
        public void Settings_without_pluginManagement_use_the_portal_by_default()
        {
            string settings = "include ':launcher', ':unityLibrary'\n";

            Assert.That(Ensure(settings, out string updated), Is.EqualTo(GradleMavenInjection.RepositoryStatus.BlockNotFound));
            Assert.That(updated, Is.EqualTo(settings));
        }

        [Test]
        public void Only_google_in_the_plugin_repositories_gets_maven_central_once_in_groovy()
        {
            string settings = "pluginManagement {\n    repositories {\n        google()\n    }\n}\n\ninclude ':launcher'\n";

            Assert.That(Ensure(settings, out string once), Is.EqualTo(GradleMavenInjection.RepositoryStatus.Inserted));
            Assert.That(once, Does.Contain("    repositories {\n        mavenCentral() // bugsee:maven-repository\n        google()\n"));
            Assert.That(Ensure(once, out string twice), Is.EqualTo(GradleMavenInjection.RepositoryStatus.AlreadyPresent));
            Assert.That(twice, Is.EqualTo(once));
        }

        [Test]
        public void Only_google_in_the_plugin_repositories_gets_maven_central_in_kotlin()
        {
            string settings = "pluginManagement {\n    repositories {\n        google()\n    }\n}\n\ninclude(\":launcher\")\n";

            Assert.That(Ensure(settings, out string updated), Is.EqualTo(GradleMavenInjection.RepositoryStatus.Inserted));
            Assert.That(updated, Does.Contain("mavenCentral() // bugsee:maven-repository"));
        }

        [Test]
        public void A_portal_without_maven_central_is_enough()
        {
            string settings = "pluginManagement {\n    repositories {\n        gradlePluginPortal()\n        google()\n    }\n}\n";

            Assert.That(Ensure(settings, out string updated), Is.EqualTo(GradleMavenInjection.RepositoryStatus.AlreadyPresent));
            Assert.That(updated, Is.EqualTo(settings));
        }

        [Test]
        public void A_commented_out_portal_does_not_count()
        {
            string settings = "pluginManagement {\n    repositories {\n        // gradlePluginPortal()\n        google()\n    }\n}\n";

            Assert.That(Ensure(settings, out _), Is.EqualTo(GradleMavenInjection.RepositoryStatus.Inserted));
        }

        [Test]
        public void The_dependency_repositories_are_not_mistaken_for_the_plugin_repositories()
        {
            string settings =
                "pluginManagement {\n    repositories {\n        google()\n    }\n}\n" +
                "dependencyResolutionManagement {\n    repositories {\n        mavenCentral()\n    }\n}\n";

            Assert.That(Ensure(settings, out string updated), Is.EqualTo(GradleMavenInjection.RepositoryStatus.Inserted));
            Assert.That(updated.IndexOf("bugsee:maven-repository"), Is.LessThan(updated.IndexOf("dependencyResolutionManagement")));
        }
    }
}
