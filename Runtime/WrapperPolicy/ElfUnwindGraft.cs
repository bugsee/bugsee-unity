using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Puts the unwind tables of an app's runtime library back into Unity's symbols copy of it.
    ///
    /// Unity's <c>symbols/&lt;abi&gt;/libunity.so</c> (the file in <c>symbols.zip</c>) has the symbol table and the build-id of the library in
    /// the APK, but its code and unwind sections are stripped to <c>SHT_NOBITS</c> placeholders (declared, no bytes). Bugsee's symbol server
    /// builds a Breakpad file from what it is given and the Android minidump walker unwinds with that file's CFI. With no <c>.eh_frame</c>
    /// there is no CFI, so a crash in a library such as <c>libunity.so</c> unwinds a frame or two and stops, short of the C# frames
    /// (Android 11 and older, which report through a minidump). The APK's copy has the bytes. This copies the symbols file and
    /// gives its <c>.eh_frame</c> and <c>.eh_frame_hdr</c> the contents of the runtime copy: one ordinary file with names and CFI, same build-id.
    /// Nothing else changes (the program headers are untouched), and nothing is sent that the server does not already get from the APK:
    /// the cost is the size of the two sections (about 12% of the runtime library).
    /// </summary>
    public static class ElfUnwindGraft
    {
        /// <summary>The sections that carry the call frame information.</summary>
        static readonly string[] UnwindSections = { ".eh_frame", ".eh_frame_hdr" };

        const int ShtProgbits = 1;
        const int ShtNobits = 8;
        const int MaxSections = 4096;
        const int MaxNamesBytes = 1024 * 1024;
        // A sanity bound on one unwind section; the largest seen is a few MB.
        const long MaxSectionBytes = 256L * 1024 * 1024;

        sealed class Section
        {
            public string Name;
            public int Type;
            public long Addr;
            public long Offset;
            public long Size;
            public long Align;
            // Where this section's header sits in the file, and the offsets of the fields to patch.
            public long HeaderPos;
            public int TypeField;
            public int OffsetField;
        }

        sealed class Elf
        {
            public bool Is64;
            public List<Section> Sections = new List<Section>();
            public long Length;
        }

        /// <summary>
        /// Writes <paramref name="output"/> as <paramref name="symbols"/> with the unwind sections it lacks taken from
        /// <paramref name="runtime"/>. Returns false, leaving <paramref name="output"/> unspecified, when there is nothing to do or the two
        /// files do not belong together; <paramref name="note"/> says why. Never throws for damaged input. All streams must be seekable.
        /// </summary>
        public static bool TryGraft(Stream symbols, Stream runtime, Stream output, out string note)
        {
            try
            {
                return Graft(symbols, runtime, output, out note);
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is OverflowException || ex is NotSupportedException)
            {
                note = "could not read the library (" + ex.Message + ")";
                return false;
            }
        }

        /// <summary>True when <paramref name="symbols"/> declares an unwind section without bytes that <see cref="TryGraft"/> could fill.</summary>
        public static bool NeedsUnwindData(Stream symbols)
        {
            try
            {
                var elf = Parse(symbols);
                if (elf == null)
                {
                    return false;
                }

                foreach (var name in UnwindSections)
                {
                    var section = Find(elf, name);
                    if (section != null && section.Type == ShtNobits && section.Size > 0)
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is OverflowException || ex is NotSupportedException)
            {
                // not a library we can read: nothing to add
            }

            return false;
        }

        static bool Graft(Stream symbols, Stream runtime, Stream output, out string note)
        {
            var symbolsInfo = AndroidNativeLibs.Inspect(symbols);
            var runtimeInfo = AndroidNativeLibs.Inspect(runtime);
            if (symbolsInfo.BuildId == null || runtimeInfo.BuildId == null)
            {
                note = "no build-id";
                return false;
            }

            if (!string.Equals(symbolsInfo.BuildId, runtimeInfo.BuildId, StringComparison.OrdinalIgnoreCase))
            {
                note = "the build-id differs from the app's copy";
                return false;
            }

            var target = Parse(symbols);
            var source = Parse(runtime);
            if (target == null || source == null || target.Is64 != source.Is64)
            {
                note = "not a little-endian ELF of the same class";
                return false;
            }

            var work = new List<Section[]>();
            foreach (var name in UnwindSections)
            {
                var want = Find(target, name);
                if (want == null || want.Type != ShtNobits || want.Size <= 0)
                {
                    continue;
                }

                var have = Find(source, name);
                // The same build-id means the same link, so the section must sit at the same address with the same size; anything else is not this library.
                if (have == null || have.Type != ShtProgbits || have.Addr != want.Addr || have.Size != want.Size
                    || have.Size > MaxSectionBytes || have.Offset < 0 || have.Offset + have.Size > source.Length)
                {
                    note = name + " of the app's copy does not match";
                    return false;
                }

                work.Add(new[] { want, have });
            }

            if (work.Count == 0)
            {
                note = "already has its unwind data";
                return false;
            }

            symbols.Seek(0, SeekOrigin.Begin);
            CopyAll(symbols, output);

            foreach (var pair in work)
            {
                var want = pair[0];
                var have = pair[1];
                long align = Math.Max(1, Math.Max(want.Align, have.Align));
                long position = output.Length;
                long padded = (position + align - 1) / align * align;
                output.Seek(position, SeekOrigin.Begin);
                for (long i = position; i < padded; i++)
                {
                    output.WriteByte(0);
                }

                runtime.Seek(have.Offset, SeekOrigin.Begin);
                CopyExactly(runtime, output, have.Size);

                // Patch the section header: it is now PROGBITS and its bytes are at the end.
                var patch = new byte[target.Is64 ? 8 : 4];
                PutUInt(patch, 0, ShtProgbits, 4);
                output.Seek(want.HeaderPos + want.TypeField, SeekOrigin.Begin);
                output.Write(patch, 0, 4);
                PutUInt(patch, 0, padded, target.Is64 ? 8 : 4);
                output.Seek(want.HeaderPos + want.OffsetField, SeekOrigin.Begin);
                output.Write(patch, 0, target.Is64 ? 8 : 4);
            }

            output.Flush();
            var names = new List<string>();
            foreach (var pair in work)
            {
                names.Add(pair[0].Name);
            }

            note = "added " + string.Join(", ", names);
            return true;
        }

        static Section Find(Elf elf, string name)
        {
            foreach (var section in elf.Sections)
            {
                if (section.Name == name)
                {
                    return section;
                }
            }

            return null;
        }

        static Elf Parse(Stream s)
        {
            var ident = new byte[64];
            s.Seek(0, SeekOrigin.Begin);
            if (Fill(s, ident, 0, 52) < 52 || ident[0] != 0x7f || ident[1] != (byte)'E' || ident[2] != (byte)'L' || ident[3] != (byte)'F')
            {
                return null;
            }

            bool is64 = ident[4] == 2;
            if ((ident[4] != 1 && !is64) || ident[5] != 1)
            {
                return null; // only little-endian (every Android ABI)
            }

            if (is64 && Fill(s, ident, 52, 12) < 12)
            {
                return null;
            }

            long shoff = is64 ? (long)GetUInt(ident, 40, 8) : (long)GetUInt(ident, 32, 4);
            int shentsize = (int)GetUInt(ident, is64 ? 58 : 46, 2);
            int shnum = (int)GetUInt(ident, is64 ? 60 : 48, 2);
            int shstrndx = (int)GetUInt(ident, is64 ? 62 : 50, 2);
            int minEntry = is64 ? 64 : 40;
            long length = s.Length;
            if (shoff <= 0 || shentsize < minEntry || shentsize > 256 || shnum <= 0 || shnum > MaxSections || shstrndx >= shnum
                || shoff + (long)shnum * shentsize > length)
            {
                return null;
            }

            var table = new byte[(long)shnum * shentsize];
            s.Seek(shoff, SeekOrigin.Begin);
            if (Fill(s, table, 0, table.Length) < table.Length)
            {
                return null;
            }

            int typeField = 4;
            int addrField = is64 ? 16 : 12;
            int offsetField = is64 ? 24 : 16;
            int sizeField = is64 ? 32 : 20;
            int alignField = is64 ? 48 : 32;
            int width = is64 ? 8 : 4;

            long strOffset = (long)GetUInt(table, shstrndx * shentsize + offsetField, width);
            long strSize = (long)GetUInt(table, shstrndx * shentsize + sizeField, width);
            if (strSize <= 0 || strSize > MaxNamesBytes || strOffset < 0 || strOffset + strSize > length)
            {
                return null;
            }

            var names = new byte[strSize];
            s.Seek(strOffset, SeekOrigin.Begin);
            if (Fill(s, names, 0, names.Length) < names.Length)
            {
                return null;
            }

            var elf = new Elf { Is64 = is64, Length = length };
            for (int i = 0; i < shnum; i++)
            {
                int at = i * shentsize;
                int nameIndex = (int)GetUInt(table, at, 4);
                if (nameIndex < 0 || nameIndex >= names.Length)
                {
                    continue;
                }

                int end = nameIndex;
                while (end < names.Length && names[end] != 0)
                {
                    end++;
                }

                elf.Sections.Add(new Section
                {
                    Name = Encoding.ASCII.GetString(names, nameIndex, end - nameIndex),
                    Type = (int)GetUInt(table, at + typeField, 4),
                    Addr = (long)GetUInt(table, at + addrField, width),
                    Offset = (long)GetUInt(table, at + offsetField, width),
                    Size = (long)GetUInt(table, at + sizeField, width),
                    Align = (long)GetUInt(table, at + alignField, width),
                    HeaderPos = shoff + at,
                    TypeField = typeField,
                    OffsetField = offsetField,
                });
            }

            return elf;
        }

        static ulong GetUInt(byte[] b, int offset, int width)
        {
            ulong value = 0;
            for (int i = width - 1; i >= 0; i--)
            {
                value = (value << 8) | b[offset + i];
            }

            return value;
        }

        static void PutUInt(byte[] b, int offset, long value, int width)
        {
            for (int i = 0; i < width; i++)
            {
                b[offset + i] = (byte)(value >> (8 * i));
            }
        }

        static int Fill(Stream s, byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int read = s.Read(buffer, offset + total, count - total);
                if (read <= 0)
                {
                    break;
                }

                total += read;
            }

            return total;
        }

        static void CopyAll(Stream from, Stream to)
        {
            var buffer = new byte[81920];
            int read;
            while ((read = from.Read(buffer, 0, buffer.Length)) > 0)
            {
                to.Write(buffer, 0, read);
            }
        }

        static void CopyExactly(Stream from, Stream to, long count)
        {
            var buffer = new byte[81920];
            while (count > 0)
            {
                int read = from.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
                if (read <= 0)
                {
                    throw new IOException("unexpected end of the library");
                }

                to.Write(buffer, 0, read);
                count -= read;
            }
        }
    }
}
