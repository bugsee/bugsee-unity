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
using Bugsee.Platform.EditorStub;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using Bugsee.Platform.Android;
#elif UNITY_IOS && !UNITY_EDITOR
using Bugsee.Platform.IOS;
#endif

namespace Bugsee
{
    /// <summary>
    /// Public Bugsee facade mirroring <c>com.bugsee.library.Bugsee</c> (Android 7.x).
    /// </summary>
    public static class Bugsee
    {
        static readonly IBugseeNativeBridge Bridge = CreateBridge();
        static ILifecycleEventListener _appLifecycleListener;

        /// <summary>Raised for every native lifecycle event (all 7.x <see cref="LifecycleEvents"/> strings).</summary>
        public static event Action<string, object> LifecycleEvent;

        public static IFeedback Feedback => Bridge.Feedback;

        public static IAppearance Appearance => Bridge.GetAppearance();

        static IBugseeNativeBridge CreateBridge()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return new AndroidBridge();
#elif UNITY_IOS && !UNITY_EDITOR
            return new IOSBridge();
#else
            return new EditorBridge();
#endif
        }

        /// <summary>
        /// Launch Bugsee. Pass typed <see cref="AndroidLaunchOptions"/> /
        /// <see cref="IOSLaunchOptions"/> / <see cref="OptionsBuilder"/> (implicit
        /// conversion), a <see cref="Dictionary{TKey,TValue}"/>, or omit for native defaults.
        /// </summary>
        public static void Launch(string appToken, Dictionary<string, object> options = null)
        {
            if (string.IsNullOrEmpty(appToken))
                throw new ArgumentException("appToken is required", nameof(appToken));

            MainThreadDispatcher.Ensure();
            Bridge.Launch(appToken, options);
        }

        public static void Relaunch(Dictionary<string, object> options = null) =>
            Bridge.Relaunch(options);

        public static void Stop(Action completion = null) => Bridge.Stop(completion);

        public static bool IsLaunched => Bridge.GetLaunched();

        public static BugseeStatus Status => Bridge.GetStatus();

        public static void StartBlackout() => Bridge.StartBlackout();

        public static void EndBlackout() => Bridge.EndBlackout();

        public static bool IsBlackout => Bridge.IsBlackout();

        [Obsolete("Use StartBlackout/EndBlackout instead.")]
        public static void Pause() => StartBlackout();

        [Obsolete("Use StartBlackout/EndBlackout instead.")]
        public static void Resume() => EndBlackout();

        public static void Log(string message, LogLevel level = LogLevel.Info) =>
            Bridge.Log(message, level);

        public static void Trace(string name, object value) => Bridge.Trace(name, value);

        public static void Event(string name, IDictionary<string, object> parameters = null) =>
            Bridge.Event(name, parameters);

        public static void LogException(Exception exception, IDictionary<string, object> options = null) =>
            Bridge.LogException(exception, options);

        /// <summary>
        /// Report a managed exception as unhandled (crash path / MethodMap).
        /// Prefer ExceptionPipeline auto-capture for uncaught cases.
        /// </summary>
        public static void LogUnhandledException(Exception exception, IDictionary<string, object> options = null) =>
            Bridge.LogUnhandledException(exception, options);

        public static void TestCrash() => Bridge.TestCrash();

        public static void ShowReportDialog(
            string summary = "",
            string description = "",
            IssueSeverity severity = IssueSeverity.High,
            IList<string> labels = null) =>
            Bridge.ShowReportDialog(summary, description, severity, labels);

        public static void Upload(
            string summary,
            string description = "",
            IssueSeverity severity = IssueSeverity.High,
            IList<string> labels = null) =>
            Bridge.Upload(summary, description, severity, labels);

        public static void SetUserIdentifier(string userIdentifier) =>
            Bridge.SetUserIdentifier(userIdentifier);

        public static string GetUserIdentifier() => Bridge.GetUserIdentifier();

        public static void ClearUserIdentifier() => Bridge.ClearUserIdentifier();

        public static void SetAttribute(string key, object value) => Bridge.SetAttribute(key, value);

        public static object GetAttribute(string key) => Bridge.GetAttribute(key);

        public static void ClearAttribute(string key) => Bridge.ClearAttribute(key);

        public static void ClearAllAttributes() => Bridge.ClearAllAttributes();

        public static void AddSecureRectangle(RectInt pixelRect) =>
            Bridge.AddSecureRectangle(pixelRect.xMin, pixelRect.yMin, pixelRect.xMax, pixelRect.yMax);

        public static void RemoveSecureRectangle(RectInt pixelRect) =>
            Bridge.RemoveSecureRectangle(pixelRect.xMin, pixelRect.yMin, pixelRect.xMax, pixelRect.yMax);

        public static void RemoveAllSecureRectangles() => Bridge.RemoveAllSecureRectangles();

        public static void CaptureViewHierarchy() => Bridge.CaptureViewHierarchy();

        public static void ResetVideoCapturePermission() => Bridge.ResetVideoCapturePermission();

        public static void SetNetworkEventFilter(EventFilter<INetworkEvent> filter) =>
            Bridge.SetNetworkEventFilter(filter);

        public static void SetLogEventFilter(EventFilter<ILogEvent> filter) =>
            Bridge.SetLogEventFilter(filter);

        public static void SetBreadcrumbFilter(EventFilter<IBreadcrumb> filter) =>
            Bridge.SetBreadcrumbFilter(filter);

        public static void SetReportHandler(IReportHandler handler) =>
            Bridge.SetReportHandler(handler);

        public static void SetLifecycleEventListener(ILifecycleEventListener listener)
        {
            _appLifecycleListener = listener;
            Bridge.SetLifecycleEventListener(listener);
        }

        /// <summary>Called from native wrapper / lifecycle proxy. Not for app use.</summary>
        internal static void HandleNativeLifecycle(string eventType, object data)
        {
            try { LifecycleEvent?.Invoke(eventType, data); }
            catch (Exception ex) { Debug.LogException(ex); }

            try { _appLifecycleListener?.OnEvent(eventType, data); }
            catch (Exception ex) { Debug.LogException(ex); }
        }
    }
}
