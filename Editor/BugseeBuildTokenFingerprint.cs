#if UNITY_EDITOR
using System;
using System.IO;
using Bugsee.WrapperPolicy;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Bugsee.Editor
{
    /// <summary>
    /// Records a fingerprint (never the token, see <see cref="BuildTokenFingerprint"/>) of BUGSEE_APP_TOKEN in the player,
    /// so <c>Bugsee.Launch</c> can warn when it is given another token than the one symbols are uploaded for. The
    /// fingerprint is a Resources TextAsset that exists only for the build: written at preprocess (a file written after the
    /// player is built would not be in it), removed when the build ends (post-build, the editor's next update after a
    /// failed or cancelled build, an editor start or exit), so nothing stays in Assets. Written for Android builds when a
    /// symbol upload is on (a token set, BUGSEE_NO_SYMBOL_UPLOAD unset) and BUGSEE_NO_TOKEN_CHECK is unset. Not for iOS and
    /// tvOS: their symbols are uploaded by the Xcode Run Script with the token of the Xcode environment, which this cannot
    /// see, so the Unity-time token says nothing about where the symbols go. Never fails the build.
    /// </summary>
    [InitializeOnLoad]
    sealed class BugseeBuildTokenFingerprint : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        public int callbackOrder => 30;

        const string Folder = "Assets/Resources";
        const string InProgressKey = "Bugsee.TokenFingerprintBuildInProgress";

        // Survives an editor exit (SessionState does not): proves this package created an empty Assets/Resources.
        static string CreatedFolderMarker =>
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Application.dataPath)), "Library", "Bugsee", "token-fingerprint-created-resources");
        static string AssetPath => Folder + "/" + BuildTokenFingerprint.ResourceName + ".txt";

        static BugseeBuildTokenFingerprint()
        {
            EditorApplication.quitting += () =>
            {
                if (!BuildPipeline.isBuildingPlayer)
                {
                    Remove();
                }
            };
            EditorApplication.delayCall += () =>
            {
                if (BuildPipeline.isBuildingPlayer || SessionState.GetBool(InProgressKey, false))
                {
                    EditorApplication.update += RemoveWhenBuildEnds;
                }
                else
                {
                    Remove();
                }
            };
        }

        static void RemoveWhenBuildEnds()
        {
            if (BuildPipeline.isBuildingPlayer)
            {
                return;
            }

            EditorApplication.update -= RemoveWhenBuildEnds;
            Remove();
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            Remove();
            if (report.summary.platform != BuildTarget.Android ||
                Environment.GetEnvironmentVariable("BUGSEE_NO_TOKEN_CHECK") != null ||
                Environment.GetEnvironmentVariable("BUGSEE_NO_SYMBOL_UPLOAD") != null || !BugseeCliRunner.HasAppToken)
            {
                return;
            }

            var fingerprint = BuildTokenFingerprint.Compute(BugseeCliRunner.AppToken);
            if (fingerprint == null)
            {
                return;
            }

            try
            {
                bool createdFolder = !Directory.Exists(Folder);
                if (createdFolder)
                {
                    Directory.CreateDirectory(Folder);
                }

                SessionState.SetBool(InProgressKey, true);
                if (createdFolder)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(CreatedFolderMarker));
                    File.WriteAllText(CreatedFolderMarker, "");
                }

                File.WriteAllText(AssetPath, fingerprint + "\n");
                AssetDatabase.ImportAsset(AssetPath);
                EditorApplication.update -= RemoveWhenBuildEnds;
                EditorApplication.update += RemoveWhenBuildEnds;
                Debug.Log("[Bugsee] Recorded the fingerprint " + fingerprint +
                          " of BUGSEE_APP_TOKEN in the build (removed afterwards), so Launch can warn about another token.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Bugsee] Could not record the build token fingerprint: " + ex.Message);
                Remove();
            }
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            Remove();
        }

        static void Remove()
        {
            try
            {
                if (File.Exists(AssetPath))
                {
                    if (!AssetDatabase.DeleteAsset(AssetPath))
                    {
                        File.Delete(AssetPath);
                        var meta = AssetPath + ".meta";
                        if (File.Exists(meta))
                        {
                            File.Delete(meta);
                        }
                    }
                }

                // The folder goes only when this package made it and nothing else is in it now.
                if (File.Exists(CreatedFolderMarker) && Directory.Exists(Folder) &&
                    Directory.GetFileSystemEntries(Folder).Length == 0)
                {
                    AssetDatabase.DeleteAsset(Folder);
                }

                if (File.Exists(CreatedFolderMarker) && !File.Exists(AssetPath))
                {
                    File.Delete(CreatedFolderMarker);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Bugsee] Could not remove " + AssetPath + ": " + ex.Message);
            }
            finally
            {
                SessionState.EraseBool(InProgressKey);
            }
        }
    }
}
#endif
