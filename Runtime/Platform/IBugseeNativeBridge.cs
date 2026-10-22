using System;
using System.Collections.Generic;
using Bugsee.Contracts.Appearance;
using Bugsee.Contracts.Exchange;
using Bugsee.Contracts.Feedback;
using Bugsee.Contracts.Lifecycle;
using Bugsee.Contracts.Options;
using Bugsee.Contracts.Reporting;
using Bugsee.Internal;
using Bugsee.WrapperPolicy;

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

        /// <summary>The effective launch options (SDK defaults merged with the host's), or null when the SDK has none.</summary>
        IReadOnlyDictionary<string, object> GetLaunchOptions();

        /// <summary>Only the launch options the host supplied (no SDK defaults); never null, empty before a launch.</summary>
        IReadOnlyDictionary<string, object> GetHostLaunchOptions();

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

        void ShowReportDialog(string summary, string description, IssueSeverity? severity, IList<string> labels);
        void Upload(string summary, string description, IssueSeverity? severity, IList<string> labels);

        /// <summary>Relay a lightweight notification to the app's messaging integrations (no report is created).</summary>
        void Notify(string title, string body, IssueSeverity severity, IDictionary<string, string> fields, bool urgent);

        /// <summary>Starts a native transaction (attributes already cleaned, may be null), or null when there is none.</summary>
        ISpanHandle StartTransaction(string name, string operation, IDictionary<string, object> attributes);

        void SetUserIdentifier(string userIdentifier);
        string GetUserIdentifier();
        void ClearUserIdentifier();

        void SetAttribute(string key, object value);
        object GetAttribute(string key);

        /// <summary>Every attribute set, typed like <see cref="GetAttribute"/>; never null, empty when none.</summary>
        IReadOnlyDictionary<string, object> GetAllAttributes();
        void ClearAttribute(string key);
        void ClearAllAttributes();

        /// <summary>Push a versioned secure-rect mask for native wrapper pull (not the public SDK API).</summary>
        void SetSecureBuffer(int display, int[] packed);

        /// <summary>Scale divisor for <see cref="SecureRectRegistry.Snapshot"/> (1 on Android, screen scale on iOS).</summary>
        float GetSecureRectSnapshotScale();

        /// <summary>
        /// Where Unity's surface sits, in the native pixels secure rectangles are registered in (top-left origin), given
        /// Unity's own screen size. False when it cannot be read. <paramref name="settled"/> is false when the numbers were read but
        /// disagree with Unity's screen (a view caught mid-rotation): the caller keeps the last settled surface. Main thread only.
        /// </summary>
        bool TryGetSecureRectViewRegion(int screenWidth, int screenHeight, out int left, out int top, out int width, out int height, out bool settled);

        void CaptureViewHierarchy();
        void ResetVideoCapturePermission();

        IAppearance GetAppearance();
        IFeedback Feedback { get; }

        void SetNetworkEventFilter(EventFilter<INetworkEvent> filter);

        /// <summary>Runs the Android SDK's default network sanitizer on the event, in place. False where the SDK has none (iOS, Editor).</summary>
        bool ApplyDefaultNetworkSanitizer(INetworkEvent networkEvent);

        /// <summary>Marks a native WebView secure (Android: android.webkit.WebView as AndroidJavaObject; iOS: WKWebView* as IntPtr). False when it is not one.</summary>
        bool AddSecureWebView(object nativeWebView);

        /// <summary>Takes a WebView marked by <see cref="AddSecureWebView"/> off the secure list. Same argument types.</summary>
        bool RemoveSecureWebView(object nativeWebView);
        void SetLogEventFilter(EventFilter<ILogEvent> filter);
        void SetBreadcrumbFilter(EventFilter<IBreadcrumb> filter);
        void SetReportHandler(IReportHandler handler);
        void SetLifecycleEventListener(ILifecycleEventListener listener);

        void DeleteCollectedDataOnDevice();
        IReport CreateReport();
        void DiscardReport(IReport report);
        void AddBreadcrumb(string category, string message, string levelName);
        void UploadReport(IReport report);

        /// <summary>Native lifecycle fan-out for bridge-side policy (iOS network buffer).</summary>
        void NotifyLifecycle(string eventType);

        /// <summary>Register the Unity <c>BugseeWrapper</c> implementation with the native SDK.</summary>
        void EnsureWrapperRegistered();
    }
}
