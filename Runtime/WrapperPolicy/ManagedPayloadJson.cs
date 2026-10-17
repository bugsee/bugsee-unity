using System;
using System.Collections.Generic;
using System.Text;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Unity's <c>JsonUtility</c> silently drops a field whose type is the type that contains it, so the
    /// payload's <c>cause</c> (same class) never reaches the wire (seen on an iOS and an Android IL2CPP device:
    /// <c>cause</c> set in memory, absent from the JSON). The payload is therefore serialized level by level
    /// without it and the levels are joined here, each appended once. Pure logic so it can be unit-tested.
    ///
    /// Wire contract of one level: <c>name</c>, <c>reason</c>, <c>frames[]</c>, <c>signature</c>,
    /// <c>buildID</c>, <c>moduleUUID</c> and an optional nested <c>cause</c> of the same shape (the reason is the
    /// plain message). The worker does not read it yet.
    /// </summary>
    public static class ManagedPayloadJson
    {
        /// <summary>
        /// Most characters of serialized level JSON one payload carries in total; the first (outermost) level counts
        /// toward it but is always kept whole. The joining text (the <c>"cause":</c> prefixes and closing braces)
        /// is not counted, so the output can run a few dozen characters over. Staging accepted a 1 MB payload
        /// from an Android device, so this is not a server limit: it keeps a chain of exceptions with huge messages
        /// from multiplying a report's size by the node count.
        /// </summary>
        public const int MaxChars = 256 * 1024;

        /// <summary>
        /// The serialized objects <paramref name="levels"/> (outermost first) joined into one, each nested as the
        /// <c>cause</c> of the one before it. A <c>cause</c> member a level already has is removed first, so the key
        /// is never duplicated. A level that is not an object is skipped. Levels after the first are dropped once the total would pass <paramref name="maxChars"/>.
        /// </summary>
        public static string Chain(IList<string> levels, int maxChars = MaxChars)
        {
            if (levels == null || levels.Count == 0) return "{}";
            var sb = new StringBuilder();
            var opened = 0;
            var needsComma = false;
            for (var i = 0; i < levels.Count; i++)
            {
                var level = StripCause(levels[i]);
                var end = level == null ? -1 : level.LastIndexOf('}');
                if (end < 0 || level[0] != '{') continue;
                var body = level.Substring(0, end).TrimEnd();
                // The outermost level is always kept whole; a cause that would pass the cap, and the rest, are dropped.
                if (opened > 0 && sb.Length + body.Length > maxChars) break;
                if (opened > 0) sb.Append(needsComma ? ",\"cause\":" : "\"cause\":");
                sb.Append(body);
                needsComma = body.Length > 1;
                opened++;
            }
            if (opened == 0) return "{}";
            // Every object is still open: the members of a level are followed by its cause, then all close.
            sb.Append('}', opened);
            return sb.ToString();
        }

        /// <summary><paramref name="json"/> with <c>"cause": causeJson</c> as its last member (see <see cref="Chain"/>).</summary>
        public static string WithCause(string json, string causeJson)
        {
            if (string.IsNullOrEmpty(json) || json[0] != '{' || string.IsNullOrEmpty(causeJson)) return json;
            return Chain(new[] { json, causeJson });
        }

        /// <summary>The object without its top-level <c>cause</c> member; anything that is not an object is returned as is.</summary>
        public static string StripCause(string json)
        {
            if (string.IsNullOrEmpty(json) || json[0] != '{') return json;
            var depth = 0;
            var i = 0;
            while (i < json.Length)
            {
                var c = json[i];
                if (c == '"')
                {
                    var start = i;
                    i = SkipString(json, i);
                    if (depth == 1 && json.Substring(start, i - start) == "\"cause\"")
                    {
                        var colon = i;
                        while (colon < json.Length && char.IsWhiteSpace(json[colon])) colon++;
                        if (colon < json.Length && json[colon] == ':')
                        {
                            var valueEnd = SkipValue(json, colon + 1);
                            return Remove(json, start, valueEnd);
                        }
                    }
                    continue;
                }
                if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') depth--;
                i++;
            }
            return json;
        }

        static string Remove(string json, int memberStart, int memberEnd)
        {
            var before = json.Substring(0, memberStart).TrimEnd();
            var after = json.Substring(memberEnd).TrimStart();
            if (before.EndsWith(",", StringComparison.Ordinal)) before = before.Substring(0, before.Length - 1);
            else if (after.StartsWith(",", StringComparison.Ordinal)) after = after.Substring(1);
            return before + after;
        }

        static int SkipString(string s, int i)
        {
            i++;
            while (i < s.Length)
            {
                if (s[i] == '\\') i += 2;
                else if (s[i] == '"') return i + 1;
                else i++;
            }
            return s.Length;
        }

        static int SkipValue(string s, int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i >= s.Length) return i;
            if (s[i] == '"') return SkipString(s, i);
            if (s[i] == '{' || s[i] == '[')
            {
                var depth = 0;
                while (i < s.Length)
                {
                    var c = s[i];
                    if (c == '"') { i = SkipString(s, i); continue; }
                    if (c == '{' || c == '[') depth++;
                    else if (c == '}' || c == ']') { depth--; if (depth == 0) return i + 1; }
                    i++;
                }
                return i;
            }
            while (i < s.Length && s[i] != ',' && s[i] != '}' && !char.IsWhiteSpace(s[i])) i++;
            return i;
        }
    }
}
