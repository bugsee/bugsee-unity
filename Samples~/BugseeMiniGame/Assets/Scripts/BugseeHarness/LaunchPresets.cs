using System;
using Bugsee.Contracts.Options;
using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>
    /// Launch-option presets for the sample (issue #118), applied on top of the options the bootstrap always sets. A preset is a
    /// complete statement of what it wants: a relaunch REPLACES the SDK's option set, so what a preset does not set goes back to the
    /// SDK default. <c>minimal</c> and <c>detect</c> use the typed properties (<see cref="AndroidLaunchOptions"/>,
    /// <see cref="IOSLaunchOptions"/>) and set every boolean to the opposite value of each other, so each option differs from the SDK
    /// default in at least one of them; <c>privacy</c> uses <see cref="OptionsBuilder"/>. The <c>presets</c> e2e scenario reads the
    /// options back and compares them (it proves the SDK took the options, and checks two that change behaviour; the rest is not
    /// observable from the sample). Selected with the intent extra <c>bugsee_preset</c> (Android) or the variable
    /// <c>BUGSEE_PRESET</c> (iOS), or from the debug panel. <c>detect</c> makes a logged exception fatal
    /// (<c>TreatUnityLogExceptionsAsFatal</c>); <c>privacy</c> uploads over Wi-Fi only, which can hold reports on a device without it.
    /// </summary>
    static class LaunchPresets
    {
        public const string Default = "default";
        public const string Minimal = "minimal";
        public const string Detect = "detect";
        public const string Privacy = "privacy";

        public static readonly string[] Names = { Default, Minimal, Detect, Privacy };

        /// <summary>The preset's name, or <see cref="Default"/> (with a warning) when it is unknown or empty.</summary>
        public static string Resolve(string name)
        {
            if (string.IsNullOrEmpty(name)) return Default;
            name = name.Trim().ToLowerInvariant();
            if (Array.IndexOf(Names, name) >= 0) return name;
            Debug.LogWarning("[BugseeSample] unknown launch preset '" + name + "', using default");
            return Default;
        }

        public static void Apply(string preset, BugseeLaunchOptions o)
        {
            switch (preset)
            {
                case Minimal: ApplySwitches(o, false); ApplyMinimal(o); break;
                case Detect: ApplySwitches(o, true); ApplyDetect(o); break;
                case Privacy: ApplyPrivacy(o); break;
            }
        }

        // The options minimal and detect set to opposite values.
        static void ApplySwitches(BugseeLaunchOptions o, bool on)
        {
            o.DetectAndReportCrash = on;
            o.DetectAndReportHang = on;
            o.TreatUnityLogExceptionsAsFatal = on;
            o.DetectAndReportEarlyCrash = on;
            o.DetectAndReportHttpErrors = on;
            o.DetectAndReportAnomaly = on;
            o.DetectFrustration = on;
            o.CaptureLogs = on;
            o.CaptureScreenshot = on;
            o.CaptureNetwork = on;
            o.CaptureNetworkBodyWithoutType = on;
            o.CaptureNetworkUseDefaultSanitizer = on;
            o.CaptureViewHierarchy = on;
            o.CaptureBreadcrumbs = on;
            o.CaptureBreadcrumbsExtras = on;
            o.ReportingTriggerByShake = on;
            o.ReportingTriggerByScreenshot = on;
            o.PerformanceAdaptiveSampling = on;
            if (o is AndroidLaunchOptions android)
            {
                android.DetectAndReportExit = on;
                android.DetectAndReportExitLowMemory = on;
                android.DetectAndReportExitNotResponding = on;
                android.DetectAndReportExitNotRespondingAsCrash = on;
                android.DetectAndReportExitExcessiveResourceUsage = on;
                android.DetectAndReportExitDependencyDied = on;
                android.DetectAndReportExitUserRequested = on;
                android.DetectAndReportExitUserWasStopped = on;
                android.DetectAndReportExitPermissionChanged = on;
                android.DetectAndReportExitPackageUpdated = on;
                android.DetectAndReportExitPackageStateChanged = on;
                android.DetectAndReportExitOther = on;
                android.DetectAndReportExitUnknown = on;
                android.DetectAndReportMainThreadMisuse = on;
                android.CaptureLogsUseAllSources = on;
                android.CaptureNetworkOnLaunch = on;
                android.AdvancedWebViewCapture = on;
                android.WebViewReportTrigger = on;
                android.CaptureRespectFlagSecure = on;
            }
        }

        // Nothing optional captured, no trigger, no detector.
        static void ApplyMinimal(BugseeLaunchOptions o)
        {
            o.Duration = 30;
            o.WifiOnlyUpload = false;
            o.ReportHandlerCallbackTimeout = 10;
            o.CaptureVideo = false;
            o.CaptureLogsLevel = LogLevel.Error;
            o.PerformanceMonitoring = false;
            o.ReportingUISummaryRequired = false;
            o.ReportingUIDescriptionRequired = false;
            o.ReportingUIEmailRequired = false;
            o.ReportingUILabelsEnabled = false;
            o.ReportingUILabelsRequired = false;
            o.ReportingUIPrioritySelectorEnabled = false;
            if (o is AndroidLaunchOptions android)
                android.CaptureVideoMode = VideoMode.V1;
            if (o is IOSLaunchOptions ios)
                ios.CrashHandlerType = CrashHandlerType.BSD;
        }

        // Every detector on, with the hang levels and capture depth at their most sensitive.
        static void ApplyDetect(BugseeLaunchOptions o)
        {
            o.Duration = 120;
            o.WifiOnlyUpload = false;
            o.ReportHandlerCallbackTimeout = 60;
            o.DetectAndReportHangFairLevel = 2000;
            o.DetectAndReportHangMediumLevel = 4000;
            o.DetectAndReportHangSevereLevel = 8000;
            o.CaptureLogsLevel = LogLevel.Verbose;
            o.CaptureScreenshotScale = 0.5f;
            if (o is AndroidLaunchOptions android)
                android.WebViewDomainAllowlist = "example.com";
            if (o is IOSLaunchOptions ios)
                ios.CrashHandlerType = CrashHandlerType.MachAndBSD;
        }

        // Capture less and ask more of the reporter: scaled video, a small network body limit, a required reporting form.
        static void ApplyPrivacy(BugseeLaunchOptions o)
        {
            var builder = new OptionsBuilder()
                .Duration(45)
                .Set(Options.WifiOnlyUpload, true)
                .Set(Options.ReportHandlerCallbackTimeout, 20)
                .SetLogLevel(LogLevel.Warning)
                .Set(Options.CaptureScreenshot, false)
                .Set(Options.CaptureViewHierarchy, false)
                .Set(Options.CaptureNetworkBodySizeLimit, 1024)
                .Set(Options.CaptureNetworkBodyWithoutType, false)
                .Set(Options.CaptureNetworkUseDefaultSanitizer, true)
                .Set(Options.CaptureBreadcrumbsExtras, false)
                .SetVideoQuality(VideoQuality.Medium)
                .SetFrameRate(FrameRate.Low)
                .Set(Options.CaptureVideoScale, 0.5f)
                .SetDefaultCrashPriority(IssueSeverity.Medium)
                .SetDefaultErrorPriority(IssueSeverity.Critical)
                .SetDefaultBugPriority(IssueSeverity.Medium)
                .Set(Options.ReportingUISummaryRequired, true)
                .Set(Options.ReportingUIDescriptionRequired, true)
                .Set(Options.ReportingUIEmailRequired, true)
                .Set(Options.ReportingUILabelsEnabled, true)
                .Set(Options.ReportingUILabelsRequired, true)
                .Set(Options.ReportingUIPrioritySelectorEnabled, true)
                .Set(Options.PerformanceAdaptiveSampling, false)
                .Set(Options.PerformanceUploadMode, "realtime");
            foreach (var pair in builder.ToDictionary())
                o.SetCustomOption(pair.Key, pair.Value);
            if (o is AndroidLaunchOptions android)
            {
                android.CaptureRespectFlagSecure = true;
                android.CaptureVideoSecureScrolling = true;
                android.CaptureVideoAdaptive = true;
            }
            if (o is IOSLaunchOptions ios)
                ios.CrashHandlerType = CrashHandlerType.Mach;
        }
    }
}
