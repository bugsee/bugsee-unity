namespace Bugsee.Contracts.Options
{
    /// <summary>
    /// Launch/option keys mirroring <c>com.bugsee.library.contracts.options.Options</c>.
    /// </summary>
    public static class Options
    {
        public const string Duration = "com.bugsee.option.config.duration";
        public const string WifiOnlyUpload = "com.bugsee.option.config.wifi-only-upload";
        public const string ReportProcessingInProcess = "com.bugsee.option.config.report-processing-in-process";
        public const string ReportHandlerCallbackTimeout = "com.bugsee.option.config.report-handler-callback-timeout";
        public const string DetectAndReportExit = "com.bugsee.option.detect.exit";
        public const string DetectAndReportExitLowMemory = "com.bugsee.option.detect.exit.low_memory";
        public const string DetectAndReportExitNotResponding = "com.bugsee.option.detect.exit.not_responding";
        public const string DetectAndReportExitNotRespondingAsCrash = "com.bugsee.option.detect.exit.not_responding.as_crash";
        public const string DetectAndReportExitExcessiveResourceUsage = "com.bugsee.option.detect.exit.excessive_resource_usage";
        public const string DetectAndReportExitDependencyDied = "com.bugsee.option.detect.exit.dependency_died";
        public const string DetectAndReportExitUserRequested = "com.bugsee.option.detect.exit.user_requested";
        public const string DetectAndReportExitUserWasStopped = "com.bugsee.option.detect.exit.user_was_stopped";
        public const string DetectAndReportExitPermissionChanged = "com.bugsee.option.detect.exit.permission_changed";
        public const string DetectAndReportExitPackageUpdated = "com.bugsee.option.detect.exit.package_updated";
        public const string DetectAndReportExitPackageStateChanged = "com.bugsee.option.detect.exit.package_state_changed";
        public const string DetectAndReportExitOther = "com.bugsee.option.detect.exit.other";
        public const string DetectAndReportExitUnknown = "com.bugsee.option.detect.exit.unknown";
        public const string DetectAndReportHang = "com.bugsee.option.detect.hang";
        public const string DetectAndReportHangFairLevel = "com.bugsee.option.detect.hang.level.fair";
        public const string DetectAndReportHangMediumLevel = "com.bugsee.option.detect.hang.level.medium";
        public const string DetectAndReportHangSevereLevel = "com.bugsee.option.detect.hang.level.severe";
        public const string DetectAndReportCrash = "com.bugsee.option.detect.crash";
        public const string DetectAndReportMainThreadMisuse = "com.bugsee.option.detect.main_thread_misuse";
        public const string DetectAndReportEarlyCrash = "com.bugsee.option.detect.early-crash";
        public const string DetectAndReportHttpErrors = "com.bugsee.option.detect.http-errors";
        public const string DetectAndReportAnomaly = "com.bugsee.option.detect.anomaly";
        public const string DetectFrustration = "com.bugsee.option.detect.frustration";
        public const string CaptureLogs = "com.bugsee.option.capture.logs";
        public const string CaptureLogsLevel = "com.bugsee.option.capture.logs.level";
        public const string CaptureLogsUseAllSources = "com.bugsee.option.capture.logs.allsources";
        public const string CaptureScreenshot = "com.bugsee.option.capture.screenshot";
        public const string CaptureScreenshotScale = "com.bugsee.option.capture.screenshot.scale";
        public const string CaptureNetwork = "com.bugsee.option.capture.network";
        public const string CaptureNetworkBodySizeLimit = "com.bugsee.option.capture.network.body-size-limit";
        public const string CaptureNetworkBodyWithoutType = "com.bugsee.option.capture.network.body-without-type";
        public const string CaptureNetworkUseDefaultSanitizer = "com.bugsee.option.capture.network.default-sanitizer";
        public const string CaptureNetworkOnLaunch = "com.bugsee.option.capture.network.on-launch";
        public const string CaptureViewHierarchy = "com.bugsee.option.capture.view-hierarchy";
        public const string CaptureRespectFlagSecure = "com.bugsee.option.capture.respect-flag-secure";
        public const string CaptureBreadcrumbs = "com.bugsee.option.capture.breadcrumbs";
        public const string CaptureBreadcrumbsExtras = "com.bugsee.option.capture.breadcrumbs.extras";
        public const string AdvancedWebViewCapture = "com.bugsee.option.capture.webview.advanced";
        public const string WebViewDomainAllowlist = "com.bugsee.option.capture.webview.domain-allowlist";
        public const string WebViewReportTrigger = "com.bugsee.option.capture.webview.report-trigger";
        public const string CaptureVideo = "com.bugsee.option.capture.video";
        public const string CaptureVideoMode = "com.bugsee.option.capture.video.mode";
        public const string CaptureVideoQuality = "com.bugsee.option.capture.video.quality";
        public const string CaptureVideoFrameRate = "com.bugsee.option.capture.video.frame-rate";
        public const string CaptureVideoAdaptive = "com.bugsee.option.capture.video.adaptive";
        public const string CaptureVideoScale = "com.bugsee.option.capture.video.scale";
        public const string CaptureVideoSecureScrolling = "com.bugsee.option.capture.video.secure-scrolling";
        public const string CaptureVideoUsingCustomMuxer = "com.bugsee.option.capture.video.custom-muxer";
        public const string CaptureVideoFullscreenRememberUserDecision = "com.bugsee.option.capture.video.fullscreen-remember-decision";
        public const string CaptureVideoFullscreenKeepRunning = "com.bugsee.option.capture.video.fullscreen-keep-running";
        public const string ReportingTriggerByShake = "com.bugsee.option.reporting.triggers.shake";
        public const string ReportingTriggerByScreenshot = "com.bugsee.option.reporting.triggers.screenshot";
        public const string ReportingTriggerByNotification = "com.bugsee.option.reporting.triggers.notification-bar";
        public const string ReportingTriggerByBroadcast = "com.bugsee.option.reporting.triggers.broadcast";
        public const string ReportingDefaultCrashPriority = "com.bugsee.option.reporting.defaults.crash-priority";
        public const string ReportingDefaultErrorPriority = "com.bugsee.option.reporting.defaults.error-priority";
        public const string ReportingDefaultBugPriority = "com.bugsee.option.reporting.defaults.bug-priority";
        public const string ReportingUISummaryRequired = "com.bugsee.option.reporting.ui.summary-required";
        public const string ReportingUIDescriptionRequired = "com.bugsee.option.reporting.ui.description-required";
        public const string ReportingUIEmailRequired = "com.bugsee.option.reporting.ui.email-required";
        public const string ReportingUILabelsEnabled = "com.bugsee.option.reporting.ui.labels-enabled";
        public const string ReportingUILabelsRequired = "com.bugsee.option.reporting.ui.labels-required";
        public const string ReportingUIPrioritySelectorEnabled = "com.bugsee.option.reporting.ui.priority-selector-enabled";
        public const string PerformanceMonitoring = "com.bugsee.option.performance.enabled";
        public const string PerformanceSampleRate = "com.bugsee.option.performance.sample-rate";
        public const string PerformanceAdaptiveSampling = "com.bugsee.option.performance.adaptive-sampling";
        public const string PerformanceUploadMode = "com.bugsee.option.performance.upload-mode";
        public const string Debug = "com.bugsee.option.$$DEBUG";
        public const string Endpoint = "com.bugsee.option.$$ENDPOINT";
    }
}
