using System;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    // The Run Script runs under Xcode's minimal PATH, so it finds bugsee-cli the way the Editor locator does rather than
    // by a bare lookup. These run the generated script for real, with a fake HOME and a stub CLI planted per location.
    public class IosCliResolutionTests
    {
        [Test]
        public void Archive_script_resolves_the_cli_from_every_place_the_editor_looks()
        {
            string text = IosArchiveUploadScript.Build();
            foreach (var place in new[]
            {
                "${BUGSEE_CLI_PATH}", "${BAKED_CLI}", "try_command bugsee-cli", "/opt/homebrew/bin /usr/local/bin",
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
            using (var box = new Sandbox())
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
            using (var box = new Sandbox())
            {
                string cli = box.Stub("exported dir/bugsee-cli");
                var run = box.Run(null, IosArchiveUploadScript.Build(cli));
                Assert.That(run.Output, Does.Contain("using " + cli));
                Assert.That(run.Calls, Has.Count.GreaterThan(0), run.Output);
                Assert.That(run.ExitCode, Is.EqualTo(0));
            }
        }

        [Test]
        public void Finds_the_cli_on_PATH()
        {
            using (var box = new Sandbox())
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
            using (var box = new Sandbox())
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
            using (var box = new Sandbox())
            {
                Directory.CreateDirectory(box.Path("home/.bugsee/bin"));
                File.WriteAllText(box.Path("home/.bugsee/bin/bugsee-cli"), "");
                string good = box.Stub("home/.local/bin/bugsee-cli");
                var run = box.Run(null);
                Assert.That(run.Output, Does.Contain("using " + good));
            }
        }

        [Test]
        public void Logs_every_place_tried_and_still_exits_zero_when_there_is_no_cli()
        {
            using (var box = new Sandbox())
            {
                var run = box.Run(env => env["BUGSEE_CLI_PATH"] = "/nonexistent/bugsee-cli");
                Assert.That(run.ExitCode, Is.EqualTo(0), run.Output);
                Assert.That(run.Calls, Is.Empty);
                Assert.That(run.Output, Does.Contain("warning: Bugsee: bugsee-cli was not found, so symbols are NOT uploaded"));
                Assert.That(run.Output, Does.Contain("/nonexistent/bugsee-cli"));
                Assert.That(run.Output, Does.Contain(box.Path("home/.bugsee/bin/bugsee-cli")));
                Assert.That(run.Output, Does.Contain(box.Path("home/Library/Caches/Bugsee/cli")));
                Assert.That(run.Output, Does.Contain("/nonexistent/bugsee-cli (not found)"));
                Assert.That(run.Output, Does.Contain("bugsee-cli (not on PATH=/usr/bin:/bin)"));
            }
        }

        [Test]
        public void A_bare_BUGSEE_CLI_PATH_that_is_not_on_PATH_is_listed()
        {
            using (var box = new Sandbox())
            {
                var run = box.Run(env => env["BUGSEE_CLI_PATH"] = "my-cli");
                Assert.That(run.Output, Does.Contain("my-cli (not on PATH=/usr/bin:/bin)"));
                Assert.That(run.ExitCode, Is.EqualTo(0));
            }
        }

        [Test]
        public void Passes_over_a_cli_that_does_not_run_and_one_older_than_the_minimum()
        {
            using (var box = new Sandbox())
            {
                string broken = box.Path("home/.bugsee/bin/bugsee-cli");
                Directory.CreateDirectory(Path.GetDirectoryName(broken));
                File.WriteAllText(broken, "#!/bin/sh\nexit 3\n");
                File.SetUnixFileMode(broken, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                box.Stub("system/bugsee-cli", "0.7.9");
                string good = box.Stub("home/.local/bin/bugsee-cli", "0.10.0");
                var run = box.Run(null);
                Assert.That(run.Output, Does.Contain(broken + " (does not run)"));
                Assert.That(run.Output, Does.Contain("(version 0.7, older than 0.8.0)").Or.Contain("older than 0.8.0"));
                Assert.That(run.Output, Does.Contain("using " + good));
                Assert.That(run.Calls, Has.Count.GreaterThan(0), run.Output);
            }
        }

        [Test]
        public void Precedence_is_explicit_then_baked_then_PATH_then_install_dirs()
        {
            using (var box = new Sandbox())
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
            using (var box = new Sandbox())
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
            using (var box = new Sandbox())
            {
                box.Stub("home/.bugsee/bin/bugsee-cli", exitCode: 7);
                var run = box.Run(null);
                Assert.That(run.Output, Does.Contain("upload command failed"));
                Assert.That(run.ExitCode, Is.EqualTo(0));
            }
        }

        [Test]
        public void Runs_with_HOME_unset_and_still_logs_when_nothing_is_found()
        {
            using (var box = new Sandbox())
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
            using (var box = new Sandbox())
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
            using (var box = new Sandbox())
            {
                box.Stub("home/Library/Caches/Bugsee/cli/0.10.0/aarch64-apple-darwin/bugsee-cli", "0.10.0");
                string pinned = box.Stub("home/Library/Caches/Bugsee/cli/" + BugseeCliPolicy.DefaultVersion + "/aarch64-apple-darwin/bugsee-cli");
                var run = box.Run(null);
                Assert.That(run.Output, Does.Contain("using " + pinned));
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

            public Sandbox()
            {
                if (!File.Exists("/bin/sh"))
                    Assert.Ignore("needs /bin/sh");
                Directory.CreateDirectory(Path("home"));
                Directory.CreateDirectory(Path("system"));
                Directory.CreateDirectory(Path("project"));
            }

            public string Path(string relative) => System.IO.Path.Combine(_root, relative);

            string Calls => Path("calls.txt");

            public string Stub(string relative, string version = "0.8.1", int exitCode = 0)
            {
                string file = Path(relative);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file));
                File.WriteAllText(file, "#!/bin/sh\n[ \"$1\" = --version ] && { echo 'bugsee-cli " + version + "'; exit 0; }\necho \"$*\" >> \"" + Calls + "\"\nexit " + exitCode + "\n");
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                return file;
            }

            public ScriptRun Run(Action<System.Collections.Generic.IDictionary<string, string>> env, string script = null, bool homeUnset = false)
            {
                string dsyms = Path("Build/Products/Release-iphoneos");
                Directory.CreateDirectory(System.IO.Path.Combine(dsyms, "App.app.dSYM"));
                string file = Path("project/upload-symbols.sh");
                File.WriteAllText(file, script ?? IosArchiveUploadScript.Build());

                var info = new ProcessStartInfo("/bin/sh", "\"" + file + "\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
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
