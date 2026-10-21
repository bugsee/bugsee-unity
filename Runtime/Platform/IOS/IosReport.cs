#if UNITY_IOS && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Bugsee.Contracts.Options;
using Bugsee.Contracts.Reporting;
using Bugsee.WrapperPolicy;

namespace Bugsee.Platform.IOS
{
    [Serializable]
    sealed class IosReportDto
    {
        public string id;
        public string type;
        public string summary;
        public string description;
        public string email;
        public int severity;
        public string[] labels;
    }

    [Serializable]
    sealed class IosAttachmentDto
    {
        public string name;
        public string fileName;
        public string mimeType;
        public string text;
        public string dataBase64;
    }

    sealed class IosReport : IReport
    {
        readonly IosReportDto _dto;
        readonly Dictionary<string, object> _attributes = new Dictionary<string, object>();
        readonly HashSet<string> _removedAttributes = new HashSet<string>();
        // Names the handler set: only these go back, so attributes it never touched keep their native values and kinds.
        readonly HashSet<string> _changedAttributes = new HashSet<string>();
        readonly List<string> _labels = new List<string>();
        readonly List<IosAttachment> _attachments = new List<IosAttachment>();
        bool _attributesClearAll;
        bool _labelsDirty;
        bool _attachmentsDirty;
        bool _attachmentsReplaceAll;

        public IosReport(IosReportDto dto) : this(dto, null)
        {
        }

        /// <param name="attributes">The report's attributes as native sent them (typed, nested values kept), or null.</param>
        public IosReport(IosReportDto dto, IDictionary<string, object> attributes)
        {
            _dto = dto ?? new IosReportDto();
            if (_dto.labels != null)
                _labels.AddRange(_dto.labels);
            if (attributes != null)
            {
                foreach (var p in attributes)
                {
                    if (string.IsNullOrEmpty(p.Key)) continue;
                    _attributes[p.Key] = p.Value;
                }
            }
        }

        /// <summary>The <c>attributes</c> object of the native report JSON with value kinds intact; empty when
        /// absent, null when the JSON cannot be read (malformed, or nested past the depth limit), so the caller can say so.</summary>
        internal static Dictionary<string, object> ReadAttributes(string json)
        {
            var root = JsonRead.Object(json);
            if (root == null) return null;
            if (root.TryGetValue("attributes", out var value) && value is Dictionary<string, object> map)
                return map;
            return new Dictionary<string, object>();
        }

        public string Id => _dto.id;

        public IssueType Type =>
            IssueTypeExtensions.FromWireValue(
                string.IsNullOrEmpty(_dto.type) ? null : _dto.type,
                IssueType.Bug);

        public string Summary
        {
            get => _dto.summary;
            set => _dto.summary = value;
        }

        public string Description
        {
            get => _dto.description;
            set => _dto.description = value;
        }

        public string Email
        {
            get => _dto.email;
            set => _dto.email = value;
        }

        public IssueSeverity? Severity
        {
            get => IssueSeverityWire.TryFromWire(_dto.severity, out var severity) ? severity : (IssueSeverity?)null;
            set => _dto.severity = value.HasValue ? (int)value.Value : 0;
        }

        public IReadOnlyDictionary<string, object> Attributes => _attributes;

        public object GetAttribute(string name)
        {
            if (name == null) return null;
            return _attributes.TryGetValue(name, out var v) ? v : null;
        }

        public void SetAttribute(string name, object value)
        {
            if (string.IsNullOrEmpty(name)) return;
            _attributes[name] = value;
            _removedAttributes.Remove(name);
            _changedAttributes.Add(name);
        }

        public void RemoveAttribute(string name)
        {
            if (name == null) return;
            _attributes.Remove(name);
            _changedAttributes.Remove(name);
            _removedAttributes.Add(name);
        }

        public void ClearAllAttributes()
        {
            _attributes.Clear();
            _removedAttributes.Clear();
            _changedAttributes.Clear();
            _attributesClearAll = true;
        }

        public IReadOnlyList<string> Labels => _labels;

        public void AddLabel(string label)
        {
            if (!string.IsNullOrEmpty(label))
            {
                _labels.Add(label);
                _labelsDirty = true;
            }
        }

        public void ClearLabels()
        {
            _labels.Clear();
            _labelsDirty = true;
        }

        public void SetLabels(IEnumerable<string> labels)
        {
            _labels.Clear();
            _labelsDirty = true;
            if (labels == null) return;
            foreach (var label in labels)
            {
                if (!string.IsNullOrEmpty(label)) _labels.Add(label);
            }
        }

        public IReadOnlyList<IAttachment> Attachments => _attachments;

        // Set once the report has been completed: a handler that is still running (it was abandoned or answered late)
        // must not copy a file nothing will ever submit or delete.
        bool _released;
        readonly object _releaseGate = new object();

        // One lock for the release check, the copy, the registration and the release cleanup, so a copy cannot land after
        // the cleanup has looked at the list.
        public IAttachment AddAttachmentFile(string path, string name, string mimeType)
        {
            lock (_releaseGate) return AddAttachmentFileLocked(path, name, mimeType);
        }

        IAttachment AddAttachmentFileLocked(string path, string name, string mimeType)
        {
            if (_released || string.IsNullOrEmpty(path) || !File.Exists(path))
                return null;

            string snapshotPath;
            try
            {
                snapshotPath = SnapshotAttachmentFile(path);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }

            var att = new IosAttachment(name ?? "attachment");
            var baseName = Path.GetFileName(path);
            att.Filename = string.IsNullOrEmpty(baseName) ? (name ?? "attachment") : baseName;
            // A missing MIME type stays unset (the report contract: "the backend treats it as unknown"); none is invented.
            att.MimeType = string.IsNullOrEmpty(mimeType) ? null : mimeType;
            att.SetSnapshotPath(snapshotPath);
            _attachments.Add(att);
            _attachmentsDirty = true;
            return att;
        }

        public IAttachment AddAttachmentBytes(byte[] data, string name, string mimeType)
        {
            // Empty data is valid and produces an empty attachment (report contract); only null is an error.
            if (data == null)
                return null;
            var att = new IosAttachment(name ?? "attachment");
            att.Filename = name ?? "attachment";
            att.MimeType = string.IsNullOrEmpty(mimeType) ? null : mimeType;
            att.SetAttachmentBytes(data);
            _attachments.Add(att);
            _attachmentsDirty = true;
            return att;
        }

        static string SnapshotAttachmentFile(string sourcePath)
        {
            var dir = Path.Combine(Path.GetTempPath(), "bugsee-unity-report-attachments");
            Directory.CreateDirectory(dir);
            var ext = Path.GetExtension(sourcePath);
            if (string.IsNullOrEmpty(ext))
                ext = ".bin";
            var dest = Path.Combine(dir, Guid.NewGuid().ToString("N") + ext);
            File.Copy(sourcePath, dest, true);
            return dest;
        }

        public void ClearAttachments()
        {
            for (var i = 0; i < _attachments.Count; i++)
                _attachments[i].DeleteSnapshotIfOwned();
            _attachments.Clear();
            _attachmentsDirty = true;
            _attachmentsReplaceAll = true;
        }

        public void ReleaseSnapshotFiles()
        {
            lock (_releaseGate)
            {
                _released = true;
                for (var i = 0; i < _attachments.Count; i++)
                    _attachments[i].DeleteSnapshotIfOwned();
            }
        }

        public string ToResultJson()
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            AppendString(sb, "summary", _dto.summary); sb.Append(',');
            AppendString(sb, "description", _dto.description); sb.Append(',');
            AppendString(sb, "email", _dto.email);
            if (_dto.severity != 0)
            {
                sb.Append(',');
                sb.Append("\"severity\":").Append(_dto.severity.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (_labelsDirty)
            {
                sb.Append(",\"labels\":[");
                for (var i = 0; i < _labels.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    IosJsonStringEncoding.AppendQuoted(sb, _labels[i]);
                }
                sb.Append(']');
            }

            // Attributes go back as a change set: the names the handler set (kind kept) and the names it removed.
            // Native keeps every attribute the handler did not touch, with its own value kind. Only
            // ClearAllAttributes replaces the whole set.
            if (!_attributesClearAll && _removedAttributes.Count > 0)
            {
                sb.Append(",\"attributeRemovals\":[");
                var firstRemoval = true;
                foreach (var name in _removedAttributes)
                {
                    if (!firstRemoval) sb.Append(',');
                    firstRemoval = false;
                    IosJsonStringEncoding.AppendQuoted(sb, name);
                }
                sb.Append(']');
            }

            if (_attributesClearAll || _changedAttributes.Count > 0)
            {
                sb.Append(",\"attributes\":{");
                var first = true;
                foreach (var kv in _attributes)
                {
                    if (kv.Key == null) continue;
                    if (!_attributesClearAll && !_changedAttributes.Contains(kv.Key)) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    IosJsonStringEncoding.AppendQuoted(sb, kv.Key);
                    sb.Append(':');
                    AppendValue(sb, kv.Value);
                }
                sb.Append('}');
            }

            if (_attributesClearAll)
                sb.Append(",\"attributesReplaceAll\":true");

            if (_attachmentsDirty)
            {
                sb.Append(",\"attachments\":[");
                for (var i = 0; i < _attachments.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(_attachments[i].ToJsonObject());
                }
                sb.Append(']');
                if (_attachmentsReplaceAll)
                    sb.Append(",\"attachmentsReplaceAll\":true");
            }

            sb.Append('}');
            return sb.ToString();
        }

        static void AppendString(StringBuilder sb, string key, string value)
        {
            IosJsonStringEncoding.AppendQuoted(sb, key);
            sb.Append(':');
            IosJsonStringEncoding.AppendQuoted(sb, value);
        }

        static void AppendRawString(StringBuilder sb, string value) =>
            IosJsonStringEncoding.AppendQuoted(sb, value);

        // The same value wire as Bugsee.SetAttribute: a whole-number double keeps its ".0" (native would read 2 as an
        // integer), "R" digits, invariant culture, enums by value, containers as JSON, NaN/Infinity as null.
        static void AppendValue(StringBuilder sb, object value) => sb.Append(AttributeWire.ToJson(value));
    }

    static class IosJsonStringEncoding
    {
        internal static void AppendQuoted(StringBuilder sb, string value)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }

            sb.Append('"');
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                switch (c)
                {
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\b':
                        sb.Append("\\b");
                        break;
                    case '\f':
                        sb.Append("\\f");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (c < '\u0020')
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }

            sb.Append('"');
        }
    }

    sealed class IosAttachment : IAttachment
    {
        string _path;
        string _text;
        string _dataBase64;
        bool _isBinary;
        bool _ownsSnapshotFile;

        public IosAttachment(string name)
        {
            Name = name;
            Filename = name;
        }

        public string Name { get; set; }
        public string Filename { get; set; }
        public string MimeType { get; set; }

        public void SetData(byte[] data)
        {
            _path = null;
            if (data == null || data.Length == 0)
            {
                _text = "";
                _dataBase64 = null;
                _isBinary = false;
                return;
            }

            // Prefer base64 for arbitrary bytes (images, etc.); UTF-8 text stays as text.
            if (LooksLikeUtf8Text(data))
            {
                _text = Encoding.UTF8.GetString(data);
                _dataBase64 = null;
                _isBinary = false;
            }
            else
            {
                _text = null;
                _dataBase64 = Convert.ToBase64String(data);
                _isBinary = true;
            }
        }

        public void SetData(string text)
        {
            _text = text ?? "";
            _dataBase64 = null;
            _path = null;
            _isBinary = false;
        }

        public void SetFilePath(string path)
        {
            _path = path;
            _text = null;
            _dataBase64 = null;
            _isBinary = false;
            _ownsSnapshotFile = false;
        }

        public void SetSnapshotPath(string path)
        {
            _path = path;
            _text = null;
            _dataBase64 = null;
            _isBinary = false;
            _ownsSnapshotFile = true;
        }

        public void DeleteSnapshotIfOwned()
        {
            if (!_ownsSnapshotFile || string.IsNullOrEmpty(_path))
                return;

            try
            {
                if (File.Exists(_path))
                    File.Delete(_path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            _ownsSnapshotFile = false;
        }

        public void SetAttachmentBytes(byte[] data)
        {
            _path = null;
            _text = null;
            _isBinary = true;
            _dataBase64 = Convert.ToBase64String(data);
            _ownsSnapshotFile = false;
        }

        public string ToJsonObject()
        {
            var sb = new StringBuilder(128);
            sb.Append('{');
            Append("name", Name); sb.Append(',');
            Append("fileName", Filename); sb.Append(',');
            if (!string.IsNullOrEmpty(MimeType))
            {
                Append("mimeType", MimeType); sb.Append(',');
            }
            if (!string.IsNullOrEmpty(_path))
            {
                Append("path", _path);
            }
            else if (_isBinary && !string.IsNullOrEmpty(_dataBase64))
            {
                Append("dataBase64", _dataBase64);
            }
            else if (!string.IsNullOrEmpty(_dataBase64))
            {
                Append("dataBase64", _dataBase64);
            }
            else
            {
                Append("dataBase64", Convert.ToBase64String(Encoding.UTF8.GetBytes(_text ?? "")));
            }
            sb.Append('}');
            return sb.ToString();

            void Append(string key, string value)
            {
                IosJsonStringEncoding.AppendQuoted(sb, key);
                sb.Append(':');
                IosJsonStringEncoding.AppendQuoted(sb, value);
            }
        }

        static bool LooksLikeUtf8Text(byte[] data)
        {
            // Reject NULs and high ratio of non-text control bytes.
            var controls = 0;
            for (var i = 0; i < data.Length; i++)
            {
                var b = data[i];
                if (b == 0) return false;
                if (b < 0x09 || (b > 0x0d && b < 0x20)) controls++;
            }
            return controls * 20 < data.Length;
        }
    }
}
#endif
