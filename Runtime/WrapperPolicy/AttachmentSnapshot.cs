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

        // Application.temporaryCachePath, captured on the main thread at startup (the Unity API is main-thread only and the
        // report handlers run elsewhere). Null until then, and in tests.
        static volatile string _cacheRoot;
        static volatile Action<string> _warn;

        public static void Configure(string cacheRoot, Action<string> warn)
        {
            _cacheRoot = cacheRoot;
            _warn = warn;
        }

        /// <summary>The directories snapshots are tried in, in order.</summary>
        public static string[] Roots()
        {
            var cache = _cacheRoot;
            string tmp = null;
            try { tmp = Path.GetTempPath(); } catch (Exception) { }
            if (string.IsNullOrEmpty(cache)) return string.IsNullOrEmpty(tmp) ? new string[0] : new[] { tmp };
            return string.IsNullOrEmpty(tmp) || tmp == cache ? new[] { cache } : new[] { cache, tmp };
        }

        /// <summary>The snapshot directory under the first root that works, creating it; null when none can be created.</summary>
        public static string DirectoryPath()
        {
            foreach (var root in Roots())
            {
                try
                {
                    var dir = Path.Combine(root, DirectoryName);
                    Directory.CreateDirectory(dir);
                    return dir;
                }
                catch (Exception) { }
            }
            return null;
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
                    var dir = Path.Combine(root, DirectoryName);
                    Directory.CreateDirectory(dir);
                    var dest = Path.Combine(dir, Guid.NewGuid().ToString("N") + ext);
                    File.Copy(sourcePath, dest, true);
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
