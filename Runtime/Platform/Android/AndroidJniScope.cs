#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Threading;
using Bugsee.Internal;
using Bugsee.WrapperPolicy;
using UnityEngine;

namespace Bugsee.Platform.Android
{
    /// <summary>
    /// Makes a JNI call safe from a managed thread-pool thread (a <c>Task</c> or <c>ThreadPool</c> thread), which the JVM
    /// does not know: a JNI call from it aborts the process (CheckJNI: "use of deleted local reference"; seen on a
    /// Galaxy Tab A 8.0, Android 11). Wrap every JNI entry that spans can reach. The decision is
    /// <see cref="JniAttachPolicy"/>: only an unattached pool thread is attached, and only a thread this scope attached is
    /// detached, so the main thread, Java-owned threads (callbacks) and threads someone else attached are never touched.
    /// Nested scopes cost nothing; a failed attach throws instead of letting the call abort the process.
    /// </summary>
    struct AndroidJniScope : IDisposable
    {
        [ThreadStatic] static int _depth;
        [ThreadStatic] static bool _weAttached;

        readonly bool _attached;

        public static AndroidJniScope Enter()
        {
            bool attach = JniAttachPolicy.ShouldAttach(
                MainThreadDispatcher.IsMainThread, Thread.CurrentThread.IsThreadPoolThread, _weAttached, _depth);
            if (attach)
            {
                if (AndroidJNI.AttachCurrentThread() != 0)
                    throw new InvalidOperationException("could not attach the calling thread to the JVM");
                _weAttached = true;
            }
            _depth++;
            return new AndroidJniScope(attach);
        }

        AndroidJniScope(bool attached) => _attached = attached;

        public void Dispose()
        {
            _depth--;
            if (!_attached) return;
            _weAttached = false;
            AndroidJNI.DetachCurrentThread();
        }
    }
}
#endif
