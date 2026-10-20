#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Bugsee.Sample.Editor
{
    /// <summary>
    /// Batchmode builds for end-to-end validation:
    /// Unity -batchmode -quit -projectPath … -executeMethod Bugsee.Sample.Editor.E2EBuild.BuildAndroid
    /// Unity -batchmode -quit -projectPath … -executeMethod Bugsee.Sample.Editor.E2EBuild.BuildIos
    ///
    /// Environment:
    ///   BUGSEE_APP_TOKEN    required; baked into the build and used for symbol upload
    ///   BUGSEE_ENDPOINT     API host (no /v2); iOS gets /v2 appended
    ///   BUGSEE_E2E_NO_TOKEN 1: build as a CI without the token in the Editor environment: BUGSEE_APP_TOKEN is not needed
    ///                       (and is cleared in this process), the build gets a dummy launch token, and no upload runs
    ///   BUGSEE_E2E_BACKEND  il2cpp (default) | mono
    ///   BUGSEE_E2E_DEV      1 (default, Development build) | 0 (Release)
    ///   BUGSEE_E2E_MINIFY   0 (default) | 1: R8 minification on Android (produces mapping.txt)
    ///   BUGSEE_E2E_FORMAT   apk (default) | aab (Build App Bundle) | export (Export Project, a Gradle project folder): Android only
    ///   BUGSEE_E2E_IOS_SIM  1: build the iOS Simulator SDK (default: the device SDK)
    ///   BUGSEE_E2E_IOS_SIM_ARCH  arm64 (default on an Apple silicon Mac) | x64 | universal: the simulator slice (Unity 2022.1+);
    ///                            iOS 26+ simulators on Apple silicon run arm64 only
    ///   BUGSEE_E2E_APPEND   1: export into an existing Xcode project (BuildOptions.AcceptExternalModificationsToPlayer)
    ///   BUGSEE_E2E_SETTING_DEV  0|1: set the Build Settings "Development Build" checkbox apart from the build's own
    ///                       options (BUGSEE_E2E_DEV), as a CI script that passes explicit BuildOptions does
    ///   BUGSEE_E2E_OUT      output path override
    ///
    /// Note: the backend, architectures and minify settings are written to the project's tracked
    /// ProjectSettings.asset, so a build leaves the working tree modified.
    ///
    /// The token never lands in the scene or in git: it is written to a git-ignored
    /// Resources/BugseeE2E.json for the duration of the build and removed afterwards (also removed at the
    /// start of the next one if a killed build left it behind). It is still inside the built player's
    /// resources, as an app token is meant to be.
    /// </summary>
    public static class E2EBuild
    {
        const string OverrideAsset = "Assets/Resources/BugseeE2E.json";

        public static void BuildAndroid() => Build(BuildTarget.Android);

        public static void BuildIos() => Build(BuildTarget.iOS);

        // Kept for the existing Mode B scripts.
        public static void BuildAndroidIl2Cpp()
        {
            Environment.SetEnvironmentVariable("BUGSEE_E2E_BACKEND", "il2cpp");
            Build(BuildTarget.Android);
        }

        public static void BuildIosIl2Cpp()
        {
            Environment.SetEnvironmentVariable("BUGSEE_E2E_BACKEND", "il2cpp");
            Build(BuildTarget.iOS);
        }

        static void Build(BuildTarget target)
        {
            var token = Environment.GetEnvironmentVariable("BUGSEE_APP_TOKEN");
            if (Environment.GetEnvironmentVariable("BUGSEE_E2E_NO_TOKEN") == "1")
            {
                // The package reads BUGSEE_APP_TOKEN from the Editor's environment: clear it so the build is token-less.
                Environment.SetEnvironmentVariable("BUGSEE_APP_TOKEN", null);
                token = "e2e-no-token-dummy-launch-token";
            }

            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("[E2EBuild] Set BUGSEE_APP_TOKEN (baked into the build, used for symbol upload).");

            var il2cpp = !string.Equals(Environment.GetEnvironmentVariable("BUGSEE_E2E_BACKEND"), "mono", StringComparison.OrdinalIgnoreCase);
            var development = Environment.GetEnvironmentVariable("BUGSEE_E2E_DEV") != "0";
            var ios = target == BuildTarget.iOS;
            var group = ios ? BuildTargetGroup.iOS : BuildTargetGroup.Android;

            var minify = Environment.GetEnvironmentVariable("BUGSEE_E2E_MINIFY") == "1";
            var format = (Environment.GetEnvironmentVariable("BUGSEE_E2E_FORMAT") ?? "apk").ToLowerInvariant();
            var label = (ios ? "ios" : "android") + "-" + (il2cpp ? "il2cpp" : "mono") + "-" + (development ? "dev" : "release")
                + (minify && !ios ? "-min" : "") + (!ios && format != "apk" ? "-" + format : "");
            var output = Environment.GetEnvironmentVariable("BUGSEE_E2E_OUT");
            if (string.IsNullOrEmpty(output))
                output = ios || format == "export" ? "Builds/E2E/" + label : "Builds/E2E/" + label + (format == "aab" ? ".aab" : ".apk");
            var outputDir = Path.GetDirectoryName(output.TrimEnd('/'));
            // GetDirectoryName("x.apk") is "", not null, and CreateDirectory("") throws.
            Directory.CreateDirectory(string.IsNullOrEmpty(outputDir) ? "." : outputDir);

            if (ios)
            {
                // The Simulator SDK needs simulator slices of the IL2CPP runtime; the device SDK does not link there.
                var simulator = Environment.GetEnvironmentVariable("BUGSEE_E2E_IOS_SIM") == "1";
                PlayerSettings.iOS.sdkVersion = simulator ? iOSSdkVersion.SimulatorSDK : iOSSdkVersion.DeviceSDK;
#if UNITY_2022_1_OR_NEWER
                if (simulator)
                    PlayerSettings.iOS.simulatorSdkArchitecture = SimulatorArchitecture();
#endif
            }

            EditorUserBuildSettings.SwitchActiveBuildTarget(group, target);
            PlayerSettings.SetScriptingBackend(group, il2cpp ? ScriptingImplementation.IL2CPP : ScriptingImplementation.Mono2x);
            EditorUserBuildSettings.development = development;
            EditorUserBuildSettings.allowDebugging = development;

            if (!ios)
            {
                PlayerSettings.Android.minifyRelease = minify;
                PlayerSettings.Android.minifyDebug = minify;
                EditorUserBuildSettings.buildAppBundle = format == "aab";
                EditorUserBuildSettings.exportAsGoogleAndroidProject = format == "export";
                // Unity 2021.3 builds ARM64 only with IL2CPP; a Mono build is ARMv7 (32-bit) only, so it will not
                // install on a 64-bit-only device or emulator image (INSTALL_FAILED_NO_MATCHING_ABIS).
                PlayerSettings.Android.targetArchitectures = il2cpp
                    ? AndroidArchitecture.ARM64
                    : AndroidArchitecture.ARMv7;
#if UNITY_2021_2_OR_NEWER
                // Native debug symbols (symbols.zip) for ELF upload; Debugging keeps full DWARF.
                EditorUserBuildSettings.androidCreateSymbols =
                    development ? AndroidCreateSymbols.Debugging : AndroidCreateSymbols.Public;
#endif
            }

            // A copy left by a build that was killed would otherwise take priority over everything else.
            RemoveOverride();
            var endpoint = Environment.GetEnvironmentVariable("BUGSEE_ENDPOINT");
            WriteOverride(token, endpoint, ios);
            BuildReport report;
            try
            {
                var scenes = ResolveScenes();
                Debug.Log("[E2EBuild] Building " + label + " → " + output + " scenes=" + string.Join(",", scenes));

                var options = development ? BuildOptions.Development | BuildOptions.AllowDebugging : BuildOptions.None;
                if (Environment.GetEnvironmentVariable("BUGSEE_E2E_APPEND") == "1")
                    options |= BuildOptions.AcceptExternalModificationsToPlayer;
                var settingDev = Environment.GetEnvironmentVariable("BUGSEE_E2E_SETTING_DEV");
                if (settingDev == "0" || settingDev == "1")
                    EditorUserBuildSettings.development = settingDev == "1";
                report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = scenes,
                    locationPathName = output,
                    target = target,
                    options = options,
                });
            }
            finally
            {
                RemoveOverride();
            }

            // Exit only after the credentials are gone: EditorApplication.Exit ends the process at once, so
            // calling it inside the try would skip the finally on a failed build.
            ExitFromReport(report);
        }

#if UNITY_2022_1_OR_NEWER
        static AppleMobileArchitectureSimulator SimulatorArchitecture()
        {
            switch ((Environment.GetEnvironmentVariable("BUGSEE_E2E_IOS_SIM_ARCH") ?? "").ToLowerInvariant())
            {
                case "x64": return AppleMobileArchitectureSimulator.X86_64;
                case "universal": return AppleMobileArchitectureSimulator.Universal;
                case "arm64": return AppleMobileArchitectureSimulator.ARM64;
                default:
                    return System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ==
                           System.Runtime.InteropServices.Architecture.Arm64
                        ? AppleMobileArchitectureSimulator.ARM64
                        : AppleMobileArchitectureSimulator.X86_64;
            }
        }

#endif

        [Serializable]
        class E2EOverride
        {
            public string token;
            public string endpoint;
        }

        static void WriteOverride(string token, string endpoint, bool ios)
        {
            if (!string.IsNullOrWhiteSpace(endpoint))
            {
                endpoint = endpoint.Trim().TrimEnd('/');
                // iOS wants the API version in the endpoint; Android appends it itself, so a value that
                // already ends in /v2 would be requested as .../v2/v2.
                var versioned = endpoint.EndsWith("/v2", StringComparison.Ordinal);
                if (ios && !versioned) endpoint += "/v2";
                else if (!ios && versioned) endpoint = endpoint.Substring(0, endpoint.Length - "/v2".Length);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(OverrideAsset));
            File.WriteAllText(OverrideAsset, JsonUtility.ToJson(new E2EOverride { token = token.Trim(), endpoint = endpoint }));
            AssetDatabase.ImportAsset(OverrideAsset);
        }

        static void RemoveOverride()
        {
            AssetDatabase.DeleteAsset(OverrideAsset);
        }

        static string[] ResolveScenes()
        {
            var scenes = EditorBuildSettings.scenes
                .Where(s => s.enabled)
                .Select(s => s.path)
                .ToArray();
            if (scenes.Length == 0)
            {
                var guids = AssetDatabase.FindAssets("t:Scene MiniGame");
                scenes = guids
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .Where(p => p.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }
            if (scenes.Length == 0)
                throw new Exception("No scenes enabled for build.");
            return scenes;
        }

        static void ExitFromReport(BuildReport report)
        {
            var summary = report.summary;
            Debug.Log("[E2EBuild] result=" + summary.result + " errors=" + summary.totalErrors +
                      " warnings=" + summary.totalWarnings + " size=" + summary.totalSize);
            if (summary.result != BuildResult.Succeeded)
                EditorApplication.Exit(1);
        }
    }
}
#endif
