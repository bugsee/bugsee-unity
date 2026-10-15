using System;
using System.IO;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    /// <summary>
    /// The Editor scripts are compiled only by Unity, so these check, in source text and nothing more, that the Editor
    /// code calls the policy classes whose behaviour is tested elsewhere (BugseeCliPolicy, AndroidUploadIdentity,
    /// AndroidBuildUuid). They prove wiring, not behaviour: real builds do that (see Documentation~/index.md).
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
        public void The_runner_starts_the_resolved_binary_with_an_argument_list()
        {
            var source = Source("Editor/BugseeCliRunner.cs");

            Assert.That(Body(source, "public static string CliPath"), Does.Contain("BugseeCliLocator.Resolve()"));
            Assert.That(Body(source, "public static int Run("), Does.Contain("BugseeCliLocator.ReportIfMissing()"));
            Assert.That(Body(source, "public static int Run("), Does.Contain("psi.ArgumentList.Add("));
            Assert.That(source, Does.Not.Contain("Arguments = string.Join"));
        }

        [Test]
        public void The_download_is_verified_and_installed_through_the_policy_steps()
        {
            var source = Source("Editor/BugseeCliLocator.cs");
            var fetch = Body(source, "static string FetchUnchecked(");

            Assert.That(fetch, Does.Contain("StartsWith(\"https://\""));
            Assert.That(fetch, Does.Contain("BugseeCliPolicy.KnownSha256("));
            Assert.That(fetch, Does.Contain("SHA-256 mismatch"));
            Assert.That(Body(source, "static string Install("), Does.Contain("File.Move(temp, finalBinary)"));
            Assert.That(source, Does.Contain("Fetch = version => Fetch(settings, version)"));
            Assert.That(source, Does.Contain("public void OnPreprocessBuild(BuildReport report) => BugseeCliLocator.Reset();"));
        }

        [Test]
        public void The_symbol_upload_keys_mapping_and_native_uploads_by_the_id_read_back_from_the_build()
        {
            var body = Body(Source("Editor/BugseeSymbolUpload.cs"), "static void UploadAndroidSymbols(");

            Assert.That(body, Does.Contain("BugseeBuildIdentity.ResolveAndroidUploadIdentity(report)"));
            Assert.That(body, Does.Contain("AndroidUploadIdentity.MappingArguments(identity.Uuid, icon)"));
            Assert.That(body, Does.Contain("UploadSymbolsZipOrElf(report, roots, version, build, identity.Uuid"));

            var elf = Body(Source("Editor/BugseeSymbolUpload.cs"), "static void UploadSymbolsZipOrElf(");
            Assert.That(elf, Does.Contain("AndroidUploadIdentity.ElfArguments(bestZip, version, build, buildUuid)"));
            Assert.That(elf, Does.Contain("AndroidUploadIdentity.ElfArguments(dir, version, build, buildUuid)"));
        }

        [Test]
        public void The_identity_is_read_from_the_apk_asset_before_the_synthesized_id()
        {
            var source = Source("Editor/BugseeBuildIdentity.cs");
            var body = Body(source, "public static UploadIdentity ResolveAndroidUploadIdentity(");

            Assert.That(body, Does.Contain("ReadAppAssetIds(report?.summary.outputPath)"));
            Assert.That(source, Does.Not.Contain("build-id.txt"), "Gradle's id file can belong to an earlier build");
            Assert.That(body, Does.Contain("AndroidBuildUuid.Synthesize("));
            Assert.That(body, Does.Contain("GradleTemplateDefaults.ReadGradleVersion("));
        }
    }
}
