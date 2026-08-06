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
