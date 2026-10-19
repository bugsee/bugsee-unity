using System.Collections.Generic;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// The JSON shuffling between the iOS bridge and Unity's JsonUtility, which cannot read free-form maps.
    /// Everything goes through <see cref="JsonRead"/> so escapes (\\, \n, \t, \uXXXX, ...) decode exactly once and in
    /// order, and a breadcrumb's <c>data</c> keeps its nested values and scalar kinds instead of being flattened.
    /// </summary>
    public static class IosBridgeJson
    {
        /// <summary>
        /// Rewrites <c>"field":[{"key":k,"value":v},...]</c> (what JsonUtility writes for a key/value array) back into
        /// <c>"field":{k:v,...}</c> for native. A miss on a non-empty array keeps the original JSON, so a native map is
        /// never wiped by a parse failure.
        /// </summary>
        public static string ToNativeMapJson(string json, string field)
        {
            if (string.IsNullOrEmpty(json)) return "{}";
            var key = "\"" + field + "\":";
            var idx = json.IndexOf(key, System.StringComparison.Ordinal);
            if (idx < 0) return json;
            var start = idx + key.Length;
            while (start < json.Length && char.IsWhiteSpace(json[start])) start++;
            if (start >= json.Length || json[start] != '[') return json;

            var end = start;
            var depth = 0;
            var inString = false;
            for (var i = start; i < json.Length; i++)
            {
                var c = json[i];
                if (inString)
                {
                    if (c == '\\' && i + 1 < json.Length) { i++; continue; }
                    if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == '[') depth++;
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0) { end = i; break; }
                }
            }
            if (end <= start) return json;

            var map = ArrayToObject(json.Substring(start, end - start + 1));
            if (map == null) return json;
            return json.Substring(0, start) + map + json.Substring(end + 1);
        }

        /// <summary>An object-valued field with nested values and scalar kinds intact; empty when absent or not an object.</summary>
        public static Dictionary<string, object> ReadObjectField(string json, string field)
        {
            var root = JsonRead.Object(json);
            if (root != null && root.TryGetValue(field, out var value) && value is Dictionary<string, object> map)
                return map;
            return new Dictionary<string, object>();
        }

        /// <summary>Appends <c>"field":{...}</c> to a JSON object that does not carry that field.</summary>
        public static string AppendObjectField(string json, string field, IDictionary<string, object> map)
        {
            var body = JsonWire.Object(map) ?? "{}";
            var close = json == null ? -1 : json.LastIndexOf('}');
            if (close < 0) return "{" + JsonWire.String(field) + ":" + body + "}";
            var head = json.Substring(0, close).TrimEnd();
            var sep = head.EndsWith("{", System.StringComparison.Ordinal) ? "" : ",";
            return head + sep + JsonWire.String(field) + ":" + body + "}";
        }

        /// <returns>The decoded string value of <paramref name="field"/>, or null when absent, null or not a string.</returns>
        public static string ExtractStringField(string obj, string field)
        {
            var map = JsonRead.Object(obj);
            return map != null && map.TryGetValue(field, out var value) ? value as string : null;
        }

        static string ArrayToObject(string arrayJson)
        {
            // Expect [{"key":"a","value":"b"}, ...]
            if (string.IsNullOrEmpty(arrayJson) || arrayJson == "[]") return "{}";
            var parts = new List<string>();
            var i = 1;
            while (i < arrayJson.Length - 1)
            {
                while (i < arrayJson.Length && (char.IsWhiteSpace(arrayJson[i]) || arrayJson[i] == ',')) i++;
                if (i >= arrayJson.Length - 1) break;
                if (arrayJson[i] != '{') return null;
                var objEnd = FindMatchingBrace(arrayJson, i);
                if (objEnd < 0) return null;
                var obj = arrayJson.Substring(i, objEnd - i + 1);
                var k = ExtractStringField(obj, "key");
                var v = ExtractStringField(obj, "value");
                if (k != null)
                    parts.Add(JsonWire.String(k) + ":" + (v == null ? "null" : JsonWire.String(v)));
                i = objEnd + 1;
            }
            // Leftover non-whitespace after the loop means an incomplete parse.
            while (i < arrayJson.Length - 1 && char.IsWhiteSpace(arrayJson[i])) i++;
            if (i < arrayJson.Length - 1) return null;
            if (parts.Count == 0) return null;
            return "{" + string.Join(",", parts.ToArray()) + "}";
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
    }
}
