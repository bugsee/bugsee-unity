using System;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Unity's <c>JsonUtility</c> silently drops a field whose type is the type that contains it, so the
    /// payload's <c>cause</c> (same class) never reaches the wire (seen on an iOS and an Android IL2CPP device:
    /// <c>cause</c> set in memory, absent from the JSON). The payload is therefore serialized without it and the
    /// cause's own JSON is spliced in. Pure logic so it can be unit-tested.
    /// </summary>
    public static class ManagedPayloadJson
    {
        /// <summary>
        /// <paramref name="json"/> (one serialized object) with <c>"cause": causeJson</c> appended as its last member.
        /// Returned unchanged when there is no cause or <paramref name="json"/> is not an object.
        /// </summary>
        public static string WithCause(string json, string causeJson)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(causeJson)) return json;
            var end = json.LastIndexOf('}');
            if (end < 0 || json[0] != '{') return json;
            var head = json.Substring(0, end).TrimEnd();
            var separator = head.Length > 1 ? "," : "";
            return head + separator + "\"cause\":" + causeJson + json.Substring(end);
        }
    }
}
