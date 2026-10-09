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
            if (_instance != null)
            {
                return _instance;
            }

            AdoptMainThreadId();
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
            DrainQueueInline();
            action();
        }

        public static void Run(Action action)
        {
            if (action == null) return;
            if (IsMainThread)
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
        /// Queued work is not cancelled when the wait times out.
        /// </summary>
        public static bool RunSync(Action action, int timeoutMs = 5000)
        {
            if (action == null) return true;
            Ensure();
            if (IsMainThread)
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

            var done = new ManualResetEventSlim(false);
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

        /// <summary>
        /// Like <see cref="RunSync"/> but skips the queued action if the wait times out
        /// (for iOS lifecycle calls that must not run after the caller has moved on).
        /// </summary>
        public static bool RunSyncLifecycle(Action action, int timeoutMs = 5000)
        {
            if (action == null) return true;
            Ensure();
            if (IsMainThread)
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

            var done = new ManualResetEventSlim(false);
            Exception captured = null;
            var skip = 0;
            Queue.Enqueue(() =>
            {
                if (Interlocked.CompareExchange(ref skip, 0, 0) != 0)
                {
                    return;
                }

                try { action(); }
                catch (Exception ex) { captured = ex; }
                finally { done.Set(); }
            });

            if (!done.Wait(timeoutMs))
            {
                Interlocked.Exchange(ref skip, 1);
                return false;
            }

            done.Dispose();
            if (captured != null)
            {
                throw captured;
            }

            return true;
        }

        void Update()
        {
            DrainQueueInline();
        }
    }
}
