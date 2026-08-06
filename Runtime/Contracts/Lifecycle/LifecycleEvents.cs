namespace Bugsee.Contracts.Lifecycle
{
    /// <summary>Mirrors com.bugsee.library.contracts.lifecycle.LifecycleEvents (+ feedback).</summary>
    public static class LifecycleEvents
    {
        public const string Launching = "com.bugsee.lifecycle.Launching";
        public const string Launched = "com.bugsee.lifecycle.Launched";
        public const string Stopping = "com.bugsee.lifecycle.Stopping";
        public const string Stopped = "com.bugsee.lifecycle.Stopped";
        public const string BlackoutEnded = "com.bugsee.lifecycle.BlackoutEnded";
        public const string BlackoutStarted = "com.bugsee.lifecycle.BlackoutStarted";
        public const string RelaunchedAfterCrash = "com.bugsee.lifecycle.RelaunchedAfterCrash";
        public const string BeforeReportShown = "com.bugsee.lifecycle.BeforeReportShown";
        public const string AfterReportShown = "com.bugsee.lifecycle.AfterReportShown";
        public const string BeforeReportUploaded = "com.bugsee.lifecycle.BeforeReportUploaded";
        public const string AfterReportUploaded = "com.bugsee.lifecycle.AfterReportUploaded";
        public const string BeforeReportAssembled = "com.bugsee.lifecycle.BeforeReportAssembled";
        public const string AfterReportAssembled = "com.bugsee.lifecycle.AfterReportAssembled";
        public const string ReportAssemblyFailed = "com.bugsee.lifecycle.ReportAssemblyFailed";
        public const string ReportUploadFailedWithFutureRetry = "com.bugsee.lifecycle.ReportUploadFailedWithFutureRetry";
        public const string ReportUploadFailed = "com.bugsee.lifecycle.ReportUploadFailed";

        // Feedback extension
        public const string BeforeFeedbackShown = "com.bugsee.lifecycle.BeforeFeedbackShown";
        public const string AfterFeedbackShown = "com.bugsee.lifecycle.AfterFeedbackShown";
    }
}
