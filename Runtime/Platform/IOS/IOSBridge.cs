#if UNITY_IOS && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
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
    sealed class IOSBridge : IBugseeNativeBridge, ILifecycleResultBridge
    {
        delegate void ManagedReportUploadNativeCallback(int succeeded, ulong uploadToken);
        delegate void StopNativeCallback(int token);
        delegate void DeleteNativeCallback(int token, int wiped);
        delegate void LaunchStartedNativeCallback(int token, int success);

        // Written on the main thread, read and cleared on the SDK queue: always through Interlocked.
        long _inFlightUploadToken; // the upload token as a long: Interlocked has no ulong overloads in Unity
        ulong _managedReportUploadFence;
        int _reportUploadGeneration;

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

        public void Launch(string appToken, IDictionary<string, object> options) => Launch(appToken, options, null);

        void ILifecycleResultBridge.Launch(string appToken, IDictionary<string, object> options, Action<bool> started) =>
            Launch(appToken, options, started);

        void Launch(string appToken, IDictionary<string, object> options, Action<bool> started)
        {
            // Build identity reads Application.buildGUID/version and Resources: main thread only, so inside the hand-off.
            if (!MainThreadDispatcher.RunSyncLifecycle(() =>
                {
                    ManagedExceptionPayload.EnsureBuildIdentity();
                    ManagedExceptionPayload.WarmModuleUuid();
                    LaunchOrDefer("Launch", () => LaunchNow(appToken, options, started), started);
                }))
            {
                Debug.LogError("[Bugsee] Launch timed out waiting for the Unity main thread.");
                RunResult(started, false);
            }
        }

        public void Relaunch(IDictionary<string, object> options) => Relaunch(options, null);

        void ILifecycleResultBridge.Relaunch(IDictionary<string, object> options, Action<bool> started) =>
            Relaunch(options, started);

        void Relaunch(IDictionary<string, object> options, Action<bool> started)
        {
            if (!MainThreadDispatcher.RunSyncLifecycle(() =>
                    LaunchOrDefer("Relaunch", () => RelaunchNow(options, started), started)))
            {
                Debug.LogError("[Bugsee] Relaunch timed out waiting for the Unity main thread.");
                RunResult(started, false);
            }
        }

        // Main thread only. A launch made while a native stop is in flight would race it (the SDK keeps a launch
        // that completes after a stop stopped), so it waits for that stop; the latest Launch/Relaunch/Stop/Delete
        // request wins (StopWaitList).
        void LaunchOrDefer(string name, Action launchNow, Action<bool> started)
        {
            var deferred = StopWaiters.TryDeferLaunch(run =>
            {
                if (run)
                {
                    launchNow();
                    return;
                }
                Debug.Log("[Bugsee] " + name + " was cancelled by a later Launch, Relaunch, Stop or Delete.");
                RunResult(started, false);
            }, out var cancelled);
            cancelled?.Invoke(false);
            if (deferred)
            {
                Debug.Log("[Bugsee] " + name + " waits for the stop in flight to finish.");
                return;
            }
            launchNow();
        }

        static bool _exceptionsFiledWarned;

        // The simulator's SDK slice has no crash reporter: nothing from a crash, a hang or a C# exception is filed there.
        static void WarnOnceIfExceptionsNotFiled()
        {
            if (_exceptionsFiledWarned) return;
            _exceptionsFiledWarned = true;
            if (_bugsee_exceptions_filed() != 0) return;
            Debug.LogWarning("[Bugsee] Running on the simulator: native crashes, hangs and exceptions from C# "
                + "(handled and unhandled) are not reported here, only from a device. Bug reports, logs, events and video "
                + "still work. Test crash and exception reporting on a physical device.");
        }

        void LaunchNow(string appToken, IDictionary<string, object> options, Action<bool> started)
        {
            EnsureWrapperRegistered();
            WarnOnceIfExceptionsNotFiled();
            DeleteCollectedDataLaunchGeneration.BumpForLaunch();
            _networkLaunchBuffer.BeginNewLaunchCycle();
            _breadcrumbLaunchBuffer.BeginNewLaunchCycle();
            _networkLaunchBuffer.SetPhase(NetworkLaunchPhase.BeforeLaunched);
            _breadcrumbLaunchBuffer.SetPhase(NetworkLaunchPhase.BeforeLaunched);
            var startedToken = RegisterLaunchResult(started);
            _bugsee_launch(appToken, ToJsonObject(WithWrapperInfo(OptionPlatformGate.ForIos(options))),
                startedToken, startedToken == 0 ? null : (LaunchStartedNativeCallback)OnNativeStarted);
            ExceptionPipeline.Install(this, options);
            HostLogForwarder.InstallOnce(this);
        }

        void RelaunchNow(IDictionary<string, object> options, Action<bool> started)
        {
            EnsureWrapperRegistered();
            WarnOnceIfExceptionsNotFiled();
            DeleteCollectedDataLaunchGeneration.BumpForLaunch();
            _networkLaunchBuffer.BeginNewLaunchCycle();
            _breadcrumbLaunchBuffer.BeginNewLaunchCycle();
            _networkLaunchBuffer.SetPhase(NetworkLaunchPhase.BeforeLaunched);
            _breadcrumbLaunchBuffer.SetPhase(NetworkLaunchPhase.BeforeLaunched);
            var startedToken = RegisterLaunchResult(started);
            _bugsee_relaunch(ToJsonObject(WithWrapperInfo(OptionPlatformGate.ForIos(options))),
                startedToken, startedToken == 0 ? null : (LaunchStartedNativeCallback)OnNativeStarted);
            ExceptionPipeline.Install(this, options);
            HostLogForwarder.InstallOnce(this);
        }

        public void Stop(Action completion = null) => Stop(completion, null);

        void ILifecycleResultBridge.Stop(Action<bool> finished) => Stop(null, finished);

        // completion is the public Stop's: dropped if a Launch follows before it runs. finished (StopAsync) runs
        // either way, so nothing awaits forever.
        void Stop(Action completion, Action<bool> finished)
        {
            if (!MainThreadDispatcher.RunSyncLifecycle(() =>
                {
                    HostLogForwarder.Uninstall();
                    ExceptionPipeline.Uninstall();
                    CancelManagedReportUpload();
                    _openReport?.ReleaseSnapshotFiles();
                    _openReport = null;
                    _openReportHandle = null;
                    _networkLaunchBuffer.SetPhase(NetworkLaunchPhase.Stopped);
                    _breadcrumbLaunchBuffer.SetPhase(NetworkLaunchPhase.Stopped);

                    // The wrapper channel is deliberately kept: the SDK drops calls made after stop
                    // and re-registering the same wrapper never hands out a new one, so clearing it
                    // here would silence the channel for every later launch.
                    StopWithSoftDeadline(completion, finished);
                }))
            {
                // The body was skipped, so nothing is in flight and no deadline is armed. StopAsync is told it
                // failed. The public completion is dropped, not run: it cannot tell success from failure, and
                // one that relaunches (Stop(() => Launch(t))) would act on a session that was never stopped.
                if (finished != null)
                {
                    Debug.LogError("[Bugsee] Stop timed out waiting for the Unity main thread; it did not run.");
                    RunResult(finished, false);
                }
                else
                {
                    Debug.LogError("[Bugsee] Stop timed out waiting for the Unity main thread; it did not run"
                        + (completion != null ? " and its completion was dropped." : "."));
                }
            }
        }

        // One native stop at a time: a Stop or Delete while one is in flight waits on it rather than issuing a
        // nested [Bugsee stop:], and a Launch or Relaunch waits for it to finish. Waiters are matched to the
        // native callback by token, and dropped if a Launch or Relaunch has started a new session since (same
        // fence as the pending GDPR delete).
        //
        // Two deadlines for a stop the SDK does not report:
        // - soft (StopSoftDeadlineMs after each Stop call): that call's completion runs, or StopAsync faults, with
        //   a warning, so no caller waits forever. The stop stays in flight: a Launch made by that completion or
        //   later is still deferred, and a further Stop still joins instead of issuing a second native stop.
        // - hard (StopHardDeadlineMs after the native stop began): the bridge gives up on the stop. The remaining
        //   waiters (a Delete) and the deferred launch run with a warning, and a late report is ignored by token.
        //   Residual race, accepted only after this long: if the native stop is in fact still running, it can
        //   stop the launch made now (beta5 keeps a launch that completes after a stop stopped).
        static readonly StopWaitList StopWaiters = new StopWaitList();
        static readonly OneShotCallbacks<bool> LaunchResults = new OneShotCallbacks<bool>();
        static readonly OneShotCallbacks<bool> StopCompletions = new OneShotCallbacks<bool>();
        static readonly WipeCompletions WipeResults = new WipeCompletions();
        static readonly NativeWipeGate WipeGate = new NativeWipeGate(StopWaiters, WipeResults);
        const int StopSoftDeadlineMs = 10000;
        const int StopHardDeadlineMs = 60000;
        const int LaunchResultTimeoutMs = 10000;

        // Main thread only. The caller's completion is held once, by token, so the soft deadline and the SDK's
        // report cannot both run it: true from the report, false from the deadline.
        void StopWithSoftDeadline(Action completion, Action<bool> finished)
        {
            if (completion == null && finished == null)
            {
                StopNativeThen(null);
                return;
            }

            var token = StopCompletions.Register(stopped =>
            {
                if (finished != null) RunResult(finished, stopped);
                else RunCompletion(completion);
            });
            Action run = () =>
            {
                if (StopCompletions.TryTake(token, out var callback)) callback(true);
            };
            // Dropped (a Launch came first): the public completion is not run; StopAsync still completes.
            Action dropped = () =>
            {
                if (StopCompletions.TryTake(token, out var callback) && finished != null) callback(true);
            };
            if (StopNativeThen(run, dropped)) ArmStopSoftDeadline(token);
        }

        static void ArmStopSoftDeadline(int token)
        {
            AfterOnMainThread(StopSoftDeadlineMs, () =>
            {
                if (!StopCompletions.TryTake(token, out var callback)) return;
                Debug.LogWarning("[Bugsee] The SDK has not reported that stop finished after "
                    + StopSoftDeadlineMs / 1000 + " s; running the Stop completion now. Launch still waits for the stop.");
                callback(false);
            });
        }

        // Main thread only (inside a lifecycle closure). True when then waits on a native stop in flight; false
        // when it has already run.
        bool StopNativeThen(Action then, Action dropped = null, bool first = false)
        {
            var generation = DeleteCollectedDataLaunchGeneration.CaptureForPendingDelete();
            var joined = StopWaiters.TryJoin(then, generation, out var cancelledLaunch, dropped, first);
            cancelledLaunch?.Invoke(false);
            if (joined) return true;
            // Already stopped: nothing will be reported, so run now. ([Bugsee stop:] on a stopped SDK still
            // calls its block, measured on beta6; this only avoids depending on that.)
            if (GetStatus() == BugseeStatus.Stopped)
            {
                _bugsee_stop(0, null);
                RunCompletion(then);
                return false;
            }

            var token = StopWaiters.Begin(then, generation, dropped, first);
            GiveUpIfStopNotReported(token);
            _bugsee_stop(token, OnNativeStopped);
            return true;
        }

        static void GiveUpIfStopNotReported(int token)
        {
            AfterOnMainThread(StopHardDeadlineMs, () =>
            {
                if (!StopWaiters.IsPending(token)) return;
                Debug.LogWarning("[Bugsee] The SDK has not reported that stop finished after "
                    + StopHardDeadlineMs / 1000 + " s; giving up on it and running what waits for it.");
                WipeResults.Settle(token, false, RunResult); // a wipe nobody heard of ends false; no-op for a stop
                CompleteStop(token);
            });
        }

        static void AfterOnMainThread(int delayMs, Action action)
        {
            var timer = default(System.Threading.Timer);
            timer = new System.Threading.Timer(_ =>
            {
                try
                {
                    MainThreadDispatcher.Run(() =>
                    {
                        try { action(); }
                        catch (Exception ex) { Debug.LogException(ex); }
                    });
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
                finally
                {
                    timer?.Dispose();
                }
            }, null, delayMs, System.Threading.Timeout.Infinite);
        }

        // Main thread only.
        static void CompleteStop(int token)
        {
            // The generation is read again before each waiter: a completion that relaunches (the documented
            // Stop(() => Launch(...))) must not have its new session stopped by a Delete that joined the same stop.
            if (StopWaiters.Complete(
                    token, DeleteCollectedDataLaunchGeneration.CaptureForPendingDelete, RunCompletion))
                Debug.LogWarning("[Bugsee] Stop finished after a later Launch; its remaining completions were dropped.");
        }

        // The user's completion must never throw out of here: this runs inside a reverse P/Invoke from an
        // Objective-C block, where an unhandled managed exception is fatal under IL2CPP.
        static void RunCompletion(Action completion)
        {
            if (completion == null) return;
            try { completion(); }
            catch (Exception ex) { Debug.LogException(ex); }
        }

        static void RunResult(Action<bool> callback, bool value)
        {
            if (callback == null) return;
            try { callback(value); }
            catch (Exception ex) { Debug.LogException(ex); }
        }

        // Native stop is asynchronous; run the caller's completion once teardown has finished.
        [MonoPInvokeCallback(typeof(StopNativeCallback))]
        static void OnNativeStopped(int token)
        {
            // Nothing may escape a reverse P/Invoke from an Objective-C block (fatal under IL2CPP), including
            // the dispatcher's own setup.
            try
            {
                // Settled on the main thread, where Launch bumps the generation, so a Launch queued ahead of
                // this report is seen.
                MainThreadDispatcher.Run(() => CompleteStop(token));
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        // The wipe finished (wiped) or did not happen. The deferred launch is released either way, so nothing waits
        // forever; a wipe that did not happen is logged, as DeleteCollectedDataOnDevice has no result to carry it.
        [MonoPInvokeCallback(typeof(DeleteNativeCallback))]
        static void OnNativeDeleted(int token, int wiped)
        {
            try
            {
                MainThreadDispatcher.Run(() =>
                {
                    if (wiped == 0)
                        Debug.LogWarning("[Bugsee] DeleteCollectedDataOnDevice did not wipe the collected data: the SDK reported a failure or had not finished stopping.");
                    WipeResults.Settle(token, wiped != 0, RunResult);
                    CompleteStop(token);
                });
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        // 0 when nobody waits for the result, so the plain Launch keeps the SDK call without a started block.
        // If the SDK never answers, the result is read from its status at the deadline.
        int RegisterLaunchResult(Action<bool> started)
        {
            if (started == null) return 0;
            var token = LaunchResults.Register(started);
            AfterOnMainThread(LaunchResultTimeoutMs, () =>
            {
                if (!LaunchResults.TryTake(token, out var callback)) return;
                var status = GetStatus();
                Debug.LogWarning("[Bugsee] The SDK has not answered the launch after "
                    + LaunchResultTimeoutMs / 1000 + " s; taking its status (" + status + ") as the answer.");
                RunResult(callback, status == BugseeStatus.Launching || status == BugseeStatus.Launched);
            });
            return token;
        }

        [MonoPInvokeCallback(typeof(LaunchStartedNativeCallback))]
        static void OnNativeStarted(int token, int success)
        {
            try
            {
                MainThreadDispatcher.Run(() =>
                {
                    if (LaunchResults.TryTake(token, out var callback)) RunResult(callback, success != 0);
                });
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        public bool GetLaunched() => GetStatus() == BugseeStatus.Launched;

        public IReadOnlyDictionary<string, object> GetLaunchOptions() =>
            JsonRead.Object(ConsumeNativeString(_bugsee_get_launch_options()));

        public IReadOnlyDictionary<string, object> GetHostLaunchOptions() =>
            JsonRead.Object(ConsumeNativeString(_bugsee_get_host_launch_options())) ?? new Dictionary<string, object>();

        // The SDK owns the state, including Launching and Stopping, so a launch it refused reads as Stopped
        // and so does anything before the first Launch (a nil instance answers 0).
        public BugseeStatus GetStatus()
        {
            var native = _bugsee_get_status();
            return native >= 0 && native <= (int)BugseeStatus.Stopping
                ? (BugseeStatus)native
                : BugseeStatus.Stopped;
        }

        public void StartBlackout()
        {
            _bugsee_start_blackout();
        }

        public void EndBlackout()
        {
            _bugsee_end_blackout();
        }

        // Read from the SDK, as on Android, so no copy of its state can drift from it.
        public bool IsBlackout() => _bugsee_is_blackout() != 0;

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

        public void DeleteCollectedDataOnDevice() => DeleteCollectedData(DeleteScope.Everything, null);

        void ILifecycleResultBridge.DeleteCollectedData(DeleteScope scope, Action<bool> done) =>
            DeleteCollectedData(scope, done);

        // done (may be null) is answered by the wipe it joins or starts: true when the SDK reported the data
        // deleted, false when it refused, gave no report within the hard deadline, or the call never reached the
        // Unity main thread. A Delete of another scope than the wipe in flight waits for it and starts its own.
        void DeleteCollectedData(DeleteScope scope, Action<bool> done)
        {
            // One path, decided inside the main-thread closure and not on the SDK's state: Launch always installs
            // the managed hooks, even when the SDK then refuses it (status stays Stopped), so they are torn down
            // unconditionally (uninstalling is idempotent), and the SDK's status is not read off-thread. The
            // delete waits on the shared native stop (joining one already in flight); the native delete only
            // stops an SDK that is not already stopped. Requests run in order, as on Android: a delete is never
            // skipped because a Launch follows it (that Launch waits for the stop and so runs after the wipe), and
            // one requested after a Launch wipes that session.
            // started: 0 not yet, 1 the closure began (it answers done itself, from the wipe), 2 the 5 s wait gave up first
            // (the closure then does nothing, and done is answered false here). So done runs once, and false only when
            // no wipe was started.
            var started = 0;
            if (!MainThreadDispatcher.RunSyncLifecycle(() =>
                {
                    if (Interlocked.CompareExchange(ref started, 1, 0) != 0) return;
                    HostLogForwarder.Uninstall();
                    ExceptionPipeline.Uninstall();
                    CancelManagedReportUpload();
                    _openReport?.ReleaseSnapshotFiles();
                    _openReport = null;
                    _openReportHandle = null;
                    _networkLaunchBuffer.SetPhase(NetworkLaunchPhase.Stopped);
                    _breadcrumbLaunchBuffer.SetPhase(NetworkLaunchPhase.Stopped);
                    if (WipeGate.TryJoinOrQueue(scope, done, () => BeginNativeDelete(scope, done))) return;
                    StopNativeThen(() => BeginNativeDelete(scope, done), first: true);
                }))
            {
                if (Interlocked.CompareExchange(ref started, 2, 0) == 0)
                {
                    Debug.LogError("[Bugsee] DeleteCollectedDataOnDevice timed out waiting for the Unity main thread.");
                    RunResult(done, false);
                }
            }
        }

        // Main thread only. The wipe is a native operation in flight like a stop: a Launch or Relaunch requested
        // meanwhile waits for the SDK to report it finished (it clears its files on a background thread), so a
        // new session never starts, or has capture files deleted, under a running wipe. Same hard deadline as a
        // stop, so nothing waits forever; no soft one, as no caller waits for the wipe.
        // A Delete that follows one still wiping shares it when it has the same scope (two wipes would release a
        // deferred Launch at the later one's report, under the first one's file pass), and otherwise waits for it and
        // then starts its own: never two native wipes at once (see NativeWipeGate).
        static void BeginNativeDelete(DeleteScope scope, Action<bool> done)
        {
            if (WipeGate.TryJoinOrQueue(scope, done, () => BeginNativeDelete(scope, done))) return;
            var generation = DeleteCollectedDataLaunchGeneration.CaptureForPendingDelete();
            var token = WipeGate.Begin(scope, done, generation);
            GiveUpIfStopNotReported(token);
            _bugsee_delete_collected_data(token, (int)scope, OnNativeDeleted);
        }

        void CancelManagedReportUpload()
        {
            _reportUploadGeneration++;
            _managedReportUploadFence++;
            Interlocked.Exchange(ref _inFlightUploadToken, 0L);
            // Native fence first: an upload applying on the SDK queue must already read as cancelled when its
            // snapshot files go away, or a report stripped of those attachments is uploaded.
            _bugsee_invalidate_managed_report_uploads();
            ReleaseAllUploadSnapshotReports();
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
            Interlocked.Exchange(ref _inFlightUploadToken, (long)uploadToken);
            _bugsee_upload_managed_report(json, uploadToken, uploadFence, OnManagedReportCreateCompletion);
        }

        [MonoPInvokeCallback(typeof(ManagedReportUploadNativeCallback))]
        static void OnManagedReportCreateCompletion(int succeeded, ulong uploadToken)
        {
            // Runs on the SDK queue: an exception must never unwind into native code.
            try
            {
                // Instance is resolved through the static bridge reference set at upload time.
                var bridge = _uploadCompletionBridge;
                if (bridge == null)
                    return;
                bridge.HandleManagedReportCreateCompletion(succeeded != 0, uploadToken);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        static volatile IOSBridge _uploadCompletionBridge;

        void HandleManagedReportCreateCompletion(bool succeeded, ulong uploadToken)
        {
            ReleaseUploadSnapshotReport(uploadToken);
            // Clears the token only when it is still this upload's; a newer upload or a cancel already replaced it.
            if (Interlocked.CompareExchange(ref _inFlightUploadToken, 0L, (long)uploadToken) != (long)uploadToken)
                return;
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

        public void Trace(string name, object value)
        {
            var json = AttributeWire.ToTraceJson(value);
            if (TraceProbe.Enabled) TraceProbe.Last = json;
            _bugsee_trace(name ?? "", json);
        }

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
            WarnOnceAboutDroppedOptions(options);
            _bugsee_logException("UnityManagedException", ManagedExceptionPayload.ToJson(payload), true,
                ToJsonObject(options));
        }

        static bool _warnedDroppedOptions;

        // The native bridge drops these keys on iOS SDK 7.0.0-beta7 and later; say so once, in the Unity console too.
        static void WarnOnceAboutDroppedOptions(IDictionary<string, object> options)
        {
            if (_warnedDroppedOptions || _bugsee_exception_options_dropped() == 0) return;
            var dropped = ExceptionOptionDrops.PresentIn(options);
            if (dropped.Count == 0) return;
            _warnedDroppedOptions = true;
            Debug.LogWarning("[Bugsee] " + ExceptionOptionDrops.Message(dropped));
        }

        public void LogUnhandledExceptionPayload(
            ManagedExceptionPayload.Payload payload,
            IDictionary<string, object> options = null)
        {
            if (payload == null) return;
            _bugsee_logException("UnityManagedException", ManagedExceptionPayload.ToJson(payload), false,
                ToJsonObject(options));
        }

        public void TestCrash() => _bugsee_test_crash();

        public void ShowReportDialog(string summary, string description, IssueSeverity? severity, IList<string> labels) =>
            _bugsee_show_report(summary ?? "", description ?? "", IssueSeverityWire.ToWire(severity), ToJsonStringArray(labels));

        public void Upload(string summary, string description, IssueSeverity? severity, IList<string> labels) =>
            _bugsee_upload(summary ?? "", description ?? "", IssueSeverityWire.ToWire(severity), ToJsonStringArray(labels));

        public void Notify(string title, string body, IssueSeverity severity, IDictionary<string, string> fields, bool urgent) =>
            _bugsee_notify(title ?? "", NotifyPolicy.Body(body), (int)severity,
                ToJsonObject(NotifyPolicy.ToWireMap(fields)), urgent ? 1 : 0);

        public ISpanHandle StartTransaction(string name, string operation, IDictionary<string, object> attributes)
        {
            long id = IosSpanHandle._bugsee_start_transaction(name, operation, SpanPolicy.AttributesToJson(attributes));
            return id == 0 ? null : new IosSpanHandle(id);
        }

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

            _bugsee_set_attribute(key ?? "", AttributeWire.ToJson(value));
            if (GetAttribute(key) == null)
                throw new ArgumentException("attribute '" + key + "' was dropped", nameof(value));
        }

        public object GetAttribute(string key)
        {
            // Typed like Android: string, bool, long or double; a container set natively reads as its JSON text.
            return AttributeWire.Decode(ConsumeNativeString(_bugsee_get_attribute(key ?? "")));
        }

        public IReadOnlyDictionary<string, object> GetAllAttributes()
        {
            // A JSON object of key to the tagged text _bugsee_get_attribute returns, decoded the same way.
            var result = new Dictionary<string, object>();
            var tagged = JsonRead.Object(ConsumeNativeString(_bugsee_get_all_attributes()));
            if (tagged == null) return result;
            foreach (var kv in tagged)
                result[kv.Key] = AttributeWire.Decode(kv.Value as string);
            return result;
        }

        public void ClearAttribute(string key) => _bugsee_clear_attribute(key ?? "");

        public void ClearAllAttributes() => _bugsee_clear_all_attributes();

        public void SetSecureBuffer(int display, int[] packed)
        {
            if (packed == null) return;
            _bugsee_set_secure_buffer(display, packed, packed.Length);
        }

        public float GetSecureRectSnapshotScale() => _bugsee_screen_scale();

        // Unity's own view on the screen, in points, times the screen scale (registered pixels are divided by the scale again by
        // the snapshot): under iPad Split View, Slide Over or Stage Manager it is not the screen. A size that does not have the
        // orientation of Unity's screen (landscape against portrait) is a view caught mid-rotation, as UIKit and Unity update at
        // different moments: not settled. The aspect itself is not compared: a render size of another aspect is stretched.
        public bool TryGetSecureRectViewRegion(int screenWidth, int screenHeight, out int left, out int top, out int width, out int height, out bool settled)
        {
            left = top = width = height = 0;
            settled = false;
            float scale = _bugsee_screen_scale();
            var rect = new float[4];
            if (scale <= 0f || screenWidth <= 0 || screenHeight <= 0) return false;
            if (_bugsee_unity_view_region(rect) == 0)
            {
                // Not readable: report the whole screen (points times scale) as the fallback, with false, so the caller can use
                // it as the display's real size. Each axis comes from the screen itself: Unity may render at another aspect.
                var size = new float[2];
                if (_bugsee_screen_points(size) != 0)
                {
                    width = Mathf.RoundToInt(size[0] * scale);
                    height = Mathf.RoundToInt(size[1] * scale);
                }
                return false;
            }
            left = Mathf.RoundToInt(rect[0] * scale);
            top = Mathf.RoundToInt(rect[1] * scale);
            width = Mathf.RoundToInt(rect[2] * scale);
            height = Mathf.RoundToInt(rect[3] * scale);
            settled = width == height || screenWidth == screenHeight || (width > height) == (screenWidth > screenHeight);
            return width > 0 && height > 0;
        }

        public void CaptureViewHierarchy() => _bugsee_capture_view_hierarchy();

        static readonly UnsupportedApiNotice _resetPermissionNotice = new UnsupportedApiNotice(Debug.LogWarning);

        // iOS 7.0.0-beta7 has no video capture permission to reset: say so once instead of doing nothing silently.
        public void ResetVideoCapturePermission() =>
            _resetPermissionNotice.Once(UnsupportedApiNotice.ResetVideoCapturePermissionMessage);

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

            var nativeStage = IosNetworkEvent.ToNativeStage(iosEvent.Stage);
            if (nativeStage == null)
            {
                Debug.LogWarning("[Bugsee] Dropped network event with stage " + iosEvent.Stage + ": no iOS equivalent.");
                return;
            }

            var requiresFiltering = ChannelSubmit.NetworkRequiresFiltering() ? 1 : 0;
            var json = IosNativeCallbacks.ToNativeMapJson(
                iosEvent.ToChannelJson(nativeStage, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), "headers");
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

        // The JSON encoder is plain C# in the policy assembly (JsonWire), where it is unit-tested.
        static string ToJsonStringArray(IList<string> labels) => JsonWire.StringArray(labels);

        /// <summary>
        /// The SDK still reads host hints (Metal capture path, frame rate) from this launch option; the wrapper
        /// protocol carries only type, version, build and context.
        /// </summary>
        static IDictionary<string, object> WithWrapperInfo(IDictionary<string, object> options)
        {
            var merged = options != null
                ? new Dictionary<string, object>(options)
                : new Dictionary<string, object>();
            merged.TryGetValue("wrapper_info", out var supplied);
            if (IosHostPolicy.IsUsableWrapperInfo(supplied))
            {
                return merged; // the caller's own, with a string type
            }

            if (supplied != null)
            {
                // The SDK indexes into it as a map and compares "type" as a string: anything else is a crash at launch.
                Debug.LogWarning("[Bugsee] The wrapper_info option must be a map with a string \"type\"; it was replaced.");
                merged.Remove("wrapper_info");
            }

            var wrapperInfo = new Dictionary<string, object>
            {
                { "type", "UNITY" },
                { "version", BugseePackageVersion.Version },
                { "metal", SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Metal },
                { "fps", IosHostPolicy.CaptureFrameRate(Application.targetFrameRate, Screen.currentResolution.refreshRate) },
            };
            var wrapperBuild = IosHostPolicy.WrapperBuild(BugseePackageVersion.Build);
            if (wrapperBuild != null) wrapperInfo["build"] = wrapperBuild;
            merged["wrapper_info"] = wrapperInfo;
            return merged;
        }

        static string ToJsonObject(IDictionary<string, object> map) => JsonWire.Object(map);

        [DllImport("__Internal")] static extern void _bugsee_launch(
            string appToken, string optionsJson, int startedToken, LaunchStartedNativeCallback started);
        [DllImport("__Internal")] static extern void _bugsee_relaunch(
            string optionsJson, int startedToken, LaunchStartedNativeCallback started);
        [DllImport("__Internal")] static extern void _bugsee_stop(int token, StopNativeCallback completion);
        [DllImport("__Internal")] static extern int _bugsee_get_status();
        [DllImport("__Internal")] static extern int _bugsee_exceptions_filed();
        [DllImport("__Internal")] static extern void _bugsee_start_blackout();
        [DllImport("__Internal")] static extern void _bugsee_end_blackout();
        [DllImport("__Internal")] static extern int _bugsee_is_blackout();
        [DllImport("__Internal")] static extern void _bugsee_log(string message, int level);
        [DllImport("__Internal")] static extern void _bugsee_channel_log(string message, int level, int source);
        [DllImport("__Internal")] static extern void _bugsee_channel_network(string eventJson, int requiresFiltering);
        [DllImport("__Internal")] static extern void _bugsee_channel_breadcrumb(
            string category,
            string message,
            int iosLevel,
            double timestampUnixSeconds);
        [DllImport("__Internal")] static extern void _bugsee_delete_collected_data(int token, int scope, DeleteNativeCallback completion);
        [DllImport("__Internal")] static extern void _bugsee_invalidate_managed_report_uploads();
        [DllImport("__Internal")] static extern void _bugsee_upload_managed_report(
            string reportJson,
            ulong uploadId,
            ulong uploadFence,
            ManagedReportUploadNativeCallback callback);
        [DllImport("__Internal")] static extern void _bugsee_trace(string name, string valueJson);
        [DllImport("__Internal")] static extern void _bugsee_event(string name, string paramsJson);
        [DllImport("__Internal")] static extern void _bugsee_logException(string name, string reason, [MarshalAs(UnmanagedType.U1)] bool handled, string optionsJson);
        [DllImport("__Internal")] static extern void _bugsee_test_crash();
        [DllImport("__Internal")] static extern int _bugsee_exception_options_dropped();
        [DllImport("__Internal")] static extern void _bugsee_show_report(string summary, string description, int severity, string labelsJson);
        [DllImport("__Internal")] static extern void _bugsee_upload(string summary, string description, int severity, string labelsJson);
        [DllImport("__Internal")] static extern void _bugsee_notify(string title, string body, int severity, string fieldsJson, int urgent);
        [DllImport("__Internal")] static extern IntPtr _bugsee_get_launch_options();
        [DllImport("__Internal")] static extern IntPtr _bugsee_get_host_launch_options();
        [DllImport("__Internal")] static extern void _bugsee_set_attribute(string key, string valueJson);
        [DllImport("__Internal")] static extern IntPtr _bugsee_get_attribute(string key);
        [DllImport("__Internal")] static extern IntPtr _bugsee_get_all_attributes();
        [DllImport("__Internal")] static extern void _bugsee_clear_attribute(string key);
        [DllImport("__Internal")] static extern void _bugsee_clear_all_attributes();
        [DllImport("__Internal")] static extern void _bugsee_set_email(string email);
        [DllImport("__Internal")] static extern IntPtr _bugsee_get_email();
        [DllImport("__Internal")] static extern void _bugsee_clear_email();
        [DllImport("__Internal")] static extern int _bugsee_unity_view_region(float[] outRect);
        [DllImport("__Internal")] static extern int _bugsee_screen_points(float[] outWH);
        [DllImport("__Internal")] static extern void _bugsee_set_secure_buffer(int display, int[] packed, int packedLength);
        [DllImport("__Internal")] static extern float _bugsee_screen_scale();
        [DllImport("__Internal")] static extern void _bugsee_capture_view_hierarchy();
        [DllImport("__Internal")] static extern void _bugsee_feedback_show();
        [DllImport("__Internal")] static extern void _bugsee_feedback_set_greeting(string message);
        [DllImport("__Internal")] static extern int _bugsee_appearance_set_color(string propertyName, int r, int g, int b, int a);
        [DllImport("__Internal")] static extern IntPtr _bugsee_appearance_get_color(string propertyName);
        [DllImport("__Internal")] static extern int _bugsee_appearance_set_string(string propertyName, string propertyValue);
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
                // The native theme colors are non-nil: a null color cannot reset to the default.
                if (string.IsNullOrEmpty(propertyName) || !color.HasValue) return this;
                var c = color.Value;
                // Only properties the native theme applied are reported by ToMap.
                if (_bugsee_appearance_set_color(propertyName, c.r, c.g, c.b, c.a) != 0)
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
                // null restores the SDK's default text.
                if (_bugsee_appearance_set_string(propertyName, value) != 0)
                {
                    if (value == null) _cache.Remove(propertyName);
                    else _cache[propertyName] = value;
                }
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
                timestampMs = timestamp,
            });
    }
}
#endif
