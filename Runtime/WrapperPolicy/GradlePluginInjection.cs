using System;
using System.Collections.Generic;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Text edits that apply the Bugsee Gradle plugin (<c>com.bugsee.android.gradle</c>) to Unity's generated
    /// <c>launcher</c> module, in the Groovy or the Kotlin DSL. Unity itself only generates Groovy files, so a
    /// <c>.kts</c> project exists when the project converts the generated files in its own post-generate step.
    ///
    /// The plugin does nothing when it is applied before the Android Gradle plugin (it looks the AGP extension up while
    /// being applied), and the build fails when the launcher names a plugin the build cannot resolve. So the edits
    /// are made in two steps that both have to work, or nothing is applied and the user is told what to add:
    /// <list type="number">
    /// <item>Make the plugin resolvable: the root declares it <c>apply false</c> in its Groovy <c>plugins { }</c> block
    /// (Unity's layout), or a root that declares AGP through <c>buildscript { dependencies { classpath } }</c> gets the plugin
    /// as a classpath entry (with <c>mavenCentral()</c> in those repositories); with neither, the launcher declares it
    /// with its version.</item>
    /// <item>Apply it after AGP: Unity's Groovy launcher gets <c>apply plugin:</c> after its own
    /// <c>apply plugin: 'com.android.application'</c> (through <see cref="MarkedGradleBlock"/>); a launcher with a
    /// <c>plugins { }</c> block that lists AGP gets an entry after it. Any other launcher is left alone.</item>
    /// </list>
    /// </summary>
    public static class GradlePluginInjection
    {
        public const string PluginId = "com.bugsee.android.gradle";
        const string PluginArtifact = "bugsee-android-gradle-plugin";
        internal const string PluginMarker = "// bugsee:gradle-plugin";
        internal const string NdkMarker = "// bugsee:gradle-ndk";
        const string LegacyMarker = "// Bugsee Gradle plugin";

        const string RootAnchor = "plugins {";
        const string LauncherAnchor = "apply plugin: 'com.android.application'";

        public sealed class PluginPatch
        {
            public string Root;
            public string Launcher;

            /// <summary>True when the launcher applies the plugin after the patch (also when the project already declared it).</summary>
            public bool Applied;

            /// <summary>True when the patch relies on the settings plugin repositories (the <c>plugins { id }</c> DSL), as opposed to a buildscript classpath.</summary>
            public bool NeedsPluginRepository;

            /// <summary>True when <c>mavenCentral()</c> was appended to the root buildscript repositories so the classpath entry resolves.</summary>
            public bool AddedBuildscriptRepository;

            /// <summary>Why the plugin is not applied, or a note about the project's own declarations; null when nothing needs saying.</summary>
            public string Message;

            /// <summary>True when <see cref="Message"/> is a problem the user has to act on, not information.</summary>
            public bool IsWarning;
        }

        /// <summary>
        /// Applies the plugin to <paramref name="launcher"/>, declaring it in <paramref name="root"/> as well where that is
        /// what makes it resolvable. Text that cannot be edited safely is returned unchanged with a message.
        /// </summary>
        public static PluginPatch Apply(
            string root, GradleMavenInjection.GradleDsl rootDsl,
            string launcher, GradleMavenInjection.GradleDsl launcherDsl,
            string pluginVersion,
            bool allowRepositoryInjection = true)
        {
            var patch = new PluginPatch { Root = root, Launcher = launcher };
            RootShape rootShape = ShapeOfRoot(root);

            if (HasUnmarkedDeclaration(launcher))
            {
                patch.Applied = true;
                patch.Message = "The launcher build file already declares the Bugsee Gradle plugin; it is left as written.";
                return patch;
            }

            // 1. Resolvable.
            bool rootDeclares = HasUnmarkedDeclaration(root);
            if (!rootDeclares)
            {
                string declared = null;
                bool rootHasPluginsBlock = GradleMavenInjection.FindTopLevelBlockOpen(MarkedGradleBlock.SplitLines(root, out _), "plugins") >= 0;
                if (rootHasPluginsBlock)
                {
                    // MarkedGradleBlock keeps any marked line of ours up to date, so it only sees roots that have the block.
                    if (rootDsl == GradleMavenInjection.GradleDsl.Groovy)
                    {
                        declared = TryMarked(root, RootAnchor, RootPluginLine(pluginVersion));
                        patch.NeedsPluginRepository = declared != null;
                    }
                }
                else
                {
                    declared = TryClasspath(root, rootDsl, pluginVersion, allowRepositoryInjection, out bool addedRepository);
                    patch.AddedBuildscriptRepository = declared != null && addedRepository;
                }

                if (declared != null)
                {
                    patch.Root = declared;
                    rootDeclares = true;
                }
            }

            // 2. Applied after AGP.
            // Only a launcher that is written in Unity's apply-plugin style: MarkedGradleBlock would otherwise take any
            // marked line of ours (an `id` entry in a plugins { } block) for an apply line and rewrite it.
            if (launcherDsl == GradleMavenInjection.GradleDsl.Groovy && rootDeclares && UsesApplyStyle(launcher))
            {
                string applied = TryMarked(launcher, LauncherAnchor, LauncherGroovyPatch());
                if (applied != null)
                {
                    patch.Launcher = applied;
                    patch.Applied = true;
                    return patch;
                }
            }

            string failure;
            string text = ApplyToLauncherBlock(launcher, launcherDsl, pluginVersion, rootDeclares, out failure);
            if (text == null)
            {
                // Leave the root as it was too: a declaration nothing applies is noise.
                patch.Root = root;
                patch.NeedsPluginRepository = false;
                patch.AddedBuildscriptRepository = false;
                patch.Message = "The Bugsee Gradle plugin was not applied: " + failure + " " + ManualInstructions(launcherDsl, pluginVersion, rootShape);
                patch.IsWarning = true;
                return patch;
            }

            patch.Launcher = text;
            patch.Applied = true;
            patch.NeedsPluginRepository |= !rootDeclares;
            return patch;
        }

        /// <summary>The line the root <c>plugins { }</c> block gets (Groovy).</summary>
        public static string RootPluginLine(string version)
        {
            return $"    id '{PluginId}' version '{version}' apply false {PluginMarker}";
        }

        /// <summary>The lines after the launcher's <c>apply plugin: 'com.android.application'</c> (Groovy).</summary>
        public static string LauncherGroovyPatch()
        {
            return $"apply plugin: '{PluginId}' {PluginMarker}\n\n" +
                   "bugsee {\n" +
                   "    // App token is supplied at runtime via Bugsee.Launch.\n" +
                   "    ndk {\n" +
                   "        enabled = true\n" +
                   "    }\n" +
                   $"}} {NdkMarker}";
        }

        /// <summary>How the root build file makes plugins available, which decides the manual instructions.</summary>
        public enum RootShape
        {
            Unknown,
            PluginsBlock,
            Buildscript,
        }

        static RootShape ShapeOfRoot(string root)
        {
            var lines = MarkedGradleBlock.SplitLines(root, out _);
            if (GradleMavenInjection.FindTopLevelBlockOpen(lines, "plugins") >= 0)
            {
                return RootShape.PluginsBlock;
            }

            return GradleMavenInjection.FindTopLevelBlockOpen(lines, "buildscript") >= 0 ? RootShape.Buildscript : RootShape.Unknown;
        }

        /// <summary>What to add by hand, in the DSL of the launcher file, when the package cannot edit it.</summary>
        public static string ManualInstructions(GradleMavenInjection.GradleDsl dsl, string version, RootShape root = RootShape.Unknown)
        {
            bool kotlin = dsl == GradleMavenInjection.GradleDsl.Kotlin;
            string id = kotlin ? $"id(\"{PluginId}\") version \"{version}\"" : $"id '{PluginId}' version '{version}'";
            string enabled = kotlin ? "enabled.set(true)" : "enabled = true";
            string file = kotlin ? "launcher/build.gradle.kts" : "launcher/build.gradle";
            string classpath = kotlin
                ? $"classpath(\"com.bugsee:{PluginArtifact}:{version}\")"
                : $"classpath 'com.bugsee:{PluginArtifact}:{version}'";
            string apply = kotlin ? $"apply(plugin = \"{PluginId}\")" : $"apply plugin: '{PluginId}'";
            string configure = kotlin
                ? $"configure<com.bugsee.android.gradle.BugseePluginExtension> {{ ndk {{ {enabled} }} }}"
                : $"bugsee {{ ndk {{ {enabled} }} }}";

            string block = $"If {file} has a plugins {{ }} block that applies com.android.application, add {id} to it after that entry " +
                           $"and \"bugsee {{ ndk {{ {enabled} }} }}\" below the block (mavenCentral() has to be in the pluginManagement repositories of settings).";
            string viaApply;
            switch (root)
            {
                case RootShape.PluginsBlock:
                    viaApply = $"If {file} applies com.android.application with apply instead, two edits are needed: {id} apply false in the root plugins {{ }} block " +
                               $"(mavenCentral() in the pluginManagement repositories of settings), and {apply} after the AGP line in {file} (then \"{configure}\" enables NDK support).";
                    break;
                case RootShape.Buildscript:
                    viaApply = $"If {file} applies com.android.application with apply instead, put {classpath} in the root buildscript dependencies " +
                               $"(mavenCentral() in its repositories) and {apply} after the AGP line in {file} (then \"{configure}\" enables NDK support).";
                    break;
                default:
                    viaApply = $"If {file} applies com.android.application with apply instead, make the plugin available to the root build ({id} apply false in a root plugins {{ }} block, " +
                               $"or {classpath} in the root buildscript dependencies) and put {apply} after the AGP line in {file} (then \"{configure}\" enables NDK support).";
                    break;
            }

            return "To use it: " + block + " " + viaApply;
        }

        static string TryMarked(string text, string anchor, string payload)
        {
            try
            {
                return MarkedGradleBlock.Apply(text, anchor, payload);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        /// <summary>True when the launcher has Unity's apply-plugin line or the apply line this package already wrote.</summary>
        static bool UsesApplyStyle(string launcher)
        {
            var lines = MarkedGradleBlock.SplitLines(launcher, out _);
            var code = GradleMavenInjection.CodeLines(lines);
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i] == LauncherAnchor && code[i] == LauncherAnchor)
                {
                    return true;
                }

                if (lines[i].IndexOf(PluginMarker, StringComparison.Ordinal) >= 0 &&
                    code[i].TrimStart().StartsWith("apply plugin: '" + PluginId + "'", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        // How a build file declares the plugin: id('..'), apply plugin: '..', apply(plugin = ".."), classpath '..:bugsee-android-gradle-plugin:..'.
        // A string that merely names it (plugins.withId('..') { }, a println) is not a declaration.
        static readonly System.Text.RegularExpressions.Regex DeclarationShape = new System.Text.RegularExpressions.Regex(
            @"\bid\s*\(?\s*['""]" + System.Text.RegularExpressions.Regex.Escape(PluginId) + @"['""]" +
            @"|\bapply\s*(?:\(\s*)?plugin\s*[:=]\s*['""]" + System.Text.RegularExpressions.Regex.Escape(PluginId) + @"['""]" +
            @"|\bclasspath\s*\(?\s*['""][^'""]*:" + PluginArtifact + @":",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>
        /// True when a code line (not a comment, not a string that only names the plugin) declares the plugin without the
        /// package's marker: the project (or its own template) declares it, and that declaration wins.
        /// </summary>
        public static bool HasUnmarkedDeclaration(string text)
        {
            if (text == null)
            {
                return false;
            }

            var lines = MarkedGradleBlock.SplitLines(text, out _);
            var code = GradleMavenInjection.CodeLines(lines);
            for (int i = 0; i < lines.Count; i++)
            {
                // Ours: the marker, or the line an earlier package version wrote under its own marker comment.
                if (lines[i].IndexOf(PluginMarker, StringComparison.Ordinal) >= 0 ||
                    lines[i].IndexOf(LegacyMarker, StringComparison.Ordinal) >= 0 ||
                    (i > 0 && lines[i - 1].Trim() == LegacyMarker))
                {
                    continue;
                }

                if (DeclarationShape.IsMatch(code[i]))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Adds the plugin to the dependencies of the root's top-level <c>buildscript</c> block (and Maven Central to
        /// its repositories, which a classpath entry resolves from); null when the root has no such block or the
        /// repositories cannot be given Maven Central.
        /// </summary>
        static string TryClasspath(string root, GradleMavenInjection.GradleDsl dsl, string version, bool allowRepositoryChange, out bool addedRepository)
        {
            addedRepository = false;
            var lines = MarkedGradleBlock.SplitLines(root, out string newline);
            int open = GradleMavenInjection.FindTopLevelBlockOpen(lines, "buildscript");
            if (open < 0 || GradleMavenInjection.IsSingleLineBlock(lines[open]))
            {
                return null;
            }

            int close = GradleMavenInjection.FindBlockClose(lines, open);
            int deps = GradleMavenInjection.FindChildBlockOpen(lines, open, close, "dependencies");
            if (deps < 0 || GradleMavenInjection.IsSingleLineBlock(lines[deps]))
            {
                return null;
            }

            string entry = dsl == GradleMavenInjection.GradleDsl.Kotlin
                ? $"classpath(\"com.bugsee:{PluginArtifact}:{version}\") {PluginMarker}"
                : $"classpath 'com.bugsee:{PluginArtifact}:{version}' {PluginMarker}";
            int marked = lines.FindIndex(open, close - open + 1, l => l.IndexOf(PluginMarker, StringComparison.Ordinal) >= 0);
            if (marked >= 0)
            {
                lines[marked] = GradleMavenInjection.LeadingWhitespace(lines[marked]) + entry;
            }
            else
            {
                lines.Insert(deps + 1, GradleMavenInjection.LeadingWhitespace(lines[deps]) + "    " + entry);
            }

            string text = MarkedGradleBlock.JoinLines(lines, newline);
            var status = GradleMavenInjection.EnsureRepository(text, "buildscript", GradleMavenInjection.RepoKind.Classpath, out string withRepository);
            if (status == GradleMavenInjection.RepositoryStatus.BlockNotFound)
            {
                if (!allowRepositoryChange || !GradleMavenInjection.TryAddRepositoriesBlock(text, "buildscript", out withRepository))
                {
                    return null;
                }
            }
            else if (status == GradleMavenInjection.RepositoryStatus.Inserted && !allowRepositoryChange)
            {
                return null;
            }

            addedRepository = status != GradleMavenInjection.RepositoryStatus.AlreadyPresent;
            return withRepository;
        }

        /// <summary>
        /// Puts the plugin into the launcher's top-level <c>plugins { }</c> block after the entry that applies
        /// <c>com.android.application</c> (with <c>version</c> unless the root already makes the plugin resolvable), and the
        /// <c>bugsee { ndk { enabled } }</c> block under that block. A second run changes nothing; a changed plugin
        /// version replaces the marked line. Null (with <paramref name="failure"/>) when the launcher has no such block.
        /// </summary>
        static string ApplyToLauncherBlock(
            string launcher, GradleMavenInjection.GradleDsl dsl, string version, bool rootDeclares, out string failure)
        {
            failure = null;
            bool kotlin = dsl == GradleMavenInjection.GradleDsl.Kotlin;
            var lines = MarkedGradleBlock.SplitLines(launcher, out string newline);
            var code = GradleMavenInjection.CodeLines(lines);

            string entry = kotlin
                ? $"id(\"{PluginId}\")" + (rootDeclares ? string.Empty : $" version \"{version}\"")
                : $"id '{PluginId}'" + (rootDeclares ? string.Empty : $" version '{version}'");
            string enabled = kotlin ? "enabled.set(true)" : "enabled = true";

            int open = GradleMavenInjection.FindTopLevelBlockOpen(lines, "plugins");
            if (open < 0)
            {
                failure = "the launcher applies com.android.application without a plugins { } block, and the plugin must be applied after it.";
                return null;
            }

            if (GradleMavenInjection.IsSingleLineBlock(lines[open]))
            {
                failure = "the launcher's plugins { } block is written on one line.";
                return null;
            }

            int close = GradleMavenInjection.FindBlockClose(lines, open);
            int agp = -1;
            for (int i = open + 1; i < close; i++)
            {
                if (code[i].IndexOf("com.android.application", StringComparison.Ordinal) >= 0 ||
                    System.Text.RegularExpressions.Regex.IsMatch(code[i], @"alias\s*\([^)]*application\s*\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    agp = i;
                }
            }

            int marked = lines.FindIndex(open, close - open + 1, l => l.IndexOf(PluginMarker, StringComparison.Ordinal) >= 0);
            if (marked >= 0 && !lines[marked].TrimStart().StartsWith("id", StringComparison.Ordinal))
            {
                failure = "the launcher carries a marked Bugsee line of another shape from an earlier build.";
                return null;
            }

            if (marked < 0 && agp < 0)
            {
                failure = "the launcher's plugins { } block does not apply com.android.application, and the plugin must be applied after it.";
                return null;
            }

            if (marked >= 0)
            {
                lines[marked] = GradleMavenInjection.LeadingWhitespace(lines[marked]) + entry + " " + PluginMarker;
            }
            else
            {
                // After the last entry: anything between the AGP entry and the end of the block applies later still.
                string indent = GradleMavenInjection.LeadingWhitespace(lines[agp]);
                lines.Insert(close, indent + entry + " " + PluginMarker);
                close++;
            }

            var ndk = new List<string>
            {
                "bugsee {",
                "    // App token is supplied at runtime via Bugsee.Launch.",
                "    ndk {",
                "        " + enabled,
                "    }",
                "} " + NdkMarker,
            };
            int ndkEnd = lines.FindIndex(l => l.IndexOf(NdkMarker, StringComparison.Ordinal) >= 0);
            if (ndkEnd >= 0)
            {
                int ndkStart = ndkEnd;
                while (ndkStart >= 0 && !lines[ndkStart].TrimStart().StartsWith("bugsee {", StringComparison.Ordinal))
                {
                    ndkStart--;
                }

                if (ndkStart >= 0)
                {
                    lines.RemoveRange(ndkStart, ndkEnd - ndkStart + 1);
                    lines.InsertRange(ndkStart, ndk);
                }
            }
            else if (!lines.Exists(l => l.TrimStart().StartsWith("bugsee {", StringComparison.Ordinal)))
            {
                var insert = new List<string> { string.Empty };
                insert.AddRange(ndk);
                lines.InsertRange(close + 1, insert);
            }

            return MarkedGradleBlock.JoinLines(lines, newline);
        }
    }
}
