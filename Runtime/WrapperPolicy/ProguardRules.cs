using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Puts the wrapper's keep rules into the generated project's consumer rules file, between markers, so that
    /// applying them twice (or after the rules changed) leaves exactly one current copy.
    /// </summary>
    public static class ProguardRules
    {
        public const string BeginMarker = "# bugsee:proguard begin (managed by com.bugsee.unity; do not edit)";
        public const string EndMarker = "# bugsee:proguard end";

        /// <summary>True when a library module's build script lists proguard-unity.txt as consumer rules (Groovy or Kotlin DSL).</summary>
        public static bool DeclaresConsumerRules(string gradleScript) =>
            gradleScript != null && Regex.IsMatch(gradleScript, @"consumerProguardFiles[^\n]*proguard-unity\.txt");

        public static string Apply(string existing, string rules)
        {
            if (existing == null) throw new ArgumentNullException(nameof(existing));
            if (rules == null) throw new ArgumentNullException(nameof(rules));

            string newline = existing.Contains("\r\n") ? "\r\n" : "\n";
            string block = BeginMarker + newline
                + rules.Replace("\r\n", "\n").Trim('\n').Replace("\n", newline) + newline
                + EndMarker + newline;

            int begin = existing.IndexOf(BeginMarker, StringComparison.Ordinal);
            if (begin >= 0)
            {
                int end = existing.IndexOf(EndMarker, begin, StringComparison.Ordinal);
                if (end >= 0)
                {
                    int after = end + EndMarker.Length;
                    if (existing.Substring(after).StartsWith(newline, StringComparison.Ordinal)) after += newline.Length;
                    return existing.Substring(0, begin) + block + existing.Substring(after);
                }

                // A begin marker whose end marker is gone (a hand edit): what follows it may be someone else's
                // rules, so only the dangling marker line is dropped and a complete block is appended.
                int lineEnd = existing.IndexOf('\n', begin);
                existing = existing.Substring(0, begin) + (lineEnd < 0 ? "" : existing.Substring(lineEnd + 1));
            }

            var sb = new StringBuilder(existing);
            if (existing.Length > 0 && !existing.EndsWith("\n", StringComparison.Ordinal)) sb.Append(newline);
            sb.Append(block);
            return sb.ToString();
        }
    }
}
