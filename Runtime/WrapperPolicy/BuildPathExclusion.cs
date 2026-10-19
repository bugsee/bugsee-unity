using System;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Which files under a build output folder are Unity's intermediate artifacts rather than this build's own:
    /// anything inside a "Library", "Temp", "Bee" or "obj" folder holds duplicate or half-built .so/.sym/line-map
    /// files that must not be uploaded.
    ///
    /// The rule applies to the path BELOW the folder the scan starts from, never to the absolute path. A project
    /// kept under ~/Library/CloudStorage (OneDrive, iCloud Drive), C:\Temp, %TEMP%, /tmp or any folder that
    /// happens to be called obj/Bee/Library would otherwise lose every symbol file and mapping.
    ///
    /// Purely lexical (no symlink resolution): a scan returns paths spelled under its root, so a symlinked root or
    /// a symlinked child is judged by the names the scan walked through, which is what the scan sees.
    /// </summary>
    public static class BuildPathExclusion
    {
        static readonly string[] ExcludedFolders = { "Library", "Temp", "Bee", "obj" };

        /// <summary>
        /// True when <paramref name="path"/> lies inside an excluded folder under <paramref name="root"/>. The path
        /// is a folder or file the scan found below the root, or already relative to it. Both separators are
        /// accepted and names compare case-insensitively (macOS and Windows file systems do). The last segment is
        /// the file or folder itself and is not tested; the root's own name and everything above it are not tested
        /// either. A rooted path that is not under the root cannot come from a scan of that root; it is tested whole.
        /// </summary>
        public static bool IsExcludedPath(string path, string root)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var p = path.Replace('\\', '/');
            var rel = p;
            if (!string.IsNullOrEmpty(root))
            {
                var r = root.Replace('\\', '/').TrimEnd('/');
                if (r.Length > 0 && p.Length > r.Length && p[r.Length] == '/'
                    && p.StartsWith(r, StringComparison.OrdinalIgnoreCase))
                {
                    rel = p.Substring(r.Length + 1);
                }
                else if (r.Length == 0 && p.StartsWith("/", StringComparison.Ordinal))
                {
                    rel = p.TrimStart('/'); // the root is "/"
                }
            }

            var segments = rel.Split('/');
            for (var i = 0; i < segments.Length - 1; i++)
            {
                foreach (var name in ExcludedFolders)
                {
                    if (string.Equals(segments[i], name, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            return false;
        }
    }
}
