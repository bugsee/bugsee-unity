using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Bugsee.WrapperPolicy
{
    /// <summary>What an ELF shared library carries for symbolication, read from its section headers.</summary>
    public sealed class ElfSymbolInfo
    {
        /// <summary>GNU build-id as lowercase hex, or null (not an ELF, no build-id note, damaged).</summary>
        public string BuildId;

        /// <summary>A non-empty <c>.symtab</c>: function names beyond the exported ones (what a stripped library lacks).</summary>
        public bool HasSymtab;

        /// <summary>A non-empty <c>.debug_info</c>: DWARF, which gives file and line.</summary>
        public bool HasDebugInfo;

        /// <summary>Whether the library can symbolicate anything the APK's own copy (dynamic symbols only) cannot.</summary>
        public bool CarriesSymbols => HasSymtab || HasDebugInfo;
    }

    /// <summary>One native library found in the Gradle build's pre-strip folder.</summary>
    public sealed class NativeLibCandidate
    {
        public string Path;
        public string Abi;
        public string Name;
        public string BuildId;
        public long Length;
    }

    /// <summary>
    /// Which native libraries of the built app, other than the ones in Unity's symbols zip, the Editor uploads
    /// symbols for: the libraries that still carry a symbol table or DWARF in the Gradle build's pre-strip folder
    /// (<c>merged_native_libs</c>: the developer's own plugin libraries and AAR libraries that ship unstripped) and
    /// whose ABI and GNU build-id are the ones in the built APK/AAB. A library with dynamic symbols only (every
    /// Bugsee library, as the AAR ships it) is not sent: the symbols server keeps the first copy of a build-id at a
    /// given level and the CLI labels such a copy <c>symtab</c>, so it could stand in the way of better symbols for
    /// that build-id (see Documentation~/index.md, "Native libraries beyond Unity's symbols zip"). The consequence is
    /// that frames inside such a library stay unsymbolicated.
    /// </summary>
    public static class AndroidNativeLibs
    {
        /// <summary>Largest single library staged for upload.</summary>
        public const long MaxFileBytes = 256L * 1024 * 1024;

        /// <summary>Largest total staged for one build; libraries over it are left out (smallest first are kept).</summary>
        public const long MaxTotalBytes = 512L * 1024 * 1024;

        const int MaxSectionNamesBytes = 1024 * 1024;
        const int MaxSections = 4096;
        const int MaxSymtabBytes = 64 * 1024 * 1024;

        /// <summary>How much of an archive entry is read to find the build-id (the note is in the first segment).</summary>
        public const int PrefixBytes = 64 * 1024;

        /// <summary>
        /// Reads the build-id and what the library carries. Never throws for damaged input: a stream that is not a
        /// parseable ELF yields an object with no build-id and nothing carried.
        /// </summary>
        public static ElfSymbolInfo Inspect(Stream stream)
        {
            var info = new ElfSymbolInfo();
            try
            {
                Read(stream, info);
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is OverflowException || ex is NotSupportedException)
            {
                info.BuildId = null;
                info.HasSymtab = false;
                info.HasDebugInfo = false;
            }

            return info;
        }

        static void Read(Stream s, ElfSymbolInfo info)
        {
            var ident = new byte[64];
            if (s.Seek(0, SeekOrigin.Begin) != 0 || Fill(s, ident, 0, 52) < 52)
            {
                return;
            }

            if (ident[0] != 0x7f || ident[1] != (byte)'E' || ident[2] != (byte)'L' || ident[3] != (byte)'F')
            {
                return;
            }

            bool is64 = ident[4] == 2;
            if (ident[4] != 1 && !is64)
            {
                return;
            }

            bool le = ident[5] == 1;
            if (!le && ident[5] != 2)
            {
                return;
            }

            if (is64 && Fill(s, ident, 52, 12) < 12)
            {
                return;
            }

            long shoff = is64 ? I64(ident, 40, le) : U32(ident, 32, le);
            int shentsize = U16(ident, is64 ? 58 : 46, le);
            int shnum = U16(ident, is64 ? 60 : 48, le);
            int shstrndx = U16(ident, is64 ? 62 : 50, le);
            int minEntry = is64 ? 64 : 40;
            // The entry size of the class (a larger one only pads); the table must lie inside the file before it is read.
            if (shoff <= 0 || shentsize < minEntry || shentsize > 256 || shnum <= 0 || shnum > MaxSections || shstrndx >= shnum)
            {
                return;
            }

            long length = s.Length;
            long tableBytes = (long)shnum * shentsize;
            if (shoff + tableBytes > length)
            {
                return;
            }

            var table = new byte[tableBytes];
            s.Seek(shoff, SeekOrigin.Begin);
            if (Fill(s, table, 0, table.Length) < table.Length)
            {
                return;
            }

            long Offset(int i) => is64 ? I64(table, i * shentsize + 24, le) : U32(table, i * shentsize + 16, le);
            long Size(int i) => is64 ? I64(table, i * shentsize + 32, le) : U32(table, i * shentsize + 20, le);
            int Type(int i) => (int)U32(table, i * shentsize + 4, le);
            int NameIndex(int i) => (int)U32(table, i * shentsize, le);
            bool Inside(int i) => Offset(i) >= 0 && Size(i) >= 0 && Offset(i) + Size(i) <= length;
            int symEntry = is64 ? 24 : 16;

            if (!Inside(shstrndx))
            {
                return;
            }

            long namesSize = Size(shstrndx);
            if (namesSize <= 0 || namesSize > MaxSectionNamesBytes)
            {
                return;
            }

            var names = new byte[namesSize];
            s.Seek(Offset(shstrndx), SeekOrigin.Begin);
            if (Fill(s, names, 0, names.Length) < names.Length)
            {
                return;
            }

            for (int i = 0; i < shnum; i++)
            {
                string name = CString(names, NameIndex(i));
                int type = Type(i);
                long size = Size(i);
                if (!Inside(i))
                {
                    continue;
                }

                if (name == ".symtab" && type == 2 && size >= symEntry)
                {
                    // Only a table that names something counts: a stripped library can keep null, file and section symbols.
                    info.HasSymtab = HasNamedSymbol(s, Offset(i), size, is64, le);
                }
                else if ((name == ".debug_info" || name == ".zdebug_info") && type != 8 && size > 0)
                {
                    // SHT_NOBITS (8): `objcopy --only-keep-debug` style placeholder, nothing to read.
                    info.HasDebugInfo = true;
                }
                else if (name == ".note.gnu.build-id" && type == 7 && size >= 16 && size <= 4096 && info.BuildId == null)
                {
                    var note = new byte[size];
                    s.Seek(Offset(i), SeekOrigin.Begin);
                    if (Fill(s, note, 0, note.Length) == note.Length)
                    {
                        info.BuildId = ParseBuildIdNote(note, le);
                    }
                }
            }
        }

        // A function, object, TLS or indirect-function symbol in a .symtab; a table too large to read counts as having one.
        static bool HasNamedSymbol(Stream s, long offset, long size, bool is64, bool le)
        {
            if (size > MaxSymtabBytes)
            {
                return true;
            }

            int entry = is64 ? 24 : 16;
            int infoAt = is64 ? 4 : 12;
            var data = new byte[size];
            s.Seek(offset, SeekOrigin.Begin);
            int read = Fill(s, data, 0, data.Length);
            for (long at = 0; at + entry <= read; at += entry)
            {
                int type = data[at + infoAt] & 0xf;
                if (type == 1 || type == 2 || type == 6 || type == 10)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The id in a <c>.note.gnu.build-id</c> section (namesz, descsz, type 3, "GNU", id), or null.</summary>
        internal static string ParseBuildIdNote(byte[] note, bool littleEndian)
        {
            if (note.Length < 16)
            {
                return null;
            }

            long namesz = U32(note, 0, littleEndian);
            long descsz = U32(note, 4, littleEndian);
            long type = U32(note, 8, littleEndian);
            long descStart = 12 + ((namesz + 3) & ~3L);
            if (type != 3 || namesz != 4 || descsz <= 0 || descsz > 64 || descStart + descsz > note.Length
                || note[12] != (byte)'G' || note[13] != (byte)'N' || note[14] != (byte)'U')
            {
                return null;
            }

            var sb = new StringBuilder((int)descsz * 2);
            for (long i = 0; i < descsz; i++)
            {
                sb.Append(note[descStart + i].ToString("x2"));
            }

            return sb.ToString();
        }

        static int Fill(Stream s, byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = s.Read(buffer, offset + total, count - total);
                if (n <= 0)
                {
                    break;
                }

                total += n;
            }

            return total;
        }

        static string CString(byte[] data, int start)
        {
            if (start < 0 || start >= data.Length)
            {
                return "";
            }

            int end = start;
            while (end < data.Length && data[end] != 0)
            {
                end++;
            }

            return Encoding.ASCII.GetString(data, start, end - start);
        }

        static long U32(byte[] b, int o, bool le)
        {
            if (o < 0 || o + 4 > b.Length)
            {
                throw new ArgumentException("offset outside the buffer");
            }

            return le
                ? (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24))
                : (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);
        }

        static int U16(byte[] b, int o, bool le) =>
            le ? b[o] | (b[o + 1] << 8) : (b[o] << 8) | b[o + 1];

        static long I64(byte[] b, int o, bool le)
        {
            long lo = le ? U32(b, o, true) : U32(b, o + 4, false);
            long hi = le ? U32(b, o + 4, true) : U32(b, o, false);
            // A value past 2^62 is not a file offset or size of anything this reads; keep it negative so callers reject it.
            return hi > 0x3fffffff ? -1 : (hi << 32) | lo;
        }

        /// <summary>The ABI folder names Unity's target architectures flags select (ARMv7 1, ARM64 2, X86 4, X86_64 8).</summary>
        public static ISet<string> AbiFolders(int architectures)
        {
            var abis = new HashSet<string>(StringComparer.Ordinal);
            if ((architectures & 1) != 0) abis.Add("armeabi-v7a");
            if ((architectures & 2) != 0) abis.Add("arm64-v8a");
            if ((architectures & 4) != 0) abis.Add("x86");
            if ((architectures & 8) != 0) abis.Add("x86_64");
            return abis;
        }

        /// <summary>
        /// The ABI folder and file name of a library path whose last three segments are <c>lib/&lt;abi&gt;/&lt;name&gt;.so</c>
        /// (an APK's <c>lib/arm64-v8a/x.so</c>, an AAB's <c>base/lib/...</c>, the Gradle folder's <c>.../out/lib/...</c>).
        /// </summary>
        public static bool TryParseLibPath(string path, out string abi, out string name)
        {
            abi = null;
            name = null;
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            var parts = path.Replace('\\', '/').Split('/');
            if (parts.Length < 3 || parts[parts.Length - 3] != "lib")
            {
                return false;
            }

            var file = parts[parts.Length - 1];
            var folder = parts[parts.Length - 2];
            if (folder.Length == 0 || !file.EndsWith(".so", StringComparison.Ordinal) || file.Length <= 3)
            {
                return false;
            }

            abi = folder;
            name = file;
            return true;
        }

        /// <summary>The key of a packaged library: ABI, file name and build-id.</summary>
        public static string PackagedKey(string abi, string name, string buildId) => abi + "/" + name + "|" + buildId;

        /// <summary>
        /// The ABI/name pairs a symbols zip already holds (entries like <c>arm64-v8a/libunity.so</c>): those are
        /// never staged again. Entries that are not libraries are ignored.
        /// </summary>
        public static ISet<string> ZipLibraryKeys(IEnumerable<string> entryNames)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entryNames)
            {
                if (string.IsNullOrEmpty(entry))
                {
                    continue;
                }

                var parts = entry.Replace('\\', '/').Split('/');
                if (parts.Length < 2)
                {
                    continue;
                }

                // AGP's native-debug-symbols.zip names them libfoo.so.sym / libfoo.so.dbg.
                var file = parts[parts.Length - 1];
                foreach (var suffix in new[] { ".sym", ".dbg" })
                {
                    if (file.EndsWith(".so" + suffix, StringComparison.Ordinal))
                    {
                        file = file.Substring(0, file.Length - suffix.Length);
                        break;
                    }
                }

                // Unity's own spellings: libfoo.sym.so, libfoo.dbg.so.
                foreach (var infix in new[] { ".sym.so", ".dbg.so" })
                {
                    if (file.EndsWith(infix, StringComparison.Ordinal))
                    {
                        file = file.Substring(0, file.Length - infix.Length) + ".so";
                        break;
                    }
                }

                if (file.EndsWith(".so", StringComparison.Ordinal))
                {
                    keys.Add(parts[parts.Length - 2] + "/" + file);
                }
            }

            return keys;
        }

        /// <summary>
        /// The libraries to send: of the carrying ones, those in the built app (same ABI, name and build-id), in a
        /// selected ABI, not in Unity's zip, once per (ABI, name, build-id), within the size limits. Each skipped
        /// library that deserves a mention is described in <paramref name="notes"/>.
        /// </summary>
        public static List<NativeLibCandidate> Choose(
            IEnumerable<NativeLibCandidate> carrying,
            ISet<string> packaged,
            ISet<string> inUnityZip,
            ISet<string> abis,
            List<string> notes)
        {
            var chosen = new List<NativeLibCandidate>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var eligible = new List<NativeLibCandidate>();
            foreach (var lib in carrying)
            {
                if (lib == null || string.IsNullOrEmpty(lib.BuildId) || string.IsNullOrEmpty(lib.Abi) || string.IsNullOrEmpty(lib.Name))
                {
                    continue;
                }

                if (abis != null && abis.Count > 0 && !abis.Contains(lib.Abi))
                {
                    continue;
                }

                if (inUnityZip != null && inUnityZip.Contains(lib.Abi + "/" + lib.Name))
                {
                    continue;
                }

                if (lib.Length > MaxFileBytes)
                {
                    notes?.Add(lib.Abi + "/" + lib.Name + " (" + lib.Length + " bytes) is over the upload size limit and is left out");
                    continue;
                }

                var key = PackagedKey(lib.Abi, lib.Name, lib.BuildId);
                if (packaged == null || !packaged.Contains(key))
                {
                    // Left by an earlier build, or a library the build did not package: its build-id is not the app's.
                    notes?.Add(lib.Abi + "/" + lib.Name + " (build-id " + lib.BuildId + ") is not the copy packaged in the app");
                    continue;
                }

                if (seen.Add(key))
                {
                    eligible.Add(lib);
                }
            }

            eligible.Sort((a, b) => a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a.Path, b.Path));
            long total = 0;
            foreach (var lib in eligible)
            {
                if (total + lib.Length > MaxTotalBytes)
                {
                    notes?.Add(lib.Abi + "/" + lib.Name + " (" + lib.Length + " bytes) is over the upload size limit and is left out");
                    continue;
                }

                total += lib.Length;
                chosen.Add(lib);
            }

            return chosen;
        }

        /// <summary>Where a chosen library is staged below the upload folder: the build-id keeps two builds of one name apart.</summary>
        public static string StagingRelativePath(NativeLibCandidate lib) =>
            lib.Abi + "/" + lib.BuildId + "/" + lib.Name;

        /// <summary>The CLI time allowed for <paramref name="bytes"/> staged: the base, plus 2 s per MiB, at most 30 minutes.</summary>
        public static int UploadTimeoutMs(long bytes, int baseMs)
        {
            long ms = baseMs + (Math.Max(0, bytes) / (1024 * 1024)) * 2000;
            return (int)Math.Min(ms, 30L * 60 * 1000);
        }

        /// <summary>
        /// The build id of a library from the start of its file (program headers and the note they point at, so a
        /// 64 KiB prefix of a compressed archive entry is enough), or null.
        /// </summary>
        public static string BuildIdFromPrefix(byte[] data, int length)
        {
            if (data == null || length > data.Length)
            {
                length = data?.Length ?? 0;
            }

            if (length < 52 || data[0] != 0x7f || data[1] != (byte)'E' || data[2] != (byte)'L' || data[3] != (byte)'F')
            {
                return null;
            }

            bool is64 = data[4] == 2;
            if (!is64 && data[4] != 1)
            {
                return null;
            }

            bool le = data[5] == 1;
            if (!le && data[5] != 2)
            {
                return null;
            }

            if (is64 && length < 64)
            {
                return null;
            }

            long phoff = is64 ? I64(data, 32, le) : U32(data, 28, le);
            int phentsize = U16(data, is64 ? 54 : 42, le);
            int phnum = U16(data, is64 ? 56 : 44, le);
            if (phoff <= 0 || phentsize < (is64 ? 56 : 32) || phentsize > 256 || phnum <= 0)
            {
                return null;
            }

            for (int i = 0; i < phnum; i++)
            {
                long ph = phoff + (long)i * phentsize;
                if (ph + phentsize > length)
                {
                    break;
                }

                if (U32(data, (int)ph, le) != 4) // PT_NOTE
                {
                    continue;
                }

                long offset = is64 ? I64(data, (int)ph + 8, le) : U32(data, (int)ph + 4, le);
                long size = is64 ? I64(data, (int)ph + 32, le) : U32(data, (int)ph + 16, le);
                if (offset < 0 || size < 16 || size > 4096 || offset + size > length)
                {
                    continue;
                }

                // A segment can hold several notes; the build-id is the one named GNU with type 3.
                long at = offset, end = offset + size;
                while (at + 12 <= end)
                {
                    long namesz = U32(data, (int)at, le);
                    long descsz = U32(data, (int)at + 4, le);
                    long type = U32(data, (int)at + 8, le);
                    long descStart = at + 12 + ((namesz + 3) & ~3L);
                    long next = descStart + ((descsz + 3) & ~3L);
                    if (descStart + descsz > end || next <= at)
                    {
                        break;
                    }

                    if (type == 3 && namesz == 4 && descsz > 0 && descsz <= 64
                        && data[at + 12] == (byte)'G' && data[at + 13] == (byte)'N' && data[at + 14] == (byte)'U')
                    {
                        var sb = new StringBuilder((int)descsz * 2);
                        for (long b = 0; b < descsz; b++)
                        {
                            sb.Append(data[descStart + b].ToString("x2"));
                        }

                        return sb.ToString();
                    }

                    at = next;
                }
            }

            return null;
        }

        /// <summary>
        /// The archives this build wrote: the output file, or the APKs of Split APKs by target architecture (a folder of
        /// them, or "Game.arm64-v8a.apk" files beside the player path "Game.apk" that does not exist), those written
        /// since <paramref name="buildStartedUtc"/>. Empty for an Export Project and when nothing is found.
        /// </summary>
        public static List<string> BuiltArchivePaths(string outputPath, DateTime buildStartedUtc)
        {
            var archives = new List<string>();
            if (string.IsNullOrEmpty(outputPath))
            {
                return archives;
            }

            if (File.Exists(outputPath))
            {
                archives.Add(outputPath);
                return archives;
            }

            if (Directory.Exists(outputPath))
            {
                // Split APKs by target architecture: Unity turns the output path into a folder holding one APK per ABI
                // (an exported project holds none).
                foreach (var apk in Directory.GetFiles(outputPath, "*.apk"))
                {
                    if (File.GetLastWriteTimeUtc(apk) >= buildStartedUtc)
                    {
                        archives.Add(apk);
                    }
                }

                return archives;
            }

            var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            var stem = Path.GetFileNameWithoutExtension(outputPath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir) && !string.IsNullOrEmpty(stem)
                && outputPath.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var apk in Directory.GetFiles(dir, "*.apk"))
                {
                    // "Game.arm64-v8a.apk" yes; "GameLite.apk" (another player) and APKs of earlier builds no.
                    if (AndroidUploadIdentity.IsSplitApkOf(Path.GetFileName(apk), stem) && File.GetLastWriteTimeUtc(apk) >= buildStartedUtc)
                    {
                        archives.Add(apk);
                    }
                }
            }

            return archives;
        }

        /// <summary>
        /// Reads each <c>lib/&lt;abi&gt;/&lt;name&gt;.so</c> file path (the Gradle merge folder) and returns those that carry
        /// symbols; <paramref name="withoutSymbols"/> counts the readable libraries that do not. A file that cannot be
        /// read is skipped with a line in <paramref name="notes"/>.
        /// </summary>
        public static List<NativeLibCandidate> ScanLibraries(IEnumerable<string> files, out int withoutSymbols, List<string> notes)
        {
            var carrying = new List<NativeLibCandidate>();
            withoutSymbols = 0;
            foreach (var path in files)
            {
                if (!TryParseLibPath(path, out var abi, out var name))
                {
                    continue;
                }

                try
                {
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        var info = Inspect(stream);
                        if (info.BuildId == null)
                        {
                            continue;
                        }

                        if (!info.CarriesSymbols)
                        {
                            withoutSymbols++;
                            continue;
                        }

                        carrying.Add(new NativeLibCandidate { Path = path, Abi = abi, Name = name, BuildId = info.BuildId, Length = stream.Length });
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    notes?.Add(path + " could not be read (" + ex.Message + ")");
                }
            }

            return carrying;
        }

        /// <summary>
        /// The (ABI, name, build-id) of every library of <paramref name="wanted"/> (<c>abi/name</c>) that an APK/AAB
        /// holds. Only the wanted entries are opened, and only the first <see cref="PrefixBytes"/> of each is read.
        /// </summary>
        public static void ReadPackagedBuildIds(Stream archive, ISet<string> wanted, ISet<string> into)
        {
            using (var zip = new ZipArchive(archive, ZipArchiveMode.Read, true))
            {
                foreach (var entry in zip.Entries)
                {
                    if (!TryParseLibPath(entry.FullName, out var abi, out var name) || !wanted.Contains(abi + "/" + name))
                    {
                        continue;
                    }

                    var buffer = new byte[PrefixBytes];
                    int read;
                    using (var raw = entry.Open())
                    {
                        read = Fill(raw, buffer, 0, buffer.Length);
                    }

                    var id = BuildIdFromPrefix(buffer, read);
                    if (id != null)
                    {
                        into.Add(PackagedKey(abi, name, id));
                    }
                }
            }
        }

        /// <summary><see cref="ZipLibraryKeys"/> of a zip stream.</summary>
        public static ISet<string> ZipLibraryKeys(Stream zip)
        {
            using (var archive = new ZipArchive(zip, ZipArchiveMode.Read, true))
            {
                var names = new List<string>();
                foreach (var entry in archive.Entries)
                {
                    names.Add(entry.FullName);
                }

                return ZipLibraryKeys(names);
            }
        }

        /// <summary>Copies the chosen libraries below <paramref name="root"/> at <see cref="StagingRelativePath"/>.</summary>
        public static void Stage(IEnumerable<NativeLibCandidate> chosen, string root)
        {
            foreach (var lib in chosen)
            {
                var target = Path.Combine(root, StagingRelativePath(lib).Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(lib.Path, target, true);
            }
        }
    }
}
