using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// The JSON the iOS bridge hands to Objective-C (launch options, event parameters, attribute values).
    /// NSJSONSerialization rejects anything invalid and the callers then fall back silently - launch options become
    /// "no options" - so every value must come out as valid JSON whatever the app put in it:
    /// control characters and unpaired surrogates are escaped, NaN/Infinity become null, an enum crosses by value
    /// (never name or ordinal), unknown types become text, keys are culture-invariant, and a self-referencing
    /// collection stops at a depth limit instead of overflowing the stack. A null value is omitted from a
    /// dictionary (an unset option falls back to the SDK's default) and written as null inside an array.
    /// </summary>
    public static class JsonWire
    {
        public const int MaxDepth = 32;

        /// <summary>A JSON object for the map, or null when it is null or empty.</summary>
        public static string Object(IDictionary<string, object> map) =>
            map == null || map.Count == 0 ? null : WriteMap(map, 0);

        /// <summary>A JSON array of strings, or null when there are none.</summary>
        public static string StringArray(IList<string> items)
        {
            if (items == null || items.Count == 0) return null;
            var parts = new List<string>(items.Count);
            foreach (var item in items)
            {
                if (item != null) parts.Add(String(item));
            }

            return parts.Count == 0 ? null : "[" + string.Join(",", parts.ToArray()) + "]";
        }

        /// <summary>One value as a JSON literal; null is "null".</summary>
        public static string Value(object value) => value == null ? "null" : Literal(value, 0);

        public static string Literal(object value) => Literal(value, 0);

        public static string String(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                        {
                            sb.Append(c).Append(s[++i]); // a valid pair is passed through
                        }
                        else if (char.IsSurrogate(c))
                        {
                            // Unpaired: UTF-8 conversion at the native boundary would corrupt it and the whole
                            // payload could be dropped, so write it as an escape.
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }

                        break;
                }
            }

            return sb.Append('"').ToString();
        }

        // Typed and non-generic dictionaries are walked without reflection: IL2CPP managed stripping may remove
        // KeyValuePair<,>.get_Key/get_Value when only reflection reaches them, which would turn every payload into {}.
        static string WriteMap(IDictionary<string, object> map, int depth)
        {
            var parts = new List<string>(map.Count);
            foreach (var kv in map) AddEntry(parts, kv.Key, kv.Value, depth);
            return "{" + string.Join(",", parts.ToArray()) + "}";
        }

        static string WriteMap(IDictionary map, int depth)
        {
            var parts = new List<string>(map.Count);
            var e = map.GetEnumerator();
            while (e.MoveNext()) AddEntry(parts, e.Key, e.Value, depth);
            return "{" + string.Join(",", parts.ToArray()) + "}";
        }

        static void AddEntry(List<string> parts, object key, object value, int depth)
        {
            if (key == null || value == null) return;
            parts.Add(String(Convert.ToString(key, CultureInfo.InvariantCulture)) + ":" + Literal(value, depth + 1));
        }

        static string Literal(object value, int depth)
        {
            if (value == null) return "null";
            if (depth > MaxDepth) return "null"; // a collection that contains itself
            switch (value)
            {
                case bool b:
                    return b ? "true" : "false";
                case string s:
                    return String(s);
                case float f:
                    return float.IsNaN(f) || float.IsInfinity(f) ? "null" : f.ToString("R", CultureInfo.InvariantCulture);
                case double d:
                    return double.IsNaN(d) || double.IsInfinity(d) ? "null" : d.ToString("R", CultureInfo.InvariantCulture);
                case decimal m:
                    return m.ToString(CultureInfo.InvariantCulture);
                case sbyte _:
                case byte _:
                case short _:
                case ushort _:
                case int _:
                case uint _:
                case long _:
                case ulong _:
                    return Convert.ToString(value, CultureInfo.InvariantCulture);
                case Enum e:
                    // An enum crosses by its value, never its name or ordinal (wrapper workbook 7.4).
                    return Convert.ToString(
                        Convert.ChangeType(e, Enum.GetUnderlyingType(e.GetType()), CultureInfo.InvariantCulture),
                        CultureInfo.InvariantCulture);
                case DateTime dt:
                    return String(dt.ToString("o", CultureInfo.InvariantCulture));
                case DateTimeOffset dto:
                    return String(dto.ToString("o", CultureInfo.InvariantCulture));
                case byte[] bytes:
                    return String(Convert.ToBase64String(bytes));
                case IDictionary<string, object> nested:
                    return WriteMap(nested, depth);
                case IDictionary map:
                    return WriteMap(map, depth);
                case IEnumerable list:
                {
                    var parts = new List<string>();
                    foreach (var item in list) parts.Add(item == null ? "null" : Literal(item, depth + 1));
                    return "[" + string.Join(",", parts.ToArray()) + "]";
                }
                default:
                    // char, Guid, TimeSpan, ... : text. Written bare it made the whole payload invalid.
                    return String(Convert.ToString(value, CultureInfo.InvariantCulture));
            }
        }
    }
}
