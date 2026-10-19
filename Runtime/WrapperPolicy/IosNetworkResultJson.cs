using System;
using System.Collections.Generic;
using System.Text;

namespace Bugsee.WrapperPolicy
{
    /// <summary>The network event fields a filter can change, as plain values (no JsonUtility).</summary>
    public sealed class IosNetworkFields
    {
        public string Url;
        public string Body;
        public long Size;
        public int ResponseCode;
        public string StatusText;
        public string ErrorShortMessage;
        public string ErrorDescription;
        public IDictionary<string, string> Headers;

        public IosNetworkFields Copy() => new IosNetworkFields
        {
            Url = Url,
            Body = Body,
            Size = Size,
            ResponseCode = ResponseCode,
            StatusText = StatusText,
            ErrorShortMessage = ErrorShortMessage,
            ErrorDescription = ErrorDescription,
            Headers = Headers == null ? null : new Dictionary<string, string>(Headers),
        };
    }

    /// <summary>
    /// The JSON a network event filter result hands to native (<c>_bugsee_complete_filter</c>, <c>_bugsee_channel_network</c>).
    /// Native applies only the keys that are present, and a JSON null clears the field. Unity's JsonUtility writes a null
    /// string as "" and cannot omit a field, so serializing the whole event made every filtered request reach the SDK with
    /// an empty error, an empty body and an empty url. Here a field is written only when the filter changed it, so
    /// anything it left alone keeps its native value (a nil error stays nil), and a deliberate null or "" still applies.
    /// </summary>
    public static class IosNetworkResultJson
    {
        /// <summary>Filter result for an event the filter was handed: only fields that differ from <paramref name="before"/>.</summary>
        public static string ForFilter(IosNetworkFields before, IosNetworkFields after, string stage)
        {
            before = before ?? new IosNetworkFields();
            after = after ?? before;
            var w = new Writer();
            w.Str("stage", stage);
            if (!string.Equals(before.Url, after.Url, StringComparison.Ordinal)) w.Str("url", after.Url, true);
            if (!string.Equals(before.Body, after.Body, StringComparison.Ordinal)) w.Str("body", after.Body, true);
            if (before.Size != after.Size) w.Raw("size", after.Size.ToString());
            if (before.ResponseCode != after.ResponseCode) w.Raw("responseCode", after.ResponseCode.ToString());
            if (!string.Equals(before.StatusText, after.StatusText, StringComparison.Ordinal)) w.Str("statusText", after.StatusText, true);
            if (!string.Equals(before.ErrorShortMessage, after.ErrorShortMessage, StringComparison.Ordinal))
                w.Str("errorShortMessage", after.ErrorShortMessage, true);
            if (!string.Equals(before.ErrorDescription, after.ErrorDescription, StringComparison.Ordinal))
                w.Str("errorDescription", after.ErrorDescription, true);
            if (!SameHeaders(before.Headers, after.Headers)) w.Raw("headers", HeadersJson(after.Headers));
            return w.End();
        }

        /// <summary>
        /// A complete event (a replacement the filter returned, or one submitted through the channel). There is nothing
        /// native to keep, so a null or empty string is simply absent: no error is not an empty error.
        /// </summary>
        public static string ForNewEvent(IosNetworkFields f, string id, string method, string mechanism, string stage, long timestampMs)
        {
            f = f ?? new IosNetworkFields();
            var w = new Writer();
            w.Str("id", id);
            w.Str("method", method);
            w.Str("mechanism", mechanism);
            w.Str("stage", stage);
            w.Str("url", f.Url);
            w.Str("body", f.Body);
            w.Str("statusText", f.StatusText);
            w.Str("errorShortMessage", f.ErrorShortMessage);
            w.Str("errorDescription", f.ErrorDescription);
            w.Raw("size", f.Size.ToString());
            w.Raw("responseCode", f.ResponseCode.ToString());
            if (timestampMs > 0) w.Raw("timestampMs", timestampMs.ToString());
            if (f.Headers != null && f.Headers.Count > 0) w.Raw("headers", HeadersJson(f.Headers));
            return w.End();
        }

        static bool SameHeaders(IDictionary<string, string> a, IDictionary<string, string> b)
        {
            var ac = a == null ? 0 : a.Count;
            var bc = b == null ? 0 : b.Count;
            if (ac != bc) return false;
            if (ac == 0) return true;
            foreach (var kv in a)
            {
                string other;
                if (!b.TryGetValue(kv.Key, out other) || !string.Equals(kv.Value, other, StringComparison.Ordinal)) return false;
            }

            return true;
        }

        static string HeadersJson(IDictionary<string, string> headers)
        {
            var sb = new StringBuilder("{");
            var first = true;
            if (headers != null)
            {
                foreach (var kv in headers)
                {
                    if (kv.Key == null) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonWire.String(kv.Key)).Append(':').Append(JsonWire.String(kv.Value));
                }
            }

            return sb.Append('}').ToString();
        }

        sealed class Writer
        {
            readonly StringBuilder _sb = new StringBuilder("{");
            bool _any;

            public void Raw(string key, string json)
            {
                if (_any) _sb.Append(',');
                _any = true;
                _sb.Append('"').Append(key).Append("\":").Append(json);
            }

            // keepEmpty: a changed field is written even when null or "" (that is the filter clearing it).
            public void Str(string key, string value, bool keepEmpty = false)
            {
                if (!keepEmpty && string.IsNullOrEmpty(value)) return;
                Raw(key, JsonWire.String(value));
            }

            public string End() => _sb.Append('}').ToString();
        }
    }
}
