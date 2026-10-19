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
            Assert.That(run.Output, Does.Contain("Bugsee: upload command failed (archive continues)"));
            Assert.That(run.ExitCode, Is.EqualTo(0));
        }

        [Test]
        public void Archive_script_uploads_nothing_without_a_token()
        {
            var run = RunScript(new[] { "BugseeMiniGame.app.dSYM" }, token: "");

            Assert.That(run.Calls, Is.Empty);
            Assert.That(run.Output, Does.Contain("BUGSEE_APP_TOKEN unset"));
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

        // The il2cpp-linemap an export's Run Script uploads must be that export's. An "Append" export keeps the earlier
        // one's files and Unity leaves its LineNumberMappings.json when the new build wrote none: it would be uploaded
        // under the new UnityFramework's UUID.
        const long ExportStart = 1_800_000_000;
        const string Uuid = "8666342e-cda1-39c3-b36a-6763ee6a0b24";

        static string PlantMap(string root, string relative, long mtime)
        {
            var map = Path.Combine(root, "project", relative);
            Directory.CreateDirectory(Path.GetDirectoryName(map));
            File.WriteAllText(map, "{}");
            File.SetLastWriteTimeUtc(map, DateTimeOffset.FromUnixTimeSeconds(mtime).UtcDateTime);
            return map;
        }

        static void PlantPending(string root, string map) =>
            File.WriteAllText(Path.Combine(root, "project", "BugseePendingIl2CppLinemap.txt"), map + "\n");

        const string SymbolsMap = "Il2CppOutputProject/Source/il2cppOutput/Symbols/LineNumberMappings.json";

        static List<string> Linemaps(ScriptRun run) => run.Calls.Where(c => c.Contains("--type il2cpp-linemap")).ToList();

        [Test]
        public void Archive_script_does_not_upload_a_line_map_older_than_the_export()
        {
            var run = RunScript(new[] { "BugseeMiniGame.app.dSYM" }, exportStart: ExportStart, uuids: Uuid, plant: root =>
            {
                var old = PlantMap(root, SymbolsMap, ExportStart - 3600);
                PlantPending(root, old);
            });

            Assert.That(Linemaps(run), Is.Empty, run.Output);
            Assert.That(run.Output, Does.Contain("skip il2cpp-linemap"));
            Assert.That(run.Output, Does.Contain("older than this export"));
            Assert.That(run.Output, Does.Contain("LineNumberMappings.json"));
            Assert.That(run.ExitCode, Is.EqualTo(0));
        }

        [Test]
        public void Archive_script_uploads_a_line_map_written_by_the_export()
        {
            var run = RunScript(new[] { "BugseeMiniGame.app.dSYM" }, exportStart: ExportStart, uuids: Uuid, plant: root =>
                PlantPending(root, PlantMap(root, SymbolsMap, ExportStart + 20)));

            var maps = Linemaps(run);
            Assert.That(maps, Has.Count.EqualTo(1), run.Output);
            Assert.That(maps[0], Does.Contain("/project/" + SymbolsMap + " --type il2cpp-linemap"));
            Assert.That(maps[0], Does.EndWith("--uuid " + Uuid));
        }

        // The pointer names a stale file but this export wrote another: the one under the exported project wins.
        [Test]
        public void Archive_script_passes_over_a_stale_pointer_for_the_exports_own_map()
        {
            var run = RunScript(new[] { "BugseeMiniGame.app.dSYM" }, exportStart: ExportStart, uuids: Uuid, plant: root =>
            {
                PlantPending(root, PlantMap(root, "Old/LineNumberMappings.json", ExportStart - 86400));
                PlantMap(root, SymbolsMap, ExportStart + 5);
            });

            var maps = Linemaps(run);
            Assert.That(maps, Has.Count.EqualTo(1), run.Output);
            Assert.That(maps[0], Does.Contain("/project/" + SymbolsMap + " --type il2cpp-linemap"));
        }

        // Without the pointer (the Editor had no token) the project is searched, newest-at-export only.
        [Test]
        public void Archive_script_searches_the_project_for_a_current_map_and_never_a_stale_one()
        {
            var stale = RunScript(new[] { "BugseeMiniGame.app.dSYM" }, exportStart: ExportStart, uuids: Uuid,
                plant: root => PlantMap(root, SymbolsMap, ExportStart - 3600));
            Assert.That(Linemaps(stale), Is.Empty, stale.Output);

            var current = RunScript(new[] { "BugseeMiniGame.app.dSYM" }, exportStart: ExportStart, uuids: Uuid,
                plant: root => PlantMap(root, SymbolsMap, ExportStart));
            Assert.That(Linemaps(current), Has.Count.EqualTo(1), current.Output);
        }

        // BUGSEE_IL2CPP_MAPPING at export time: the user's own file, outside the project, as old as they like.
        [Test]
        public void Archive_script_uses_a_map_outside_the_project_as_given()
        {
            var run = RunScript(new[] { "BugseeMiniGame.app.dSYM" }, exportStart: ExportStart, uuids: Uuid, plant: root =>
            {
                var own = Path.Combine(root, "mine", "LineNumberMappings.json");
                Directory.CreateDirectory(Path.GetDirectoryName(own));
                File.WriteAllText(own, "{}");
                File.SetLastWriteTimeUtc(own, DateTimeOffset.FromUnixTimeSeconds(ExportStart - 86400).UtcDateTime);
                PlantPending(root, own);
            });

            var maps = Linemaps(run);
            Assert.That(maps, Has.Count.EqualTo(1), run.Output);
            Assert.That(maps[0], Does.Contain("/mine/LineNumberMappings.json --type il2cpp-linemap"));
        }

        // The export's own map is named relative to it, so a copy of the export uploads its own map.
        [Test]
        public void Archive_script_resolves_a_relative_pointer_inside_the_project()
        {
            var run = RunScript(new[] { "BugseeMiniGame.app.dSYM" }, exportStart: ExportStart, uuids: Uuid, plant: root =>
            {
                PlantMap(root, SymbolsMap, ExportStart + 5);
                File.WriteAllText(Path.Combine(root, "project", "BugseePendingIl2CppLinemap.txt"), SymbolsMap + "\n");
            });

            var maps = Linemaps(run);
            Assert.That(maps, Has.Count.EqualTo(1), run.Output);
            Assert.That(maps[0], Does.Contain("/project/" + SymbolsMap + " --type il2cpp-linemap"));
        }

        // A file server's clock may run a little behind the build machine's.
        [Test]
        public void Archive_script_allows_two_minutes_of_clock_skew()
        {
            var run = RunScript(new[] { "BugseeMiniGame.app.dSYM" }, exportStart: ExportStart, uuids: Uuid,
                plant: root => PlantPending(root, PlantMap(root, SymbolsMap, ExportStart - 100)));
            Assert.That(Linemaps(run), Has.Count.EqualTo(1), run.Output);
        }

        // /tmp vs /private/tmp: a pointer through a link is still a path inside the project, so the freshness check applies.
        [Test]
        public void Archive_script_decides_inside_the_project_by_resolved_path()
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT) Assert.Ignore("needs symbolic links");
            var run = RunScript(new[] { "BugseeMiniGame.app.dSYM" }, exportStart: ExportStart, uuids: Uuid, plant: root =>
            {
                var stale = PlantMap(root, SymbolsMap, ExportStart - 3600);
                var link = Path.Combine(root, "link");
                Directory.CreateSymbolicLink(link, Path.Combine(root, "project"));
                PlantPending(root, Path.Combine(link, SymbolsMap));
            });

            Assert.That(Linemaps(run), Is.Empty, run.Output);
        }

        // Folder names with glob characters are matched literally.
        [Test]
        public void Archive_script_does_not_expand_globs_in_found_paths()
        {
            var run = RunScript(new[] { "BugseeMiniGame.app.dSYM" }, exportStart: ExportStart, uuids: Uuid, plant: root =>
            {
                PlantMap(root, "Il2CppOutputProject/a*b/LineNumberMappings.json", ExportStart - 3600);
                PlantMap(root, "Il2CppOutputProject/aXb/LineNumberMappings.json", ExportStart + 10);
            });

            var maps = Linemaps(run);
            Assert.That(maps, Has.Count.EqualTo(1), run.Output);
            Assert.That(maps[0], Does.Contain("/aXb/LineNumberMappings.json"));
        }

        // A script written without the export time (an unknown one) keeps the earlier behaviour.
        [Test]
        public void Archive_script_without_an_export_time_accepts_any_map_in_the_project()
        {
            var run = RunScript(new[] { "BugseeMiniGame.app.dSYM" }, uuids: Uuid,
                plant: root => PlantMap(root, SymbolsMap, ExportStart - 86400));

            Assert.That(Linemaps(run), Has.Count.EqualTo(1), run.Output);
        }

        [Test]
        public void Export_start_is_baked_in_only_when_known()
        {
            Assert.That(IosArchiveUploadScript.Build(), Does.Contain("EXPORT_START=''\n"));
            Assert.That(IosArchiveUploadScript.Build(null, 1_800_000_000), Does.Contain("EXPORT_START='1800000000'\n"));
        }

        static ScriptRun RunScript(string[] dsymBundles, int cliExitCode = 0, string token = "test-token",
            string platform = "iphoneos", Action<string> plant = null, long exportStart = 0, string uuids = null)
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
                File.WriteAllText(script, IosArchiveUploadScript.Build(null, exportStart));

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
                if (uuids != null) info.Environment["BUGSEE_IL2CPP_UUIDS"] = uuids;
                else info.Environment.Remove("BUGSEE_IL2CPP_UUIDS");

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
