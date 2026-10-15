using System;
using System.Collections.Generic;
using System.IO;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Picks the launcher icon the Bugsee Gradle plugin attaches to the mapping upload (<c>bugsee-cli --icon</c>, shown
    /// as the app's icon in the dashboard): the highest-density raster <c>app_icon</c> of the generated launcher
    /// module (Unity names it <c>@mipmap/app_icon</c>). Adaptive icons are XML and have no raster to send.
    /// </summary>
    public static class AndroidIconPicker
    {
        static readonly string[] Densities = { "ldpi", "mdpi", "hdpi", "xhdpi", "xxhdpi", "xxxhdpi" };
        static readonly string[] Extensions = { ".png", ".webp", ".jpg", ".jpeg" };

        /// <summary>The best candidate among <paramref name="files"/> (paths ending <c>mipmap-*/app_icon.png</c> and similar), or null.</summary>
        public static string Pick(IEnumerable<string> files)
        {
            string best = null;
            int bestRank = -1;
            foreach (string file in files)
            {
                int rank = Rank(file);
                if (rank > bestRank)
                {
                    best = file;
                    bestRank = rank;
                }
            }

            return best;
        }

        static int Rank(string file)
        {
            string normalized = (file ?? "").Replace('\\', '/');
            string name = Path.GetFileName(normalized);
            bool raster = false;
            foreach (string ext in Extensions)
            {
                raster |= string.Equals(name, "app_icon" + ext, StringComparison.OrdinalIgnoreCase);
            }

            if (!raster)
            {
                return -1;
            }

            string folder = Path.GetFileName(Path.GetDirectoryName(normalized) ?? "");
            for (int i = Densities.Length - 1; i >= 0; i--)
            {
                if (folder.EndsWith("-" + Densities[i], StringComparison.OrdinalIgnoreCase))
                {
                    return i + 1;
                }
            }

            return 0;
        }
    }
}
