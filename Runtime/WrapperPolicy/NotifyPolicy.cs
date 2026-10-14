using System.Collections.Generic;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Argument rules for <c>Bugsee.Notify</c>, shared by both bridges. Both native SDKs ignore a notification without
    /// a title (logging a warning) and take the fields as a string-to-string map.
    /// </summary>
    public static class NotifyPolicy
    {
        /// <summary>
        /// The body both bridges send: null becomes empty. A JNI call cannot pass a null to pick the Android overload,
        /// so iOS sends the same empty string and the two platforms agree.
        /// </summary>
        public static string Body(string body) => body ?? "";

        public static bool HasTitle(string title) => !string.IsNullOrEmpty(title);

        /// <summary>
        /// A copy of <paramref name="fields"/> without null keys or null values (a native string map cannot hold
        /// them), or null when nothing is left.
        /// </summary>
        public static Dictionary<string, string> CleanFields(IDictionary<string, string> fields)
        {
            if (fields == null) return null;
            Dictionary<string, string> clean = null;
            foreach (var kv in fields)
            {
                if (kv.Key == null || kv.Value == null) continue;
                if (clean == null) clean = new Dictionary<string, string>();
                clean[kv.Key] = kv.Value;
            }
            return clean;
        }

        /// <summary>The cleaned fields as the object map <see cref="JsonWire.Object"/> writes, or null when none are left.</summary>
        public static IDictionary<string, object> ToWireMap(IDictionary<string, string> fields)
        {
            var clean = CleanFields(fields);
            if (clean == null) return null;
            var map = new Dictionary<string, object>(clean.Count);
            foreach (var kv in clean) map[kv.Key] = kv.Value;
            return map;
        }
    }
}
