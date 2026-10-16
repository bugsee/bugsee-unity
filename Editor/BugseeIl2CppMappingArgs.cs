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
    /// Player Settings string is restored afterwards; the saved state is kept in Library/Bugsee so an interrupted build
    /// is restored on the next editor load or build. A string the user changed meanwhile is left alone.
    /// </summary>
    [InitializeOnLoad]
    sealed class BugseeIl2CppMappingArgs : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        public int callbackOrder => 20;

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
                if (!BuildPipeline.isBuildingPlayer)
                {
                    ResolvePending();
                }
            };
        }

        static bool Applies(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.Android && report.summary.platform != BuildTarget.iOS)
            {
                return false;
            }

            // iOS has no Mono backend; Android builds Mono too, which writes no IL2CPP output.
            bool il2cpp = report.summary.platform == BuildTarget.iOS ||
                          PlayerSettings.GetScriptingBackend(NamedBuildTarget.Android) == ScriptingImplementation.IL2CPP;
            return il2cpp &&
                   BugseeCliRunner.HasAppToken &&
                   Environment.GetEnvironmentVariable("BUGSEE_NO_IL2CPP_MAPPING") == null;
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            ResolvePending();
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
            PlayerSettings.SetAdditionalIl2CppArgs(applied);
            Debug.Log("[Bugsee] Building with IL2CPP arguments \"" + applied + "\" so the line-number map and MethodMap are written (restored afterwards).");
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            ResolvePending();
        }

        static void ResolvePending()
        {
            if (!File.Exists(PendingPath))
            {
                return;
            }

            try
            {
                PendingRestore pending = JsonUtility.FromJson<PendingRestore>(File.ReadAllText(PendingPath));
                if (pending != null && string.Equals(PlayerSettings.GetAdditionalIl2CppArgs(), pending.applied, StringComparison.Ordinal))
                {
                    PlayerSettings.SetAdditionalIl2CppArgs(pending.previous ?? string.Empty);
                    Debug.Log("[Bugsee] Restored the additional IL2CPP arguments.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Bugsee] Could not restore the additional IL2CPP arguments: " + ex.Message);
            }
            finally
            {
                try { File.Delete(PendingPath); } catch (Exception) { }
            }
        }
    }
}
#endif
