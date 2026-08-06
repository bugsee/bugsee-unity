#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using Bugsee.Contracts.Exchange;
using Bugsee.Contracts.Reporting;
using Bugsee.Internal;
using UnityEngine;

namespace Bugsee.Platform.Android
{
    sealed class EventFilterProxy<TNative> : AndroidJavaProxy where TNative : class
    {
        readonly string _javaInterface;
        readonly Func<AndroidJavaObject, TNative> _wrap;
        readonly Action<TNative, AndroidJavaObject> _applyBack;
        EventFilter<TNative> _filter;

        public EventFilterProxy(
            string javaEventFilterInterface,
            Func<AndroidJavaObject, TNative> wrap,
            Action<TNative, AndroidJavaObject> applyBack)
            : base(javaEventFilterInterface)
        {
            _javaInterface = javaEventFilterInterface;
            _wrap = wrap;
            _applyBack = applyBack;
        }

        public void SetFilter(EventFilter<TNative> filter) => _filter = filter;

        // EventFilter.filter(T data, Callback1<T> callback)
        public void filter(AndroidJavaObject data, AndroidJavaObject callback)
        {
            var filter = _filter;
            if (filter == null)
            {
                callback?.Call("run", data);
                return;
            }

            // Filters may touch managed state — run on main thread, then complete.
            // Completing off-thread is fine for the SDK; we keep ordering by posting work.
            MainThreadDispatcher.Run(() =>
            {
                try
                {
                    var managed = _wrap(data);
                    var result = filter(managed);
                    if (result == null)
                    {
                        callback?.Call("run", (AndroidJavaObject)null);
                        return;
                    }

                    _applyBack?.Invoke(result, data);
                    callback?.Call("run", data);
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                    callback?.Call("run", data);
                }
            });
        }
    }

    sealed class ReportHandlerProxy : AndroidJavaProxy
    {
        IReportHandler _handler;

        public ReportHandlerProxy()
            : base("com.bugsee.library.contracts.reporting.ReportHandler")
        {
        }

        public void SetHandler(IReportHandler handler) => _handler = handler;

        public void onBeforeReportCreated(AndroidJavaObject report, bool isTerminating, AndroidJavaObject completion)
        {
            Invoke(report, isTerminating, completion, before: true);
        }

        public void onAfterReportCreated(AndroidJavaObject report, bool isTerminating, AndroidJavaObject completion)
        {
            Invoke(report, isTerminating, completion, before: false);
        }

        void Invoke(AndroidJavaObject report, bool isTerminating, AndroidJavaObject completion, bool before)
        {
            var handler = _handler;
            if (handler == null)
            {
                completion?.Call("run");
                return;
            }

            // Terminating: must not async round-trip.
            if (isTerminating)
            {
                try
                {
                    var wrapped = new AndroidReport(report);
                    if (before) handler.OnBeforeReportCreated(wrapped, true, () => { });
                    else handler.OnAfterReportCreated(wrapped, true, () => { });
                }
                catch (Exception ex) { Debug.LogException(ex); }
                completion?.Call("run");
                return;
            }

            MainThreadDispatcher.Run(() =>
            {
                var finished = false;
                Action done = () =>
                {
                    if (finished) return;
                    finished = true;
                    completion?.Call("run");
                };

                try
                {
                    var wrapped = new AndroidReport(report);
                    if (before) handler.OnBeforeReportCreated(wrapped, false, done);
                    else handler.OnAfterReportCreated(wrapped, false, done);
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                    done();
                }
            });
        }
    }

    sealed class LifecycleListenerProxy : AndroidJavaProxy
    {
        public LifecycleListenerProxy()
            : base("com.bugsee.library.contracts.lifecycle.LifecycleEventListener")
        {
        }

        public void onEvent(string eventType, AndroidJavaObject data)
        {
            // Fan-out through the public facade (event + app listener).
            Bugsee.Internal.MainThreadDispatcher.Run(() =>
            {
                try { Bugsee.Bugsee.HandleNativeLifecycle(eventType, null); }
                catch (Exception ex) { Debug.LogException(ex); }
            });
        }
    }

    sealed class RunnableProxy : AndroidJavaProxy
    {
        readonly Action _action;

        public RunnableProxy(Action action) : base("java.lang.Runnable")
        {
            _action = action;
        }

        public void run() => _action?.Invoke();
    }
}
#endif
