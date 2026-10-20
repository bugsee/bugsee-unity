#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Bugsee.WrapperPolicy;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Bugsee.Editor
{
    /// <summary>
    /// Editor/CI symbol orchestrator: Android ELF / ProGuard / symbols.zip and
    /// iOS archive-time dSYM + linemap Run Script. Never fails the Unity build.
    /// </summary>
    public sealed class BugseeSymbolUpload : IPostprocessBuildWithReport
    {
        public int callbackOrder => 910;

        // When the build that is being exported began (seconds since the Unix epoch), for the Run Script's check that the
        // line map it finds belongs to this export. Set before the gates below, which only decide about uploading.
        static long exportStartEpoch;

        public void OnPostprocessBuild(BuildReport report)
        {
            exportStartEpoch = BuildArtifactChoice.UnixSeconds(report.summary.buildStartedAt);
            if (report.summary.platform == BuildTarget.iOS || report.summary.platform == BuildTarget.tvOS)
            {
                // The Run Script's age check is safe only where the export step could compare the exported maps with this build's
                // own output and bring a valid one's time up to date (an unchanged Append export does not copy it again): see
                // BugseeIl2CppLinemapUpload's sweep, which runs before this.
                if (!BugseeIl2CppLinemapUpload.IosAgeCheckSafe)
                {
                    exportStartEpoch = 0;
                    var message = "Bugsee: the exported IL2CPP line map could not be checked against this build's own IL2CPP output " +
                                  "(not found, a link in the way, or a file's time could not be set); the Run Script uploads the map " +
                                  "it finds without an age check.";
                    if (BugseeCliRunner.HasAppToken) Debug.LogWarning(message);
                    else Debug.Log(message);
                }
            }

            var optedOut = Environment.GetEnvironmentVariable("BUGSEE_NO_SYMBOL_UPLOAD") != null;
            if (optedOut || !BugseeCliRunner.HasAppToken)
            {
                if (report.summary.platform == BuildTarget.Android)
                {
                    try
                    {
                        NoteMappingNotUploaded(report, optedOut
                            ? "BUGSEE_NO_SYMBOL_UPLOAD is set"
                            : "BUGSEE_APP_TOKEN is not set in the Unity Editor's environment");
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"Bugsee: could not check for the R8 mapping: {ex.Message}");
                    }
                }

                return;
            }

            var version = BugseeBuildIdentity.ResolveVersion(report);
            var build = BugseeBuildIdentity.ResolveBuildNumber(report);

            switch (report.summary.platform)
            {
                case BuildTarget.Android:
                    try
                    {
                        UploadAndroidSymbols(report, version, build);
                    }
                    catch (Exception ex)
                    {
                        // Whatever goes wrong here (an unreadable file, a locked folder) must not fail the build.
                        Debug.LogWarning($"Bugsee: Android symbol upload failed: {ex.Message} — build continues.");
                    }
                    break;
                case BuildTarget.iOS:
                case BuildTarget.tvOS:
                    // dSYM + linemap UUID happen at archive time (see PostProcessBuild).
                    break;
            }
        }

        [PostProcessBuild(1000)]
        public static void OnIosPostProcess(BuildTarget target, string pathToBuiltProject)
        {
            if (target != BuildTarget.iOS && target != BuildTarget.tvOS)
            {
                return;
            }

            try
            {
                InjectArchiveUploadScript(pathToBuiltProject);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Bugsee: failed injecting archive symbol upload script: {ex.Message}");
            }
        }

        static void UploadAndroidSymbols(BuildReport report, string version, string build)
        {
            var roots = CollectRoots(report);
            // Unity writes the R8 mapping beside the player as "<name>_mapping.txt" (a bare "mapping.txt" never
            // matches it). Only this build's own file is uploaded, see BuildArtifactChoice.
            var stem = OutputStem(report);
            var allowGeneric = OutputIsDirectory(report);
            // Keyed by the id the built app reports (read back from the built APK/AAB, see
            // BugseeBuildIdentity.ResolveAndroidUploadIdentity), never assumed from what the build script contained.
            var identity = BugseeBuildIdentity.ResolveAndroidUploadIdentity(report);
            // Said only when a mapping is actually uploaded under it (R8 off: nothing to match).
            var identityWarning = identity.Warning != null && identity.Source != BuildIdSource.None ? "Bugsee: " + identity.Warning : null;

            // The Gradle plugin attaches the launcher icon to the mapping (the app's icon in the dashboard).
            var icon = FindLauncherIcon();
            if (icon != null)
            {
                Debug.Log($"Bugsee: attaching launcher icon {icon} to the mapping upload.");
            }

            var mappingExtra = AndroidUploadIdentity.MappingArguments(identity.Uuid, icon);
            var seen = new List<MappingFileInfo>();
            var found = UploadIfFound(roots, "*mapping.txt", "proguard", version, build,
                path => BuildArtifactChoice.MappingRank(path, stem, allowGeneric), mappingExtra, identityWarning, seen, MappingUsable(report));
            if (found == null)
            {
                WarnMappingMissing(report, roots, stem, seen, version, build);
            }
            // Unity "Create symbols.zip" / Gradle native debug symbols.
            UploadSymbolsZipOrElf(report, roots, version, build, identity.ElfUuid ?? Guid.NewGuid().ToString("D"));
        }

        static string FindLauncherIcon()
        {
            var root = BugseeAndroidGradleSetup.GeneratedProjectRoot;
            var res = string.IsNullOrEmpty(root) ? null : Path.Combine(root, "launcher", "src", "main", "res");
            if (res == null || !Directory.Exists(res))
            {
                return null;
            }

            try
            {
                return AndroidIconPicker.Pick(Directory.EnumerateFiles(res, "app_icon.*", SearchOption.AllDirectories));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Bugsee: launcher icon scan failed under {res}: {ex.Message}");
                return null;
            }
        }

        static void UploadSymbolsZipOrElf(BuildReport report, List<string> roots, string version, string build, string buildUuid)
        {
            var bestZip = FindSymbolsZip(report);
            var elfDirs = new List<string>();

            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                {
                    continue;
                }

                try
                {
                    foreach (var so in Directory.EnumerateFiles(root, "libil2cpp.so", SearchOption.AllDirectories))
                    {
                        if (BuildPathExclusion.IsExcludedPath(so, root))
                        {
                            continue;
                        }
                        var dir = Path.GetDirectoryName(so);
                        if (!string.IsNullOrEmpty(dir) && !elfDirs.Contains(dir))
                        {
                            elfDirs.Add(dir);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Bugsee: Android symbol scan failed under {root}: {ex.Message}");
                }
            }

            if (bestZip != null)
            {
                // bugsee-cli --type elf accepts a zip of ELF objects (Unity symbols.zip layout).
                // --uuid is the BUILD_UUID correlation key (required by CLI); each .so is still
                // keyed by its own GNU build-id.
                Debug.Log($"Bugsee: uploading Android symbols zip via {BugseeCliRunner.CliPath}: {bestZip}");
                var args = AndroidUploadIdentity.ElfArguments(bestZip, version, build, buildUuid);
                var exit = BugseeCliRunner.Run(args);
                if (exit != 0)
                {
                    Debug.LogWarning(
                        $"Bugsee: bugsee-cli elf upload failed (exit {exit}) — build continues. " +
                        "Verify Unity symbols.zip layout (per-ABI folders with .so) and --uuid.");
                }
                return;
            }

            foreach (var dir in elfDirs)
            {
                Debug.Log($"Bugsee: uploading ELF folder via {BugseeCliRunner.CliPath}: {dir}");
                var exit = BugseeCliRunner.Run(AndroidUploadIdentity.ElfArguments(dir, version, build, buildUuid));
                if (exit != 0)
                {
                    Debug.LogWarning(
                        $"Bugsee: bugsee-cli elf upload failed for {dir} (exit {exit}) — build continues.");
                }
            }

            if (bestZip == null && elfDirs.Count == 0)
            {
                Debug.LogWarning(
                    "Bugsee: no Android symbols.zip / libil2cpp.so found for ELF upload. " +
                    "Enable Create symbols.zip / native debug symbols in Player Settings.");
            }
        }

        /// <summary>
        /// Uploads the best file under <paramref name="roots"/>; returns its path, or null when there is none.
        /// <paramref name="seen"/> collects every file with this build's name, whatever its size or age.
        /// </summary>
        static string UploadIfFound(
            List<string> roots,
            string pattern,
            string type,
            string version,
            string build,
            Func<string, int> rank,
            IList<string> extraArgs = null,
            string warnWhenFound = null,
            List<MappingFileInfo> seen = null,
            Func<MappingFileInfo, bool> usable = null)
        {
            var best = PickBest(roots, pattern, rank, seen, usable);
            if (best == null)
            {
                return null;
            }

            if (warnWhenFound != null)
            {
                Debug.LogWarning(warnWhenFound);
            }

            Debug.Log($"Bugsee: uploading {type} via {BugseeCliRunner.CliPath}: {best}");
            var args = new List<string>
            {
                "debug-files", "upload", best,
                "--type", type,
                "--version", version,
                "--build", build,
            };
            if (extraArgs != null)
            {
                args.AddRange(extraArgs);
            }

            var exit = BugseeCliRunner.Run(args);
            if (exit != 0)
            {
                Debug.LogWarning($"Bugsee: bugsee-cli {type} upload failed (exit {exit}) — build continues.");
            }

            return best;
        }

        static string PickBest(List<string> roots, string pattern, Func<string, int> rank, List<MappingFileInfo> seen, Func<MappingFileInfo, bool> usable = null)
        {
            string best = null;
            var bestRank = BuildArtifactChoice.Reject;
            var bestTime = DateTime.MinValue;
            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                {
                    continue;
                }
                try
                {
                    foreach (var file in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
                    {
                        if (BuildPathExclusion.IsExcludedPath(file, root))
                        {
                            continue;
                        }
                        var fileRank = rank(file);
                        if (fileRank == BuildArtifactChoice.Reject)
                        {
                            continue;
                        }
                        DateTime mtime;
                        try { mtime = File.GetLastWriteTimeUtc(file); }
                        catch { mtime = DateTime.MinValue; }
                        var info = new MappingFileInfo(file, FileLength(file), mtime);
                        seen?.Add(info);
                        // R8 writes an empty mapping when nothing was obfuscated: there is nothing to deobfuscate.
                        if (usable != null ? !usable(info) : info.Length <= 0)
                        {
                            continue;
                        }
                        if (best == null || BuildArtifactChoice.Better(fileRank, mtime, bestRank, bestTime))
                        {
                            best = file;
                            bestRank = fileRank;
                            bestTime = mtime;
                        }
                    }
                }
                catch { /* ignore */ }
            }

            return best;
        }

        static long FileLength(string path)
        {
            try { return new FileInfo(path).Length; }
            catch { return -1; }
        }

        // What counts as this build's mapping (see AndroidMappingNotice.IsUsable).
        static Func<MappingFileInfo, bool> MappingUsable(BuildReport report)
        {
            var format = OutputFormat(report);
            var started = BuildClock.StartedUtc(report.summary.buildStartedAt);
            return file => AndroidMappingNotice.IsUsable(file, format, started);
        }

        // Whether R8 ran for this build: Player Settings' Minify Release or Debug, by the build's own Development option.
        static bool MinifyIsOn(BuildReport report) =>
            AndroidMappingNotice.MinifyIsOn(
                (report.summary.options & BuildOptions.Development) != 0,
                PlayerSettings.Android.minifyRelease,
                PlayerSettings.Android.minifyDebug);

        static AndroidOutputFormat OutputFormat(BuildReport report)
        {
            if (EditorUserBuildSettings.exportAsGoogleAndroidProject)
            {
                return AndroidOutputFormat.ExportProject;
            }

            var path = report.summary.outputPath;
            // A folder that is not a split-APK folder ("Game.apk/") is an exported project, however it was asked for.
            if (OutputIsDirectory(report) && (path == null || !path.TrimEnd('/', '\\').EndsWith(".apk", StringComparison.OrdinalIgnoreCase)))
            {
                return AndroidOutputFormat.ExportProject;
            }

            return EditorUserBuildSettings.buildAppBundle || (path != null && path.EndsWith(".aab", StringComparison.OrdinalIgnoreCase))
                ? AndroidOutputFormat.Aab
                : AndroidOutputFormat.Apk;
        }

        // Minify (R8) is on and this build's mapping did not reach the upload: say so, it is why Java stacks stay obfuscated.
        static void WarnMappingMissing(BuildReport report, List<string> roots, string stem, List<MappingFileInfo> seen, string version, string build)
        {
            if (!MinifyIsOn(report))
            {
                return;
            }

            var message = AndroidMappingNotice.MissingMapping(
                true, OutputFormat(report), ExpectedMappingName(report, stem), roots, seen, BuildClock.StartedUtc(report.summary.buildStartedAt), version, build);
            if (message != null)
            {
                Debug.LogWarning(message);
            }
        }

        static string ExpectedMappingName(BuildReport report, string stem) =>
            OutputIsDirectory(report) ? "mapping.txt" : stem + "_mapping.txt";

        // The upload is off (no token, or opted out): a mapping that exists is still never uploaded, which a Minify build deserves to be told.
        static void NoteMappingNotUploaded(BuildReport report, string reason)
        {
            if (!MinifyIsOn(report))
            {
                return;
            }

            var stem = OutputStem(report);
            var allowGeneric = OutputIsDirectory(report);
            var best = PickBest(CollectRoots(report), "*mapping.txt", path => BuildArtifactChoice.MappingRank(path, stem, allowGeneric), null, MappingUsable(report));
            if (best == null)
            {
                return;
            }

            Debug.Log(AndroidMappingNotice.MappingNotUploaded(
                best, reason, BugseeBuildIdentity.ResolveVersion(report), BugseeBuildIdentity.ResolveBuildNumber(report)));
        }

        /// <summary>
        /// This build's symbols zip, or null. Shared by the upload and by the IL2CPP module-id lookup so both
        /// use the same file.
        /// </summary>
        internal static string FindSymbolsZip(BuildReport report)
        {
            var stem = OutputStem(report);
            var allowGeneric = OutputIsDirectory(report);
            // Unity puts the scripting backend in the zip name; matching it keeps the other backend's zip of the
            // same player, version and code (a reused output folder) from being chosen by modification time.
            // Version, code and backend are the build-time values, not whatever a later postprocessor set.
            var names = BugseeBuildIdentity.AndroidNameSettings(report);
            string best = null;
            var bestRank = BuildArtifactChoice.Reject;
            var bestTime = DateTime.MinValue;
            foreach (var root in CollectRoots(report))
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                {
                    continue;
                }

                try
                {
                    foreach (var zip in Directory.EnumerateFiles(root, "*.zip", SearchOption.AllDirectories))
                    {
                        if (BuildPathExclusion.IsExcludedPath(zip, root))
                        {
                            continue;
                        }
                        var rank = BuildArtifactChoice.SymbolsZipRank(
                            zip, stem, allowGeneric, names.BundleVersion, names.VersionCode, names.Backend);
                        if (rank == BuildArtifactChoice.Reject)
                        {
                            continue;
                        }
                        DateTime mtime;
                        try { mtime = File.GetLastWriteTimeUtc(zip); }
                        catch { mtime = DateTime.MinValue; }
                        if (best == null || BuildArtifactChoice.Better(rank, mtime, bestRank, bestTime))
                        {
                            best = zip;
                            bestRank = rank;
                            bestTime = mtime;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Bugsee: Android symbols zip scan failed under {root}: {ex.Message}");
                }
            }

            return best;
        }

        /// <summary>
        /// Name of the player being built ("MyGame" for MyGame.apk, "proj" for an exported project folder).
        /// Unity names the symbols zip and the R8 mapping after it, which tells this build's files apart from
        /// other builds' in the same folder.
        /// </summary>
        internal static string OutputStem(BuildReport report)
        {
            var path = report?.summary.outputPath;
            if (string.IsNullOrEmpty(path)) return null;
            if (OutputIsDirectory(report))
            {
                // Split APKs by target architecture turn "Game.apk" into a folder of that name; Unity still names the
                // symbols zip after "Game".
                var folder = Path.GetFileName(path.TrimEnd('/', '\\'));
                return folder.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) || folder.EndsWith(".aab", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetFileNameWithoutExtension(folder)
                    : folder;
            }

            return Path.GetFileNameWithoutExtension(path);
        }

        /// <summary>An exported project (Android Gradle project, Xcode folder), not a single APK/AAB file.</summary>
        internal static bool OutputIsDirectory(BuildReport report)
        {
            var path = report?.summary.outputPath;
            return !string.IsNullOrEmpty(path)
                && (Directory.Exists(path) || path.EndsWith("/", StringComparison.Ordinal) || path.EndsWith("\\", StringComparison.Ordinal));
        }

        // A single APK/AAB sits among its siblings in one folder, so that folder is searched (by exact name, see
        // BuildArtifactChoice). An exported project is a folder of its own: search only inside it, never its
        // parent, where sibling exports and unrelated builds live.
        static List<string> CollectRoots(BuildReport report)
        {
            var roots = new List<string>();
            var output = report.summary.outputPath;
            if (string.IsNullOrEmpty(output))
            {
                return roots;
            }

            if (OutputIsDirectory(report))
            {
                roots.Add(output);
                return roots;
            }

            var parent = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(parent))
            {
                roots.Add(parent);
            }

            return roots;
        }

        // The DWARF files inside the dSYMs of the app and of UnityFramework, which the script uploads. A configuration
        // without a dSYM (Debug with DEBUG_INFORMATION_FORMAT = dwarf) leaves them missing; Xcode does not mind.
        static readonly string[] DsymInputPaths =
        {
            "$(DWARF_DSYM_FOLDER_PATH)/$(DWARF_DSYM_FILE_NAME)/Contents/Resources/DWARF/$(EXECUTABLE_NAME)",
            "$(DWARF_DSYM_FOLDER_PATH)/UnityFramework.framework.dSYM/Contents/Resources/DWARF/UnityFramework",
        };

        static void InjectArchiveUploadScript(string pathToBuiltProject)
        {
            var projectPath = PBXProject.GetPBXProjectPath(pathToBuiltProject);
            var project = new PBXProject();
            project.ReadFromFile(projectPath);
            var target = project.GetUnityMainTargetGuid();

            const string phaseName = "Bugsee Upload Symbols";
            // Write a dedicated shell script and add a run-script phase.

            var scriptsDir = Path.Combine(pathToBuiltProject, "BugseeScripts");
            Directory.CreateDirectory(scriptsDir);
            var scriptPath = Path.Combine(scriptsDir, "upload-symbols.sh");
            // The CLI installed on this machine is a candidate for the script: the Xcode build phase's PATH is minimal.
            // Only for an upload that is on (token set, not opted out), and only on macOS, whose paths Xcode can use.
            string cli = null;
            if (Application.platform == RuntimePlatform.OSXEditor && BugseeCliRunner.HasAppToken
                && Environment.GetEnvironmentVariable("BUGSEE_NO_SYMBOL_UPLOAD") == null)
            {
                cli = BugseeCliLocator.FindInstalled();
                if (cli != null) cli = Path.GetFullPath(cli);
            }

            File.WriteAllText(scriptPath, IosArchiveUploadScript.Build(cli, exportStartEpoch), new UTF8Encoding(false));

            // Run it through sh: a file written from C# has no executable bit, so invoking the path directly
            // fails the whole build with "Permission denied" (Debug and Release alike, before the script's own
            // "never fail the build" handling can run). The "||" keeps a failing upload from failing the build.
            const string shellScript =
                "/bin/sh \"${PROJECT_DIR}/BugseeScripts/upload-symbols.sh\" || echo \"Bugsee: symbol upload script failed (the build continues).\"\n";
            // An "Append" export keeps the phase an earlier package version added (the bare-path one fails), and
            // AddShellScriptBuildPhase reuses a phase only when its script is identical: update it in place first.
            // The inputs and "always out of date" (below) are written as text after the phase is added: PBXProject has no API
            // for them, and a phase that carries them is not recognised as the one AddShellScriptBuildPhase would add (the
            // export would end with two), so an earlier export's are taken off first.
            project.ReadFromString(XcodeShellScriptPhase.SetInputs(
                XcodeShellScriptPhase.RewriteScript(project.WriteToString(), phaseName, shellScript), phaseName, null));
            project.AddShellScriptBuildPhase(target, phaseName, "/bin/sh", shellScript);
            // The phase reads the exported project and the build's dSYMs and uploads over the network: a sandboxed script
            // phase (ENABLE_USER_SCRIPT_SANDBOXING = YES, the default of new Xcode projects) is not allowed to, and Xcode
            // then fails it with "Operation not permitted" before the script starts. Unity leaves the setting unset (off).
            if (project.GetBuildPropertyForAnyConfig(target, "ENABLE_USER_SCRIPT_SANDBOXING") == "YES")
            {
                Debug.Log("[Bugsee] Setting ENABLE_USER_SCRIPT_SANDBOXING to NO on the Unity-iPhone target (it was YES): " +
                          "Xcode does not start a sandboxed Run Script phase here, so symbols would not be uploaded.");
            }
            project.SetBuildProperty(target, "ENABLE_USER_SCRIPT_SANDBOXING", "NO");
            project.WriteToFile(projectPath);
            // Declared inputs make Xcode run the phase after the tasks that write the app's and UnityFramework's dSYMs: a
            // phase without inputs may run beside them and find the dSYM missing or half written.
            File.WriteAllText(projectPath, XcodeShellScriptPhase.SetInputs(File.ReadAllText(projectPath), phaseName, DsymInputPaths),
                new UTF8Encoding(false));
            Debug.Log(
                "[Bugsee] Injected Xcode Run Script 'Bugsee Upload Symbols' for archive-time " +
                "dSYM + il2cpp-linemap upload (requires BUGSEE_APP_TOKEN in the Xcode/CI env).");
        }
    }
}
#endif
