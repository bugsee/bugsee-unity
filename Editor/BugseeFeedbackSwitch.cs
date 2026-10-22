using System;
using UnityEditor;
using UnityEditor.Build;
using Bugsee.WrapperPolicy;

namespace Bugsee.Editor
{
    /// <summary>Whether the project opted in to the Feedback add-on (<see cref="FeedbackOptIn"/>) for a build target.</summary>
    static class BugseeFeedbackSwitch
    {
        internal static bool IsEnabled(NamedBuildTarget target)
        {
            string defines = PlayerSettings.GetScriptingDefineSymbols(target);
#if UNITY_6000_0_OR_NEWER
            // Unity 6 build profiles carry their own defines, which Player Settings does not show.
            var profile = UnityEditor.Build.Profile.BuildProfile.GetActiveBuildProfile();
            if (profile != null && profile.scriptingDefines != null)
                defines += ";" + string.Join(";", profile.scriptingDefines);
#endif
            return FeedbackOptIn.IsEnabled(defines, Environment.GetEnvironmentVariable(FeedbackOptIn.EnvVar));
        }
    }
}
