#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using Bugsee.WrapperPolicy;
using UnityEngine;

namespace Bugsee.Platform.Android
{
    /// <summary>Shared JNI boxing / unboxing for attributes, maps, and lifecycle payloads.</summary>
    static class AndroidJavaConverters
    {
        public static AndroidJavaObject Box(object value)
        {
            value = AttributeWire.ToNative(value);
            if (value == null) return null;
            if (value is string s) return new AndroidJavaObject("java.lang.String", s);
            if (value is bool b) return new AndroidJavaObject("java.lang.Boolean", b);
            if (value is int i) return new AndroidJavaObject("java.lang.Integer", i);
            if (value is long l) return new AndroidJavaObject("java.lang.Long", l);
            if (value is float f) return new AndroidJavaObject("java.lang.Float", f);
            if (value is double d) return new AndroidJavaObject("java.lang.Double", d);
            if (value is byte by) return new AndroidJavaObject("java.lang.Byte", (sbyte)by);
            if (value is short sh) return new AndroidJavaObject("java.lang.Short", sh);
            return new AndroidJavaObject("java.lang.String", Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Unbox for attribute values: integers read back as long and floating point as double, the kinds iOS returns,
        /// whatever width was set.
        /// </summary>
        public static object UnboxAttribute(AndroidJavaObject javaObject) => Unbox(javaObject, true);

        public static object Unbox(AndroidJavaObject javaObject) => Unbox(javaObject, false);

        static object Unbox(AndroidJavaObject javaObject, bool widen)
        {
            if (javaObject == null) return null;

            string name;
            using (var clazz = javaObject.Call<AndroidJavaObject>("getClass"))
                name = clazz?.Call<string>("getName");

            if (string.IsNullOrEmpty(name))
                return javaObject.Call<string>("toString");

            switch (name)
            {
                case "java.lang.String":
                    return javaObject.Call<string>("toString");
                case "java.lang.Boolean":
                    return javaObject.Call<bool>("booleanValue");
                case "java.lang.Integer":
                    return widen ? (object)(long)javaObject.Call<int>("intValue") : javaObject.Call<int>("intValue");
                case "java.lang.Long":
                    return javaObject.Call<long>("longValue");
                case "java.lang.Float":
                    return widen ? (object)(double)javaObject.Call<float>("floatValue") : javaObject.Call<float>("floatValue");
                case "java.lang.Double":
                    return javaObject.Call<double>("doubleValue");
                case "java.lang.Byte":
                    return widen ? (object)(long)(byte)javaObject.Call<sbyte>("byteValue") : (byte)javaObject.Call<sbyte>("byteValue");
                case "java.lang.Short":
                    return widen ? (object)(long)javaObject.Call<short>("shortValue") : javaObject.Call<short>("shortValue");
                case "java.lang.Character":
                    return javaObject.Call<char>("charValue");
            }

            if (LooksLikeMap(name) || HasEntrySet(javaObject))
                return MapToDictionary(javaObject, widen);

            // Lifecycle payloads are typically String snapshot/request ids;
            // other objects fall back to toString for a usable managed value.
            return javaObject.Call<string>("toString");
        }

        public static object UnboxLifecycleData(AndroidJavaObject data) => Unbox(data);

        public static Dictionary<string, object> MapToDictionary(AndroidJavaObject map) => MapToDictionary(map, false);

        public static Dictionary<string, object> AttributesToDictionary(AndroidJavaObject map) => MapToDictionary(map, true);

        static Dictionary<string, object> MapToDictionary(AndroidJavaObject map, bool widen)
        {
            var result = new Dictionary<string, object>();
            if (map == null) return result;

            using (var entrySet = map.Call<AndroidJavaObject>("entrySet"))
            using (var iterator = entrySet.Call<AndroidJavaObject>("iterator"))
            {
                while (iterator.Call<bool>("hasNext"))
                {
                    using (var entry = iterator.Call<AndroidJavaObject>("next"))
                    {
                        var key = entry.Call<AndroidJavaObject>("getKey")?.Call<string>("toString");
                        if (string.IsNullOrEmpty(key)) continue;
                        result[key] = Unbox(entry.Call<AndroidJavaObject>("getValue"), widen);
                    }
                }
            }

            return result;
        }

        public static Dictionary<string, string> MapToStringDictionary(AndroidJavaObject map)
        {
            var result = new Dictionary<string, string>();
            if (map == null) return result;

            using (var entrySet = map.Call<AndroidJavaObject>("entrySet"))
            using (var iterator = entrySet.Call<AndroidJavaObject>("iterator"))
            {
                while (iterator.Call<bool>("hasNext"))
                {
                    using (var entry = iterator.Call<AndroidJavaObject>("next"))
                    {
                        var key = entry.Call<AndroidJavaObject>("getKey")?.Call<string>("toString");
                        if (string.IsNullOrEmpty(key)) continue;
                        result[key] = entry.Call<AndroidJavaObject>("getValue")?.Call<string>("toString");
                    }
                }
            }

            return result;
        }

        public static AndroidJavaObject StringDictionaryToMap(IDictionary<string, string> dict)
        {
            var map = new AndroidJavaObject("java.util.HashMap");
            if (dict == null) return map;
            foreach (var kv in dict)
            {
                if (kv.Key == null) continue;
                using (var key = new AndroidJavaObject("java.lang.String", kv.Key))
                using (var value = kv.Value == null
                           ? null
                           : new AndroidJavaObject("java.lang.String", kv.Value))
                {
                    map.Call<AndroidJavaObject>("put", key, value);
                }
            }
            return map;
        }

        public static AndroidJavaObject ObjectDictionaryToMap(IDictionary<string, object> dict)
        {
            var map = new AndroidJavaObject("java.util.HashMap");
            if (dict == null) return map;
            foreach (var kv in dict)
            {
                if (kv.Key == null) continue;
                using (var key = new AndroidJavaObject("java.lang.String", kv.Key))
                using (var value = Box(kv.Value))
                {
                    map.Call<AndroidJavaObject>("put", key, value);
                }
            }
            return map;
        }

        public static sbyte[] ToSBytes(byte[] data)
        {
            if (data == null) return null;
            var signed = new sbyte[data.Length];
            Buffer.BlockCopy(data, 0, signed, 0, data.Length);
            return signed;
        }

        static bool LooksLikeMap(string className) =>
            className == "java.util.HashMap"
            || className == "java.util.LinkedHashMap"
            || className == "java.util.TreeMap"
            || className == "java.util.ConcurrentHashMap"
            || (className != null && className.IndexOf("Map", StringComparison.Ordinal) >= 0
                && className.StartsWith("java.util.", StringComparison.Ordinal));

        static bool HasEntrySet(AndroidJavaObject javaObject)
        {
            try
            {
                using (var entrySet = javaObject.Call<AndroidJavaObject>("entrySet"))
                    return entrySet != null;
            }
            catch
            {
                return false;
            }
        }
    }
}
#endif
