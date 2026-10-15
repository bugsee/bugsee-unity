using System;
using System.IO;
using Bugsee.WrapperPolicy;
using UnityEditor;
using UnityEditor.Android;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Bugsee.Editor
{
    /// <summary>
    /// Wires the Bugsee Android Gradle plugin into Unity's <c>launcher</c> module (instrumentation only runs on
    /// <c>com.android.application</c>, not unityLibrary). Plugin 4.x needs AGP 8.6+ / Gradle 8.7+.
    ///
    /// The package creates no Gradle template. It patches the Gradle project Unity generated (the hook
    /// <see cref="BugseeAndroidMavenDependencies"/> already uses): the plugin goes into the root
    /// <c>plugins {}</c> block and the launcher <c>build.gradle</c>, only when the generated root file pins AGP 8.6+.
    /// So there is no file in Assets to go stale across Unity upgrades, and the AGP/Gradle versions are always the
    /// installed Unity's own. A project's own custom templates are patched the same way as Unity's defaults.
    /// Earlier package versions wrote hand-made AGP 7.4.2 templates into Assets/Plugins/Android; those exact files
    /// (<see cref="GradleTemplateDefaults.IsLegacyPackageBaseTemplate"/>) are removed when this Unity's default AGP
    /// is another major, since Gradle in Unity 6 cannot run them. Any other template is left alone and only warned about.
    /// The runtime AARs (<c>bugsee-android</c> + <c>bugsee-android-ndk</c>) are declared by <see cref="BugseeAndroidMavenDependencies"/>.
    /// </summary>
    sealed class BugseeAndroidGradleSetup : IPreprocessBuildWithReport, IPostGenerateGradleAndroidProject
    {
        public int callbackOrder => 10;

        public const string GradlePluginVersion = "4.0.8";
        public const string SdkVersion = BugseePackageVersion.AndroidSdkVersion;

        const string PrefForcePlugin = "Bugsee.Android.ForceGradlePlugin";

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform == BuildTarget.Android)
            {
                RemoveLegacyTemplates();
            }
        }

        [MenuItem("Bugsee/Android/Remove Legacy Gradle Templates")]
        static void MenuRemoveLegacy()
        {
            RemoveLegacyTemplates();
            Debug.Log("[Bugsee] Checked Assets/Plugins/Android for Gradle templates written by earlier package versions.");
        }

        [MenuItem("Bugsee/Android/Force Gradle Plugin On Older AGP (unsupported)")]
        static void MenuForcePlugin()
        {
            bool next = !EditorPrefs.GetBool(PrefForcePlugin, false);
            EditorPrefs.SetBool(PrefForcePlugin, next);
            Debug.Log(
                "[Bugsee] Force Gradle plugin = " + next +
                ". Plugin 4.x needs AGP 8.6+; forcing it on an older AGP will likely fail Gradle configuration.");
        }

        public void OnPostGenerateGradleAndroidProject(string unityLibraryPath)
        {
            try
            {
                PatchGeneratedProject(Path.GetDirectoryName(Path.GetFullPath(unityLibraryPath)));
            }
            catch (Exception ex) when (!(ex is BuildFailedException))
            {
                Debug.LogWarning("[Bugsee] Could not patch the generated Gradle project for the Bugsee Gradle plugin: " + ex.Message);
            }
        }

        static void PatchGeneratedProject(string projectRoot)
        {
            string rootPath = Path.Combine(projectRoot, "build.gradle");
            string launcherPath = Path.Combine(projectRoot, "launcher", "build.gradle");
            if (!File.Exists(rootPath) || !File.Exists(launcherPath))
            {
                Debug.LogWarning(
                    "[Bugsee] No Groovy root/launcher build.gradle in the generated Gradle project; " +
                    "the Bugsee Gradle plugin is not applied.");
                return;
            }

            string root = File.ReadAllText(rootPath);
            string launcher = File.ReadAllText(launcherPath);

            // 2021.3's template sets no native symbol level; 6000.x sets it from Player Settings and is left alone.
            string patchedLauncher = GradleTemplateDefaults.EnsureReleaseDebugSymbols(launcher);

            bool force = EditorPrefs.GetBool(PrefForcePlugin, false);
            bool apply = force || GradleTemplateDefaults.AgpMeetsBugseePlugin(root);
            string patchedRoot = root;
            if (apply)
            {
                patchedRoot = TryApplyMarkedGradle(
                    rootPath,
                    root,
                    "plugins {",
                    $"    id 'com.bugsee.android.gradle' version '{GradlePluginVersion}' apply false // bugsee:gradle-plugin");
                patchedLauncher = TryApplyMarkedGradle(
                    launcherPath,
                    patchedLauncher,
                    "apply plugin: 'com.android.application'",
                    LauncherMarkedGradlePatch());
            }

            if (!string.Equals(root, patchedRoot, StringComparison.Ordinal))
            {
                File.WriteAllText(rootPath, patchedRoot);
            }

            if (!string.Equals(launcher, patchedLauncher, StringComparison.Ordinal))
            {
                File.WriteAllText(launcherPath, patchedLauncher);
            }

            if (apply)
            {
                Debug.Log(
                    $"[Bugsee] Launcher applies com.bugsee.android.gradle:{GradlePluginVersion}. " +
                    $"Runtime AAR pin {SdkVersion} with NDK enabled.");
            }
            else
            {
                Debug.Log(
                    "[Bugsee] Bugsee Gradle plugin not applied: it needs AGP 8.6+ and the generated project pins " +
                    "an older or unreadable version. bugsee-android + bugsee-android-ndk are still declared in the generated Gradle project.");
            }
        }

        static string UnityTemplatePath(string name)
        {
            try
            {
                // The Android module is not inside Unity.app on macOS, so ask Unity where it lives.
                string engine = BuildPipeline.GetPlaybackEngineDirectory(BuildTarget.Android, BuildOptions.None);
                return string.IsNullOrEmpty(engine) ? null : Path.Combine(engine, "Tools", "GradleTemplates", name);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Android build time only (never on editor reload, so iOS-only projects see nothing).</summary>
        internal static void RemoveLegacyTemplates()
        {
            string dir = Path.Combine(Application.dataPath, "Plugins", "Android");
            string basePath = Path.Combine(dir, "baseProjectTemplate.gradle");
            string launcherPath = Path.Combine(dir, "launcherTemplate.gradle");
            string unityBase = UnityTemplatePath("baseProjectTemplate.gradle");
            if (!File.Exists(basePath) || unityBase == null || !File.Exists(unityBase))
            {
                return;
            }

            string projectBase = File.ReadAllText(basePath);
            string unityDefault = File.ReadAllText(unityBase);
            if (!GradleTemplateDefaults.PinsDifferentAgpMajor(projectBase, unityDefault))
            {
                return;
            }

            if (GradleTemplateDefaults.IsLegacyPackageBaseTemplate(projectBase))
            {
                DeleteTemplate(basePath, "pinned Android Gradle plugin " + GradleTemplateDefaults.PinnedAgpMajor(projectBase) +
                    ".x, but Unity " + Application.unityVersion + " builds with " + GradleTemplateDefaults.PinnedAgpMajor(unityDefault) + ".x");
                if (File.Exists(launcherPath) && GradleTemplateDefaults.IsLegacyPackageLauncherTemplate(File.ReadAllText(launcherPath)))
                {
                    DeleteTemplate(launcherPath, "uses the Gradle DSL of Unity 2021.3, which this Unity's Gradle cannot run");
                }

                return;
            }

            Debug.LogWarning(
                "[Bugsee] " + basePath + " pins Android Gradle plugin " + GradleTemplateDefaults.PinnedAgpMajor(projectBase) +
                ".x but Unity " + Application.unityVersion + " builds with " + GradleTemplateDefaults.PinnedAgpMajor(unityDefault) +
                ".x, so Gradle will fail with \"DependencyHandler.module(java.lang.Object)\" while evaluating :launcher. " +
                "This is your own template (not one written by the Bugsee package), so it is left unchanged. " +
                "Delete it and Assets/Plugins/Android/launcherTemplate.gradle to use Unity's defaults (Player Settings > Publishing Settings), " +
                "or re-create them from " + Path.GetDirectoryName(unityBase) + " and re-apply your edits.");
        }

        static void DeleteTemplate(string path, string reason)
        {
            string asset = ToAssetPath(path);
            if (!AssetDatabase.DeleteAsset(asset))
            {
                File.Delete(path);
            }

            Debug.LogWarning(
                "[Bugsee] Removed " + asset + ", written by an earlier Bugsee package version: it " + reason +
                ". Unity's default template is used and the Bugsee Gradle plugin is patched into the generated project.");
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

        static string TryApplyMarkedGradle(string path, string text, string anchorLine, string markedLine)
        {
            try
            {
                return MarkedGradleBlock.Apply(text, anchorLine, markedLine);
            }
            catch (InvalidOperationException ex)
            {
                Debug.LogWarning("[Bugsee] Could not add the Bugsee Gradle plugin to " + path + ": " + ex.Message);
                return text;
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
