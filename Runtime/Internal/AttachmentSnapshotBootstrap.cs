using Bugsee.WrapperPolicy;
using UnityEngine;

namespace Bugsee.Internal
{
    /// <summary>Hands the app's temporary cache directory (a main-thread-only Unity API) to the attachment snapshotter.</summary>
    static class AttachmentSnapshotBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Configure()
        {
            string cache = null;
            try { cache = Application.temporaryCachePath; } catch (System.Exception) { }
            AttachmentSnapshot.Configure(cache, message => Debug.LogWarning("[Bugsee] " + message));
        }
    }
}
