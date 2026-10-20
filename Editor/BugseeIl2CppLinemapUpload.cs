#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using Bugsee.WrapperPolicy;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Bugsee.Editor
{
    /// <summary>
    /// Post-build discovery of Unity IL2CPP <c>LineNumberMappings.json</c> and
    /// upload via <c>bugsee-cli debug-files upload --type il2cpp-linemap</c>.
    ///
    /// Failure policy:
    /// - Auto-discovery miss → warn + continue (native symbols still upload).
    /// - BUGSEE_NO_IL2CPP_MAPPING or BUGSEE_NO_SYMBOL_UPLOAD set → skip silently.
    /// - BUGSEE_IL2CPP_MAPPING path set but missing → warn + skip (do not fail build).
    /// - No UUID(s) resolved → warn + skip (do not send an unkeyed blob).
    ///   On iOS this is expected at Unity export; archive-time Run Script uploads instead.
    /// - CLI non-zero exit → warn + continue (never fail the Unity build).
    /// </summary>
    public sealed class BugseeIl2CppLinemapUpload : IPostprocessBuildWithReport
    {
        public int callbackOrder => 900;

        public void OnPostprocessBuild(BuildReport report)
        {
            // Before any gate below: an "Append" export keeps the pointer of the earlier export, and it must not outlive
            // an export that finds no map of its own (it would name the earlier build's, or a folder that is gone).
            DeleteIosPendingLinemap(report);
            SweepIosIl2CppOutput(report);

            if (!SymbolUploadPolicy.LinemapUploadAllowed(
                    Environment.GetEnvironmentVariable("BUGSEE_NO_SYMBOL_UPLOAD") != null,
                    Environment.GetEnvironmentVariable("BUGSEE_NO_IL2CPP_MAPPING") != null))
            {
                return;
            }

            if (!BugseeCliRunner.HasAppToken)
            {
                // Opt-in via BUGSEE_APP_TOKEN; CI may upload separately.
                return;
            }

            var explicitPath = Environment.GetEnvironmentVariable("BUGSEE_IL2CPP_MAPPING");
            string jsonPath = null;
            if (!string.IsNullOrEmpty(explicitPath))
            {
                if (!File.Exists(explicitPath))
                {
                    Debug.LogWarning(
                        $"Bugsee: BUGSEE_IL2CPP_MAPPING set but file missing: {explicitPath} — skipping.");
                    return;
                }
                jsonPath = explicitPath;
            }
            else
            {
                jsonPath = FindLineNumberMappings(report);
                if (jsonPath == null)
                {
                    Debug.LogWarning(
                        "Bugsee: LineNumberMappings.json not found — skipping il2cpp-linemap upload. " +
                        "The package adds --emit-source-mapping --emit-method-map to the IL2CPP arguments of an IL2CPP build unless BUGSEE_NO_IL2CPP_MAPPING is set; " +
                        "check that the scripting backend is IL2CPP, or set BUGSEE_IL2CPP_MAPPING to a LineNumberMappings.json.");
                    return;
                }
            }

            var uuids = ResolveModuleUuids(report);

            if (uuids.Count == 0)
            {
                if (report.summary.platform == BuildTarget.iOS
                    || report.summary.platform == BuildTarget.tvOS)
                {
                    Debug.LogWarning(
                        "Bugsee: no UnityFramework UUID at Unity export — " +
                        "il2cpp-linemap upload deferred to Xcode archive Run Script " +
                        "(injected by BugseeSymbolUpload). Build continues.");
                    // Persist mapping path for the archive script.
                    TryWriteIosPendingLinemap(report, jsonPath);
                    return;
                }

                Debug.LogWarning(
                    "Bugsee: cannot upload il2cpp-linemap — no IL2CPP module UUID(s). " +
                    "Pass BUGSEE_IL2CPP_UUIDS or ensure libil2cpp.so is under the build output. " +
                    "Skipping (build continues).");
                return;
            }

            Upload(jsonPath, uuids, BugseeBuildIdentity.ResolveVersion(report),
                BugseeBuildIdentity.ResolveBuildNumber(report));
        }

        internal static void Upload(
            string jsonPath,
            IList<string> uuids,
            string version,
            string build)
        {
            if (string.IsNullOrEmpty(jsonPath) || uuids == null || uuids.Count == 0)
            {
                return;
            }

            Debug.Log($"Bugsee: uploading il2cpp-linemap via {BugseeCliRunner.CliPath} …");
            var exit = BugseeCliRunner.Run(new[]
            {
                "debug-files", "upload", jsonPath,
                "--type", "il2cpp-linemap",
                "--version", version,
                "--build", build,
                "--uuid", string.Join(",", uuids),
            });
            if (exit != 0)
            {
                Debug.LogWarning(
                    $"Bugsee: bugsee-cli il2cpp-linemap upload failed (exit {exit}) — build continues.");
            }
        }

        static bool IsIosFamily(BuildReport report) =>
            report.summary.platform == BuildTarget.iOS || report.summary.platform == BuildTarget.tvOS;

        static void DeleteIosPendingLinemap(BuildReport report)
        {
            if (!IsIosFamily(report))
            {
                return;
            }

            try
            {
                var outPath = report.summary.outputPath;
                if (!string.IsNullOrEmpty(outPath))
                {
                    File.Delete(Path.Combine(outPath, "BugseePendingIl2CppLinemap.txt"));
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Bugsee: failed removing the pending iOS linemap pointer of an earlier export: {ex.Message}");
            }
        }

        // An "Append" export keeps the earlier export's IL2CPP files, and Unity copies one in only when it changed: this build's
        // own map is there with the earlier export's time, and one this build did not write is left beside it (a MethodMap.tsv
        // from a build without --emit-method-map would be uploaded with this build's line map). What decides is the build's
        // own output in Library: an equal file is current and gets a fresh time, any other older than the export is removed,
        // and only when Bugsee is handling the map for this build (token set, upload not turned off, no map of the user's own).
        // Whether the Run Script may apply its age check to the exported map: set by the sweep of the export that is running
        // (false until it has run), read by BugseeSymbolUpload when it writes the script.
        internal static bool IosAgeCheckSafe;

        static void SweepIosIl2CppOutput(BuildReport report)
        {
            IosAgeCheckSafe = false;
            if (!IsIosFamily(report))
            {
                return;
            }

            try
            {
                var mayDelete = SymbolUploadPolicy.LinemapUploadAllowed(
                        Environment.GetEnvironmentVariable("BUGSEE_NO_SYMBOL_UPLOAD") != null,
                        Environment.GetEnvironmentVariable("BUGSEE_NO_IL2CPP_MAPPING") != null)
                    && BugseeCliRunner.HasAppToken
                    && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BUGSEE_IL2CPP_MAPPING"));
                var projectRoot = Path.GetDirectoryName(Application.dataPath);
                var buildSymbols = IosIl2CppOutputSweep.BuildSymbolsDirectory(
                    projectRoot, report.summary.platform == BuildTarget.tvOS ? "tvOS" : "iOS");
                var result = IosIl2CppOutputSweep.Run(
                    report.summary.outputPath, buildSymbols, report.summary.buildStartedAt, mayDelete);
                IosAgeCheckSafe = result.AgeCheckSafe;
                foreach (var action in result.Actions)
                {
                    Debug.Log($"Bugsee: IL2CPP output of the iOS export: {action} (compared with this build's own output in Library).");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Bugsee: could not check the exported IL2CPP map files against this build's: {ex.Message}");
            }
        }

        static void TryWriteIosPendingLinemap(BuildReport report, string jsonPath)
        {
            try
            {
                var outPath = report.summary.outputPath;
                if (string.IsNullOrEmpty(outPath) || !Directory.Exists(outPath))
                {
                    return;
                }
                var dest = Path.Combine(outPath, "BugseePendingIl2CppLinemap.txt");
                File.WriteAllText(dest, BuildArtifactChoice.PendingPointer(outPath, jsonPath) + "\n");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Bugsee: failed writing pending iOS linemap pointer: {ex.Message}");
            }
        }

        // Only this build's own output is searched. A single APK's IL2CPP output is in the folder Unity creates
        // beside it ("<player>_BackUpThisFolder_ButDontShipItWithYourGame"); an exported project (Xcode, Gradle)
        // is a folder of its own. Searching the parent as well found another build's file: an iOS export
        // was pointed at the Android build's LineNumberMappings.json, which would be uploaded under the iOS module
        // id. No match means no upload (with a warning), never someone else's file.
        static string FindLineNumberMappings(BuildReport report)
        {
            var roots = new List<string>();
            var outputRoot = BuildArtifactChoice.Il2CppOutputRoot(
                report.summary.outputPath, BugseeSymbolUpload.OutputIsDirectory(report));
            if (outputRoot != null)
            {
                roots.Add(outputRoot);
            }

            string bestPreferred = null;
            DateTime bestPreferredTime = DateTime.MinValue;
            string bestAny = null;
            DateTime bestAnyTime = DateTime.MinValue;
            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                {
                    continue;
                }
                try
                {
                    foreach (var file in Directory.EnumerateFiles(
                                 root, "LineNumberMappings.json", SearchOption.AllDirectories))
                    {
                        if (BuildPathExclusion.IsExcludedPath(file, root))
                        {
                            continue;
                        }

                        var norm = file.Replace('\\', '/');

                        DateTime mtime;
                        try { mtime = File.GetLastWriteTimeUtc(file); }
                        catch { mtime = DateTime.MinValue; }

                        var preferred = norm.IndexOf("Il2CppOutputProject", StringComparison.OrdinalIgnoreCase) >= 0
                            || norm.IndexOf("il2cppOutput", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (preferred)
                        {
                            if (bestPreferred == null || mtime >= bestPreferredTime)
                            {
                                bestPreferred = file;
                                bestPreferredTime = mtime;
                            }
                        }
                        else if (bestAny == null || mtime >= bestAnyTime)
                        {
                            bestAny = file;
                            bestAnyTime = mtime;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Bugsee: scan for LineNumberMappings failed under {root}: {ex.Message}");
                }
            }
            return bestPreferred ?? bestAny;
        }

        static List<string> ResolveModuleUuids(BuildReport report)
        {
            var list = new List<string>();
            var env = Environment.GetEnvironmentVariable("BUGSEE_IL2CPP_UUIDS");
            if (!string.IsNullOrEmpty(env))
            {
                foreach (var part in env.Split(','))
                {
                    var t = part.Trim();
                    if (t.Length > 0)
                    {
                        list.Add(t);
                    }
                }
                return list;
            }

            var derived = BugseeIl2CppModuleIdentity.DiscoverFromBuild(report);
            if (derived.Count > 0)
            {
                Debug.Log(
                    "Bugsee: derived IL2CPP module UUID(s) from native binary: " +
                    string.Join(", ", derived));
            }
            return derived;
        }
    }
}
#endif
