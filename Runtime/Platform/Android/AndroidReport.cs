#if UNITY_ANDROID && !UNITY_EDITOR
using System.Collections.Generic;
using Bugsee.Contracts.Options;
using Bugsee.Contracts.Reporting;
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

        public IssueSeverity Severity
        {
            get
            {
                using (var s = _report.Call<AndroidJavaObject>("getSeverity"))
                {
                    var v = s?.Call<int>("getValue") ?? (int)IssueSeverity.High;
                    return IssueSeverityExtensions.FromIntValue(v);
                }
            }
            set
            {
                using (var clazz = new AndroidJavaClass("com.bugsee.library.contracts.options.IssueSeverity"))
                using (var sev = clazz.CallStatic<AndroidJavaObject>("fromIntValue", (int)value))
                {
                    _report.Call("setSeverity", sev);
                }
            }
        }

        public IReadOnlyDictionary<string, object> Attributes => new Dictionary<string, object>();

        public object GetAttribute(string name) => _report.Call<AndroidJavaObject>("getAttribute", name);

        public void SetAttribute(string name, object value)
        {
            // Best-effort: strings and boxed primitives.
            if (value is string s) _report.Call("setAttribute", name, new AndroidJavaObject("java.lang.String", s));
            else if (value is int i) _report.Call("setAttribute", name, new AndroidJavaObject("java.lang.Integer", i));
            else if (value is bool b) _report.Call("setAttribute", name, new AndroidJavaObject("java.lang.Boolean", b));
            else if (value != null) _report.Call("setAttribute", name, new AndroidJavaObject("java.lang.String", value.ToString()));
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
            get => _attachment.Call<string>("getFilename");
            set => _attachment.Call("setFilename", value);
        }

        public string MimeType
        {
            get => _attachment.Call<string>("getMimeType");
            set => _attachment.Call("setMimeType", value);
        }

        public void SetData(byte[] data)
        {
            if (data == null) return;
            // setData(byte[]) on Attachment — pass via AndroidJNI helper
            var javaBytes = AndroidJNIHelper.ConvertToJNIArray(data);
            // CallObjectMethod path is awkward; use AndroidJavaObject with sbyte[] when possible.
            _attachment.Call("setData", data);
        }

        public void SetData(string text) => _attachment.Call("setData", text);
    }
}
#endif
