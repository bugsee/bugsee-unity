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
        public IosKv[] attributes;
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
        readonly List<string> _labels = new List<string>();
        readonly List<IosAttachment> _attachments = new List<IosAttachment>();
        readonly bool _hadAttributeArray;
        bool _attributesDirty;

        public IosReport(IosReportDto dto)
        {
            _dto = dto ?? new IosReportDto();
            _hadAttributeArray = _dto.attributes != null;
            if (_dto.labels != null)
                _labels.AddRange(_dto.labels);
            if (_dto.attributes != null)
            {
                foreach (var p in _dto.attributes)
                {
                    if (p == null || string.IsNullOrEmpty(p.key)) continue;
                    _attributes[p.key] = p.value;
                }
            }
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
            _attributesDirty = true;
        }

        public void RemoveAttribute(string name)
        {
            if (name == null) return;
            _attributes.Remove(name);
            _attributesDirty = true;
        }

        public void ClearAllAttributes()
        {
            _attributes.Clear();
            _attributesDirty = true;
        }

        public IReadOnlyList<string> Labels => _labels;

        public void AddLabel(string label)
        {
            if (!string.IsNullOrEmpty(label)) _labels.Add(label);
        }

        public void ClearLabels() => _labels.Clear();

        public void SetLabels(IEnumerable<string> labels)
        {
            _labels.Clear();
            if (labels == null) return;
            foreach (var label in labels)
            {
                if (!string.IsNullOrEmpty(label)) _labels.Add(label);
            }
        }

        public IReadOnlyList<IAttachment> Attachments => _attachments;

        public IAttachment AddAttachmentFile(string path, string name, string mimeType)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
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
            att.MimeType = mimeType ?? "application/octet-stream";
            att.SetSnapshotPath(snapshotPath);
            _attachments.Add(att);
            return att;
        }

        public IAttachment AddAttachmentBytes(byte[] data, string name, string mimeType)
        {
            if (data == null || data.Length == 0)
                return null;
            var att = new IosAttachment(name ?? "attachment");
            att.Filename = name ?? "attachment";
            att.MimeType = mimeType ?? "application/octet-stream";
            att.SetAttachmentBytes(data);
            _attachments.Add(att);
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
        }

        public void ReleaseSnapshotFiles()
        {
            for (var i = 0; i < _attachments.Count; i++)
                _attachments[i].DeleteSnapshotIfOwned();
        }

        public string ToResultJson()
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            AppendString(sb, "summary", _dto.summary); sb.Append(',');
            AppendString(sb, "description", _dto.description); sb.Append(',');
            AppendString(sb, "email", _dto.email); sb.Append(',');
            sb.Append("\"severity\":").Append(_dto.severity).Append(',');

            sb.Append("\"labels\":[");
            for (var i = 0; i < _labels.Count; i++)
            {
                if (i > 0) sb.Append(',');
                AppendRawString(sb, _labels[i]);
            }
            sb.Append("],");

            // Omit attributes when normalize failed to load them and user never
            // touched the map — otherwise ApplyReportDict would wipe native attrs.
            if (_hadAttributeArray || _attributesDirty || _attributes.Count > 0)
            {
                sb.Append("\"attributes\":{");
                var first = true;
                foreach (var kv in _attributes)
                {
                    if (kv.Key == null) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    AppendRawString(sb, kv.Key);
                    sb.Append(':');
                    AppendValue(sb, kv.Value);
                }
                sb.Append("},");
            }

            sb.Append("\"attachments\":[");
            for (var i = 0; i < _attachments.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(_attachments[i].ToJsonObject());
            }
            sb.Append("]}");
            return sb.ToString();
        }

        static void AppendString(StringBuilder sb, string key, string value)
        {
            AppendRawString(sb, key);
            sb.Append(':');
            AppendRawString(sb, value);
        }

        static void AppendRawString(StringBuilder sb, string value)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }
            sb.Append('"');
            sb.Append(value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r"));
            sb.Append('"');
        }

        static void AppendValue(StringBuilder sb, object value)
        {
            if (value == null) { sb.Append("null"); return; }
            if (value is bool b) { sb.Append(b ? "true" : "false"); return; }
            if (value is byte || value is sbyte || value is short || value is ushort
                || value is int || value is uint || value is long || value is ulong
                || value is float || value is double || value is decimal)
            {
                sb.Append(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
                return;
            }
            AppendRawString(sb, Convert.ToString(value));
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
            MimeType = "text/plain";
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
                if (string.IsNullOrEmpty(MimeType) || MimeType == "text/plain")
                    MimeType = "application/octet-stream";
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
            Append("mimeType", MimeType); sb.Append(',');
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
                sb.Append('"').Append(key).Append("\":");
                if (value == null) { sb.Append("null"); return; }
                sb.Append('"')
                    .Append(value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r"))
                    .Append('"');
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
