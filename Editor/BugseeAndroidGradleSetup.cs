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
    /// <see cref="BugseeAndroidMavenDependencies"/> already uses): the plugin goes into the root and the launcher build
    /// file, only when the generated root file pins AGP 8.6+ (read through <c>gradle/libs.versions.toml</c> when the root
    /// names AGP by alias). Unity generates Groovy files; a project that converted them to the Kotlin DSL is patched
    /// in Kotlin syntax. The edits are <see cref="GradlePluginInjection"/> (unit-tested); a layout it cannot edit safely
    /// is left alone with a warning that names the lines to add.
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
        const string GradlePluginMarker = "// bugsee:gradle-plugin";
        public const string SdkVersion = BugseePackageVersion.AndroidSdkVersion;

        const string PrefForcePlugin = "Bugsee.Android.ForceGradlePlugin";

        /// <summary>The generated Gradle project of the current Android build (the folder with settings.gradle), or null.</summary>
        internal static string GeneratedProjectRoot { get; private set; }

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform == BuildTarget.Android)
            {
                GeneratedProjectRoot = null;
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
                GeneratedProjectRoot = Path.GetDirectoryName(Path.GetFullPath(unityLibraryPath));
                PatchGeneratedProject(GeneratedProjectRoot);
            }
            catch (Exception ex) when (!(ex is BuildFailedException))
            {
                Debug.LogWarning("[Bugsee] Could not patch the generated Gradle project for the Bugsee Gradle plugin: " + ex.Message);
            }
        }

        static void PatchGeneratedProject(string projectRoot)
        {
            // Unity generates Groovy files; a project that converts them to the Kotlin DSL in its own post-generate step
            // leaves .kts files here (Groovy first when both exist, as in BugseeAndroidMavenDependencies).
            string rootPath = BugseeAndroidMavenDependencies.FindGradleFile(projectRoot, "build");
            string launcherPath = BugseeAndroidMavenDependencies.FindGradleFile(Path.Combine(projectRoot, "launcher"), "build");
            if (rootPath == null || launcherPath == null)
            {
                Debug.LogWarning(
                    "[Bugsee] No root or launcher build.gradle(.kts) in the generated Gradle project (" + projectRoot + "); " +
                    "the Bugsee Gradle plugin is not applied. " + GradlePluginInjection.ManualInstructions(GradleMavenInjection.GradleDsl.Groovy, GradlePluginVersion));
                return;
            }

            var rootDsl = GradleMavenInjection.DslForFile(rootPath);
            var launcherDsl = GradleMavenInjection.DslForFile(launcherPath);
            string root = File.ReadAllText(rootPath);
            string launcher = File.ReadAllText(launcherPath);
            string catalogPath = Path.Combine(projectRoot, "gradle", "libs.versions.toml");
            string catalog = File.Exists(catalogPath) ? File.ReadAllText(catalogPath) : null;

            // 2021.3's template sets no native symbol level; 6000.x sets it from Player Settings and is left alone.
            string patchedLauncher = GradleTemplateDefaults.EnsureReleaseDebugSymbols(launcher, launcherDsl);

            bool force = EditorPrefs.GetBool(PrefForcePlugin, false);
            bool wanted = force || GradleTemplateDefaults.AgpMeetsBugseePlugin(root, catalog);
            string patchedRoot = root;
            bool applied = false;
            GradlePluginInjection.PluginPatch patch = null;
            if (wanted)
            {
                patch = GradlePluginInjection.Apply(root, rootDsl, patchedLauncher, launcherDsl, GradlePluginVersion);
                patchedRoot = patch.Root;
                patchedLauncher = patch.Launcher;
                applied = patch.Applied;
                if (patch.Message != null)
                {
                    if (patch.IsWarning)
                    {
                        Debug.LogWarning("[Bugsee] " + patch.Message);
                    }
                    else
                    {
                        Debug.Log("[Bugsee] " + patch.Message);
                    }
                }

                if (applied && patch.NeedsPluginRepository)
                {
                    EnsurePluginRepositories(projectRoot);
                }
            }

            if (!applied && Environment.GetEnvironmentVariable("BUGSEE_NO_GRADLE_BUILD_ID") == null &&
                GradleTemplateDefaults.AgpSupportsBuildIdBlock(root, catalog))
            {
                if (launcherDsl == GradleMavenInjection.GradleDsl.Kotlin)
                {
                    // The block is Groovy code; it cannot be spliced into a Kotlin script.
                    Debug.Log(
                        "[Bugsee] " + launcherPath + " is a Kotlin DSL file, so the build id asset task (a Groovy script) was not added. " +
                        "The SDK derives its own build id and the mapping is uploaded under it.");
                }
                else
                {
                    patchedLauncher = GradleBuildIdBlock.Apply(patchedLauncher, out BuildIdBlockStatus blockStatus);
                    if (blockStatus == BuildIdBlockStatus.Applied)
                    {
                        Debug.Log(
                            "[Bugsee] Added the build id asset task (assets/bugsee_build_id.properties, the id of the R8 mapping) to " + launcherPath +
                            ": the part of the Bugsee Gradle plugin that works on this AGP. The upload uses whatever id the built app carries.");
                    }
                    else if (blockStatus == BuildIdBlockStatus.NoAnchor)
                    {
                        Debug.LogWarning(
                            "[Bugsee] No top-level android { } block in " + launcherPath + ", so the build id asset was not added. " +
                            "The SDK derives its own build id and the mapping is uploaded under it.");
                    }
                }
            }
            else
            {
                // Not applying it (plugin, another AGP, opt-out): the Unity version regenerates only what changed, so a
                // block an earlier build added (AGP 7.4, then a switch to 8.x) must not stay behind.
                patchedLauncher = GradleBuildIdBlock.Remove(patchedLauncher);
            }

            if (!string.Equals(root, patchedRoot, StringComparison.Ordinal))
            {
                File.WriteAllText(rootPath, patchedRoot);
            }

            if (!string.Equals(launcher, patchedLauncher, StringComparison.Ordinal))
            {
                File.WriteAllText(launcherPath, patchedLauncher);
            }

            if (applied)
            {
                Debug.Log(
                    $"[Bugsee] Launcher applies com.bugsee.android.gradle:{GradlePluginVersion}. " +
                    $"Runtime AAR pin {SdkVersion} with NDK enabled.");
            }
            else if (wanted)
            {
                // GradlePluginInjection already said why and what to add.
            }
            else if (!GradleTemplateDefaults.TryGetAgpVersion(root, catalog, out int agpMajor, out int agpMinor))
            {
                Debug.LogWarning(
                    "[Bugsee] Could not read the Android Gradle plugin version from " + rootPath + " (a version catalog other than " +
                    "gradle/libs.versions.toml, or a computed version), so the Bugsee Gradle plugin was NOT applied. " +
                    "bugsee-android + bugsee-android-ndk are still declared. If the project uses AGP 8.6+, " +
                    GradlePluginInjection.ManualInstructions(launcherDsl, GradlePluginVersion) +
                    " Or enable Bugsee > Android > Force Gradle Plugin On Older AGP (unsupported) to apply it regardless of the detected version.");
            }
            else
            {
                Debug.Log(
                    "[Bugsee] Bugsee Gradle plugin not applied: it needs AGP 8.6+ and the generated project pins " + agpMajor + "." + agpMinor +
                    ". bugsee-android + bugsee-android-ndk are still declared in the generated Gradle project.");
            }
        }

        /// <summary>The plugin resolves from Maven Central; a settings file that lists plugin repositories without it gets it added.</summary>
        static void EnsurePluginRepositories(string projectRoot)
        {
            string settingsPath = BugseeAndroidMavenDependencies.FindGradleFile(projectRoot, "settings");
            if (settingsPath == null)
            {
                return;
            }

            string settings = File.ReadAllText(settingsPath);
            var status = GradleMavenInjection.EnsurePluginRepositories(settings, out string updated);
            if (status == GradleMavenInjection.RepositoryStatus.Inserted)
            {
                File.WriteAllText(settingsPath, updated);
                Debug.Log("[Bugsee] Added mavenCentral() to the pluginManagement repositories in " + settingsPath + " so the Bugsee Gradle plugin resolves.");
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
            string unityLauncher = UnityTemplatePath("launcherTemplate.gradle");
            if (unityBase == null || !File.Exists(unityBase) || unityLauncher == null || !File.Exists(unityLauncher))
            {
                return;
            }

            string unityDir = Path.GetDirectoryName(unityBase);
            string unityBaseText = File.ReadAllText(unityBase);
            int unityAgp = GradleTemplateDefaults.PinnedAgpMajor(unityBaseText);
            string backupDir = null;

            if (File.Exists(basePath))
            {
                string text = File.ReadAllText(basePath);
                if (GradleTemplateDefaults.PinsDifferentAgpMajor(text, unityBaseText))
                {
                    int pinned = GradleTemplateDefaults.PinnedAgpMajor(text);
                    if (GradleTemplateDefaults.IsLegacyPackageBaseTemplate(text))
                    {
                        string origin = GradleTemplateDefaults.HasPackageMarker(text)
                            ? "written by an earlier Bugsee package version"
                            : "an unedited copy of Unity 2021.3's default template";
                        BackupAndDelete(basePath, ref backupDir,
                            origin + ", pins Android Gradle plugin " + pinned + ".x but Unity " + Application.unityVersion +
                            " builds with " + unityAgp + ".x, which fails in Gradle");
                    }
                    else
                    {
                        Debug.LogWarning(
                            "[Bugsee] " + basePath + " pins Android Gradle plugin " + pinned + ".x but Unity " + Application.unityVersion +
                            " builds with " + unityAgp + ".x, so Gradle will fail with \"DependencyHandler.module(java.lang.Object)\" while " +
                            "evaluating :launcher. It has edits of its own, so it is left unchanged. Delete it (and launcherTemplate.gradle) to use " +
                            "Unity's defaults, or re-create them from " + unityDir + " and re-apply your edits.");
                    }
                }
            }

            if (File.Exists(launcherPath))
            {
                string text = File.ReadAllText(launcherPath);
                if (GradleTemplateDefaults.UsesRemovedAndroidDsl(text) &&
                    !GradleTemplateDefaults.UsesRemovedAndroidDsl(File.ReadAllText(unityLauncher)))
                {
                    if (GradleTemplateDefaults.IsLegacyPackageLauncherTemplate(text))
                    {
                        BackupAndDelete(launcherPath, ref backupDir,
                            "an unedited launcher template from an earlier Bugsee package version; it uses the Unity 2021.3 Gradle DSL " +
                            "(lintOptions/aaptOptions), which this Unity's Gradle rejects");
                    }
                    else
                    {
                        Debug.LogWarning(
                            "[Bugsee] " + launcherPath + " uses the Unity 2021.3 Gradle DSL (lintOptions/aaptOptions), which Unity " +
                            Application.unityVersion + "'s Gradle rejects" +
                            (GradleTemplateDefaults.HasPackageMarker(text) ? " (it carries Bugsee plugin lines from an earlier package version but also has your edits)" : "") +
                            ". It is left unchanged. Delete it to use Unity's default, or re-create it from " + unityDir +
                            " and re-apply your edits (signing, dependencies).");
                    }
                }
            }
        }

        /// <summary>Copies the file outside Assets (Library/Bugsee/legacy-templates/&lt;timestamp&gt;) before removing it.</summary>
        static void BackupAndDelete(string path, ref string backupDir, string reason)
        {
            if (backupDir == null)
            {
                string project = Path.GetDirectoryName(Path.GetFullPath(Application.dataPath));
                backupDir = Path.Combine(project, "Library", "Bugsee", "legacy-templates", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                Directory.CreateDirectory(backupDir);
            }

            string backup = Path.Combine(backupDir, Path.GetFileName(path));
            File.Copy(path, backup, true);
            if (File.Exists(path + ".meta"))
            {
                File.Copy(path + ".meta", backup + ".meta", true);
            }

            string asset = ToAssetPath(path);
            if (!AssetDatabase.DeleteAsset(asset))
            {
                File.Delete(path);
            }

            Debug.LogWarning(
                "[Bugsee] Removed " + asset + ": " + reason + ". Unity's default template is used and the Bugsee Gradle plugin is " +
                "patched into the generated project. A copy is in " + backup + ".");
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
