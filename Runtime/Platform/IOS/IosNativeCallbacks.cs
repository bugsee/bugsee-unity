#if UNITY_IOS && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AOT;
using Bugsee.Contracts.Exchange;
using Bugsee.Contracts.Reporting;
using Bugsee.Internal;
using Bugsee.WrapperPolicy;
using UnityEngine;

namespace Bugsee.Platform.IOS
{
    /// <summary>
    /// MonoPInvokeCallback hub for iOS filters, report handler, and lifecycle.
    /// Native owns request tokens; managed completes via <c>_bugsee_complete_*</c>.
    /// </summary>
    static class IosNativeCallbacks
    {
        const int KindNetwork = 1;
        const int KindLog = 2;
        const int KindBreadcrumb = 3;

        static bool _registered;
        static EventFilter<INetworkEvent> _networkFilter;
        static EventFilter<ILogEvent> _logFilter;
        static EventFilter<IBreadcrumb> _breadcrumbFilter;
        static IReportHandler _reportHandler;
        static bool _hostContextPublished;

        public static void EnsureRegistered()
        {
            if (_registered) return;
            _registered = true;
            _bugsee_register_unity_callbacks(OnFilter, OnReport, OnLifecycle);
            _bugsee_register_data_request_callback(OnDataRequest);
        }

        public static void EnsureWrapper()
        {
            EnsureRegistered();
            MainThreadDispatcher.Ensure();
            void Work()
            {
                PublishHostContext();
                _bugsee_ensure_wrapper(BugseePackageVersion.Version, IosHostPolicy.WrapperBuild(BugseePackageVersion.Build));
            }

            if (MainThreadDispatcher.IsMainThread)
            {
                Work();
            }
            else
            {
                MainThreadDispatcher.RunSync(Work);
            }
        }

        static void PublishHostContext()
        {
            if (_hostContextPublished)
            {
                return;
            }

            var context = new Dictionary<string, object>
            {
                ["unity_version"] = Application.unityVersion ?? "",
                ["unity_platform"] = Application.platform.ToString(),
                ["product_name"] = Application.productName ?? "",
            };
            _bugsee_set_wrapper_context(WrapperContextJson(context));
            _hostContextPublished = true;
        }

        static string WrapperContextJson(Dictionary<string, object> map)
        {
            var parts = new List<string>(map.Count);
            foreach (var kv in map)
            {
                if (kv.Key == null || kv.Value == null)
                {
                    continue;
                }

                parts.Add(JsonWire.String(kv.Key) + ":" + JsonWire.String(InvariantText.Of(kv.Value)));
            }

            return "{" + string.Join(",", parts.ToArray()) + "}";
        }

        public static void SetNetworkFilter(EventFilter<INetworkEvent> filter)
        {
            EnsureRegistered();
            _networkFilter = filter;
            _bugsee_set_network_filter_enabled(filter != null ? 1 : 0);
        }

        public static void SetLogFilter(EventFilter<ILogEvent> filter)
        {
            EnsureRegistered();
            _logFilter = filter;
            _bugsee_set_log_filter_enabled(filter != null ? 1 : 0);
        }

        public static void SetBreadcrumbFilter(EventFilter<IBreadcrumb> filter)
        {
            EnsureRegistered();
            _breadcrumbFilter = filter;
            _bugsee_set_breadcrumb_filter_enabled(filter != null ? 1 : 0);
        }

        public static void SetReportHandler(IReportHandler handler)
        {
            EnsureWrapper();
            _reportHandler = handler;
        }

        [MonoPInvokeCallback(typeof(FilterCb))]
        static void OnFilter(long requestId, int kind, string json)
        {
            // Snapshot before the main-thread hop so in-flight events keep the filter that was active when native captured them.
            var networkFilter = _networkFilter;
            var logFilter = _logFilter;
            var breadcrumbFilter = _breadcrumbFilter;

            // Filters may touch Unity APIs — hop to main thread when needed.
            void Work()
            {
                try
                {
                    switch (kind)
                    {
                        case KindNetwork:
                            CompleteNetwork(requestId, json, networkFilter);
                            break;
                        case KindLog:
                            CompleteLog(requestId, json, logFilter);
                            break;
                        case KindBreadcrumb:
                            CompleteBreadcrumb(requestId, json, breadcrumbFilter);
                            break;
                        default:
                            _bugsee_complete_filter(requestId, FilterCompletion.Drop, null);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Debug.Log("filter-failed " + ex.GetType().Name);
                    _bugsee_complete_filter(requestId, FilterCompletion.OnThrow, null);
                }
            }

            if (MainThreadDispatcher.IsMainThread) Work();
            else MainThreadDispatcher.Run(Work);
        }

        static void CompleteNetwork(long requestId, string json, EventFilter<INetworkEvent> filter)
        {
            if (filter == null)
            {
                _bugsee_complete_filter(requestId, FilterCompletion.Keep, json);
                return;
            }

            var dto = JsonUtility.FromJson<IosNetworkDto>(IosJsonNormalize.NormalizeNetwork(json ?? "{}"));
            var managed = new IosNetworkEvent(dto);
            var result = filter(managed);
            if (result == null)
            {
                _bugsee_complete_filter(requestId, 0, null);
                return;
            }

            var ios = result as IosNetworkEvent ?? managed;
            if (ReferenceEquals(ios, managed))
            {
                // Stage is read-only, so the SDK's own type string goes back unchanged.
                _bugsee_complete_filter(requestId, 1, ToNativeMapJson(ios.ToResultJson(), "headers"));
                return;
            }

            // A replacement event (e.g. from the exchange factory) stores the managed stage name; the SDK only
            // recognises its own types, so map it the same way channel submit does.
            var nativeStage = IosNetworkEvent.ToNativeStage(ios.Stage);
            if (nativeStage == null)
            {
                _bugsee_complete_filter(requestId, FilterCompletion.Drop, null);
                return;
            }

            _bugsee_complete_filter(requestId, 1, ToNativeMapJson(ios.ToReplacementJson(nativeStage), "headers"));
        }

        static void CompleteLog(long requestId, string json, EventFilter<ILogEvent> filter)
        {
            if (filter == null)
            {
                _bugsee_complete_filter(requestId, FilterCompletion.Keep, json);
                return;
            }

            var dto = JsonUtility.FromJson<IosLogDto>(json ?? "{}");
            var managed = new IosLogEvent(dto);
            var result = filter(managed);
            if (result == null)
            {
                _bugsee_complete_filter(requestId, 0, null);
                return;
            }

            var ios = result as IosLogEvent ?? managed;
            _bugsee_complete_filter(requestId, 1, ios.ToResultJson());
        }

        static void CompleteBreadcrumb(long requestId, string json, EventFilter<IBreadcrumb> filter)
        {
            if (filter == null)
            {
                _bugsee_complete_filter(requestId, FilterCompletion.Keep, json);
                return;
            }

            // data is read separately: JsonUtility cannot hold nested values or keep a scalar's kind.
            var dto = JsonUtility.FromJson<IosBreadcrumbDto>(json ?? "{}");
            var managed = new IosBreadcrumb(dto, IosBridgeJson.ReadObjectField(json, "data"));
            var result = filter(managed);
            if (result == null)
            {
                _bugsee_complete_filter(requestId, 0, null);
                return;
            }

            var ios = result as IosBreadcrumb ?? managed;
            _bugsee_complete_filter(requestId, 1, ios.ToResultJson());
        }

        [MonoPInvokeCallback(typeof(ReportCb))]
        static void OnReport(long requestId, int phase, int isTerminating, string json)
        {
            if (isTerminating != 0)
            {
                _bugsee_complete_report(requestId, null);
                return;
            }

            // The SDK stops waiting for the handler after 30 s and carries on with the report unchanged
            // (the ReportHandlerCallbackTimeout option is not read on iOS). A handler that would only start after
            // that is skipped, and a reply after it is acknowledged without touching the report.
            IosReport report = null;
            var guard = new LateReplyGuard(ReportHandlerDeadline.DefaultBudgetMs, LateReplyGuard.StopwatchMillis,
                applyEdits => CompleteReport(requestId, report, applyEdits && report != null ? report.ToResultJson() : null));

            // A handler that never answers must not keep the native entry, the report and its snapshot files alive.
            guard.ArmTimeout(1000, abandon => MainThreadDispatcher.Run(() =>
            {
                Debug.LogWarning("[Bugsee] The report handler did not call its completion callback by the SDK's deadline; it was abandoned.");
                abandon();
            }));

            void Work()
            {
                if (!guard.TryBegin()) return;
                try
                {
                    var handler = _reportHandler;
                    // attributes are read separately: JsonUtility cannot hold free-form values or keep a scalar's kind.
                    var dto = JsonUtility.FromJson<IosReportDto>(json ?? "{}");
                    var attributes = IosReport.ReadAttributes(json);
                    if (attributes == null)
                        Debug.LogWarning("[Bugsee] The report's attributes could not be read (malformed or nested too deep); the report handler sees none. Native keeps them unless the handler sets attributes or clears them all.");
                    report = new IosReport(dto, attributes);

                    if (handler == null)
                    {
                        guard.Abandon();
                        return;
                    }

                    if (phase == 0)
                        handler.OnBeforeReportCreated(report, false, guard.Done);
                    else
                        handler.OnAfterReportCreated(report, false, guard.Done);
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                    guard.Abandon();
                }
            }

            static void CompleteReport(long requestId, IosReport report, string resultJson)
            {
                _bugsee_complete_report(requestId, resultJson);
                report?.ReleaseSnapshotFiles();
            }

            if (MainThreadDispatcher.IsMainThread)
                Work();
            else
                MainThreadDispatcher.Run(Work);
        }

        [MonoPInvokeCallback(typeof(LifecycleCb))]
        static void OnLifecycle(string eventType, string dataJson)
        {
            // A string arrives as a JSON string literal, a dictionary as a JSON object: the same types as Android.
            object data = LifecyclePayload.Decode(dataJson);

            MainThreadDispatcher.Run(() =>
            {
                try { global::Bugsee.Bugsee.HandleNativeLifecycle(eventType, data); }
                catch (Exception ex) { Debug.LogException(ex); }
            });
        }

        // The native tree is in points. Unity renders at the screen's native scale, which on a Plus, mini or Display Zoom device
        // (or with a reduced render resolution) is not UIScreen.scale, so the ratio is taken from Unity's own width.
        static float UnityPixelsPerPoint() => _bugsee_pixels_per_point(Screen.width);

        // requestData from the SDK. Only "vh" gets an answer from managed code; the native side answers every other type itself.
        // The SDK asks on the main thread, which is Unity's, so the scene is read and answered inside this call.
        [MonoPInvokeCallback(typeof(DataRequestCb))]
        static void OnDataRequest(long requestId, string dataType)
        {
            try
            {
                ManagedViewHierarchy.Handle(dataType, UnityPixelsPerPoint, json => _bugsee_complete_data_request(requestId, json));
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                _bugsee_complete_data_request(requestId, null);
            }
        }

        /// <summary>
        /// JsonUtility emits kv arrays; native Apply* expects object maps for headers.
        /// </summary>
        internal static string ToNativeMapJson(string json, string field) =>
            IosBridgeJson.ToNativeMapJson(json, field);

        delegate void FilterCb(long requestId, int kind, string json);
        delegate void ReportCb(long requestId, int phase, int isTerminating, string json);
        delegate void LifecycleCb(string eventType, string dataJson);
        delegate void DataRequestCb(long requestId, string dataType);

        [DllImport("__Internal")]
        static extern void _bugsee_register_unity_callbacks(FilterCb filterCb, ReportCb reportCb, LifecycleCb lifecycleCb);

        [DllImport("__Internal")]
        static extern void _bugsee_register_data_request_callback(DataRequestCb dataRequestCb);

        [DllImport("__Internal")]
        static extern void _bugsee_complete_data_request(long requestId, string json);

        [DllImport("__Internal")]
        static extern float _bugsee_pixels_per_point(int unityPixelWidth);

        [DllImport("__Internal")]
        static extern void _bugsee_ensure_wrapper(string version, string build);

        [DllImport("__Internal")]
        static extern void _bugsee_set_wrapper_context(string json);

        [DllImport("__Internal")]
        static extern void _bugsee_set_network_filter_enabled(int enabled);

        [DllImport("__Internal")]
        static extern void _bugsee_set_log_filter_enabled(int enabled);

        [DllImport("__Internal")]
        static extern void _bugsee_set_breadcrumb_filter_enabled(int enabled);

        [DllImport("__Internal")]
        static extern void _bugsee_complete_filter(long requestId, int keep, string resultJson);

        [DllImport("__Internal")]
        static extern void _bugsee_complete_report(long requestId, string resultJson);
    }
}
#endif
