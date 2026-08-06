#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Bugsee.Platform.Android
{
    /// <summary>Shared JNI boxing / unboxing for attributes, maps, and lifecycle payloads.</summary>
    static class AndroidJavaConverters
    {
        public static AndroidJavaObject Box(object value)
        {
            if (value == null) return null;
            if (value is string s) return new AndroidJavaObject("java.lang.String", s);
            if (value is bool b) return new AndroidJavaObject("java.lang.Boolean", b);
            if (value is int i) return new AndroidJavaObject("java.lang.Integer", i);
            if (value is long l) return new AndroidJavaObject("java.lang.Long", l);
            if (value is float f) return new AndroidJavaObject("java.lang.Float", f);
            if (value is double d) return new AndroidJavaObject("java.lang.Double", d);
            if (value is byte by) return new AndroidJavaObject("java.lang.Byte", (sbyte)by);
            if (value is short sh) return new AndroidJavaObject("java.lang.Short", sh);
            return new AndroidJavaObject("java.lang.String", value.ToString());
        }

        public static object Unbox(AndroidJavaObject javaObject)
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
                    return javaObject.Call<int>("intValue");
                case "java.lang.Long":
                    return javaObject.Call<long>("longValue");
                case "java.lang.Float":
                    return javaObject.Call<float>("floatValue");
                case "java.lang.Double":
                    return javaObject.Call<double>("doubleValue");
                case "java.lang.Byte":
                    return (byte)javaObject.Call<sbyte>("byteValue");
                case "java.lang.Short":
                    return javaObject.Call<short>("shortValue");
                case "java.lang.Character":
                    return javaObject.Call<char>("charValue");
            }

            if (LooksLikeMap(name) || HasEntrySet(javaObject))
                return MapToDictionary(javaObject);

            // Lifecycle payloads are typically String snapshot/request ids;
            // other objects fall back to toString for a usable managed value.
            return javaObject.Call<string>("toString");
        }

        public static object UnboxLifecycleData(AndroidJavaObject data) => Unbox(data);

        public static Dictionary<string, object> MapToDictionary(AndroidJavaObject map)
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
                        result[key] = Unbox(entry.Call<AndroidJavaObject>("getValue"));
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
