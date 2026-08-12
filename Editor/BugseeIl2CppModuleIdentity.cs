#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Bugsee.Editor
{
    /// <summary>
    /// Derive IL2CPP module identities (GNU build-id / Mach-O LC_UUID) from
    /// co-located native binaries when <c>BUGSEE_IL2CPP_UUIDS</c> is unset.
    /// </summary>
    internal static class BugseeIl2CppModuleIdentity
    {
        static readonly string[] CandidateNames =
        {
            "libil2cpp.so",
            "UnityFramework",
            "GameAssembly.dll",
            "GameAssembly.so",
            "GameAssembly",
        };

        public static List<string> DiscoverFromBuild(BuildReport report)
        {
            var found = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var candidates = new List<(string path, DateTime mtime, int score)>();

            foreach (var root in BuildRoots(report))
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                {
                    continue;
                }
                try
                {
                    CollectCandidateBinaries(root, candidates);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Bugsee: native identity scan failed under {root}: {ex.Message}");
                }
            }

            // Prefer higher score (libil2cpp / UnityFramework under build output),
            // then newest mtime — never take a low-score Library/Bee hit when a
            // build-output candidate exists.
            candidates.Sort((a, b) =>
            {
                var byScore = b.score.CompareTo(a.score);
                return byScore != 0 ? byScore : b.mtime.CompareTo(a.mtime);
            });

            var strongHits = 0;
            foreach (var c in candidates)
            {
                if (c.score < 10)
                {
                    // Skip project-wide junk (Library/Temp) unless nothing better.
                    if (candidates.Exists(x => x.score >= 10))
                    {
                        continue;
                    }
                }
                var before = found.Count;
                TryCollectIdentities(c.path, found, seen);
                if (c.score >= 10 && found.Count > before)
                {
                    strongHits++;
                }
                // Collect every strong libil2cpp / UnityFramework hit (multi-ABI).
                // Only stop early when falling back to low-score junk after a miss.
                if (strongHits == 0 && found.Count > 0 && c.score < 10)
                {
                    break;
                }
            }
            return found;
        }

        static void CollectCandidateBinaries(string root, List<(string path, DateTime mtime, int score)> into)
        {
            foreach (var name in CandidateNames)
            {
                foreach (var path in Directory.EnumerateFiles(root, name, SearchOption.AllDirectories))
                {
                    if (IsExcludedPath(path))
                    {
                        continue;
                    }
                    DateTime mtime;
                    try { mtime = File.GetLastWriteTimeUtc(path); }
                    catch { mtime = DateTime.MinValue; }
                    into.Add((path, mtime, ScorePath(path, name)));
                }
            }
            foreach (var dir in Directory.EnumerateDirectories(root, "*.framework", SearchOption.AllDirectories))
            {
                if (IsExcludedPath(dir))
                {
                    continue;
                }
                var binary = Path.Combine(dir, Path.GetFileNameWithoutExtension(dir));
                if (!File.Exists(binary))
                {
                    continue;
                }
                if (!Path.GetFileNameWithoutExtension(dir)
                        .Equals("UnityFramework", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                DateTime mtime;
                try { mtime = File.GetLastWriteTimeUtc(binary); }
                catch { mtime = DateTime.MinValue; }
                into.Add((binary, mtime, ScorePath(binary, "UnityFramework")));
            }
        }

        static bool IsExcludedPath(string path)
        {
            var norm = path.Replace('\\', '/');
            return norm.IndexOf("/Library/", StringComparison.OrdinalIgnoreCase) >= 0
                || norm.IndexOf("/Temp/", StringComparison.OrdinalIgnoreCase) >= 0
                || norm.IndexOf("/obj/", StringComparison.OrdinalIgnoreCase) >= 0
                || norm.IndexOf("/Bee/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static int ScorePath(string path, string name)
        {
            var norm = path.Replace('\\', '/');
            var score = 0;
            if (name.IndexOf("libil2cpp", StringComparison.OrdinalIgnoreCase) >= 0
                || name.Equals("UnityFramework", StringComparison.OrdinalIgnoreCase))
            {
                score += 50;
            }
            if (norm.IndexOf("/jniLibs/", StringComparison.OrdinalIgnoreCase) >= 0
                || norm.IndexOf(".apk", StringComparison.OrdinalIgnoreCase) >= 0
                || norm.IndexOf(".aab", StringComparison.OrdinalIgnoreCase) >= 0
                || norm.IndexOf(".app/", StringComparison.OrdinalIgnoreCase) >= 0
                || norm.IndexOf("/Frameworks/", StringComparison.OrdinalIgnoreCase) >= 0
                || norm.IndexOf("Il2CppOutputProject", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score += 40;
            }
            if (norm.IndexOf("/symbols/", StringComparison.OrdinalIgnoreCase) >= 0
                || norm.IndexOf(".dSYM", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score += 20;
            }
            return score;
        }

        static IEnumerable<string> BuildRoots(BuildReport report)
        {
            // Only scan the player output tree — never the whole project cwd
            // (that pulls stale Library/Bee binaries and poisons UUID identity).
            if (!string.IsNullOrEmpty(report.summary.outputPath))
            {
                var outPath = report.summary.outputPath;
                yield return outPath;
                var parent = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(parent))
                {
                    yield return parent;
                }
            }
        }

        static void TryCollectIdentities(string path, List<string> found, HashSet<string> seen)
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length < 16)
                {
                    return;
                }
                // ELF
                if (bytes[0] == 0x7f && bytes[1] == (byte)'E' && bytes[2] == (byte)'L' && bytes[3] == (byte)'F')
                {
                    var id = TryReadElfBuildId(bytes);
                    if (!string.IsNullOrEmpty(id) && seen.Add(id))
                    {
                        found.Add(id);
                    }
                    return;
                }
                // Mach-O (thin or fat)
                foreach (var id in TryReadMachOUuids(bytes))
                {
                    if (seen.Add(id))
                    {
                        found.Add(id);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Bugsee: failed reading identity from {path}: {ex.Message}");
            }
        }

        /// <summary>NT_GNU_BUILD_ID note → lowercase hex (no dashes).</summary>
        internal static string TryReadElfBuildId(byte[] elf)
        {
            if (elf.Length < 52)
            {
                return null;
            }
            bool le = elf[5] == 1;
            bool is64 = elf[4] == 2;
            int ePhOff = is64 ? ReadI32(elf, 32, le) : ReadI32(elf, 28, le);
            int ePhEntSize = is64 ? ReadI16(elf, 54, le) : ReadI16(elf, 42, le);
            int ePhNum = is64 ? ReadI16(elf, 56, le) : ReadI16(elf, 44, le);
            if (ePhOff <= 0 || ePhEntSize <= 0 || ePhNum <= 0)
            {
                return null;
            }

            for (int i = 0; i < ePhNum; i++)
            {
                int ph = ePhOff + i * ePhEntSize;
                if (ph + ePhEntSize > elf.Length)
                {
                    break;
                }
                int pType = ReadI32(elf, ph, le);
                if (pType != 4) // PT_NOTE
                {
                    continue;
                }
                long pOffset = is64 ? ReadI64(elf, ph + 8, le) : ReadI32(elf, ph + 4, le);
                long pFilesz = is64 ? ReadI64(elf, ph + 32, le) : ReadI32(elf, ph + 16, le);
                if (pOffset < 0 || pFilesz <= 0 || pOffset + pFilesz > elf.Length)
                {
                    continue;
                }
                int off = (int)pOffset;
                int end = off + (int)pFilesz;
                while (off + 12 <= end)
                {
                    int namesz = ReadI32(elf, off, le);
                    int descsz = ReadI32(elf, off + 4, le);
                    int type = ReadI32(elf, off + 8, le);
                    int nameStart = off + 12;
                    int namePad = (namesz + 3) & ~3;
                    int descStart = nameStart + namePad;
                    int descPad = (descsz + 3) & ~3;
                    if (descStart + descsz > end || namesz < 0 || descsz < 0)
                    {
                        break;
                    }
                    // GNU + NT_GNU_BUILD_ID (3)
                    if (type == 3 && namesz >= 3 &&
                        elf[nameStart] == (byte)'G' &&
                        elf[nameStart + 1] == (byte)'N' &&
                        elf[nameStart + 2] == (byte)'U')
                    {
                        var sb = new StringBuilder(descsz * 2);
                        for (int b = 0; b < descsz; b++)
                        {
                            sb.Append(elf[descStart + b].ToString("x2"));
                        }
                        return sb.ToString();
                    }
                    off = descStart + descPad;
                }
            }
            return null;
        }

        /// <summary>LC_UUID from thin or fat Mach-O → dashed lowercase UUID strings.</summary>
        internal static IEnumerable<string> TryReadMachOUuids(byte[] data)
        {
            if (data.Length < 8)
            {
                yield break;
            }
            uint magic = ReadU32BE(data, 0);
            // Fat
            if (magic == 0xCAFEBABE || magic == 0xBEBAFECA)
            {
                bool swap = magic == 0xBEBAFECA;
                int nfat = (int)(swap ? ReadU32LE(data, 4) : ReadU32BE(data, 4));
                for (int i = 0; i < nfat && 8 + (i + 1) * 20 <= data.Length; i++)
                {
                    int baseOff = 8 + i * 20;
                    uint offset = swap ? ReadU32LE(data, baseOff + 8) : ReadU32BE(data, baseOff + 8);
                    uint size = swap ? ReadU32LE(data, baseOff + 12) : ReadU32BE(data, baseOff + 12);
                    if (offset + size > data.Length || size < 8)
                    {
                        continue;
                    }
                    var slice = new byte[size];
                    Buffer.BlockCopy(data, (int)offset, slice, 0, (int)size);
                    foreach (var id in TryReadThinMachOUuids(slice))
                    {
                        yield return id;
                    }
                }
                yield break;
            }
            foreach (var id in TryReadThinMachOUuids(data))
            {
                yield return id;
            }
        }

        static IEnumerable<string> TryReadThinMachOUuids(byte[] data)
        {
            if (data.Length < 28)
            {
                yield break;
            }
            uint magic = ReadU32BE(data, 0);
            bool le;
            int headerSize;
            switch (magic)
            {
                case 0xFEEDFACE: // MH_MAGIC
                    le = false; headerSize = 28; break;
                case 0xCEFAEDFE: // MH_CIGAM
                    le = true; headerSize = 28; break;
                case 0xFEEDFACF: // MH_MAGIC_64
                    le = false; headerSize = 32; break;
                case 0xCFFAEDFE: // MH_CIGAM_64
                    le = true; headerSize = 32; break;
                default:
                    yield break;
            }
            int ncmds = (int)(le ? ReadU32LE(data, 16) : ReadU32BE(data, 16));
            int sizeofcmds = (int)(le ? ReadU32LE(data, 20) : ReadU32BE(data, 20));
            int off = headerSize;
            int end = Math.Min(data.Length, headerSize + sizeofcmds);
            for (int i = 0; i < ncmds && off + 8 <= end; i++)
            {
                uint cmd = le ? ReadU32LE(data, off) : ReadU32BE(data, off);
                int cmdsize = (int)(le ? ReadU32LE(data, off + 4) : ReadU32BE(data, off + 4));
                if (cmdsize < 8 || off + cmdsize > end)
                {
                    break;
                }
                if (cmd == 0x1B && cmdsize >= 24) // LC_UUID
                {
                    yield return FormatUuid(data, off + 8);
                }
                off += cmdsize;
            }
        }

        static string FormatUuid(byte[] data, int offset)
        {
            // Mach-O UUID bytes → 8-4-4-4-12 lowercase hex with dashes
            var b = new byte[16];
            Buffer.BlockCopy(data, offset, b, 0, 16);
            return string.Format(
                "{0:x2}{1:x2}{2:x2}{3:x2}-{4:x2}{5:x2}-{6:x2}{7:x2}-{8:x2}{9:x2}-{10:x2}{11:x2}{12:x2}{13:x2}{14:x2}{15:x2}",
                b[0], b[1], b[2], b[3], b[4], b[5], b[6], b[7],
                b[8], b[9], b[10], b[11], b[12], b[13], b[14], b[15]);
        }

        static int ReadI16(byte[] d, int o, bool le) =>
            le ? d[o] | (d[o + 1] << 8) : (d[o] << 8) | d[o + 1];

        static int ReadI32(byte[] d, int o, bool le) =>
            le
                ? d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24)
                : (d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3];

        static long ReadI64(byte[] d, int o, bool le)
        {
            if (le)
            {
                uint lo = (uint)ReadI32(d, o, true);
                uint hi = (uint)ReadI32(d, o + 4, true);
                return (long)((ulong)hi << 32 | lo);
            }
            uint hiBe = (uint)ReadI32(d, o, false);
            uint loBe = (uint)ReadI32(d, o + 4, false);
            return (long)((ulong)hiBe << 32 | loBe);
        }

        static uint ReadU32BE(byte[] d, int o) =>
            ((uint)d[o] << 24) | ((uint)d[o + 1] << 16) | ((uint)d[o + 2] << 8) | d[o + 3];

        static uint ReadU32LE(byte[] d, int o) =>
            d[o] | ((uint)d[o + 1] << 8) | ((uint)d[o + 2] << 16) | ((uint)d[o + 3] << 24);
    }
}
#endif
