using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Bugsee.Contracts.Apm;
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
        /// Does not wait for the SDK to finish launching; on iOS, off the Unity main thread, it waits up to 5 s for
        /// the main thread to take the call and does not run if that times out (logged). A Launch made while a stop
        /// is in flight waits for it, and a Launch while the SDK is already running ignores the new token and
        /// native options (stop first; on iOS the managed capture options are still applied again). Ordering rules
        /// differ per platform: see the lifecycle section of the documentation.
        /// </summary>
        public static void Launch(string appToken, Dictionary<string, object> options = null)
        {
            if (string.IsNullOrEmpty(appToken))
                throw new ArgumentException("appToken is required", nameof(appToken));

            MainThreadDispatcher.Ensure();
            BuildTokenCheck.OnLaunch(appToken);
            options = ApplyCapturePolicy(options, out bool startPusher);
            if (Bridge is ILifecycleResultBridge bridge)
            {
                Interlocked.Increment(ref _pendingLaunches);
                bridge.Launch(appToken, options, accepted => AfterLaunchResultCounted(accepted, startPusher, options));
            }
            else
            {
                Bridge.Launch(appToken, options);
                AfterLaunchResult(true, startPusher, options);
            }
        }

        /// <summary>
        /// Relaunch the SDK with new options. Does not wait for the SDK, and waits for a stop in flight like
        /// <see cref="Launch"/> (the same 5 s main-thread hand-off applies on iOS). Android ignores it with a
        /// warning unless the SDK is Launched. See <see cref="RelaunchAsync"/> for the result.
        /// </summary>
        public static void Relaunch(Dictionary<string, object> options = null)
        {
            options = ApplyCapturePolicy(options, out bool startPusher);
            if (Bridge is ILifecycleResultBridge bridge)
            {
                Interlocked.Increment(ref _pendingLaunches);
                bridge.Relaunch(options, accepted => AfterLaunchResultCounted(accepted, startPusher, options));
            }
            else
            {
                Bridge.Relaunch(options);
                AfterLaunchResult(true, startPusher, options);
            }
        }

        // VideoMode.DirectBuffers needs a frame source, and in Unity that is the Unity-side capturer (Unity 2022.2+). When it
        // cannot run, the native SDK is launched in its default capture mode instead, never in a DirectBuffers session
        // without frames. See UnityCapturePolicy.
        static Dictionary<string, object> ApplyCapturePolicy(Dictionary<string, object> options, out bool startPusher)
        {
            var effective = UnityCapturePolicy.Apply(options, UnityCaptureProbe.Current, out VideoModeDecision decision);
            startPusher = decision.UseUnityCapture;
            if (decision.FallbackReason != null)
                Debug.LogWarning("[Bugsee] VideoMode.DirectBuffers was requested, but Unity-side capture cannot run: "
                    + decision.FallbackReason + ". Launching with the SDK's default capture instead.");
            return effective;
        }

        static readonly PusherLaunchState PusherState = new PusherLaunchState();
        static bool _pusherFailureHooked;
        // Launch/Relaunch requests handed to the native SDK whose answer has not arrived. While one is waiting, a fallback
        // relaunch would replace its options in the lifecycle queue, so the fallback is left to that request.
        static int _pendingLaunches;

        // Not atomic with the lifecycle machine: a request can arrive between this check and the relaunch. The window is
        // small, a request made then is queued behind it (it still runs and decides), and the worst case is that the fallback
        // relaunch's options are replaced by that request's, which has its own policy/fallback handling when it is answered.
        static bool NoNewerLaunchWaiting() => Volatile.Read(ref _pendingLaunches) == 0;

        static void AfterLaunchResultCounted(bool accepted, bool startPusher, Dictionary<string, object> options)
        {
            Interlocked.Decrement(ref _pendingLaunches);
            AfterLaunchResult(accepted, startPusher, options);
        }

        // The pusher follows what the native SDK actually did with a launch request, once its answer is known: an ignored or
        // refused request (e.g. a Launch while the SDK is running) leaves it alone, an accepted one is read back from the
        // effective launch options. If it cannot start (now, or later after repeated readback failures) the session is
        // relaunched with the default capture. All of it runs on the main thread.
        static void AfterLaunchResult(bool accepted, bool startPusher, Dictionary<string, object> options)
        {
            if (!_pusherFailureHooked)
            {
                _pusherFailureHooked = true;
                Capture.BugseeFrameCapturer.Failed += () => MainThreadDispatcher.Run(() =>
                    CaptureStartup.OnPusherFailed(PusherState, NoNewerLaunchWaiting, Bridge.Relaunch,
                        message => Debug.LogWarning("[Bugsee] " + message)));
            }
            CaptureStartup.OnLaunchResult(
                accepted,
                startPusher,
                options,
                PusherState,
                NoNewerLaunchWaiting,
                () => IsStartingOrLaunched(Bridge.GetStatus()),
                EffectiveVideoMode,
                () => Capture.BugseeFrameCapturer.Enable(Capture.BugseeFrameCapturer.DefaultOptions),
                Capture.BugseeFrameCapturer.Disable,
                Bridge.Relaunch,
                message => Debug.LogWarning("[Bugsee] " + message),
                MainThreadDispatcher.Run);
        }

        // The video mode the native SDK is really running with, from its effective launch options; null when unreadable.
        static VideoMode? EffectiveVideoMode()
        {
            IReadOnlyDictionary<string, object> launch = Bridge.GetLaunchOptions();
            if (launch == null || !launch.TryGetValue(Options.CaptureVideoMode, out object mode) || mode == null) return null;
            return int.TryParse(Convert.ToString(mode, System.Globalization.CultureInfo.InvariantCulture), out int value)
                ? VideoModeExtensions.FromIntValue(value)
                : (VideoMode?)null;
        }

        /// <summary>
        /// Stop Bugsee. <paramref name="completion"/> runs once the SDK has finished stopping. A Launch/Relaunch
        /// requested during the stop waits for it, and the completion runs before that Launch does. If the
        /// completion itself calls Launch, that later request replaces the waiting one (it completes false) and is
        /// the one that starts. On iOS the completion is dropped if the stop itself did not run (called off the main thread and the Unity main thread
        /// did not take it within 5 s; an error is logged), or if an earlier completion of the same stop already
        /// launched. Use <see cref="StopAsync"/> to be told about the first case. If the SDK has not reported the stop within 10 s of
        /// the call, the completion runs anyway with a warning (a Launch still waits for the stop); after 60 s the
        /// wrapper stops waiting for the stop. Residual race after that: on iOS a late native stop can still stop a
        /// Launch made after the 60 s give-up.
        /// </summary>
        public static void Stop(Action completion = null)
        {
            ForgetPusherSession();
            Bridge.Stop(completion);
        }

        // A stopped session has no DirectBuffers state: a later pusher failure must not relaunch it with old options.
        static void ForgetPusherSession()
        {
            PusherState.OnStopped();
            Capture.BugseeFrameCapturer.Disable();
        }

        /// <summary>
        /// <see cref="Launch"/> that completes with the SDK's answer: true when it accepted the launch, false
        /// when it refused it or the launch never ran (a later request cancelled it while it waited
        /// for a stop in flight; which requests do, differs per platform, see the lifecycle section of the
        /// documentation). If the SDK gives no answer within 10 s the result is true when its status
        /// is Launching/Launched and false otherwise. Does not wait for the SDK; on iOS, off the Unity main thread, it
        /// waits up to 5 s for the main thread to take the call (false if it does not). Awaited from the Unity main
        /// thread, the continuation resumes there.
        /// </summary>
        public static Task<bool> LaunchAsync(string appToken, Dictionary<string, object> options = null)
        {
            if (string.IsNullOrEmpty(appToken))
                throw new ArgumentException("appToken is required", nameof(appToken));

            MainThreadDispatcher.Ensure();
            BuildTokenCheck.OnLaunch(appToken);
            options = ApplyCapturePolicy(options, out bool startPusher);
            var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Bridge is ILifecycleResultBridge bridge)
            {
                Interlocked.Increment(ref _pendingLaunches);
                bridge.Launch(appToken, options, accepted =>
                {
                    // The task must complete even if the capture follow-up throws (the lifecycle machine swallows it).
                    try { AfterLaunchResultCounted(accepted, startPusher, options); }
                    finally { result.TrySetResult(accepted); }
                });
            }
            else
            {
                Bridge.Launch(appToken, options);
                AfterLaunchResult(true, startPusher, options);
                result.TrySetResult(IsStartingOrLaunched(Bridge.GetStatus()));
            }
            return result.Task;
        }

        /// <summary><see cref="Relaunch"/> that completes with the SDK's answer, as <see cref="LaunchAsync"/>.</summary>
        public static Task<bool> RelaunchAsync(Dictionary<string, object> options = null)
        {
            options = ApplyCapturePolicy(options, out bool startPusher);
            var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Bridge is ILifecycleResultBridge bridge)
            {
                Interlocked.Increment(ref _pendingLaunches);
                bridge.Relaunch(options, accepted =>
                {
                    try { AfterLaunchResultCounted(accepted, startPusher, options); }
                    finally { result.TrySetResult(accepted); }
                });
            }
            else
            {
                Bridge.Relaunch(options);
                AfterLaunchResult(true, startPusher, options);
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
            ForgetPusherSession();
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

        /// <summary>
        /// The options the SDK is running with: its defaults with the ones passed to <see cref="Launch"/> merged over
        /// them, as a read-only snapshot (changing it changes nothing). Null while the SDK is not launched. Keys are
        /// the native SDK's own option names and differ between Android and iOS; values are string, bool, long,
        /// double (or a nested dictionary or list); narrower numeric types are widened the same way on both platforms. The
        /// SDK's internal keys (starting with a double dollar sign) are left out. A value with no such form, such as a
        /// Java array, reads as its text.
        /// </summary>
        public static IReadOnlyDictionary<string, object> GetLaunchOptions()
        {
            var options = LaunchOptionsWire.Normalize(Bridge.GetLaunched() ? Bridge.GetLaunchOptions() : null);
            return options == null ? null : new ReadOnlyDictionary<string, object>(options);
        }

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

        /// <summary>
        /// Delete the data collected on the device, including finished report bundles. Stops the SDK first if it
        /// is not stopped (joining a stop in flight) and leaves it stopped: Launch again to resume. It has no
        /// result; a failure is logged (use <see cref="DeleteCollectedDataOnDeviceAsync"/> for one, or to delete less).
        /// On Android it runs in request order with Launch/Relaunch (ones requested
        /// earlier run first); on iOS a Launch/Relaunch requested after it waits for the wipe. A delete already
        /// handed to the native SDK can finish after the 10 s warning, and after the 60 s hard deadline an Android
        /// Launch can run while a native delete is still wiping.
        /// </summary>
        public static void DeleteCollectedDataOnDevice()
        {
            ForgetPusherSession(); // it stops the SDK
            Bridge.DeleteCollectedDataOnDevice();
        }

        /// <summary>
        /// <see cref="DeleteCollectedDataOnDevice()"/> for the data of <paramref name="scope"/> only. The narrower
        /// scopes keep what the wider one deletes (see <see cref="DeleteScope"/>); the SDK is stopped either way.
        /// </summary>
        public static void DeleteCollectedDataOnDevice(DeleteScope scope)
        {
            ForgetPusherSession(); // it stops the SDK
            if (Bridge is ILifecycleResultBridge bridge) bridge.DeleteCollectedData(scope, null);
            else Bridge.DeleteCollectedDataOnDevice();
        }

        /// <summary>
        /// <see cref="DeleteCollectedDataOnDevice(DeleteScope)"/> that completes with the SDK's answer: true when it
        /// reported the data deleted, false when it refused, gave no answer in time (Android 10 s, iOS 60 s), or on iOS
        /// the call did not reach the Unity main thread within 5 s. A false after a timeout can still be followed by
        /// the wipe finishing. Deletes <see cref="DeleteScope.Everything"/> by default, as
        /// <see cref="DeleteCollectedDataOnDevice()"/>. The SDK is left stopped. In the Editor nothing is collected,
        /// so it completes true. Awaited from the Unity main thread, the continuation resumes there.
        /// </summary>
        public static Task<bool> DeleteCollectedDataOnDeviceAsync(DeleteScope scope = DeleteScope.Everything)
        {
            ForgetPusherSession(); // it stops the SDK
            var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Bridge is ILifecycleResultBridge bridge)
            {
                bridge.DeleteCollectedData(scope, wiped => result.TrySetResult(wiped));
            }
            else
            {
                Bridge.DeleteCollectedDataOnDevice();
                result.TrySetResult(true);
            }
            return result.Task;
        }

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

        /// <summary>
        /// Show the report dialog. An omitted <paramref name="severity"/> lets the SDK pick its default, the
        /// <c>ReportingDefaultBugPriority</c> launch option (High unless configured).
        /// </summary>
        public static void ShowReportDialog(
            string summary = "",
            string description = "",
            IssueSeverity? severity = null,
            IList<string> labels = null) =>
            Bridge.ShowReportDialog(summary, description, severity, labels);

        /// <summary>
        /// Create and upload a report without UI. An omitted <paramref name="severity"/> lets the SDK pick its
        /// default, the <c>ReportingDefaultBugPriority</c> launch option (High unless configured).
        /// </summary>
        public static void Upload(
            string summary,
            string description = "",
            IssueSeverity? severity = null,
            IList<string> labels = null) =>
            Bridge.Upload(summary, description, severity, labels);

        /// <summary>Upload a report created with <see cref="CreateReport"/>.</summary>
        public static void Upload(IReport report) => Bridge.UploadReport(report);

        /// <summary>
        /// Relay a lightweight notification to the app's messaging integrations, without creating a report (no video,
        /// logs or events are attached). It is stored offline and uploaded in batches. Ignored, with a native warning,
        /// when <paramref name="title"/> is empty or the SDK is not launched. <paramref name="urgent"/> asks for
        /// delivery without waiting for the batch; <paramref name="fields"/> are extra string key/value pairs
        /// (entries with a null key or value are dropped).
        /// </summary>
        public static void Notify(
            string title,
            string body = null,
            IssueSeverity severity = IssueSeverity.Medium,
            IDictionary<string, string> fields = null,
            bool urgent = false)
        {
            if (!NotifyPolicy.HasTitle(title))
            {
                Debug.LogWarning("[Bugsee] Notify ignored: a title is required.");
                return;
            }
            Bridge.Notify(title, body, severity, NotifyPolicy.CleanFields(fields), urgent);
        }

        /// <summary>
        /// Starts a performance transaction, the root of a trace; start child spans with <see cref="ISpan.StartChild"/>
        /// and finish (or dispose) every span. Spans are always created from a parent: there is no global "current
        /// span", because the native SDKs track it per thread. Returns an inert span that accepts every call when the
        /// SDK is not launched, when <paramref name="name"/> or <paramref name="operation"/> is empty, or when
        /// performance monitoring is off. <paramref name="attributes"/> take the same values as
        /// <see cref="SetAttribute"/>; invalid entries are dropped with a warning.
        /// </summary>
        public static ISpan StartTransaction(string name, string operation, IDictionary<string, object> attributes = null)
        {
            if (!SpanPolicy.HasText(name) || !SpanPolicy.HasText(operation))
            {
                Debug.LogWarning("[Bugsee] StartTransaction ignored: a name and an operation are required.");
                return new NoopSpan();
            }
            var rejected = new List<string>();
            var clean = SpanPolicy.CleanAttributes(attributes, rejected);
            foreach (var reason in rejected) Debug.LogWarning("[Bugsee] StartTransaction attribute dropped: " + reason + ".");

            return SpanStart.Transaction(
                () => Bridge.GetLaunched(),
                () => Bridge.StartTransaction(name, operation, clean),
                operation,
                message => Debug.LogWarning("[Bugsee] " + message));
        }

        public static void SetUserIdentifier(string userIdentifier) =>
            Bridge.SetUserIdentifier(userIdentifier);

        public static string GetUserIdentifier() => Bridge.GetUserIdentifier();

        public static void ClearUserIdentifier() => Bridge.ClearUserIdentifier();

        public static void SetAttribute(string key, object value) => Bridge.SetAttribute(key, value);

        public static object GetAttribute(string key) => Bridge.GetAttribute(key);

        public static void ClearAttribute(string key) => Bridge.ClearAttribute(key);

        public static void ClearAllAttributes() => Bridge.ClearAllAttributes();

        /// <summary>
        /// Hides a screen region in captured video and report screenshots.
        /// Coordinates are screen pixels with the origin at the TOP-LEFT of the screen, as both native SDKs
        /// expect (Android pixels; iOS points, converted by the screen scale). The rectangle is passed through
        /// as given: it is NOT flipped from Unity's bottom-left <c>Screen</c>/<c>Input</c> space. Convert with
        /// <c>y = Screen.height - rect.yMax</c>; see the package documentation.
        /// Limitation: the rectangle must already be in the native display's coordinate space. With Android
        /// Resolution Scaling or <c>Screen.SetResolution</c>, or a non-fullscreen Unity view (status bar or notch
        /// offset), <c>Screen.height</c> and pixels differ from native pixels, so scale or offset the rectangle
        /// yourself. On iOS the wrapper divides by <c>UIScreen.scale</c>, which is wrong if the render size
        /// differs from points x scale.
        /// </summary>
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

        /// <summary>
        /// Stops hiding a rectangle added with <see cref="AddSecureRectangle"/>. It must be the same
        /// top-left-origin rectangle that was added (see <see cref="AddSecureRectangle"/>).
        /// </summary>
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
