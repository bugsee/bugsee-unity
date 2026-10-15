#if UNITY_EDITOR
using System;
using System.IO;
using Bugsee.WrapperPolicy;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Bugsee.Editor
{
    /// <summary>Shared version/build resolution + runtime identity file writer.</summary>
    internal static class BugseeBuildIdentity
    {
        const string ResourcesFolder = "Assets/Resources";
        const string ModuleUuidAsset = "BugseeIl2CppModuleUuids.txt";

        /// <summary>Android name settings as they were at the end of preprocess (see BugseeBuildSettingsSnapshot).</summary>
        internal static PlayerNameSettings Recorded;

        /// <summary>
        /// The version, code and backend Unity named this Android build's files with: the values recorded for this
        /// output at preprocess, or the live Player Settings when none were (BuildArtifactChoice.BuildTimeSettings).
        /// </summary>
        internal static PlayerNameSettings AndroidNameSettings(BuildReport report)
        {
            var output = report?.summary.outputPath;
            return BuildArtifactChoice.BuildTimeSettings(Recorded, output, LiveAndroidNameSettings(output));
        }

        internal static PlayerNameSettings LiveAndroidNameSettings(string outputPath) =>
            new PlayerNameSettings(
                outputPath,
                PlayerSettings.bundleVersion,
                PlayerSettings.Android.bundleVersionCode,
                PlayerSettings.GetScriptingBackend(NamedBuildTarget.Android).ToString());

        public static string ResolveVersion(BuildReport report) =>
            Environment.GetEnvironmentVariable("BUGSEE_VERSION")
            ?? (report?.summary.platform == BuildTarget.Android
                ? AndroidNameSettings(report).BundleVersion
                : PlayerSettings.bundleVersion)
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
                    return AndroidNameSettings(report).VersionCode.ToString();
            }

            return DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        }

        /// <summary>
        /// BUILD_UUID the symbol uploads are keyed by (<c>bugsee-cli --uuid</c>): BUGSEE_BUILD_UUID, else on Android the
        /// id the SDK derives itself for an app the Gradle plugin did not build (<see cref="AndroidBuildUuid"/>: token,
        /// version name and version code as the APK carries them, not BUGSEE_VERSION/BUGSEE_BUILD), else a random one. Per-.so GNU build-ids remain the real native symbol keys.
        /// </summary>
        public static string ResolveBuildUuid(BuildReport report)
        {
            var env = Environment.GetEnvironmentVariable("BUGSEE_BUILD_UUID");
            if (!string.IsNullOrEmpty(env))
            {
                return env.Trim();
            }

            if (report?.summary.platform == BuildTarget.Android)
            {
                var names = AndroidNameSettings(report);
                var synthesized = AndroidBuildUuid.Synthesize(
                    BugseeCliRunner.AppToken, names.BundleVersion, names.VersionCode.ToString());
                if (synthesized != null)
                {
                    return synthesized;
                }
            }

            // Prefer a stable dashed UUID when nothing above applies.
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

    /// <summary>
    /// Records the Android version, code and backend at the end of preprocess. Unity names the symbols zip from
    /// these during the build; a postprocess callback ordered before Bugsee's (a version-code incrementor) can
    /// change them before the upload looks for the zip. Last in preprocess so preprocess-time bumps are included.
    /// </summary>
    sealed class BugseeBuildSettingsSnapshot : IPreprocessBuildWithReport
    {
        public int callbackOrder => int.MaxValue;

        public void OnPreprocessBuild(BuildReport report)
        {
            BugseeBuildIdentity.Recorded = report.summary.platform == BuildTarget.Android
                ? BugseeBuildIdentity.LiveAndroidNameSettings(report.summary.outputPath)
                : null;
        }
    }
}
#endif
