using System;
using System.Collections.Generic;
using Bugsee.Contracts.Appearance;
using Bugsee.Contracts.Exchange;
using Bugsee.Contracts.Feedback;
using Bugsee.Contracts.Lifecycle;
using Bugsee.Contracts.Options;
using Bugsee.Contracts.Reporting;
using Bugsee.Platform;
using UnityEngine;

namespace Bugsee.Platform.IOS
{
    /// <summary>
    /// Placeholder iOS bridge. Native SPM is linked for future work, but the
    /// C# ↔ ObjC/Swift bridge is not implemented yet — fail loudly instead of
    /// silently no-op'ing on device.
    /// </summary>
    sealed class IOSBridge : IBugseeNativeBridge
    {
        const string Message =
            "Bugsee iOS native bridge is not implemented yet. Use Android, or the Editor no-op bridge.";

        public bool IsSupported => false;

        public IFeedback Feedback { get; } = new StubFeedback();

        public void Launch(string appToken, IDictionary<string, object> options) =>
            throw new NotSupportedException(Message);

        public void Relaunch(IDictionary<string, object> options) =>
            throw new NotSupportedException(Message);

        public void Stop(Action completion = null) => completion?.Invoke();
        public bool GetLaunched() => false;
        public BugseeStatus GetStatus() => BugseeStatus.Stopped;
        public void StartBlackout() { }
        public void EndBlackout() { }
        public bool IsBlackout() => false;
        public void Log(string message, LogLevel level) { }
        public void Trace(string name, object value) { }
        public void Event(string name, IDictionary<string, object> parameters) { }
        public void LogException(Exception exception, IDictionary<string, object> options = null) { }
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
    }
}
