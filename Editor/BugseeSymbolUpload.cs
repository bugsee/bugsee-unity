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

        public void OnPostprocessBuild(BuildReport report)
        {
            if (Environment.GetEnvironmentVariable("BUGSEE_NO_SYMBOL_UPLOAD") != null)
            {
                return;
            }

            if (!BugseeCliRunner.HasAppToken)
            {
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
            UploadIfFound(roots, "*mapping.txt", "proguard", version, build,
                path => BuildArtifactChoice.MappingRank(path, stem, allowGeneric), mappingExtra, identityWarning);
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
                        if (IsExcludedPath(so))
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

        static void UploadIfFound(
            List<string> roots,
            string pattern,
            string type,
            string version,
            string build,
            Func<string, int> rank,
            IList<string> extraArgs = null,
            string warnWhenFound = null)
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
                        if (IsExcludedPath(file))
                        {
                            continue;
                        }
                        // R8 writes an empty mapping when nothing was obfuscated: there is nothing to deobfuscate.
                        if (IsEmptyFile(file))
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

            if (best == null)
            {
                return;
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
                        if (IsExcludedPath(zip))
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

        static bool IsEmptyFile(string path)
        {
            try { return new FileInfo(path).Length == 0; }
            catch { return false; }
        }

        static bool IsExcludedPath(string path)
        {
            var norm = path.Replace('\\', '/');
            return norm.IndexOf("/Library/", StringComparison.OrdinalIgnoreCase) >= 0
                || norm.IndexOf("/Temp/", StringComparison.OrdinalIgnoreCase) >= 0
                || norm.IndexOf("/Bee/", StringComparison.OrdinalIgnoreCase) >= 0
                || norm.IndexOf("/obj/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

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
            // The CLI the Editor resolves now is a candidate for the script: the Xcode build phase's PATH is minimal.
            var cli = BugseeCliLocator.Resolve();
            File.WriteAllText(scriptPath, IosArchiveUploadScript.Build(cli.Found ? cli.Path : null), new UTF8Encoding(false));

            // Run it through sh: a file written from C# has no executable bit, so invoking the path directly
            // fails the whole build with "Permission denied" (Debug and Release alike, before the script's own
            // "never fail the build" handling can run). The "||" keeps a failing upload from failing the build.
            const string shellScript =
                "/bin/sh \"${PROJECT_DIR}/BugseeScripts/upload-symbols.sh\" || echo \"Bugsee: symbol upload script failed (the build continues).\"\n";
            // An "Append" export keeps the phase an earlier package version added (the bare-path one fails), and
            // AddShellScriptBuildPhase reuses a phase only when its script is identical: update it in place first.
            project.ReadFromString(XcodeShellScriptPhase.RewriteScript(project.WriteToString(), phaseName, shellScript));
            project.AddShellScriptBuildPhase(target, phaseName, "/bin/sh", shellScript);
            project.WriteToFile(projectPath);
            Debug.Log(
                "[Bugsee] Injected Xcode Run Script 'Bugsee Upload Symbols' for archive-time " +
                "dSYM + il2cpp-linemap upload (requires BUGSEE_APP_TOKEN in the Xcode/CI env).");
        }
    }
}
#endif
