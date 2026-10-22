#if UNITY_IOS && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Bugsee.WrapperPolicy;
using UnityEngine;

namespace Bugsee.Platform.IOS
{
    /// <summary>
    /// Per-display screenshots of the live report a report handler holds (native id: the handler request id).
    /// Every call is answered by the native report while the handler's request is pending; once the handler has
    /// answered, native refuses (-1) and these report an empty result. See <see cref="ReportScreenshotPolicy"/>.
    /// </summary>
    static class IosReportScreenshots
    {
        static readonly UnsupportedApiNotice Notice = new UnsupportedApiNotice(Debug.LogWarning);

        internal static IReadOnlyList<int> DisplayIds(long requestId)
        {
            if (requestId == 0) return new int[0];
            var ptr = _bugsee_report_screenshot_ids(requestId);
            if (ptr == IntPtr.Zero) return new int[0];
            try { return ReportScreenshotPolicy.ParseDisplayIds(Marshal.PtrToStringAnsi(ptr)); }
            finally { _bugsee_free(ptr); }
        }

        internal static byte[] GetPng(long requestId, int displayId)
        {
            if (requestId == 0) return null;
            var length = _bugsee_report_screenshot_png(requestId, displayId, out var bytes);
            if (length == -2)
            {
                Debug.LogWarning("[Bugsee] GetScreenshotPng(" + displayId + ") returned null: the display holds a screenshot the system could not encode as PNG. "
                                 + "It is still listed in ScreenshotDisplayIds; do not assume it is absent.");
                return null;
            }
            if (bytes == IntPtr.Zero) return null;
            try
            {
                if (length <= 0) return null;
                if (length > ReportScreenshotPolicy.MaxPngBytes)
                {
                    Debug.LogWarning(ReportScreenshotPolicy.TooLargeToReadMessage(displayId, length));
                    return null;
                }
                var copy = new byte[length];
                Marshal.Copy(bytes, copy, 0, length);
                return copy;
            }
            finally { _bugsee_free(bytes); }
        }

        internal static bool SetPng(long requestId, int displayId, byte[] png)
        {
            if (requestId == 0)
            {
                Notice.Once(ReportScreenshotPolicy.SnapshotReportMessage);
                return false;
            }
            var reason = ReportScreenshotPolicy.RejectReason(png);
            if (reason != null)
            {
                Debug.LogWarning(reason);
                return false;
            }
            var result = _bugsee_report_set_screenshot(requestId, displayId, png, png == null ? 0 : png.Length);
            if (result == 1) return true;
            if (result == 0)
                Debug.LogWarning("[Bugsee] SetScreenshotPng ignored: the system could not decode the PNG.");
            else
                Notice.Once(ReportScreenshotPolicy.ClosedMessage);
            return false;
        }

        internal static bool IsChanged(long requestId) =>
            requestId != 0 && _bugsee_report_screenshot_changed(requestId) == 1;

        [DllImport("__Internal")] static extern IntPtr _bugsee_report_screenshot_ids(long requestId);
        [DllImport("__Internal")] static extern int _bugsee_report_screenshot_png(long requestId, int displayId, out IntPtr bytes);
        [DllImport("__Internal")] static extern int _bugsee_report_set_screenshot(long requestId, int displayId, byte[] bytes, int length);
        [DllImport("__Internal")] static extern int _bugsee_report_screenshot_changed(long requestId);
        [DllImport("__Internal")] static extern void _bugsee_free(IntPtr ptr);
    }
}
#endif
