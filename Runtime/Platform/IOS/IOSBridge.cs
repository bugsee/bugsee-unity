#if UNITY_IOS && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Bugsee.Contracts.Appearance;
using Bugsee.Contracts.Exchange;
using Bugsee.Contracts.Feedback;
using Bugsee.Contracts.Lifecycle;
using Bugsee.Contracts.Options;
using Bugsee.WrapperPolicy;
using Bugsee.Contracts.Reporting;
using Bugsee.Internal;
using Bugsee.Platform;
using UnityEngine;

namespace Bugsee.Platform.IOS
{
    /// <summary>
    /// P/Invoke bridge to <c>Plugins/iOS/BugseeUnityBridge.mm</c> /
    /// <c>BugseeUnityCallbacks.mm</c> + Bugsee.xcframework.
    /// Native crash reporting is owned by the iOS SDK when
    /// <see cref="Options.DetectAndReportCrash"/> is enabled at Launch.
    /// </summary>
    sealed class IOSBridge : IBugseeNativeBridge
    {
        bool _launched;
        bool _blackout;
        IosAppearance _appearance;

        public bool IsSupported => true;

        public IFeedback Feedback { get; } = new IosFeedback();

        public void EnsureWrapperRegistered() => IosNativeCallbacks.EnsureWrapper();

        public void Launch(string appToken, IDictionary<string, object> options)
        {
            ManagedExceptionPayload.EnsureBuildIdentity();
            if (!MainThreadDispatcher.RunSyncLifecycle(() =>
                {
                    EnsureWrapperRegistered();
                    _bugsee_launch(appToken, ToJsonObject(OptionPlatformGate.ForIos(options)));
                    _launched = true;
                    ExceptionPipeline.Install(this, options);
                    HostLogForwarder.InstallOnce(this);
                }))
            {
                Debug.LogError("[Bugsee] Launch timed out waiting for the Unity main thread.");
            }
        }

        public void Relaunch(IDictionary<string, object> options)
        {
            if (!MainThreadDispatcher.RunSyncLifecycle(() =>
                {
                    _bugsee_relaunch(ToJsonObject(OptionPlatformGate.ForIos(options)));
                    ExceptionPipeline.Install(this, options);
                    HostLogForwarder.InstallOnce(this);
                }))
            {
                Debug.LogError("[Bugsee] Relaunch timed out waiting for the Unity main thread.");
            }
        }

        public void Stop(Action completion = null)
        {
            if (!MainThreadDispatcher.RunSyncLifecycle(() =>
                {
                    HostLogForwarder.Uninstall();
                    ExceptionPipeline.Uninstall();
                    _bugsee_clear_wrapper_channel();
                    _bugsee_stop();
                    _launched = false;
                    completion?.Invoke();
                }))
            {
                Debug.LogError("[Bugsee] Stop timed out waiting for the Unity main thread.");
            }
        }

        public bool GetLaunched() => _launched && _bugsee_get_launched();

        public BugseeStatus GetStatus() =>
            GetLaunched() ? BugseeStatus.Launched : BugseeStatus.Stopped;

        public void StartBlackout()
        {
            _bugsee_start_blackout();
            _blackout = true;
        }

        public void EndBlackout()
        {
            _bugsee_end_blackout();
            _blackout = false;
        }

        public bool IsBlackout() => _blackout;

        public void ChannelLog(string message, LogLevel level)
        {
            if (string.IsNullOrEmpty(message)) return;
            _bugsee_channel_log(message, (int)level, WrapperLogSourcePolicy.Resolve(null));
        }

        public void Log(string message, LogLevel level) =>
            _bugsee_log(message ?? "", (int)level);

        public void Trace(string name, object value) =>
            _bugsee_trace(name ?? "", ToJsonValue(value));

        public void Event(string name, IDictionary<string, object> parameters) =>
            _bugsee_event(name ?? "", ToJsonObject(parameters));

        public void LogException(Exception exception, IDictionary<string, object> options = null)
        {
            if (exception == null) return;
            LogExceptionPayload(ManagedExceptionPayload.FromException(exception), options);
        }

        public void LogUnhandledException(Exception exception, IDictionary<string, object> options = null)
        {
            if (exception == null) return;
            LogUnhandledExceptionPayload(ManagedExceptionPayload.FromException(exception), options);
        }

        public void LogExceptionPayload(
            ManagedExceptionPayload.Payload payload,
            IDictionary<string, object> options = null)
        {
            if (payload == null) return;
            _bugsee_logException("UnityManagedException", ManagedExceptionPayload.ToJson(payload), true);
        }

        public void LogUnhandledExceptionPayload(
            ManagedExceptionPayload.Payload payload,
            IDictionary<string, object> options = null)
        {
            if (payload == null) return;
            _bugsee_logException("UnityManagedException", ManagedExceptionPayload.ToJson(payload), false);
        }

        public void TestCrash() => _bugsee_test_crash();

        public void ShowReportDialog(string summary, string description, IssueSeverity severity, IList<string> labels) =>
            _bugsee_show_report(summary ?? "", description ?? "", (int)severity, ToJsonStringArray(labels));

        public void Upload(string summary, string description, IssueSeverity severity, IList<string> labels) =>
            _bugsee_upload(summary ?? "", description ?? "", (int)severity, ToJsonStringArray(labels));

        public void SetUserIdentifier(string userIdentifier) =>
            _bugsee_set_email(userIdentifier ?? "");

        public string GetUserIdentifier() => ConsumeNativeString(_bugsee_get_email());

        public void ClearUserIdentifier() => _bugsee_clear_email();

        public void SetAttribute(string key, object value) =>
            _bugsee_set_attribute(key ?? "", ToJsonValue(value));

        public object GetAttribute(string key)
        {
            // Native returns JSON when possible, otherwise description text.
            return ConsumeNativeString(_bugsee_get_attribute(key ?? ""));
        }

        public void ClearAttribute(string key) => _bugsee_clear_attribute(key ?? "");

        public void ClearAllAttributes() => _bugsee_clear_all_attributes();

        public void AddSecureRectangle(int left, int top, int right, int bottom) =>
            _bugsee_add_secure_rect(left, top, right - left, bottom - top);

        public void RemoveSecureRectangle(int left, int top, int right, int bottom) =>
            _bugsee_remove_secure_rect(left, top, right - left, bottom - top);

        public void RemoveAllSecureRectangles() => _bugsee_remove_all_secure_rects();

        public void CaptureViewHierarchy() => _bugsee_capture_view_hierarchy();

        public void ResetVideoCapturePermission() { }

        public IAppearance GetAppearance() =>
            _appearance ?? (_appearance = new IosAppearance());

        public void SetNetworkEventFilter(EventFilter<INetworkEvent> filter) =>
            IosNativeCallbacks.SetNetworkFilter(filter);

        public void SetLogEventFilter(EventFilter<ILogEvent> filter) =>
            IosNativeCallbacks.SetLogFilter(filter);

        public void SetBreadcrumbFilter(EventFilter<IBreadcrumb> filter) =>
            IosNativeCallbacks.SetBreadcrumbFilter(filter);

        public void SetReportHandler(IReportHandler handler) =>
            IosNativeCallbacks.SetReportHandler(handler);

        public void SetLifecycleEventListener(ILifecycleEventListener listener)
        {
            // App listener is held on the Bugsee facade; wrapper onLifecycleEvent fans out there.
            EnsureWrapperRegistered();
        }

        static string ConsumeNativeString(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return null;
            try
            {
                return Marshal.PtrToStringAnsi(ptr);
            }
            finally
            {
                _bugsee_free(ptr);
            }
        }

        static string ToJsonStringArray(IList<string> labels)
        {
            if (labels == null || labels.Count == 0) return null;
            var parts = new List<string>(labels.Count);
            foreach (var label in labels)
            {
                if (label == null) continue;
                parts.Add(JsonString(label));
            }
            return parts.Count == 0 ? null : "[" + string.Join(",", parts.ToArray()) + "]";
        }

        static string ToJsonObject(IDictionary<string, object> map)
        {
            if (map == null || map.Count == 0) return null;
            var parts = new List<string>();
            foreach (var kv in map)
            {
                if (kv.Key == null || kv.Value == null) continue;
                parts.Add(JsonString(kv.Key) + ":" + ToJsonLiteral(kv.Value));
            }
            return "{" + string.Join(",", parts.ToArray()) + "}";
        }

        static string ToJsonValue(object value)
        {
            if (value == null) return "null";
            if (value is string || value is bool || value is byte || value is sbyte
                || value is short || value is ushort || value is int || value is uint
                || value is long || value is ulong || value is float || value is double
                || value is decimal)
            {
                return ToJsonLiteral(value);
            }
            return JsonString(value.ToString());
        }

        static string ToJsonLiteral(object value)
        {
            switch (value)
            {
                case bool b:
                    return b ? "true" : "false";
                case string s:
                    return JsonString(s);
                case float f:
                    return f.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case double d:
                    return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case decimal m:
                    return m.ToString(System.Globalization.CultureInfo.InvariantCulture);
                default:
                    return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        static string JsonString(string s)
        {
            if (s == null) return "null";
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r") + "\"";
        }

        [DllImport("__Internal")] static extern void _bugsee_launch(string appToken, string optionsJson);
        [DllImport("__Internal")] static extern void _bugsee_relaunch(string optionsJson);
        [DllImport("__Internal")] static extern void _bugsee_stop();
        [DllImport("__Internal")] static extern void _bugsee_clear_wrapper_channel();
        [DllImport("__Internal")] static extern bool _bugsee_get_launched();
        [DllImport("__Internal")] static extern void _bugsee_start_blackout();
        [DllImport("__Internal")] static extern void _bugsee_end_blackout();
        [DllImport("__Internal")] static extern void _bugsee_log(string message, int level);
        [DllImport("__Internal")] static extern void _bugsee_channel_log(string message, int level, int source);
        [DllImport("__Internal")] static extern void _bugsee_trace(string name, string valueJson);
        [DllImport("__Internal")] static extern void _bugsee_event(string name, string paramsJson);
        [DllImport("__Internal")] static extern void _bugsee_logException(string name, string reason, bool handled);
        [DllImport("__Internal")] static extern void _bugsee_test_crash();
        [DllImport("__Internal")] static extern void _bugsee_show_report(string summary, string description, int severity, string labelsJson);
        [DllImport("__Internal")] static extern void _bugsee_upload(string summary, string description, int severity, string labelsJson);
        [DllImport("__Internal")] static extern void _bugsee_set_attribute(string key, string valueJson);
        [DllImport("__Internal")] static extern IntPtr _bugsee_get_attribute(string key);
        [DllImport("__Internal")] static extern void _bugsee_clear_attribute(string key);
        [DllImport("__Internal")] static extern void _bugsee_clear_all_attributes();
        [DllImport("__Internal")] static extern void _bugsee_set_email(string email);
        [DllImport("__Internal")] static extern IntPtr _bugsee_get_email();
        [DllImport("__Internal")] static extern void _bugsee_clear_email();
        [DllImport("__Internal")] static extern IntPtr _bugsee_get_device_id();
        [DllImport("__Internal")] static extern void _bugsee_add_secure_rect(float x, float y, float w, float h);
        [DllImport("__Internal")] static extern void _bugsee_remove_secure_rect(float x, float y, float w, float h);
        [DllImport("__Internal")] static extern void _bugsee_remove_all_secure_rects();
        [DllImport("__Internal")] static extern void _bugsee_capture_view_hierarchy();
        [DllImport("__Internal")] static extern void _bugsee_feedback_show();
        [DllImport("__Internal")] static extern void _bugsee_feedback_set_greeting(string message);
        [DllImport("__Internal")] static extern void _bugsee_appearance_set_color(string propertyName, int r, int g, int b, int a);
        [DllImport("__Internal")] static extern IntPtr _bugsee_appearance_get_color(string propertyName);
        [DllImport("__Internal")] static extern void _bugsee_appearance_set_string(string propertyName, string propertyValue);
        [DllImport("__Internal")] static extern IntPtr _bugsee_appearance_get_string(string propertyName);
        [DllImport("__Internal")] static extern void _bugsee_free(IntPtr ptr);

        sealed class IosFeedback : IFeedback
        {
            public void ShowFeedbackUi() => _bugsee_feedback_show();
            public void SetDefaultGreeting(string greeting) =>
                _bugsee_feedback_set_greeting(greeting ?? "");
            public void SetListener(IFeedbackListener listener) { }
        }

        sealed class IosAppearance : IAppearance
        {
            readonly Dictionary<string, object> _cache = new Dictionary<string, object>();

            public IAppearance SetColor(string propertyName, Color32? color)
            {
                if (string.IsNullOrEmpty(propertyName) || !color.HasValue) return this;
                var c = color.Value;
                _bugsee_appearance_set_color(propertyName, c.r, c.g, c.b, c.a);
                _cache[propertyName] = c;
                return this;
            }

            public Color32? GetColor(string propertyName)
            {
                if (string.IsNullOrEmpty(propertyName)) return null;
                var hex = ConsumeNativeString(_bugsee_appearance_get_color(propertyName));
                if (string.IsNullOrEmpty(hex) || hex[0] != '#' || hex.Length < 9) return null;
                // #AARRGGBB
                byte a = Convert.ToByte(hex.Substring(1, 2), 16);
                byte r = Convert.ToByte(hex.Substring(3, 2), 16);
                byte g = Convert.ToByte(hex.Substring(5, 2), 16);
                byte b = Convert.ToByte(hex.Substring(7, 2), 16);
                return new Color32(r, g, b, a);
            }

            public IAppearance SetString(string propertyName, string value)
            {
                if (string.IsNullOrEmpty(propertyName)) return this;
                _bugsee_appearance_set_string(propertyName, value ?? "");
                _cache[propertyName] = value;
                return this;
            }

            public string GetString(string propertyName)
            {
                if (string.IsNullOrEmpty(propertyName)) return null;
                return ConsumeNativeString(_bugsee_appearance_get_string(propertyName));
            }

            public IReadOnlyDictionary<string, object> ToMap() =>
                new Dictionary<string, object>(_cache);
        }
    }
}
#endif
