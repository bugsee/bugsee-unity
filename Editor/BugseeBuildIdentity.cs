#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using Bugsee.WrapperPolicy;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Bugsee.Editor
{
    /// <summary>Shared version/build resolution.</summary>
    internal static class BugseeBuildIdentity
    {
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
                    return AndroidNameSettings(report).VersionCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            return DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
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
            string code = names.VersionCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
            // What the APK carries, when the generated launcher says so plainly (split APK codes, overrides).
            var root = BugseeAndroidGradleSetup.GeneratedProjectRoot;
            var launcher = string.IsNullOrEmpty(root) ? null : BugseeAndroidMavenDependencies.FindGradleFile(Path.Combine(root, "launcher"), "build");
            try
            {
                if (launcher != null)
                {
                    GradleTemplateDefaults.ReadGradleVersion(File.ReadAllText(launcher), out var gradleName, out var gradleCode);
                    name = gradleName ?? name;
                    code = gradleCode ?? code;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Bugsee: could not read the generated launcher's version: {ex.Message}");
            }

            var failures = new List<string>();
            var appId = AndroidUploadIdentity.SelectAppAssetId(ReadAppAssetIds(report?.summary.outputPath, report == null ? DateTime.MinValue : BuildClock.StartedUtc(report.summary.buildStartedAt), failures), out var conflict);
            if (conflict != null)
            {
                Debug.LogWarning("Bugsee: " + conflict);
            }

            return AndroidUploadIdentity.Decide(
                Environment.GetEnvironmentVariable("BUGSEE_BUILD_UUID"),
                appId,
                AndroidBuildUuid.Synthesize(BugseeCliRunner.AppToken, name, code),
                conflict,
                failures.Count == 0 ? null : string.Join("; ", failures));
        }

        /// <summary>
        /// The build id asset of each APK/AAB this build produced: the output file itself, or, when Unity wrote one APK
        /// per ABI (Split APKs by target architecture), where that path is a folder holding them, every APK in it.
        /// An entry is null when that file carries no id or cannot be read; a file that cannot be read is also described in
        /// <paramref name="failures"/> (no id can then be told from "none": see <see cref="AndroidUploadIdentity.Decide"/>).
        /// </summary>
        static List<string> ReadAppAssetIds(string outputPath, DateTime buildStartedUtc, List<string> failures)
        {
            var ids = new List<string>();
            if (string.IsNullOrEmpty(outputPath))
            {
                return ids;
            }

            try
            {
                foreach (var archive in AndroidNativeLibs.BuiltArchivePaths(outputPath, buildStartedUtc))
                {
                    ids.Add(ReadAppAssetId(archive, failures));
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Bugsee: could not look for split APKs of {outputPath}: {ex.Message}");
                failures.Add("could not look for the APKs of " + outputPath + ": " + ex.Message);
            }

            return ids;
        }

        /// <summary>The id in one APK/AAB's build id asset, or null (no such asset, not an archive, unreadable).</summary>
        static string ReadAppAssetId(string path, List<string> failures)
        {
            try
            {
                using (var zip = new System.IO.Compression.ZipArchive(BugseeLongPathFile.OpenRead(path), System.IO.Compression.ZipArchiveMode.Read))
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
                var why = LongPathPolicy.Describe(path, ex.Message);
                Debug.LogWarning($"Bugsee: could not read the build id asset from {path}: {why}");
                failures.Add(Path.GetFileName(path) + ": " + why);
            }

            return null;
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
