using System;
using System.IO;
using System.Security.Cryptography;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class BugseeCliExitAndDownloadTests
    {
        [Test]
        public void Exit_zero_has_no_warning()
        {
            Assert.That(BugseeCliPolicy.DescribeExit(0), Is.Null);
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(10)]
        [TestCase(11)]
        [TestCase(20)]
        [TestCase(21)]
        [TestCase(30)]
        [TestCase(31)]
        [TestCase(40)]
        [TestCase(51)]
        [TestCase(101)]
        [TestCase(-1)]
        public void Other_nonzero_exits_get_the_generic_message_only(int code)
        {
            string text = BugseeCliPolicy.DescribeExit(code);
            Assert.That(text, Does.Contain("upload failed (exit " + code + ")"));
            Assert.That(text, Does.Contain("build continues"));
            Assert.That(text, Does.Not.Contain("server-side"));
        }

        [Test]
        public void Exit_50_adds_the_server_state_note()
        {
            string text = BugseeCliPolicy.DescribeExit(50);
            Assert.That(text, Does.Contain("upload failed (exit 50)"));
            Assert.That(text, Does.Contain("build continues"));
            Assert.That(text, Does.Contain("may have been applied server-side"));
            Assert.That(text, Does.Contain("dashboard"));
            Assert.That(text, Does.Contain("do not re-run blindly"));
        }

        [TestCase("https://github.com/bugsee/bugsee-cli/releases/download/v0.9.0/x.tar.xz", true)]
        [TestCase("https://127.0.0.1:9/x", true)]
        [TestCase("HTTPS://example.com/x", true)]
        [TestCase("http://example.com/x", false)]
        [TestCase("ftp://example.com/x", false)]
        [TestCase("file:///tmp/x", false)]
        [TestCase("/relative/x", false)]
        [TestCase("example.com/x", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        public void IsAllowedDownloadUrl_accepts_only_https(string url, bool expected)
        {
            Assert.That(BugseeCliPolicy.IsAllowedDownloadUrl(url), Is.EqualTo(expected));
        }

        [Test]
        public void IsAllowedDownloadUrl_covers_the_base_url_override()
        {
            Assert.That(BugseeCliPolicy.IsAllowedDownloadUrl(
                BugseeCliPolicy.ArchiveUrl("https://127.0.0.1:9", "0.9.0", "x86_64-apple-darwin")), Is.True);
            Assert.That(BugseeCliPolicy.IsAllowedDownloadUrl(
                BugseeCliPolicy.ArchiveUrl("http://mirror.local/", "0.9.0", "x86_64-apple-darwin")), Is.False);
        }

        static string WriteTemp(out string hex)
        {
            string path = Path.Combine(Path.GetTempPath(), "bugsee-sha-" + Guid.NewGuid().ToString("N") + ".bin");
            byte[] data = { 1, 2, 3, 4, 5, 250 };
            File.WriteAllBytes(path, data);
            using (var sha = SHA256.Create())
            {
                hex = BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
            }

            return path;
        }

        [Test]
        public void VerifyArchiveSha256_accepts_the_right_hash_in_either_case()
        {
            string path = WriteTemp(out string hex);
            try
            {
                Assert.DoesNotThrow(() => BugseeCliPolicy.VerifyArchiveSha256(path, hex));
                Assert.DoesNotThrow(() => BugseeCliPolicy.VerifyArchiveSha256(path, hex.ToUpperInvariant()));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void VerifyArchiveSha256_throws_on_a_wrong_hash_naming_both()
        {
            string path = WriteTemp(out string hex);
            try
            {
                string wrong = new string('0', 64);
                var ex = Assert.Throws<IOException>(() => BugseeCliPolicy.VerifyArchiveSha256(path, wrong));
                Assert.That(ex.Message, Does.Contain("SHA-256 mismatch for " + Path.GetFileName(path)));
                Assert.That(ex.Message, Does.Contain("expected " + wrong));
                Assert.That(ex.Message, Does.Contain("got " + hex));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestCase(null)]
        [TestCase("")]
        public void VerifyArchiveSha256_never_matches_a_missing_expected_hash(string expected)
        {
            string path = WriteTemp(out _);
            try
            {
                Assert.Throws<IOException>(() => BugseeCliPolicy.VerifyArchiveSha256(path, expected));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void VerifyArchiveSha256_throws_for_a_missing_file()
        {
            string path = Path.Combine(Path.GetTempPath(), "bugsee-sha-missing-" + Guid.NewGuid().ToString("N"));
            Assert.Throws<FileNotFoundException>(() => BugseeCliPolicy.VerifyArchiveSha256(path, new string('0', 64)));
        }
    }
}
