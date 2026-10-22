using System;
using System.Collections;
using System.Collections.Generic;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Makes the launch options read the same on both platforms: integer types widen to long and float to double
    /// (Android returns Integer/Float for some defaults, iOS parses JSON as long/double), also inside nested maps and
    /// lists, and the SDK-internal keys (a <c>$$</c> name, bare or after the <c>com.bugsee.option.</c> prefix: Android's
    /// <c>$$ENDPOINT</c>, <c>$$DEBUG</c>, <c>$$WRAPPER</c>, iOS's <c>$$ENDPOINT</c>) are left out.
    /// </summary>
    public static class LaunchOptionsWire
    {
        public const string InternalKeyPrefix = "$$";
        const string OptionKeyPrefix = "com.bugsee.option.";

        /// <summary>True for a hidden SDK-internal key: <c>$$X</c> or <c>com.bugsee.option.$$X</c>.</summary>
        public static bool IsInternalKey(string key)
        {
            if (key == null) return false;
            if (key.StartsWith(InternalKeyPrefix, StringComparison.Ordinal)) return true;
            return key.StartsWith(OptionKeyPrefix, StringComparison.Ordinal)
                && string.CompareOrdinal(key, OptionKeyPrefix.Length, InternalKeyPrefix, 0, InternalKeyPrefix.Length) == 0;
        }

        public static Dictionary<string, object> Normalize(IEnumerable<KeyValuePair<string, object>> options)
        {
            if (options == null) return null;
            var result = new Dictionary<string, object>();
            foreach (var kv in options)
            {
                if (kv.Key == null || IsInternalKey(kv.Key)) continue;
                result[kv.Key] = Value(kv.Value);
            }
            return result;
        }

        static object Value(object value)
        {
            switch (value)
            {
                case sbyte v: return (long)v;
                case byte v: return (long)v;
                case short v: return (long)v;
                case ushort v: return (long)v;
                case int v: return (long)v;
                case uint v: return (long)v;
                case float v: return (double)v;
                case ulong v: return v <= long.MaxValue ? (object)(long)v : (double)v;
                case string s: return s;
                case IDictionary<string, object> map:
                    var copy = new Dictionary<string, object>(map.Count);
                    foreach (var kv in map)
                        if (kv.Key != null) copy[kv.Key] = Value(kv.Value);
                    return copy;
                case IEnumerable seq:
                    var list = new List<object>();
                    foreach (var item in seq) list.Add(Value(item));
                    return list;
                default: return value;
            }
        }
    }
}
