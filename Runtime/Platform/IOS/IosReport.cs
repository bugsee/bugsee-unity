#if UNITY_IOS && !UNITY_EDITOR
using System;
using System.Collections.Generic;
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
        bool _labelsDirty;
        bool _attachmentsDirty;

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

        public IAttachment CreateAndAddAttachment(string name)
        {
            var att = new IosAttachment(name ?? "attachment");
            _attachments.Add(att);
            _attachmentsDirty = true;
            return att;
        }

        public void ClearAttachments()
        {
            _attachments.Clear();
            _attachmentsDirty = true;
        }

        public string ToResultJson()
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            AppendString(sb, "summary", _dto.summary); sb.Append(',');
            AppendString(sb, "description", _dto.description); sb.Append(',');
            AppendString(sb, "email", _dto.email); sb.Append(',');
            sb.Append("\"severity\":").Append(_dto.severity);

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

            // Omit attributes when normalize failed to load them and user never
            // touched the map — otherwise ApplyReportDict would wipe native attrs.
            if (_hadAttributeArray || _attributesDirty || _attributes.Count > 0)
            {
                sb.Append(",\"attributes\":{");
                var first = true;
                foreach (var kv in _attributes)
                {
                    if (kv.Key == null) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    IosJsonStringEncoding.AppendQuoted(sb, kv.Key);
                    sb.Append(':');
                    AppendValue(sb, kv.Value);
                }
                sb.Append('}');
            }

            if (_attachmentsDirty)
            {
                sb.Append(",\"attachments\":[");
                for (var i = 0; i < _attachments.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(_attachments[i].ToJsonObject());
                }
                sb.Append(']');
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

        static void AppendValue(StringBuilder sb, object value)
        {
            if (value == null) { sb.Append("null"); return; }
            if (value is bool b) { sb.Append(b ? "true" : "false"); return; }
            if (value is float f)
            {
                if (float.IsNaN(f) || float.IsInfinity(f)) { sb.Append("null"); return; }
                sb.Append(f.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return;
            }
            if (value is double d)
            {
                if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
                sb.Append(d.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return;
            }
            if (value is byte || value is sbyte || value is short || value is ushort
                || value is int || value is uint || value is long || value is ulong
                || value is decimal)
            {
                sb.Append(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
                return;
            }
            IosJsonStringEncoding.AppendQuoted(sb, Convert.ToString(value));
        }
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
        string _text;
        string _dataBase64;
        bool _isBinary;

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
            _isBinary = false;
        }

        public string ToJsonObject()
        {
            var sb = new StringBuilder(128);
            sb.Append('{');
            Append("name", Name); sb.Append(',');
            Append("fileName", Filename); sb.Append(',');
            Append("mimeType", MimeType); sb.Append(',');
            if (_isBinary && !string.IsNullOrEmpty(_dataBase64))
            {
                Append("dataBase64", _dataBase64);
            }
            else
            {
                Append("text", _text ?? "");
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
