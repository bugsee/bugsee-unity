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
        static readonly Regex PhaseList = new Regex("buildPhases = \\((?<body>[^)]*)\\);");
        static readonly Regex Entry = new Regex("^\\s*(?<id>[0-9A-Fa-f]+)\\b");

        /// <summary>
        /// Drops the second and later mentions of a build phase in a target's <c>buildPhases</c> list. Unity's "Append"
        /// export (BuildOptions.AcceptExternalModificationsToPlayer) after the IL2CPP arguments changed lists the
        /// GameAssembly target's "Run Script" phase twice (seen on 2022.3 without any Bugsee package); xcodebuild then
        /// fails the archive with "Unexpected duplicate tasks". The phase object stays once, so nothing else changes.
        /// </summary>
        public static string RemoveDuplicates(string pbxproj, out int removed)
        {
            if (pbxproj == null) throw new ArgumentNullException(nameof(pbxproj));

            int count = 0;
            string result = PhaseList.Replace(pbxproj, m =>
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var kept = new StringBuilder();
                bool changed = false;
                foreach (string line in SplitKeepingEnds(m.Groups["body"].Value))
                {
                    Match e = Entry.Match(line);
                    if (e.Success && !seen.Add(e.Groups["id"].Value))
                    {
                        changed = true;
                        count++;
                        continue;
                    }

                    kept.Append(line);
                }

                return changed ? "buildPhases = (" + kept + ");" : m.Value;
            });
            removed = count;
            return result;
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
