using System;
using System.IO;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    /// <summary>
    /// The Editor scripts are compiled only by Unity, so the wiring the policy classes depend on is checked in source:
    /// the upload resolves its CLI through the locator, and a mapping is keyed by the SDK id when no build id asset
    /// runs (Unity 2021.3 / 2022.3).
    /// </summary>
    public class OldUnitySymbolUploadSourceTests
    {
        static string Source(string relative)
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, "package.json")))
                    return File.ReadAllText(Path.Combine(dir, relative));
                dir = Directory.GetParent(dir)?.FullName;
            }

            throw new InvalidOperationException("Could not locate repo root (package.json).");
        }

        static string Body(string source, string signature)
        {
            int start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0), "Missing: " + signature);
            int brace = source.IndexOf('{', start);
            int depth = 0;
            for (int i = brace; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(start, i - start + 1);
            }

            Assert.Fail("Unclosed body: " + signature);
            return null;
        }

        [Test]
        public void The_runner_starts_the_resolved_binary_and_never_the_bare_name()
        {
            var source = Source("Editor/BugseeCliRunner.cs");

            Assert.That(Body(source, "public static string CliPath"), Does.Contain("BugseeCliLocator.Resolve()"));
            Assert.That(Body(source, "public static int Run("), Does.Contain("BugseeCliLocator.ReportIfMissing()"));
            Assert.That(source, Does.Not.Contain("?? \"bugsee-cli\""));
        }

        [Test]
        public void The_locator_downloads_only_over_https_and_checks_the_published_checksum()
        {
            var body = Body(Source("Editor/BugseeCliLocator.cs"), "static string Download(");

            Assert.That(body, Does.Contain("StartsWith(\"https://\""));
            Assert.That(body, Does.Contain("BugseeCliPolicy.ParseSha256Sidecar("));
            Assert.That(body, Does.Contain("Sha256Hex(archive)"));
            Assert.That(body, Does.Contain("SHA-256 mismatch"));
        }

        [Test]
        public void The_locator_forgets_a_failed_lookup_when_the_next_build_starts()
        {
            var source = Source("Editor/BugseeCliLocator.cs");

            Assert.That(source, Does.Contain("class BugseeCliResolutionReset : IPreprocessBuildWithReport"));
            Assert.That(source, Does.Contain("public void OnPreprocessBuild(BuildReport report) => BugseeCliLocator.Reset();"));
        }

        [Test]
        public void The_mapping_upload_carries_the_sdk_build_id_unless_the_gradle_plugin_runs()
        {
            var body = Body(Source("Editor/BugseeSymbolUpload.cs"), "static void UploadAndroidSymbols(");

            Assert.That(body, Does.Contain("if (!BugseeAndroidGradleSetup.BuildIdAssetInjected)"));
            Assert.That(body, Does.Contain("mappingExtra.Add(\"--uuid\")"));
            Assert.That(body, Does.Contain("BugseeBuildIdentity.ResolveBuildUuid(report)"));
            Assert.That(body, Does.Contain("mappingExtra.Add(\"--icon\")"), "the plugin attaches the launcher icon to the mapping");
            Assert.That(Body(Source("Editor/BugseeSymbolUpload.cs"), "static string FindLauncherIcon("), Does.Contain("AndroidIconPicker.Pick("));
        }

        [Test]
        public void An_empty_mapping_is_never_uploaded()
        {
            var source = Source("Editor/BugseeSymbolUpload.cs");

            Assert.That(Body(source, "static void UploadIfFound("), Does.Contain("if (IsEmptyFile(file))"));
            Assert.That(Body(source, "static bool IsEmptyFile("), Does.Contain("new FileInfo(path).Length == 0"));
        }

        [Test]
        public void The_android_build_id_is_the_one_the_sdk_derives_from_the_app_as_built()
        {
            var body = Body(Source("Editor/BugseeBuildIdentity.cs"), "public static string ResolveBuildUuid(");

            Assert.That(body, Does.Contain("AndroidBuildUuid.Synthesize("));
            Assert.That(body, Does.Contain("names.BundleVersion"));
            Assert.That(body, Does.Contain("names.VersionCode"));
        }

        [Test]
        public void The_gradle_setup_records_whether_the_build_id_asset_was_added_for_this_build_only()
        {
            var source = Source("Editor/BugseeAndroidGradleSetup.cs");

            Assert.That(Body(source, "public void OnPreprocessBuild("), Does.Contain("BuildIdAssetInjected = false"));
            Assert.That(Body(source, "static void PatchGeneratedProject("), Does.Contain("BuildIdAssetInjected = pluginPresent || blockPresent"));
        }
    }
}
