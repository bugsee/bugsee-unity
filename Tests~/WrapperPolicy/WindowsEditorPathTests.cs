using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    /// <summary>
    /// The Editor symbol path as a Windows Editor sees it, simulated on any host: CliSettings.IsWindows switches the
    /// policy to ';' PATH entries, backslash paths, case-insensitive de-duplication and the .exe name. Nothing here runs
    /// on Windows; the manual checklist in Documentation~/index.md covers what only a Windows Editor can show.
    /// </summary>
    public class WindowsEditorPathTests
    {
        sealed class Files
        {
            public readonly Dictionary<string, string> Present = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public CliProbe Probe() => new CliProbe
            {
                FileExists = Present.ContainsKey,
                QueryVersion = p => Present.TryGetValue(p, out var v) && v != "" ? v : null,
            };
        }

        static CliSettings Windows(string path = "") => new CliSettings
        {
            IsWindows = true,
            PathEnv = path,
            Home = "C:\\Users\\dev",
            LocalAppData = "C:\\Users\\dev\\AppData\\Local",
            Triple = "x86_64-pc-windows-msvc",
        };

        [Test]
        public void Path_entries_split_on_semicolons_so_drive_letters_survive()
        {
            var dirs = BugseeCliPolicy.SearchDirectories(Windows("C:\\Tools;D:\\bin\\;;  \"C:\\Program Files\\Bugsee\"  "))
                .Select(e => e.Key).ToList();

            Assert.That(dirs.Take(3), Is.EqualTo(new[] { "C:\\Tools", "D:\\bin", "C:\\Program Files\\Bugsee" }));
        }

        [Test]
        public void Install_directories_are_the_windows_ones_and_never_the_posix_ones()
        {
            var entries = BugseeCliPolicy.SearchDirectories(Windows());
            var dirs = entries.Select(e => e.Key).ToList();

            Assert.That(dirs, Is.EqualTo(new[] { "C:\\Users\\dev\\AppData\\Local\\Bugsee\\bin", "C:\\Users\\dev\\.bugsee\\bin" }));
            Assert.That(entries.All(e => e.Value == CliSource.KnownDirectory));
        }

        [Test]
        public void Directories_differing_only_in_case_or_a_trailing_backslash_are_one_entry()
        {
            var dirs = BugseeCliPolicy.SearchDirectories(
                    Windows("C:\\Users\\dev\\AppData\\Local\\Bugsee\\bin;c:\\users\\DEV\\appdata\\local\\bugsee\\bin\\;C:\\Tools;c:\\tools"))
                .Select(e => e.Key).ToList();

            Assert.That(dirs.Count(d => d.Equals("C:\\Users\\dev\\AppData\\Local\\Bugsee\\bin", StringComparison.OrdinalIgnoreCase)), Is.EqualTo(1));
            Assert.That(dirs.Count(d => d.Equals("C:\\Tools", StringComparison.OrdinalIgnoreCase)), Is.EqualTo(1));
        }

        [Test]
        public void Drive_roots_and_unc_shares_keep_their_form()
        {
            // "C:\" loses its slash with the trailing-separator trim, and the candidate must still be C:\bugsee-cli.exe.
            var files = new Files();
            files.Present["C:\\bugsee-cli.exe"] = "0.8.1";
            var root = BugseeCliPolicy.Resolve(Windows("C:\\"), files.Probe());
            Assert.That(root.Path, Is.EqualTo("C:\\bugsee-cli.exe"));

            files = new Files();
            files.Present["\\\\build01\\tools\\bugsee-cli.exe"] = "0.8.1";
            var unc = BugseeCliPolicy.Resolve(Windows("\\\\build01\\tools\\"), files.Probe());
            Assert.That(unc.Path, Is.EqualTo("\\\\build01\\tools\\bugsee-cli.exe"));
            Assert.That(unc.Source, Is.EqualTo(CliSource.Path));
        }

        [Test]
        public void The_binary_looked_for_is_the_exe_and_a_bare_name_is_not_taken_for_it()
        {
            var files = new Files();
            files.Present["C:\\Tools\\bugsee-cli"] = "0.8.1"; // e.g. a Unix binary someone copied along; not runnable on Windows
            var r = BugseeCliPolicy.Resolve(Windows("C:\\Tools"), files.Probe());

            Assert.That(r.Found, Is.False);
            files.Present["C:\\Tools\\bugsee-cli.exe"] = "0.8.1";
            Assert.That(BugseeCliPolicy.Resolve(Windows("C:\\Tools"), files.Probe()).Path, Is.EqualTo("C:\\Tools\\bugsee-cli.exe"));
        }

        [Test]
        public void Install_ps1_location_is_found_without_it_being_on_the_editors_path()
        {
            var files = new Files();
            files.Present["C:\\Users\\dev\\AppData\\Local\\Bugsee\\bin\\bugsee-cli.exe"] = "0.8.1";

            var r = BugseeCliPolicy.Resolve(Windows("C:\\Windows\\System32"), files.Probe());

            Assert.That(r.Path, Is.EqualTo("C:\\Users\\dev\\AppData\\Local\\Bugsee\\bin\\bugsee-cli.exe"));
            Assert.That(r.Source, Is.EqualTo(CliSource.KnownDirectory));
        }

        [Test]
        public void Without_localappdata_the_home_directory_is_still_searched()
        {
            var s = Windows();
            s.LocalAppData = null;
            Assert.That(BugseeCliPolicy.SearchDirectories(s).Select(e => e.Key), Is.EqualTo(new[] { "C:\\Users\\dev\\.bugsee\\bin" }));
        }

        [TestCase("\"C:\\Program Files\\Bugsee\\bugsee-cli.exe\"")]
        [TestCase("  \"C:\\Program Files\\Bugsee\\bugsee-cli.exe\"  ")]
        [TestCase("C:\\Program Files\\Bugsee\\bugsee-cli.exe")]
        [TestCase("C:\\Program Files\\Bugsee\\bugsee-cli")]
        public void An_explicit_windows_path_survives_quotes_and_a_missing_extension(string value)
        {
            var files = new Files();
            files.Present["C:\\Program Files\\Bugsee\\bugsee-cli.exe"] = "0.8.1";
            var s = Windows();
            s.ExplicitPath = value;

            var r = BugseeCliPolicy.Resolve(s, files.Probe());

            Assert.That(r.Path, Is.EqualTo("C:\\Program Files\\Bugsee\\bugsee-cli.exe"));
            Assert.That(r.Source, Is.EqualTo(CliSource.Explicit));
        }

        [Test]
        public void An_explicit_path_is_not_rewritten_off_windows()
        {
            var files = new Files();
            files.Present["/opt/x/bugsee-cli.exe"] = "0.8.1";
            var s = new CliSettings { PathEnv = "/usr/bin", ExplicitPath = "/opt/x/bugsee-cli" };

            var r = BugseeCliPolicy.Resolve(s, files.Probe());

            Assert.That(r.Found, Is.False);
            Assert.That(r.Notes.First(), Does.Contain("/opt/x/bugsee-cli").And.Contain("does not exist"));
        }

        [Test]
        public void An_explicit_windows_path_that_is_missing_is_reported_without_its_quotes()
        {
            var s = Windows();
            s.ExplicitPath = "\"D:\\gone\\bugsee-cli.exe\"";

            var r = BugseeCliPolicy.Resolve(s, new Files().Probe());

            Assert.That(r.Notes.First(), Does.Contain("D:\\gone\\bugsee-cli.exe").And.Contain("does not exist"));
        }

        [Test]
        public void The_download_cache_is_laid_out_with_backslashes_and_the_exe_name_including_long_paths()
        {
            var s = Windows();
            Assert.That(
                BugseeCliPolicy.CachedBinaryPath(s, "C:\\Users\\dev\\AppData\\Local\\Bugsee\\cache\\cli\\", "0.8.1", "x86_64-pc-windows-msvc"),
                Is.EqualTo("C:\\Users\\dev\\AppData\\Local\\Bugsee\\cache\\cli\\0.8.1\\x86_64-pc-windows-msvc\\bugsee-cli.exe"));

            // Over MAX_PATH (260): the policy must neither truncate nor reroute it.
            string deep = "D:\\" + string.Join("\\", Enumerable.Repeat("a-rather-long-folder-name", 12)) + "\\Library\\Bugsee\\cli";
            string cached = BugseeCliPolicy.CachedBinaryPath(s, deep, "0.8.1", "aarch64-pc-windows-msvc");
            Assert.That(cached.Length, Is.GreaterThan(260));
            Assert.That(cached, Is.EqualTo(deep + "\\0.8.1\\aarch64-pc-windows-msvc\\bugsee-cli.exe"));
        }

        [Test]
        public void A_cached_windows_download_is_used_before_downloading_again()
        {
            var files = new Files();
            files.Present["C:\\Users\\dev\\AppData\\Local\\Bugsee\\cache\\cli\\0.8.1\\x86_64-pc-windows-msvc\\bugsee-cli.exe"] = "0.8.1";
            var s = Windows();
            s.DownloadAllowed = true;
            s.CacheDirectories.Add("C:\\Users\\dev\\AppData\\Local\\Bugsee\\cache\\cli");

            var r = BugseeCliPolicy.Resolve(s, files.Probe());

            Assert.That(r.Source, Is.EqualTo(CliSource.Cache));
        }

        [Test]
        public void A_windows_download_installs_the_exe_under_the_first_writable_cache_directory()
        {
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var probe = new CliProbe
            {
                FileExists = present.Contains,
                QueryVersion = p => "0.8.1",
                Fetch = v => "C:\\Users\\dev\\AppData\\Local\\Temp\\bugsee-cli-1\\x\\bugsee-cli.exe",
                Install = (staged, dir, v) =>
                {
                    if (dir.StartsWith("C:\\locked", StringComparison.Ordinal)) throw new UnauthorizedAccessException("Access to the path is denied.");
                    string target = BugseeCliPolicy.CachedBinaryPath(Windows(), dir, v, "x86_64-pc-windows-msvc");
                    present.Add(target);
                    return target;
                },
            };
            var s = Windows();
            s.DownloadAllowed = true;
            s.CacheDirectories.Add("C:\\locked\\cli");
            s.CacheDirectories.Add("D:\\proj\\Library\\Bugsee\\cli");

            var r = BugseeCliPolicy.Resolve(s, probe);

            Assert.That(r.Source, Is.EqualTo(CliSource.Downloaded));
            Assert.That(r.Path, Is.EqualTo("D:\\proj\\Library\\Bugsee\\cli\\0.8.1\\x86_64-pc-windows-msvc\\bugsee-cli.exe"));
            Assert.That(r.Notes.Single(), Does.Contain("C:\\locked\\cli").And.Contain("denied"));
        }

        [Test]
        public void Windows_archives_are_zips_named_by_both_published_triples()
        {
            Assert.That(BugseeCliPolicy.ArchiveName("x86_64-pc-windows-msvc"), Is.EqualTo("bugsee-cli-x86_64-pc-windows-msvc.zip"));
            Assert.That(BugseeCliPolicy.ArchiveName("aarch64-pc-windows-msvc"), Is.EqualTo("bugsee-cli-aarch64-pc-windows-msvc.zip"));
            Assert.That(BugseeCliPolicy.BinaryName(BugseeCliPolicy.IsWindowsTriple("aarch64-pc-windows-msvc")), Is.EqualTo("bugsee-cli.exe"));
            Assert.That(BugseeCliPolicy.KnownSha256("0.8.1", "x86_64-pc-windows-msvc"), Is.Not.EqualTo(BugseeCliPolicy.KnownSha256("0.8.1", "aarch64-pc-windows-msvc")));
        }

        [TestCase("bugsee-cli.exe 0.8.1\r\n", "0.8.1")]
        [TestCase("bugsee-cli 0.8.1\r\n\r\n", "0.8.1")]
        [TestCase("\r\nbugsee-cli 0.9.0-beta.1\r\n", "0.9.0-beta.1")]
        public void Version_output_with_windows_line_endings_is_parsed(string output, string expected)
        {
            Assert.That(BugseeCliPolicy.ParseVersionOutput(output), Is.EqualTo(expected));
        }

        [Test]
        public void A_checksum_sidecar_written_on_windows_is_parsed()
        {
            string hash = new string('a', 64);
            Assert.That(BugseeCliPolicy.ParseSha256Sidecar(hash + " *bugsee-cli-x86_64-pc-windows-msvc.zip\r\n"), Is.EqualTo(hash));
        }

        [Test]
        public void The_not_found_message_names_the_installer_of_the_host()
        {
            var r = new CliResolution();

            string windows = BugseeCliPolicy.NotFoundMessage(r, windows: true);
            Assert.That(windows, Does.Contain("install.ps1").And.Not.Contain("install.sh"));
            Assert.That(windows, Does.Contain("BUGSEE_CLI_PATH"));

            string posix = BugseeCliPolicy.NotFoundMessage(r);
            Assert.That(posix, Does.Contain("install.sh").And.Not.Contain("install.ps1"));
        }

        [Test]
        public void The_ios_run_script_is_posix_text_whatever_the_host_and_bakes_no_windows_path()
        {
            foreach (string baked in new[] { null, "C:\\Users\\dev\\bugsee-cli.exe", "\\\\server\\share\\bugsee-cli.exe", "C:/Users/dev/bugsee-cli.exe" })
            {
                string script = IosArchiveUploadScript.Build(baked);
                Assert.That(script, Does.Not.Contain("\r"), baked);
                Assert.That(script, Does.StartWith("#!/bin/sh\n"), baked);
                Assert.That(script, Does.Contain("BAKED_CLI=''\n"), baked);
            }
        }
    }
}
