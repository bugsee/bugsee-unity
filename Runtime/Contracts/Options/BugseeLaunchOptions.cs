using System;
using System.Collections.Generic;

namespace Bugsee.Contracts.Options
{
    /// <summary>
    /// Base launch options: write-only properties over an internal map.
    /// Unset keys are omitted so the native SDK applies its defaults.
    /// Prefer <see cref="AndroidLaunchOptions"/> or <see cref="IOSLaunchOptions"/>;
    /// do not construct this type directly from app code.
    /// </summary>
    /// <remarks>
    /// Property names follow Android 7.0 contracts for now. Revisit unification
    /// when the Bugsee iOS SDK reaches RC or stable.
    /// </remarks>
    public abstract class BugseeLaunchOptions
    {
        readonly Dictionary<string, object> _map = new Dictionary<string, object>();

        protected BugseeLaunchOptions() { }

        /// <summary>Sets or replaces an arbitrary option key (escape hatch).</summary>
        public void SetCustomOption(string key, object value)
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("key is required", nameof(key));
            Set(key, value);
        }

        /// <summary>Removes all previously set options from this instance.</summary>
        public void Clear() => _map.Clear();

        /// <summary>Snapshot of set keys only (for the native bridge).</summary>
        public Dictionary<string, object> ToDictionary() =>
            new Dictionary<string, object>(_map);

        /// <summary>
        /// Enables <c>Bugsee.Launch(token, launchOptions)</c> via conversion to
        /// <see cref="Dictionary{TKey,TValue}"/> (avoids null-literal overload ambiguity).
        /// </summary>
        public static implicit operator Dictionary<string, object>(BugseeLaunchOptions options) =>
            options == null ? null : new Dictionary<string, object>(options._map);

        protected void Set(string key, object value)
        {
            if (value == null) _map.Remove(key);
            else _map[key] = value;
        }

        // --- Config ---

        public int Duration
        {
            set => Set(Options.Duration, value);
        }

        public bool WifiOnlyUpload
        {
            set => Set(Options.WifiOnlyUpload, value);
        }

        public int ReportHandlerCallbackTimeout
        {
            set => Set(Options.ReportHandlerCallbackTimeout, value);
        }

        // --- Detect ---

        public bool DetectAndReportHang
        {
            set => Set(Options.DetectAndReportHang, value);
        }

        public int DetectAndReportHangFairLevel
        {
            set => Set(Options.DetectAndReportHangFairLevel, value);
        }

        public int DetectAndReportHangMediumLevel
        {
            set => Set(Options.DetectAndReportHangMediumLevel, value);
        }

        public int DetectAndReportHangSevereLevel
        {
            set => Set(Options.DetectAndReportHangSevereLevel, value);
        }

        public bool DetectAndReportCrash
        {
            set => Set(Options.DetectAndReportCrash, value);
        }

        public bool DetectAndReportEarlyCrash
        {
            set => Set(Options.DetectAndReportEarlyCrash, value);
        }

        public bool DetectAndReportHttpErrors
        {
            set => Set(Options.DetectAndReportHttpErrors, value);
        }

        public bool DetectAndReportAnomaly
        {
            set => Set(Options.DetectAndReportAnomaly, value);
        }

        public bool DetectFrustration
        {
            set => Set(Options.DetectFrustration, value);
        }

        // --- Capture: logs / screenshot / network / breadcrumbs / video (shared) ---

        public bool CaptureLogs
        {
            set => Set(Options.CaptureLogs, value);
        }

        public LogLevel CaptureLogsLevel
        {
            set => Set(Options.CaptureLogsLevel, (int)value);
        }

        public bool CaptureScreenshot
        {
            set => Set(Options.CaptureScreenshot, value);
        }

        public float CaptureScreenshotScale
        {
            set => Set(Options.CaptureScreenshotScale, value);
        }

        public bool CaptureNetwork
        {
            set => Set(Options.CaptureNetwork, value);
        }

        public int CaptureNetworkBodySizeLimit
        {
            set => Set(Options.CaptureNetworkBodySizeLimit, value);
        }

        public bool CaptureNetworkBodyWithoutType
        {
            set => Set(Options.CaptureNetworkBodyWithoutType, value);
        }

        public bool CaptureNetworkUseDefaultSanitizer
        {
            set => Set(Options.CaptureNetworkUseDefaultSanitizer, value);
        }

        public bool CaptureViewHierarchy
        {
            set => Set(Options.CaptureViewHierarchy, value);
        }

        public bool CaptureBreadcrumbs
        {
            set => Set(Options.CaptureBreadcrumbs, value);
        }

        public bool CaptureBreadcrumbsExtras
        {
            set => Set(Options.CaptureBreadcrumbsExtras, value);
        }

        public bool CaptureVideo
        {
            set => Set(Options.CaptureVideo, value);
        }

        public VideoQuality CaptureVideoQuality
        {
            set => Set(Options.CaptureVideoQuality, (int)value);
        }

        public FrameRate CaptureVideoFrameRate
        {
            set => Set(Options.CaptureVideoFrameRate, (int)value);
        }

        public float CaptureVideoScale
        {
            set => Set(Options.CaptureVideoScale, value);
        }

        // --- Reporting (shared) ---

        public bool ReportingTriggerByShake
        {
            set => Set(Options.ReportingTriggerByShake, value);
        }

        public bool ReportingTriggerByScreenshot
        {
            set => Set(Options.ReportingTriggerByScreenshot, value);
        }

        public IssueSeverity ReportingDefaultCrashPriority
        {
            set => Set(Options.ReportingDefaultCrashPriority, (int)value);
        }

        public IssueSeverity ReportingDefaultErrorPriority
        {
            set => Set(Options.ReportingDefaultErrorPriority, (int)value);
        }

        public IssueSeverity ReportingDefaultBugPriority
        {
            set => Set(Options.ReportingDefaultBugPriority, (int)value);
        }

        public bool ReportingUISummaryRequired
        {
            set => Set(Options.ReportingUISummaryRequired, value);
        }

        public bool ReportingUIDescriptionRequired
        {
            set => Set(Options.ReportingUIDescriptionRequired, value);
        }

        public bool ReportingUIEmailRequired
        {
            set => Set(Options.ReportingUIEmailRequired, value);
        }

        public bool ReportingUILabelsEnabled
        {
            set => Set(Options.ReportingUILabelsEnabled, value);
        }

        public bool ReportingUILabelsRequired
        {
            set => Set(Options.ReportingUILabelsRequired, value);
        }

        public bool ReportingUIPrioritySelectorEnabled
        {
            set => Set(Options.ReportingUIPrioritySelectorEnabled, value);
        }

        // --- Performance ---

        public bool PerformanceMonitoring
        {
            set => Set(Options.PerformanceMonitoring, value);
        }

        public float PerformanceSampleRate
        {
            set => Set(Options.PerformanceSampleRate, value);
        }

        public bool PerformanceAdaptiveSampling
        {
            set => Set(Options.PerformanceAdaptiveSampling, value);
        }

        public string PerformanceUploadMode
        {
            set => Set(Options.PerformanceUploadMode, value);
        }

        // --- Advanced ---

        public bool Debug
        {
            set => Set(Options.Debug, value);
        }

        public string Endpoint
        {
            set => Set(Options.Endpoint, value);
        }
    }
}
