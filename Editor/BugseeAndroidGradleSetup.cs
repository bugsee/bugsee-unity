using System;
using System.IO;
using Bugsee.WrapperPolicy;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Bugsee.Editor
{
    /// <summary>
    /// Wires the Bugsee Android Gradle plugin into Unity's <c>launcher</c> module.
    /// Instrumentation only runs on <c>com.android.application</c> (not unityLibrary).
    ///
    /// Plugin 4.x requires AGP ≥ 8.6 / Gradle ≥ 8.7 (Unity 6+). On Unity 2021.3 the
    /// plugin apply is skipped by default. The runtime AARs (<c>bugsee-android</c> +
    /// <c>bugsee-android-ndk</c>) are declared by <see cref="BugseeAndroidMavenDependencies"/>.
    /// </summary>
    [InitializeOnLoad]
    sealed class BugseeAndroidGradleSetup : IPreprocessBuildWithReport
    {
        public int callbackOrder => 10;

        public const string GradlePluginVersion = "4.0.8";
        public const string SdkVersion = BugseePackageVersion.AndroidSdkVersion;

        const string PrefForcePlugin = "Bugsee.Android.ForceGradlePlugin";
        const string KotlinExcludeMarker = "// Bugsee: bugsee-android Gradle .module";

        static BugseeAndroidGradleSetup()
        {
            EditorApplication.delayCall += () =>
            {
                if (!EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    EnsureProjectTemplates(silent: true);
                }
            };
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform == BuildTarget.Android)
            {
                EnsureProjectTemplates(silent: false);
            }
        }

        [MenuItem("Bugsee/Android/Ensure Gradle Plugin Templates")]
        static void MenuEnsure()
        {
            EnsureProjectTemplates(silent: false);
            Debug.Log(
                "[Bugsee] Android Gradle templates refreshed. " +
                "Enable Custom Base / Launcher Gradle Template under Player Settings → Publishing Settings if needed.");
        }

        [MenuItem("Bugsee/Android/Force Gradle Plugin On Older Unity (unsupported)")]
        static void MenuForcePlugin()
        {
            bool next = !EditorPrefs.GetBool(PrefForcePlugin, false);
            EditorPrefs.SetBool(PrefForcePlugin, next);
            Debug.Log(
                "[Bugsee] Force Gradle plugin = " + next +
                ". Plugin 4.x needs AGP 8.6+; forcing on Unity 2021.3 will likely fail Gradle configuration.");
            EnsureProjectTemplates(silent: false);
        }

        internal static void EnsureProjectTemplates(bool silent)
        {
            Directory.CreateDirectory(Path.Combine(Application.dataPath, "Plugins", "Android"));

            bool applyPlugin = ShouldApplyGradlePlugin();
            WriteBaseProjectTemplate(applyPlugin);
            WriteLauncherTemplate(applyPlugin);
            RemoveStaleKotlinStdlibExclusion();

            if (silent)
            {
                return;
            }

            if (applyPlugin)
            {
                Debug.Log(
                    $"[Bugsee] Launcher applies com.bugsee.android.gradle:{GradlePluginVersion}. " +
                    $"Runtime AAR pin {SdkVersion} with NDK enabled.");
            }
            else
            {
                Debug.LogWarning(
                    "[Bugsee] Bugsee Gradle plugin not applied (needs AGP 8.6+ / Unity 6+). " +
                    "bugsee-android + bugsee-android-ndk are still declared in the generated Gradle project.");
            }
        }

        /// <summary>
        /// 7.1.4+ (including 7.4.1) ships empty POM and Gradle .module metadata. Drop the kotlin-stdlib
        /// exclusion previously patched into mainTemplate for older .module files.
        /// </summary>
        static void RemoveStaleKotlinStdlibExclusion()
        {
            string path = Path.Combine(Application.dataPath, "Plugins", "Android", "mainTemplate.gradle");
            if (!File.Exists(path))
            {
                return;
            }

            string text = File.ReadAllText(path);
            int start = text.IndexOf(KotlinExcludeMarker, StringComparison.Ordinal);
            if (start < 0)
            {
                return;
            }

            if (start > 0 && text[start - 1] == '\n')
            {
                start--;
            }

            int brace = text.IndexOf("configurations.configureEach", start, StringComparison.Ordinal);
            if (brace < 0)
            {
                return;
            }

            int end = text.IndexOf('}', brace);
            if (end < 0)
            {
                return;
            }

            end++;
            if (end < text.Length && text[end] == '\n')
            {
                end++;
            }

            text = text.Remove(start, end - start);
            WriteIfChanged(path, text);
        }

        static bool ShouldApplyGradlePlugin()
        {
            if (EditorPrefs.GetBool(PrefForcePlugin, false))
            {
                return true;
            }

            // Unity 6 == 6000.x ships AGP 8.x, which meets plugin 4.x requirements.
            return GetUnityMajorVersion() >= 6000;
        }

        static int GetUnityMajorVersion()
        {
            string head = Application.unityVersion.Split('f')[0];
            string major = head.Split('.')[0];
            return int.TryParse(major, out int v) ? v : 0;
        }

        static void WriteBaseProjectTemplate(bool applyPlugin)
        {
            string path = Path.Combine(Application.dataPath, "Plugins", "Android", "baseProjectTemplate.gradle");
            string text = ReadOrCopyUnityDefault(path, "baseProjectTemplate.gradle");
            if (text == null)
            {
                return;
            }

            WarnIfIncompatibleAgp(path, text, "baseProjectTemplate.gradle");
            if (applyPlugin)
            {
                TryApplyMarkedGradle(
                    path,
                    "plugins {",
                    $"    id 'com.bugsee.android.gradle' version '{GradlePluginVersion}' apply false // bugsee:gradle-plugin");
            }
        }

        static void WriteLauncherTemplate(bool applyPlugin)
        {
            string path = Path.Combine(Application.dataPath, "Plugins", "Android", "launcherTemplate.gradle");
            bool existed = File.Exists(path);
            string text = ReadOrCopyUnityDefault(path, "launcherTemplate.gradle");
            if (text == null)
            {
                return;
            }

            if (!existed)
            {
                // Only for the copy made here; an existing template is the project's own.
                WriteIfChanged(path, GradleTemplateDefaults.EnsureReleaseDebugSymbols(text));
            }

            if (applyPlugin)
            {
                TryApplyMarkedGradle(
                    path,
                    "apply plugin: 'com.android.application'",
                    LauncherMarkedGradlePatch());
            }
        }

        static string UnityTemplatePath(string name)
        {
            // The module is not inside Unity.app on macOS, so ask Unity where Android Build Support lives.
            string engine = BuildPipeline.GetPlaybackEngineDirectory(BuildTarget.Android, BuildOptions.None);
            return string.IsNullOrEmpty(engine) ? null : Path.Combine(engine, "Tools", "GradleTemplates", name);
        }

        /// <summary>
        /// The project's template, or a copy of the one this Unity ships (AGP/Gradle versions and the launcher
        /// layout differ per Unity line, so the package never writes its own). Null when there is none to copy
        /// (no Android Build Support module); the Unity default is then used unmodified.
        /// </summary>
        static string ReadOrCopyUnityDefault(string path, string name)
        {
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }

            string source = UnityTemplatePath(name);
            if (source == null || !File.Exists(source))
            {
                Debug.LogWarning("[Bugsee] Unity's default " + name + " was not found (" + source + "); not creating one.");
                return null;
            }

            string text = File.ReadAllText(source);
            WriteIfChanged(path, text);
            return text;
        }

        static void WarnIfIncompatibleAgp(string path, string projectText, string name)
        {
            string source = UnityTemplatePath(name);
            if (source != null && File.Exists(source) && GradleTemplateDefaults.PinsDifferentAgpMajor(projectText, File.ReadAllText(source)))
            {
                Debug.LogWarning(
                    "[Bugsee] " + path + " pins Android Gradle plugin " + GradleTemplateDefaults.PinnedAgpMajor(projectText) +
                    ".x but Unity " + Application.unityVersion + " builds with " +
                    GradleTemplateDefaults.PinnedAgpMajor(File.ReadAllText(source)) +
                    ".x, so the Android build will fail in Gradle. Delete the custom template (Bugsee copies Unity's default) " +
                    "or update the pinned version.");
            }
        }

        internal static string LauncherMarkedGradlePatch()
        {
            return "apply plugin: 'com.bugsee.android.gradle' // bugsee:gradle-plugin\n\n" +
                   "bugsee {\n" +
                   "    // App token is supplied at runtime via Bugsee.Launch.\n" +
                   "    ndk {\n" +
                   "        enabled = true\n" +
                   "    }\n" +
                   "} // bugsee:gradle-ndk";
        }

        static void TryApplyMarkedGradle(string path, string anchorLine, string markedLine)
        {
            try
            {
                string existing = File.ReadAllText(path);
                string updated = MarkedGradleBlock.Apply(existing, anchorLine, markedLine);
                WriteIfChanged(path, updated);
            }
            catch (InvalidOperationException ex)
            {
                Debug.LogWarning(ex.Message);
            }
        }

        static void WriteIfChanged(string path, string contents)
        {
            if (File.Exists(path) && File.ReadAllText(path) == contents)
            {
                return;
            }

            File.WriteAllText(path, contents);
            string assetPath = ToAssetPath(path);
            if (assetPath.StartsWith("Assets/", StringComparison.Ordinal))
            {
                AssetDatabase.ImportAsset(assetPath);
            }
        }

        static string ToAssetPath(string absolutePath)
        {
            absolutePath = absolutePath.Replace('\\', '/');
            string data = Application.dataPath.Replace('\\', '/');
            if (absolutePath.StartsWith(data, StringComparison.Ordinal))
            {
                return "Assets" + absolutePath.Substring(data.Length);
            }

            return absolutePath;
        }
    }
}
