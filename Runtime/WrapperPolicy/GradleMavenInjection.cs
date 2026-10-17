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

            /// <summary>
            /// The outer block declares repositories in a shape that could not be read (everything on one line, or
            /// <c>repositories {</c> on the line of the outer brace). Only reported for plugin repositories, where a wrong guess
            /// fails the build.
            /// </summary>
            Unparsable,
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
            var foreign = FindProjectDeclarations(lines, hasRegion ? regionStart : -1, hasRegion ? regionEnd : -1, group);

            bool hasDirect = foreign.Exists(d => !d.Nested);
            if (foreign.Count > 0 && !hasDirect)
            {
                // Declared only inside if/else/with blocks: do not inject a duplicate or override the project's
                // condition, but a block we cannot evaluate is never a reason to delete an earlier injection.
                result.Status = DependencyStatus.DeclaredByProject;
                ReportProjectDeclarations(result, foreign, version);
                result.Text = buildGradle;
                return result;
            }

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
            return EnsureRepository(gradle, outerBlock, RepoKind.Dependencies, out updated);
        }

        /// <summary>What a repositories block has to serve: the SDK artifacts, the Gradle plugin's marker and jar, or the plugin jar as a buildscript classpath entry.</summary>
        internal enum RepoKind
        {
            Dependencies,
            Plugins,
            Classpath,
        }

        // (group, artifact) pairs a repository has to serve, per kind.
        static readonly string[][] DependencyTargets =
        {
            new[] { "com.bugsee", CoreArtifactToken },
            new[] { "com.bugsee", NdkArtifactToken },
        };

        static readonly string[][] PluginTargets =
        {
            new[] { "com.bugsee.android.gradle", "com.bugsee.android.gradle.gradle.plugin" },
            new[] { "com.bugsee", "bugsee-android-gradle-plugin" },
        };

        static readonly string[][] ClasspathTargets =
        {
            new[] { "com.bugsee", "bugsee-android-gradle-plugin" },
        };

        static string[][] TargetsFor(RepoKind kind)
        {
            return kind == RepoKind.Dependencies ? DependencyTargets : kind == RepoKind.Plugins ? PluginTargets : ClasspathTargets;
        }

        /// <summary>
        /// The shared edit behind <see cref="EnsureMavenCentral"/> and <see cref="EnsurePluginRepositories"/>. Dependency repositories get the
        /// marked <c>mavenCentral()</c> first (as always); plugin and classpath repositories get it LAST, so a repository the project
        /// deliberately lists ahead of Central (a corporate mirror) keeps resolving everything else first.
        /// </summary>
        internal static RepositoryStatus EnsureRepository(string gradle, string outerBlock, RepoKind kind, out string updated)
        {
            if (gradle == null)
            {
                throw new ArgumentNullException(nameof(gradle));
            }

            if (string.IsNullOrEmpty(outerBlock))
            {
                throw new ArgumentException("Outer block name is required.", nameof(outerBlock));
            }

            bool last = kind != RepoKind.Dependencies;
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
                if (kind == RepoKind.Plugins && MentionsRepositories(lines, outerOpen, outerClose))
                {
                    return RepositoryStatus.Unparsable;
                }

                return RepositoryStatus.BlockNotFound;
            }

            int reposClose = FindBlockClose(lines, reposOpen);
            if (BlockDeclaresMavenCentral(lines, reposOpen, reposClose, kind))
            {
                return RepositoryStatus.AlreadyPresent;
            }

            if (last)
            {
                // Appended after the last entry. The closing brace is found by lexing (a comment after it may hold braces), and an
                // entry that shares the closing line (`google() }`) is split off it so the new line really comes last.
                var code = CodeLines(lines);
                int depthAtStart = 0;
                for (int i = reposOpen; i < reposClose; i++)
                {
                    depthAtStart += BraceDelta(code[i]);
                }

                int closeAt = CloseBraceIndex(lines[reposClose], reposOpen == reposClose ? 0 : depthAtStart);
                if (closeAt < 0)
                {
                    return RepositoryStatus.BlockNotFound;
                }

                string before = lines[reposClose].Substring(0, closeAt);
                string rest = lines[reposClose].Substring(closeAt);
                if (reposOpen == reposClose)
                {
                    lines[reposClose] = before.TrimEnd() + "; mavenCentral() " + rest;
                }
                else
                {
                    string entryIndent = LeadingWhitespace(lines[reposOpen]) + "    ";
                    var tail = new List<string>();
                    if (before.Trim().Length > 0)
                    {
                        lines[reposClose] = before.TrimEnd();
                        tail.Add(entryIndent + "mavenCentral() " + RepositoryMarker);
                        tail.Add(LeadingWhitespace(lines[reposOpen]) + rest);
                        lines.InsertRange(reposClose + 1, tail);
                    }
                    else
                    {
                        lines.Insert(reposClose, entryIndent + "mavenCentral() " + RepositoryMarker);
                    }
                }

                updated = JoinLines(lines, newline, gradle.EndsWith("\n", StringComparison.Ordinal));
                return RepositoryStatus.Inserted;
            }

            if (reposOpen == reposClose)
            {
                // `repositories { google() }` on one line: a new line would be a sibling statement, and a
                // line comment would swallow the closing brace, so declare it inside the braces.
                int word = lines[reposOpen].IndexOf("repositories", StringComparison.Ordinal);
                int brace = word < 0 ? -1 : lines[reposOpen].IndexOf('{', word);
                if (brace < 0)
                {
                    return RepositoryStatus.BlockNotFound;
                }

                lines[reposOpen] = lines[reposOpen].Insert(brace + 1, " mavenCentral();");
                updated = JoinLines(lines, newline, gradle.EndsWith("\n", StringComparison.Ordinal));
                return RepositoryStatus.Inserted;
            }

            string indent = LeadingWhitespace(lines[reposOpen]) + "    ";
            lines.Insert(reposOpen + 1, indent + "mavenCentral() " + RepositoryMarker);
            updated = JoinLines(lines, newline, gradle.EndsWith("\n", StringComparison.Ordinal));
            return RepositoryStatus.Inserted;
        }

        static bool MentionsRepositories(List<string> lines, int open, int close)
        {
            var code = CodeLines(lines);
            for (int i = open; i <= close && i < code.Count; i++)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(code[i], @"\brepositories\s*\{"))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Index in <paramref name="line"/> of the <c>}</c> that brings the brace depth, starting at <paramref name="depthAtStart"/>,
        /// to zero (strings and comments skipped; a first line starts at zero and its opening brace raises the depth); -1 when none.
        /// </summary>
        static int CloseBraceIndex(string line, int depthAtStart)
        {
            int depth = depthAtStart;
            char quote = '\0';
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quote != '\0')
                {
                    if (c == '\\')
                    {
                        i++;
                    }
                    else if (c == quote)
                    {
                        quote = '\0';
                    }

                    continue;
                }

                if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
                {
                    return -1;
                }

                if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
                {
                    int end = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        return -1;
                    }

                    i = end + 1;
                    continue;
                }

                if (c == '\'' || c == '"')
                {
                    quote = c;
                }
                else if (c == '{')
                {
                    depth++;
                }
                else if (c == '}' && --depth == 0)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Makes sure the plugin repositories of a settings file can serve the Bugsee Gradle plugin: when the top-level
        /// <c>pluginManagement</c> block declares a <c>repositories</c> block that serves neither through the plugin portal
        /// (<c>gradlePluginPortal()</c> or its <c>plugins.gradle.org/m2</c> URL), Maven Central, nor a repository whose
        /// content filter explicitly includes the Bugsee groups, <c>mavenCentral()</c> is appended after the existing entries.
        /// <see cref="RepositoryStatus.BlockNotFound"/> means the settings declare no plugin repositories, so Gradle's
        /// default (the plugin portal) applies; <see cref="RepositoryStatus.Unparsable"/> means they declare some in a shape
        /// that could not be read, so nothing can be said about whether the plugin resolves.
        /// </summary>
        public static RepositoryStatus EnsurePluginRepositories(string settings, out string updated)
        {
            return EnsureRepository(settings, "pluginManagement", RepoKind.Plugins, out updated);
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

        internal static bool TryAddRepositoriesBlock(string gradle, string outerBlock, out string updated)
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

            /// <summary>Found inside an if/else/with/for/while block: enough to skip injecting, never to remove an earlier injection.</summary>
            public bool Nested;
        }

        /// <summary>
        /// The Bugsee declarations a project makes itself, found in one pass over the file. Only a statement
        /// directly inside a top-level <c>dependencies { }</c> block counts, and only when its configuration is
        /// <c>implementation</c>, <c>api</c>, <c>runtimeOnly</c>, <c>compile</c> or <c>runtime</c> (or the
        /// <c>debug</c> / <c>release</c> prefixed <c>Implementation</c>/<c>Api</c>/<c>RuntimeOnly</c>; other flavor
        /// prefixes are not recognised, which at worst injects a duplicate of the same coordinate) and the Bugsee
        /// coordinate is a direct argument of it: a coordinate string, a <c>group</c>/<c>name</c> map pair, a map
        /// <c>name</c> naming a local AAR, or <c>files('...bugsee-android...aar')</c>. Anything else (other
        /// configurations, <c>exclude</c>, <c>platform</c>, <c>fileTree</c>, <c>constraints</c>,
        /// <c>resolutionStrategy</c>, variables, println) is not a declaration, because an earlier injection is
        /// removed when one is found.
        /// </summary>
        static List<Declaration> FindProjectDeclarations(List<string> lines, int skipStart, int skipEnd, string group)
        {
            var found = new List<Declaration>();
            var lex = new LexState();
            var text = new StringBuilder();
            for (int i = 0; i < lines.Count; i++)
            {
                string code = StripComments(lines[i], ref lex);
                text.Append(i >= skipStart && i <= skipEnd ? string.Empty : code).Append('\n');
            }

            string src = text.ToString();
            int depth = 0;
            bool inDeps = false;
            var statement = new StringBuilder();
            char quote = '\0';
            for (int i = 0; i < src.Length; i++)
            {
                char c = src[i];
                if (quote != '\0')
                {
                    if (c == '\n')
                    {
                        quote = '\0';
                        if (inDeps)
                        {
                            statement.Append(c);
                        }

                        continue;
                    }

                    if (inDeps)
                    {
                        statement.Append(c);
                    }

                    if (c == '\\' && i + 1 < src.Length && src[i + 1] != '\n')
                    {
                        i++;
                        if (inDeps)
                        {
                            statement.Append(src[i]);
                        }
                    }
                    else if (c == quote)
                    {
                        quote = '\0';
                    }

                    continue;
                }

                if (c == '\n')
                {
                    if (inDeps && depth == 1)
                    {
                        string pending = statement.ToString().TrimEnd();
                        if (pending.EndsWith(",", StringComparison.Ordinal) ||
                            pending.EndsWith("\\", StringComparison.Ordinal))
                        {
                            statement.Append(c);
                        }
                        else
                        {
                            FlushStatement(statement, group, found);
                        }
                    }
                    else if (inDeps)
                    {
                        statement.Append(c);
                    }

                    continue;
                }

                if (c == '\'' || c == '"')
                {
                    quote = c;
                    if (inDeps)
                    {
                        statement.Append(c);
                    }
                }
                else if (c == '(' || c == '{' || c == '[')
                {
                    if (!inDeps && depth == 0 && c == '{' && HeadIdentifier(src, i) == "dependencies")
                    {
                        inDeps = true;
                        statement.Clear();
                    }
                    else if (inDeps)
                    {
                        statement.Append(c);
                    }

                    depth++;
                }
                else if (c == ')' || c == '}' || c == ']')
                {
                    depth = Math.Max(0, depth - 1);
                    if (inDeps && depth == 0)
                    {
                        FlushStatement(statement, group, found);
                        inDeps = false;
                    }
                    else if (inDeps)
                    {
                        statement.Append(c);
                    }
                }
                else if (c == ';' && inDeps && depth == 1)
                {
                    FlushStatement(statement, group, found);
                }
                else if (inDeps && !(statement.Length == 0 && char.IsWhiteSpace(c)))
                {
                    statement.Append(c);
                }
            }

            return found;
        }

        static string HeadIdentifier(string code, int opener)
        {
            int k = opener - 1;
            while (k >= 0 && char.IsWhiteSpace(code[k]))
            {
                k--;
            }

            int end = k + 1;
            while (k >= 0 && (char.IsLetterOrDigit(code[k]) || code[k] == '_'))
            {
                k--;
            }

            return code.Substring(k + 1, end - k - 1);
        }

        static void FlushStatement(StringBuilder statement, string group, List<Declaration> found)
        {
            string text = statement.ToString().Trim();
            statement.Clear();
            if (text.Length == 0)
            {
                return;
            }

            string display = Collapse(text);
            var direct = ParseDeclarations(text, group);
            foreach (var declaration in direct)
            {
                declaration.Line = display;
                found.Add(declaration);
            }

            if (direct.Count == 0)
            {
                CollectNested(text, group, display, found, 0);
            }
        }

        /// <summary>A statement as one short line for messages: whitespace collapsed, long text cut.</summary>
        static string Collapse(string statement)
        {
            string text = System.Text.RegularExpressions.Regex.Replace(statement.Trim(), @"\s+", " ");
            return text.Length <= 160 ? text : text.Substring(0, 157) + "...";
        }

        static bool IsControlFlow(string statement)
        {
            string name = ReadIdentifier(statement, 0, out _);
            return name == "if" || name == "else" || name == "with" || name == "for" || name == "while";
        }

        static string ReadIdentifier(string text, int start, out int end)
        {
            end = start;
            while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_'))
            {
                end++;
            }

            return text.Substring(start, end - start);
        }

        /// <summary>
        /// Declarations inside the body of an if/else/with/for/while statement, found with the same strict
        /// rules as top-level ones but marked <see cref="Declaration.Nested"/>.
        /// </summary>
        static void CollectNested(string statement, string group, string display, List<Declaration> found, int level)
        {
            if (level > 8 || !IsControlFlow(statement))
            {
                return;
            }

            string t = statement.TrimStart();
            string name = ReadIdentifier(t, 0, out int i);
            if (name == "else")
            {
                int j = SkipWhitespace(t, i);
                if (ReadIdentifier(t, j, out int afterIf) == "if")
                {
                    i = afterIf;
                }
            }

            i = SkipWhitespace(t, i);
            if (i < t.Length && t[i] == '(')
            {
                int close = MatchingClose(t, i);
                if (close < 0)
                {
                    return;
                }

                i = SkipWhitespace(t, close + 1);
            }

            string body;
            if (i < t.Length && t[i] == '{')
            {
                int close = MatchingClose(t, i);
                body = close < 0 ? t.Substring(i + 1) : t.Substring(i + 1, close - i - 1);
                if (close >= 0 && t.Substring(close + 1).TrimStart().StartsWith("else", StringComparison.Ordinal))
                {
                    CollectNested(t.Substring(close + 1).TrimStart(), group, display, found, level + 1);
                }
            }
            else
            {
                body = t.Substring(i);
            }

            foreach (string inner in SplitStatements(body))
            {
                var declarations = ParseDeclarations(inner.Trim(), group);
                foreach (var declaration in declarations)
                {
                    declaration.Line = Collapse(inner);
                    declaration.Nested = true;
                    found.Add(declaration);
                }

                if (declarations.Count == 0)
                {
                    CollectNested(inner.Trim(), group, display, found, level + 1);
                }
            }
        }

        /// <summary>Splits at newlines and semicolons outside strings and brackets; a trailing comma or backslash continues the statement.</summary>
        static List<string> SplitStatements(string body)
        {
            var parts = new List<string>();
            var current = new StringBuilder();
            int depth = 0;
            char quote = '\0';
            for (int i = 0; i < body.Length; i++)
            {
                char c = body[i];
                if (quote != '\0')
                {
                    if (c == '\n')
                    {
                        quote = '\0';
                    }
                    else if (c == '\\' && i + 1 < body.Length)
                    {
                        current.Append(c);
                        c = body[++i];
                    }
                    else if (c == quote)
                    {
                        quote = '\0';
                    }

                    current.Append(c);
                    continue;
                }

                if (c == '\'' || c == '"')
                {
                    quote = c;
                }
                else if (c == '(' || c == '[' || c == '{')
                {
                    depth++;
                }
                else if (c == ')' || c == ']' || c == '}')
                {
                    depth = Math.Max(0, depth - 1);
                }
                else if (depth == 0 && (c == '\n' || c == ';'))
                {
                    string pending = current.ToString().TrimEnd();
                    if (c == '\n' && (pending.EndsWith(",", StringComparison.Ordinal) ||
                                      pending.EndsWith("\\", StringComparison.Ordinal)))
                    {
                        current.Append(c);
                        continue;
                    }

                    if (pending.Length > 0)
                    {
                        parts.Add(pending);
                    }

                    current.Clear();
                    continue;
                }

                current.Append(c);
            }

            if (current.ToString().Trim().Length > 0)
            {
                parts.Add(current.ToString());
            }

            return parts;
        }

        static bool IsRuntimeConfiguration(string name)
        {
            switch (name)
            {
                case "implementation":
                case "api":
                case "runtimeOnly":
                case "compile":
                case "runtime":
                case "debugImplementation":
                case "releaseImplementation":
                case "debugApi":
                case "releaseApi":
                case "debugRuntimeOnly":
                case "releaseRuntimeOnly":
                    return true;
                default:
                    return false;
            }
        }

        static List<Declaration> ParseDeclarations(string statement, string group)
        {
            var result = new List<Declaration>();
            int p = 0;
            while (p < statement.Length && (char.IsLetterOrDigit(statement[p]) || statement[p] == '_'))
            {
                p++;
            }

            if (p == 0 || !IsRuntimeConfiguration(statement.Substring(0, p)))
            {
                return result;
            }

            if (p < statement.Length && !char.IsWhiteSpace(statement[p]) && statement[p] != '(' &&
                statement[p] != '\'' && statement[p] != '"')
            {
                return result;
            }

            string rest = statement.Substring(p).TrimStart();
            string args;
            if (rest.StartsWith("(", StringComparison.Ordinal))
            {
                int close = MatchingClose(rest, 0);
                args = close < 0 ? rest.Substring(1) : rest.Substring(1, close - 1);
            }
            else
            {
                args = rest;
            }

            string mapGroup = null;
            string mapName = null;
            foreach (string raw in SplitTopLevel(args))
            {
                string arg = raw.Trim();
                string literal = AsLiteral(arg);
                if (literal != null)
                {
                    string[] parts = literal.Split(':');
                    if (parts.Length >= 2 && parts[0] == group)
                    {
                        string artifact = parts[1] + (parts.Length > 2 ? ":" + parts[2].Split('@')[0] : string.Empty);
                        AddIfBugsee(result, artifact);
                    }

                    continue;
                }

                var pair = System.Text.RegularExpressions.Regex.Match(arg, @"^(group|name)\s*[:=]\s*(.+)$", System.Text.RegularExpressions.RegexOptions.Singleline);
                if (pair.Success)
                {
                    string value = AsLiteral(pair.Groups[2].Value.Trim());
                    if (value != null)
                    {
                        if (pair.Groups[1].Value == "group")
                        {
                            mapGroup = value;
                        }
                        else
                        {
                            mapName = value;
                        }
                    }

                    continue;
                }

                var files = System.Text.RegularExpressions.Regex.Match(arg, @"^files\s*\((.*)\)$", System.Text.RegularExpressions.RegexOptions.Singleline);
                if (files.Success)
                {
                    foreach (string file in SplitTopLevel(files.Groups[1].Value))
                    {
                        string path = AsLiteral(file.Trim());
                        if (path == null || !path.EndsWith(".aar", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        AddIfBugsee(result, path.Substring(path.LastIndexOfAny(new[] { '/', '\\' }) + 1));
                    }
                }
            }

            if (mapName != null && (mapGroup == null || mapGroup == group))
            {
                AddIfBugsee(result, mapName);
            }

            return result;
        }

        static void AddIfBugsee(List<Declaration> result, string artifactAndVersion)
        {
            if (!artifactAndVersion.StartsWith(CoreArtifactToken, StringComparison.Ordinal))
            {
                return;
            }

            var declaration = Classify(artifactAndVersion, 0);
            if (declaration != null)
            {
                result.Add(declaration);
            }
        }

        /// <summary>The content of <paramref name="arg"/> when it is exactly one quoted string literal, else null.</summary>
        static string AsLiteral(string arg)
        {
            if (arg.Length < 2 || (arg[0] != '\'' && arg[0] != '"') || arg[arg.Length - 1] != arg[0])
            {
                return null;
            }

            string inner = arg.Substring(1, arg.Length - 2);
            return inner.IndexOf(arg[0]) >= 0 ? null : inner;
        }

        /// <summary>Index of the bracket closing the one at <paramref name="open"/> (strings skipped), or -1.</summary>
        static int MatchingClose(string s, int open)
        {
            int depth = 0;
            char quote = '\0';
            for (int i = open; i < s.Length; i++)
            {
                char c = s[i];
                if (quote != '\0')
                {
                    if (c == '\\')
                    {
                        i++;
                    }
                    else if (c == quote)
                    {
                        quote = '\0';
                    }

                    continue;
                }

                if (c == '\'' || c == '"')
                {
                    quote = c;
                }
                else if (c == '(' || c == '[' || c == '{')
                {
                    depth++;
                }
                else if (c == ')' || c == ']' || c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        /// <summary>Splits at commas outside strings and brackets; a top-level <c>{</c> (a trailing closure) ends the list.</summary>
        static List<string> SplitTopLevel(string args)
        {
            var parts = new List<string>();
            var current = new StringBuilder();
            int depth = 0;
            char quote = '\0';
            for (int i = 0; i < args.Length; i++)
            {
                char c = args[i];
                if (quote != '\0')
                {
                    current.Append(c);
                    if (c == '\\' && i + 1 < args.Length)
                    {
                        current.Append(args[++i]);
                    }
                    else if (c == quote)
                    {
                        quote = '\0';
                    }

                    continue;
                }

                if (c == '\'' || c == '"')
                {
                    quote = c;
                }
                else if (c == '{' && depth == 0)
                {
                    break;
                }
                else if (c == '(' || c == '[' || c == '{')
                {
                    depth++;
                }
                else if (c == ')' || c == ']' || c == '}')
                {
                    depth--;
                }
                else if (c == ',' && depth == 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                    continue;
                }

                current.Append(c);
            }

            parts.Add(current.ToString());
            return parts;
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
                    !reported.Add(declaration.Line + "|" + declaration.IsNdk))
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

        /// <summary>
        /// True when a repositories block declares a Maven Central that serves the Bugsee artifacts: an unfiltered
        /// <c>mavenCentral()</c>, or a <c>mavenCentral { content { ... } }</c> whose include filters cover
        /// <c>com.bugsee:bugsee-android</c> and <c>bugsee-android-ndk</c> and whose exclude filters do not touch
        /// them. A filter that cannot be read (version filters, unparsable regex) counts as not serving them, and the
        /// caller then adds the marked unfiltered <c>mavenCentral()</c> (Gradle unions repositories).
        /// </summary>
        static bool BlockDeclaresMavenCentral(List<string> lines, int open, int close, RepoKind kind = RepoKind.Dependencies)
        {
            string[][] targets = TargetsFor(kind);
            bool plugins = kind != RepoKind.Dependencies;
            var lex = new LexState();
            var text = new StringBuilder();
            for (int i = open; i <= close; i++)
            {
                text.Append(StripComments(lines[i], ref lex)).Append('\n');
            }

            // The text between the braces of the repositories block, so a one-line block or a
            // `repositories { mavenCentral()` first line is scanned too.
            string whole = text.ToString();
            string wholeMasked = MaskStrings(whole);
            int braceOpen = wholeMasked.IndexOf('{');
            if (braceOpen < 0)
            {
                return false;
            }

            int braceClose = MatchingClose(wholeMasked, braceOpen);
            string block = braceClose < 0 ? whole.Substring(braceOpen + 1) : whole.Substring(braceOpen + 1, braceClose - braceOpen - 1);
            string masked = MaskStrings(block);

            // exclusiveContent { forRepository { mavenCentral() } filter { ... } }: the filter decides.
            var exclusive = new List<int[]>();
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(masked, @"\bexclusiveContent\s*\{"))
            {
                int openAt = m.Index + m.Length - 1;
                int closeAt = MatchingClose(masked, openAt);
                exclusive.Add(new[] { openAt, closeAt < 0 ? block.Length : closeAt });
            }

            // maven { url 'https://repo.maven.apache.org/maven2' [content { ... }] }
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(masked, @"\bmaven\s*\{"))
            {
                int openAt = m.Index + m.Length - 1;
                int closeAt = MatchingClose(masked, openAt);
                string body = closeAt < 0 ? block.Substring(openAt + 1) : block.Substring(openAt + 1, closeAt - openAt - 1);
                bool central = body.IndexOf("repo.maven.apache.org/maven2", StringComparison.Ordinal) >= 0 ||
                               body.IndexOf("repo1.maven.org/maven2", StringComparison.Ordinal) >= 0 ||
                               (plugins && body.IndexOf("plugins.gradle.org/m2", StringComparison.Ordinal) >= 0);
                // A proxy at another URL counts only when its content filter explicitly claims the Bugsee groups.
                bool claimed = plugins && !central && ContentFilterServesBugsee(body, "content", targets, requireInclude: true);
                if ((central || claimed) && ContentFilterServesBugsee(body, "content", targets) &&
                    ExclusiveFiltersServe(block, exclusive, m.Index, targets))
                {
                    return true;
                }
            }

            // Kotlin shorthands: maven("https://...") and maven(url = "...") [{ content { } }].
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(masked, @"\bmaven\s*\("))
            {
                int openParen = m.Index + m.Length - 1;
                int closeParen = MatchingClose(block, openParen);
                if (closeParen < 0)
                {
                    continue;
                }

                string args = block.Substring(openParen + 1, closeParen - openParen - 1);
                string body = args;
                int after = SkipWhitespace(block, closeParen + 1);
                if (after < block.Length && block[after] == '{')
                {
                    int closeBrace = MatchingClose(masked, after);
                    body += " " + (closeBrace < 0 ? block.Substring(after + 1) : block.Substring(after + 1, closeBrace - after - 1));
                }

                bool central = args.IndexOf("repo.maven.apache.org/maven2", StringComparison.Ordinal) >= 0 ||
                               args.IndexOf("repo1.maven.org/maven2", StringComparison.Ordinal) >= 0 ||
                               (plugins && args.IndexOf("plugins.gradle.org/m2", StringComparison.Ordinal) >= 0);
                bool claimed = plugins && !central && ContentFilterServesBugsee(body, "content", targets, requireInclude: true);
                if ((central || claimed) && ContentFilterServesBugsee(body, "content", targets) &&
                    ExclusiveFiltersServe(block, exclusive, m.Index, targets))
                {
                    return true;
                }
            }

            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(masked, plugins ? @"\b(?:mavenCentral|gradlePluginPortal)\b" : @"\bmavenCentral\b"))
            {
                int p = SkipWhitespace(block, m.Index + m.Length);
                if (p < block.Length && block[p] == '(')
                {
                    int closeParen = MatchingClose(block, p);
                    if (closeParen < 0 || block.Substring(p + 1, closeParen - p - 1).Trim().Length > 0)
                    {
                        continue;
                    }

                    p = SkipWhitespace(block, closeParen + 1);
                }

                bool served = true;
                if (p < block.Length && block[p] == '{')
                {
                    int closeBrace = MatchingClose(masked, p);
                    string body = closeBrace < 0 ? block.Substring(p + 1) : block.Substring(p + 1, closeBrace - p - 1);
                    served = ContentFilterServesBugsee(body, "content", targets);
                }

                served &= ExclusiveFiltersServe(block, exclusive, m.Index, targets);
                if (served)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>True unless the repository at <paramref name="index"/> sits in an <c>exclusiveContent</c> whose <c>filter</c> does not serve Bugsee.</summary>
        static bool ExclusiveFiltersServe(string block, List<int[]> exclusive, int index, string[][] targets)
        {
            foreach (int[] span in exclusive)
            {
                if (index > span[0] && index < span[1] &&
                    !ContentFilterServesBugsee(
                        block.Substring(span[0] + 1, span[1] - span[0] - 1), "filter", targets, requireFilter: true))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>The text with the contents of string literals blanked, so names inside strings are not matched.</summary>
        static string MaskStrings(string code)
        {
            var sb = new StringBuilder(code.Length);
            char quote = '\0';
            for (int i = 0; i < code.Length; i++)
            {
                char c = code[i];
                if (quote != '\0')
                {
                    if (c == '\\' && i + 1 < code.Length)
                    {
                        sb.Append("  ");
                        i++;
                    }
                    else if (c == quote || c == '\n')
                    {
                        quote = '\0';
                        sb.Append(c);
                    }
                    else
                    {
                        sb.Append(' ');
                    }

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

        static int SkipWhitespace(string s, int p)
        {
            while (p < s.Length && char.IsWhiteSpace(s[p]))
            {
                p++;
            }

            return p;
        }

        static bool ContentFilterServesBugsee(string body, string filterName, string[][] targets, bool requireFilter = false, bool requireInclude = false)
        {
            var content = System.Text.RegularExpressions.Regex.Match(body, @"\b" + filterName + @"\s*\{");
            if (!content.Success)
            {
                return !requireFilter && !requireInclude;
            }

            int open = content.Index + content.Length - 1;
            int closeBrace = MatchingClose(body, open);
            string filter = closeBrace < 0 ? body.Substring(open + 1) : body.Substring(open + 1, closeBrace - open - 1);

            bool anyInclude = false;
            var covered = new bool[targets.Length];
            foreach (System.Text.RegularExpressions.Match call in System.Text.RegularExpressions.Regex.Matches(
                         filter,
                         @"\b(include|exclude)(Group|GroupAndSubgroups|GroupByRegex|Module|ModuleByRegex|Version|VersionByRegex)\s*\(?\s*(['""])(.*?)\3(?:\s*,\s*(['""])(.*?)\5)?"))
            {
                bool include = call.Groups[1].Value == "include";
                string kind = call.Groups[2].Value;
                string first = call.Groups[4].Value.Replace("\\\\", "\\");
                string second = call.Groups[6].Success ? call.Groups[6].Value.Replace("\\\\", "\\") : null;
                if (kind.StartsWith("Version", StringComparison.Ordinal))
                {
                    return false;
                }

                if (!include)
                {
                    foreach (string[] target in targets)
                    {
                        if (FilterMatches(kind, first, second, target[0], target[1]))
                        {
                            return false;
                        }
                    }

                    continue;
                }

                anyInclude = true;
                for (int t = 0; t < targets.Length; t++)
                {
                    covered[t] |= FilterMatches(kind, first, second, targets[t][0], targets[t][1]);
                }
            }

            if (!anyInclude)
            {
                return !requireInclude;
            }

            return Array.TrueForAll(covered, c => c);
        }

        /// <summary>True when a content-filter call (kind, first and optional second string) matches the Bugsee group and <paramref name="artifact"/>; an unparsable regex matches.</summary>
        static bool FilterMatches(string kind, string first, string second, string bugseeGroup, string artifact)
        {
            switch (kind)
            {
                case "Group":
                    return first == bugseeGroup;
                case "GroupAndSubgroups":
                    return bugseeGroup == first || bugseeGroup.StartsWith(first + ".", StringComparison.Ordinal);
                case "GroupByRegex":
                    return RegexMatches(first, bugseeGroup);
                case "Module":
                    return first == bugseeGroup && second == artifact;
                case "ModuleByRegex":
                    return RegexMatches(first, bugseeGroup) && second != null && RegexMatches(second, artifact);
                default:
                    return true;
            }
        }

        /// <summary>How long one project-written filter regex may run (a runaway one is read as matching).</summary>
        internal static readonly TimeSpan FilterRegexTimeout = TimeSpan.FromMilliseconds(500);

        static bool RegexMatches(string pattern, string input)
        {
            return RegexMatches(pattern, input, FilterRegexTimeout);
        }

        internal static bool RegexMatches(string pattern, string input, TimeSpan timeout)
        {
            try
            {
                // A project's own filter regex runs inside the Editor build step: bound it.
                return System.Text.RegularExpressions.Regex.IsMatch(
                    input, "^(?:" + pattern + ")$", System.Text.RegularExpressions.RegexOptions.None, timeout);
            }
            catch (ArgumentException)
            {
                return true;
            }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                return true;
            }
        }

        /// <summary>Each line reduced to its code (comments removed, string contents kept, their braces blanked), for structure scans.</summary>
        internal static List<string> CodeLines(List<string> lines)
        {
            var state = new LexState();
            var code = new List<string>(lines.Count);
            foreach (string line in lines)
            {
                code.Add(StripComments(line, ref state));
            }

            return code;
        }

        /// <summary>Index of the line opening a depth-0 <c>name {</c> block, or -1.</summary>
        internal static int FindTopLevelBlockOpen(List<string> lines, string name)
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
        internal static int FindChildBlockOpen(List<string> lines, int open, int close, string name)
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
        internal static int FindBlockClose(List<string> lines, int open)
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

        internal static bool IsSingleLineBlock(string line)
        {
            var inBlockComment = new LexState();
            string code = StripComments(line, ref inBlockComment);
            int open = code.IndexOf('{');
            return open >= 0 && code.IndexOf('}', open) >= 0;
        }

        internal static int BraceDelta(string code)
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
                if (inBlockComment.RawQuote != '\0')
                {
                    if (c == inBlockComment.RawQuote && i + 2 < line.Length && line[i + 1] == c && line[i + 2] == c)
                    {
                        inBlockComment.RawQuote = '\0';
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

                // Kotlin / Groovy triple-quoted strings (Groovy also with three single quotes) can span lines: their content is dropped like a comment.
                if (quote == '\0' && (c == '"' || c == '\'') && i + 2 < line.Length && line[i + 1] == c && line[i + 2] == c)
                {
                    inBlockComment.RawQuote = c;
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

        /// <summary>Lexer state carried across lines: inside a block comment or a triple-quoted string (<see cref="RawQuote"/> is its quote character).</summary>
        struct LexState
        {
            public bool Block;
            public char RawQuote;

            public static implicit operator bool(LexState state)
            {
                return state.Block || state.RawQuote != '\0';
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

        internal static string LeadingWhitespace(string line)
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
