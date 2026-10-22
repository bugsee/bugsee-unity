using System;
using System.Collections.Generic;
using System.Globalization;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Rules for the per-display report screenshots (<c>IReport.GetScreenshotPng</c> / <c>SetScreenshotPng</c>): the PNG
    /// bytes that may cross the bridge, and what is said when a report cannot hold screenshots.
    /// </summary>
    public static class ReportScreenshotPolicy
    {
        /// <summary>The largest PNG the wrapper moves across the bridge in either direction (32 MiB). A capture is
        /// about 2 MB; anything near this is a mistake, and a copy that size on a handler's turn is not free.</summary>
        public const int MaxPngBytes = 32 * 1024 * 1024;

        /// <summary>Said once for a report that is a managed snapshot (<c>Bugsee.CreateReport</c>): it has no native
        /// report, hence no captured screenshots, and a screenshot set on it would never reach the upload.</summary>
        public const string SnapshotReportMessage =
            "[Bugsee] Report screenshots are available only on the report a report handler receives "
            + "(Bugsee.SetReportHandler): a report from Bugsee.CreateReport is a managed snapshot with no captured screenshot, "
            + "and a screenshot set on it is not kept.";

        /// <summary>Said once when a screenshot call arrives after the handler's turn ended (answered, skipped or abandoned).</summary>
        public const string ClosedMessage =
            "[Bugsee] Screenshot call ignored: the report handler's turn is over (it was answered, skipped or abandoned), "
            + "so the report can no longer change.";

        /// <summary>The longest side (pixels) of a PNG that is set: a small file can declare a huge canvas, and the platform
        /// allocates the decoded bitmap (4 bytes a pixel) before anything else can object.</summary>
        public const int MaxSidePixels = 8192;

        /// <summary>The most pixels (width x height) of a PNG that is set (32 Mi, a 128 MiB bitmap).</summary>
        public const long MaxPixels = 32L * 1024 * 1024;

        static readonly byte[] PngMagic = { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A };

        /// <summary>True when <paramref name="data"/> starts with the PNG signature.</summary>
        public static bool IsPng(byte[] data)
        {
            if (data == null || data.Length < PngMagic.Length) return false;
            for (var i = 0; i < PngMagic.Length; i++)
                if (data[i] != PngMagic[i]) return false;
            return true;
        }

        /// <summary>Why <paramref name="png"/> cannot be set as a screenshot, or null when it can. Null data is not
        /// checked here: it means "remove".</summary>
        public static string RejectReason(byte[] png)
        {
            if (png == null) return null;
            if (png.Length > MaxPngBytes)
                return "[Bugsee] SetScreenshotPng ignored: the PNG is " + png.Length.ToString(CultureInfo.InvariantCulture)
                       + " bytes, over the " + MaxPngBytes.ToString(CultureInfo.InvariantCulture) + " byte limit.";
            if (!IsPng(png))
                return "[Bugsee] SetScreenshotPng ignored: the data is not a PNG (no PNG signature). Pass PNG bytes, or null to remove the screenshot.";
            if (!TryReadSize(png, out var width, out var height) || width == 0 || height == 0)
                return "[Bugsee] SetScreenshotPng ignored: the PNG has no readable image header or a zero width or height.";
            if (width > MaxSidePixels || height > MaxSidePixels || (long)width * height > MaxPixels)
                return "[Bugsee] SetScreenshotPng ignored: the PNG is " + width.ToString(CultureInfo.InvariantCulture) + "x"
                       + height.ToString(CultureInfo.InvariantCulture) + " pixels, over the limit of "
                       + MaxSidePixels.ToString(CultureInfo.InvariantCulture) + " per side and "
                       + MaxPixels.ToString(CultureInfo.InvariantCulture) + " in total.";
            return null;
        }

        /// <summary>Width and height from the IHDR chunk (bytes 8..23: length, "IHDR", then two big-endian 32-bit
        /// values); false when the data is too short or the first chunk is not IHDR. A value above int.MaxValue is
        /// returned as int.MaxValue.</summary>
        public static bool TryReadSize(byte[] png, out int width, out int height)
        {
            width = height = 0;
            if (!IsPng(png) || png.Length < 24) return false;
            if (png[12] != (byte)'I' || png[13] != (byte)'H' || png[14] != (byte)'D' || png[15] != (byte)'R') return false;
            width = BigEndian(png, 16);
            height = BigEndian(png, 20);
            return true;
        }

        static int BigEndian(byte[] d, int at)
        {
            var v = ((long)d[at] << 24) | ((long)d[at + 1] << 16) | ((long)d[at + 2] << 8) | d[at + 3];
            return v > int.MaxValue ? int.MaxValue : (int)v;
        }

        /// <summary>The message for a native PNG that is over the limit (it is not returned).</summary>
        public static string TooLargeToReadMessage(int displayId, long length) =>
            "[Bugsee] GetScreenshotPng(" + displayId.ToString(CultureInfo.InvariantCulture) + ") returned null: the screenshot is "
            + length.ToString(CultureInfo.InvariantCulture) + " bytes as PNG, over the "
            + MaxPngBytes.ToString(CultureInfo.InvariantCulture) + " byte limit.";

        /// <summary>The display ids of a JSON array of integers ("[0,1]"), ascending and distinct; empty for null,
        /// empty or malformed text (a non-integer element is skipped).</summary>
        public static IReadOnlyList<int> ParseDisplayIds(string json)
        {
            var ids = new List<int>();
            if (string.IsNullOrWhiteSpace(json)) return ids;
            var text = json.Trim();
            if (text.Length < 2 || text[0] != '[' || text[text.Length - 1] != ']') return ids;
            foreach (var part in text.Substring(1, text.Length - 2).Split(','))
            {
                if (int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && !ids.Contains(id))
                    ids.Add(id);
            }
            ids.Sort();
            return ids;
        }
    }
}
