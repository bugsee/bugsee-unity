#if UNITY_IOS && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AOT;
using Bugsee.Contracts.Appearance;
using Bugsee.Contracts.Exchange;
using Bugsee.Contracts.Feedback;
using Bugsee.Contracts.Lifecycle;
using Bugsee.Contracts.Options;
using Bugsee.WrapperPolicy;
using Bugsee.Contracts.Reporting;
using UnityEngine;
using Bugsee.Internal;
using Bugsee.Platform;

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
        delegate void ManagedReportUploadNativeCallback(int succeeded, ulong uploadToken);
        delegate int DeleteCollectedDataShouldRunNativeCallback(int capturedGeneration);

        ulong _inFlightUploadToken;
        ulong _managedReportUploadFence;
        int _reportUploadGeneration;

        bool _launched;
        bool _blackout;
        IosAppearance _appearance;
        IosReport _openReport;
        IosLiveReport _openReportHandle;
        readonly object _uploadSnapshotReportsGate = new object();
        readonly Dictionary<ulong, IosReport> _uploadSnapshotReports = new Dictionary<ulong, IosReport>();
        readonly NetworkEventLaunchBuffer<INetworkEvent> _networkLaunchBuffer = new NetworkEventLaunchBuffer<INetworkEvent>();
        readonly NetworkEventLaunchBuffer<IosPendingBreadcrumb> _breadcrumbLaunchBuffer =
            new NetworkEventLaunchBuffer<IosPendingBreadcrumb>();

        public bool IsSupported => true;

        public IOSBridge()
        {
            _networkLaunchBuffer.SetSubmitHandler(SubmitNetworkEventToChannel);
            _breadcrumbLaunchBuffer.SetSubmitHandler(SubmitBreadcrumbToChannel);
        }

        public IFeedback Feedback { get; } = new IosFeedback();

        public void EnsureWrapperRegistered() => IosNativeCallbacks.EnsureWrapper();

        public void Launch(string appToken, IDictionary<string, object> options)
        {
            ManagedExceptionPayload.EnsureBuildIdentity();
            if (!MainThreadDispatcher.RunSyncLifecycle(() =>
                {
                    EnsureWrapperRegistered();
                    DeleteCollectedDataLaunchGeneration.BumpForLaunch();
                    _networkLaunchBuffer.BeginNewLaunchCycle();
                    _breadcrumbLaunchBuffer.BeginNewLaunchCycle();
                    _networkLaunchBuffer.SetPhase(NetworkLaunchPhase.BeforeLaunched);
                    _breadcrumbLaunchBuffer.SetPhase(NetworkLaunchPhase.BeforeLaunched);
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
                    DeleteCollectedDataLaunchGeneration.BumpForLaunch();
                    _networkLaunchBuffer.BeginNewLaunchCycle();
                    _breadcrumbLaunchBuffer.BeginNewLaunchCycle();
                    _networkLaunchBuffer.SetPhase(NetworkLaunchPhase.BeforeLaunched);
                    _breadcrumbLaunchBuffer.SetPhase(NetworkLaunchPhase.BeforeLaunched);
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
                    CancelManagedReportUpload();
                    _openReport?.ReleaseSnapshotFiles();
                    _openReport = null;
                    _openReportHandle = null;
                    _networkLaunchBuffer.SetPhase(NetworkLaunchPhase.Stopped);
                    _breadcrumbLaunchBuffer.SetPhase(NetworkLaunchPhase.Stopped);
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

        public void AddNetworkEvent(INetworkEvent networkEvent)
        {
            _networkLaunchBuffer.Enqueue(networkEvent);
        }

        public void NotifyLifecycle(string eventType)
        {
            if (eventType == LifecycleEvents.Launched)
            {
                _networkLaunchBuffer.SetPhaseFromLifecycle(NetworkLaunchPhase.Launched);
                _breadcrumbLaunchBuffer.SetPhaseFromLifecycle(NetworkLaunchPhase.Launched);
            }
            else if (eventType == LifecycleEvents.Stopped)
            {
                _networkLaunchBuffer.SetPhaseFromLifecycle(NetworkLaunchPhase.Stopped);
                _breadcrumbLaunchBuffer.SetPhaseFromLifecycle(NetworkLaunchPhase.Stopped);
            }
        }

        public void DeleteCollectedDataOnDevice()
        {
            var deleteGeneration = DeleteCollectedDataLaunchGeneration.CaptureForPendingDelete();
            if (GetLaunched())
            {
                if (!MainThreadDispatcher.RunSyncLifecycle(() =>
                    {
                        HostLogForwarder.Uninstall();
                        ExceptionPipeline.Uninstall();
                        _bugsee_clear_wrapper_channel();
                        CancelManagedReportUpload();
                        _openReport?.ReleaseSnapshotFiles();
                        _openReport = null;
                        _openReportHandle = null;
                        _networkLaunchBuffer.SetPhase(NetworkLaunchPhase.Stopped);
                        _breadcrumbLaunchBuffer.SetPhase(NetworkLaunchPhase.Stopped);
                        _launched = false;
                        _bugsee_delete_collected_data(deleteGeneration, ShouldRunDeleteCollectedDataNative);
                    }))
                {
                    Debug.LogError("[Bugsee] DeleteCollectedDataOnDevice timed out waiting for the Unity main thread.");
                }

                return;
            }

            CancelManagedReportUpload();
            _openReport?.ReleaseSnapshotFiles();
            _openReport = null;
            _openReportHandle = null;
            _networkLaunchBuffer.SetPhase(NetworkLaunchPhase.Stopped);
            _breadcrumbLaunchBuffer.SetPhase(NetworkLaunchPhase.Stopped);
            _bugsee_delete_collected_data(deleteGeneration, ShouldRunDeleteCollectedDataNative);
        }

        [MonoPInvokeCallback(typeof(DeleteCollectedDataShouldRunNativeCallback))]
        static int ShouldRunDeleteCollectedDataNative(int capturedGeneration) =>
            DeleteCollectedDataLaunchGeneration.ShouldRunDelete(capturedGeneration) ? 1 : 0;

        void CancelManagedReportUpload()
        {
            _reportUploadGeneration++;
            _managedReportUploadFence++;
            _inFlightUploadToken = 0;
            ReleaseAllUploadSnapshotReports();
            _bugsee_invalidate_managed_report_uploads();
        }

        void ReleaseAllUploadSnapshotReports()
        {
            lock (_uploadSnapshotReportsGate)
            {
                foreach (var kv in _uploadSnapshotReports)
                    kv.Value?.ReleaseSnapshotFiles();
                _uploadSnapshotReports.Clear();
            }
        }

        void ReleaseUploadSnapshotReport(ulong uploadToken)
        {
            IosReport report;
            lock (_uploadSnapshotReportsGate)
            {
                if (!_uploadSnapshotReports.TryGetValue(uploadToken, out report))
                    return;
                _uploadSnapshotReports.Remove(uploadToken);
            }

            report?.ReleaseSnapshotFiles();
        }

        public IReport CreateReport()
        {
            if (_openReport != null)
                throw new InvalidOperationException("a report is already open");
            _openReport = new IosReport(new IosReportDto());
            _openReportHandle = new IosLiveReport(_openReport);
            return _openReportHandle;
        }

        public void DiscardReport(IReport report)
        {
            if (report == null)
                throw new ArgumentNullException(nameof(report));
            if (!ReferenceEquals(report, _openReportHandle))
                throw new ArgumentException("Report was not created by CreateReport.", nameof(report));
            _reportUploadGeneration++;
            _openReport?.ReleaseSnapshotFiles();
            _openReport = null;
            _openReportHandle = null;
        }

        public void UploadReport(IReport report)
        {
            if (!ReferenceEquals(report, _openReportHandle))
                throw new ArgumentException("Report was not created by CreateReport.", nameof(report));
            if (_openReport == null)
                throw new InvalidOperationException("CreateReport failed.");
            if (!string.IsNullOrEmpty(_openReport.Email))
            {
                Debug.LogWarning(
                    "[Bugsee] Email on CreateReport is not supported on iOS managed upload; use session identity APIs.");
            }
            var snapshotReport = _openReport;
            var json = snapshotReport.ToResultJson();
            var uploadToken = (ulong)++_reportUploadGeneration;
            var uploadFence = _managedReportUploadFence;
            lock (_uploadSnapshotReportsGate)
                _uploadSnapshotReports[uploadToken] = snapshotReport;
            _openReport = null;
            _openReportHandle = null;
            _uploadCompletionBridge = this;
            _inFlightUploadToken = uploadToken;
            _bugsee_upload_managed_report(json, uploadToken, uploadFence, OnManagedReportCreateCompletion);
        }

        [MonoPInvokeCallback(typeof(ManagedReportUploadNativeCallback))]
        static void OnManagedReportCreateCompletion(int succeeded, ulong uploadToken)
        {
            // Instance is resolved through the static bridge reference set at upload time.
            if (_uploadCompletionBridge == null)
                return;
            _uploadCompletionBridge.HandleManagedReportCreateCompletion(succeeded != 0, uploadToken);
        }

        static IOSBridge _uploadCompletionBridge;

        void HandleManagedReportCreateCompletion(bool succeeded, ulong uploadToken)
        {
            ReleaseUploadSnapshotReport(uploadToken);
            if (uploadToken != _inFlightUploadToken)
                return;
            _inFlightUploadToken = 0;
            if (!succeeded)
                Debug.LogError("[Bugsee] CreateReport failed.");
        }

        static readonly DateTime BreadcrumbUnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public void AddBreadcrumb(string category, string message, string levelName)
        {
            var level = BreadcrumbLevelMap.ParseOrThrow(levelName);
            _breadcrumbLaunchBuffer.Enqueue(new IosPendingBreadcrumb
            {
                Category = category ?? "",
                Message = message ?? "",
                IosLevel = BreadcrumbLevelMap.ToIos(level),
                TimestampUnixSeconds = (DateTime.UtcNow - BreadcrumbUnixEpoch).TotalSeconds,
            });
        }

        void SubmitBreadcrumbToChannel(IosPendingBreadcrumb breadcrumb)
        {
            _bugsee_channel_breadcrumb(
                breadcrumb.Category,
                breadcrumb.Message,
                breadcrumb.IosLevel,
                breadcrumb.TimestampUnixSeconds);
        }

        public IBugseeExchangeFactory GetExchangeFactory() => IosExchangeFactory.Instance;

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

        public void SetUserIdentifier(string userIdentifier)
        {
            var normalized = UserIdentifierPolicy.ForSet(userIdentifier);
            if (normalized == null)
                ClearUserIdentifier();
            else
                _bugsee_set_email(normalized);
        }

        public string GetUserIdentifier() =>
            UserIdentifierPolicy.ForGet(ConsumeNativeString(_bugsee_get_email()));

        public void ClearUserIdentifier() => _bugsee_clear_email();

        public void SetAttribute(string key, object value)
        {
            var decision = AttributePolicy.Evaluate(key, value);
            if (!decision.Accepted)
                throw new ArgumentException(decision.Error, nameof(value));

            _bugsee_set_attribute(key ?? "", ToJsonValue(value));
            if (GetAttribute(key) == null)
                throw new ArgumentException("attribute '" + key + "' was dropped", nameof(value));
        }

        public object GetAttribute(string key)
        {
            // Native returns JSON when possible, otherwise description text.
            return ConsumeNativeString(_bugsee_get_attribute(key ?? ""));
        }

        public void ClearAttribute(string key) => _bugsee_clear_attribute(key ?? "");

        public void ClearAllAttributes() => _bugsee_clear_all_attributes();

        public void SetSecureBuffer(int display, int[] packed)
        {
            if (packed == null) return;
            _bugsee_set_secure_buffer(display, packed, packed.Length);
        }

        public float GetSecureRectSnapshotScale() => _bugsee_screen_scale();

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

        void SubmitNetworkEventToChannel(INetworkEvent networkEvent)
        {
            if (networkEvent == null)
                return;

            var iosEvent = networkEvent as IosNetworkEvent;
            if (iosEvent == null)
            {
                var factory = GetExchangeFactory();
                if (factory == null)
                    return;
                iosEvent = (IosNetworkEvent)factory.CreateNetworkEvent(
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    networkEvent.Stage,
                    string.IsNullOrEmpty(networkEvent.Id) ? null : networkEvent.Id,
                    networkEvent.Mechanism,
                    networkEvent.Method);
                if (iosEvent == null)
                    return;
                CopyNetworkFields(networkEvent, iosEvent);
            }

            var requiresFiltering = ChannelSubmit.NetworkRequiresFiltering() ? 1 : 0;
            var json = IosNativeCallbacks.ToNativeMapJson(iosEvent.ToResultJson(), "headers");
            _bugsee_channel_network(json, requiresFiltering);
        }

        static void CopyNetworkFields(INetworkEvent source, INetworkEvent target)
        {
            if (source == null || target == null || ReferenceEquals(source, target))
                return;
            target.Url = source.Url;
            target.Body = source.Body;
            target.Size = source.Size;
            target.ResponseCode = source.ResponseCode;
            target.StatusText = source.StatusText;
            target.ErrorShortMessage = source.ErrorShortMessage;
            target.ErrorDescription = source.ErrorDescription;
            target.Headers = source.Headers;
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
        [DllImport("__Internal")] static extern void _bugsee_channel_network(string eventJson, int requiresFiltering);
        [DllImport("__Internal")] static extern void _bugsee_channel_breadcrumb(
            string category,
            string message,
            int iosLevel,
            double timestampUnixSeconds);
        [DllImport("__Internal")] static extern void _bugsee_delete_collected_data(
            int capturedGeneration,
            DeleteCollectedDataShouldRunNativeCallback shouldRun);
        [DllImport("__Internal")] static extern void _bugsee_cancel_managed_report_upload(ulong uploadId);
        [DllImport("__Internal")] static extern void _bugsee_invalidate_managed_report_uploads();
        [DllImport("__Internal")] static extern void _bugsee_upload_managed_report(
            string reportJson,
            ulong uploadId,
            ulong uploadFence,
            ManagedReportUploadNativeCallback callback);
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
        [DllImport("__Internal")] static extern void _bugsee_set_secure_buffer(int display, int[] packed, int packedLength);
        [DllImport("__Internal")] static extern float _bugsee_screen_scale();
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

        /// <summary>Managed report snapshot from <see cref="CreateReport"/>; native work runs at upload.</summary>
        internal sealed class IosLiveReport : IReport
        {
            readonly IosReport _inner;

            internal IosLiveReport(IosReport inner)
            {
                _inner = inner;
            }

            public string Id => _inner.Id;
            public IssueType Type => _inner.Type;

            public string Summary
            {
                get => _inner.Summary;
                set => _inner.Summary = value;
            }

            public string Description
            {
                get => _inner.Description;
                set => _inner.Description = value;
            }

            public string Email
            {
                get => _inner.Email;
                set => _inner.Email = value;
            }

            public IssueSeverity? Severity
            {
                get => _inner.Severity;
                set => _inner.Severity = value;
            }

            public IReadOnlyDictionary<string, object> Attributes => _inner.Attributes;

            public object GetAttribute(string name) => _inner.GetAttribute(name);

            public void SetAttribute(string name, object value) => _inner.SetAttribute(name, value);

            public void RemoveAttribute(string name) => _inner.RemoveAttribute(name);

            public void ClearAllAttributes() => _inner.ClearAllAttributes();

            public IReadOnlyList<string> Labels => _inner.Labels;

            public void AddLabel(string label) => _inner.AddLabel(label);

            public void ClearLabels() => _inner.ClearLabels();

            public void SetLabels(IEnumerable<string> labels) => _inner.SetLabels(labels);

            public IReadOnlyList<IAttachment> Attachments
            {
                get
                {
                    var inner = _inner.Attachments;
                    var wrapped = new List<IAttachment>(inner.Count);
                    for (var i = 0; i < inner.Count; i++)
                    {
                        if (inner[i] is IosAttachment iosAttachment)
                            wrapped.Add(new IosLiveAttachment(iosAttachment));
                        else
                            wrapped.Add(inner[i]);
                    }
                    return wrapped;
                }
            }

            public IAttachment AddAttachmentFile(string path, string name, string mimeType)
            {
                var att = _inner.AddAttachmentFile(path, name, mimeType) as IosAttachment;
                return att == null ? null : new IosLiveAttachment(att);
            }

            public IAttachment AddAttachmentBytes(byte[] data, string name, string mimeType)
            {
                var att = _inner.AddAttachmentBytes(data, name, mimeType) as IosAttachment;
                return att == null ? null : new IosLiveAttachment(att);
            }

            public void ClearAttachments() => _inner.ClearAttachments();
        }

        sealed class IosLiveAttachment : IAttachment
        {
            readonly IosAttachment _inner;

            internal IosLiveAttachment(IosAttachment inner)
            {
                _inner = inner;
            }

            public string Name
            {
                get => _inner.Name;
                set => _inner.Name = value;
            }

            public string Filename
            {
                get => _inner.Filename;
                set => _inner.Filename = value;
            }

            public string MimeType
            {
                get => _inner.MimeType;
                set => _inner.MimeType = value;
            }

            public void SetData(byte[] data) => _inner.SetData(data);

            public void SetData(string text) => _inner.SetData(text);
        }
    }

    sealed class IosExchangeFactory : IBugseeExchangeFactory
    {
        public static readonly IosExchangeFactory Instance = new IosExchangeFactory();

        public INetworkEvent CreateNetworkEvent(
            long timestamp,
            NetworkEventStage stage,
            string id,
            string mechanism,
            string method) =>
            new IosNetworkEvent(new IosNetworkDto
            {
                id = string.IsNullOrEmpty(id) ? Guid.NewGuid().ToString("N") : id,
                method = method,
                mechanism = mechanism,
                stage = stage.ToString(),
            });
    }
}
#endif
