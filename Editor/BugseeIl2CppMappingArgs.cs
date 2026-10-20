#if UNITY_EDITOR
using System;
using Bugsee.WrapperPolicy;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Bugsee.Editor
{
    /// <summary>
    /// Makes an IL2CPP Android/iOS/tvOS build write <c>LineNumberMappings.json</c> and <c>MethodMap.tsv</c>
    /// (see <see cref="Il2CppMappingArgs"/>): IL2CPP writes neither unless asked, so the il2cpp-linemap upload had no
    /// mapping, or one without MethodMap (mangled names could not be demangled). Runs for every IL2CPP build unless
    /// BUGSEE_NO_IL2CPP_MAPPING is set: no Bugsee app token is needed (the files are what a later CI job uploads; only the
    /// upload needs the token).
    /// The arguments travel in the IL2CPP_ADDITIONAL_ARGS environment variable of the Editor process, which Unity adds to the
    /// il2cpp command line next to Player Settings > Additional IL2CPP Arguments. Nothing is written to the VCS-tracked
    /// ProjectSettings.asset and nothing has to be restored on disk. The variable is put back to its earlier value after the
    /// build; the post-build callback is skipped when the pipeline aborts, so an editor update handler does it once no build
    /// is running (success, failure or cancel), and the process environment dies with the editor in any case.
    /// Unity itself adds --emit-source-mapping on 2022.3 and 6000 when Cloud Diagnostics crash reporting is on; an argument
    /// the Player Settings or the variable already has is not added again. Switching the arguments on or off changes the
    /// generated C++ (source_info comments), so IL2CPP recompiles it once.
    /// </summary>
    [InitializeOnLoad]
    sealed class BugseeIl2CppMappingArgs : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        public int callbackOrder => 20;

        const string EnvName = "IL2CPP_ADDITIONAL_ARGS";
        const string AppliedKey = "Bugsee.Il2CppArgsEnvApplied";
        const string PreviousKey = "Bugsee.Il2CppArgsEnvPrevious";

        static BugseeIl2CppMappingArgs()
        {
            // A domain reload during a build loses the update handler: set it again until the build is over.
            EditorApplication.delayCall += () =>
            {
                if (SessionState.GetBool(AppliedKey, false))
                {
                    EditorApplication.update += RestoreWhenBuildEnds;
                }
            };
        }

        static void RestoreWhenBuildEnds()
        {
            if (BuildPipeline.isBuildingPlayer)
            {
                return;
            }

            EditorApplication.update -= RestoreWhenBuildEnds;
            Restore();
        }

        static void Restore()
        {
            if (!SessionState.GetBool(AppliedKey, false))
            {
                return;
            }

            string previous = SessionState.GetString(PreviousKey, string.Empty);
            try
            {
                Environment.SetEnvironmentVariable(EnvName, previous.Length == 0 ? null : previous);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Bugsee] Could not restore " + EnvName + ": " + ex.Message);
            }

            SessionState.EraseBool(AppliedKey);
            SessionState.EraseString(PreviousKey);
        }

        static bool Applies(BuildReport report)
        {
            BuildTarget platform = report.summary.platform;
            if (platform != BuildTarget.Android && platform != BuildTarget.iOS && platform != BuildTarget.tvOS)
            {
                return false;
            }

            // iOS has no Mono backend; Android builds Mono too, which writes no IL2CPP output.
            bool il2cpp = platform != BuildTarget.Android ||
                          PlayerSettings.GetScriptingBackend(NamedBuildTarget.Android) == ScriptingImplementation.IL2CPP;
            // Emission does not need the token (the files are for a later upload too, possibly from another CI job).
            return Il2CppMappingArgs.ShouldEmit(il2cpp, Environment.GetEnvironmentVariable("BUGSEE_NO_IL2CPP_MAPPING") != null);
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            // A value left by an aborted earlier build is put back first (no build of this kind is running yet).
            Restore();
            if (!Applies(report))
            {
                return;
            }

            string current = Environment.GetEnvironmentVariable(EnvName) ?? string.Empty;
            string value = Il2CppMappingArgs.EnvironmentValue(PlayerSettings.GetAdditionalIl2CppArgs(), current);
            if (value == null)
            {
                return;
            }

            try
            {
                SessionState.SetString(PreviousKey, current);
                SessionState.SetBool(AppliedKey, true);
                Environment.SetEnvironmentVariable(EnvName, value);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Bugsee] Could not set " + EnvName + ": " + ex.Message + "; the line-number map and MethodMap are not written for this build.");
                Restore();
                return;
            }

            EditorApplication.update -= RestoreWhenBuildEnds;
            EditorApplication.update += RestoreWhenBuildEnds;
            Debug.Log("[Bugsee] Building with " + EnvName + "=\"" + value + "\" so the line-number map and MethodMap are written (restored afterwards).");
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            Restore();
        }
    }
}
#endif
