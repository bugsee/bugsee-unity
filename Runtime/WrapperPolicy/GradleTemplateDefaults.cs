using System;
using System.Text.RegularExpressions;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Reads and fixes the Gradle files of an Android build. The package creates no Gradle template: the Android
    /// Gradle plugin and Gradle versions belong to the Unity line (AGP 7.4.2 on 2021.3, AGP 9.0.0 on 6000.0.84f1) and a
    /// pinned copy breaks every other line. It patches the generated project instead, and only removes the template
    /// files earlier package versions wrote (<see cref="IsLegacyPackageBaseTemplate"/>, <see cref="IsLegacyPackageLauncherTemplate"/>).
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

        /// <summary>The (major, minor) of the AGP a base/root build file pins, or false when it pins none literally.</summary>
        public static bool TryGetAgpVersion(string text, out int major, out int minor)
        {
            major = minor = -1;
            if (text == null)
            {
                return false;
            }

            Match m = ApplicationPluginVersion.Match(text);
            if (!m.Success)
            {
                m = ClasspathVersion.Match(text);
            }

            if (!m.Success)
            {
                return false;
            }

            string[] parts = m.Groups["v"].Value.Split('.');
            if (!int.TryParse(parts[0], out major))
            {
                return false;
            }

            minor = parts.Length > 1 && int.TryParse(parts[1], out int mi) ? mi : 0;
            return true;
        }

        /// <summary>Bugsee Gradle plugin 4.x needs AGP 8.6 or newer (Gradle 8.7+). False when the AGP version is unknown.</summary>
        public static bool AgpMeetsBugseePlugin(string rootBuildFile)
        {
            return TryGetAgpVersion(rootBuildFile, out int major, out int minor) &&
                   (major > 8 || (major == 8 && minor >= 6));
        }

        const string PluginMarker = "bugsee:gradle-plugin";

        // The baseProjectTemplate.gradle earlier package versions wrote on every Unity line (Unity 2021.3's default
        // with its AGP 7.4.2 pin, plus one marked Bugsee plugin line when the plugin was applied).
        static readonly string[] LegacyBaseBody =
        {
            "plugins {",
            "// If you are changing the Android Gradle Plugin version, make sure it is compatible with the Gradle version preinstalled with Unity",
            "// See which Gradle version is preinstalled with Unity here https://docs.unity3d.com/Manual/android-gradle-overview.html",
            "// See official Gradle and Android Gradle Plugin compatibility table here https://developer.android.com/studio/releases/gradle-plugin#updating-gradle",
            "// To specify a custom Gradle version in Unity, go do \"Preferences > External Tools\", uncheck \"Gradle Installed with Unity (recommended)\" and specify a path to a custom Gradle version",
            "id 'com.android.application' version '7.4.2' apply false",
            "id 'com.android.library' version '7.4.2' apply false",
            "**BUILD_SCRIPT_DEPS**",
            "}",
            "",
            "task clean(type: Delete) {",
            "delete rootProject.buildDir",
            "}",
        };

        static string[] NormalizedLines(string text)
        {
            var lines = new System.Collections.Generic.List<string>();
            foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                lines.Add(raw.Trim());
            }

            while (lines.Count > 0 && lines[lines.Count - 1].Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }

            return lines.ToArray();
        }

        /// <summary>
        /// True for a base template an earlier package version generated: exactly the AGP 7.4.2 body above, apart from
        /// whitespace and the marked Bugsee plugin line. A template with any other content is the project's own.
        /// </summary>
        public static bool IsLegacyPackageBaseTemplate(string text)
        {
            if (text == null)
            {
                return false;
            }

            var kept = new System.Collections.Generic.List<string>();
            foreach (string line in NormalizedLines(text))
            {
                if (line.IndexOf(PluginMarker, StringComparison.Ordinal) < 0)
                {
                    kept.Add(line);
                }
            }

            return kept.Count == LegacyBaseBody.Length && System.Linq.Enumerable.SequenceEqual(kept, LegacyBaseBody);
        }

        /// <summary>
        /// True for a launcher template an earlier package version generated (Unity 2021.3's Groovy layout, which
        /// Unity 6's Gradle cannot run): it carries the package's <c>bugsee:gradle-plugin</c> marker, or the
        /// release <c>debugSymbolLevel 'FULL'</c> block the package added to Unity 2021.3's template, and still uses the
        /// removed <c>lintOptions</c>/<c>aaptOptions</c> DSL.
        /// </summary>
        public static bool IsLegacyPackageLauncherTemplate(string text)
        {
            if (text == null)
            {
                return false;
            }

            bool oldDsl = text.IndexOf("lintOptions", StringComparison.Ordinal) >= 0 ||
                          text.IndexOf("aaptOptions", StringComparison.Ordinal) >= 0;
            bool ours = text.IndexOf(PluginMarker, StringComparison.Ordinal) >= 0 ||
                        (text.IndexOf("debugSymbolLevel 'FULL'", StringComparison.Ordinal) >= 0 &&
                         text.IndexOf("**LAUNCHER_SOURCE_BUILD_SETUP**", StringComparison.Ordinal) >= 0);
            return oldDsl && ours;
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
