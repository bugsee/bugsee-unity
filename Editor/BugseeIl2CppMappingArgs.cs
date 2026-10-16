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
    /// <summary>
    /// Makes an IL2CPP Android/iOS build write <c>LineNumberMappings.json</c> and <c>MethodMap.tsv</c>
    /// (see <see cref="Il2CppMappingArgs"/>): IL2CPP writes neither unless asked, so the il2cpp-linemap upload had no
    /// mapping, or one without MethodMap (mangled names could not be demangled). Runs only when the il2cpp-linemap upload
    /// is on (a Bugsee app token, and BUGSEE_NO_IL2CPP_MAPPING unset). The arguments are added for this build and the
    /// Player Settings string is restored afterwards (the string is saved in the VCS-tracked ProjectSettings.asset while
    /// the build runs). The post-build callback is skipped when the pipeline aborts, so a one-shot editor update handler
    /// restores once no build is running (success, failure or cancel); the saved state in Library/Bugsee also covers an
    /// editor exit. A string the user changed meanwhile is kept (see <see cref="Il2CppArgsRestorePolicy"/>).
    /// Unity itself adds --emit-source-mapping on 2022.3 and 6000 when Cloud Diagnostics crash reporting is on; the
    /// duplicate argument is harmless. Toggling the token changes the arguments and so invalidates the IL2CPP cache.
    /// </summary>
    [InitializeOnLoad]
    sealed class BugseeIl2CppMappingArgs : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        public int callbackOrder => 20;

        const string InProgressKey = "Bugsee.Il2CppArgsBuildInProgress";

        [Serializable]
        sealed class PendingRestore
        {
            public string previous;
            public string applied;
        }

        static string PendingPath =>
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Application.dataPath)), "Library", "Bugsee", "il2cpp-args-restore.json");

        static BugseeIl2CppMappingArgs()
        {
            EditorApplication.delayCall += () =>
            {
                if (!File.Exists(PendingPath))
                {
                    return;
                }

                // A domain reload during a build loses the update handler: set it again until the build is over.
                if (BuildPipeline.isBuildingPlayer || SessionState.GetBool(InProgressKey, false))
                {
                    EditorApplication.update += RestoreWhenBuildEnds;
                }
                else
                {
                    ResolvePending(force: false);
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
            ResolvePending(force: false);
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
            return il2cpp &&
                   BugseeCliRunner.HasAppToken &&
                   Environment.GetEnvironmentVariable("BUGSEE_NO_IL2CPP_MAPPING") == null &&
                   string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BUGSEE_IL2CPP_MAPPING"));
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            // A restore left by an earlier build is settled first (no build of this kind is running yet).
            ResolvePending(force: true);
            if (!Applies(report))
            {
                return;
            }

            string previous = PlayerSettings.GetAdditionalIl2CppArgs() ?? string.Empty;
            string applied = Il2CppMappingArgs.Ensure(previous);
            if (string.Equals(previous, applied, StringComparison.Ordinal))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(PendingPath));
            File.WriteAllText(PendingPath, JsonUtility.ToJson(new PendingRestore { previous = previous, applied = applied }));
            SessionState.SetBool(InProgressKey, true);
            PlayerSettings.SetAdditionalIl2CppArgs(applied);
            EditorApplication.update -= RestoreWhenBuildEnds;
            EditorApplication.update += RestoreWhenBuildEnds;
            Debug.Log("[Bugsee] Building with IL2CPP arguments \"" + applied + "\" so the line-number map and MethodMap are written (restored afterwards).");
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            // Fast path: BuildPipeline.isBuildingPlayer is still true inside this callback.
            ResolvePending(force: true);
        }

        /// <param name="force">The caller knows no build of ours is running (post-build callback, or before a new build).</param>
        static void ResolvePending(bool force)
        {
            if (!File.Exists(PendingPath))
            {
                return;
            }

            try
            {
                PendingRestore pending = JsonUtility.FromJson<PendingRestore>(File.ReadAllText(PendingPath));
                if (pending != null)
                {
                    bool unchanged = string.Equals(PlayerSettings.GetAdditionalIl2CppArgs(), pending.applied, StringComparison.Ordinal);
                    switch (Il2CppArgsRestorePolicy.Decide(!force && BuildPipeline.isBuildingPlayer, unchanged))
                    {
                        case SymbolRestoreAction.Skip:
                            return;
                        case SymbolRestoreAction.Restore:
                            PlayerSettings.SetAdditionalIl2CppArgs(pending.previous ?? string.Empty);
                            Debug.Log("[Bugsee] Restored the additional IL2CPP arguments.");
                            break;
                        default:
                            Debug.Log("[Bugsee] The additional IL2CPP arguments were changed since the Bugsee build added the mapping arguments; keeping your setting.");
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Bugsee] Could not restore the additional IL2CPP arguments: " + ex.Message);
            }
            SessionState.EraseBool(InProgressKey);
            try { File.Delete(PendingPath); } catch (Exception) { }
        }
    }
}
#endif
