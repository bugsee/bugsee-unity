using System;
using System.IO;
using System.Text;
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
    /// plugin apply is skipped by default. EDM deps: <c>bugsee-android</c> +
    /// <c>bugsee-android-ndk</c> 7.1.1. Gradle .module may still list kotlin-stdlib —
    /// <see cref="EnsureKotlinStdlibExclusion"/> strips it for Unity D8.
    /// </summary>
    [InitializeOnLoad]
    sealed class BugseeAndroidGradleSetup : IPreprocessBuildWithReport
    {
        public int callbackOrder => 10;

        public const string GradlePluginVersion = "4.0.5";
        public const string SdkVersion = "7.1.1";

        const string PrefForcePlugin = "Bugsee.Android.ForceGradlePlugin";
        const string Marker = "// Bugsee Gradle plugin";
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
            EnsureKotlinStdlibExclusion();

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
                    "EDM still resolves bugsee-android + bugsee-android-ndk; enable Custom Gradle templates for NDK plugin.");
            }
        }

        /// <summary>
        /// bugsee-android publishes empty POM deps but Gradle Module Metadata may still
        /// require kotlin-stdlib. Prefer .module over POM → Unity 2021.3 D8 fails.
        /// Patch mainTemplate outside the EDM resolver markers so Force Resolve keeps it.
        /// </summary>
        static void EnsureKotlinStdlibExclusion()
        {
            string path = Path.Combine(Application.dataPath, "Plugins", "Android", "mainTemplate.gradle");
            if (!File.Exists(path))
            {
                return;
            }

            string text = File.ReadAllText(path);
            if (text.Contains(KotlinExcludeMarker))
            {
                return;
            }

            const string block =
                "\n" + KotlinExcludeMarker + " still requires kotlin-stdlib 2.1\n" +
                "// (POM is empty). Unity 2021.3 D8 cannot dex it — exclude while .module lists it.\n" +
                "configurations.configureEach {\n" +
                "    exclude group: 'org.jetbrains.kotlin', module: 'kotlin-stdlib'\n" +
                "    exclude group: 'org.jetbrains.kotlin', module: 'kotlin-stdlib-jdk7'\n" +
                "    exclude group: 'org.jetbrains.kotlin', module: 'kotlin-stdlib-jdk8'\n" +
                "}\n";

            const string depsEnd = "**DEPS**}";
            int idx = text.IndexOf(depsEnd, StringComparison.Ordinal);
            if (idx >= 0)
            {
                text = text.Insert(idx + depsEnd.Length, block);
                WriteIfChanged(path, text);
                return;
            }

            // Fallback when Unity/EDM already expanded **DEPS**.
            const string resolverExclusions = "// Android Resolver Exclusions Start";
            idx = text.IndexOf(resolverExclusions, StringComparison.Ordinal);
            if (idx >= 0)
            {
                text = text.Insert(idx, block + "\n");
                WriteIfChanged(path, text);
            }
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
            var sb = new StringBuilder();
            sb.AppendLine("plugins {");
            sb.AppendLine("    // If you are changing the Android Gradle Plugin version, make sure it is compatible with the Gradle version preinstalled with Unity");
            sb.AppendLine("    // See which Gradle version is preinstalled with Unity here https://docs.unity3d.com/Manual/android-gradle-overview.html");
            sb.AppendLine("    // See official Gradle and Android Gradle Plugin compatibility table here https://developer.android.com/studio/releases/gradle-plugin#updating-gradle");
            sb.AppendLine("    // To specify a custom Gradle version in Unity, go do \"Preferences > External Tools\", uncheck \"Gradle Installed with Unity (recommended)\" and specify a path to a custom Gradle version");
            sb.AppendLine("    id 'com.android.application' version '7.4.2' apply false");
            sb.AppendLine("    id 'com.android.library' version '7.4.2' apply false");
            if (applyPlugin)
            {
                sb.AppendLine($"    {Marker}");
                sb.AppendLine($"    id 'com.bugsee.android.gradle' version '{GradlePluginVersion}' apply false");
            }

            sb.AppendLine("    **BUILD_SCRIPT_DEPS**");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("task clean(type: Delete) {");
            sb.AppendLine("    delete rootProject.buildDir");
            sb.AppendLine("}");
            sb.AppendLine();
            WriteIfChanged(path, sb.ToString());
        }

        static void WriteLauncherTemplate(bool applyPlugin)
        {
            string path = Path.Combine(Application.dataPath, "Plugins", "Android", "launcherTemplate.gradle");
            var sb = new StringBuilder();
            sb.AppendLine("apply plugin: 'com.android.application'");
            if (applyPlugin)
            {
                sb.AppendLine(Marker);
                sb.AppendLine("apply plugin: 'com.bugsee.android.gradle'");
                sb.AppendLine();
                sb.AppendLine("bugsee {");
                sb.AppendLine("    // App token is supplied at runtime via Bugsee.Launch.");
                sb.AppendLine("    ndk {");
                sb.AppendLine("        enabled = true");
                sb.AppendLine("    }");
                sb.AppendLine("}");
            }

            sb.AppendLine();
            sb.AppendLine("dependencies {");
            sb.AppendLine("    implementation project(':unityLibrary')");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("android {");
            sb.AppendLine("    namespace \"**NAMESPACE**\"");
            sb.AppendLine("    ndkPath \"**NDKPATH**\"");
            sb.AppendLine("    compileSdkVersion **APIVERSION**");
            sb.AppendLine("    buildToolsVersion '**BUILDTOOLS**'");
            sb.AppendLine();
            sb.AppendLine("    compileOptions {");
            sb.AppendLine("        sourceCompatibility JavaVersion.VERSION_11");
            sb.AppendLine("        targetCompatibility JavaVersion.VERSION_11");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    defaultConfig {");
            sb.AppendLine("        minSdkVersion **MINSDKVERSION**");
            sb.AppendLine("        targetSdkVersion **TARGETSDKVERSION**");
            sb.AppendLine("        applicationId '**APPLICATIONID**'");
            sb.AppendLine("        ndk {");
            sb.AppendLine("            abiFilters **ABIFILTERS**");
            sb.AppendLine("        }");
            sb.AppendLine("        versionCode **VERSIONCODE**");
            sb.AppendLine("        versionName '**VERSIONNAME**'");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    aaptOptions {");
            sb.AppendLine("        noCompress = **BUILTIN_NOCOMPRESS** + unityStreamingAssets.tokenize(', ')");
            sb.AppendLine("        ignoreAssetsPattern = \"!.svn:!.git:!.ds_store:!*.scc:.*:!CVS:!thumbs.db:!picasa.ini:!*~\"");
            sb.AppendLine("    }**SIGN**");
            sb.AppendLine();
            sb.AppendLine("    lintOptions {");
            sb.AppendLine("        abortOnError false");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    buildTypes {");
            sb.AppendLine("        debug {");
            sb.AppendLine("            minifyEnabled **MINIFY_DEBUG**");
            sb.AppendLine("            proguardFiles getDefaultProguardFile('proguard-android.txt')**SIGNCONFIG**");
            sb.AppendLine("            jniDebuggable true");
            sb.AppendLine("        }");
            sb.AppendLine("        release {");
            sb.AppendLine("            minifyEnabled **MINIFY_RELEASE**");
            sb.AppendLine("            proguardFiles getDefaultProguardFile('proguard-android.txt')**SIGNCONFIG**");
            sb.AppendLine("            ndk {");
            // FULL for DWARF line programs — required for IL2CPP LineNumberMappings primary apply.
            sb.AppendLine("                debugSymbolLevel 'FULL'");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine("    }**PACKAGING_OPTIONS****PLAY_ASSET_PACKS****SPLITS**");
            sb.AppendLine("**BUILT_APK_LOCATION**");
            sb.AppendLine("    bundle {");
            sb.AppendLine("        language {");
            sb.AppendLine("            enableSplit = false");
            sb.AppendLine("        }");
            sb.AppendLine("        density {");
            sb.AppendLine("            enableSplit = false");
            sb.AppendLine("        }");
            sb.AppendLine("        abi {");
            sb.AppendLine("            enableSplit = true");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}**SPLITS_VERSION_CODE****LAUNCHER_SOURCE_BUILD_SETUP**");
            sb.AppendLine();
            WriteIfChanged(path, sb.ToString());
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
