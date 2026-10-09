#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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

            var version = BugseeBuildIdentity.ResolveVersion();
            var build = BugseeBuildIdentity.ResolveBuildNumber(report);

            switch (report.summary.platform)
            {
                case BuildTarget.Android:
                    UploadAndroidSymbols(report, version, build);
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
            UploadIfFound(roots, "**/mapping.txt", "proguard", version, build, preferNewest: true);
            // Unity "Create symbols.zip" / Gradle native debug symbols.
            UploadSymbolsZipOrElf(report, roots, version, build);
        }

        static void UploadSymbolsZipOrElf(BuildReport report, List<string> roots, string version, string build)
        {
            string bestZip = null;
            DateTime bestZipTime = DateTime.MinValue;
            var elfDirs = new List<string>();

            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                {
                    continue;
                }

                try
                {
                    foreach (var zip in Directory.EnumerateFiles(root, "*.zip", SearchOption.AllDirectories))
                    {
                        var name = Path.GetFileName(zip);
                        if (name.IndexOf("symbols", StringComparison.OrdinalIgnoreCase) < 0
                            && name.IndexOf("native-debug", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }
                        if (IsExcludedPath(zip))
                        {
                            continue;
                        }
                        DateTime mtime;
                        try { mtime = File.GetLastWriteTimeUtc(zip); }
                        catch { mtime = DateTime.MinValue; }
                        if (bestZip == null || mtime >= bestZipTime)
                        {
                            bestZip = zip;
                            bestZipTime = mtime;
                        }
                    }

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
                var buildUuid = BugseeBuildIdentity.ResolveBuildUuid(report);
                Debug.Log($"Bugsee: uploading Android symbols zip via {BugseeCliRunner.CliPath}: {bestZip}");
                var args = new List<string>
                {
                    "debug-files", "upload", bestZip,
                    "--type", "elf",
                    "--version", version,
                    "--build", build,
                };
                if (!string.IsNullOrEmpty(buildUuid))
                {
                    args.Add("--uuid");
                    args.Add(buildUuid);
                }
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
                var exit = BugseeCliRunner.Run(new[]
                {
                    "debug-files", "upload", dir,
                    "--type", "elf",
                    "--version", version,
                    "--build", build,
                });
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
            string fileName,
            string type,
            string version,
            string build,
            bool preferNewest)
        {
            // fileName may be a plain name (mapping.txt)
            var plain = fileName.Replace("**/", "");
            string best = null;
            DateTime bestTime = DateTime.MinValue;
            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                {
                    continue;
                }
                try
                {
                    foreach (var file in Directory.EnumerateFiles(root, plain, SearchOption.AllDirectories))
                    {
                        if (IsExcludedPath(file))
                        {
                            continue;
                        }
                        DateTime mtime;
                        try { mtime = File.GetLastWriteTimeUtc(file); }
                        catch { mtime = DateTime.MinValue; }
                        if (best == null || mtime >= bestTime)
                        {
                            best = file;
                            bestTime = mtime;
                        }
                        if (!preferNewest)
                        {
                            break;
                        }
                    }
                }
                catch { /* ignore */ }
            }

            if (best == null)
            {
                return;
            }

            Debug.Log($"Bugsee: uploading {type} via {BugseeCliRunner.CliPath}: {best}");
            var exit = BugseeCliRunner.Run(new[]
            {
                "debug-files", "upload", best,
                "--type", type,
                "--version", version,
                "--build", build,
            });
            if (exit != 0)
            {
                Debug.LogWarning($"Bugsee: bugsee-cli {type} upload failed (exit {exit}) — build continues.");
            }
        }

        static List<string> CollectRoots(BuildReport report)
        {
            var roots = new List<string>();
            if (!string.IsNullOrEmpty(report.summary.outputPath))
            {
                roots.Add(report.summary.outputPath);
                var parent = Path.GetDirectoryName(report.summary.outputPath);
                if (!string.IsNullOrEmpty(parent))
                {
                    roots.Add(parent);
                }
            }
            return roots;
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
            // Duplicate phases on re-export are annoying but harmless (token-gated).

            var scriptsDir = Path.Combine(pathToBuiltProject, "BugseeScripts");
            Directory.CreateDirectory(scriptsDir);
            var scriptPath = Path.Combine(scriptsDir, "upload-symbols.sh");
            File.WriteAllText(scriptPath, BuildArchiveScript(), new UTF8Encoding(false));

            project.AddShellScriptBuildPhase(
                target,
                phaseName,
                "/bin/sh",
                "\"${PROJECT_DIR}/BugseeScripts/upload-symbols.sh\"\n");
            project.WriteToFile(projectPath);
            Debug.Log(
                "[Bugsee] Injected Xcode Run Script 'Bugsee Upload Symbols' for archive-time " +
                "dSYM + il2cpp-linemap upload (requires BUGSEE_APP_TOKEN in the Xcode/CI env).");
        }

        static string BuildArchiveScript()
        {
            // Uses DWARF_DSYM_FOLDER_PATH / DWARF_DSYM_FILE_NAME from Xcode.
            // Resolves UnityFramework UUID via dwarfdump / otool when available.
            var sb = new StringBuilder();
            sb.AppendLine("#!/bin/sh");
            sb.AppendLine("set -e");
            sb.AppendLine("# Bugsee archive-time symbol upload (dSYM + il2cpp-linemap).");
            sb.AppendLine("# Never fail the Archive — swallow CLI errors.");
            sb.AppendLine("if [ \"${CONFIGURATION}\" != \"Release\" ] && [ \"${BUGSEE_UPLOAD_ON_DEBUG}\" != \"1\" ]; then");
            sb.AppendLine("  echo \"Bugsee: skip symbol upload for ${CONFIGURATION} (set BUGSEE_UPLOAD_ON_DEBUG=1 to force).\"");
            sb.AppendLine("  exit 0");
            sb.AppendLine("fi");
            sb.AppendLine("if [ -z \"${BUGSEE_APP_TOKEN}\" ]; then");
            sb.AppendLine("  echo \"Bugsee: BUGSEE_APP_TOKEN unset — skip archive symbol upload.\"");
            sb.AppendLine("  exit 0");
            sb.AppendLine("fi");
            sb.AppendLine("CLI=\"${BUGSEE_CLI_PATH:-bugsee-cli}\"");
            sb.AppendLine("VERSION=\"${BUGSEE_VERSION:-${MARKETING_VERSION}}\"");
            sb.AppendLine("BUILD=\"${BUGSEE_BUILD:-${CURRENT_PROJECT_VERSION}}\"");
            sb.AppendLine("upload() {");
            sb.AppendLine("  \"$@\" || echo \"Bugsee: upload command failed (archive continues): $*\"");
            sb.AppendLine("}");
            sb.AppendLine("");
            sb.AppendLine("# --- dSYM ---");
            sb.AppendLine("if [ -n \"${DWARF_DSYM_FOLDER_PATH}\" ] && [ -n \"${DWARF_DSYM_FILE_NAME}\" ]; then");
            sb.AppendLine("  DSYM=\"${DWARF_DSYM_FOLDER_PATH}/${DWARF_DSYM_FILE_NAME}\"");
            sb.AppendLine("  if [ -d \"${DSYM}\" ]; then");
            sb.AppendLine("    echo \"Bugsee: uploading dSYM ${DSYM}\"");
            sb.AppendLine("    upload \"${CLI}\" debug-files upload \"${DSYM}\" --type dsym --version \"${VERSION}\" --build \"${BUILD}\"");
            sb.AppendLine("  fi");
            sb.AppendLine("fi");
            sb.AppendLine("");
            sb.AppendLine("# Also upload UnityFramework.framework.dSYM when present beside the app dSYM.");
            sb.AppendLine("if [ -n \"${DWARF_DSYM_FOLDER_PATH}\" ]; then");
            sb.AppendLine("  UF_DSYM=\"${DWARF_DSYM_FOLDER_PATH}/UnityFramework.framework.dSYM\"");
            sb.AppendLine("  if [ -d \"${UF_DSYM}\" ]; then");
            sb.AppendLine("    echo \"Bugsee: uploading UnityFramework dSYM ${UF_DSYM}\"");
            sb.AppendLine("    upload \"${CLI}\" debug-files upload \"${UF_DSYM}\" --type dsym --version \"${VERSION}\" --build \"${BUILD}\"");
            sb.AppendLine("  fi");
            sb.AppendLine("fi");
            sb.AppendLine("");
            sb.AppendLine("# --- il2cpp-linemap (UUID from UnityFramework binary / dSYM) ---");
            sb.AppendLine("LINEMAP=\"\"");
            sb.AppendLine("if [ -f \"${PROJECT_DIR}/BugseePendingIl2CppLinemap.txt\" ]; then");
            sb.AppendLine("  LINEMAP=$(head -n1 \"${PROJECT_DIR}/BugseePendingIl2CppLinemap.txt\")");
            sb.AppendLine("fi");
            sb.AppendLine("if [ -z \"${LINEMAP}\" ] || [ ! -f \"${LINEMAP}\" ]; then");
            sb.AppendLine("  LINEMAP=$(find \"${PROJECT_DIR}\" -name LineNumberMappings.json 2>/dev/null | head -n1 || true)");
            sb.AppendLine("fi");
            sb.AppendLine("UUID=\"${BUGSEE_IL2CPP_UUIDS:-}\"");
            sb.AppendLine("if [ -z \"${UUID}\" ]; then");
            sb.AppendLine("  UF_BIN=\"${BUILT_PRODUCTS_DIR}/UnityFramework.framework/UnityFramework\"");
            sb.AppendLine("  if [ ! -f \"${UF_BIN}\" ]; then");
            sb.AppendLine("    UF_BIN=$(find \"${BUILT_PRODUCTS_DIR}\" -path '*/UnityFramework.framework/UnityFramework' 2>/dev/null | head -n1 || true)");
            sb.AppendLine("  fi");
            sb.AppendLine("  if [ -f \"${UF_BIN}\" ]; then");
            sb.AppendLine("    if command -v dwarfdump >/dev/null 2>&1; then");
            sb.AppendLine("      UUID=$(dwarfdump -u \"${UF_BIN}\" 2>/dev/null | sed -n 's/.*UUID: \\([^ ]*\\).*/\\1/p' | head -n1 | tr '[:upper:]' '[:lower:]' || true)");
            sb.AppendLine("    fi");
            sb.AppendLine("    if [ -z \"${UUID}\" ] && command -v otool >/dev/null 2>&1; then");
            sb.AppendLine("      UUID=$(otool -l \"${UF_BIN}\" 2>/dev/null | awk '/uuid/ {print tolower($2); exit}' || true)");
            sb.AppendLine("    fi");
            sb.AppendLine("  fi");
            sb.AppendLine("fi");
            sb.AppendLine("if [ -n \"${LINEMAP}\" ] && [ -f \"${LINEMAP}\" ] && [ -n \"${UUID}\" ]; then");
            sb.AppendLine("  echo \"Bugsee: uploading il2cpp-linemap ${LINEMAP} uuid=${UUID}\"");
            sb.AppendLine("  upload \"${CLI}\" debug-files upload \"${LINEMAP}\" --type il2cpp-linemap --version \"${VERSION}\" --build \"${BUILD}\" --uuid \"${UUID}\"");
            sb.AppendLine("else");
            sb.AppendLine("  echo \"Bugsee: skip il2cpp-linemap (map='${LINEMAP}' uuid='${UUID}')\"");
            sb.AppendLine("fi");
            sb.AppendLine("exit 0");
            return sb.ToString();
        }
    }
}
#endif
