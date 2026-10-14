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
            // The zip version in Tests~/iOS/run-tests.sh is checked by the listed pins below, which read dotted prereleases.
        }

        // update-native-sdks.sh rewrites exactly these places from versions.env. Hand-edited or forgotten ones fail here.
        static System.Collections.Generic.List<string[]> PinEntries()
        {
            var entries = new System.Collections.Generic.List<string[]>();
            foreach (var raw in File.ReadAllLines(RepoFile("Tools~/scripts/native-pins.txt")))
            {
                var line = raw.TrimEnd('\r');
                if (line.Trim().Length == 0 || line.StartsWith("#")) continue;
                entries.Add(line.Split(new[] { '|' }, 3));
            }
            return entries;
        }

        // CHANGELOG: only the first (current) version section; released sections keep their history.
        static void PinRegion(string file, string text, out int start, out int length)
        {
            start = 0;
            length = text.Length;
            if (file != "CHANGELOG.md") return;
            var first = text.IndexOf("\n## [", System.StringComparison.Ordinal);
            if (first < 0) return;
            var next = text.IndexOf("\n## [", first + 1, System.StringComparison.Ordinal);
            start = first;
            length = (next < 0 ? text.Length : next) - first;
        }

        [Test]
        public void Listed_pins_in_docs_tests_and_constants_match_versions_env()
        {
            var want = new System.Collections.Generic.Dictionary<string, string>
            {
                ["android"] = Capture("Tools~/versions.env", "ANDROID_SDK_VERSION=\"([^\"]+)\""),
                ["ios"] = Capture("Tools~/versions.env", "IOS_SDK_VERSION=\"([^\"]+)\""),
                ["gradleplugin"] = Capture("Tools~/versions.env", "ANDROID_GRADLE_PLUGIN_VERSION=\"([^\"]+)\""),
            };
            var drift = new System.Collections.Generic.List<string>();
            var entries = PinEntries();
            Assert.That(entries, Is.Not.Empty);
            foreach (var parts in entries)
            {
                Assert.That(want.ContainsKey(parts[0]), Is.True, "unknown pin kind in: " + string.Join("|", parts));
                var text = File.ReadAllText(RepoFile(parts[1]));
                PinRegion(parts[1], text, out var start, out var length);
                var matches = Regex.Matches(text.Substring(start, length), parts[2]);
                // A new release heading leaves the CHANGELOG's first section without pins: nothing to check there.
                if (parts[1] != "CHANGELOG.md")
                    Assert.That(matches.Count, Is.GreaterThan(0), parts[1] + " has no match for " + parts[2]);
                foreach (Match m in matches)
                    if (m.Groups[1].Value != want[parts[0]])
                        drift.Add(parts[1] + ": " + m.Groups[1].Value + ", versions.env says " + want[parts[0]]);
            }
            Assert.That(drift, Is.Empty, "run Tools~/scripts/update-native-sdks.sh");
        }

        [Test]
        public void Changelog_pins_read_only_the_first_section()
        {
            const string Log = "# Changelog\n\n## [Unreleased]\n\n- nothing pinned yet\n\n## [0.1.0]\n\n- `com.bugsee:bugsee-android:7.0.0`\n";
            PinRegion("CHANGELOG.md", Log, out var start, out var length);
            var region = Log.Substring(start, length);
            Assert.That(region, Does.Contain("Unreleased"));
            Assert.That(region, Does.Not.Contain("7.0.0"), "released sections keep their history");
            PinRegion("README.md", Log, out start, out length);
            Assert.That(length, Is.EqualTo(Log.Length), "only the CHANGELOG is scoped");
        }

        // A dotted prerelease must be read whole. If the pattern stopped at the first dot, rewriting 7.5.0-rc.1
        // would leave ".1" behind and the script would append another one on every run.
        [Test]
        public void Pin_patterns_read_a_dotted_prerelease_whole()
        {
            const string Dotted = "9.9.9-rc.1";
            foreach (var parts in PinEntries())
            {
                var text = File.ReadAllText(RepoFile(parts[1]));
                PinRegion(parts[1], text, out var start, out var length);
                var region = text.Substring(start, length);
                var rewritten = Regex.Replace(region, parts[2], m =>
                    m.Value.Substring(0, m.Groups[1].Index - m.Index) + Dotted +
                    m.Value.Substring(m.Groups[1].Index - m.Index + m.Groups[1].Length));
                foreach (Match m in Regex.Matches(rewritten, parts[2]))
                    Assert.That(m.Groups[1].Value, Is.EqualTo(Dotted), parts[1] + " " + parts[2]);
            }
        }
    }
}
