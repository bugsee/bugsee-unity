#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
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
    /// - BUGSEE_NO_IL2CPP_MAPPING set → skip silently.
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
            if (Environment.GetEnvironmentVariable("BUGSEE_NO_IL2CPP_MAPPING") != null)
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
                        "Set BUGSEE_IL2CPP_MAPPING or enable IL2CPP source mapping in Player Settings.");
                    return;
                }
            }

            var uuids = ResolveModuleUuids(report);
            BugseeBuildIdentity.WriteRuntimeModuleUuidFile(uuids);

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

            Upload(jsonPath, uuids, BugseeBuildIdentity.ResolveVersion(),
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
                File.WriteAllText(dest, jsonPath + "\n");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Bugsee: failed writing pending iOS linemap pointer: {ex.Message}");
            }
        }

        static string FindLineNumberMappings(BuildReport report)
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
                        var norm = file.Replace('\\', '/');
                        if (norm.IndexOf("/Library/", StringComparison.OrdinalIgnoreCase) >= 0
                            || norm.IndexOf("/Temp/", StringComparison.OrdinalIgnoreCase) >= 0
                            || norm.IndexOf("/Bee/", StringComparison.OrdinalIgnoreCase) >= 0
                            || norm.IndexOf("/obj/", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            continue;
                        }

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
