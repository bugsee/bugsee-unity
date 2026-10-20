using System.Collections.Generic;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Turns the iOS bridge's lifecycle payload into the managed value the public listener receives, the same
    /// types Android delivers: a string stays a string, a dictionary becomes a
    /// <c>Dictionary&lt;string, object&gt;</c>, nothing is null. The bridge sends a JSON string literal for a string,
    /// a JSON object for a dictionary and (for any other payload) a JSON string holding its description, so a
    /// dictionary that happens to hold one key cannot be mistaken for a string.
    /// </summary>
    public static class LifecyclePayload
    {
        public static object Decode(string dataJson)
        {
            if (string.IsNullOrEmpty(dataJson)) return null;
            char first = FirstNonSpace(dataJson);
            if (first == '{')
            {
                var map = JsonRead.Object(dataJson);
                return map != null ? (object)map : dataJson;
            }
            if (first == '"')
            {
                var box = JsonRead.Object("{\"v\":" + dataJson + "}");
                if (box != null && box.TryGetValue("v", out var value) && value is string text) return text;
            }
            return dataJson;
        }

        static char FirstNonSpace(string s)
        {
            foreach (char c in s)
            {
                if (c != ' ' && c != '\t' && c != '\n' && c != '\r') return c;
            }
            return '\0';
        }
    }
}
