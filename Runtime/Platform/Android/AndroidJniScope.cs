#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Threading;
using Bugsee.Internal;
using Bugsee.WrapperPolicy;
using UnityEngine;

namespace Bugsee.Platform.Android
{
    /// <summary>
    /// Makes a JNI call safe from a managed thread other than the main one (a <c>Task</c>/<c>ThreadPool</c> thread, a
    /// dedicated <c>new Thread</c>, a Java-created callback thread, the UI thread): the JVM does not know a managed thread
    /// until it is attached, and a JNI call from it aborts the process (CheckJNI: "use of deleted local reference"; seen
    /// on a Galaxy Tab A 8.0, Android 11). Wrap every JNI entry that spans can reach. <see cref="JniAttachPolicy"/> decides:
    /// pool threads are attached for the scope and detached after, other threads are attached once and left attached
    /// (the attach is idempotent, and nothing is detached that this scope did not attach on a pool thread). Nested scopes
    /// cost nothing; a failed attach throws instead of letting the call abort the process.
    /// </summary>
    struct AndroidJniScope : IDisposable
    {
        [ThreadStatic] static int _depth;
        [ThreadStatic] static bool _weAttached;

        readonly bool _detach;

        public static AndroidJniScope Enter()
        {
            var mode = JniAttachPolicy.Decide(
                MainThreadDispatcher.IsMainThread, Thread.CurrentThread.IsThreadPoolThread, _weAttached, _depth);
            if (mode != JniAttachMode.None)
            {
                if (AndroidJNI.AttachCurrentThread() != 0)
                    throw new InvalidOperationException("could not attach the calling thread to the JVM");
                _weAttached = mode == JniAttachMode.AttachDetach;
            }
            _depth++;
            return new AndroidJniScope(mode == JniAttachMode.AttachDetach);
        }

        AndroidJniScope(bool detach) => _detach = detach;

        public void Dispose()
        {
            _depth--;
            if (!_detach) return;
            _weAttached = false;
            AndroidJNI.DetachCurrentThread();
        }
    }
}
#endif
