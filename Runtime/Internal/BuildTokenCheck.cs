using System;
using Bugsee.WrapperPolicy;
using UnityEngine;

namespace Bugsee.Internal
{
    /// <summary>
    /// Logs one warning at <c>Launch</c> when the token differs from the BUGSEE_APP_TOKEN the build uploaded its symbols
    /// for (see <see cref="BuildTokenFingerprint"/>). Silent when they match, and when the build recorded nothing (no
    /// build token, symbol upload off, an Editor run, a player built by a CI that uploads separately). Never throws.
    /// </summary>
    internal static class BuildTokenCheck
    {
        static int _checked;

        public static void OnLaunch(string appToken)
        {
#if !UNITY_EDITOR
            if (string.IsNullOrEmpty(appToken) || System.Threading.Interlocked.Exchange(ref _checked, 1) == 1)
            {
                return;
            }

            // Resources.Load is main-thread only; Launch can be called from another thread.
            MainThreadDispatcher.Run(() => Check(appToken));
#endif
        }

        static void Check(string appToken)
        {
            try
            {
                var asset = Resources.Load<TextAsset>(BuildTokenFingerprint.ResourceName);
                var recorded = asset != null ? asset.text : null;
                if (BuildTokenFingerprint.Check(recorded, appToken) == BuildTokenFingerprint.Verdict.Mismatch)
                {
                    Debug.LogWarning("[Bugsee] " + BuildTokenFingerprint.MismatchMessage(recorded, appToken));
                }
            }
            catch (Exception)
            {
                // A diagnostic only.
            }
        }
    }
}
