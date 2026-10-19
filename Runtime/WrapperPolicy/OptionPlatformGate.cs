using System.Collections.Generic;
using Bugsee.Contracts.Options;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Options one native SDK has no counterpart for. Android-only: absent from the iOS headers and binary as of
    /// 7.0.0-beta7 (iOS keeps an unknown key and getLaunchOptions then shows it). iOS-only: the Android SDK 7.4.2
    /// drops keys it does not know.
    /// </summary>
    public static class OptionPlatformGate
    {
        static readonly HashSet<string> AndroidOnlyKeys = BuildAndroidOnlyKeys();

        public static readonly IReadOnlyCollection<string> AndroidOnly = new List<string>(AndroidOnlyKeys).AsReadOnly();

        static readonly HashSet<string> IosOnlyKeys = new HashSet<string>
        {
            Options.CaptureCrashHandlerType,
        };

        public static readonly IReadOnlyCollection<string> IosOnly = new List<string>(IosOnlyKeys).AsReadOnly();

        static HashSet<string> BuildAndroidOnlyKeys()
        {
            return new HashSet<string>
            {
                Options.DetectAndReportExitLowMemory,
                Options.DetectAndReportExitNotResponding,
                Options.DetectAndReportExitNotRespondingAsCrash,
                Options.DetectAndReportExitExcessiveResourceUsage,
                Options.DetectAndReportExitDependencyDied,
                Options.DetectAndReportExitUserRequested,
                Options.DetectAndReportExitUserWasStopped,
                Options.DetectAndReportExitPermissionChanged,
                Options.DetectAndReportExitPackageUpdated,
                Options.DetectAndReportExitPackageStateChanged,
                Options.DetectAndReportExitOther,
                Options.DetectAndReportExitUnknown,
                Options.ReportingTriggerByNotification,
                Options.ReportingTriggerByBroadcast,
                Options.CaptureLogsUseAllSources,
                Options.CaptureRespectFlagSecure,
                Options.CaptureVideoMode,
                Options.CaptureVideoUsingCustomMuxer,
                Options.CaptureVideoSecureScrolling,
                Options.CaptureVideoFullscreenRememberUserDecision,
                Options.CaptureVideoFullscreenKeepRunning,
                Options.ReportHandlerCallbackTimeout,
                Options.PerformanceUploadMode,
                Options.ReportProcessingInProcess,
                Options.WebViewDomainAllowlist,
                // Android keys with no constant in Options yet (typed options come with #96); the strings are the
                // Android SDK 7.4.2 keys. iOS keeps an unknown key verbatim, so they are gated like the rest.
                "com.bugsee.option.detect.crash-ndk",
                "com.bugsee.option.crash-ndk.tombstone-logs",
                "com.bugsee.option.detect.anr.sampling",
                "com.bugsee.option.detect.hang.sampling",
                "com.bugsee.option.detect.memory_leaks",
                "com.bugsee.option.detect.memory_leaks.mode",
                "com.bugsee.option.detect.memory_leaks.deep_sample_rate",
                "com.bugsee.option.detect.thread_leaks",
                "com.bugsee.option.detect.exit.bg_low_memory_as_error",
                "com.bugsee.option.config.max-pending-reports",
                "com.bugsee.option.config.max-pending-report-age",
            };
        }

        public static IDictionary<string, object> ForIos(IDictionary<string, object> options)
        {
            return Without(options, AndroidOnlyKeys);
        }

        public static IDictionary<string, object> ForAndroid(IDictionary<string, object> options)
        {
            return Without(options, IosOnlyKeys);
        }

        static IDictionary<string, object> Without(IDictionary<string, object> options, HashSet<string> drop)
        {
            if (options == null || options.Count == 0)
                return new Dictionary<string, object>();

            var result = new Dictionary<string, object>(options.Count);
            foreach (var kv in options)
            {
                if (kv.Key == null || drop.Contains(kv.Key))
                    continue;
                result[kv.Key] = kv.Value;
            }

            return result;
        }
    }
}
