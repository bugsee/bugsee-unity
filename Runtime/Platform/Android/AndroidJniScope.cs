#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Threading;
using UnityEngine;

namespace Bugsee.Platform.Android
{
    /// <summary>
    /// Makes a JNI call safe from a thread Unity did not create (a thread-pool or <c>Task</c> thread). Such a thread
    /// is not attached to the JVM, and a JNI call from it aborts the process (CheckJNI: "use of deleted local
    /// reference"; seen on a Galaxy Tab A 8.0 / Android 11). Wrap every JNI entry that spans can reach: on a
    /// foreign thread the outermost scope attaches the thread and detaches it again, on the main thread (and nested
    /// scopes) it does nothing, so a thread Unity attached itself is never detached.
    /// </summary>
    struct AndroidJniScope : IDisposable
    {
        static int _mainThreadId = -1;

        [ThreadStatic] static int _depth;

        readonly bool _attached;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void CaptureMainThread() => _mainThreadId = Thread.CurrentThread.ManagedThreadId;

        public static AndroidJniScope Enter()
        {
            if (_depth++ > 0 || Thread.CurrentThread.ManagedThreadId == _mainThreadId)
                return new AndroidJniScope(false);
            AndroidJNI.AttachCurrentThread();
            return new AndroidJniScope(true);
        }

        AndroidJniScope(bool attached) => _attached = attached;

        public void Dispose()
        {
            _depth--;
            if (_attached) AndroidJNI.DetachCurrentThread();
        }
    }
}
#endif
