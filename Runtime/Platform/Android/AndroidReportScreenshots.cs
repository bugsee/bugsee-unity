#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Threading;
using Bugsee.WrapperPolicy;
using UnityEngine;

namespace Bugsee.Platform.Android
{
    /// <summary>
    /// Per-display screenshots of the live Java <c>Report</c> a report handler holds. Reads wait (bounded) for the
    /// SDK's asynchronous <c>getScreenshot</c> and encode the Bitmap to PNG; sets decode the PNG to a Bitmap. See
    /// <see cref="ReportScreenshotPolicy"/> for the limits.
    /// </summary>
    static class AndroidReportScreenshots
    {
        const int ReadTimeoutMs = 5000;
        static readonly UnsupportedApiNotice Notice = new UnsupportedApiNotice(Debug.LogWarning);

        /// <summary>Receives <c>Callback1&lt;Bitmap&gt;.run</c> (on whichever thread the SDK uses) and encodes the bitmap there.</summary>
        sealed class PngCallback : AndroidJavaProxy
        {
            readonly ManualResetEventSlim _done = new ManualResetEventSlim(false);
            public byte[] Png;

            public PngCallback() : base("com.bugsee.library.contracts.common.Callback1") { }

            public void run(AndroidJavaObject bitmap)
            {
                try
                {
                    if (bitmap != null)
                    {
                        using (bitmap)
                            Png = Encode(bitmap);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[Bugsee] Encoding the report screenshot failed: " + ex.Message);
                }
                finally
                {
                    _done.Set();
                }
            }

            public bool Wait() => _done.Wait(ReadTimeoutMs);
        }

        static byte[] Encode(AndroidJavaObject bitmap)
        {
            using (var formatClass = new AndroidJavaClass("android.graphics.Bitmap$CompressFormat"))
            using (var png = formatClass.GetStatic<AndroidJavaObject>("PNG"))
            using (var stream = new AndroidJavaObject("java.io.ByteArrayOutputStream"))
            {
                if (!bitmap.Call<bool>("compress", png, 100, stream))
                    return null;
                var signed = stream.Call<sbyte[]>("toByteArray");
                if (signed == null || signed.Length == 0) return null;
                var bytes = new byte[signed.Length];
                Buffer.BlockCopy(signed, 0, bytes, 0, bytes.Length);
                return bytes;
            }
        }

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
            var callback = new PngCallback();
            report.Call("getScreenshot", displayId, callback);
            if (!callback.Wait())
            {
                Debug.LogWarning("[Bugsee] GetScreenshotPng returned null: the SDK did not deliver the screenshot within "
                                 + (ReadTimeoutMs / 1000) + " s.");
                return null;
            }
            var png = callback.Png;
            if (png != null && png.Length > ReportScreenshotPolicy.MaxPngBytes)
            {
                Debug.LogWarning(ReportScreenshotPolicy.TooLargeToReadMessage(displayId, png.Length));
                return null;
            }
            return png;
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

            if (png == null)
                return CallSetScreenshot(report, displayId, IntPtr.Zero);

            var signed = AndroidJavaConverters.ToSBytes(png);
            using (var factory = new AndroidJavaClass("android.graphics.BitmapFactory"))
            using (var bitmap = factory.CallStatic<AndroidJavaObject>("decodeByteArray", signed, 0, signed.Length))
            {
                if (bitmap == null)
                {
                    Debug.LogWarning("[Bugsee] SetScreenshotPng ignored: the system could not decode the PNG.");
                    return false;
                }
                return CallSetScreenshot(report, displayId, bitmap.GetRawObject());
            }
        }

        // Report.setScreenshot(int, Bitmap) through raw JNI: Unity cannot type a null argument for the Bitmap overload
        // (AndroidJavaObject.Call sends a null as Object, which names no method), and a null is how a screenshot is removed.
        static bool CallSetScreenshot(AndroidJavaObject report, int displayId, IntPtr bitmap)
        {
            var clazz = report.GetRawClass();
            var method = AndroidJNIHelper.GetMethodID(clazz, "setScreenshot", "(ILandroid/graphics/Bitmap;)V", false);
            AndroidJNI.CallVoidMethod(report.GetRawObject(), method, new[] { new jvalue { i = displayId }, new jvalue { l = bitmap } });
            var exception = AndroidJNI.ExceptionOccurred();
            if (exception == IntPtr.Zero) return true;
            AndroidJNI.ExceptionClear();
            AndroidJNI.DeleteLocalRef(exception);
            Debug.LogWarning("[Bugsee] SetScreenshotPng failed: Report.setScreenshot threw.");
            return false;
        }

        internal static bool IsChanged(AndroidJavaObject report, ReportEditGate gate) =>
            (gate == null || gate.IsOpen) && report.Call<bool>("isScreenshotChanged");

        internal static void SnapshotNotice() => Notice.Once(ReportScreenshotPolicy.SnapshotReportMessage);
    }
}
#endif
