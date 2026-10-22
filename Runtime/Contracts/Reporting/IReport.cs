using System.Collections.Generic;
using Bugsee.Contracts.Options;

namespace Bugsee.Contracts.Reporting
{
    /// <summary>
    /// Subset of com.bugsee.library.contracts.reporting.Report that is useful from C#. <c>exportVideo</c> is not exposed.
    ///
    /// Screenshots (<see cref="ScreenshotDisplayIds"/>, <see cref="GetScreenshotPng"/>, <see cref="SetScreenshotPng"/>,
    /// <see cref="IsScreenshotChanged"/>) work on the report a report handler receives
    /// (<c>Bugsee.SetReportHandler</c>, both phases), on Android and iOS, on Unity's main thread, inside the handler's
    /// turn. They are not available on a report from <c>Bugsee.CreateReport</c> (a managed snapshot with no captured
    /// screenshot): the getters return nothing and a set is refused with a one-time warning. Tracked in bugsee-unity#224.
    ///
    /// PRIVACY: a screenshot is the app's screen as captured, and can show anything on it, including content you
    /// marked secure (secure rectangles are drawn over the capture, not guaranteed to be absent from a bitmap you
    /// replace or re-encode) and personal data. Treat the bytes as sensitive: do not log them, write them to shared
    /// storage or send them anywhere the report itself does not go. A screenshot you set replaces the captured one in
    /// the report and its upload; nothing is redacted for you. To omit the SDK's screenshot altogether use the
    /// CaptureScreenshot launch option; attachments cannot remove, replace or redact it.
    /// </summary>
    public interface IReport
    {
        string Id { get; }
        IssueType Type { get; }
        string Summary { get; set; }
        string Description { get; set; }
        string Email { get; set; }
        IssueSeverity? Severity { get; set; }

        IReadOnlyDictionary<string, object> Attributes { get; }
        object GetAttribute(string name);
        void SetAttribute(string name, object value);
        void RemoveAttribute(string name);
        void ClearAllAttributes();

        IReadOnlyList<string> Labels { get; }
        void AddLabel(string label);
        void ClearLabels();
        void SetLabels(IEnumerable<string> labels);

        IReadOnlyList<IAttachment> Attachments { get; }
        IAttachment AddAttachmentFile(string path, string name, string mimeType);
        IAttachment AddAttachmentBytes(byte[] data, string name, string mimeType);
        void ClearAttachments();

        /// <summary>The displays that hold a screenshot, ascending (usually just 0). Empty when there is none, the report
        /// is not a report handler's, or the handler's turn is over.</summary>
        IReadOnlyList<int> ScreenshotDisplayIds { get; }

        /// <summary>The display's screenshot as PNG bytes, or null when the display has none, the report is not a report
        /// handler's, the handler's turn is over, or the PNG is over <c>ReportScreenshotPolicy.MaxPngBytes</c> (32 MiB;
        /// a warning says so). Blocks the calling thread (Unity's main thread) while the SDK produces it. The returned
        /// array is a copy you own; see the privacy note on <see cref="IReport"/>.</summary>
        byte[] GetScreenshotPng(int displayId);

        /// <summary>Replaces the display's screenshot with <paramref name="png"/>, or removes it when null. Returns true
        /// when the report took the change; false (with a warning, never silently) when the data is not a PNG, is over
        /// 32 MiB, cannot be decoded by the platform, or the report cannot hold screenshots. Replacing or removing an
        /// existing screenshot sets <see cref="IsScreenshotChanged"/>. The pixels you pass reach the backend with the report.</summary>
        bool SetScreenshotPng(int displayId, byte[] png);

        /// <summary>True once a screenshot the report holds was replaced or removed (by this handler or an earlier edit).
        /// Adding one to a report that had none does not count on Android.</summary>
        bool IsScreenshotChanged { get; }
    }
}
