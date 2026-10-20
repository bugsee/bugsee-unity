using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class IosSymbolUploadGateTests
    {
        // A crash from a Debug (Development) build is unsymbolicated without its dSYM, so no configuration may be
        // skipped unless the build environment asks.
        [Test]
        public void Archive_script_uploads_every_configuration_unless_debug_is_opted_out()
        {
            string text = IosArchiveUploadScript.Build();
            Assert.That(text, Does.Not.Contain("!= \"Release\""));
            Assert.That(text, Does.Not.Contain("BUGSEE_UPLOAD_ON_DEBUG"));
            Assert.That(text, Does.Contain("${BUGSEE_SKIP_UPLOAD_ON_DEBUG}"));
            Assert.That(text, Does.Contain("${CONFIGURATION}\" = \"Debug\""));
        }

        // Unity's Debug configuration writes no dSYM, so an upload there would otherwise end silently with nothing sent.
        [Test]
        public void Archive_script_warns_when_the_build_has_no_dsym()
        {
            string text = IosArchiveUploadScript.Build();
            Assert.That(text, Does.Contain(
                "if [ ! -d \"${DWARF_DSYM_FOLDER_PATH}/${DWARF_DSYM_FILE_NAME}\" ] && [ ! -d \"${DWARF_DSYM_FOLDER_PATH}/UnityFramework.framework.dSYM\" ]; then"));
            Assert.That(text, Does.Contain("echo \"warning: Bugsee: no dSYM for ${CONFIGURATION}"));
        }

        [Test]
        public void Archive_script_has_unix_line_endings()
        {
            Assert.That(IosArchiveUploadScript.Build(), Does.Not.Contain("\r"));
        }

        // Xcode copies the dSYMs shipped in embedded xcframeworks (Bugsee.framework.dSYM) next to the app's. Without
        // that one a crash with a Bugsee frame on the stack stays missing_sym, so every dSYM there is uploaded.
        [Test]
        public void Archive_script_uploads_every_dsym_in_the_dsym_folder()
        {
            var run = RunScript(new[] { "BugseeMiniGame.app.dSYM", "UnityFramework.framework.dSYM", "Bugsee.framework.dSYM" });

            var dsyms = run.Calls.Where(c => c.Contains("--type dsym")).ToList();
            Assert.That(dsyms, Has.Count.EqualTo(3), run.Output);
            foreach (var name in new[] { "BugseeMiniGame.app.dSYM", "UnityFramework.framework.dSYM", "Bugsee.framework.dSYM" })
                Assert.That(dsyms, Has.One.Contains("/" + name + " --type dsym --version 1.2 --build 34"), run.Output);
            Assert.That(run.Output, Does.Not.Contain("warning: Bugsee: no dSYM"));
            Assert.That(run.ExitCode, Is.EqualTo(0));
        }

        // Debug with DEBUG_INFORMATION_FORMAT=dwarf: only the xcframework's dSYM is there. It is still uploaded, and
        // the warning about the app's missing dSYM still shows.
        [Test]
        public void Archive_script_uploads_the_sdk_dsym_and_warns_when_the_app_has_none()
        {
            var run = RunScript(new[] { "Bugsee.framework.dSYM" });

            var dsyms = run.Calls.Where(c => c.Contains("--type dsym")).ToList();
            Assert.That(dsyms, Has.Count.EqualTo(1), run.Output);
            Assert.That(dsyms[0], Does.Contain("/Bugsee.framework.dSYM --type dsym"));
            Assert.That(run.Output, Does.Contain("warning: Bugsee: no dSYM for Debug"));
            Assert.That(run.ExitCode, Is.EqualTo(0));
        }

        [Test]
        public void Archive_script_never_fails_the_build_when_the_cli_fails()
        {
            var run = RunScript(new[] { "BugseeMiniGame.app.dSYM", "Bugsee.framework.dSYM" }, cliExitCode: 3);

            Assert.That(run.Calls.Count(c => c.Contains("--type dsym")), Is.EqualTo(2), run.Output);
            Assert.That(run.Output, Does.Contain("warning: Bugsee: symbol upload failed (exit 3)"));
            Assert.That(run.ExitCode, Is.EqualTo(0));
        }

        // The CLI's exit-code contract in words, as warning: lines (Xcode lists those; error: would fail the build).
        [TestCase(1, "bugsee-cli hit an unexpected error")]
        [TestCase(2, "rejected its arguments")]
        [TestCase(21, "the server rejected the app token")]
        [TestCase(30, "server returned an error")]
        [TestCase(31, "could not be reached (network)")]
        [TestCase(127, "bugsee-cli could not be run")]
        [TestCase(99, "bugsee-cli failed")]
        public void Archive_script_warns_with_the_reason_for_each_cli_failure(int code, string reason)
        {
            var run = RunScript(new[] { "BugseeMiniGame.app.dSYM" }, cliExitCode: code);

            var lines = run.Output.Split('\n').Where(l => l.StartsWith("warning: Bugsee: symbol upload failed (exit " + code + ")")).ToList();
            Assert.That(lines, Is.Not.Empty, run.Output);
            Assert.That(lines[0], Does.Contain(reason));
            Assert.That(lines[0], Does.Contain("BugseeMiniGame.app.dSYM"));
            Assert.That(run.Output, Does.Contain("warning: Bugsee: some symbols were NOT uploaded"));
            Assert.That(run.Output, Does.Not.Contain("error:"));
            Assert.That(run.ExitCode, Is.EqualTo(0));
        }

        [Test]
        public void Archive_script_has_no_failure_warning_when_every_upload_succeeds()
        {
            var run = RunScript(new[] { "BugseeMiniGame.app.dSYM" });

            Assert.That(run.Output, Does.Not.Contain("symbol upload failed"));
            Assert.That(run.Output, Does.Not.Contain("NOT uploaded"));
            Assert.That(run.ExitCode, Is.EqualTo(0));
        }

        // No dSYM at all still gets the no-dSYM warning, and the build goes on.
        [Test]
        public void Archive_script_warns_and_exits_zero_with_no_dsyms_at_all()
        {
            var run = RunScript(new string[0]);

            Assert.That(run.Output, Does.Contain("warning: Bugsee: no dSYM for Debug"));
            Assert.That(run.ExitCode, Is.EqualTo(0));
        }

        [Test]
        public void Archive_script_uploads_nothing_without_a_token()
        {
            var run = RunScript(new[] { "BugseeMiniGame.app.dSYM" }, token: "");

            Assert.That(run.Calls, Is.Empty);
            var warning = run.Output.Split('\n').Single(l => l.Contains("BUGSEE_APP_TOKEN unset"));
            Assert.That(warning, Does.StartWith("warning: Bugsee: "));
            Assert.That(warning, Does.Contain("build setting"));
            Assert.That(run.ExitCode, Is.EqualTo(0));
        }

        class ScriptRun
        {
            public int ExitCode;
            public string Output;
            public List<string> Calls;
        }

        // The SPM checkout of the SDK as Xcode lays it out: every slice of the xcframework has its own dSYM.
        static readonly string[] Slices = { "ios-arm64", "ios-arm64_x86_64-simulator", "tvos-arm64" };

        static void PlantXcframework(string parent)
        {
            foreach (var slice in Slices)
                Directory.CreateDirectory(Path.Combine(parent, "Bugsee.xcframework", slice, "dSYMs", "Bugsee.framework.dSYM",
                    "Contents", "Resources", "DWARF"));
        }

        // Should Xcode not copy the SDK's dSYM next to the app's, it is taken from the remote SPM checkout in
        // DerivedData, in the slice of the platform being built.
        [TestCase("iphoneos", "ios-arm64")]
        [TestCase("iphonesimulator", "ios-arm64_x86_64-simulator")]
        public void Archive_script_falls_back_to_the_sdk_dsym_in_the_spm_checkout(string platform, string slice)
        {
            var run = RunScript(new[] { "BugseeMiniGame.app.dSYM" }, platform: platform,
                plant: root => PlantXcframework(Path.Combine(root, "SourcePackages", "artifacts", "spm", "Bugsee")));

            var dsyms = run.Calls.Where(c => c.Contains("--type dsym")).ToList();
            Assert.That(dsyms, Has.Count.EqualTo(2), run.Output);
            Assert.That(dsyms, Has.One.Contains(
                "/SourcePackages/artifacts/spm/Bugsee/Bugsee.xcframework/" + slice + "/dSYMs/Bugsee.framework.dSYM --type dsym"), run.Output);
        }

        // A local package: the xcframework inside the exported Xcode project.
        [Test]
        public void Archive_script_falls_back_to_the_sdk_dsym_in_a_local_package()
        {
            var run = RunScript(new string[0], plant: root => PlantXcframework(Path.Combine(root, "project", "Bugsee")));

            var dsyms = run.Calls.Where(c => c.Contains("--type dsym")).ToList();
            Assert.That(dsyms, Has.Count.EqualTo(1), run.Output);
            Assert.That(dsyms[0], Does.Contain("/project/Bugsee/Bugsee.xcframework/ios-arm64/dSYMs/Bugsee.framework.dSYM --type dsym"));
        }

        // The layout Xcode produces (seen on device builds and an archive): the copy next to the app's dSYM is the
        // one uploaded, and the xcframework's is not sent a second time.
        [Test]
        public void Archive_script_uploads_the_sdk_dsym_once_when_xcode_copied_it()
        {
            var run = RunScript(new[] { "BugseeMiniGame.app.dSYM", "Bugsee.framework.dSYM" },
                plant: root => PlantXcframework(Path.Combine(root, "SourcePackages", "artifacts", "spm", "Bugsee")));

            var dsyms = run.Calls.Where(c => c.Contains("Bugsee.framework.dSYM")).ToList();
            Assert.That(dsyms, Has.Count.EqualTo(1), run.Output);
            Assert.That(dsyms[0], Does.Contain("/Build/Products/Debug-iphoneos/Bugsee.framework.dSYM --type dsym"));
        }

        static ScriptRun RunScript(string[] dsymBundles, int cliExitCode = 0, string token = "test-token",
            string platform = "iphoneos", Action<string> plant = null)
        {
            if (!File.Exists("/bin/sh"))
                Assert.Ignore("needs /bin/sh");

            var root = Path.Combine(Path.GetTempPath(), "bugsee-upload-script-" + Guid.NewGuid().ToString("N"));
            try
            {
                var dsymDir = Path.Combine(root, "Build", "Products", "Debug-iphoneos");
                var projectDir = Path.Combine(root, "project");
                Directory.CreateDirectory(dsymDir);
                Directory.CreateDirectory(projectDir);
                foreach (var bundle in dsymBundles)
                    Directory.CreateDirectory(Path.Combine(dsymDir, bundle, "Contents", "Resources", "DWARF"));
                plant?.Invoke(root);

                var calls = Path.Combine(root, "calls.txt");
                var cli = Path.Combine(root, "fake-cli.sh");
                File.WriteAllText(cli, "#!/bin/sh\n[ \"$1\" = --version ] && { echo 'bugsee-cli 0.8.1'; exit 0; }\necho \"$*\" >> \"" + calls + "\"\nexit " + cliExitCode + "\n");
                File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                var script = Path.Combine(projectDir, "upload-symbols.sh");
                File.WriteAllText(script, IosArchiveUploadScript.Build());

                var info = new ProcessStartInfo("/bin/sh", "\"" + script + "\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                info.Environment["CONFIGURATION"] = "Debug";
                info.Environment["BUGSEE_APP_TOKEN"] = token;
                info.Environment["BUGSEE_CLI_PATH"] = cli;
                info.Environment["MARKETING_VERSION"] = "1.2";
                info.Environment["CURRENT_PROJECT_VERSION"] = "34";
                info.Environment["DWARF_DSYM_FOLDER_PATH"] = dsymDir;
                info.Environment["DWARF_DSYM_FILE_NAME"] = "BugseeMiniGame.app.dSYM";
                info.Environment["BUILT_PRODUCTS_DIR"] = dsymDir;
                info.Environment["BUILD_DIR"] = Path.Combine(root, "Build", "Products");
                info.Environment["PLATFORM_NAME"] = platform;
                info.Environment["PROJECT_DIR"] = projectDir;
                info.Environment.Remove("BUGSEE_SKIP_UPLOAD_ON_DEBUG");
                info.Environment.Remove("BUGSEE_IL2CPP_UUIDS");

                using (var process = Process.Start(info))
                {
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    Assert.That(process.WaitForExit(30000), Is.True, "upload script timed out");
                    return new ScriptRun
                    {
                        ExitCode = process.ExitCode,
                        Output = stdout.Result + stderr.Result,
                        Calls = File.Exists(calls) ? File.ReadAllLines(calls).ToList() : new List<string>(),
                    };
                }
            }
            finally
            {
                try { Directory.Delete(root, true); }
                catch (IOException) { }
            }
        }
    }
}
