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

        /// <summary>Most values (entries, elements and scalars together) <see cref="Bounded"/> will visit.</summary>
        public const int MaxNodes = 4096;

        /// <summary>
        /// A map, list or array as JSON no longer than <paramref name="maxChars"/>. Always valid JSON: when the next
        /// entry or element would not fit (or <see cref="MaxNodes"/> values were visited) the rest is dropped whole,
        /// never cut mid-token, so <paramref name="truncated"/> tells the text is a prefix of the data. A collection
        /// that contains itself writes null where it repeats. Other enumerables (a Unity Transform walks its
        /// children) are written as text, not iterated. Null map values are omitted, as in <see cref="Value"/>.
        /// </summary>
        public static string Bounded(object container, int maxChars, out bool truncated)
        {
            var state = new BoundState();
            var text = BoundedValue(container, 0, maxChars, state) ?? "null";
            truncated = state.Truncated;
            return text;
        }

        /// <summary>True for the values <see cref="Bounded"/> writes as a JSON object or array.</summary>
        public static bool IsContainer(object value)
        {
            if (value == null || value is string || value is byte[]) return false;
            if (value is IDictionary || value is ICollection || value is IDictionary<string, object>) return true;
            foreach (var face in value.GetType().GetInterfaces())
            {
                if (!face.IsGenericType) continue;
                var def = face.GetGenericTypeDefinition();
                if (def == typeof(ICollection<>) || def == typeof(IReadOnlyCollection<>)) return true;
            }

            return false;
        }

        sealed class BoundState
        {
            public int Nodes;
            public bool Truncated;
            public readonly List<object> Path = new List<object>();
        }

        // Returns null when the value does not fit in budget (the caller drops it and marks the result truncated).
        static string BoundedValue(object value, int depth, int budget, BoundState state)
        {
            if (value == null) return Fit("null", budget);
            if (++state.Nodes > MaxNodes) { state.Truncated = true; return null; }
            if (!IsContainer(value))
            {
                string leaf = value is IEnumerable && !(value is string) && !(value is byte[])
                    ? String(Convert.ToString(value, CultureInfo.InvariantCulture))
                    : Literal(value, 0);
                return Fit(leaf, budget);
            }

            if (depth > MaxDepth) return Fit("null", budget);
            foreach (var seen in state.Path)
                if (ReferenceEquals(seen, value)) return Fit("null", budget);

            state.Path.Add(value);
            try
            {
                var map = value as IDictionary ?? (value is IDictionary<string, object> typed ? new TypedMap(typed) : null);
                var sb = new StringBuilder(map != null ? "{" : "[");
                var closer = map != null ? '}' : ']';
                var first = true;
                IDictionaryEnumerator de = map?.GetEnumerator();
                IEnumerator plain = map == null ? ((IEnumerable)value).GetEnumerator() : null;
                while (map != null ? de.MoveNext() : plain.MoveNext())
                {
                    object item = map != null ? de.Value : plain.Current;
                    if (map != null && (de.Key == null || item == null)) continue;
                    var prefix = (first ? "" : ",")
                        + (map != null ? String(Convert.ToString(de.Key, CultureInfo.InvariantCulture)) + ":" : "");
                    int room = budget - sb.Length - prefix.Length - 1;
                    var before = state.Truncated;
                    var text = room > 0 ? BoundedValue(item, depth + 1, room, state) : null;
                    if (text == null || text.Length > room) { state.Truncated = true; break; }
                    sb.Append(prefix).Append(text);
                    first = false;
                    // A shortened child ends this level too, so what is written stays a prefix of the data.
                    if (!before && state.Truncated) break;
                }
                return sb.Append(closer).ToString();
            }
            finally { state.Path.RemoveAt(state.Path.Count - 1); }
        }

        // Adapts IDictionary<string, object> (which is not the non-generic IDictionary) to the walk above.
        sealed class TypedMap : IDictionary
        {
            readonly IDictionary<string, object> _map;
            public TypedMap(IDictionary<string, object> map) { _map = map; }
            public IDictionaryEnumerator GetEnumerator() => new Walker(_map);
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            public int Count => _map.Count;
            public bool IsReadOnly => true;
            public bool IsFixedSize => true;
            public bool IsSynchronized => false;
            public object SyncRoot => this;
            public ICollection Keys => throw new NotSupportedException();
            public ICollection Values => throw new NotSupportedException();
            public object this[object key] { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public bool Contains(object key) => throw new NotSupportedException();
            public void Add(object key, object value) => throw new NotSupportedException();
            public void Clear() => throw new NotSupportedException();
            public void Remove(object key) => throw new NotSupportedException();
            public void CopyTo(Array array, int index) => throw new NotSupportedException();

            sealed class Walker : IDictionaryEnumerator
            {
                readonly IEnumerator<KeyValuePair<string, object>> _e;
                public Walker(IDictionary<string, object> map) { _e = map.GetEnumerator(); }
                public bool MoveNext() => _e.MoveNext();
                public void Reset() => throw new NotSupportedException();
                public object Current => Entry;
                public DictionaryEntry Entry => new DictionaryEntry(_e.Current.Key, _e.Current.Value);
                public object Key => _e.Current.Key;
                public object Value => _e.Current.Value;
            }
        }

        static string Fit(string text, int budget) => text.Length <= budget ? text : null;

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
