using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    /// <summary>
    /// The wrapper version is written in several places. They are generated from one file;
    /// this fails when one is edited by hand and the others are not.
    /// </summary>
    [TestFixture]
    public class WrapperIdentityDriftTests
    {
        static string RepoFile(string relative)
        {
            var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "package.json"))) dir = dir.Parent;
            Assert.That(dir, Is.Not.Null, "repo root (package.json) not found");
            return Path.Combine(dir.FullName, relative);
        }

        static string Capture(string file, string pattern)
        {
            var match = Regex.Match(File.ReadAllText(RepoFile(file)), pattern);
            Assert.That(match.Success, Is.True, file + " does not match " + pattern);
            return match.Groups[1].Value;
        }

        [Test]
        public void Package_version_is_the_same_everywhere()
        {
            var source = Capture("Tools~/versions.env", "BUGSEE_PACKAGE_VERSION=\"([^\"]+)\"");
            Assert.That(Capture("package.json", "\"version\":\\s*\"([^\"]+)\""), Is.EqualTo(source));
            Assert.That(Capture("Runtime/BugseePackageVersion.cs", "Version = \"([^\"]+)\""), Is.EqualTo(source));
            Assert.That(Capture("Plugins/Android/UnityWrapperIdentity.java", "VERSION = \"([^\"]+)\""), Is.EqualTo(source),
                "early Android registration would report a different wrapper version than C#");
            // Both files are written by one run of the generator, so the build must agree too.
            Assert.That(Capture("Plugins/Android/UnityWrapperIdentity.java", "BUILD = \"([^\"]*)\""),
                Is.EqualTo(Capture("Runtime/BugseePackageVersion.cs", "Build = \"([^\"]*)\"")));
        }

        // Lifecycle events must reach C# through one path. Registering the managed listener with
        // Bugsee.setLifecycleEventsListener as well would deliver events twice, or lose some in the gap.
        [Test]
        public void Lifecycle_events_reach_managed_code_only_through_the_wrapper()
        {
            var bridge = File.ReadAllText(RepoFile("Runtime/Platform/Android/AndroidBridge.cs"));
            Assert.That(bridge, Does.Contain("attachLifecycleListener"));
            Assert.That(bridge, Does.Not.Contain("CallStatic(\"setLifecycleEventsListener\""), "the call, not a mention in a comment");
            var rules = File.ReadAllText(RepoFile("Plugins/Android/bugsee-unity-wrapper.proguard"));
            Assert.That(rules, Does.Contain("attachLifecycleListener"));
        }

        [Test]
        public void Native_sdk_pins_are_the_same_everywhere()
        {
            var android = Capture("Tools~/versions.env", "ANDROID_SDK_VERSION=\"([^\"]+)\"");
            Assert.That(Capture("Runtime/BugseePackageVersion.cs", "AndroidSdkVersion = \"([^\"]+)\""), Is.EqualTo(android));
            // The Maven dependencies are declared from BugseePackageVersion at build time (no XML any more), so
            // both artifacts take the one version above.
            var injector = File.ReadAllText(RepoFile("Editor/BugseeAndroidMavenDependencies.cs"));
            Assert.That(injector, Does.Contain("BugseePackageVersion.AndroidSdkVersion"));
            Assert.That(injector, Does.Contain("BugseePackageVersion.AndroidMavenArtifact"));
            Assert.That(injector, Does.Contain("BugseePackageVersion.AndroidNdkArtifact"),
                "the NDK artifact must be declared with the core one");
            Assert.That(File.ReadAllText(RepoFile("Editor/BugseeAndroidGradleSetup.cs")),
                Does.Contain("SdkVersion = BugseePackageVersion.AndroidSdkVersion;"));
            Assert.That(Capture("Tests~/Android/wrapper/build.gradle", "bugsee-android:([^\"]+)\""), Is.EqualTo(android));

            var ios = Capture("Tools~/versions.env", "IOS_SDK_VERSION=\"([^\"]+)\"");
            Assert.That(Capture("Runtime/BugseePackageVersion.cs", "IosSdkVersion = \"([^\"]+)\""), Is.EqualTo(ios));
            Assert.That(Capture("Tests~/iOS/run-tests.sh", "Bugsee-([0-9][^.]*\\.[0-9.]+(?:-[a-z0-9]+)?)\\.zip\""), Is.EqualTo(ios));
        }
    }
}
