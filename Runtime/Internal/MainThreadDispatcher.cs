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
            AdoptMainThreadId();
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

        static void AdoptMainThreadId()
        {
            if (_mainThreadId == 0)
            {
                _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            }
        }

        public static bool IsMainThread =>
            _mainThreadId != 0 && Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        static bool ShouldRunInline() => _mainThreadId == 0 || IsMainThread;

        static void DrainQueueInline()
        {
            while (Queue.TryDequeue(out var pending))
            {
                try { pending(); }
                catch (Exception ex) { Debug.LogException(ex); }
            }
        }

        static void RunInlineOnMainThread(Action action)
        {
            AdoptMainThreadId();
            DrainQueueInline();
            action();
        }

        public static void Run(Action action)
        {
            if (action == null) return;
            if (ShouldRunInline())
            {
                RunInlineOnMainThread(action);
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
            if (ShouldRunInline())
            {
                try
                {
                    RunInlineOnMainThread(action);
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                    throw;
                }
            }

            Ensure();
            using (var done = new ManualResetEventSlim(false))
            {
                Exception captured = null;
                var cancelled = 0;
                Queue.Enqueue(() =>
                {
                    if (Interlocked.CompareExchange(ref cancelled, 0, 0) != 0)
                    {
                        return;
                    }

                    try { action(); }
                    catch (Exception ex) { captured = ex; }
                    finally { done.Set(); }
                });

                if (!done.Wait(timeoutMs))
                {
                    Interlocked.Exchange(ref cancelled, 1);
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
            if (!IsMainThread)
            {
                return;
            }

            DrainQueueInline();
        }
    }
}
