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
                "${HOME}/Library/Caches/Bugsee/cli", "/bugsee/cli\"/*/*/bugsee-cli",
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
                Assert.That(run.Output, Does.Contain("BUGSEE_CLI_PATH"));
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

            public string Stub(string relative)
            {
                string file = Path(relative);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file));
                File.WriteAllText(file, "#!/bin/sh\necho \"$*\" >> \"" + Calls + "\"\nexit 0\n");
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                return file;
            }

            public ScriptRun Run(Action<System.Collections.Generic.IDictionary<string, string>> env, string script = null)
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
                info.Environment["HOME"] = Path("home");
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
