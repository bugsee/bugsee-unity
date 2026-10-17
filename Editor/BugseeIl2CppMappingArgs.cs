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
    /// editor exit (an editor quit restores too, so a batchmode run that exits right after a failed build is clean; only a
    /// killed editor process leaves the arguments for the next session). A string the user changed meanwhile is kept (see <see cref="Il2CppArgsRestorePolicy"/>).
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
            EditorApplication.quitting += RestoreOnQuit;
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

        static void RestoreOnQuit()
        {
            try
            {
                // A batchmode run that exits straight after a failed build never reaches an update: restore here (the
                // change is written to ProjectSettings.asset before the editor exits, checked on 2022.3.62f3).
                // Never while a build is still running.
                if (!BuildPipeline.isBuildingPlayer)
                {
                    ResolvePending(force: true);
                }
            }
            catch (Exception)
            {
            }
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

            // The record is the only way back to the user's arguments: without it nothing is applied, and an
            // unwritable Library/Bugsee must not fail the build over this optional feature.
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PendingPath));
                File.WriteAllText(PendingPath, JsonUtility.ToJson(new PendingRestore { previous = previous, applied = applied }));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Bugsee] Could not save the IL2CPP arguments restore record (" + ex.Message +
                                 "); not adding the mapping arguments, so the line-number map and MethodMap are not written for this build.");
                return;
            }

            SessionState.SetBool(InProgressKey, true);
            try
            {
                PlayerSettings.SetAdditionalIl2CppArgs(applied);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Bugsee] Could not set the additional IL2CPP arguments: " + ex.Message);
                SessionState.EraseBool(InProgressKey);
                try { File.Delete(PendingPath); } catch (Exception) { }
                return;
            }

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

            PendingRestore pending = null;
            try
            {
                pending = JsonUtility.FromJson<PendingRestore>(File.ReadAllText(PendingPath));
            }
            catch (IOException ex)
            {
                // Unreadable right now, not unparseable: keep the record for the next attempt.
                Debug.LogWarning("[Bugsee] Could not read the IL2CPP arguments restore record: " + ex.Message);
                return;
            }
            catch (Exception)
            {
                // Unparseable: nothing to restore from.
            }

            bool restoreThrew = false;
            if (pending != null)
            {
                try
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
                catch (Exception ex)
                {
                    restoreThrew = true;
                    Debug.LogWarning("[Bugsee] Could not restore the additional IL2CPP arguments (the record is kept for the next editor session): " + ex.Message);
                }
            }

            if (Il2CppArgsRestorePolicy.DeleteRecord(pending != null, restoreThrew))
            {
                SessionState.EraseBool(InProgressKey);
                try { File.Delete(PendingPath); } catch (Exception) { }
            }
        }
    }
}
#endif
