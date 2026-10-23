using System;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Windows paths over 260 characters. The Editor's Mono 6.13 (Unity 2021.3) lists, stats and enumerates them but
    /// <c>FileStream</c>/<c>File.ReadAllBytes</c> fail with <see cref="System.IO.DirectoryNotFoundException"/>, whatever
    /// <c>LongPathsEnabled</c> says. Reads of build outputs therefore go through the extended-length form
    /// (<c>\\?\C:\...</c>) when the plain open fails; this class decides when and builds the path. No file access here.
    /// </summary>
    public static class LongPathPolicy
    {
        /// <summary>Paths from this length on are retried in the extended form (MAX_PATH is 260, a directory's limit 248).</summary>
        public const int ExtendFrom = 240;

        const string Prefix = @"\\?\";
        const string UncPrefix = @"\\?\UNC\";

        /// <summary>Whether a failed open of <paramref name="path"/> is worth retrying in the extended form.</summary>
        public static bool NeedsExtended(string path, bool windows) =>
            windows && !string.IsNullOrEmpty(path) && path.Length >= ExtendFrom && !IsExtended(path) && Extend(path) != path;

        public static bool IsExtended(string path) =>
            path != null && path.StartsWith(Prefix, StringComparison.Ordinal);

        /// <summary>
        /// The extended-length form of an absolute Windows path (drive or UNC), with <c>/</c> turned into <c>\</c>
        /// (the prefix switches the system's normalization off). Anything else (relative, already extended) is returned
        /// unchanged. The path must already be full (no <c>.</c> or <c>..</c>).
        /// </summary>
        public static string Extend(string path)
        {
            if (string.IsNullOrEmpty(path) || IsExtended(path))
            {
                return path;
            }

            var p = path.Replace('/', '\\');
            if (p.StartsWith(@"\\.\", StringComparison.Ordinal))
            {
                return path;
            }

            if (p.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return UncPrefix + p.Substring(2);
            }

            if (p.Length >= 3 && char.IsLetter(p[0]) && p[1] == ':' && p[2] == '\\')
            {
                return Prefix + p;
            }

            return path;
        }

        /// <summary>
        /// The text for an Editor read failure of a build output at <paramref name="path"/>: names the length when the path
        /// is at or over the limit, so the warning points at the cause instead of a bare "could not find a part of the path".
        /// </summary>
        public static string Describe(string path, string message)
        {
            if (path != null && path.Length >= 260)
            {
                return message + " (the path is " + path.Length + " characters long; Windows tools and the Editor's Mono limit paths to 260, build to a shorter folder)";
            }

            return message;
        }
    }
}
