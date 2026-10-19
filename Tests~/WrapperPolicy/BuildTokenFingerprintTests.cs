using System;
using System.IO;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    // The build records a fingerprint of BUGSEE_APP_TOKEN, Launch compares it with its token.
    public class BuildTokenFingerprintTests
    {
        const string TokenA = "11111111-2222-3333-4444-555555555555";
        const string TokenB = "99999999-8888-7777-6666-555555555555";

        [Test]
        public void Fingerprint_is_sixteen_hex_characters_of_a_sha256_prefix()
        {
            // sha256("abc") = ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad
            Assert.That(BuildTokenFingerprint.Compute("abc"), Is.EqualTo("ba7816bf8f01cfea"));
        }

        [Test]
        public void Fingerprint_does_not_contain_the_token_or_any_part_of_it()
        {
            var f = BuildTokenFingerprint.Compute(TokenA);
            Assert.That(f, Has.Length.EqualTo(16));
            Assert.That(f, Does.Match("^[0-9a-f]{16}$"));
            foreach (var part in TokenA.Split('-'))
                Assert.That(f, Does.Not.Contain(part));
        }

        [Test]
        public void Whitespace_around_the_token_does_not_change_it()
        {
            Assert.That(BuildTokenFingerprint.Compute("  " + TokenA + "\n"), Is.EqualTo(BuildTokenFingerprint.Compute(TokenA)));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void A_blank_token_has_no_fingerprint(string token)
        {
            Assert.That(BuildTokenFingerprint.Compute(token), Is.Null);
        }

        [Test]
        public void Equal_tokens_match_even_when_the_asset_is_padded_or_upper_case()
        {
            var f = BuildTokenFingerprint.Compute(TokenA);
            Assert.That(BuildTokenFingerprint.Check(f, TokenA), Is.EqualTo(BuildTokenFingerprint.Verdict.Match));
            Assert.That(BuildTokenFingerprint.Check(f + "\r\n", " " + TokenA), Is.EqualTo(BuildTokenFingerprint.Verdict.Match));
            Assert.That(BuildTokenFingerprint.Check(f.ToUpperInvariant(), TokenA), Is.EqualTo(BuildTokenFingerprint.Verdict.Match));
        }

        [Test]
        public void Different_tokens_mismatch()
        {
            Assert.That(BuildTokenFingerprint.Check(BuildTokenFingerprint.Compute(TokenA), TokenB), Is.EqualTo(BuildTokenFingerprint.Verdict.Mismatch));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("  \n")]
        public void No_recorded_fingerprint_is_silent(string recorded)
        {
            Assert.That(BuildTokenFingerprint.Check(recorded, TokenA), Is.EqualTo(BuildTokenFingerprint.Verdict.NotApplicable));
        }

        [Test]
        public void No_launch_token_is_silent()
        {
            Assert.That(BuildTokenFingerprint.Check(BuildTokenFingerprint.Compute(TokenA), null), Is.EqualTo(BuildTokenFingerprint.Verdict.NotApplicable));
        }

        [Test]
        public void The_warning_names_both_fingerprints_and_neither_token()
        {
            var recorded = BuildTokenFingerprint.Compute(TokenA);
            var message = BuildTokenFingerprint.MismatchMessage(recorded, TokenB);
            Assert.That(message, Does.Contain(recorded));
            Assert.That(message, Does.Contain(BuildTokenFingerprint.Compute(TokenB)));
            Assert.That(message, Does.Not.Contain(TokenA).And.Not.Contain(TokenB));
        }

        [Test]
        public void Launch_checks_the_token_and_the_editor_step_runs_before_the_player_is_built()
        {
            var root = RepoRoot();
            var bugsee = File.ReadAllText(Path.Combine(root, "Runtime/Bugsee.cs"));
            Assert.That(bugsee, Does.Contain("BuildTokenCheck.OnLaunch(appToken);"));

            var editor = File.ReadAllText(Path.Combine(root, "Editor/BugseeBuildTokenFingerprint.cs"));
            // Written at preprocess (a file written after the build is not in the player), removed after it.
            Assert.That(editor, Does.Contain("IPreprocessBuildWithReport"));
            Assert.That(editor, Does.Contain("IPostprocessBuildWithReport"));
            Assert.That(editor, Does.Contain("BuildTokenFingerprint.Compute(BugseeCliRunner.AppToken)"));
            Assert.That(editor, Does.Not.Contain("WriteAllText(AssetPath, BugseeCliRunner.AppToken"));
            Assert.That(editor, Does.Contain("BUGSEE_NO_SYMBOL_UPLOAD"));
            // Opt-out, and Android only: iOS symbols go up from the Xcode Run Script with the Xcode environment's token.
            Assert.That(editor, Does.Contain("BUGSEE_NO_TOKEN_CHECK"));
            Assert.That(editor, Does.Contain("report.summary.platform != BuildTarget.Android"));

            var check = File.ReadAllText(Path.Combine(root, "Runtime/Internal/BuildTokenCheck.cs"));
            Assert.That(check, Does.Contain("Resources.Load<TextAsset>(BuildTokenFingerprint.ResourceName)"));
            Assert.That(check, Does.Contain("MainThreadDispatcher.Run"));
        }

        [Test]
        public void The_warning_says_the_apps_may_differ_and_names_the_opt_out()
        {
            var message = BuildTokenFingerprint.MismatchMessage(BuildTokenFingerprint.Compute(TokenA), TokenB);
            Assert.That(message, Does.Contain("may not match"));
            Assert.That(message, Does.Contain("BUGSEE_NO_TOKEN_CHECK"));
        }

        static string RepoRoot()
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, "package.json")))
                    return dir;
                dir = Directory.GetParent(dir)?.FullName;
            }

            throw new InvalidOperationException("Could not locate repo root (package.json).");
        }
    }
}
