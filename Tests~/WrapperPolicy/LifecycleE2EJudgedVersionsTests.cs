using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    // The sample's lifecycle device scenarios only judge (record failures) on the SDK versions listed in
    // LifecycleE2E.JudgedSdkVersions; elsewhere they log INFO. This keeps a pin bump from silently skipping them.
    public class LifecycleE2EJudgedVersionsTests
    {
        [Test]
        public void The_lifecycle_e2e_judges_the_pinned_android_sdk()
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "package.json")))
                dir = Directory.GetParent(dir)?.FullName;
            Assert.That(dir, Is.Not.Null.And.Not.Empty, "repo root");

            var pinLine = File.ReadAllLines(Path.Combine(dir, "Tools~", "versions.env"))
                .FirstOrDefault(l => l.StartsWith("ANDROID_SDK_VERSION=", StringComparison.Ordinal));
            Assert.That(pinLine, Is.Not.Null, "ANDROID_SDK_VERSION not found in Tools~/versions.env");
            var pinned = pinLine.Substring(pinLine.IndexOf('=') + 1).Trim('"');

            var source = File.ReadAllText(Path.Combine(dir, "Samples~", "BugseeMiniGame", "Assets", "Scripts", "BugseeHarness", "LifecycleE2E.cs"));
            var m = Regex.Match(source, @"JudgedSdkVersions\s*=\s*\{([^}]*)\}");
            Assert.That(m.Success, Is.True, "LifecycleE2E.JudgedSdkVersions not found");
            var judged = Regex.Matches(m.Groups[1].Value, "\"([^\"]+)\"").Cast<Match>().Select(x => x.Groups[1].Value).ToArray();

            Assert.That(judged, Does.Contain(pinned),
                "Android SDK " + pinned + " is pinned but LifecycleE2E would not judge it (the lifecycle device checks would only log). "
                + "Re-run the lifecycle scenarios on a device build with this SDK, confirm the Launch/Relaunch/Stop expectations, then add it to JudgedSdkVersions.");
        }
    }
}
