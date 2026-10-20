#if UNITY_IOS && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using Bugsee.Contracts.Exchange;
using Bugsee.Contracts.Options;
using Bugsee.WrapperPolicy;
using UnityEngine;

namespace Bugsee.Platform.IOS
{
    [Serializable]
    sealed class IosKv
    {
        public string key;
        public string value;
    }

    [Serializable]
    sealed class IosNetworkDto
    {
        public string id;
        public string url;
        public string method;
        public string mechanism;
        public string body;
        public long size;
        public int responseCode;
        public string statusText;
        public string errorShortMessage;
        public string errorDescription;
        public string stage;
        public long timestampMs;
        public IosKv[] headers;
    }

    [Serializable]
    sealed class IosLogDto
    {
        public string message;
        public int level;
    }

    [Serializable]
    sealed class IosBreadcrumbDto
    {
        public long timestamp;
        public string category;
        public string message;
        public string type;
        public int level;
    }

    sealed class IosNetworkEvent : INetworkEvent
    {
        readonly IosNetworkDto _dto;
        Dictionary<string, string> _headers;

        // What native handed over, to tell a changed header map from an untouched one (Headers hands out a mutable map).
        readonly IosNetworkFields _original;
        // Fields a setter was called on: the result carries exactly these, whatever value they hold.
        IosNetworkField _touched;

        public IosNetworkEvent(IosNetworkDto dto)
        {
            _dto = dto ?? new IosNetworkDto();
            _headers = FromKv(_dto.headers);
            _original = Fields();
        }

        IosNetworkFields Fields() => new IosNetworkFields
        {
            Url = _dto.url,
            Body = _dto.body,
            Size = _dto.size,
            ResponseCode = _dto.responseCode,
            StatusText = _dto.statusText,
            ErrorShortMessage = _dto.errorShortMessage,
            ErrorDescription = _dto.errorDescription,
            Headers = _headers == null ? null : new Dictionary<string, string>(_headers),
        };

        public string Id => _dto.id;
        public string Mechanism => _dto.mechanism;
        public string Method => _dto.method;

        public string Url
        {
            get => _dto.url;
            set { _dto.url = value; _touched |= IosNetworkField.Url; }
        }

        public string Body
        {
            get => _dto.body;
            set { _dto.body = value; _touched |= IosNetworkField.Body; }
        }

        public long Size
        {
            get => _dto.size;
            set { _dto.size = value; _touched |= IosNetworkField.Size; }
        }

        public int ResponseCode
        {
            get => _dto.responseCode;
            set { _dto.responseCode = value; _touched |= IosNetworkField.ResponseCode; }
        }

        public string StatusText
        {
            get => _dto.statusText;
            set { _dto.statusText = value; _touched |= IosNetworkField.StatusText; }
        }

        public string ErrorShortMessage
        {
            get => _dto.errorShortMessage;
            set { _dto.errorShortMessage = value; _touched |= IosNetworkField.ErrorShortMessage; }
        }

        public string ErrorDescription
        {
            get => _dto.errorDescription;
            set { _dto.errorDescription = value; _touched |= IosNetworkField.ErrorDescription; }
        }

        public IDictionary<string, string> Headers
        {
            get => _headers ?? (_headers = new Dictionary<string, string>());
            set
            {
                _touched |= IosNetworkField.Headers;
                _headers = value != null
                    ? new Dictionary<string, string>(value)
                    : new Dictionary<string, string>();
            }
        }

        // No iOS counterpart: kept on the managed event only, not part of the DTO or the channel JSON.
        public bool IsOverride { get; set; }

        public NetworkEventStage Stage => ParseStage(_dto.stage);

        // Fields only, no JsonUtility: it writes a null string as "" and cannot omit one, which gave every filtered
        // event an empty error, body and url. Only the fields the filter set go back (a body native could not read
        // as text arrives as "", so a changed value cannot be told from an untouched one by comparing); headers also
        // when the map handed out was edited in place. They go out as an object map, as native reads them.
        public string ToResultJson()
        {
            var touched = _touched;
            if (!IosNetworkResultJson.SameHeaders(_original.Headers, _headers)) touched |= IosNetworkField.Headers;
            return IosNetworkResultJson.ForFilter(Fields(), touched, _dto.stage);
        }

        /// <summary>
        /// This event as the filter's replacement for the event it was handed: every field is written (see
        /// <see cref="IosNetworkResultJson.ForReplacement"/>).
        /// </summary>
        internal string ToReplacementJson(string nativeStage) =>
            IosNetworkResultJson.ForReplacement(Fields(), _dto.method, _dto.mechanism, nativeStage);

        /// <summary>
        /// Serializes for the wrapper channel. The SDK types events by its own strings
        /// ("before" / "complete" / "cancel" / "error": BugseeNetworkEventBegin is the string "before"),
        /// not by <see cref="NetworkEventStage"/> names. The timestamp is the event's own time when it has
        /// one, else <paramref name="timestampMs"/> (Unix milliseconds, the unit the SDK uses).
        /// </summary>
        internal string ToChannelJson(string nativeStage, long timestampMs) =>
            IosNetworkResultJson.ForNewEvent(Fields(), _dto.id, _dto.method, _dto.mechanism, nativeStage,
                _dto.timestampMs > 0 ? _dto.timestampMs : timestampMs); // an event held in the launch buffer keeps the time it happened

        /// <summary>
        /// SDK event type for a stage, or null when iOS has no equivalent (redirect, timings-only,
        /// WebSocket without a sub-type): such events are not submitted rather than mistyped.
        /// </summary>
        internal static string ToNativeStage(NetworkEventStage stage)
        {
            switch (stage)
            {
                case NetworkEventStage.RequestStarted: return "before";
                case NetworkEventStage.RequestCompleted: return "complete";
                case NetworkEventStage.RequestErrored: return "error";
                case NetworkEventStage.RequestAborted: return "cancel";
                default: return null;
            }
        }

        static NetworkEventStage ParseStage(string stage)
        {
            if (string.IsNullOrEmpty(stage)) return NetworkEventStage.RequestCompleted;
            if (stage.IndexOf("Begin", StringComparison.OrdinalIgnoreCase) >= 0
                || stage.IndexOf("Start", StringComparison.OrdinalIgnoreCase) >= 0
                || string.Equals(stage, "before", StringComparison.OrdinalIgnoreCase)) // the SDK's BugseeNetworkEventBegin
                return NetworkEventStage.RequestStarted;
            if (stage.IndexOf("Cancel", StringComparison.OrdinalIgnoreCase) >= 0
                || stage.IndexOf("Abort", StringComparison.OrdinalIgnoreCase) >= 0)
                return NetworkEventStage.RequestAborted;
            if (stage.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0)
                return NetworkEventStage.RequestErrored;
            if (stage.IndexOf("Redirect", StringComparison.OrdinalIgnoreCase) >= 0)
                return NetworkEventStage.Redirect;
            if (stage.IndexOf("WebSocket", StringComparison.OrdinalIgnoreCase) >= 0)
                return NetworkEventStage.WebSocket;
            if (stage.IndexOf("Timing", StringComparison.OrdinalIgnoreCase) >= 0)
                return NetworkEventStage.RequestTimingsReceived;
            return NetworkEventStage.RequestCompleted;
        }

        internal static Dictionary<string, string> FromKv(IosKv[] pairs)
        {
            var map = new Dictionary<string, string>();
            if (pairs == null) return map;
            foreach (var p in pairs)
            {
                if (p == null || string.IsNullOrEmpty(p.key)) continue;
                map[p.key] = p.value;
            }
            return map;
        }

        internal static IosKv[] ToKv(IDictionary<string, string> map)
        {
            if (map == null || map.Count == 0) return Array.Empty<IosKv>();
            var list = new List<IosKv>(map.Count);
            foreach (var kv in map)
            {
                if (kv.Key == null) continue;
                list.Add(new IosKv { key = kv.Key, value = kv.Value });
            }
            return list.ToArray();
        }
    }

    sealed class IosLogEvent : ILogEvent
    {
        readonly IosLogDto _dto;

        public IosLogEvent(IosLogDto dto)
        {
            _dto = dto ?? new IosLogDto();
        }

        public string Message
        {
            get => _dto.message;
            set => _dto.message = value;
        }

        public LogLevel Level
        {
            get => LogLevelExtensions.FromRawValue(_dto.level);
            set => _dto.level = (int)value;
        }

        public string ToResultJson() => JsonUtility.ToJson(_dto);
    }

    sealed class IosBreadcrumb : IBreadcrumb
    {
        readonly IosBreadcrumbDto _dto;
        Dictionary<string, object> _data;
        // Data hands out a mutable dictionary, so any access counts as a touch. Until then the result omits `data`
        // and native keeps its own (exact numbers, nulls, a nil data stay as they were).
        bool _dataTouched;

        public IosBreadcrumb(IosBreadcrumbDto dto, IDictionary<string, object> data)
        {
            _dto = dto ?? new IosBreadcrumbDto();
            // null: native data could not be read (malformed or too deep); a filter that reads Data then sees empty.
            _data = data != null ? new Dictionary<string, object>(data) : null;
        }

        public long Timestamp
        {
            get => _dto.timestamp;
            set => _dto.timestamp = value;
        }

        public string Category
        {
            get => _dto.category;
            set => _dto.category = value;
        }

        public string Message
        {
            get => _dto.message;
            set => _dto.message = value;
        }

        public string Type
        {
            get => _dto.type;
            set => _dto.type = value;
        }

        public LogLevel? Level
        {
            get => _dto.level == 0 ? (LogLevel?)null : LogLevelExtensions.FromRawValue(_dto.level);
            set => _dto.level = value.HasValue ? (int)value.Value : 0;
        }

        public IDictionary<string, object> Data
        {
            get
            {
                _dataTouched = true;
                return _data ?? (_data = new Dictionary<string, object>());
            }
            set
            {
                _dataTouched = true;
                _data = value != null
                    ? new Dictionary<string, object>(value)
                    : new Dictionary<string, object>();
            }
        }

        // A touched data goes back as a real JSON object (nested values and scalar kinds kept), not through JsonUtility.
        public string ToResultJson() =>
            IosBridgeJson.AppendObjectField(JsonUtility.ToJson(_dto), "data", _dataTouched ? _data : null);
    }

    /// <summary>
    /// JsonUtility cannot deserialize free-form dictionaries; normalize native JSON
    /// (object maps) into parallel key/value arrays before FromJson.
    /// </summary>
    static class IosJsonNormalize
    {
        public static string NormalizeNetwork(string json)
        {
            if (string.IsNullOrEmpty(json)) return "{}";
            // Headers arrive as {"a":"b"}; rewrite to [{"key":"a","value":"b"}].
            return RewriteObjectMapField(json, "headers");
        }

        public static string NormalizeReport(string json)
        {
            if (string.IsNullOrEmpty(json)) return "{}";
            return RewriteObjectMapField(json, "attributes");
        }

        static string RewriteObjectMapField(string json, string field)
        {
            var key = "\"" + field + "\":";
            var idx = json.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return json;
            var start = idx + key.Length;
            while (start < json.Length && char.IsWhiteSpace(json[start])) start++;
            if (start >= json.Length || json[start] != '{') return json;
            var end = FindMatchingBrace(json, start);
            if (end < 0) return json;
            var objectJson = json.Substring(start, end - start + 1);
            if (objectJson == "{}" || objectJson == "null")
            {
                return json.Substring(0, start) + "[]" + json.Substring(end + 1);
            }
            var pairs = new List<string>();
            // Minimal object walker: "k":value pairs where value is string/number/bool/null.
            var i = 1;
            while (i < objectJson.Length - 1)
            {
                while (i < objectJson.Length && (char.IsWhiteSpace(objectJson[i]) || objectJson[i] == ',')) i++;
                if (i >= objectJson.Length - 1) break;
                if (objectJson[i] != '"')
                {
                    // Nested object/array or unexpected token — keep original JSON.
                    return json;
                }
                var kEnd = objectJson.IndexOf('"', i + 1);
                if (kEnd < 0) return json;
                var k = objectJson.Substring(i, kEnd - i + 1);
                i = kEnd + 1;
                while (i < objectJson.Length && (char.IsWhiteSpace(objectJson[i]) || objectJson[i] == ':')) i++;
                if (i >= objectJson.Length) return json;
                string v;
                if (objectJson[i] == '"')
                {
                    var vEnd = FindStringEnd(objectJson, i);
                    if (vEnd < 0) return json;
                    v = objectJson.Substring(i, vEnd - i + 1);
                    i = vEnd + 1;
                }
                else if (objectJson[i] == '{' || objectJson[i] == '[')
                {
                    // Non-scalar map values cannot round-trip via IosKv — keep original.
                    return json;
                }
                else
                {
                    var vStart = i;
                    while (i < objectJson.Length && objectJson[i] != ',' && objectJson[i] != '}') i++;
                    v = objectJson.Substring(vStart, i - vStart).Trim();
                    // Coerce non-strings to JSON strings for IosKv.value.
                    if (v != "null" && !(v.Length > 0 && v[0] == '"'))
                        v = "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
                }
                pairs.Add("{\"key\":" + k + ",\"value\":" + v + "}");
            }
            var array = "[" + string.Join(",", pairs.ToArray()) + "]";
            return json.Substring(0, start) + array + json.Substring(end + 1);
        }

        static int FindMatchingBrace(string s, int openIdx)
        {
            var depth = 0;
            var inString = false;
            for (var i = openIdx; i < s.Length; i++)
            {
                var c = s[i];
                if (inString)
                {
                    if (c == '\\' && i + 1 < s.Length) { i++; continue; }
                    if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        static int FindStringEnd(string s, int openQuote)
        {
            for (var i = openQuote + 1; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length) { i++; continue; }
                if (s[i] == '"') return i;
            }
            return -1;
        }
    }
}
#endif
