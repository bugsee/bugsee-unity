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
        }

        public static void EnsureWrapper()
        {
            EnsureRegistered();
            MainThreadDispatcher.Ensure();
            void Work()
            {
                PublishHostContext();
                _bugsee_ensure_wrapper(BugseePackageVersion.Version, BugseePackageVersion.Build);
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

                parts.Add(JsonString(kv.Key) + ":" + JsonString(kv.Value.ToString()));
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
                    Debug.LogException(ex);
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
            _bugsee_complete_filter(requestId, 1, ToNativeMapJson(ios.ToResultJson(), "headers"));
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

            var dto = JsonUtility.FromJson<IosBreadcrumbDto>(
                IosJsonNormalize.NormalizeBreadcrumb(json ?? "{}"));
            var managed = new IosBreadcrumb(dto);
            var result = filter(managed);
            if (result == null)
            {
                _bugsee_complete_filter(requestId, 0, null);
                return;
            }

            var ios = result as IosBreadcrumb ?? managed;
            _bugsee_complete_filter(requestId, 1, ToNativeMapJson(ios.ToResultJson(), "data"));
        }

        [MonoPInvokeCallback(typeof(ReportCb))]
        static void OnReport(long requestId, int phase, int isTerminating, string json)
        {
            void Work()
            {
                try
                {
                    var handler = _reportHandler;
                    var dto = JsonUtility.FromJson<IosReportDto>(
                        IosJsonNormalize.NormalizeReport(json ?? "{}"));
                    var report = new IosReport(dto);

                    if (handler == null)
                    {
                        _bugsee_complete_report(requestId, null);
                        return;
                    }

                    // Terminating: sync handler + force complete (Android pattern).
                    // Process may die; must not wait on async done().
                    if (isTerminating != 0)
                    {
                        try
                        {
                            Action noop = () => { };
                            if (phase == 0)
                                handler.OnBeforeReportCreated(report, true, noop);
                            else
                                handler.OnAfterReportCreated(report, true, noop);
                        }
                        catch (Exception ex) { Debug.LogException(ex); }
                        _bugsee_complete_report(requestId, report.ToResultJson());
                        return;
                    }

                    var finished = false;
                    Action done = () =>
                    {
                        if (finished) return;
                        finished = true;
                        _bugsee_complete_report(requestId, report.ToResultJson());
                    };

                    if (phase == 0)
                        handler.OnBeforeReportCreated(report, false, done);
                    else
                        handler.OnAfterReportCreated(report, false, done);
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                    _bugsee_complete_report(requestId, null);
                }
            }

            if (isTerminating != 0 || MainThreadDispatcher.IsMainThread)
            {
                Work();
            }
            else
            {
                MainThreadDispatcher.Run(Work);
            }
        }

        [MonoPInvokeCallback(typeof(LifecycleCb))]
        static void OnLifecycle(string eventType, string dataJson)
        {
            object data = null;
            if (!string.IsNullOrEmpty(dataJson))
            {
                // Prefer a simple string payload when wrapped as {"value":"..."}.
                if (dataJson.IndexOf("\"value\"", StringComparison.Ordinal) >= 0)
                {
                    try
                    {
                        var box = JsonUtility.FromJson<StringBox>(dataJson);
                        data = box != null ? box.value : dataJson;
                    }
                    catch
                    {
                        data = dataJson;
                    }
                }
                else
                {
                    data = dataJson;
                }
            }

            MainThreadDispatcher.Run(() =>
            {
                try { global::Bugsee.Bugsee.HandleNativeLifecycle(eventType, data); }
                catch (Exception ex) { Debug.LogException(ex); }
            });
        }

        /// <summary>
        /// JsonUtility emits kv arrays; native Apply* expects object maps for headers/data.
        /// </summary>
        internal static string ToNativeMapJson(string json, string field)
        {
            if (string.IsNullOrEmpty(json)) return "{}";
            var key = "\"" + field + "\":";
            var idx = json.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return json;
            var start = idx + key.Length;
            while (start < json.Length && char.IsWhiteSpace(json[start])) start++;
            if (start >= json.Length || json[start] != '[') return json;

            var end = start;
            var depth = 0;
            var inString = false;
            for (var i = start; i < json.Length; i++)
            {
                var c = json[i];
                if (inString)
                {
                    if (c == '\\' && i + 1 < json.Length) { i++; continue; }
                    if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == '[') depth++;
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0) { end = i; break; }
                }
            }
            if (end <= start) return json;

            var arrayJson = json.Substring(start, end - start + 1);
            var map = ArrayToObject(arrayJson);
            // Parse miss on non-empty array → keep original (avoid wiping native maps).
            if (map == null) return json;
            return json.Substring(0, start) + map + json.Substring(end + 1);
        }

        /// <returns>Object JSON, or null when parse failed for a non-empty array.</returns>
        static string ArrayToObject(string arrayJson)
        {
            // Expect [{"key":"a","value":"b"}, ...]
            if (string.IsNullOrEmpty(arrayJson) || arrayJson == "[]") return "{}";
            var parts = new System.Collections.Generic.List<string>();
            var i = 1;
            while (i < arrayJson.Length - 1)
            {
                while (i < arrayJson.Length && (char.IsWhiteSpace(arrayJson[i]) || arrayJson[i] == ',')) i++;
                if (i >= arrayJson.Length - 1) break;
                if (arrayJson[i] != '{') return null;
                var objEnd = FindMatchingBrace(arrayJson, i);
                if (objEnd < 0) return null;
                var obj = arrayJson.Substring(i, objEnd - i + 1);
                var k = ExtractStringField(obj, "key");
                var v = ExtractStringField(obj, "value");
                if (k != null)
                {
                    parts.Add(JsonString(k) + ":" + (v == null ? "null" : JsonString(v)));
                }
                i = objEnd + 1;
            }
            // Leftover non-whitespace after the loop → incomplete parse.
            while (i < arrayJson.Length - 1 && char.IsWhiteSpace(arrayJson[i])) i++;
            if (i < arrayJson.Length - 1) return null;
            if (parts.Count == 0) return null;
            return "{" + string.Join(",", parts.ToArray()) + "}";
        }

        static int FindMatchingBrace(string s, int openIdx)
        {
            var depth = 0;
            var inString = false;
            for (var i = openIdx; i < s.Length; i++)
            {
                var c = s[i];
                if (inString)
                {
                    if (c == '\\' && i + 1 < s.Length) { i++; continue; }
                    if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        static string ExtractStringField(string obj, string field)
        {
            var key = "\"" + field + "\":";
            var idx = obj.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return null;
            var i = idx + key.Length;
            while (i < obj.Length && char.IsWhiteSpace(obj[i])) i++;
            if (i >= obj.Length) return null;
            if (obj.Substring(i).StartsWith("null", StringComparison.Ordinal)) return null;
            if (obj[i] != '"') return null;
            var end = i + 1;
            while (end < obj.Length)
            {
                if (obj[end] == '\\' && end + 1 < obj.Length) { end += 2; continue; }
                if (obj[end] == '"') break;
                end++;
            }
            if (end >= obj.Length) return null;
            return obj.Substring(i + 1, end - i - 1)
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\")
                .Replace("\\n", "\n")
                .Replace("\\r", "\r");
        }

        static string JsonString(string s)
        {
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        [Serializable]
        sealed class StringBox
        {
            public string value;
        }

        delegate void FilterCb(long requestId, int kind, string json);
        delegate void ReportCb(long requestId, int phase, int isTerminating, string json);
        delegate void LifecycleCb(string eventType, string dataJson);

        [DllImport("__Internal")]
        static extern void _bugsee_register_unity_callbacks(FilterCb filterCb, ReportCb reportCb, LifecycleCb lifecycleCb);

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
