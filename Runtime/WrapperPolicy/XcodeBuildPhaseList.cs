using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Text edit on a serialized Xcode project (project.pbxproj): a build phase listed twice in one target.
    /// </summary>
    public static class XcodeBuildPhaseList
    {
        static readonly Regex Entry = new Regex("^\\s*(?<id>[0-9A-Fa-f]+)\\b");

        /// <summary>
        /// Drops the second and later mentions of a build phase in a target's <c>buildPhases</c> list (the first one keeps
        /// its place). Unity's "Append" export (BuildOptions.AcceptExternalModificationsToPlayer) after the IL2CPP arguments
        /// changed lists the GameAssembly target's "Run Script" phase twice, the same id again (seen on 2022.3 without any
        /// Bugsee package); xcodebuild then fails the archive with "Unexpected duplicate tasks". The phase object stays
        /// once, so nothing else changes. Known limit: only a repeated id is removed; if Unity ever created a second phase
        /// with a new id, it is not recognised. Other lists (files, children) are left alone.
        /// </summary>
        public static string RemoveDuplicates(string pbxproj, out int removed)
        {
            if (pbxproj == null) throw new ArgumentNullException(nameof(pbxproj));

            int count = 0;
            var result = new StringBuilder(pbxproj.Length);
            HashSet<string> seen = null;
            foreach (string line in SplitKeepingEnds(pbxproj))
            {
                string trimmed = line.Trim();
                if (seen == null)
                {
                    if (trimmed.StartsWith("buildPhases = (", StringComparison.Ordinal)) seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }
                else if (trimmed == ");")
                {
                    seen = null;
                }
                else
                {
                    Match e = Entry.Match(line);
                    if (e.Success && !seen.Add(e.Groups["id"].Value))
                    {
                        count++;
                        continue;
                    }
                }

                result.Append(line);
            }

            removed = count;
            return count == 0 ? pbxproj : result.ToString();
        }

        static IEnumerable<string> SplitKeepingEnds(string text)
        {
            int start = 0;
            while (start < text.Length)
            {
                int nl = text.IndexOf('\n', start);
                int end = nl < 0 ? text.Length : nl + 1;
                yield return text.Substring(start, end - start);
                start = end;
            }
        }
    }
}
