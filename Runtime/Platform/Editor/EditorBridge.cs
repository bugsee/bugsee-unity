using System;
using System.Collections.Generic;
using Bugsee.Contracts.Appearance;
using Bugsee.Contracts.Exchange;
using Bugsee.Contracts.Feedback;
using Bugsee.Contracts.Lifecycle;
using Bugsee.Contracts.Options;
using Bugsee.Contracts.Reporting;
using Bugsee.Internal;
using Bugsee.Platform;
using Bugsee.WrapperPolicy;
using UnityEngine;

namespace Bugsee.Platform.EditorStub
{
    /// <summary>No-op bridge used in the Editor and unsupported platforms.</summary>
    sealed class EditorBridge : IBugseeNativeBridge
    {
        public bool IsSupported => false;

        public IFeedback Feedback { get; } = new StubFeedback();

        public void Launch(string appToken, IDictionary<string, object> options) { }
        public void Relaunch(IDictionary<string, object> options) { }
        public void Stop(Action completion = null) => completion?.Invoke();
        public bool GetLaunched() => false;
        public BugseeStatus GetStatus() => BugseeStatus.Stopped;

        bool _blackout;
        public void StartBlackout() => _blackout = true;
        public void EndBlackout() => _blackout = false;
        public bool IsBlackout() => _blackout;
        public void Log(string message, LogLevel level) { }
        public void ChannelLog(string message, LogLevel level) { }
        public void AddNetworkEvent(INetworkEvent networkEvent) { }

        public IBugseeExchangeFactory GetExchangeFactory() => EditorExchangeFactory.Instance;

        public void Trace(string name, object value) { }
        public void Event(string name, IDictionary<string, object> parameters) { }
        public void LogException(Exception exception, IDictionary<string, object> options = null) { }
        public void LogUnhandledException(Exception exception, IDictionary<string, object> options = null) { }
        public void LogExceptionPayload(ManagedExceptionPayload.Payload payload, IDictionary<string, object> options = null) { }
        public void LogUnhandledExceptionPayload(ManagedExceptionPayload.Payload payload, IDictionary<string, object> options = null) { }
        public void TestCrash() { }
        public void ShowReportDialog(string summary, string description, IssueSeverity severity, IList<string> labels) { }
        public void Upload(string summary, string description, IssueSeverity severity, IList<string> labels) { }
        public void SetUserIdentifier(string userIdentifier) { }
        public string GetUserIdentifier() => null;
        public void ClearUserIdentifier() { }
        public void SetAttribute(string key, object value) { }
        public object GetAttribute(string key) => null;
        public void ClearAttribute(string key) { }
        public void ClearAllAttributes() { }
        public void AddSecureRectangle(int left, int top, int right, int bottom) { }
        public void RemoveSecureRectangle(int left, int top, int right, int bottom) { }
        public void RemoveAllSecureRectangles() { }
        public void CaptureViewHierarchy() { }
        public void ResetVideoCapturePermission() { }
        public IAppearance GetAppearance() => new StubAppearance();
        public void SetNetworkEventFilter(EventFilter<INetworkEvent> filter) { }
        public void SetLogEventFilter(EventFilter<ILogEvent> filter) { }
        public void SetBreadcrumbFilter(EventFilter<IBreadcrumb> filter) { }
        public void SetReportHandler(IReportHandler handler) { }
        public void SetLifecycleEventListener(ILifecycleEventListener listener) { }
        public void NotifyLifecycle(string eventType) { }

        public void DeleteCollectedDataOnDevice() { }

        public IReport CreateReport() =>
            throw new NotSupportedException("CreateReport is not supported in the Unity Editor.");

        public void AddBreadcrumb(string category, string message, string levelName)
        {
            BreadcrumbLevelMap.ParseOrThrow(levelName);
        }

        public void UploadReport(IReport report) =>
            throw new NotSupportedException("Upload(IReport) is not supported in the Unity Editor.");

        public void DiscardReport(IReport report)
        {
            if (report == null)
                throw new ArgumentNullException(nameof(report));
        }

        public void EnsureWrapperRegistered() { }

        sealed class StubAppearance : IAppearance
        {
            public IAppearance SetColor(string propertyName, Color32? color) => this;
            public Color32? GetColor(string propertyName) => null;
            public IAppearance SetString(string propertyName, string value) => this;
            public string GetString(string propertyName) => null;
            public IReadOnlyDictionary<string, object> ToMap() => new Dictionary<string, object>();
        }

        sealed class StubFeedback : IFeedback
        {
            public void ShowFeedbackUi() { }
            public void SetDefaultGreeting(string greeting) { }
            public void SetListener(IFeedbackListener listener) { }
        }

        sealed class EditorExchangeFactory : IBugseeExchangeFactory
        {
            public static readonly EditorExchangeFactory Instance = new EditorExchangeFactory();

            public INetworkEvent CreateNetworkEvent(
                long timestamp,
                NetworkEventStage stage,
                string id,
                string mechanism,
                string method) =>
                new EditorNetworkEvent(timestamp, stage, id, mechanism, method);
        }

        sealed class EditorNetworkEvent : INetworkEvent
        {
            readonly long _timestamp;
            readonly NetworkEventStage _stage;
            readonly string _method;
            readonly string _mechanism;

            public EditorNetworkEvent(
                long timestamp,
                NetworkEventStage stage,
                string id,
                string mechanism,
                string method)
            {
                _timestamp = timestamp;
                _stage = stage;
                _method = method;
                _mechanism = mechanism;
                Id = string.IsNullOrEmpty(id) ? Guid.NewGuid().ToString("N") : id;
            }

            public string Id { get; }
            public string Mechanism => _mechanism;
            public string Url { get; set; }
            public string Method => _method;
            public string Body { get; set; }
            public long Size { get; set; }
            public int ResponseCode { get; set; }
            public string StatusText { get; set; }
            public string ErrorShortMessage { get; set; }
            public string ErrorDescription { get; set; }
            public IDictionary<string, string> Headers { get; set; }
            public NetworkEventStage Stage => _stage;
        }
    }
}
