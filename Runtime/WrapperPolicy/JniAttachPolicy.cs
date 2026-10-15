namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// When a JNI call needs this wrapper to attach (and later detach) the calling thread. A JNI call from a thread the
    /// JVM does not know aborts the process, and detaching a thread that has Java frames (a callback on a Java-owned
    /// thread) or that someone else attached aborts it too, so the wrapper only touches a thread it can prove is
    /// unattached: a managed thread-pool thread (Unity attaches only its own threads and Java-owned ones are never in
    /// the managed pool), never the main thread, never inside a scope it already holds. A thread that application code
    /// attached by itself must not be a pool thread it also uses for spans.
    /// </summary>
    public static class JniAttachPolicy
    {
        public static bool ShouldAttach(bool isMainThread, bool isPoolThread, bool alreadyAttached, int depth) =>
            depth == 0 && !alreadyAttached && !isMainThread && isPoolThread;
    }
}
