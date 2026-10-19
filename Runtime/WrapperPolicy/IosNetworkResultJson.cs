using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Bugsee.WrapperPolicy
{
    /// <summary>The network event fields a filter can set.</summary>
    [Flags]
    public enum IosNetworkField
    {
        None = 0,
        Url = 1,
        Body = 2,
        Size = 4,
        ResponseCode = 8,
        StatusText = 16,
        ErrorShortMessage = 32,
        ErrorDescription = 64,
        Headers = 128,
    }

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
    /// Native applies only the keys that are present, and a JSON null (or an empty error string) clears the field.
    /// Unity's JsonUtility writes a null string as "" and cannot omit a field, so serializing the whole event made
    /// every filtered request reach the SDK with an empty error, an empty body and an empty url. Three shapes:
    /// <see cref="ForFilter"/> (the event the filter was handed: only what it set), <see cref="ForReplacement"/>
    /// (an event the filter returned instead: every field written, because native applies it onto the original) and
    /// <see cref="ForNewEvent"/> (a channel submit onto a fresh native event: empty strings are simply absent).
    /// </summary>
    public static class IosNetworkResultJson
    {
        /// <summary>
        /// Filter result for the event the filter was handed: <paramref name="stage"/> plus the fields in
        /// <paramref name="touched"/> (the filter assigned them, whatever the value: null and "" clear, which includes
        /// a body native sent as null because it is not text).
        /// </summary>
        public static string ForFilter(IosNetworkFields now, IosNetworkField touched, string stage)
        {
            now = now ?? new IosNetworkFields();
            var w = new Writer();
            w.Str("stage", stage);
            if (Has(touched, IosNetworkField.Url)) w.Str("url", now.Url, true);
            if (Has(touched, IosNetworkField.Body)) w.Str("body", now.Body, true);
            if (Has(touched, IosNetworkField.Size)) w.Raw("size", Num(now.Size));
            if (Has(touched, IosNetworkField.ResponseCode)) w.Raw("responseCode", Num(now.ResponseCode));
            if (Has(touched, IosNetworkField.StatusText)) w.Str("statusText", now.StatusText, true);
            if (Has(touched, IosNetworkField.ErrorShortMessage)) w.Str("errorShortMessage", now.ErrorShortMessage, true);
            if (Has(touched, IosNetworkField.ErrorDescription)) w.Str("errorDescription", now.ErrorDescription, true);
            if (Has(touched, IosNetworkField.Headers)) w.Raw("headers", HeadersJson(now.Headers));
            return w.End();
        }

        /// <summary>
        /// An event the filter returned in place of the one it was handed. Native applies the result onto the original
        /// event and changes only the keys present, so every field is written, null and empty ones as JSON null: what
        /// the replacement does not carry (body, Authorization header, error) must not survive from the original.
        /// No timestamp: the replacement's creation time must not overwrite when the request happened.
        /// </summary>
        public static string ForReplacement(IosNetworkFields f, string method, string mechanism, string stage)
        {
            f = f ?? new IosNetworkFields();
            var w = new Writer();
            w.Str("method", method);
            w.Str("mechanism", mechanism);
            w.Str("stage", stage);
            w.Str("url", f.Url, true);
            w.Raw("body", string.IsNullOrEmpty(f.Body) ? "null" : JsonWire.String(f.Body));
            w.Raw("errorShortMessage", string.IsNullOrEmpty(f.ErrorShortMessage) ? "null" : JsonWire.String(f.ErrorShortMessage));
            w.Raw("errorDescription", string.IsNullOrEmpty(f.ErrorDescription) ? "null" : JsonWire.String(f.ErrorDescription));
            w.Raw("size", Num(f.Size));
            w.Raw("responseCode", Num(f.ResponseCode));
            w.Raw("headers", HeadersJson(f.Headers));
            return w.End();
        }

        /// <summary>
        /// A complete event for a fresh native event (a channel submit). There is nothing native to keep, so a null or
        /// empty string is simply absent: no error is not an empty error.
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
            w.Raw("size", Num(f.Size));
            w.Raw("responseCode", Num(f.ResponseCode));
            if (timestampMs > 0) w.Raw("timestampMs", Num(timestampMs));
            if (f.Headers != null && f.Headers.Count > 0) w.Raw("headers", HeadersJson(f.Headers));
            return w.End();
        }

        static bool Has(IosNetworkField set, IosNetworkField flag) => (set & flag) != 0;

        // Invariant: a culture's minus sign or digits would make the JSON invalid and native would drop the whole result.
        static string Num(long n) => n.ToString(CultureInfo.InvariantCulture);

        public static bool SameHeaders(IDictionary<string, string> a, IDictionary<string, string> b)
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
                    // A null value would reach native as NSNull inside the headers; the old path sent "".
                    sb.Append(JsonWire.String(kv.Key)).Append(':').Append(JsonWire.String(kv.Value ?? ""));
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
