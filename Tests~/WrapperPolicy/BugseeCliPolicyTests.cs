using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class BugseeCliPolicyTests
    {
        // A fake host: the files that exist, the version each reports, and what a download produces.
        sealed class Host
        {
            public readonly Dictionary<string, string> Files = new Dictionary<string, string>(); // path -> version ("" = does not run)
            public readonly List<string> Downloads = new List<string>();
            public readonly List<string> Installs = new List<string>();
            public readonly List<string> Discards = new List<string>();
            public Func<string, string> OnFetch;
            public Func<string, string, string, string> OnInstall;

            public CliProbe Probe() => new CliProbe
            {
                FileExists = Files.ContainsKey,
                QueryVersion = p => Files.TryGetValue(p, out var v) && v != "" ? v : null,
                Fetch = version =>
                {
                    Downloads.Add(version);
                    return OnFetch?.Invoke(version);
                },
                Install = (staged, dir, version) =>
                {
                    Installs.Add(dir);
                    return OnInstall?.Invoke(staged, dir, version);
                },
                Discard = staged => Discards.Add(staged),
            };
        }

        static CliSettings Mac(string path = "/usr/bin:/bin", bool download = true) => new CliSettings
        {
            PathEnv = path,
            Home = "/Users/dev",
            Triple = "aarch64-apple-darwin",
            DownloadAllowed = download,
            CacheDirectories = { "/Users/dev/Library/Caches/Bugsee/cli", "/proj/Library/Bugsee/cli" },
        };

        [TestCase("macos", "Arm64", "aarch64-apple-darwin")]
        [TestCase("macos", "X64", "x86_64-apple-darwin")]
        [TestCase("Linux", "x86_64", "x86_64-unknown-linux-gnu")]
        [TestCase("linux", "aarch64", "aarch64-unknown-linux-gnu")]
        [TestCase("windows", "X64", "x86_64-pc-windows-msvc")]
        [TestCase("windows", "Arm64", null)]
        [TestCase("linux", "X86", null)]
        [TestCase("", "X64", null)]
        public void Host_triple_names_the_published_release(string os, string arch, string expected)
        {
            Assert.That(BugseeCliPolicy.HostTriple(os, arch), Is.EqualTo(expected));
        }

        [Test]
        public void Archives_are_zip_on_windows_and_tar_xz_elsewhere()
        {
            Assert.That(BugseeCliPolicy.ArchiveUrl(null, "0.8.1", "aarch64-apple-darwin"),
                Is.EqualTo("https://download.bugsee.com/cli/v0.8.1/bugsee-cli-aarch64-apple-darwin.tar.xz"));
            Assert.That(BugseeCliPolicy.ArchiveUrl("https://mirror.example/cli/", "0.8.1", "x86_64-pc-windows-msvc"),
                Is.EqualTo("https://mirror.example/cli/v0.8.1/bugsee-cli-x86_64-pc-windows-msvc.zip"));
            Assert.That(BugseeCliPolicy.ChecksumUrl("https://x/y.tar.xz"), Is.EqualTo("https://x/y.tar.xz.sha256"));
        }

        [Test]
        public void Checksum_sidecar_is_read_in_binary_and_text_mode_and_malformed_ones_are_rejected()
        {
            string hash = new string('a', 32) + new string('B', 32);
            Assert.That(BugseeCliPolicy.ParseSha256Sidecar(hash + " *bugsee-cli-x.tar.xz\n"), Is.EqualTo(hash.ToLowerInvariant()));
            Assert.That(BugseeCliPolicy.ParseSha256Sidecar(hash + "  bugsee-cli-x.tar.xz"), Is.EqualTo(hash.ToLowerInvariant()));
            Assert.That(BugseeCliPolicy.ParseSha256Sidecar(hash), Is.EqualTo(hash.ToLowerInvariant()));
            Assert.That(BugseeCliPolicy.ParseSha256Sidecar(""), Is.Null);
            Assert.That(BugseeCliPolicy.ParseSha256Sidecar("<html>Not found</html>"), Is.Null);
            Assert.That(BugseeCliPolicy.ParseSha256Sidecar(new string('g', 64)), Is.Null);
        }

        [TestCase("bugsee-cli 0.8.1\n", "0.8.1")]
        [TestCase("bugsee-cli 0.9.0-rc.1", "0.9.0-rc.1")]
        [TestCase("", null)]
        [TestCase("error: nope", null)]
        public void Version_is_read_from_the_cli_banner(string output, string expected)
        {
            Assert.That(BugseeCliPolicy.ParseVersionOutput(output), Is.EqualTo(expected));
        }

        [TestCase("0.8.1", "0.8.0", true)]
        [TestCase("0.8.0", "0.8.0", true)]
        [TestCase("0.7.13", "0.8.0", false)]
        [TestCase("0.8.0", "0.8.1", false)]
        [TestCase("0.8.1", "0.8.1", true)]
        [TestCase("0.8.1-rc.1", "0.8.1", true)]
        [TestCase("0.8.0-rc.1", "0.8.1", false)]
        [TestCase("0.9.0", "0.8.1", true)]
        [TestCase("1.0.0", "0.8.1", true)]
        [TestCase("0.8", "0.8.1", false)]
        [TestCase("garbage", "0.8.1", false)]
        [TestCase("", "0.8.1", false)]
        [TestCase(null, "0.8.1", false)]
        [TestCase("0.10.0", "0.8.1", true)]
        [TestCase("1.0", "0.8.1", true)]
        [TestCase("0.8.0-rc.1", "0.8.0", true)]
        public void Version_comparison_is_numeric(string version, string min, bool expected)
        {
            Assert.That(BugseeCliPolicy.VersionAtLeast(version, min), Is.EqualTo(expected));
        }

        [Test]
        public void Search_order_is_path_then_the_install_directories_without_duplicates()
        {
            var s = Mac("/usr/bin:/opt/homebrew/bin/:/usr/bin");

            var dirs = BugseeCliPolicy.SearchDirectories(s);

            Assert.That(dirs.Select(d => d.Key), Is.EqualTo(new[]
            {
                "/usr/bin", "/opt/homebrew/bin", "/usr/local/bin",
                "/Users/dev/.local/bin", "/Users/dev/.cargo/bin", "/Users/dev/.bugsee/bin",
            }));
            Assert.That(dirs[0].Value, Is.EqualTo(CliSource.Path));
            Assert.That(dirs[1].Value, Is.EqualTo(CliSource.Path), "a well-known directory that is also on PATH counts as PATH");
            Assert.That(dirs[2].Value, Is.EqualTo(CliSource.KnownDirectory));
        }

        [Test]
        public void Windows_searches_localappdata_and_uses_semicolons()
        {
            var s = new CliSettings
            {
                IsWindows = true,
                PathEnv = "C:\\Windows;C:\\tools",
                Home = "C:\\Users\\dev",
                LocalAppData = "C:\\Users\\dev\\AppData\\Local",
            };

            var dirs = BugseeCliPolicy.SearchDirectories(s).Select(d => d.Key).ToList();

            Assert.That(dirs, Is.EqualTo(new[]
            {
                "C:\\Windows", "C:\\tools", "C:\\Users\\dev\\AppData\\Local\\Bugsee\\bin", "C:\\Users\\dev\\.bugsee\\bin",
            }));
        }

        [Test]
        public void A_cli_in_a_directory_missing_from_the_editor_path_is_found()
        {
            // The reported failure: the Hub-started editor's PATH has no /opt/homebrew/bin, so a bare "bugsee-cli"
            // could not be started.
            var host = new Host();
            host.Files["/opt/homebrew/bin/bugsee-cli"] = "0.8.1";

            var r = BugseeCliPolicy.Resolve(Mac(), host.Probe());

            Assert.That(r.Found, Is.True);
            Assert.That(r.Path, Is.EqualTo("/opt/homebrew/bin/bugsee-cli"));
            Assert.That(r.Source, Is.EqualTo(CliSource.KnownDirectory));
            Assert.That(host.Downloads, Is.Empty);
        }

        [Test]
        public void The_floor_is_the_release_that_upgrades_symbol_tables_without_force()
        {
            Assert.That(BugseeCliPolicy.MinVersion, Is.EqualTo("0.8.1"));
            Assert.That(BugseeCliPolicy.VersionAtLeast(BugseeCliPolicy.DefaultVersion, BugseeCliPolicy.MinVersion), Is.True);
        }

        [Test]
        public void An_installed_0_8_0_is_passed_over_and_the_pinned_release_is_downloaded()
        {
            var host = new Host();
            host.Files["/opt/homebrew/bin/bugsee-cli"] = "0.8.0";

            var r = BugseeCliPolicy.Resolve(Mac(), host.Probe());

            Assert.That(r.Notes[0], Does.Contain("0.8.0").And.Contain("older than 0.8.1"));
            Assert.That(r.Path, Is.Not.EqualTo("/opt/homebrew/bin/bugsee-cli"));
        }

        [Test]
        public void Explicit_path_wins_over_everything_and_skips_the_version_gate()
        {
            var host = new Host();
            host.Files["/opt/homebrew/bin/bugsee-cli"] = "0.8.1";
            host.Files["/custom/bugsee-cli"] = "0.7.0";
            var s = Mac();
            s.ExplicitPath = "/custom/bugsee-cli";

            var r = BugseeCliPolicy.Resolve(s, host.Probe());

            Assert.That(r.Path, Is.EqualTo("/custom/bugsee-cli"));
            Assert.That(r.Source, Is.EqualTo(CliSource.Explicit));
            Assert.That(r.Notes.Single(), Does.Contain("older than " + BugseeCliPolicy.MinVersion));
        }

        [Test]
        public void Explicit_path_that_does_not_exist_is_reported_and_discovery_continues()
        {
            var host = new Host();
            host.Files["/usr/local/bin/bugsee-cli"] = "0.8.1";
            var s = Mac();
            s.ExplicitPath = "/nope/bugsee-cli";

            var r = BugseeCliPolicy.Resolve(s, host.Probe());

            Assert.That(r.Path, Is.EqualTo("/usr/local/bin/bugsee-cli"));
            Assert.That(r.Notes.Single(), Does.Contain("/nope/bugsee-cli").And.Contain("does not exist"));
        }

        [Test]
        public void On_windows_a_bare_command_also_tries_the_exe_name()
        {
            var host = new Host();
            host.Files["C:\\tools\\bugsee-cli.exe"] = "0.8.1";
            var s = new CliSettings { IsWindows = true, PathEnv = "C:\\tools", ExplicitPath = "bugsee-cli" };

            var r = BugseeCliPolicy.Resolve(s, host.Probe());

            Assert.That(r.Path, Is.EqualTo("C:\\tools\\bugsee-cli.exe"));
        }

        [Test]
        public void The_default_release_has_a_pinned_hash_for_every_published_host_and_other_versions_none()
        {
            foreach (var triple in new[] { "aarch64-apple-darwin", "x86_64-apple-darwin", "x86_64-unknown-linux-gnu", "aarch64-unknown-linux-gnu", "x86_64-pc-windows-msvc" })
            {
                Assert.That(BugseeCliPolicy.KnownSha256(BugseeCliPolicy.DefaultVersion, triple), Has.Length.EqualTo(64), triple);
                Assert.That(BugseeCliPolicy.ParseSha256Sidecar(BugseeCliPolicy.KnownSha256(BugseeCliPolicy.DefaultVersion, triple)), Is.Not.Null);
            }

            Assert.That(BugseeCliPolicy.KnownSha256("0.8.2", "aarch64-apple-darwin"), Is.Null);
            Assert.That(BugseeCliPolicy.KnownSha256(BugseeCliPolicy.DefaultVersion, "riscv"), Is.Null);
        }

        [Test]
        public void Explicit_bare_command_is_looked_up_in_every_search_directory()
        {
            var host = new Host();
            host.Files["/Users/dev/.local/bin/bugsee-cli"] = "0.8.1";
            var s = Mac();
            s.ExplicitPath = "bugsee-cli";

            var r = BugseeCliPolicy.Resolve(s, host.Probe());

            Assert.That(r.Path, Is.EqualTo("/Users/dev/.local/bin/bugsee-cli"));
            Assert.That(r.Source, Is.EqualTo(CliSource.Explicit));
        }

        [Test]
        public void Old_or_broken_installs_are_passed_over_for_the_next_candidate()
        {
            var host = new Host();
            host.Files["/opt/homebrew/bin/bugsee-cli"] = "0.7.13";
            host.Files["/usr/local/bin/bugsee-cli"] = "";
            host.Files["/Users/dev/.bugsee/bin/bugsee-cli"] = "0.8.1";

            var r = BugseeCliPolicy.Resolve(Mac(), host.Probe());

            Assert.That(r.Path, Is.EqualTo("/Users/dev/.bugsee/bin/bugsee-cli"));
            Assert.That(r.Notes, Has.Count.EqualTo(2));
            Assert.That(r.Notes[0], Does.Contain("0.7.13"));
            Assert.That(r.Notes[1], Does.Contain("does not run"));
        }

        [Test]
        public void A_cached_download_is_used_without_downloading_again()
        {
            var host = new Host();
            host.Files["/Users/dev/Library/Caches/Bugsee/cli/0.8.1/aarch64-apple-darwin/bugsee-cli"] = "0.8.1";

            var r = BugseeCliPolicy.Resolve(Mac(), host.Probe());

            Assert.That(r.Source, Is.EqualTo(CliSource.Cache));
            Assert.That(host.Downloads, Is.Empty);
        }

        [Test]
        public void A_missing_cli_is_downloaded_once_and_installed_into_the_first_cache_directory()
        {
            var host = new Host();
            host.OnFetch = version => "/tmp/stage/bugsee-cli";
            host.OnInstall = (staged, dir, version) =>
            {
                string path = dir + "/" + version + "/aarch64-apple-darwin/bugsee-cli";
                host.Files[path] = version;
                return path;
            };

            var r = BugseeCliPolicy.Resolve(Mac(), host.Probe());

            Assert.That(r.Source, Is.EqualTo(CliSource.Downloaded));
            Assert.That(r.Path, Is.EqualTo("/Users/dev/Library/Caches/Bugsee/cli/0.8.1/aarch64-apple-darwin/bugsee-cli"));
            Assert.That(host.Downloads, Is.EqualTo(new[] { "0.8.1" }));
            Assert.That(host.Installs, Is.EqualTo(new[] { "/Users/dev/Library/Caches/Bugsee/cli" }));
            Assert.That(host.Discards, Is.EqualTo(new[] { "/tmp/stage/bugsee-cli" }), "the staged copy is always removed");
        }

        [Test]
        public void A_failing_download_costs_one_attempt_whatever_the_number_of_cache_directories()
        {
            var host = new Host();
            host.OnFetch = version => throw new InvalidOperationException("timed out");

            var r = BugseeCliPolicy.Resolve(Mac(), host.Probe());

            Assert.That(r.Found, Is.False);
            Assert.That(host.Downloads, Has.Count.EqualTo(1));
            Assert.That(host.Installs, Is.Empty);
            string message = BugseeCliPolicy.NotFoundMessage(r);
            Assert.That(message, Does.Contain("timed out").And.Contain("BUGSEE_CLI_PATH").And.Contain("install.sh"));
            Assert.That(message, Does.Contain("build continues"));
        }

        [Test]
        public void An_unwritable_first_cache_directory_falls_back_to_the_next_without_downloading_again()
        {
            var host = new Host();
            host.OnFetch = version => "/tmp/stage/bugsee-cli";
            host.OnInstall = (staged, dir, version) =>
            {
                if (dir.StartsWith("/Users/dev/Library"))
                {
                    throw new InvalidOperationException("read-only file system");
                }

                string path = dir + "/bugsee-cli";
                host.Files[path] = version;
                return path;
            };

            var r = BugseeCliPolicy.Resolve(Mac(), host.Probe());

            Assert.That(r.Path, Is.EqualTo("/proj/Library/Bugsee/cli/bugsee-cli"));
            Assert.That(host.Downloads, Has.Count.EqualTo(1));
            Assert.That(host.Installs, Has.Count.EqualTo(2));
            Assert.That(r.Notes.Single(), Does.Contain("read-only file system"));
            Assert.That(host.Discards, Has.Count.EqualTo(1));
        }

        [Test]
        public void Download_is_not_attempted_when_it_is_not_allowed()
        {
            var host = new Host();

            var r = BugseeCliPolicy.Resolve(Mac(download: false), host.Probe());

            Assert.That(r.Found, Is.False);
            Assert.That(host.Downloads, Is.Empty);
            Assert.That(r.Notes.Single(), Does.Contain("BUGSEE_NO_CLI_DOWNLOAD"));

            var s = Mac(download: false);
            s.DownloadDisabledNote = "failed earlier in this Editor session";
            Assert.That(BugseeCliPolicy.Resolve(s, host.Probe()).Notes.Single(), Is.EqualTo("failed earlier in this Editor session"));
        }

        [Test]
        public void A_host_without_a_published_release_gets_a_note_and_no_download()
        {
            var host = new Host();
            var s = Mac();
            s.Triple = null;

            var r = BugseeCliPolicy.Resolve(s, host.Probe());

            Assert.That(r.Found, Is.False);
            Assert.That(host.Downloads, Is.Empty);
            Assert.That(r.Notes.Single(), Does.Contain("No bugsee-cli release is published"));
        }

        [TestCase(true, false, true)]
        [TestCase(true, true, false)]
        [TestCase(false, false, false)]
        public void Download_needs_the_token_and_respects_the_opt_out(bool token, bool optedOut, bool expected)
        {
            Assert.That(BugseeCliPolicy.DownloadAllowed(token, optedOut), Is.EqualTo(expected));
        }

        [TestCase("0.8.1", true)]
        [TestCase("1.2", true)]
        [TestCase("0.9.0-rc.1", true)]
        [TestCase("../..", false)]
        [TestCase("0.8.1/../../x", false)]
        [TestCase("", false)]
        [TestCase("v0.8.1", false)]
        [TestCase(null, false)]
        public void Only_release_versions_may_reach_a_path_or_url(string version, bool expected)
        {
            Assert.That(BugseeCliPolicy.IsValidVersion(version), Is.EqualTo(expected));
        }

        [TestCase("https://user:secret@mirror.example/cli", "https://mirror.example/cli")]
        [TestCase("https://mirror.example/cli/v0.8.1/x.tar.xz", "https://mirror.example/cli/v0.8.1/x.tar.xz")]
        [TestCase("not a url", "not a url")]
        [TestCase(null, null)]
        public void Credentials_are_removed_from_urls_before_they_are_logged(string url, string expected)
        {
            var redacted = BugseeCliPolicy.RedactUrl(url);

            Assert.That(redacted == null ? null : redacted.TrimEnd('/'), Is.EqualTo(expected == null ? null : expected.TrimEnd('/')));
            Assert.That(redacted ?? "", Does.Not.Contain("secret"));
        }

        [Test]
        public void Cache_layout_is_version_then_triple()
        {
            Assert.That(
                BugseeCliPolicy.CachedBinaryPath(new CliSettings(), "/c", "0.8.1", "x86_64-pc-windows-msvc"),
                Is.EqualTo("/c/0.8.1/x86_64-pc-windows-msvc/bugsee-cli.exe"));
        }
    }
}
