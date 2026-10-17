#if UNITY_EDITOR
using System;
using Bugsee.WrapperPolicy;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using Debug = UnityEngine.Debug;

namespace Bugsee.Editor
{
    /// <summary>
    /// Warns once per domain load when an Android build used the Mono scripting backend (see
    /// <see cref="AndroidMonoSymbolPolicy"/>). Independent of the symbol upload settings (token, opt-out), and the
    /// backend is the one recorded for this build, not the live Player Settings. Never fails the build.
    /// </summary>
    public sealed class BugseeAndroidMonoWarning : IPostprocessBuildWithReport
    {
        static bool _warned;

        public int callbackOrder => 900;

        public void OnPostprocessBuild(BuildReport report)
        {
            try
            {
                if (report == null || report.summary.platform != BuildTarget.Android)
                {
                    return;
                }

                var backend = BugseeBuildIdentity.AndroidNameSettings(report).Backend;
                var warning = AndroidMonoSymbolPolicy.TakeWarning(backend, ref _warned);
                if (warning != null)
                {
                    Debug.LogWarning(warning);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Bugsee: could not check the Android scripting backend: {ex.Message}");
            }
        }
    }
}
#endif
