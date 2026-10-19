using System;
using System.Collections.Generic;
using System.Text;

namespace Bugsee.WrapperPolicy
{
    /// <summary>What Unity's Android build produced: a single APK, a single AAB, or an exported Gradle project folder.</summary>
    public enum AndroidOutputFormat
    {
        Apk,
        Aab,
        ExportProject,
    }

    /// <summary>A file that looked like this build's R8 mapping, with what decides whether it is usable.</summary>
    public readonly struct MappingFileInfo
    {
        public MappingFileInfo(string path, long length)
        {
            Path = path;
            Length = length;
        }

        public string Path { get; }
        public long Length { get; }
    }

    /// <summary>
    /// The start of a build as the Editor reports it. <c>BuildSummary.buildStartedAt</c> is built from UTC ticks but
    /// carries <c>DateTimeKind.Unspecified</c>, so <c>ToUniversalTime()</c> on it applies the machine's UTC offset a
    /// second time (hours early east of UTC, in the future west of it). This is the one place that reads it as UTC.
    /// </summary>
    public static class BuildClock
    {
        public static DateTime StartedUtc(DateTime buildStartedAt) => DateTime.SpecifyKind(buildStartedAt, DateTimeKind.Utc);
    }

    /// <summary>
    /// The Editor warning for an Android build whose R8 mapping did not reach the upload. With Minify on, the Java
    /// frames of every crash from that build lose their line numbers (and the names of classes R8 may rename) unless
    /// its mapping.txt is uploaded; before this an Export Project, which has no mapping at all, was never reported.
    ///
    /// A mapping is usable when it exists and is not empty. Its age is deliberately not judged: Unity deletes and
    /// recreates <c>&lt;player&gt;_mapping.txt</c> on every build, but copies Gradle's file with its modification time,
    /// and Gradle does not rewrite mapping.txt when R8's task is up to date, so a good mapping can predate the build.
    /// </summary>
    public static class AndroidMappingNotice
    {
        /// <summary>
        /// Whether R8 runs for this build: Unity applies Player Settings' Minify Release to a release build and Minify
        /// Debug to a development one.
        /// </summary>
        public static bool MinifyIsOn(bool developmentBuild, bool minifyRelease, bool minifyDebug) =>
            developmentBuild ? minifyDebug : minifyRelease;

        /// <summary>A mapping with content: R8 writes an empty one when nothing was obfuscated.</summary>
        public static bool IsUsable(MappingFileInfo file) => file.Length > 0;

        /// <summary>
        /// The warning to log, or null when there is nothing to say (Minify off, or a usable mapping was found).
        /// <paramref name="found"/> holds every file with this build's mapping name that was seen, whatever its size;
        /// <paramref name="searched"/> the folders that were looked in.
        /// </summary>
        public static string MissingMapping(
            bool minify,
            AndroidOutputFormat format,
            string expectedName,
            IList<string> searched,
            IList<MappingFileInfo> found,
            string version,
            string build)
        {
            if (!minify)
            {
                return null;
            }

            var seen = found ?? new List<MappingFileInfo>();
            foreach (var file in seen)
            {
                if (IsUsable(file))
                {
                    return null;
                }
            }

            var text = new StringBuilder("Bugsee: Minify (R8) is on for this ").Append(FormatName(format)).Append(" build, but ");
            if (seen.Count == 0)
            {
                text.Append("no R8 mapping (").Append(expectedName).Append(") was found, so none can be uploaded");
            }
            else
            {
                text.Append("the R8 mapping found has no content (");
                for (var i = 0; i < seen.Count; i++)
                {
                    if (i > 0) text.Append("; ");
                    var file = seen[i];
                    text.Append(file.Path).Append(": ").Append(file.Length < 0 ? "unreadable" : "empty");
                }

                text.Append(")");
            }

            text.Append(". Java and Kotlin frames of crashes from this build will lose their line numbers and, for classes R8 may rename, their names in Bugsee.");
            text.Append(" Searched: ").Append(Searched(searched)).Append(".");
            text.Append(Where(format));
            text.Append(" Upload it by hand once you have it: ").Append(UploadCommand("<path to mapping.txt>", version, build)).Append(UploadNote);
            return text.ToString();
        }

        /// <summary>
        /// The one line for a build whose mapping exists but is not uploaded because the upload is off (no app token,
        /// or BUGSEE_NO_SYMBOL_UPLOAD). A Minify build whose mapping is never uploaded is as unreadable as one that has none.
        /// </summary>
        public static string MappingNotUploaded(string mappingPath, string reason, string version, string build) =>
            "Bugsee: the R8 mapping " + mappingPath + " was not uploaded (" + reason + "), so Java and Kotlin frames of crashes from this build will lose their line numbers and, for classes R8 may rename, their names in Bugsee. " +
            "Upload it by hand: " + UploadCommand(mappingPath, version, build) + UploadNote;

        const string UploadNote = " (with BUGSEE_APP_TOKEN set in the environment).";

        /// <summary>The CLI call that uploads a mapping by hand (the id is computed from the file, as the app's asset id is).</summary>
        public static string UploadCommand(string mappingPath, string version, string build) =>
            "bugsee-cli debug-files upload \"" + mappingPath + "\" --type proguard --version " + version + " --build " + build;

        static string FormatName(AndroidOutputFormat format)
        {
            switch (format)
            {
                case AndroidOutputFormat.Aab: return "App Bundle (AAB)";
                case AndroidOutputFormat.ExportProject: return "Export Project (Gradle project)";
                default: return "APK";
            }
        }

        static string Searched(IList<string> searched)
        {
            if (searched == null || searched.Count == 0)
            {
                return "nothing (the build has no output path)";
            }

            return string.Join(", ", searched) + " (recursively)";
        }

        // Where the mapping lands, so the user knows what to look for. The exact folders are Gradle's, not Unity's.
        static string Where(AndroidOutputFormat format)
        {
            switch (format)
            {
                case AndroidOutputFormat.ExportProject:
                    return " An exported Gradle project is not built by Unity: Gradle writes the mapping when you build it, to launcher/build/outputs/mapping/<variant>/mapping.txt inside the project, so build it there and upload that file. (On AGP 8.6 and later the exported project carries the Bugsee Gradle plugin, which uploads the mapping itself when it has the app token; a second upload of the same mapping is harmless.)";
                case AndroidOutputFormat.Aab:
                    return " Unity copies the mapping beside the bundle as <name>_mapping.txt; when it is missing, take launcher/build/outputs/mapping/<variant>/mapping.txt from Unity's Gradle project (Library/Bee/Android/Prj/<IL2CPP|Mono2x>/Gradle).";
                default:
                    return " Unity copies the mapping beside the APK as <name>_mapping.txt; when it is missing, take launcher/build/outputs/mapping/<variant>/mapping.txt from Unity's Gradle project.";
            }
        }
    }
}
