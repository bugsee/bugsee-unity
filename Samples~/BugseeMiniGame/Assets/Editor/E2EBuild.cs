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
    /// Batchmode builds for Mode B e2e:
    /// Unity -batchmode -quit -projectPath … -executeMethod Bugsee.Sample.Editor.E2EBuild.BuildAndroidIl2Cpp
    /// Unity -batchmode -quit -projectPath … -executeMethod Bugsee.Sample.Editor.E2EBuild.BuildIosIl2Cpp
    /// </summary>
    public static class E2EBuild
    {
        const string OutApk = "Builds/E2E/BugseeMiniGame-e2e.apk";
        const string OutIos = "Builds/E2E/ios";

        public static void BuildAndroidIl2Cpp()
        {
            Directory.CreateDirectory("Builds/E2E");

            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            // Encourage LineNumberMappings.json generation for Mode B LNM.
            EditorUserBuildSettings.development = true;
            EditorUserBuildSettings.allowDebugging = true;
#if UNITY_2021_2_OR_NEWER
            EditorUserBuildSettings.androidCreateSymbols = AndroidCreateSymbols.Debugging;
#endif
            try
            {
                PlayerSettings.SetAdditionalIl2CppArgs("--emit-source-mapping");
            }
            catch
            {
                /* older editors */
            }

            var token = Environment.GetEnvironmentVariable("BUGSEE_APP_TOKEN");
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("[E2EBuild] Set BUGSEE_APP_TOKEN for Android symbol upload.");
            Environment.SetEnvironmentVariable("BUGSEE_APP_TOKEN", token);

            var scenes = ResolveScenes();
            Debug.Log("[E2EBuild] Building Android IL2CPP → " + OutApk + " scenes=" + string.Join(",", scenes));

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = OutApk,
                target = BuildTarget.Android,
                options = BuildOptions.Development | BuildOptions.AllowDebugging,
            });

            ExitFromReport(report);
        }

        public static void BuildIosIl2Cpp()
        {
            Directory.CreateDirectory(OutIos);

            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.iOS, BuildTarget.iOS);
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.iOS, ScriptingImplementation.IL2CPP);
            EditorUserBuildSettings.development = true;
            EditorUserBuildSettings.allowDebugging = true;
            try
            {
                PlayerSettings.SetAdditionalIl2CppArgs("--emit-source-mapping");
            }
            catch
            {
                /* older editors */
            }

            var token = Environment.GetEnvironmentVariable("BUGSEE_APP_TOKEN");
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("[E2EBuild] Set BUGSEE_APP_TOKEN for iOS symbol upload.");
            Environment.SetEnvironmentVariable("BUGSEE_APP_TOKEN", token);

            var scenes = ResolveScenes();
            Debug.Log("[E2EBuild] Building iOS IL2CPP → " + OutIos + " scenes=" + string.Join(",", scenes));

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = OutIos,
                target = BuildTarget.iOS,
                options = BuildOptions.Development | BuildOptions.AllowDebugging,
            });

            ExitFromReport(report);
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
