using System;
using System.Collections.Generic;
using System.Text;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Text edits on Unity's generated Gradle project that make it pull the Bugsee Android SDK from Maven
    /// without EDM4U or custom Gradle templates: the module dependencies go into unityLibrary/build.gradle,
    /// and <c>mavenCentral()</c> is added to the project repositories when the generated layout lacks it.
    ///
    /// Every edit is marked, so applying it again (a second build, an append export) changes nothing.
    /// A Bugsee dependency the project already declares itself (EDM4U, a custom mainTemplate.gradle, a
    /// local AAR) wins: nothing is injected next to it, and a version that differs from the package pin
    /// is reported.
    /// </summary>
    public static class GradleMavenInjection
    {
        internal const string DependenciesBeginMarker = "// bugsee:maven-dependencies begin";
        internal const string DependenciesEndMarker = "// bugsee:maven-dependencies end";
        internal const string RepositoryMarker = "// bugsee:maven-repository";

        const string CoreArtifactToken = "bugsee-android";
        const string NdkArtifactToken = "bugsee-android-ndk";

        public enum DependencyStatus
        {
            /// <summary>The marked dependencies were inserted or upserted.</summary>
            Injected,

            /// <summary>The marked dependencies were already present and current.</summary>
            AlreadyInjected,

            /// <summary>The project declares Bugsee itself; nothing was injected.</summary>
            DeclaredByProject,
        }

        public enum RepositoryStatus
        {
            /// <summary>The block was not found in this file.</summary>
            BlockNotFound,

            /// <summary><c>mavenCentral()</c> is already declared in the block.</summary>
            AlreadyPresent,

            /// <summary><c>mavenCentral()</c> was inserted.</summary>
            Inserted,
        }

        public sealed class DependencyResult
        {
            public string Text;
            public DependencyStatus Status;
            public readonly List<string> Warnings = new List<string>();
        }

        /// <summary>
        /// Declares <paramref name="group"/>:<paramref name="artifacts"/>:<paramref name="version"/> in the
        /// top-level <c>dependencies</c> block of a module build.gradle (a new block is appended when there
        /// is none).
        /// </summary>
        public static DependencyResult ApplyDependencies(
            string buildGradle,
            string group,
            IList<string> artifacts,
            string version)
        {
            if (buildGradle == null)
            {
                throw new ArgumentNullException(nameof(buildGradle));
            }

            if (string.IsNullOrEmpty(group))
            {
                throw new ArgumentException("Maven group is required.", nameof(group));
            }

            if (artifacts == null || artifacts.Count == 0)
            {
                throw new ArgumentException("At least one artifact is required.", nameof(artifacts));
            }

            if (string.IsNullOrEmpty(version))
            {
                throw new ArgumentException("Version is required.", nameof(version));
            }

            var result = new DependencyResult();
            var lines = SplitLines(buildGradle, out string newline);
            bool trailingNewline = buildGradle.EndsWith("\n", StringComparison.Ordinal);

            bool hasRegion = TryFindDependencyRegion(lines, out int regionStart, out int regionEnd);
            var foreign = FindProjectDeclarations(lines, hasRegion ? regionStart : -1, hasRegion ? regionEnd : -1);

            if (foreign.Count > 0)
            {
                result.Status = DependencyStatus.DeclaredByProject;
                if (hasRegion)
                {
                    // An earlier run injected next to what is now a project declaration: the project wins.
                    lines.RemoveRange(regionStart, regionEnd - regionStart + 1);
                }

                ReportProjectDeclarations(result, foreign, version);
                result.Text = hasRegion ? JoinLines(lines, newline, trailingNewline) : buildGradle;
                return result;
            }

            if (hasRegion)
            {
                string indent = LeadingWhitespace(lines[regionStart]);
                var replacement = BuildRegion(indent, group, artifacts, version);
                if (RangeEquals(lines, regionStart, regionEnd, replacement))
                {
                    result.Status = DependencyStatus.AlreadyInjected;
                    result.Text = buildGradle;
                    return result;
                }

                lines.RemoveRange(regionStart, regionEnd - regionStart + 1);
                lines.InsertRange(regionStart, replacement);
                result.Status = DependencyStatus.Injected;
                result.Text = JoinLines(lines, newline, trailingNewline);
                return result;
            }

            int open = FindTopLevelBlockOpen(lines, "dependencies");
            if (open >= 0 && IsSingleLineBlock(lines[open]))
            {
                // `dependencies { ... }` on one line: leave it alone and declare a block of our own.
                open = -1;
            }

            if (open >= 0)
            {
                string indent = LeadingWhitespace(lines[open]) + "    ";
                lines.InsertRange(open + 1, BuildRegion(indent, group, artifacts, version));
            }
            else
            {
                if (lines.Count > 0 && lines[lines.Count - 1].Length == 0)
                {
                    lines.RemoveAt(lines.Count - 1);
                }

                lines.Add(string.Empty);
                lines.Add("dependencies {");
                lines.AddRange(BuildRegion("    ", group, artifacts, version));
                lines.Add("}");
                lines.Add(string.Empty);
                trailingNewline = false;
            }

            result.Status = DependencyStatus.Injected;
            result.Text = JoinLines(lines, newline, trailingNewline);
            return result;
        }

        /// <summary>
        /// Makes sure the <c>repositories</c> block nested in the top-level <paramref name="outerBlock"/>
        /// (for example <c>dependencyResolutionManagement</c> in settings.gradle or <c>allprojects</c> in the
        /// root build.gradle) declares <c>mavenCentral()</c>.
        /// </summary>
        public static RepositoryStatus EnsureMavenCentral(string gradle, string outerBlock, out string updated)
        {
            if (gradle == null)
            {
                throw new ArgumentNullException(nameof(gradle));
            }

            if (string.IsNullOrEmpty(outerBlock))
            {
                throw new ArgumentException("Outer block name is required.", nameof(outerBlock));
            }

            updated = gradle;
            var lines = SplitLines(gradle, out string newline);
            int outerOpen = FindTopLevelBlockOpen(lines, outerBlock);
            if (outerOpen < 0)
            {
                return RepositoryStatus.BlockNotFound;
            }

            int outerClose = FindBlockClose(lines, outerOpen);
            int reposOpen = FindChildBlockOpen(lines, outerOpen, outerClose, "repositories");
            if (reposOpen < 0)
            {
                return RepositoryStatus.BlockNotFound;
            }

            int reposClose = FindBlockClose(lines, reposOpen);
            for (int i = reposOpen + 1; i < reposClose; i++)
            {
                if (DeclaresMavenCentral(lines[i]))
                {
                    return RepositoryStatus.AlreadyPresent;
                }
            }

            string indent = LeadingWhitespace(lines[reposOpen]) + "    ";
            lines.Insert(reposOpen + 1, indent + "mavenCentral() " + RepositoryMarker);
            updated = JoinLines(lines, newline, gradle.EndsWith("\n", StringComparison.Ordinal));
            return RepositoryStatus.Inserted;
        }

        static List<string> BuildRegion(string indent, string group, IList<string> artifacts, string version)
        {
            var region = new List<string> { indent + DependenciesBeginMarker + " (com.bugsee.unity)" };
            for (int i = 0; i < artifacts.Count; i++)
            {
                region.Add(indent + "implementation '" + group + ":" + artifacts[i] + ":" + version + "'");
            }

            region.Add(indent + DependenciesEndMarker);
            return region;
        }

        static bool TryFindDependencyRegion(List<string> lines, out int start, out int end)
        {
            start = -1;
            end = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].IndexOf(DependenciesBeginMarker, StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                for (int j = i; j < lines.Count; j++)
                {
                    if (lines[j].IndexOf(DependenciesEndMarker, StringComparison.Ordinal) >= 0)
                    {
                        start = i;
                        end = j;
                        return true;
                    }
                }

                // A begin marker without an end: treat the begin line alone as the region.
                start = i;
                end = i;
                return true;
            }

            return false;
        }

        sealed class Declaration
        {
            public bool IsNdk;
            public string Version;
            public string Line;
        }

        static List<Declaration> FindProjectDeclarations(List<string> lines, int skipStart, int skipEnd)
        {
            var found = new List<Declaration>();
            bool inBlockComment = false;
            for (int i = 0; i < lines.Count; i++)
            {
                string code = StripComments(lines[i], ref inBlockComment);
                if (i >= skipStart && i <= skipEnd)
                {
                    continue;
                }

                int search = 0;
                while (search < code.Length)
                {
                    int at = code.IndexOf(CoreArtifactToken, search, StringComparison.Ordinal);
                    if (at < 0)
                    {
                        break;
                    }

                    search = at + CoreArtifactToken.Length;
                    if (at > 0 && IsArtifactChar(code[at - 1]))
                    {
                        continue;
                    }

                    var declaration = Classify(code, at);
                    if (declaration != null)
                    {
                        declaration.Line = lines[i].Trim();
                        found.Add(declaration);
                    }
                }
            }

            return found;
        }

        /// <summary>
        /// <c>bugsee-android</c> or <c>bugsee-android-ndk</c>, as a Maven coordinate
        /// (<c>com.bugsee:bugsee-android:7.4.1</c>), a map notation (<c>name: 'bugsee-android'</c>) or a local
        /// AAR (<c>bugsee-android-7.4.1</c>). Other artifacts (<c>bugsee-android-feedback</c>) are not ours to
        /// manage.
        /// </summary>
        static Declaration Classify(string code, int at)
        {
            int after = at + CoreArtifactToken.Length;
            bool isNdk = string.CompareOrdinal(code, at, NdkArtifactToken, 0, NdkArtifactToken.Length) == 0 &&
                         (at + NdkArtifactToken.Length >= code.Length ||
                          !IsArtifactChar(code[at + NdkArtifactToken.Length]) ||
                          code[at + NdkArtifactToken.Length] == '-');
            if (isNdk)
            {
                after = at + NdkArtifactToken.Length;
            }
            else if (after < code.Length && code[after] == '-' &&
                     !(after + 1 < code.Length && char.IsDigit(code[after + 1])))
            {
                return null;
            }
            else if (after < code.Length && (char.IsLetterOrDigit(code[after]) || code[after] == '_'))
            {
                return null;
            }

            return new Declaration { IsNdk = isNdk, Version = ReadVersion(code, after) };
        }

        static string ReadVersion(string code, int after)
        {
            if (after >= code.Length || (code[after] != ':' && code[after] != '-'))
            {
                return null;
            }

            int start = after + 1;
            int end = start;
            while (end < code.Length && IsVersionChar(code[end]))
            {
                end++;
            }

            if (end == start || (!char.IsDigit(code[start]) && code[start] != '+'))
            {
                return null;
            }

            string version = code.Substring(start, end - start);
            if (version.EndsWith(".aar", StringComparison.OrdinalIgnoreCase))
            {
                version = version.Substring(0, version.Length - 4);
            }

            return version;
        }

        static void ReportProjectDeclarations(DependencyResult result, List<Declaration> found, string version)
        {
            bool hasCore = false;
            bool hasNdk = false;
            var reported = new HashSet<string>(StringComparer.Ordinal);
            foreach (var declaration in found)
            {
                hasCore |= !declaration.IsNdk;
                hasNdk |= declaration.IsNdk;
                if (declaration.Version == null ||
                    string.Equals(declaration.Version, version, StringComparison.Ordinal) ||
                    !reported.Add(declaration.Line))
                {
                    continue;
                }

                result.Warnings.Add(
                    "The Gradle project already declares " + declaration.Line + ", which differs from the " +
                    version + " this com.bugsee.unity version is built and tested against. The project's " +
                    "declaration is kept; align it with " + version + " (or remove it and let the package add it).");
            }

            if (hasCore && !hasNdk)
            {
                result.Warnings.Add(
                    "The Gradle project declares bugsee-android but not bugsee-android-ndk, so native (NDK) " +
                    "crashes are not captured. Declare bugsee-android-ndk at the same version, or remove the " +
                    "project's Bugsee declaration and let the package add both.");
            }
            else if (hasNdk && !hasCore)
            {
                result.Warnings.Add(
                    "The Gradle project declares bugsee-android-ndk but not bugsee-android. Declare both, or " +
                    "remove the project's Bugsee declaration and let the package add both.");
            }
        }

        static bool DeclaresMavenCentral(string line)
        {
            bool inBlockComment = false;
            string code = StripComments(line, ref inBlockComment);
            return code.IndexOf("mavenCentral()", StringComparison.Ordinal) >= 0 ||
                   code.IndexOf("repo.maven.apache.org/maven2", StringComparison.Ordinal) >= 0 ||
                   code.IndexOf("repo1.maven.org/maven2", StringComparison.Ordinal) >= 0;
        }

        /// <summary>Index of the line opening a depth-0 <c>name {</c> block, or -1.</summary>
        static int FindTopLevelBlockOpen(List<string> lines, string name)
        {
            int depth = 0;
            bool inBlockComment = false;
            for (int i = 0; i < lines.Count; i++)
            {
                bool wasInComment = inBlockComment;
                string code = StripComments(lines[i], ref inBlockComment);
                if (depth == 0 && !wasInComment && OpensBlock(code, name))
                {
                    return i;
                }

                depth += BraceDelta(code);
            }

            return -1;
        }

        /// <summary>Index of the line opening <c>name {</c> directly inside the block opened at <paramref name="open"/>.</summary>
        static int FindChildBlockOpen(List<string> lines, int open, int close, string name)
        {
            bool inBlockComment = false;
            int depth = BraceDelta(StripComments(lines[open], ref inBlockComment));
            for (int i = open + 1; i < close; i++)
            {
                bool wasInComment = inBlockComment;
                string code = StripComments(lines[i], ref inBlockComment);
                if (depth == 1 && !wasInComment && OpensBlock(code, name))
                {
                    return i;
                }

                depth += BraceDelta(code);
            }

            return -1;
        }

        /// <summary>Index of the line where the block opened at <paramref name="open"/> closes.</summary>
        static int FindBlockClose(List<string> lines, int open)
        {
            int depth = 0;
            bool inBlockComment = false;
            for (int i = open; i < lines.Count; i++)
            {
                depth += BraceDelta(StripComments(lines[i], ref inBlockComment));
                if (depth <= 0)
                {
                    return i;
                }
            }

            return lines.Count - 1;
        }

        static bool OpensBlock(string code, string name)
        {
            string trimmed = code.Trim();
            if (!trimmed.StartsWith(name, StringComparison.Ordinal))
            {
                return false;
            }

            string rest = trimmed.Substring(name.Length).TrimStart();
            return rest.StartsWith("{", StringComparison.Ordinal);
        }

        static bool IsSingleLineBlock(string line)
        {
            bool inBlockComment = false;
            string code = StripComments(line, ref inBlockComment);
            int open = code.IndexOf('{');
            return open >= 0 && code.IndexOf('}', open) >= 0;
        }

        static int BraceDelta(string code)
        {
            int delta = 0;
            foreach (char c in code)
            {
                if (c == '{')
                {
                    delta++;
                }
                else if (c == '}')
                {
                    delta--;
                }
            }

            return delta;
        }

        /// <summary>
        /// The code part of a line: comments removed, string literal contents kept (they hold the
        /// coordinates) but their braces blanked so they do not count as structure.
        /// </summary>
        static string StripComments(string line, ref bool inBlockComment)
        {
            var sb = new StringBuilder(line.Length);
            char quote = '\0';
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inBlockComment)
                {
                    if (c == '*' && i + 1 < line.Length && line[i + 1] == '/')
                    {
                        inBlockComment = false;
                        i++;
                    }

                    continue;
                }

                if (quote != '\0')
                {
                    if (c == '\\' && i + 1 < line.Length)
                    {
                        sb.Append(c).Append(line[i + 1]);
                        i++;
                        continue;
                    }

                    if (c == quote)
                    {
                        quote = '\0';
                    }

                    sb.Append(c == '{' || c == '}' ? ' ' : c);
                    continue;
                }

                if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
                {
                    break;
                }

                if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
                {
                    inBlockComment = true;
                    i++;
                    continue;
                }

                if (c == '\'' || c == '"')
                {
                    quote = c;
                }

                sb.Append(c);
            }

            return sb.ToString();
        }

        static bool IsArtifactChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.';
        }

        static bool IsVersionChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_' || c == '+';
        }

        static string LeadingWhitespace(string line)
        {
            int i = 0;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
            {
                i++;
            }

            return line.Substring(0, i);
        }

        static bool RangeEquals(List<string> lines, int start, int end, List<string> replacement)
        {
            if (end - start + 1 != replacement.Count)
            {
                return false;
            }

            for (int i = 0; i < replacement.Count; i++)
            {
                if (!string.Equals(lines[start + i], replacement[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        static List<string> SplitLines(string text, out string newline)
        {
            newline = text.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
            var lines = new List<string>(text.Replace("\r\n", "\n").Split('\n'));
            if (text.EndsWith("\n", StringComparison.Ordinal))
            {
                lines.RemoveAt(lines.Count - 1);
            }

            return lines;
        }

        static string JoinLines(List<string> lines, string newline, bool trailingNewline)
        {
            string joined = string.Join(newline, lines);
            return trailingNewline ? joined + newline : joined;
        }
    }
}
