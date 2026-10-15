using System;
using System.Text.RegularExpressions;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Edits to Unity's own default Gradle templates (copied into Assets/Plugins/Android when a project has none).
    /// The package never writes its own copy of a template: the Android Gradle plugin and Gradle versions belong to
    /// the Unity line (AGP 7.4.2 on 2021.3, AGP 9.0.0 on Unity 6000.0.84f1) and a pinned copy breaks every other line.
    /// </summary>
    public static class GradleTemplateDefaults
    {
        static readonly Regex ApplicationPluginVersion = new Regex(
            @"id\s+['""]com\.android\.application['""]\s+version\s+['""](?<v>[^'""]+)['""]",
            RegexOptions.CultureInvariant);

        static readonly Regex ClasspathVersion = new Regex(
            @"com\.android\.tools\.build:gradle:(?<v>[0-9][^'""\s]*)",
            RegexOptions.CultureInvariant);

        /// <summary>The Android Gradle plugin major a base project template pins, or -1 when it pins none (or not literally).</summary>
        public static int PinnedAgpMajor(string baseProjectTemplate)
        {
            if (baseProjectTemplate == null)
            {
                return -1;
            }

            Match m = ApplicationPluginVersion.Match(baseProjectTemplate);
            if (!m.Success)
            {
                m = ClasspathVersion.Match(baseProjectTemplate);
            }

            if (!m.Success)
            {
                return -1;
            }

            string v = m.Groups["v"].Value;
            int dot = v.IndexOf('.');
            return int.TryParse(dot < 0 ? v : v.Substring(0, dot), out int major) ? major : -1;
        }

        /// <summary>
        /// True when <paramref name="projectTemplate"/> pins another AGP major than Unity's default: Gradle in this
        /// Unity cannot run it (AGP 7 calls DependencyHandler.module, removed in Gradle 9).
        /// </summary>
        public static bool PinsDifferentAgpMajor(string projectTemplate, string unityDefaultTemplate)
        {
            int project = PinnedAgpMajor(projectTemplate);
            int unity = PinnedAgpMajor(unityDefaultTemplate);
            return project > 0 && unity > 0 && project != unity;
        }

        /// <summary>
        /// Unity 2021.3's default launcher template sets no native debug symbol level, so the release build type
        /// gets <c>ndk { debugSymbolLevel 'FULL' }</c> (DWARF line programs, needed for the IL2CPP line mapping).
        /// Unity 6's template already carries a <c>debugSymbolLevel</c>, which Unity sets from the Player Settings.
        /// A template that already mentions <c>debugSymbolLevel</c>, or has no single <c>release {</c> line, is returned unchanged.
        /// </summary>
        public static string EnsureReleaseDebugSymbols(string launcherTemplate)
        {
            if (launcherTemplate == null)
            {
                throw new ArgumentNullException(nameof(launcherTemplate));
            }

            if (launcherTemplate.IndexOf("debugSymbolLevel", StringComparison.Ordinal) >= 0)
            {
                return launcherTemplate;
            }

            MatchCollection releases = Regex.Matches(launcherTemplate, @"^([ \t]*)release[ \t]*\{[ \t]*\r?$", RegexOptions.Multiline);
            if (releases.Count != 1)
            {
                return launcherTemplate;
            }

            Match release = releases[0];
            string indent = release.Groups[1].Value + "    ";
            string nl = launcherTemplate.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
            string block = nl + indent + "ndk {" + nl + indent + "    debugSymbolLevel 'FULL'" + nl + indent + "}";
            int insertAt = release.Index + release.Length - (release.Value.EndsWith("\r") ? 1 : 0);
            return launcherTemplate.Insert(insertAt, block);
        }
    }
}
