using System;
using System.IO;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Copies a file attachment of a managed report to a private location at add time, so it survives until the report is
    /// uploaded. Path.GetTempPath() is not usable on every device (on Android 11 it is /data/local/tmp, which the app cannot
    /// write), so the copy goes under the app's own temporary cache directory first.
    /// </summary>
    public static class AttachmentSnapshot
    {
        const string DirectoryName = "bugsee-unity-report-attachments";

        // The app's private cache directories, captured on the main thread at startup (the Unity APIs are main-thread only
        // and the report handlers run elsewhere). Empty until then, and in tests.
        static volatile string[] _preferred = new string[0];
        static volatile Action<string> _warn;

        public static void Configure(string[] preferredRoots, Action<string> warn)
        {
            _preferred = preferredRoots ?? new string[0];
            _warn = warn;
        }

        public static bool IsConfigured => _preferred.Length > 0;

        static string Normalize(string path)
        {
            try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch (Exception) { return path; }
        }

        /// <summary>The directories snapshots are tried in, in order: the configured ones, then the system temp path.</summary>
        public static string[] Roots()
        {
            var roots = new System.Collections.Generic.List<string>();
            var seen = new System.Collections.Generic.HashSet<string>();
            foreach (var r in _preferred)
                if (!string.IsNullOrEmpty(r) && seen.Add(Normalize(r))) roots.Add(r);
            string tmp = null;
            try { tmp = Path.GetTempPath(); } catch (Exception) { }
            if (!string.IsNullOrEmpty(tmp) && seen.Add(Normalize(tmp))) roots.Add(tmp);
            return roots.ToArray();
        }

        /// <summary>The snapshot directory under the first root that works, creating it; null when none can be created.</summary>
        public static string DirectoryPath()
        {
            foreach (var root in Roots())
            {
                try { return EnsureDirectory(root); }
                catch (Exception) { }
            }
            return null;
        }

        static string EnsureDirectory(string root)
        {
            var dir = Path.Combine(root, DirectoryName);
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>
        /// Deletes snapshot files older than maxAge under every root: copies a crash or kill left behind (the report that
        /// owned them is gone with the process). Best effort; meant for a background thread at startup.
        /// </summary>
        public static void CleanStale(string[] roots, TimeSpan maxAge)
        {
            foreach (var root in roots)
            {
                try
                {
                    var dir = Path.Combine(root, DirectoryName);
                    if (!Directory.Exists(dir)) continue;
                    var cutoff = DateTime.UtcNow - maxAge;
                    foreach (var file in Directory.GetFiles(dir))
                    {
                        try { if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file); }
                        catch (Exception) { }
                    }
                }
                catch (Exception) { }
            }
        }

        /// <summary>Copies the file; returns the copy's path, or null (with a warning) when no location is writable.</summary>
        public static string TryCopy(string sourcePath)
        {
            return TryCopy(sourcePath, Roots(), _warn);
        }

        public static string TryCopy(string sourcePath, string[] roots, Action<string> warn)
        {
            var ext = Path.GetExtension(sourcePath);
            if (string.IsNullOrEmpty(ext)) ext = ".bin";
            Exception last = null;
            string lastRoot = null;
            foreach (var root in roots)
            {
                try
                {
                    var dest = Path.Combine(EnsureDirectory(root), Guid.NewGuid().ToString("N") + ext);
                    File.Copy(sourcePath, dest, true);
                    // File.Copy can carry the source's timestamp over; stamp the copy so CleanStale measures the snapshot's age.
                    try { File.SetLastWriteTimeUtc(dest, DateTime.UtcNow); } catch (Exception) { }
                    return dest;
                }
                catch (Exception ex)
                {
                    last = ex;
                    lastRoot = root;
                }
            }
            warn?.Invoke("AddAttachmentFile: could not copy '" + sourcePath + "' to a temporary location"
                + (lastRoot == null ? " (no temporary directory is available)" : " (" + lastRoot + "): " + last?.Message)
                + "; the attachment was not added.");
            return null;
        }
    }
}
