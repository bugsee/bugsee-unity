using System;
using System.Collections.Generic;

namespace Bugsee.Contracts.Options
{
    /// <summary>
    /// Fluent builder for launch options. Values are stored under Android <see cref="Options"/> keys.
    /// Enum options are stored as their wire integer/byte values; the Android bridge coerces to SDK enums.
    /// </summary>
    public sealed class OptionsBuilder
    {
        readonly Dictionary<string, object> _map = new Dictionary<string, object>();

        public OptionsBuilder Set(string key, object value)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("key");
            if (value == null) _map.Remove(key);
            else _map[key] = value;
            return this;
        }

        public OptionsBuilder Set(string key, bool value) => Set(key, (object)value);
        public OptionsBuilder Set(string key, int value) => Set(key, (object)value);
        public OptionsBuilder Set(string key, float value) => Set(key, (object)value);
        public OptionsBuilder Set(string key, string value) => Set(key, (object)value);

        public OptionsBuilder SetVideoMode(VideoMode mode) => Set(Options.CaptureVideoMode, (int)mode);
        public OptionsBuilder SetVideoQuality(VideoQuality quality) => Set(Options.CaptureVideoQuality, (int)quality);
        public OptionsBuilder SetFrameRate(FrameRate rate) => Set(Options.CaptureVideoFrameRate, (int)rate);
        public OptionsBuilder SetLogLevel(LogLevel level) => Set(Options.CaptureLogsLevel, (int)level);
        public OptionsBuilder SetDefaultCrashPriority(IssueSeverity s) => Set(Options.ReportingDefaultCrashPriority, (int)s);
        public OptionsBuilder SetDefaultErrorPriority(IssueSeverity s) => Set(Options.ReportingDefaultErrorPriority, (int)s);
        public OptionsBuilder SetDefaultBugPriority(IssueSeverity s) => Set(Options.ReportingDefaultBugPriority, (int)s);

        public OptionsBuilder Duration(int seconds) => Set(Options.Duration, seconds);
        public OptionsBuilder CaptureVideo(bool enabled) => Set(Options.CaptureVideo, enabled);
        public OptionsBuilder CaptureNetwork(bool enabled) => Set(Options.CaptureNetwork, enabled);
        public OptionsBuilder CaptureLogs(bool enabled) => Set(Options.CaptureLogs, enabled);
        public OptionsBuilder DetectAndReportCrash(bool enabled) => Set(Options.DetectAndReportCrash, enabled);
        public OptionsBuilder Debug(bool enabled) => Set(Options.Debug, enabled);
        public OptionsBuilder Endpoint(string url) => Set(Options.Endpoint, url);

        public IReadOnlyDictionary<string, object> Build() =>
            new Dictionary<string, object>(_map);

        public Dictionary<string, object> ToDictionary() => new Dictionary<string, object>(_map);
    }
}
