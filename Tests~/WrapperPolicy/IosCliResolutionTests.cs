using System;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    // The Run Script runs under Xcode's minimal PATH, so it finds bugsee-cli the way the Editor locator does rather than
    // by a bare lookup. These run the generated script for real, with a fake HOME and a stub CLI planted per location.
    [TestFixture("/bin/sh")]
    [TestFixture("/bin/dash")]
    public class IosCliResolutionTests
    {
        readonly string _shell;

        public IosCliResolutionTests(string shell) { _shell = shell; }

        [Test]
        public void Archive_script_resolves_the_cli_from_every_place_the_editor_looks()
        {
            string text = IosArchiveUploadScript.Build();
            foreach (var place in new[]
            {
                "${BUGSEE_CLI_PATH}", "${BAKED_CLI}", "for DIR in ${PATH}", "/opt/homebrew/bin /usr/local/bin",
                "${HOME}/.bugsee/bin/bugsee-cli", "${HOME}/.local/bin/bugsee-cli", "${HOME}/.cargo/bin/bugsee-cli",
                "${HOME}/Library/Caches/Bugsee/cli", "${XDG_CACHE_HOME:-${HOME}/.cache}/bugsee/cli", "\"${CACHE}\"/*/*/bugsee-cli",
            })
                Assert.That(text, Does.Contain(place));
            Assert.That(text, Does.Not.Contain("CLI=\"${BUGSEE_CLI_PATH:-bugsee-cli}\""));
        }

        [Test]
        public void Build_bakes_the_resolved_path_as_one_quoted_word()
        {
            string text = IosArchiveUploadScript.Build("/Users/o'neil/cli dir/bugsee-cli");
            Assert.That(text, Does.Contain("BAKED_CLI='/Users/o'\\''neil/cli dir/bugsee-cli'\n"));
            Assert.That(IosArchiveUploadScript.Build(null), Does.Contain("BAKED_CLI=''\n"));
            Assert.That(IosArchiveUploadScript.Build("/a\nb"), Does.Contain("BAKED_CLI=''\n"));
        }

        [Test]
        public void Finds_the_cli_named_by_BUGSEE_CLI_PATH()
        {
            using (var box = new Sandbox(_shell))
            {
                string cli = box.Stub("explicit/bugsee-cli");
                var run = box.Run(env => env["BUGSEE_CLI_PATH"] = cli);
                Assert.That(run.Calls, Has.Count.GreaterThan(0), run.Output);
                Assert.That(run.Output, Does.Contain("using " + cli));
                Assert.That(run.ExitCode, Is.EqualTo(0));
            }
        }

        [Test]
        public void Finds_the_cli_baked_in_at_export_time()
        {
            using (var box = new Sandbox(_shell))
            {
                string cli = box.Stub("exported dir/bugsee-cli");
                var run = box.Run(null, IosArchiveUploadScript.Build(cli));
                Assert.That(run.Output, Does.Contain("using " + cli));
                // Found first: places after it are not reported as passed over.
                Assert.That(run.Output, Does.Not.Contain("not on PATH"));
                Assert.That(run.Calls, Has.Count.GreaterThan(0), run.Output);
                Assert.That(run.ExitCode, Is.EqualTo(0));
            }
        }

        [Test]
        public void Finds_the_cli_on_PATH()
        {
            using (var box = new Sandbox(_shell))
            {
                string cli = box.Stub("pathdir/bugsee-cli");
                var run = box.Run(env => env["PATH"] = Path.GetDirectoryName(cli) + ":/usr/bin:/bin");
                Assert.That(run.Output, Does.Contain("using " + cli));
                Assert.That(run.ExitCode, Is.EqualTo(0));
            }
        }

        [TestCase("home/.bugsee/bin/bugsee-cli")]
        [TestCase("home/.local/bin/bugsee-cli")]
        [TestCase("home/.cargo/bin/bugsee-cli")]
        [TestCase("home/Library/Caches/Bugsee/cli/0.8.1/aarch64-apple-darwin/bugsee-cli")]
        [TestCase("home/.cache/bugsee/cli/0.8.1/x86_64-unknown-linux-gnu/bugsee-cli")]
        [TestCase("system/bugsee-cli")]
        public void Finds_the_cli_in_an_install_or_cache_directory_under_a_minimal_PATH(string relative)
        {
            using (var box = new Sandbox(_shell))
            {
                string cli = box.Stub(relative);
                var run = box.Run(null);
                Assert.That(run.Output, Does.Contain("using " + cli));
                Assert.That(run.Calls, Has.Count.GreaterThan(0), run.Output);
                Assert.That(run.ExitCode, Is.EqualTo(0));
            }
        }

        [Test]
        public void Ignores_a_candidate_that_is_not_executable()
        {
            using (var box = new Sandbox(_shell))
            {
                Directory.CreateDirectory(box.Path("home/.local/bin"));
                File.WriteAllText(box.Path("home/.local/bin/bugsee-cli"), "");
                string good = box.Stub("home/.bugsee/bin/bugsee-cli");
                var run = box.Run(null);
                Assert.That(run.Output, Does.Contain("using " + good));
            }
        }

        [Test]
        public void Logs_every_place_tried_and_still_exits_zero_when_there_is_no_cli()
        {
            using (var box = new Sandbox(_shell))
            {
                var run = box.Run(env => env["BUGSEE_CLI_PATH"] = "/nonexistent/bugsee-cli");
                Assert.That(run.ExitCode, Is.EqualTo(0), run.Output);
                Assert.That(run.Calls, Is.Empty);
                Assert.That(run.Output, Does.Contain("warning: Bugsee: bugsee-cli was not found, so symbols are NOT uploaded"));
                Assert.That(run.Output, Does.Contain("/nonexistent/bugsee-cli"));
                Assert.That(run.Output, Does.Contain(box.Path("home/.bugsee/bin/bugsee-cli")));
                Assert.That(run.Output, Does.Contain(box.Path("home/Library/Caches/Bugsee/cli")));
                Assert.That(run.Output, Does.Contain("/nonexistent/bugsee-cli (not found)"));
                Assert.That(run.Output, Does.Contain("bugsee-cli (not found in PATH=/usr/bin:/bin)"));
            }
        }

        [Test]
        public void A_bare_BUGSEE_CLI_PATH_that_is_not_on_PATH_is_listed()
        {
            using (var box = new Sandbox(_shell))
            {
                var run = box.Run(env => env["BUGSEE_CLI_PATH"] = "my-cli");
                Assert.That(run.Output, Does.Contain("my-cli (not on PATH=/usr/bin:/bin)"));
                Assert.That(run.ExitCode, Is.EqualTo(0));
            }
        }

        [Test]
        public void Passes_over_a_cli_that_does_not_run_and_one_older_than_the_minimum()
        {
            using (var box = new Sandbox(_shell))
            {
                string broken = box.Path("home/.local/bin/bugsee-cli");
                Directory.CreateDirectory(Path.GetDirectoryName(broken));
                File.WriteAllText(broken, "#!/bin/sh\nexit 3\n");
                File.SetUnixFileMode(broken, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                box.Stub("system/bugsee-cli", "0.7.9");
                string good = box.Stub("home/.bugsee/bin/bugsee-cli", "0.10.0");
                var run = box.Run(null);
                Assert.That(run.Output, Does.Contain(broken + " (does not run)"));
                Assert.That(run.Output, Does.Contain("(version 0.7.9, older than 0.8.0)"));
                Assert.That(run.Output, Does.Contain("using " + good));
                Assert.That(run.Calls, Has.Count.GreaterThan(0), run.Output);
            }
        }

        [Test]
        public void Precedence_is_explicit_then_baked_then_PATH_then_install_dirs()
        {
            using (var box = new Sandbox(_shell))
            {
                string explicitCli = box.Stub("a/bugsee-cli");
                string baked = box.Stub("b/bugsee-cli");
                string onPath = box.Stub("c/bugsee-cli");
                box.Stub("home/.bugsee/bin/bugsee-cli");
                string script = IosArchiveUploadScript.Build(baked);
                Action<System.Collections.Generic.IDictionary<string, string>> withPath =
                    env => env["PATH"] = Path.GetDirectoryName(onPath) + ":/usr/bin:/bin";
                Assert.That(box.Run(env => { withPath(env); env["BUGSEE_CLI_PATH"] = explicitCli; }, script).Output, Does.Contain("using " + explicitCli));
                Assert.That(box.Run(withPath, script).Output, Does.Contain("using " + baked));
                Assert.That(box.Run(withPath, IosArchiveUploadScript.Build(box.Path("missing"))).Output, Does.Contain("using " + onPath));
            }
        }

        [Test]
        public void A_missing_baked_path_falls_through_to_the_next_candidate()
        {
            using (var box = new Sandbox(_shell))
            {
                string cli = box.Stub("home/.bugsee/bin/bugsee-cli");
                var run = box.Run(null, IosArchiveUploadScript.Build(box.Path("gone/bugsee-cli")));
                Assert.That(run.Output, Does.Contain(box.Path("gone/bugsee-cli") + " (not found)"));
                Assert.That(run.Output, Does.Contain("using " + cli));
            }
        }

        [Test]
        public void A_cli_that_fails_its_uploads_still_exits_zero()
        {
            using (var box = new Sandbox(_shell))
            {
                box.Stub("home/.bugsee/bin/bugsee-cli", exitCode: 7);
                var run = box.Run(null);
                Assert.That(run.Output, Does.Contain("warning: Bugsee: symbol upload failed (exit 7)"));
                Assert.That(run.ExitCode, Is.EqualTo(0));
            }
        }

        [Test]
        public void Runs_with_HOME_unset_and_still_logs_when_nothing_is_found()
        {
            using (var box = new Sandbox(_shell))
            {
                var run = box.Run(null, null, homeUnset: true);
                Assert.That(run.ExitCode, Is.EqualTo(0), run.Output);
                Assert.That(run.Output, Does.Contain("bugsee-cli was not found"));
                string onPath = box.Stub("c/bugsee-cli");
                run = box.Run(env => env["PATH"] = Path.GetDirectoryName(onPath) + ":/usr/bin:/bin", null, homeUnset: true);
                Assert.That(run.Output, Does.Contain("using " + onPath));
            }
        }

        // The baked path is data inside a quoted word: $, backtick, quote and backslash must not be expanded or run.
        [Test]
        public void A_baked_path_with_shell_metacharacters_is_taken_literally()
        {
            using (var box = new Sandbox(_shell))
            {
                string cli = box.Stub("we ird$HOME`touch pwned`\"q'uote/bugsee-cli");
                var run = box.Run(null, IosArchiveUploadScript.Build(cli));
                Assert.That(run.Output, Does.Contain("using " + cli));
                Assert.That(run.Calls, Has.Count.GreaterThan(0), run.Output);
                Assert.That(File.Exists(box.Path("pwned")) || File.Exists("pwned"), Is.False);
            }
        }

        [Test]
        public void Only_an_absolute_path_is_baked()
        {
            Assert.That(IosArchiveUploadScript.Build("C:\\Users\\me\\bugsee-cli.exe"), Does.Contain("BAKED_CLI=''\n"));
            Assert.That(IosArchiveUploadScript.Build("relative/bugsee-cli"), Does.Contain("BAKED_CLI=''\n"));
        }

        [Test]
        public void Prefers_the_pinned_release_in_the_cache_over_a_lexically_later_one()
        {
            using (var box = new Sandbox(_shell))
            {
                box.Stub("home/Library/Caches/Bugsee/cli/0.10.0/aarch64-apple-darwin/bugsee-cli", "0.10.0");
                string pinned = box.Stub("home/Library/Caches/Bugsee/cli/" + BugseeCliPolicy.DefaultVersion + "/aarch64-apple-darwin/bugsee-cli");
                var run = box.Run(null);
                Assert.That(run.Output, Does.Contain("using " + pinned));
            }
        }

        [TestCase("bugsee-cli 0.8.1\n")]
        [TestCase("bugsee-cli 10.0.0\n")]
        [TestCase("bugsee-cli 1.2.0 (protocol 0.3.1)\n")]
        [TestCase("bugsee-cli 0.8.0\n")]
        [TestCase("bugsee-cli 0.8.0-beta.1\n")]
        [TestCase("bugsee-cli 0.8.1\r\n")]
        [TestCase("bugsee-cli 1.0.0\n")]
        [TestCase("bugsee-cli 0.9.0\n")]
        [TestCase("bugsee-cli\n0.8.2\n")]
        [TestCase("bugsee-cli 0.08.01\n")]
        [TestCase("0.9\n")]
        public void Accepts_a_discovered_cli_whose_version_is_at_least_the_minimum(string output)
        {
            using (var box = new Sandbox(_shell))
            {
                string cli = box.Stub("home/.local/bin/bugsee-cli", versionOutput: output);
                var run = box.Run(null);
                Assert.That(run.Output, Does.Contain("using " + cli));
            }
        }

        [TestCase("bugsee-cli 0.7.9 (protocol 1.2.3)\n")]
        [TestCase("bugsee-cli 0.7.99\n")]
        [TestCase("bugsee-cli 0.7.100\n")]
        [TestCase("bugsee-cli 0.7\n")]
        [TestCase("bugsee-cli v0.9\n")]
        [TestCase("")]
        [TestCase("not a version at all\n")]
        public void Passes_over_a_discovered_cli_that_is_too_old_or_prints_no_version(string output)
        {
            using (var box = new Sandbox(_shell))
            {
                string cli = box.Stub("home/.local/bin/bugsee-cli", versionOutput: output);
                var run = box.Run(null);
                Assert.That(run.ExitCode, Is.EqualTo(0));
                Assert.That(run.Output, Does.Contain("bugsee-cli was not found"));
                Assert.That(run.Output, Does.Contain(cli + " (").And.Matches(@"\((no version in --version output|version [0-9a-z.-]+, older than 0\.8\.0)\)"));
            }
        }

        [Test]
        public void The_script_floor_is_the_linemap_floor_baked_in_from_the_policy_constant()
        {
            Assert.That(IosArchiveUploadScript.Build(), Does.Contain("\nMIN_VERSION='0.8.0'\n"));
        }

        [Test]
        public void A_version_printed_only_to_stderr_does_not_count()
        {
            using (var box = new Sandbox(_shell))
            {
                string cli = box.Path("home/.local/bin/bugsee-cli");
                Directory.CreateDirectory(Path.GetDirectoryName(cli));
                File.WriteAllText(cli, "#!/bin/sh\n[ \"$1\" = --version ] && { echo 'bugsee-cli 0.9.0' >&2; exit 0; }\nexit 0\n");
                File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Assert.That(box.Run(null).Output, Does.Contain(cli + " (no version in --version output)"));
            }
        }

        // A probe that reads stdin or hangs must not hold the build: stdin is /dev/null and a watchdog stops it.
        [Test]
        public void A_probe_that_reads_stdin_or_hangs_is_stopped()
        {
            using (var box = new Sandbox(_shell))
            {
                string reader = box.Path("home/.local/bin/bugsee-cli");
                Directory.CreateDirectory(Path.GetDirectoryName(reader));
                File.WriteAllText(reader, "#!/bin/sh\n[ \"$1\" = --version ] || exit 0\nread line\necho \"bugsee-cli 0.9.0\"\n");
                File.SetUnixFileMode(reader, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                var run = box.Run(null);
                // Its stdin is a pipe nobody writes to (as under a build tool): the probe gets /dev/null instead.
                Assert.That(run.Output, Does.Contain("using " + reader), run.Output);
                File.Delete(reader);

                string hang = box.Path("home/.cargo/bin/bugsee-cli");
                Directory.CreateDirectory(Path.GetDirectoryName(hang));
                File.WriteAllText(hang, "#!/bin/sh\nexec sleep 30\n");
                File.SetUnixFileMode(hang, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                string good = box.Stub("home/.bugsee/bin/bugsee-cli");
                var watch = System.Diagnostics.Stopwatch.StartNew();
                run = box.Run(env => env["BUGSEE_CLI_PROBE_TIMEOUT"] = "1");
                Assert.That(watch.Elapsed.TotalSeconds, Is.LessThan(20), run.Output);
                Assert.That(run.Output, Does.Contain(hang + " (does not run)"));
                Assert.That(run.Output, Does.Contain("using " + good));
            }
        }

        // A probe that ignores SIGTERM is killed after the watchdog's grace period instead of holding the build.
        [Test]
        public void A_probe_that_ignores_sigterm_is_killed()
        {
            using (var box = new Sandbox(_shell))
            {
                string stubborn = box.Path("home/.local/bin/bugsee-cli");
                Directory.CreateDirectory(Path.GetDirectoryName(stubborn));
                File.WriteAllText(stubborn, "#!/bin/sh\ntrap '' TERM\nwhile :; do sleep 1; done\n");
                File.SetUnixFileMode(stubborn, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                string good = box.Stub("home/.bugsee/bin/bugsee-cli");
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var run = box.Run(env => env["BUGSEE_CLI_PROBE_TIMEOUT"] = "1");
                Assert.That(watch.Elapsed.TotalSeconds, Is.LessThan(20), run.Output);
                Assert.That(run.Output, Does.Contain(stubborn + " (does not run)"));
                Assert.That(run.Output, Does.Contain("using " + good));
                Assert.That(run.ExitCode, Is.EqualTo(0));
            }
        }

        // The Editor tries every PATH entry: an old CLI in the first must not hide a good one in the second.
        [Test]
        public void Tries_every_PATH_entry_and_probes_a_path_only_once()
        {
            using (var box = new Sandbox(_shell))
            {
                box.Stub("d1/bugsee-cli", "0.7.9");
                string good = box.Stub("d2/bugsee-cli");
                var run = box.Run(env => env["PATH"] = box.Path("d1") + "::" + box.Path("d2") + ":" + box.Path("d1") + ":/usr/bin:/bin");
                Assert.That(run.Output, Does.Contain("using " + good), run.Output);
                Assert.That(System.Text.RegularExpressions.Regex.Matches(run.Output, System.Text.RegularExpressions.Regex.Escape(box.Path("d1/bugsee-cli") + " (")).Count, Is.EqualTo(1));

                // A directory that is on PATH and also a system directory: one probe, one line.
                string both = box.Stub("system/bugsee-cli", "0.7.9");
                run = box.Run(env => env["PATH"] = box.Path("system") + ":/usr/bin:/bin");
                Assert.That(System.Text.RegularExpressions.Regex.Matches(run.Output, System.Text.RegularExpressions.Regex.Escape(both + " (")).Count, Is.EqualTo(1), run.Output);
            }
        }

        // The pinned release matches both cache globs: it is probed once, so a hanging one costs one timeout, not two.
        [Test]
        public void A_pinned_cache_binary_matching_both_globs_is_probed_once()
        {
            using (var box = new Sandbox(_shell))
            {
                string hang = box.Path("home/Library/Caches/Bugsee/cli/" + BugseeCliPolicy.DefaultVersion + "/aarch64-apple-darwin/bugsee-cli");
                Directory.CreateDirectory(Path.GetDirectoryName(hang));
                File.WriteAllText(hang, "#!/bin/sh\necho probe >> \"" + box.Path("probes.txt") + "\"\nexec sleep 30\n");
                File.SetUnixFileMode(hang, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                var run = box.Run(env => env["BUGSEE_CLI_PROBE_TIMEOUT"] = "1");
                Assert.That(File.ReadAllLines(box.Path("probes.txt")), Has.Length.EqualTo(1), run.Output);
                Assert.That(System.Text.RegularExpressions.Regex.Matches(run.Output, System.Text.RegularExpressions.Regex.Escape(hang + " (does not run)")).Count, Is.EqualTo(1));
                Assert.That(run.ExitCode, Is.EqualTo(0));
            }
        }

        // The probe's output goes to an unpredictable temp file that is removed, also after a probe was stopped.
        [Test]
        public void The_probe_output_file_is_unpredictable_and_removed()
        {
            string text = IosArchiveUploadScript.Build();
            Assert.That(text, Does.Contain("mktemp \"${PROBE_DIR}/bugsee-cli-version.XXXXXX\""));
            // The probe runs in a command substitution: the cleanup trap has to be set inside it, after the file exists.
            int probe = text.IndexOf("probe_version() {");
            int create = text.IndexOf("mktemp", probe);
            int trap = text.IndexOf("trap 'rm -f \"${PROBE_OUT}\"' EXIT", probe);
            Assert.That(trap, Is.GreaterThan(create));
            Assert.That(trap, Is.LessThan(text.IndexOf("try_cli() {", probe)));
            Assert.That(text.IndexOf("trap 'rm -f"), Is.EqualTo(trap), "no other (dead) trap");
            using (var box = new Sandbox(_shell))
            {
                string tmp = box.Path("tmp");
                Directory.CreateDirectory(tmp);
                string hang = box.Path("home/.local/bin/bugsee-cli");
                Directory.CreateDirectory(Path.GetDirectoryName(hang));
                File.WriteAllText(hang, "#!/bin/sh\nexec sleep 30\n");
                File.SetUnixFileMode(hang, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                box.Stub("home/.bugsee/bin/bugsee-cli");
                var run = box.Run(env => { env["TMPDIR"] = tmp; env["BUGSEE_CLI_PROBE_TIMEOUT"] = "1"; });
                Assert.That(run.Output, Does.Contain("using " + box.Path("home/.bugsee/bin/bugsee-cli")), run.Output);
                Assert.That(Directory.GetFileSystemEntries(tmp), Is.Empty);
            }
        }

        [Test]
        public void An_unwritable_or_missing_TMPDIR_still_lets_a_candidate_run()
        {
            using (var box = new Sandbox(_shell))
            {
                string cli = box.Stub("home/.bugsee/bin/bugsee-cli");
                var run = box.Run(env => env["TMPDIR"] = box.Path("no/such/dir"));
                Assert.That(run.Output, Does.Contain("using " + cli), run.Output);
                Assert.That(run.Output, Does.Not.Contain("No such file"));
            }
        }

        // BUGSEE_CLI_VERSION picks the cache directory; one that is not a release version cannot leave the cache.
        [TestCase("../..")]
        [TestCase("../../..")]
        [TestCase("1/../..")]
        public void A_BUGSEE_CLI_VERSION_that_is_not_a_release_does_not_escape_the_cache(string version)
        {
            using (var box = new Sandbox(_shell))
            {
                box.Stub("home/Library/Caches/escaped/bugsee-cli");
                var run = box.Run(env => env["BUGSEE_CLI_VERSION"] = version);
                Assert.That(run.Output, Does.Contain("bugsee-cli was not found"), run.Output);
            }
        }

        [Test]
        public void A_BUGSEE_CLI_VERSION_with_a_leading_v_selects_that_release_in_the_cache()
        {
            using (var box = new Sandbox(_shell))
            {
                box.Stub("home/Library/Caches/Bugsee/cli/0.10.0/aarch64-apple-darwin/bugsee-cli", "0.10.0");
                string wanted = box.Stub("home/Library/Caches/Bugsee/cli/0.9.5/aarch64-apple-darwin/bugsee-cli", "0.9.5");
                var run = box.Run(env => env["BUGSEE_CLI_VERSION"] = "v0.9.5");
                Assert.That(run.Output, Does.Contain("using " + wanted), run.Output);
            }
        }

        // The Editor half of the bake: the Editor's own lookup (explicit path, PATH, install dirs, cache), never downloading.
        [Test]
        public void The_baked_lookup_is_the_editors_without_the_download()
        {
            string text = File.ReadAllText(Path.Combine(
                TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "..", "Editor", "BugseeCliLocator.cs"));
            int start = text.IndexOf("public static string FindInstalled()");
            Assert.That(start, Is.GreaterThan(0));
            string body = text.Substring(start, text.IndexOf("\n        }\n", start) - start);
            Assert.That(body, Does.Contain("ReadSettings(quiet: true)"));
            Assert.That(body, Does.Contain("DownloadAllowed = false"));
        }

        // An explicit BUGSEE_CLI_PATH (and the path the Editor baked in) is the user's choice: used without a --version gate.
        [Test]
        public void An_explicit_or_baked_wrapper_without_version_support_is_used()
        {
            using (var box = new Sandbox(_shell))
            {
                string wrapper = box.Path("wrap/bugsee-cli");
                Directory.CreateDirectory(Path.GetDirectoryName(wrapper));
                File.WriteAllText(wrapper, "#!/bin/sh\n[ \"$1\" = --version ] && exit 2\necho \"$*\" >> \"" + box.Path("calls.txt") + "\"\nexit 0\n");
                File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                var run = box.Run(env => env["BUGSEE_CLI_PATH"] = wrapper);
                Assert.That(run.Output, Does.Contain("using " + wrapper));
                Assert.That(run.Calls, Has.Count.GreaterThan(0), run.Output);
                run = box.Run(null, IosArchiveUploadScript.Build(wrapper));
                Assert.That(run.Output, Does.Contain("using " + wrapper));
            }
        }

        sealed class ScriptRun
        {
            public int ExitCode;
            public string Output;
            public System.Collections.Generic.List<string> Calls;
        }

        sealed class Sandbox : IDisposable
        {
            readonly string _root = Directory.CreateDirectory(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "bugsee-cli-resolve-" + Guid.NewGuid().ToString("N"))).FullName;

            readonly string _shell;

            public Sandbox(string shell)
            {
                _shell = shell;
                if (!File.Exists(shell))
                    Assert.Ignore("needs " + shell + " (the Run Script is checked under it when present)");
                Directory.CreateDirectory(Path("home"));
                Directory.CreateDirectory(Path("system"));
                Directory.CreateDirectory(Path("project"));
            }

            public string Path(string relative) => System.IO.Path.Combine(_root, relative);

            string Calls => Path("calls.txt");

            public string Stub(string relative, string version = "0.8.1", int exitCode = 0, string versionOutput = null)
            {
                string file = Path(relative);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file));
                File.WriteAllText(file, "#!/bin/sh\n[ \"$1\" = --version ] && { cat \"$0.ver\"; exit 0; }\necho \"$*\" >> \"" + Calls + "\"\nexit " + exitCode + "\n");
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                File.WriteAllText(file + ".ver", versionOutput ?? "bugsee-cli " + version + "\n");
                return file;
            }

            public ScriptRun Run(Action<System.Collections.Generic.IDictionary<string, string>> env, string script = null, bool homeUnset = false)
            {
                string dsyms = Path("Build/Products/Release-iphoneos");
                Directory.CreateDirectory(System.IO.Path.Combine(dsyms, "App.app.dSYM"));
                string file = Path("project/upload-symbols.sh");
                File.WriteAllText(file, script ?? IosArchiveUploadScript.Build());

                var info = new ProcessStartInfo(_shell, "\"" + file + "\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                };
                info.Environment.Clear();
                info.Environment["PATH"] = "/usr/bin:/bin";
                if (!homeUnset) info.Environment["HOME"] = Path("home");
                info.Environment["BUGSEE_CLI_SYSTEM_DIRS"] = Path("system");
                info.Environment["CONFIGURATION"] = "Release";
                info.Environment["BUGSEE_APP_TOKEN"] = "test-token";
                info.Environment["MARKETING_VERSION"] = "1.2";
                info.Environment["CURRENT_PROJECT_VERSION"] = "34";
                info.Environment["DWARF_DSYM_FOLDER_PATH"] = dsyms;
                info.Environment["DWARF_DSYM_FILE_NAME"] = "App.app.dSYM";
                info.Environment["BUILT_PRODUCTS_DIR"] = dsyms;
                info.Environment["BUILD_DIR"] = Path("Build/Products");
                info.Environment["PLATFORM_NAME"] = "iphoneos";
                info.Environment["PROJECT_DIR"] = Path("project");
                env?.Invoke(info.Environment);

                using (var process = Process.Start(info))
                {
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    Assert.That(process.WaitForExit(30000), Is.True, "upload script timed out");
                    return new ScriptRun
                    {
                        ExitCode = process.ExitCode,
                        Output = stdout.Result + stderr.Result,
                        Calls = File.Exists(Calls)
                            ? new System.Collections.Generic.List<string>(File.ReadAllLines(Calls))
                            : new System.Collections.Generic.List<string>(),
                    };
                }
            }

            public void Dispose()
            {
                try { Directory.Delete(_root, true); }
                catch (IOException) { }
            }
        }
    }
}
