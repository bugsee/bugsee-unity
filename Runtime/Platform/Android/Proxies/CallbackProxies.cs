#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using Bugsee.Contracts.Exchange;
using Bugsee.Contracts.Reporting;
using Bugsee.Internal;
using UnityEngine;

namespace Bugsee.Platform.Android
{
    /// <summary>
    /// Proxies Java <c>EventFilter&lt;T&gt;</c>. Managed wrappers mutate the native
    /// event in place; returning null drops the event.
    /// </summary>
    sealed class EventFilterProxy<TNative> : AndroidJavaProxy where TNative : class
    {
        readonly Func<AndroidJavaObject, TNative> _wrap;
        EventFilter<TNative> _filter;

        public EventFilterProxy(Func<AndroidJavaObject, TNative> wrap)
            : base("com.bugsee.library.contracts.exchange.EventFilter")
        {
            _wrap = wrap;
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
            MainThreadDispatcher.Run(() =>
            {
                try
                {
                    var managed = _wrap(data);
                    var result = filter(managed);
                    callback?.Call("run", result == null ? (AndroidJavaObject)null : data);
                }
                catch (Exception ex)
                {
                    Debug.Log("filter-failed " + ex.GetType().Name);
                    callback?.Call("run", (AndroidJavaObject)null);
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

            if (isTerminating)
            {
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
            // Unbox on the calling (often background) thread so we don't retain
            // a Java local ref across the main-thread hop.
            object managedData = null;
            try { managedData = AndroidJavaConverters.UnboxLifecycleData(data); }
            catch (Exception ex) { Debug.LogException(ex); }

            DispatchLifecycle(eventType, managedData);
        }

        // Android may pass Object as a JSON String; Unity JNI matches that exact
        // signature and otherwise logs "No such proxy method: onEvent(String,String)".
        public void onEvent(string eventType, string data)
        {
            DispatchLifecycle(eventType, data);
        }

        static void DispatchLifecycle(string eventType, object managedData)
        {
            MainThreadDispatcher.Run(() =>
            {
                // global:: avoids Bugsee → class Bugsee.Bugsee name collision in this namespace.
                try { global::Bugsee.Bugsee.HandleNativeLifecycle(eventType, managedData); }
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
