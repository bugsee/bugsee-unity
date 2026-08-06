#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using Bugsee.Contracts.Options;
using UnityEngine;

namespace Bugsee.Platform.Android
{
    static class AndroidOptionsMapper
    {
        const string VideoModeClass = "com.bugsee.library.contracts.options.VideoMode";
        const string VideoQualityClass = "com.bugsee.library.contracts.options.VideoQuality";
        const string FrameRateClass = "com.bugsee.library.contracts.options.FrameRate";
        const string LogLevelClass = "com.bugsee.library.contracts.options.LogLevel";
        const string IssueSeverityClass = "com.bugsee.library.contracts.options.IssueSeverity";

        public static AndroidJavaObject ToJavaMap(IDictionary<string, object> options)
        {
            var map = new AndroidJavaObject("java.util.HashMap");
            if (options == null) return map;

            foreach (var kv in options)
            {
                if (string.IsNullOrEmpty(kv.Key) || kv.Value == null) continue;
                var javaValue = ToJavaValue(kv.Key, kv.Value);
                if (javaValue == null) continue;
                map.Call<AndroidJavaObject>("put", kv.Key, javaValue);
            }

            return map;
        }

        static AndroidJavaObject ToJavaValue(string key, object value)
        {
            // Enum options: coerce int/enum → SDK enum instance (Serializable).
            if (key == Options.CaptureVideoMode)
                return EnumFromInt(VideoModeClass, "fromIntValue", CoerceInt(value), (int)VideoMode.V2);
            if (key == Options.CaptureVideoQuality)
                return EnumFromInt(VideoQualityClass, "fromIntValue", CoerceInt(value), (int)VideoQuality.Default);
            if (key == Options.CaptureVideoFrameRate)
                return EnumFromInt(FrameRateClass, "fromIntValue", CoerceInt(value), (int)FrameRate.High);
            if (key == Options.CaptureLogsLevel)
                return LogLevelFromRaw(CoerceInt(value));
            if (key == Options.ReportingDefaultCrashPriority ||
                key == Options.ReportingDefaultErrorPriority ||
                key == Options.ReportingDefaultBugPriority)
                return EnumFromInt(IssueSeverityClass, "fromIntValue", CoerceInt(value), (int)IssueSeverity.High);

            if (value is bool b) return new AndroidJavaObject("java.lang.Boolean", b);
            if (value is int i) return new AndroidJavaObject("java.lang.Integer", i);
            if (value is long l) return new AndroidJavaObject("java.lang.Long", l);
            if (value is float f) return new AndroidJavaObject("java.lang.Float", f);
            if (value is double d) return new AndroidJavaObject("java.lang.Double", d);
            if (value is string s) return new AndroidJavaObject("java.lang.String", s);
            if (value is VideoMode vm) return EnumFromInt(VideoModeClass, "fromIntValue", (int)vm, (int)VideoMode.V2);
            if (value is VideoQuality vq) return EnumFromInt(VideoQualityClass, "fromIntValue", (int)vq, (int)VideoQuality.Default);
            if (value is FrameRate fr) return EnumFromInt(FrameRateClass, "fromIntValue", (int)fr, (int)FrameRate.High);
            if (value is LogLevel ll) return LogLevelFromRaw((int)ll);
            if (value is IssueSeverity sev) return EnumFromInt(IssueSeverityClass, "fromIntValue", (int)sev, (int)IssueSeverity.High);

            Debug.LogWarning($"[Bugsee] Unsupported option value type for '{key}': {value.GetType().FullName}");
            return null;
        }

        static int CoerceInt(object value)
        {
            if (value is int i) return i;
            if (value is byte by) return by;
            if (value is long l) return (int)l;
            if (value is Enum e) return Convert.ToInt32(e);
            return Convert.ToInt32(value);
        }

        static AndroidJavaObject EnumFromInt(string className, string method, int value, int defaultValue)
        {
            using (var clazz = new AndroidJavaClass(className))
            using (var def = clazz.CallStatic<AndroidJavaObject>(method, defaultValue))
            {
                return clazz.CallStatic<AndroidJavaObject>(method, value, def);
            }
        }

        static AndroidJavaObject LogLevelFromRaw(int value)
        {
            using (var clazz = new AndroidJavaClass(LogLevelClass))
            using (var def = clazz.CallStatic<AndroidJavaObject>("fromRawValue", (byte)LogLevel.Info))
            {
                return clazz.CallStatic<AndroidJavaObject>("fromRawValue", (byte)value, def);
            }
        }
    }
}
#endif
