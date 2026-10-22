#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using Bugsee.WrapperPolicy;
using UnityEngine;

namespace Bugsee.Platform.Android
{
    /// <summary>
    /// Per-display screenshots of the live Java <c>Report</c> a report handler holds. The PNG encode, decode and the
    /// blocking read are done by <c>UnityWrapper.getReportScreenshotPng</c> / <c>setReportScreenshotPng</c>
    /// (Plugins/Android/UnityWrapperProvider.java); this class applies the managed side of
    /// <see cref="ReportScreenshotPolicy"/>.
    /// </summary>
    static class AndroidReportScreenshots
    {
        static readonly UnsupportedApiNotice Notice = new UnsupportedApiNotice(Debug.LogWarning);

        internal static IReadOnlyList<int> DisplayIds(AndroidJavaObject report, ReportEditGate gate)
        {
            var ids = new List<int>();
            if (gate != null && !gate.IsOpen) return ids;
            using (var list = report.Call<AndroidJavaObject>("getScreenshotDisplayIds"))
            {
                if (list == null) return ids;
                var size = list.Call<int>("size");
                for (var i = 0; i < size; i++)
                {
                    using (var boxed = list.Call<AndroidJavaObject>("get", i))
                        ids.Add(boxed.Call<int>("intValue"));
                }
            }
            ids.Sort();
            return ids;
        }

        internal static byte[] GetPng(AndroidJavaObject report, ReportEditGate gate, int displayId)
        {
            if (gate != null && !gate.IsOpen) return null;
            using (var wrapper = new AndroidJavaClass("com.bugsee.unity.UnityWrapper"))
            {
                var signed = wrapper.CallStatic<sbyte[]>("getReportScreenshotPng", report, displayId);
                if (signed == null || signed.Length == 0) return null;
                if (signed.Length > ReportScreenshotPolicy.MaxPngBytes)
                {
                    Debug.LogWarning(ReportScreenshotPolicy.TooLargeToReadMessage(displayId, signed.Length));
                    return null;
                }
                var bytes = new byte[signed.Length];
                Buffer.BlockCopy(signed, 0, bytes, 0, bytes.Length);
                return bytes;
            }
        }

        internal static bool SetPng(AndroidJavaObject report, ReportEditGate gate, int displayId, byte[] png)
        {
            if (gate != null && !gate.IsOpen)
            {
                Notice.Once(ReportScreenshotPolicy.ClosedMessage);
                return false;
            }
            var reason = ReportScreenshotPolicy.RejectReason(png);
            if (reason != null)
            {
                Debug.LogWarning(reason);
                return false;
            }

            using (var wrapper = new AndroidJavaClass("com.bugsee.unity.UnityWrapper"))
            {
                var applied = wrapper.CallStatic<int>("setReportScreenshotPng", report, displayId, AndroidJavaConverters.ToSBytes(png));
                if (applied == 1) return true;
            }
            Debug.LogWarning("[Bugsee] SetScreenshotPng ignored: the system could not decode the PNG.");
            return false;
        }

        internal static bool IsChanged(AndroidJavaObject report, ReportEditGate gate) =>
            (gate == null || gate.IsOpen) && report.Call<bool>("isScreenshotChanged");

        internal static void SnapshotNotice() => Notice.Once(ReportScreenshotPolicy.SnapshotReportMessage);
    }
}
#endif
