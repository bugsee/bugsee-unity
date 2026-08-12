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

        /// <summary>
        /// Run on the main thread and block the caller until completion (or timeout).
        /// Used for fatal managed exception flush before process teardown.
        /// </summary>
        public static bool RunSync(Action action, int timeoutMs = 5000)
        {
            if (action == null) return true;
            if (IsMainThread)
            {
                action();
                return true;
            }

            Ensure();
            using (var done = new ManualResetEventSlim(false))
            {
                Exception captured = null;
                Queue.Enqueue(() =>
                {
                    try { action(); }
                    catch (Exception ex) { captured = ex; }
                    finally { done.Set(); }
                });

                if (!done.Wait(timeoutMs))
                {
                    return false;
                }
                if (captured != null)
                {
                    throw captured;
                }
                return true;
            }
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
