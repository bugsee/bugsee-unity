#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;
using Bugsee.Contracts.Options;
using Bugsee.Contracts.Reporting;
using Bugsee.WrapperPolicy;
using UnityEngine;

namespace Bugsee.Platform.Android
{
    /// <summary>Thin managed wrapper over a Java Report instance.</summary>
    sealed class AndroidReport : IReport
    {
        readonly AndroidJavaObject _report;

        public AndroidReport(AndroidJavaObject report)
        {
            _report = report;
        }

        public string Id => _report.Call<string>("getId");

        public IssueType Type
        {
            get
            {
                using (var t = _report.Call<AndroidJavaObject>("getType"))
                {
                    var wire = t?.Call<string>("toString");
                    return IssueTypeExtensions.FromWireValue(wire);
                }
            }
        }

        public string Summary
        {
            get => _report.Call<string>("getSummary");
            set => _report.Call("setSummary", value);
        }

        public string Description
        {
            get => _report.Call<string>("getDescription");
            set => _report.Call("setDescription", value);
        }

        public string Email
        {
            get => _report.Call<string>("getEmail");
            set => _report.Call("setEmail", value);
        }

        public IssueSeverity? Severity
        {
            get
            {
                using (var s = _report.Call<AndroidJavaObject>("getSeverity"))
                {
                    var v = s?.Call<int>("getValue") ?? (int)IssueSeverity.High;
                    return IssueSeverityWire.TryFromWire(v, out var severity) ? severity : (IssueSeverity?)null;
                }
            }
            set
            {
                if (!value.HasValue)
                    return;

                using (var clazz = new AndroidJavaClass("com.bugsee.library.contracts.options.IssueSeverity"))
                using (var sev = clazz.CallStatic<AndroidJavaObject>("fromIntValue", (int)value.Value))
                {
                    _report.Call("setSeverity", sev);
                }
            }
        }

        public IReadOnlyDictionary<string, object> Attributes => new Dictionary<string, object>();

        public object GetAttribute(string name)
        {
            using (var value = _report.Call<AndroidJavaObject>("getAttribute", name))
                return AndroidJavaConverters.Unbox(value);
        }

        public void SetAttribute(string name, object value)
        {
            using (var boxed = AndroidJavaConverters.Box(value))
                _report.Call("setAttribute", name, boxed);
        }

        public void RemoveAttribute(string name) => _report.Call("removeAttribute", name);
        public void ClearAllAttributes() => _report.Call("clearAllAttributes");

        public IReadOnlyList<string> Labels
        {
            get
            {
                var list = new List<string>();
                using (var javaList = _report.Call<AndroidJavaObject>("getLabels"))
                {
                    if (javaList == null) return list;
                    var size = javaList.Call<int>("size");
                    for (var i = 0; i < size; i++)
                        list.Add(javaList.Call<string>("get", i));
                }
                return list;
            }
        }

        public void AddLabel(string label) => _report.Call("addLabel", label);
        public void ClearLabels() => _report.Call("clearLabels");

        public void SetLabels(IEnumerable<string> labels)
        {
            using (var javaList = new AndroidJavaObject("java.util.ArrayList"))
            {
                if (labels != null)
                {
                    foreach (var label in labels)
                        javaList.Call<bool>("add", label);
                }
                _report.Call("setLabels", javaList);
            }
        }

        public IReadOnlyList<IAttachment> Attachments => new List<IAttachment>();

        public IAttachment CreateAndAddAttachment(string name)
        {
            var att = _report.Call<AndroidJavaObject>("createAndAddAttachment", name);
            return new AndroidAttachment(att);
        }

        public void ClearAttachments() => _report.Call("clearAttachments");
    }

    sealed class AndroidAttachment : IAttachment
    {
        readonly AndroidJavaObject _attachment;

        public AndroidAttachment(AndroidJavaObject attachment)
        {
            _attachment = attachment;
        }

        public string Name
        {
            get => _attachment.Call<string>("getName");
            set => _attachment.Call("setName", value);
        }

        public string Filename
        {
            get => _attachment.Call<string>("getFileName", (string)null);
            set => _attachment.Call("setFileName", value);
        }

        public string MimeType
        {
            get => _attachment.Call<string>("getMimeType");
            set => _attachment.Call("setMimeType", value);
        }

        public void SetData(byte[] data)
        {
            if (data == null || data.Length == 0) return;

            using (var stream = _attachment.Call<AndroidJavaObject>("openStream"))
            {
                if (stream == null)
                    throw new InvalidOperationException("Attachment.openStream() returned null.");

                try
                {
                    var signed = AndroidJavaConverters.ToSBytes(data);
                    stream.Call("write", signed);
                    stream.Call("flush");
                }
                finally
                {
                    stream.Call("close");
                }
            }
        }

        public void SetData(string text)
        {
            if (text == null) return;
            SetData(Encoding.UTF8.GetBytes(text));
        }
    }

    /// <summary>Upload generation fence; Stop/Delete invalidate in-flight createReport callbacks.</summary>
    internal static class AndroidManagedReportUploadFence
    {
        static ulong _fence;

        public static ulong Current => _fence;

        public static void Invalidate() => checked { _fence++; }

        public static bool IsActive(ulong captured) => captured == _fence;
    }

    /// <summary>Managed report snapshot; native Report is created at upload.</summary>
    sealed class AndroidManagedReport : IReport
    {
        readonly Dictionary<string, object> _attributes = new Dictionary<string, object>();
        readonly HashSet<string> _removedAttributes = new HashSet<string>();
        readonly List<string> _labels = new List<string>();
        readonly List<AndroidManagedAttachment> _attachments = new List<AndroidManagedAttachment>();
        bool _emailAssigned;
        bool _attributesOverlayDirty;
        bool _attributesClearAll;
        bool _labelsDirty;
        bool _attachmentsDirty;

        public string Id => "";

        public IssueType Type => IssueType.Bug;

        public string Summary { get; set; }
        public string Description { get; set; }

        public string Email
        {
            get => _email;
            set
            {
                _email = value;
                _emailAssigned = true;
            }
        }

        string _email;

        public IssueSeverity? Severity { get; set; }

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
            _attributesOverlayDirty = true;
        }

        public void RemoveAttribute(string name)
        {
            if (name == null) return;
            _attributes.Remove(name);
            _removedAttributes.Add(name);
            _attributesOverlayDirty = true;
        }

        public void ClearAllAttributes()
        {
            _attributes.Clear();
            _removedAttributes.Clear();
            _attributesClearAll = true;
            _attributesOverlayDirty = false;
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
            var att = new AndroidManagedAttachment(name ?? "attachment", MarkAttachmentsDirty);
            _attachments.Add(att);
            _attachmentsDirty = true;
            return att;
        }

        public void ClearAttachments()
        {
            _attachments.Clear();
            _attachmentsDirty = true;
        }

        void MarkAttachmentsDirty() => _attachmentsDirty = true;

        internal void ApplyTo(AndroidJavaObject javaReport)
        {
            if (javaReport == null)
                return;

            var report = new AndroidReport(javaReport);
            if (Summary != null)
                report.Summary = Summary;
            if (Description != null)
                report.Description = Description;
            if (_emailAssigned)
                report.Email = Email;
            if (Severity.HasValue)
                report.Severity = Severity;

            if (_attributesClearAll)
            {
                report.ClearAllAttributes();
                foreach (var kv in _attributes)
                {
                    if (kv.Key == null) continue;
                    report.SetAttribute(kv.Key, kv.Value);
                }
            }
            else if (_attributesOverlayDirty)
            {
                foreach (var name in _removedAttributes)
                    report.RemoveAttribute(name);
                foreach (var kv in _attributes)
                {
                    if (kv.Key == null) continue;
                    report.SetAttribute(kv.Key, kv.Value);
                }
            }

            if (_labelsDirty)
            {
                report.ClearLabels();
                report.SetLabels(_labels);
            }

            if (_attachmentsDirty)
            {
                report.ClearAttachments();
                for (var i = 0; i < _attachments.Count; i++)
                    _attachments[i].ApplyTo(report);
            }
        }
    }

    sealed class AndroidManagedAttachment : IAttachment
    {
        readonly Action _markDirty;
        byte[] _bytes;
        string _text;

        public AndroidManagedAttachment(string name, Action markDirty)
        {
            _markDirty = markDirty;
            Name = name;
            Filename = name;
            MimeType = "text/plain";
        }

        public string Name { get; set; }
        public string Filename { get; set; }
        public string MimeType { get; set; }

        public void SetData(byte[] data)
        {
            _bytes = data;
            _text = null;
            _markDirty?.Invoke();
        }

        public void SetData(string text)
        {
            _text = text ?? "";
            _bytes = null;
            _markDirty?.Invoke();
        }

        internal void ApplyTo(AndroidReport report)
        {
            var attachment = report.CreateAndAddAttachment(Name ?? "attachment");
            attachment.Filename = Filename;
            attachment.MimeType = MimeType;
            if (_bytes != null && _bytes.Length > 0)
                attachment.SetData(_bytes);
            else if (_text != null)
                attachment.SetData(_text);
        }
    }
}
#endif
