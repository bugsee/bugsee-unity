namespace Bugsee.Contracts.Options
{
    /// <summary>
    /// Android launch options. Shared options live on <see cref="BugseeLaunchOptions"/>;
    /// this type adds Android-only write-only properties.
    /// </summary>
    public sealed class AndroidLaunchOptions : BugseeLaunchOptions
    {
        public AndroidLaunchOptions() { }

        // --- Config (Android) ---

        /// <summary>
        /// Maximum number of reports waiting to be sent. Default when omitted: 30; the SDK raises a value below 1 to 1.
        /// Over the limit, whole pending reports are deleted (errors first, then bug reports, then crashes, oldest first;
        /// the newest crash and a report being exported or uploaded are kept). Lower values lose reports sooner.
        /// </summary>
        public int MaxPendingReports
        {
            set => Set(Options.MaxPendingReports, value);
        }

        /// <summary>
        /// Maximum age in days of a report waiting to be sent; older ones are deleted whatever their type.
        /// Default when omitted: 30. 0 or a negative value disables aging (reports then wait until sent or evicted by
        /// <see cref="BugseeLaunchOptions.MaxDataSize"/> or <see cref="MaxPendingReports"/>).
        /// </summary>
        public int MaxPendingReportAge
        {
            set => Set(Options.MaxPendingReportAge, value);
        }

        public bool ReportProcessingInProcess
        {
            set => Set(Options.ReportProcessingInProcess, value);
        }

        // --- Detect: ApplicationExitInfo / main-thread (Android) ---

        public bool DetectAndReportExit
        {
            set => Set(Options.DetectAndReportExit, value);
        }

        public bool DetectAndReportExitLowMemory
        {
            set => Set(Options.DetectAndReportExitLowMemory, value);
        }

        public bool DetectAndReportExitNotResponding
        {
            set => Set(Options.DetectAndReportExitNotResponding, value);
        }

        public bool DetectAndReportExitNotRespondingAsCrash
        {
            set => Set(Options.DetectAndReportExitNotRespondingAsCrash, value);
        }

        public bool DetectAndReportExitExcessiveResourceUsage
        {
            set => Set(Options.DetectAndReportExitExcessiveResourceUsage, value);
        }

        public bool DetectAndReportExitDependencyDied
        {
            set => Set(Options.DetectAndReportExitDependencyDied, value);
        }

        public bool DetectAndReportExitUserRequested
        {
            set => Set(Options.DetectAndReportExitUserRequested, value);
        }

        public bool DetectAndReportExitUserWasStopped
        {
            set => Set(Options.DetectAndReportExitUserWasStopped, value);
        }

        public bool DetectAndReportExitPermissionChanged
        {
            set => Set(Options.DetectAndReportExitPermissionChanged, value);
        }

        public bool DetectAndReportExitPackageUpdated
        {
            set => Set(Options.DetectAndReportExitPackageUpdated, value);
        }

        public bool DetectAndReportExitPackageStateChanged
        {
            set => Set(Options.DetectAndReportExitPackageStateChanged, value);
        }

        public bool DetectAndReportExitOther
        {
            set => Set(Options.DetectAndReportExitOther, value);
        }

        public bool DetectAndReportExitUnknown
        {
            set => Set(Options.DetectAndReportExitUnknown, value);
        }

        /// <summary>
        /// Report a low-memory kill of a process that was not in the foreground at death as a non-fatal error instead of
        /// a crash (foreground kills stay crashes). Default when omitted: false. Needs <see cref="DetectAndReportExitLowMemory"/>
        /// and a device that reports low-memory kills; elsewhere a background SIGKILL is already a non-fatal error.
        /// </summary>
        public bool DetectAndReportExitLowMemoryBackgroundAsError
        {
            set => Set(Options.DetectAndReportExitLowMemoryBackgroundAsError, value);
        }

        /// <summary>
        /// Sample the main thread's stack during a hang and blame the derived frames instead of one snapshot taken when
        /// reporting. Default when omitted: true. Only has effect with <see cref="BugseeLaunchOptions.DetectAndReportHang"/>.
        /// </summary>
        public bool DetectAndReportHangSampling
        {
            set => Set(Options.DetectAndReportHangSampling, value);
        }

        /// <summary>
        /// Sample the main thread's stack during an ANR and blame the derived frames instead of one snapshot taken
        /// when the OS confirms it. Default when omitted: true. Only has effect with <see cref="DetectAndReportExitNotResponding"/>.
        /// </summary>
        public bool DetectAndReportAnrSampling
        {
            set => Set(Options.DetectAndReportAnrSampling, value);
        }

        /// <summary>
        /// Detect and report native (NDK) crashes. Default when omitted: true. Also off whenever
        /// <see cref="BugseeLaunchOptions.DetectAndReportCrash"/> is false. The key belongs to the <c>bugsee-android-ndk</c>
        /// extension, which the wrapper always declares: the SDK knows it only once that extension has registered,
        /// otherwise it is dropped. On Unity 6 builds that get the Bugsee Gradle plugin (AGP 8.6+) the extension is
        /// currently not registered (bugsee-unity#226), so this option has no effect there.
        /// </summary>
        public bool DetectAndReportCrashNdk
        {
            set => Set(Options.DetectAndReportCrashNdk, value);
        }

        /// <summary>
        /// Include the OS tombstone's raw log buffers in a native crash report. Default when omitted: false. Privacy: these
        /// buffers bypass Bugsee's log filters and can hold other apps' log lines and sensitive data, so they are uploaded
        /// unredacted. Same NDK-extension registration caveat as <see cref="DetectAndReportCrashNdk"/> (#226).
        /// </summary>
        public bool CrashNdkIncludeTombstoneLogs
        {
            set => Set(Options.CrashNdkIncludeTombstoneLogs, value);
        }

        public bool DetectAndReportMainThreadMisuse
        {
            set => Set(Options.DetectAndReportMainThreadMisuse, value);
        }

        // --- Capture (Android) ---

        public bool CaptureNetworkOnLaunch
        {
            set => Set(Options.CaptureNetworkOnLaunch, value);
        }

        public bool CaptureLogsUseAllSources
        {
            set => Set(Options.CaptureLogsUseAllSources, value);
        }

        public bool CaptureRespectFlagSecure
        {
            set => Set(Options.CaptureRespectFlagSecure, value);
        }

        public bool AdvancedWebViewCapture
        {
            set => Set(Options.AdvancedWebViewCapture, value);
        }

        public string WebViewDomainAllowlist
        {
            set => Set(Options.WebViewDomainAllowlist, value);
        }

        public bool WebViewReportTrigger
        {
            set => Set(Options.WebViewReportTrigger, value);
        }

        public VideoMode CaptureVideoMode
        {
            set => Set(Options.CaptureVideoMode, (int)value);
        }

        public bool CaptureVideoAdaptive
        {
            set => Set(Options.CaptureVideoAdaptive, value);
        }

        public bool CaptureVideoSecureScrolling
        {
            set => Set(Options.CaptureVideoSecureScrolling, value);
        }

        public bool CaptureVideoUsingCustomMuxer
        {
            set => Set(Options.CaptureVideoUsingCustomMuxer, value);
        }

        public bool CaptureVideoFullscreenRememberUserDecision
        {
            set => Set(Options.CaptureVideoFullscreenRememberUserDecision, value);
        }

        public bool CaptureVideoFullscreenKeepRunning
        {
            set => Set(Options.CaptureVideoFullscreenKeepRunning, value);
        }

        // --- Reporting triggers (Android) ---

        public bool ReportingTriggerByNotification
        {
            set => Set(Options.ReportingTriggerByNotification, value);
        }

        public bool ReportingTriggerByBroadcast
        {
            set => Set(Options.ReportingTriggerByBroadcast, value);
        }
    }
}
