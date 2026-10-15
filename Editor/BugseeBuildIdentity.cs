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
        /// The build id an Android symbol upload is keyed by: the one the built app reports. Read back from what Gradle
        /// produced (the APK/AAB's <c>assets/bugsee_build_id.properties</c>), with
        /// the id the SDK derives itself (<see cref="AndroidBuildUuid"/>) as the fallback, see
        /// <see cref="AndroidUploadIdentity"/>. BUGSEE_BUILD_UUID overrides. Per-.so GNU build-ids remain the real native
        /// symbol keys; this id is what ties a mapping to a session (and groups the ELF upload with it).
        /// </summary>
        public static UploadIdentity ResolveAndroidUploadIdentity(BuildReport report)
        {
            var names = AndroidNameSettings(report);
            string name = names.BundleVersion;
            string code = names.VersionCode.ToString();
            // What the APK carries, when the generated launcher says so plainly (split APK codes, overrides).
            var root = BugseeAndroidGradleSetup.GeneratedProjectRoot;
            var launcher = string.IsNullOrEmpty(root) ? null : Path.Combine(root, "launcher", "build.gradle");
            if (launcher != null && File.Exists(launcher))
            {
                GradleTemplateDefaults.ReadGradleVersion(File.ReadAllText(launcher), out var gradleName, out var gradleCode);
                name = gradleName ?? name;
                code = gradleCode ?? code;
            }

            return AndroidUploadIdentity.Decide(
                Environment.GetEnvironmentVariable("BUGSEE_BUILD_UUID"),
                ReadAppAssetId(report?.summary.outputPath),
                AndroidBuildUuid.Synthesize(BugseeCliRunner.AppToken, name, code));
        }

        /// <summary>The id in the APK/AAB's build id asset, or null (no such asset, not an archive, unreadable).</summary>
        static string ReadAppAssetId(string outputPath)
        {
            try
            {
                if (string.IsNullOrEmpty(outputPath) || !File.Exists(outputPath))
                {
                    return null;
                }

                using (var zip = System.IO.Compression.ZipFile.OpenRead(outputPath))
                {
                    foreach (var entry in zip.Entries)
                    {
                        if (entry.FullName == "assets/bugsee_build_id.properties" ||
                            entry.FullName == "base/assets/bugsee_build_id.properties")
                        {
                            using (var reader = new StreamReader(entry.Open()))
                            {
                                return AndroidUploadIdentity.ParseAssetProperties(reader.ReadToEnd());
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Bugsee: could not read the build id asset from {outputPath}: {ex.Message}");
            }

            return null;
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
