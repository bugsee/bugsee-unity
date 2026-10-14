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

        /// <summary>The syntax of the Gradle file being edited.</summary>
        public enum GradleDsl
        {
            /// <summary><c>build.gradle</c>: <c>implementation 'g:a:v'</c>.</summary>
            Groovy,

            /// <summary><c>build.gradle.kts</c>: <c>implementation("g:a:v")</c>.</summary>
            Kotlin,
        }

        /// <summary>How <c>dependencyResolutionManagement</c> treats repositories declared by projects.</summary>
        public enum RepositoriesMode
        {
            /// <summary>No <c>repositoriesMode</c> is set; Gradle's default, <c>PREFER_PROJECT</c>, applies.</summary>
            Unspecified,
            PreferSettings,
            PreferProject,
            FailOnProjectRepos,
        }

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
            return ApplyDependencies(buildGradle, group, artifacts, version, GradleDsl.Groovy);
        }

        /// <summary>The DSL a Gradle file is written in, by its file name (<c>.kts</c> is Kotlin).</summary>
        public static GradleDsl DslForFile(string path)
        {
            return path != null && path.EndsWith(".kts", StringComparison.OrdinalIgnoreCase)
                ? GradleDsl.Kotlin
                : GradleDsl.Groovy;
        }

        /// <summary>
        /// As <see cref="ApplyDependencies(string, string, IList{string}, string)"/>, writing the
        /// declarations in the syntax of <paramref name="dsl"/>.
        /// </summary>
        public static DependencyResult ApplyDependencies(
            string buildGradle,
            string group,
            IList<string> artifacts,
            string version,
            GradleDsl dsl)
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
                var replacement = BuildRegion(indent, group, artifacts, version, dsl);
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
                lines.InsertRange(open + 1, BuildRegion(indent, group, artifacts, version, dsl));
            }
            else
            {
                if (lines.Count > 0 && lines[lines.Count - 1].Length == 0)
                {
                    lines.RemoveAt(lines.Count - 1);
                }

                lines.Add(string.Empty);
                lines.Add("dependencies {");
                lines.AddRange(BuildRegion("    ", group, artifacts, version, dsl));
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

        /// <summary>
        /// The <c>repositoriesMode</c> set in the top-level <c>dependencyResolutionManagement</c> block of a
        /// settings file (Groovy and Kotlin spell it alike: <c>repositoriesMode.set(...)</c> or
        /// <c>repositoriesMode = ...</c>), or <see cref="RepositoriesMode.Unspecified"/> when the block or the
        /// setting is missing.
        /// </summary>
        public static RepositoriesMode GetRepositoriesMode(string settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            var lines = SplitLines(settings, out _);
            int open = FindTopLevelBlockOpen(lines, "dependencyResolutionManagement");
            if (open < 0)
            {
                return RepositoriesMode.Unspecified;
            }

            int close = FindBlockClose(lines, open);
            var inBlockComment = new LexState();
            for (int i = open; i <= close; i++)
            {
                string code = StripComments(lines[i], ref inBlockComment);
                int at = code.IndexOf("repositoriesMode", StringComparison.Ordinal);
                if (at < 0)
                {
                    continue;
                }

                if (code.IndexOf("PREFER_SETTINGS", at, StringComparison.Ordinal) >= 0)
                {
                    return RepositoriesMode.PreferSettings;
                }

                if (code.IndexOf("PREFER_PROJECT", at, StringComparison.Ordinal) >= 0)
                {
                    return RepositoriesMode.PreferProject;
                }

                if (code.IndexOf("FAIL_ON_PROJECT_REPOS", at, StringComparison.Ordinal) >= 0)
                {
                    return RepositoriesMode.FailOnProjectRepos;
                }
            }

            return RepositoriesMode.Unspecified;
        }

        /// <summary>
        /// Where <c>mavenCentral()</c> has to be declared so a Maven dependency resolves, given the project's
        /// settings and root build files (either DSL; pass null for a missing file). The settings
        /// <c>dependencyResolutionManagement</c> repositories are used when that block declares any; with
        /// <c>PREFER_PROJECT</c> (also Gradle's default when no mode is set) the root <c>allprojects</c>
        /// repositories are kept in step too, as they take precedence there. Without a settings repositories
        /// block the root <c>allprojects</c> block is the target, except under <c>FAIL_ON_PROJECT_REPOS</c>,
        /// where project repositories are not allowed to exist, so nothing is added there.
        /// </summary>
        public static ProjectRepositoriesResult EnsureProjectRepositories(string settings, string rootBuild)
        {
            var result = new ProjectRepositoriesResult
            {
                Settings = settings,
                RootBuild = rootBuild,
                Mode = settings == null ? RepositoriesMode.Unspecified : GetRepositoriesMode(settings),
            };

            RepositoryStatus inSettings = RepositoryStatus.BlockNotFound;
            if (settings != null)
            {
                inSettings = EnsureMavenCentral(settings, "dependencyResolutionManagement", out result.Settings);
            }

            if (inSettings == RepositoryStatus.BlockNotFound &&
                (result.Mode == RepositoriesMode.PreferSettings || result.Mode == RepositoriesMode.FailOnProjectRepos) &&
                TryAddRepositoriesBlock(settings, "dependencyResolutionManagement", out string withBlock))
            {
                // Project repositories are ignored (PREFER_SETTINGS) or rejected (FAIL_ON_PROJECT_REPOS) in these
                // modes, so with no settings repositories block the dependency can only resolve through a new one.
                result.Settings = withBlock;
                inSettings = RepositoryStatus.Inserted;
            }

            bool settingsDeclares = inSettings != RepositoryStatus.BlockNotFound;
            bool rootWins = !settingsDeclares ||
                            result.Mode == RepositoriesMode.PreferProject ||
                            result.Mode == RepositoriesMode.Unspecified;
            RepositoryStatus inRoot = RepositoryStatus.BlockNotFound;
            if (rootBuild != null && rootWins && result.Mode != RepositoriesMode.FailOnProjectRepos)
            {
                inRoot = EnsureMavenCentral(rootBuild, "allprojects", out result.RootBuild);
            }

            result.SettingsStatus = inSettings;
            result.RootBuildStatus = inRoot;
            result.Found = settingsDeclares || inRoot != RepositoryStatus.BlockNotFound;
            return result;
        }

        static bool TryAddRepositoriesBlock(string gradle, string outerBlock, out string updated)
        {
            updated = gradle;
            var lines = SplitLines(gradle, out string newline);
            int outerOpen = FindTopLevelBlockOpen(lines, outerBlock);
            if (outerOpen < 0 || IsSingleLineBlock(lines[outerOpen]))
            {
                return false;
            }

            int outerClose = FindBlockClose(lines, outerOpen);
            string indent = LeadingWhitespace(lines[outerOpen]) + "    ";
            lines.InsertRange(outerClose, new[]
            {
                indent + "repositories {",
                indent + "    mavenCentral() " + RepositoryMarker,
                indent + "}",
            });
            updated = JoinLines(lines, newline, gradle.EndsWith("\n", StringComparison.Ordinal));
            return true;
        }

        public sealed class ProjectRepositoriesResult
        {
            public string Settings;
            public string RootBuild;
            public RepositoriesMode Mode;
            public RepositoryStatus SettingsStatus;
            public RepositoryStatus RootBuildStatus;

            /// <summary>False when no repositories block was found to hold (or already hold) mavenCentral().</summary>
            public bool Found;
        }

        static List<string> BuildRegion(string indent, string group, IList<string> artifacts, string version, GradleDsl dsl)
        {
            var region = new List<string> { indent + DependenciesBeginMarker + " (com.bugsee.unity)" };
            for (int i = 0; i < artifacts.Count; i++)
            {
                string coordinate = group + ":" + artifacts[i] + ":" + version;
                region.Add(dsl == GradleDsl.Kotlin
                    ? indent + "implementation(\"" + coordinate + "\")"
                    : indent + "implementation '" + coordinate + "'");
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
            var inBlockComment = new LexState();
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
                    if (declaration != null && !IsOtherConfiguration(code.Substring(0, at)))
                    {
                        declaration.Line = lines[i].Trim();
                        found.Add(declaration);
                    }
                }
            }

            return found;
        }

        /// <summary>
        /// True when the call or configuration the coordinate belongs to is one that does not put the SDK on the
        /// app's runtime classpath: <c>exclude</c>, <c>compileOnly*</c>, test configurations (<c>testImplementation</c>,
        /// <c>androidTestImplementation</c>, ...) and annotation-processor configurations. <paramref name="before"/>
        /// is the code of the line up to the coordinate. A coordinate with no call before it (a continuation line)
        /// counts as a declaration.
        /// </summary>
        static bool IsOtherConfiguration(string before)
        {
            // Drop string contents (they hold group names such as "com.bugsee") and the keys of map notation.
            var sb = new StringBuilder();
            char quote = '\0';
            foreach (char c in before)
            {
                if (quote != '\0')
                {
                    if (c == quote)
                    {
                        quote = '\0';
                    }

                    continue;
                }

                if (c == '\'' || c == '"')
                {
                    quote = c;
                    sb.Append(' ');
                    continue;
                }

                sb.Append(c);
            }

            string name = null;
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(sb.ToString(), @"[A-Za-z_][A-Za-z0-9_]*"))
            {
                switch (m.Value)
                {
                    case "group":
                    case "name":
                    case "module":
                    case "version":
                    case "ext":
                    case "classifier":
                        continue;
                }

                name = m.Value;
            }

            if (name == null)
            {
                return false;
            }

            string lower = name.ToLowerInvariant();
            return lower == "exclude" ||
                   lower.Contains("compileonly") ||
                   lower.StartsWith("test", StringComparison.Ordinal) ||
                   lower.StartsWith("androidtest", StringComparison.Ordinal) ||
                   lower.StartsWith("unittest", StringComparison.Ordinal) ||
                   lower.Contains("annotationprocessor") ||
                   lower.StartsWith("kapt", StringComparison.Ordinal) ||
                   lower.StartsWith("ksp", StringComparison.Ordinal);
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
            var inBlockComment = new LexState();
            string code = StripComments(line, ref inBlockComment);
            return System.Text.RegularExpressions.Regex.IsMatch(code, @"\bmavenCentral\s*[({]") ||
                   code.IndexOf("repo.maven.apache.org/maven2", StringComparison.Ordinal) >= 0 ||
                   code.IndexOf("repo1.maven.org/maven2", StringComparison.Ordinal) >= 0;
        }

        /// <summary>Index of the line opening a depth-0 <c>name {</c> block, or -1.</summary>
        static int FindTopLevelBlockOpen(List<string> lines, string name)
        {
            int depth = 0;
            var inBlockComment = new LexState();
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
            var inBlockComment = new LexState();
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
            var inBlockComment = new LexState();
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
            var inBlockComment = new LexState();
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
        static string StripComments(string line, ref LexState inBlockComment)
        {
            var sb = new StringBuilder(line.Length);
            char quote = '\0';
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inBlockComment.RawString)
                {
                    if (c == '"' && i + 2 < line.Length && line[i + 1] == '"' && line[i + 2] == '"')
                    {
                        inBlockComment.RawString = false;
                        i += 2;
                    }

                    continue;
                }

                if (inBlockComment.Block)
                {
                    if (c == '*' && i + 1 < line.Length && line[i + 1] == '/')
                    {
                        inBlockComment.Block = false;
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
                    inBlockComment.Block = true;
                    i++;
                    continue;
                }

                // Kotlin / Groovy triple-quoted strings can span lines: their content is dropped like a comment.
                if (quote == '\0' && c == '"' && i + 2 < line.Length && line[i + 1] == '"' && line[i + 2] == '"')
                {
                    inBlockComment.RawString = true;
                    i += 2;
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

        /// <summary>Lexer state carried across lines: inside a block comment or a triple-quoted string.</summary>
        struct LexState
        {
            public bool Block;
            public bool RawString;

            public static implicit operator bool(LexState state)
            {
                return state.Block || state.RawString;
            }
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
