#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Threading;
using Bugsee.Contracts.Exchange;
using Bugsee.Contracts.Reporting;
using Bugsee.Internal;
using Bugsee.WrapperPolicy;
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

        readonly long _sdkTimeoutMs;

        /// <param name="sdkTimeoutMs">How long the SDK waits for this filter before it drops the event (0: never).</param>
        public EventFilterProxy(Func<AndroidJavaObject, TNative> wrap, long sdkTimeoutMs = 0)
            : base("com.bugsee.library.contracts.exchange.EventFilter")
        {
            _wrap = wrap;
            _sdkTimeoutMs = sdkTimeoutMs;
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

            // The SDK's window for the reply starts now; see FilterReplyWindow.
            var window = new FilterReplyWindow(_sdkTimeoutMs, LateReplyGuard.StopwatchMillis);

            // Filters may touch managed state — run on main thread, then complete.
            // The AndroidJavaObject arguments are backed by JNI global references, not the proxy
            // call's local frame, so they stay valid across the hop; release them deterministically
            // once the callback has run rather than waiting for the finalizer.
            MainThreadDispatcher.Run(() =>
            {
                TNative managed = null;
                if (!window.IsOpen)
                {
                    // The SDK dropped the event and may already have handed its callback to another one.
                    Debug.LogWarning("[Bugsee] A filter was skipped: the SDK stopped waiting for it before the Unity main loop could run it.");
                    ReleaseArgs(data, callback);
                    return;
                }

                try
                {
                    managed = _wrap(data);
                    var result = filter(managed);
                    Reply(window, callback, result == null ? (AndroidJavaObject)null : data);
                }
                catch (Exception ex)
                {
                    Debug.Log("filter-failed " + ex.GetType().Name);
                    Reply(window, callback, (AndroidJavaObject)null);
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

        // A reply after the SDK's window is withheld: the SDK has moved on and a late one would complete another event.
        static void Reply(FilterReplyWindow window, AndroidJavaObject callback, AndroidJavaObject value)
        {
            if (!window.IsOpen)
            {
                Debug.LogWarning("[Bugsee] A filter reply was withheld: it came after the SDK stopped waiting for it.");
                return;
            }
            callback?.Call("run", value);
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

            // The SDK waits for the handler at most ReportHandlerCallbackTimeout, then carries on with the report
            // unchanged. While the Unity loop is paused (app in the background) this hop sits in the queue, so a
            // handler that would only start after that is skipped and a reply after it is only acknowledged.
            var gate = new ReportEditGate();
            var guard = new LateReplyGuard(BudgetMillis(), LateReplyGuard.StopwatchMillis, _ =>
            {
                gate.Close(); // whatever the handler kept can no longer change the report
                try { completion?.Call("run"); }
                finally { try { completion?.Dispose(); } catch (Exception) { } }
            });

            // A handler that never answers must not keep the completion alive: abandon it after the SDK's deadline.
            guard.ArmTimeout(AbandonMarginMs, run =>
            {
                Debug.LogWarning("[Bugsee] The report handler did not call its completion callback by the SDK's deadline; it was abandoned.");
                try { using (AndroidJniScope.Enter()) run(); }
                catch (Exception ex) { Debug.LogWarning("[Bugsee] Abandoning the report handler failed: " + ex.Message); }
            });

            MainThreadDispatcher.Run(() =>
            {
                // The after-phase is often a notification (record the report, send analytics), so it still runs late,
                // with a completion that only acknowledges; the before-phase exists to edit the report and is skipped.
                var late = !guard.TryBegin();
                if (late && before)
                {
                    Debug.LogWarning("[Bugsee] The report handler was skipped: the SDK stopped waiting for it before the Unity main loop could run it.");
                    try { report?.Dispose(); } catch (Exception) { }
                    return;
                }

                try
                {
                    var wrapped = new AndroidReport(report, gate);
                    Action done = late ? (() => { }) : (Action)guard.Done;
                    if (before) handler.OnBeforeReportCreated(wrapped, false, done);
                    else handler.OnAfterReportCreated(wrapped, false, done);
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                    guard.Abandon();
                }
            });
        }

        /// <summary>Forget the cached timeout: the SDK was (re)launched, possibly with another one.</summary>
        internal static void InvalidateBudget() => Interlocked.Exchange(ref _budgetMs, -1);

        const long AbandonMarginMs = 1000;
        const long BudgetRefreshMs = 60000;
        static long _budgetMs = -1;
        static long _budgetReadAtMs;

        // The launch options are read over JNI, so at most once a minute; when they cannot be read (not launched, a
        // relaunch in progress) the last value stays, so a larger configured timeout is not cut to the default.
        static long BudgetMillis()
        {
            var now = LateReplyGuard.StopwatchMillis();
            var cached = Interlocked.Read(ref _budgetMs);
            if (cached >= 0 && now - Interlocked.Read(ref _budgetReadAtMs) < BudgetRefreshMs) return cached;
            try
            {
                var launch = global::Bugsee.Bugsee.GetLaunchOptions();
                if (launch != null)
                {
                    var budget = ReportHandlerDeadline.BudgetMillis(launch, true);
                    Interlocked.Exchange(ref _budgetMs, budget);
                    Interlocked.Exchange(ref _budgetReadAtMs, now);
                    return budget;
                }
            }
            catch (Exception) { }
            return cached >= 0 ? cached : ReportHandlerDeadline.DefaultBudgetMs;
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

    /// <summary>
    /// The SDK's <c>requestData</c> channel for the one flow Unity answers (the managed view hierarchy). Called on the Android
    /// main thread; the scene is read on Unity's thread and the reply is made from there, so this never blocks the UI thread.
    /// </summary>
    sealed class DataRequestProviderProxy : AndroidJavaProxy
    {
        public DataRequestProviderProxy()
            : base("com.bugsee.library.contracts.common.DataRequestProvider")
        {
        }

        public void requestData(string dataType, AndroidJavaObject callback)
        {
            if (callback == null) return;
            try
            {
                ManagedViewHierarchy.Handle(dataType, () => 1f, json => Reply(callback, json));
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                Reply(callback, null);
            }
        }

        static void Reply(AndroidJavaObject callback, string json)
        {
            try
            {
                if (json != null)
                {
                    callback.Call("onResult", json);
                }
                else
                {
                    using (var wrapper = new AndroidJavaClass("com.bugsee.unity.UnityWrapper"))
                        wrapper.CallStatic("replyNoData", callback);
                }
            }
            catch (Exception ex) { Debug.LogException(ex); }
            finally { callback.Dispose(); }
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
