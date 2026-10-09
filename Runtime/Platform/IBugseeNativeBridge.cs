using System;
using System.Collections.Generic;
using Bugsee.Contracts.Appearance;
using Bugsee.Contracts.Exchange;
using Bugsee.Contracts.Feedback;
using Bugsee.Contracts.Lifecycle;
using Bugsee.Contracts.Options;
using Bugsee.Contracts.Reporting;
using Bugsee.Internal;

namespace Bugsee.Platform
{
    /// <summary>Platform-specific native Bugsee SDK bridge.</summary>
    public interface IBugseeNativeBridge
    {
        bool IsSupported { get; }

        void Launch(string appToken, IDictionary<string, object> options);
        void Relaunch(IDictionary<string, object> options);
        void Stop(Action completion = null);
        bool GetLaunched();
        BugseeStatus GetStatus();

        void StartBlackout();
        void EndBlackout();
        bool IsBlackout();

        void Log(string message, LogLevel level);

        /// <summary>Submit a host log on the wrapper channel. Public <see cref="Log"/> stays on the SDK log API.</summary>
        void ChannelLog(string message, LogLevel level);

        /// <summary>Submit a network event on the wrapper channel, not the public SDK addNetworkEvent API.</summary>
        void AddNetworkEvent(INetworkEvent networkEvent);

        IBugseeExchangeFactory GetExchangeFactory();
        void Trace(string name, object value);
        void Event(string name, IDictionary<string, object> parameters);
        void LogException(Exception exception, IDictionary<string, object> options = null);
        void LogUnhandledException(Exception exception, IDictionary<string, object> options = null);
        void LogExceptionPayload(ManagedExceptionPayload.Payload payload, IDictionary<string, object> options = null);
        void LogUnhandledExceptionPayload(ManagedExceptionPayload.Payload payload, IDictionary<string, object> options = null);
        void TestCrash();

        void ShowReportDialog(string summary, string description, IssueSeverity severity, IList<string> labels);
        void Upload(string summary, string description, IssueSeverity severity, IList<string> labels);

        void SetUserIdentifier(string userIdentifier);
        string GetUserIdentifier();
        void ClearUserIdentifier();

        void SetAttribute(string key, object value);
        object GetAttribute(string key);
        void ClearAttribute(string key);
        void ClearAllAttributes();

        void AddSecureRectangle(int left, int top, int right, int bottom);
        void RemoveSecureRectangle(int left, int top, int right, int bottom);
        void RemoveAllSecureRectangles();

        void CaptureViewHierarchy();
        void ResetVideoCapturePermission();

        IAppearance GetAppearance();
        IFeedback Feedback { get; }

        void SetNetworkEventFilter(EventFilter<INetworkEvent> filter);
        void SetLogEventFilter(EventFilter<ILogEvent> filter);
        void SetBreadcrumbFilter(EventFilter<IBreadcrumb> filter);
        void SetReportHandler(IReportHandler handler);
        void SetLifecycleEventListener(ILifecycleEventListener listener);

        /// <summary>Register the Unity <c>BugseeWrapper</c> implementation with the native SDK.</summary>
        void EnsureWrapperRegistered();
    }
}
