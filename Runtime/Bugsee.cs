using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Bugsee.Contracts.Appearance;
using Bugsee.Contracts.Exchange;
using Bugsee.Contracts.Feedback;
using Bugsee.Contracts.Lifecycle;
using Bugsee.Contracts.Options;
using Bugsee.Contracts.Reporting;
using Bugsee.Internal;
using Bugsee.Platform;
using Bugsee.Platform.EditorStub;
using Bugsee.WrapperPolicy;
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
        const int DefaultSecureRectDisplayId = 0;
        static readonly SecureRectManualOwners _manualSecureRectOwners = new SecureRectManualOwners();

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

        /// <summary>
        /// Stop Bugsee. <paramref name="completion"/> runs once the SDK has finished stopping. It does not run if
        /// a Launch/Relaunch starts a new session first, or if the stop itself did not run (on iOS, when called
        /// off the main thread and the Unity main thread did not take it within 5 s; an error is logged). Use
        /// <see cref="StopAsync"/> to be told about that failure.
        /// </summary>
        public static void Stop(Action completion = null) => Bridge.Stop(completion);

        /// <summary>
        /// <see cref="Launch"/> that completes with the SDK's answer: true when it accepted the launch, false
        /// when it refused it or the launch never ran (a later Launch, Relaunch, Stop or Delete cancelled it while
        /// it waited for a stop in flight). Never blocks the calling thread; awaited from the Unity main thread,
        /// the continuation resumes there.
        /// </summary>
        public static Task<bool> LaunchAsync(string appToken, Dictionary<string, object> options = null)
        {
            if (string.IsNullOrEmpty(appToken))
                throw new ArgumentException("appToken is required", nameof(appToken));

            MainThreadDispatcher.Ensure();
            var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Bridge is ILifecycleResultBridge bridge)
            {
                bridge.Launch(appToken, options, accepted => result.TrySetResult(accepted));
            }
            else
            {
                Bridge.Launch(appToken, options);
                result.TrySetResult(IsStartingOrLaunched(Bridge.GetStatus()));
            }
            return result.Task;
        }

        /// <summary><see cref="Relaunch"/> that completes with the SDK's answer, as <see cref="LaunchAsync"/>.</summary>
        public static Task<bool> RelaunchAsync(Dictionary<string, object> options = null)
        {
            var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Bridge is ILifecycleResultBridge bridge)
            {
                bridge.Relaunch(options, accepted => result.TrySetResult(accepted));
            }
            else
            {
                Bridge.Relaunch(options);
                result.TrySetResult(IsStartingOrLaunched(Bridge.GetStatus()));
            }
            return result.Task;
        }

        /// <summary>
        /// <see cref="Stop"/> that completes once the SDK has finished stopping. Unlike a Stop completion it
        /// completes even when a Launch follows before it does. Faults with <see cref="TimeoutException"/> if the
        /// SDK has not reported the stop within 10 s of this call (a Launch still waits for that stop). On iOS it
        /// also faults when the stop could not be handed to the Unity main thread within 5 s. On Android the
        /// hand-off is queued and never refused, so if the Unity main thread is stalled the Task stays pending
        /// (the 10 s deadline is itself driven from the main thread).
        /// </summary>
        public static Task StopAsync()
        {
            var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Bridge is ILifecycleResultBridge bridge)
            {
                bridge.Stop(ran =>
                {
                    if (ran) result.TrySetResult(true);
                    else result.TrySetException(new TimeoutException("Stop did not finish in time."));
                });
            }
            else
            {
                Bridge.Stop(() => result.TrySetResult(true));
            }
            return result.Task;
        }

        static bool IsStartingOrLaunched(BugseeStatus status) =>
            status == BugseeStatus.Launching || status == BugseeStatus.Launched;

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

        public static void AddNetworkEvent(INetworkEvent networkEvent) =>
            Bridge.AddNetworkEvent(networkEvent);

        public static void DeleteCollectedDataOnDevice() =>
            Bridge.DeleteCollectedDataOnDevice();

        public static IReport CreateReport() => Bridge.CreateReport();

        /// <summary>Release a report from <see cref="CreateReport"/> without uploading.</summary>
        public static void Discard(IReport report) => Bridge.DiscardReport(report);

        public static void AddBreadcrumb(string category, string message, string levelName) =>
            Bridge.AddBreadcrumb(category, message, levelName);

        public static IBugseeExchangeFactory GetExchangeFactory() =>
            Bridge.GetExchangeFactory();

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

        /// <summary>Upload a report created with <see cref="CreateReport"/>.</summary>
        public static void Upload(IReport report) => Bridge.UploadReport(report);

        public static void SetUserIdentifier(string userIdentifier) =>
            Bridge.SetUserIdentifier(userIdentifier);

        public static string GetUserIdentifier() => Bridge.GetUserIdentifier();

        public static void ClearUserIdentifier() => Bridge.ClearUserIdentifier();

        public static void SetAttribute(string key, object value) => Bridge.SetAttribute(key, value);

        public static object GetAttribute(string key) => Bridge.GetAttribute(key);

        public static void ClearAttribute(string key) => Bridge.ClearAttribute(key);

        public static void ClearAllAttributes() => Bridge.ClearAllAttributes();

        public static void AddSecureRectangle(RectInt pixelRect)
        {
            _manualSecureRectOwners.Add(
                pixelRect.xMin,
                pixelRect.yMin,
                pixelRect.xMax,
                pixelRect.yMax,
                SecureRectRegistry.Instance,
                DefaultSecureRectDisplayId);
            PushSecureBufferForDisplay(DefaultSecureRectDisplayId);
        }

        public static void RemoveSecureRectangle(RectInt pixelRect)
        {
            if (!_manualSecureRectOwners.Remove(
                pixelRect.xMin,
                pixelRect.yMin,
                pixelRect.xMax,
                pixelRect.yMax,
                SecureRectRegistry.Instance))
                return;

            PushSecureBufferForDisplay(DefaultSecureRectDisplayId);
        }

        public static void RemoveAllSecureRectangles()
        {
            _manualSecureRectOwners.RemoveAll(SecureRectRegistry.Instance);
            PushSecureBufferForDisplay(DefaultSecureRectDisplayId);
        }

        static void PushSecureBufferForDisplay(int displayId)
        {
            float pixelsPerNativeUnit = Bridge.GetSecureRectSnapshotScale();
            int[] packed = SecureRectRegistry.Instance.Snapshot(displayId, pixelsPerNativeUnit);
            Bridge.SetSecureBuffer(displayId, packed);
        }

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
            try { Bridge.NotifyLifecycle(eventType); }
            catch (Exception ex) { Debug.LogException(ex); }

            try { LifecycleEvent?.Invoke(eventType, data); }
            catch (Exception ex) { Debug.LogException(ex); }

            try { _appLifecycleListener?.OnEvent(eventType, data); }
            catch (Exception ex) { Debug.LogException(ex); }
        }
    }
}
