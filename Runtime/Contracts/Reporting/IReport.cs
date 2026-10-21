using System.Collections.Generic;
using Bugsee.Contracts.Options;

namespace Bugsee.Contracts.Reporting
{
    /// <summary>
    /// Subset of com.bugsee.library.contracts.reporting.Report that is useful from C#.
    /// Both native SDKs have per-display screenshot get/set/changed members and exportVideo on their report objects;
    /// this wrapper does not expose them yet (reports cross the bridge as managed snapshots, and only Android's report
    /// handler path holds a live native report). Tracked in bugsee-unity#224. To keep the SDK's screenshot out of
    /// reports use the CaptureScreenshot launch option, secure rectangles or blackout; attachments cannot remove,
    /// replace or redact it.
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
    }
}
