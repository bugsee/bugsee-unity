using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class ElfUnwindGraftTests
    {
        const string Id = "820fe9ac1b715093c373e9b3e79df01c00000000";

        const int Progbits = 1;
        const int Nobits = 8;

        sealed class Sec
        {
            public string Name;
            public int Type;
            public long Addr;
            public byte[] Data;      // the bytes (PROGBITS) or the declared size only (NOBITS)
            public int DeclaredSize; // NOBITS
        }

        static Sec P(string name, long addr, byte[] data, int type = Progbits) => new Sec { Name = name, Type = type, Addr = addr, Data = data, DeclaredSize = data.Length };

        static byte[] Bytes(int n, byte seed) => Enumerable.Range(0, n).Select(i => (byte)(seed + i)).ToArray();

        static byte[] BuildIdNote(string id)
        {
            var desc = Enumerable.Range(0, id.Length / 2).Select(i => Convert.ToByte(id.Substring(i * 2, 2), 16)).ToArray();
            var note = new List<byte>();
            note.AddRange(BitConverter.GetBytes(4));
            note.AddRange(BitConverter.GetBytes(desc.Length));
            note.AddRange(BitConverter.GetBytes(3));
            note.AddRange(Encoding.ASCII.GetBytes("GNU\0"));
            note.AddRange(desc);
            return note.ToArray();
        }

        // An ELF (little endian) with a build-id note and the given sections. A NOBITS section has a header and no bytes.
        static byte[] Elf(bool is64, string buildId, params Sec[] extra)
        {
            var sections = new List<Sec> { P(".note.gnu.build-id", 0x200, BuildIdNote(buildId), 7) };
            sections.AddRange(extra);

            var names = new MemoryStream();
            names.WriteByte(0);
            var nameOffsets = new List<int>();
            foreach (var s in sections.Select(x => x.Name).Concat(new[] { ".shstrtab" }))
            {
                nameOffsets.Add((int)names.Length);
                var b = Encoding.ASCII.GetBytes(s + "\0");
                names.Write(b, 0, b.Length);
            }

            int ehsize = is64 ? 64 : 52, shentsize = is64 ? 64 : 40;
            var body = new MemoryStream();
            var offsets = new List<long>();
            foreach (var s in sections)
            {
                offsets.Add(ehsize + body.Length);
                if (s.Type != Nobits)
                {
                    body.Write(s.Data, 0, s.Data.Length);
                }
            }

            long namesOffset = ehsize + body.Length;
            var nb = names.ToArray();
            body.Write(nb, 0, nb.Length);
            while ((ehsize + body.Length) % 8 != 0)
            {
                body.WriteByte(0);
            }

            long shoff = ehsize + body.Length;
            int shnum = sections.Count + 2;

            var h = new byte[ehsize];
            h[0] = 0x7f; h[1] = (byte)'E'; h[2] = (byte)'L'; h[3] = (byte)'F';
            h[4] = (byte)(is64 ? 2 : 1); h[5] = 1; h[6] = 1;
            Put(h, is64 ? 40 : 32, shoff, is64 ? 8 : 4);
            Put(h, is64 ? 58 : 46, shentsize, 2);
            Put(h, is64 ? 60 : 48, shnum, 2);
            Put(h, is64 ? 62 : 50, shnum - 1, 2);

            var table = new byte[shnum * shentsize];
            for (int i = 0; i < sections.Count; i++)
            {
                Header(table, i + 1, nameOffsets[i], sections[i].Type, sections[i].Addr, offsets[i], sections[i].Type == Nobits ? sections[i].DeclaredSize : sections[i].Data.Length, is64);
            }

            Header(table, shnum - 1, nameOffsets[nameOffsets.Count - 1], 3, 0, namesOffset, nb.Length, is64);

            var file = new MemoryStream();
            file.Write(h, 0, h.Length);
            body.WriteTo(file);
            file.Write(table, 0, table.Length);
            return file.ToArray();
        }

        static void Header(byte[] t, int index, int nameOff, int type, long addr, long offset, long size, bool is64)
        {
            int shentsize = is64 ? 64 : 40;
            int o = index * shentsize;
            Put(t, o, nameOff, 4);
            Put(t, o + 4, type, 4);
            Put(t, o + (is64 ? 16 : 12), addr, is64 ? 8 : 4);
            Put(t, o + (is64 ? 24 : 16), offset, is64 ? 8 : 4);
            Put(t, o + (is64 ? 32 : 20), size, is64 ? 8 : 4);
            Put(t, o + (is64 ? 48 : 32), 8, is64 ? 8 : 4); // sh_addralign
        }

        static void Put(byte[] b, int offset, long value, int width)
        {
            for (int i = 0; i < width; i++) b[offset + i] = (byte)(value >> (8 * i));
        }

        static byte[] Graft(byte[] symbols, byte[] runtime, out bool ok, out string note)
        {
            using (var output = new MemoryStream())
            {
                ok = ElfUnwindGraft.TryGraft(new MemoryStream(symbols), new MemoryStream(runtime), output, out note);
                return output.ToArray();
            }
        }

        // The type and the bytes of a section of an ELF, by name, read independently of the code under test.
        static (int type, byte[] data) Read(byte[] elf, string name)
        {
            bool is64 = elf[4] == 2;
            long shoff = is64 ? BitConverter.ToInt64(elf, 40) : BitConverter.ToInt32(elf, 32);
            int shentsize = BitConverter.ToUInt16(elf, is64 ? 58 : 46);
            int shnum = BitConverter.ToUInt16(elf, is64 ? 60 : 48);
            int shstrndx = BitConverter.ToUInt16(elf, is64 ? 62 : 50);
            long Off(int i) => is64 ? BitConverter.ToInt64(elf, (int)shoff + i * shentsize + 24) : BitConverter.ToInt32(elf, (int)shoff + i * shentsize + 16);
            long Size(int i) => is64 ? BitConverter.ToInt64(elf, (int)shoff + i * shentsize + 32) : BitConverter.ToInt32(elf, (int)shoff + i * shentsize + 20);
            long names = Off(shstrndx);
            for (int i = 0; i < shnum; i++)
            {
                int n = (int)(names + BitConverter.ToUInt32(elf, (int)shoff + i * shentsize));
                int end = n;
                while (elf[end] != 0) end++;
                if (Encoding.ASCII.GetString(elf, n, end - n) == name)
                {
                    int type = (int)BitConverter.ToUInt32(elf, (int)shoff + i * shentsize + 4);
                    return (type, type == Nobits ? new byte[0] : elf.Skip((int)Off(i)).Take((int)Size(i)).ToArray());
                }
            }

            return (-1, null);
        }

        static Sec[] Stripped() => new[]
        {
            P(".eh_frame_hdr", 0x1762cc, new byte[40], Nobits),
            P(".eh_frame", 0x1cdd78, new byte[1000], Nobits),
            P(".text", 0x350b80, new byte[4000], Nobits),
        };

        static Sec[] Runtime(byte[] hdr, byte[] frame) => new[]
        {
            P(".eh_frame_hdr", 0x1762cc, hdr),
            P(".eh_frame", 0x1cdd78, frame),
            P(".text", 0x350b80, new byte[4000]),
        };

        [TestCase(true)]
        [TestCase(false)]
        public void The_unwind_sections_take_the_bytes_of_the_runtime_copy(bool is64)
        {
            var hdr = Bytes(40, 1);
            var frame = Bytes(1000, 7);
            var symbols = Elf(is64, Id, Stripped());
            var runtime = Elf(is64, Id, Runtime(hdr, frame));

            var result = Graft(symbols, runtime, out var ok, out var note);

            Assert.That(ok, Is.True, note);
            Assert.That(Read(result, ".eh_frame"), Is.EqualTo((Progbits, frame)));
            Assert.That(Read(result, ".eh_frame_hdr"), Is.EqualTo((Progbits, hdr)));
            // The other sections stay as they were: a placeholder is still a placeholder.
            Assert.That(Read(result, ".text").type, Is.EqualTo(Nobits));
            Assert.That(AndroidNativeLibs.Inspect(new MemoryStream(result)).BuildId, Is.EqualTo(Id));
            Assert.That(result.Length, Is.GreaterThan(symbols.Length));
        }

        [Test]
        public void The_symbols_file_is_copied_unchanged_apart_from_the_two_headers_and_the_appended_bytes()
        {
            var symbols = Elf(true, Id, Stripped());
            var result = Graft(symbols, Elf(true, Id, Runtime(Bytes(40, 1), Bytes(1000, 7))), out var ok, out _);

            Assert.That(ok, Is.True);
            int differing = Enumerable.Range(0, symbols.Length).Count(i => symbols[i] != result[i]);
            // type (1 byte: 8 -> 1) and offset (a few bytes) of each of two headers
            Assert.That(differing, Is.LessThan(40));
        }

        [Test]
        public void A_copy_with_another_build_id_is_refused()
        {
            Graft(Elf(true, Id, Stripped()), Elf(true, "0000000000000000000000000000000000000001", Runtime(Bytes(40, 1), Bytes(1000, 7))), out var ok, out var note);

            Assert.That(ok, Is.False);
            Assert.That(note, Does.Contain("build-id"));
        }

        [Test]
        public void A_section_of_another_size_or_address_is_refused()
        {
            var symbols = Elf(true, Id, Stripped());

            Graft(symbols, Elf(true, Id, Runtime(Bytes(40, 1), Bytes(999, 7))), out var wrongSize, out _);
            var moved = Runtime(Bytes(40, 1), Bytes(1000, 7));
            moved[1].Addr += 8;
            Graft(symbols, Elf(true, Id, moved), out var wrongAddress, out _);

            Assert.That(wrongSize, Is.False);
            Assert.That(wrongAddress, Is.False);
        }

        [Test]
        public void A_runtime_copy_without_the_unwind_data_is_refused()
        {
            var runtime = Elf(true, Id, P(".text", 0x350b80, new byte[4000]));

            Graft(Elf(true, Id, Stripped()), runtime, out var ok, out _);

            Assert.That(ok, Is.False);
        }

        [Test]
        public void A_symbols_file_that_already_has_the_data_is_left_alone()
        {
            var full = Elf(true, Id, Runtime(Bytes(40, 1), Bytes(1000, 7)));

            Graft(full, full, out var ok, out var note);

            Assert.That(ok, Is.False);
            Assert.That(note, Does.Contain("already"));
            Assert.That(ElfUnwindGraft.NeedsUnwindData(new MemoryStream(full)), Is.False);
        }

        [Test]
        public void Needs_the_data_only_for_a_stripped_unwind_section()
        {
            Assert.That(ElfUnwindGraft.NeedsUnwindData(new MemoryStream(Elf(true, Id, Stripped()))), Is.True);
            Assert.That(ElfUnwindGraft.NeedsUnwindData(new MemoryStream(Elf(true, Id, P(".text", 0x1000, new byte[16])))), Is.False);
        }

        [Test]
        public void Damaged_input_is_refused_without_throwing()
        {
            var good = Elf(true, Id, Runtime(Bytes(40, 1), Bytes(1000, 7)));
            var symbols = Elf(true, Id, Stripped());
            var inputs = new[] { new byte[0], new byte[10], Encoding.ASCII.GetBytes("not an elf at all, just text that is long enough to read"), good.Take(good.Length / 2).ToArray() };

            foreach (var bad in inputs)
            {
                Graft(symbols, bad, out var ok1, out _);
                Graft(bad, good, out var ok2, out _);
                Assert.That(ok1, Is.False);
                Assert.That(ok2, Is.False);
                Assert.That(ElfUnwindGraft.NeedsUnwindData(new MemoryStream(bad)), Is.False);
            }
        }
    }
}
