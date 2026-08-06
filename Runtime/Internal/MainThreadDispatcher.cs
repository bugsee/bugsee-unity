using System;
using System.Collections.Concurrent;
using System.Threading;
using UnityEngine;

namespace Bugsee.Internal
{
    /// <summary>Marshals work from JNI/SDK worker threads onto the Unity main thread.</summary>
    public sealed class MainThreadDispatcher : MonoBehaviour
    {
        static MainThreadDispatcher _instance;
        static readonly ConcurrentQueue<Action> Queue = new ConcurrentQueue<Action>();
        static int _mainThreadId;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Bootstrap()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            Ensure();
        }

        public static MainThreadDispatcher Ensure()
        {
            if (_instance != null) return _instance;
            var go = new GameObject("Bugsee.MainThreadDispatcher");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<MainThreadDispatcher>();
            return _instance;
        }

        public static bool IsMainThread =>
            Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        public static void Run(Action action)
        {
            if (action == null) return;
            if (IsMainThread)
            {
                action();
                return;
            }

            Ensure();
            Queue.Enqueue(action);
        }

        void Update()
        {
            while (Queue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { Debug.LogException(ex); }
            }
        }
    }
}
