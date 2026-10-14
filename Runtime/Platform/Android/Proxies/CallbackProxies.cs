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
                try { callback?.Call("run", data); }
                finally { ReleaseArgs(data, callback); }
                return;
            }

            // Filters may touch managed state — run on main thread, then complete.
            // The AndroidJavaObject arguments are backed by JNI global references, not the proxy
            // call's local frame, so they stay valid across the hop; release them deterministically
            // once the callback has run rather than waiting for the finalizer.
            MainThreadDispatcher.Run(() =>
            {
                TNative managed = null;
                try
                {
                    managed = _wrap(data);
                    var result = filter(managed);
                    callback?.Call("run", result == null ? (AndroidJavaObject)null : data);
                }
                catch (Exception ex)
                {
                    Debug.Log("filter-failed " + ex.GetType().Name);
                    callback?.Call("run", (AndroidJavaObject)null);
                }
                finally
                {
                    // The wrapper must not outlive the native reference: a stored event fails with
                    // ObjectDisposedException instead of touching a released object.
                    (managed as IReleasableNativeEvent)?.ReleaseNative();
                    ReleaseArgs(data, callback);
                }
            });
        }

        static void ReleaseArgs(AndroidJavaObject data, AndroidJavaObject callback)
        {
            try { data?.Dispose(); } catch (Exception) { }
            try { callback?.Dispose(); } catch (Exception) { }
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

        static void ReleaseArgs(AndroidJavaObject report, AndroidJavaObject completion)
        {
            try { report?.Dispose(); } catch (Exception) { }
            try { completion?.Dispose(); } catch (Exception) { }
        }

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
                try { completion?.Call("run"); }
                finally { ReleaseArgs(report, completion); }
                return;
            }

            if (isTerminating)
            {
                try { completion?.Call("run"); }
                finally { ReleaseArgs(report, completion); }
                return;
            }

            MainThreadDispatcher.Run(() =>
            {
                var finished = false;
                Action done = () =>
                {
                    if (finished) return;
                    finished = true;
                    try { completion?.Call("run"); }
                    finally { try { completion?.Dispose(); } catch (Exception) { } }
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

        // One overload only. With two (AndroidJavaObject / string) Unity's JNI cannot choose when the
        // data is null and throws "Ambiguous overloads found for onEvent". `object` accepts a Java
        // map, a string (Android may pass the data as a JSON String) and null.
        public void onEvent(string eventType, object data)
        {
            object managedData = null;
            try
            {
                var javaData = data as AndroidJavaObject;
                // Unbox on the calling (often background) thread so only managed values cross the
                // main-thread hop, then release the argument's global reference.
                if (javaData != null)
                {
                    try { managedData = AndroidJavaConverters.UnboxLifecycleData(javaData); }
                    finally { javaData.Dispose(); }
                }
                else managedData = data;
            }
            catch (Exception ex) { Debug.LogException(ex); }

            DispatchLifecycle(eventType, managedData);
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

    sealed class BooleanCallback1Proxy : AndroidJavaProxy
    {
        readonly Action<bool> _onResult;

        public BooleanCallback1Proxy(Action<bool> onResult)
            : base("com.bugsee.library.contracts.common.Callback1")
        {
            _onResult = onResult;
        }

        // Callback1<Boolean>.run is generic, so the SDK passes a java.lang.Boolean; Unity hands it
        // over already unboxed as System.Boolean (a run(AndroidJavaObject) overload is never matched:
        // "No such proxy method: run(System.Boolean)"). `object` covers both shapes and null.
        public void run(object value)
        {
            if (value is bool flag)
            {
                _onResult?.Invoke(flag);
                return;
            }

            if (value is AndroidJavaObject javaBoolean)
            {
                try { _onResult?.Invoke(javaBoolean.Call<bool>("booleanValue")); }
                finally { javaBoolean.Dispose(); }
                return;
            }

            _onResult?.Invoke(false);
        }
    }

    sealed class ReportCreationListenerProxy : AndroidJavaProxy
    {
        readonly AndroidManagedReport _snapshot;
        readonly AndroidJavaClass _bugsee;
        readonly ulong _uploadFence;

        public ReportCreationListenerProxy(
            AndroidManagedReport snapshot,
            AndroidJavaClass bugsee,
            ulong uploadFence)
            : base("com.bugsee.library.contracts.reporting.ReportCreationListener")
        {
            _snapshot = snapshot;
            _bugsee = bugsee;
            _uploadFence = uploadFence;
        }

        public void onCreated(AndroidJavaObject report)
        {
            try
            {
                if (!AndroidManagedReportUploadFence.IsActive(_uploadFence))
                    return;

                if (report == null)
                {
                    Debug.LogError("[Bugsee] CreateReport failed.");
                    return;
                }

                if (!AndroidManagedReportUploadFence.IsActive(_uploadFence))
                    return;
                _snapshot.ApplyTo(report);
                if (!AndroidManagedReportUploadFence.IsActive(_uploadFence))
                    return;
                _bugsee.CallStatic("upload", report);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
            finally
            {
                _snapshot?.ReleaseSnapshotFiles();
                try { report?.Dispose(); } catch (Exception) { }
            }
        }
    }
}
#endif
