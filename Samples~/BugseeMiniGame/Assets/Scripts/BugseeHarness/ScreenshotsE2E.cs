using System;
using System.Collections;
using System.Collections.Generic;
using Bugsee.Contracts.Reporting;
using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>
    /// Scenario "screenshots" (issue #224): a report handler installed with SetReportHandler lists the report's
    /// screenshot display ids, reads one (PNG signature), replaces it with a generated PNG and reads it back (size from
    /// the PNG header), refuses data that is not a PNG, removes it, and checks IsScreenshotChanged along the way. It
    /// ends by putting the generated PNG back, so the uploaded report carries a screenshot you can recognise (a 16x16
    /// magenta square). A report from CreateReport (a managed snapshot) must list no screenshots and refuse a set.
    /// Logs <c>[Sample] screenshots PASS|FAIL name :: detail</c> and a final <c>screenshots e2e done failures=N</c>.
    /// </summary>
    static class ScreenshotsE2E
    {
        const int Side = 16;
        static int _failures;
        static bool _handlerRan;

        public static IEnumerator Run(MonoBehaviour host, string marker)
        {
            _failures = 0;
            _handlerRan = false;
            Debug.Log("[Sample] screenshots e2e start launched=" + Bugsee.IsLaunched);
            var waited = 0f;
            while (!Bugsee.IsLaunched && waited < 60f)
            {
                waited += 0.5f;
                yield return new WaitForSeconds(0.5f);
            }

            // The managed-snapshot report: nothing captured, a set refused, no exception.
            try
            {
                var snapshot = Bugsee.CreateReport();
                Check("snapshot report lists no screenshots", snapshot.ScreenshotDisplayIds.Count == 0, "count=" + snapshot.ScreenshotDisplayIds.Count);
                Check("snapshot report reads null", snapshot.GetScreenshotPng(0) == null, "");
                Check("snapshot report refuses a set", !snapshot.SetScreenshotPng(0, GeneratedPng()), "");
                Check("snapshot report is not changed", !snapshot.IsScreenshotChanged, "");
                Bugsee.Discard(snapshot);
            }
            catch (Exception ex) { Check("snapshot report checks ran", false, ex.GetType().Name + ": " + ex.Message); }

            Bugsee.SetReportHandler(new Probe());
            yield return new WaitForSeconds(1f);
            Bugsee.Upload("screenshots-" + marker, "report screenshot API");

            waited = 0f;
            while (!_handlerRan && waited < 45f)
            {
                waited += 0.5f;
                yield return new WaitForSeconds(0.5f);
            }
            Check("the report handler ran", _handlerRan, "waited=" + waited.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
            Bugsee.SetReportHandler(null);
            Debug.Log("[Sample] screenshots e2e done failures=" + _failures);
        }

        static byte[] GeneratedPng()
        {
            var texture = new Texture2D(Side, Side, TextureFormat.RGBA32, false);
            var pixels = new Color32[Side * Side];
            for (var i = 0; i < pixels.Length; i++) pixels[i] = new Color32(255, 0, 255, 255);
            texture.SetPixels32(pixels);
            texture.Apply();
            var png = texture.EncodeToPNG();
            UnityEngine.Object.Destroy(texture);
            return png;
        }

        static bool HasPngSignature(byte[] data) =>
            data != null && data.Length > 24 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47
            && data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A;

        // Width and height from the IHDR chunk (big-endian, bytes 16..23).
        static string Dimensions(byte[] png) =>
            !HasPngSignature(png) ? "n/a"
                : (((png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19]) + "x" + ((png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23]));

        static string Join(IReadOnlyList<int> ids)
        {
            var parts = new List<string>();
            foreach (var id in ids) parts.Add(id.ToString());
            return "[" + string.Join(",", parts.ToArray()) + "]";
        }

        static void Check(string name, bool ok, string detail)
        {
            if (!ok) _failures++;
            Debug.Log("[Sample] screenshots " + (ok ? "PASS " : "FAIL ") + name + " :: " + detail);
        }

        sealed class Probe : ReportHandlerBase
        {
            public override void OnBeforeReportCreated(IReport report, bool isTerminating, Action completionCallback)
            {
                try { Body(report); }
                catch (Exception ex) { Check("handler checks ran without an exception", false, ex.GetType().Name + ": " + ex.Message); }
                _handlerRan = true;
                completionCallback?.Invoke();
            }

            static void Body(IReport report)
            {
                var ids = report.ScreenshotDisplayIds;
                Check("the report lists a screenshot display", ids.Count > 0, "ids=" + Join(ids));
                Check("IsScreenshotChanged starts false", !report.IsScreenshotChanged, "");
                var display = ids.Count > 0 ? ids[0] : 0;

                var original = report.GetScreenshotPng(display);
                Check("the captured screenshot reads as a PNG", HasPngSignature(original),
                    "bytes=" + (original == null ? "null" : original.Length.ToString()) + " size=" + Dimensions(original));
                Check("reading does not mark the report changed", !report.IsScreenshotChanged, "");
                Check("a display without a screenshot reads null", report.GetScreenshotPng(99) == null, "");

                Check("a non-PNG is refused", !report.SetScreenshotPng(display, new byte[] { 1, 2, 3, 4 }), "");
                Check("a refused set changes nothing", !report.IsScreenshotChanged, "");

                var generated = GeneratedPng();
                Check("a generated PNG is accepted", report.SetScreenshotPng(display, generated), "bytes=" + generated.Length);
                Check("IsScreenshotChanged is true after a set", report.IsScreenshotChanged, "");
                var back = report.GetScreenshotPng(display);
                Check("the replacement reads back with the generated size", HasPngSignature(back) && Dimensions(back) == Side + "x" + Side,
                    "size=" + Dimensions(back));

                Check("null removes the screenshot", report.SetScreenshotPng(display, null), "");
                Check("the removed display is no longer listed", !new List<int>(report.ScreenshotDisplayIds).Contains(display),
                    "ids=" + Join(report.ScreenshotDisplayIds));
                Check("the removed display reads null", report.GetScreenshotPng(display) == null, "");
                Check("IsScreenshotChanged stays true", report.IsScreenshotChanged, "");

                Check("the generated PNG goes back for the upload", report.SetScreenshotPng(display, generated), "");
            }
        }
    }
}
