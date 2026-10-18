using System.Collections.Generic;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Keys of <c>Bugsee.LogException(exception, options)</c> that the native iOS SDK no longer has an equivalent for
    /// (7.0.0-beta7 removed them with BugseeExceptionLoggingOptions). The wrapper says so once instead of dropping them silently.
    /// </summary>
    public static class ExceptionOptionDrops
    {
        static readonly string[] Keys = { "labels", "includeVideo", "mergingRules" };

        /// <summary>The dropped keys present in <paramref name="options"/>, in a fixed order.</summary>
        public static List<string> PresentIn(IDictionary<string, object> options)
        {
            var found = new List<string>();
            if (options == null) return found;
            foreach (var key in Keys)
            {
                if (options.ContainsKey(key)) found.Add(key);
            }

            return found;
        }

        public static string Message(IList<string> keys) =>
            "LogException option(s) " + string.Join(", ", keys) +
            " are ignored by this iOS SDK: a logged exception takes no labels, always carries the live capture and has no merging rules.";
    }
}
