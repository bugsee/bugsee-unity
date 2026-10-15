namespace Bugsee.WrapperPolicy
{
    public enum JniAttachMode
    {
        /// <summary>Make no attach call.</summary>
        None,

        /// <summary>Attach for the scope and detach when it ends.</summary>
        AttachDetach,

        /// <summary>Attach and leave the thread attached (until the thread exits).</summary>
        AttachOnly
    }

    /// <summary>
    /// What a JNI scope does for the calling thread. A JNI call from a thread the JVM does not know aborts the process
    /// (CheckJNI "use of deleted local reference"), so every managed thread except the main one gets an attach: the
    /// attach is idempotent, so a thread that is already attached (the Android UI thread, a Java-created thread running
    /// an <c>AndroidJavaProxy</c> callback, a thread the application attached) is unaffected. Detaching is the dangerous
    /// half (ART aborts on a detach while the thread has Java frames, and on a thread attached by someone else), so the
    /// wrapper detaches only a managed thread-pool thread (the pool is managed code only, never a Java callback or a
    /// thread the application attached for its own use), and leaves every other thread attached: ART detaches it when
    /// the thread exits (a warning, not an abort; see the tablet evidence in the PR).
    /// </summary>
    public static class JniAttachPolicy
    {
        /// <param name="alreadyAttached">This wrapper holds a pool-thread attach (<see cref="JniAttachMode.AttachDetach"/>) on the thread.</param>
        /// <param name="depth">Scopes this wrapper already holds on the thread; a nested scope does nothing.</param>
        public static JniAttachMode Decide(bool isMainThread, bool isPoolThread, bool alreadyAttached, int depth)
        {
            if (depth > 0 || isMainThread) return JniAttachMode.None;
            // A pool thread is attached only for a scope, so "already attached" is a nested case. A dedicated thread is
            // attached again every outermost scope: the attach is idempotent, and a cached flag would go stale if the
            // application detached the thread itself.
            if (isPoolThread) return alreadyAttached ? JniAttachMode.None : JniAttachMode.AttachDetach;
            return JniAttachMode.AttachOnly;
        }
    }
}
