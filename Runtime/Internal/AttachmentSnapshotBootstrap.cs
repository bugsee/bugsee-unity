using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Bugsee.WrapperPolicy;
using UnityEngine;

namespace Bugsee.Internal
{
    /// <summary>Hands the app's private cache directories (main-thread-only Unity/JNI APIs) to the attachment snapshotter.</summary>
    static class AttachmentSnapshotBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Configure() => Apply(); // runs on the main thread

        /// <summary>Idempotent; reads the paths only on the main thread.</summary>
        internal static void EnsureConfigured()
        {
            if (AttachmentSnapshot.IsConfigured || !MainThreadDispatcher.IsMainThread) return;
            Apply();
        }

        static void Apply()
        {
            var roots = new List<string>();
#if UNITY_ANDROID && !UNITY_EDITOR
            // The internal cache dir first: Application.temporaryCachePath can be the external one, which other apps can read
            // on Android 5 to 9, and the attachment may be private.
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var dir = activity?.Call<AndroidJavaObject>("getCacheDir"))
                {
                    var path = dir?.Call<string>("getAbsolutePath");
                    if (!string.IsNullOrEmpty(path)) roots.Add(path);
                }
            }
            catch (Exception) { /* fall back to the Unity path below */ }
#endif
            try { roots.Add(Application.temporaryCachePath); } catch (Exception) { }
            AttachmentSnapshot.Configure(roots.ToArray(), message => Debug.LogWarning("[Bugsee] " + message));
            var toClean = AttachmentSnapshot.Roots();
            Task.Run(() => AttachmentSnapshot.CleanStale(toClean, TimeSpan.FromDays(1)));
        }
    }
}
