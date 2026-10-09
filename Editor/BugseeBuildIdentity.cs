#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Bugsee.Editor
{
    /// <summary>Shared version/build resolution + runtime identity file writer.</summary>
    internal static class BugseeBuildIdentity
    {
        const string ResourcesFolder = "Assets/Resources";
        const string ModuleUuidAsset = "BugseeIl2CppModuleUuids.txt";

        public static string ResolveVersion() =>
            Environment.GetEnvironmentVariable("BUGSEE_VERSION")
            ?? PlayerSettings.bundleVersion
            ?? "0.0.0";

        public static string ResolveBuildNumber(BuildReport report)
        {
            var env = Environment.GetEnvironmentVariable("BUGSEE_BUILD");
            if (!string.IsNullOrEmpty(env))
            {
                return env;
            }

            switch (report.summary.platform)
            {
                case BuildTarget.iOS:
                case BuildTarget.tvOS:
                    if (!string.IsNullOrEmpty(PlayerSettings.iOS.buildNumber))
                    {
                        return PlayerSettings.iOS.buildNumber;
                    }
                    break;
                case BuildTarget.Android:
                    return PlayerSettings.Android.bundleVersionCode.ToString();
            }

            return DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        }

        /// <summary>
        /// Correlation BUILD_UUID for <c>bugsee-cli --type elf --uuid</c>.
        /// Per-.so GNU build-ids remain the real symbol keys.
        /// </summary>
        public static string ResolveBuildUuid(BuildReport report)
        {
            var env = Environment.GetEnvironmentVariable("BUGSEE_BUILD_UUID");
            if (!string.IsNullOrEmpty(env))
            {
                return env.Trim();
            }

            // Prefer a stable dashed UUID when BUGSEE_BUILD_UUID is unset.
            return Guid.NewGuid().ToString("D");
        }

        /// <summary>
        /// Persist discovered IL2CPP module UUID(s) so the runtime managed-exception
        /// payload can include <c>moduleUUID</c> for MethodMap / future LNM.
        /// </summary>
        public static void WriteRuntimeModuleUuidFile(System.Collections.Generic.IList<string> uuids)
        {
            if (uuids == null || uuids.Count == 0)
            {
                return;
            }

            try
            {
                if (!Directory.Exists(ResourcesFolder))
                {
                    Directory.CreateDirectory(ResourcesFolder);
                }
                var path = Path.Combine(ResourcesFolder, ModuleUuidAsset);
                File.WriteAllText(path, string.Join("\n", uuids) + "\n");
                AssetDatabase.ImportAsset(path);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Bugsee: failed writing runtime module UUID file: {ex.Message}");
            }
        }
    }
}
#endif
