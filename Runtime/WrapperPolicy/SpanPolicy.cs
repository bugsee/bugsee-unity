using System;
using System.Collections.Generic;
using Bugsee.Contracts.Apm;

namespace Bugsee.WrapperPolicy
{
    /// <summary>Argument rules for transactions and spans, shared by both bridges.</summary>
    public static class SpanPolicy
    {
        public static bool HasText(string value) => !string.IsNullOrEmpty(value);

        /// <summary>Android takes a nullable description only through its one-argument overload, so null crosses as empty.</summary>
        public static string Description(string description) => description ?? "";

        public static SpanStatus Normalize(SpanStatus status) =>
            Enum.IsDefined(typeof(SpanStatus), status) ? status : SpanStatus.Unknown;

        /// <summary>The Android <c>SpanStatus</c> enum constant for a status.</summary>
        public static string AndroidStatusName(SpanStatus status)
        {
            switch (Normalize(status))
            {
                case SpanStatus.Ok: return "OK";
                case SpanStatus.Error: return "ERROR";
                case SpanStatus.Timeout: return "TIMEOUT";
                case SpanStatus.Cancelled: return "CANCELLED";
                case SpanStatus.DeadlineExceeded: return "DEADLINE_EXCEEDED";
                default: return "UNKNOWN";
            }
        }

        /// <summary>
        /// The entries of <paramref name="attributes"/> that <see cref="AttributePolicy"/> accepts, or null when none
        /// are left. Each rejected entry is described in <paramref name="rejected"/> (when given) instead of throwing.
        /// </summary>
        public static Dictionary<string, object> CleanAttributes(IDictionary<string, object> attributes, ICollection<string> rejected = null)
        {
            if (attributes == null) return null;
            Dictionary<string, object> clean = null;
            foreach (var kv in attributes)
            {
                var decision = AttributePolicy.Evaluate(kv.Key, kv.Value);
                if (!decision.Accepted)
                {
                    rejected?.Add(decision.Error);
                    continue;
                }
                if (clean == null) clean = new Dictionary<string, object>();
                clean[kv.Key] = AttributeWire.ToBoxable(kv.Value);
            }
            return clean;
        }
    }
}
