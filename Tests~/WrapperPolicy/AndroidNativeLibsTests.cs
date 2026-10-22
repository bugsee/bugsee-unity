using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class AndroidNativeLibsTests
    {
        const string Id = "f1e9d1f9e2e8497bb4eb6ced3ebb2f252806c81a";

        // A minimal ELF with the given sections: build-id note, and optionally .symtab / .debug_info (type 1 or NOBITS 8).
        static byte[] Elf(bool is64 = true, bool le = true, string buildId = Id, bool symtab = false, bool debug = false, int debugType = 1,
            bool sectionHeaders = true, int[] symtabKinds = null, bool programHeaders = true, bool unnamedSymbols = false, int strtabMode = 0, bool linkStrtab = true)
        {
            var sections = new List<(string name, int type, byte[] data)>();
            if (buildId != null)
            {
                var desc = Enumerable.Range(0, buildId.Length / 2).Select(i => Convert.ToByte(buildId.Substring(i * 2, 2), 16)).ToArray();
                var note = new List<byte>();
                note.AddRange(U32(4, le)); note.AddRange(U32(desc.Length, le)); note.AddRange(U32(3, le));
                note.AddRange(Encoding.ASCII.GetBytes("GNU\0")); note.AddRange(desc);
                sections.Add((".note.gnu.build-id", 7, note.ToArray()));
            }

            int symtabIndex = -1;
            if (symtab)
            {
                sections.Add((".symtab", 2, Symtab(is64, le, symtabKinds, !unnamedSymbols, strtabMode == 2 ? 1000 : 0)));
                symtabIndex = sections.Count; // header index: the null section is 0
                var count = (symtabKinds ?? new[] { 2, 1 }).Length + 8;
                var str = new byte[count + 1]; // 2: the names index far beyond it
                if (strtabMode != 1) for (int i = 0; i < str.Length - 1; i++) str[i] = (byte)'x'; // 1: every string is empty
                sections.Add((".strtab", 3, str));
            }
            if (debug) sections.Add((".debug_info", debugType, new byte[16]));

            var names = new MemoryStream();
            names.WriteByte(0);
            var nameOffsets = new List<int>();
            foreach (var s in sections.Concat(new[] { (".shstrtab", 3, new byte[0]) }))
            {
                nameOffsets.Add((int)names.Length);
                var b = Encoding.ASCII.GetBytes(s.Item1 + "\0");
                names.Write(b, 0, b.Length);
            }

            int ehsize = is64 ? 64 : 52, shentsize = is64 ? 64 : 40, phentsize = is64 ? 56 : 32;
            int dataStart = ehsize + phentsize;
            var body = new MemoryStream();
            var offsets = new List<long>();
            foreach (var s in sections)
            {
                offsets.Add(dataStart + body.Length);
                body.Write(s.data, 0, s.data.Length);
            }

            long namesOffset = dataStart + body.Length;
            var nb = names.ToArray();
            body.Write(nb, 0, nb.Length);
            long shoff = dataStart + body.Length;
            int shnum = sections.Count + 2; // null + sections + shstrtab

            var file = new MemoryStream();
            var h = new byte[ehsize];
            h[0] = 0x7f; h[1] = (byte)'E'; h[2] = (byte)'L'; h[3] = (byte)'F';
            h[4] = (byte)(is64 ? 2 : 1); h[5] = (byte)(le ? 1 : 2); h[6] = 1;
            Put(h, is64 ? 40 : 32, shoff, is64 ? 8 : 4, le);
            Put(h, is64 ? 58 : 46, shentsize, 2, le);
            Put(h, is64 ? 60 : 48, sectionHeaders ? shnum : 0, 2, le);
            Put(h, is64 ? 62 : 50, shnum - 1, 2, le);
            if (programHeaders && buildId != null)
            {
                Put(h, is64 ? 32 : 28, ehsize, is64 ? 8 : 4, le);
                Put(h, is64 ? 54 : 42, phentsize, 2, le);
                Put(h, is64 ? 56 : 44, 1, 2, le);
            }

            file.Write(h, 0, h.Length);
            var ph = new byte[phentsize];
            Put(ph, 0, 4, 4, le); // PT_NOTE
            Put(ph, is64 ? 8 : 4, offsets.Count > 0 ? offsets[0] : 0, is64 ? 8 : 4, le);
            Put(ph, is64 ? 32 : 16, buildId != null ? sections[0].data.Length : 0, is64 ? 8 : 4, le);
            file.Write(ph, 0, ph.Length);
            body.WriteTo(file);

            var table = new byte[shnum * shentsize];
            for (int i = 0; i < sections.Count; i++) Section(table, i + 1, nameOffsets[i], sections[i].type, offsets[i], sections[i].data.Length);
            Section(table, shnum - 1, nameOffsets[nameOffsets.Count - 1], 3, namesOffset, nb.Length);
            if (symtabIndex > 0 && linkStrtab) Put(table, symtabIndex * shentsize + (is64 ? 40 : 24), symtabIndex + 1, 4, le); // sh_link
            file.Write(table, 0, table.Length);
            return file.ToArray();

            void Section(byte[] t, int index, int nameOff, int type, long offset, long size)
            {
                int o = index * shentsize;
                Put(t, o, nameOff, 4, le);
                Put(t, o + 4, type, 4, le);
                Put(t, o + (is64 ? 24 : 16), offset, is64 ? 8 : 4, le);
                Put(t, o + (is64 ? 32 : 20), size, is64 ? 8 : 4, le);
            }
        }

        // One symbol per kind (STT_*), after the null symbol.
        static byte[] Symtab(bool is64, bool le, int[] kinds, bool named = true, int nameBase = 0)
        {
            kinds = kinds ?? new[] { 2, 1 };
            int entry = is64 ? 24 : 16;
            var b = new byte[entry * (kinds.Length + 1)];
            for (int i = 0; i < kinds.Length; i++)
            {
                b[(i + 1) * entry + (is64 ? 4 : 12)] = (byte)kinds[i];
                if (named) Put(b, (i + 1) * entry, nameBase + i + 1, 4, le); // st_name
            }
            return b;
        }

        static byte[] U32(long v, bool le)
        {
            var b = new byte[4];
            Put(b, 0, v, 4, le);
            return b;
        }

        static void Put(byte[] b, int o, long v, int width, bool le)
        {
            for (int i = 0; i < width; i++)
            {
                b[o + (le ? i : width - 1 - i)] = (byte)(v >> (8 * i));
            }
        }

        static ElfSymbolInfo Inspect(byte[] bytes) => AndroidNativeLibs.Inspect(new MemoryStream(bytes));

        [Test]
        public void A_library_with_dynamic_symbols_only_carries_nothing_but_has_a_build_id()
        {
            // What every Bugsee library is in the APK, the AAR and the Gradle merge folder: build-id, no .symtab, no DWARF.
            var info = Inspect(Elf());

            Assert.That(info.BuildId, Is.EqualTo(Id));
            Assert.That(info.CarriesSymbols, Is.False);
        }

        [Test]
        public void A_symbol_table_or_debug_info_makes_a_library_carry_symbols()
        {
            Assert.That(Inspect(Elf(symtab: true)).HasSymtab, Is.True);
            Assert.That(Inspect(Elf(symtab: true)).CarriesSymbols, Is.True);
            Assert.That(Inspect(Elf(debug: true)).HasDebugInfo, Is.True);
            Assert.That(Inspect(Elf(debug: true)).CarriesSymbols, Is.True);
        }

        [Test]
        public void A_debug_info_placeholder_without_bytes_carries_nothing()
        {
            Assert.That(Inspect(Elf(debug: true, debugType: 8)).CarriesSymbols, Is.False);
        }

        [TestCase(true, true)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(false, false)]
        public void Every_class_and_byte_order_is_read(bool is64, bool le)
        {
            var info = Inspect(Elf(is64, le, symtab: true));

            Assert.That(info.BuildId, Is.EqualTo(Id));
            Assert.That(info.HasSymtab, Is.True);
        }

        [Test]
        public void A_library_without_a_build_id_note_has_none()
        {
            Assert.That(Inspect(Elf(buildId: null, symtab: true)).BuildId, Is.Null);
        }

        [Test]
        public void A_library_without_section_headers_reads_as_carrying_nothing()
        {
            var info = Inspect(Elf(symtab: true, sectionHeaders: false));

            Assert.That(info.BuildId, Is.Null);
            Assert.That(info.CarriesSymbols, Is.False);
        }

        [Test]
        public void Damaged_or_foreign_files_never_throw()
        {
            var good = Elf(symtab: true);
            Assert.That(Inspect(new byte[0]).BuildId, Is.Null);
            Assert.That(Inspect(Encoding.ASCII.GetBytes("not an elf file at all, just text, long enough to pass the header size")).BuildId, Is.Null);
            for (int cut = 0; cut < good.Length; cut += 7)
            {
                Assert.DoesNotThrow(() => Inspect(good.Take(cut).ToArray()), "cut at " + cut);
            }

            var rng = new Random(7);
            for (int i = 0; i < 300; i++)
            {
                var bytes = (byte[])good.Clone();
                bytes[rng.Next(bytes.Length)] = (byte)rng.Next(256);
                Assert.DoesNotThrow(() => Inspect(bytes), "flip " + i);
            }
        }

        [Test]
        public void A_section_table_pointing_past_the_end_is_ignored()
        {
            var bytes = Elf(symtab: true);
            bytes[40] = 0xff; bytes[41] = 0xff; bytes[42] = 0xff; bytes[43] = 0x7f; // e_shoff far beyond the file

            var info = Inspect(bytes);

            Assert.That(info.BuildId, Is.Null);
            Assert.That(info.CarriesSymbols, Is.False);
        }

        [TestCase("lib/arm64-v8a/libfoo.so", "arm64-v8a", "libfoo.so")]
        [TestCase("base/lib/x86_64/libfoo.so", "x86_64", "libfoo.so")]
        [TestCase("/a/b/merged_native_libs/debug/out/lib/armeabi-v7a/libc++_shared.so", "armeabi-v7a", "libc++_shared.so")]
        [TestCase("C:\\g\\out\\lib\\x86\\libfoo.so", "x86", "libfoo.so")]
        public void A_library_path_names_its_abi_and_file(string path, string abi, string name)
        {
            Assert.That(AndroidNativeLibs.TryParseLibPath(path, out var a, out var n), Is.True);
            Assert.That(a, Is.EqualTo(abi));
            Assert.That(n, Is.EqualTo(name));
        }

        [TestCase("lib/arm64-v8a/libfoo.so.sym")]
        [TestCase("lib/arm64-v8a/.so")]
        [TestCase("jni/arm64-v8a/libfoo.so")]
        [TestCase("libfoo.so")]
        [TestCase("lib/libfoo.so")]
        [TestCase("")]
        [TestCase(null)]
        public void Other_paths_are_not_libraries(string path)
        {
            Assert.That(AndroidNativeLibs.TryParseLibPath(path, out _, out _), Is.False);
        }

        [Test]
        public void Architecture_flags_select_the_abi_folders()
        {
            Assert.That(AndroidNativeLibs.AbiFolders(2), Is.EquivalentTo(new[] { "arm64-v8a" }));
            Assert.That(AndroidNativeLibs.AbiFolders(1 | 2), Is.EquivalentTo(new[] { "armeabi-v7a", "arm64-v8a" }));
            Assert.That(AndroidNativeLibs.AbiFolders(4 | 8), Is.EquivalentTo(new[] { "x86", "x86_64" }));
            Assert.That(AndroidNativeLibs.AbiFolders(-1), Has.Count.EqualTo(4));
            Assert.That(AndroidNativeLibs.AbiFolders(0), Is.Empty);
        }

        [Test]
        public void The_unity_zip_names_are_keyed_by_abi_and_file()
        {
            var keys = AndroidNativeLibs.ZipLibraryKeys(new[] { "arm64-v8a/", "arm64-v8a/libil2cpp.so", "arm64-v8a/libunity.so", "x/readme.txt", "libmain.so", null });

            Assert.That(keys, Is.EquivalentTo(new[] { "arm64-v8a/libil2cpp.so", "arm64-v8a/libunity.so" }));
        }

        static NativeLibCandidate Lib(string abi, string name, string id, long length = 1000, string path = null) =>
            new NativeLibCandidate { Abi = abi, Name = name, BuildId = id, Length = length, Path = path ?? ("/g/" + abi + "/" + name) };

        static ISet<string> Packaged(params NativeLibCandidate[] libs) =>
            new HashSet<string>(libs.Select(l => AndroidNativeLibs.PackagedKey(l.Abi, l.Name, l.BuildId)));

        [Test]
        public void A_carrying_library_the_app_packages_is_chosen()
        {
            var a = Lib("arm64-v8a", "libplugin.so", "aa");

            var chosen = AndroidNativeLibs.Choose(new[] { a }, Packaged(a), new HashSet<string>(), new HashSet<string> { "arm64-v8a" }, new List<string>());

            Assert.That(chosen, Is.EqualTo(new[] { a }));
        }

        [Test]
        public void A_library_of_an_abi_the_build_did_not_target_is_left_out()
        {
            var a = Lib("x86_64", "libplugin.so", "aa");
            var notes = new List<string>();

            var chosen = AndroidNativeLibs.Choose(new[] { a }, Packaged(a), new HashSet<string>(), new HashSet<string> { "arm64-v8a" }, notes);

            Assert.That(chosen, Is.Empty);
        }

        [Test]
        public void A_library_already_in_unitys_zip_is_not_sent_again()
        {
            var a = Lib("arm64-v8a", "libunity.so", "aa", 30_000_000);

            var chosen = AndroidNativeLibs.Choose(
                new[] { a }, Packaged(a), new HashSet<string> { "arm64-v8a/libunity.so" }, new HashSet<string> { "arm64-v8a" }, new List<string>());

            Assert.That(chosen, Is.Empty);
        }

        [Test]
        public void A_library_with_another_build_id_than_the_packaged_one_is_left_out_and_noted()
        {
            // A stale intermediate from an earlier build: its symbols would never match the app.
            var stale = Lib("arm64-v8a", "libplugin.so", "old");
            var inApp = Lib("arm64-v8a", "libplugin.so", "new");
            var notes = new List<string>();

            var chosen = AndroidNativeLibs.Choose(new[] { stale }, Packaged(inApp), new HashSet<string>(), new HashSet<string> { "arm64-v8a" }, notes);

            Assert.That(chosen, Is.Empty);
            Assert.That(notes.Single(), Does.Contain("libplugin.so").And.Contain("old").And.Contain("not the copy packaged"));
        }

        [Test]
        public void The_same_library_found_in_two_modules_is_sent_once()
        {
            var a = Lib("arm64-v8a", "libplugin.so", "aa", path: "/g/launcher/libplugin.so");
            var b = Lib("arm64-v8a", "libplugin.so", "aa", path: "/g/unityLibrary/libplugin.so");

            var chosen = AndroidNativeLibs.Choose(new[] { a, b }, Packaged(a), new HashSet<string>(), new HashSet<string> { "arm64-v8a" }, new List<string>());

            Assert.That(chosen, Has.Count.EqualTo(1));
        }

        [Test]
        public void Libraries_over_the_size_limits_are_left_out_and_noted_smallest_kept_first()
        {
            var big = Lib("arm64-v8a", "libbig.so", "b1", AndroidNativeLibs.MaxFileBytes + 1);
            var half1 = Lib("arm64-v8a", "libh1.so", "b2", AndroidNativeLibs.MaxTotalBytes / 2);
            var half2 = Lib("arm64-v8a", "libh2.so", "b3", AndroidNativeLibs.MaxTotalBytes / 2);
            var tiny = Lib("arm64-v8a", "libtiny.so", "b4", 10);
            var notes = new List<string>();

            var chosen = AndroidNativeLibs.Choose(
                new[] { big, half1, half2, tiny }, Packaged(big, half1, half2, tiny), new HashSet<string>(), new HashSet<string> { "arm64-v8a" }, notes);

            // The two halves fit alone, but not with the tiny one in front: the smaller libraries are kept first.
            Assert.That(chosen.Select(l => l.Name).ToList(), Is.EqualTo(new[] { "libtiny.so", "libh1.so" }));
            Assert.That(notes, Has.Count.EqualTo(2));
            Assert.That(notes.Any(n => n.Contains("libbig.so") && n.Contains("size limit")), Is.True);
            Assert.That(notes.Any(n => n.Contains("libh2.so") && n.Contains("size limit")), Is.True);
        }

        [Test]
        public void Candidates_without_an_identity_are_ignored()
        {
            var chosen = AndroidNativeLibs.Choose(
                new[] { null, Lib("arm64-v8a", "libx.so", null), Lib(null, "libx.so", "aa"), Lib("arm64-v8a", null, "aa") },
                new HashSet<string>(), new HashSet<string>(), new HashSet<string>(), new List<string>());

            Assert.That(chosen, Is.Empty);
        }

        [Test]
        public void A_symbol_table_that_names_nothing_is_not_symbols()
        {
            // STT_FILE (4) and STT_SECTION (3) only: what `strip --strip-unneeded` can leave.
            Assert.That(Inspect(Elf(symtab: true, symtabKinds: new[] { 4, 3, 3, 3, 3 })).CarriesSymbols, Is.False);
            Assert.That(Inspect(Elf(symtab: true, symtabKinds: new[] { 4, 3, 2 })).CarriesSymbols, Is.True);
            Assert.That(Inspect(Elf(false, false, symtab: true, symtabKinds: new[] { 3, 1 })).CarriesSymbols, Is.True);
        }

        [Test]
        public void A_name_index_must_point_to_a_non_empty_string_in_the_linked_string_table()
        {
            Assert.That(Inspect(Elf(symtab: true)).CarriesSymbols, Is.True);
            Assert.That(Inspect(Elf(symtab: true, strtabMode: 1)).CarriesSymbols, Is.False, "every named string is empty");
            Assert.That(Inspect(Elf(symtab: true, strtabMode: 2)).CarriesSymbols, Is.False, "index beyond the string table");
            Assert.That(Inspect(Elf(symtab: true, linkStrtab: false)).CarriesSymbols, Is.False, "no linked string table");
            Assert.That(Inspect(Elf(false, false, symtab: true, strtabMode: 1)).CarriesSymbols, Is.False);
            Assert.That(Inspect(Elf(false, false, symtab: true)).CarriesSymbols, Is.True);
        }

        [Test]
        public void A_function_symbol_without_a_name_is_not_a_named_symbol()
        {
            var bytes = Elf(symtab: true, symtabKinds: new[] { 2, 1 }, unnamedSymbols: true);

            Assert.That(Inspect(bytes).CarriesSymbols, Is.False);
        }

        [Test]
        public void A_large_symbol_table_is_scanned_through_to_its_last_entry()
        {
            // 300000 section symbols and one named function at the end: past any single read chunk.
            var kinds = Enumerable.Repeat(3, 300000).Concat(new[] { 2 }).ToArray();
            var none = Enumerable.Repeat(3, 300001).ToArray();

            Assert.That(Inspect(Elf(symtab: true, symtabKinds: kinds)).CarriesSymbols, Is.True);
            Assert.That(Inspect(Elf(symtab: true, symtabKinds: none)).CarriesSymbols, Is.False);
        }

        [Test]
        public void An_oversized_section_entry_size_or_table_is_rejected_without_allocating_it()
        {
            var bytes = Elf(symtab: true);
            bytes[58] = 0xff; bytes[59] = 0xff; // e_shentsize 65535 x e_shnum: ~268 MB if trusted
            Assert.That(Inspect(bytes).BuildId, Is.Null);

            bytes = Elf(symtab: true);
            bytes[60] = 0xff; bytes[61] = 0x7f; // e_shnum 32767: more entries than the file holds
            Assert.That(Inspect(bytes).BuildId, Is.Null);
        }

        [Test]
        public void A_64_bit_offset_past_the_file_or_past_two_to_the_62_is_rejected()
        {
            var bytes = Elf(symtab: true);
            for (int i = 40; i < 48; i++) bytes[i] = 0xff; // e_shoff = 2^64 - 1
            Assert.That(Inspect(bytes).BuildId, Is.Null);

            bytes = Elf(symtab: true);
            for (int i = 40; i < 48; i++) bytes[i] = 0;
            bytes[47] = 0x40; // e_shoff = 2^62
            Assert.That(Inspect(bytes).BuildId, Is.Null);
        }

        [Test]
        public void The_build_id_is_read_from_the_program_header_note_of_a_short_prefix()
        {
            foreach (var is64 in new[] { true, false })
            foreach (var le in new[] { true, false })
            {
                var bytes = Elf(is64, le, symtab: true);
                Assert.That(AndroidNativeLibs.BuildIdFromPrefix(bytes, bytes.Length), Is.EqualTo(Id), is64 + "/" + le);
            }

            Assert.That(AndroidNativeLibs.BuildIdFromPrefix(Elf(programHeaders: false), 1000), Is.Null);
            Assert.That(AndroidNativeLibs.BuildIdFromPrefix(new byte[10], 10), Is.Null);
            Assert.That(AndroidNativeLibs.BuildIdFromPrefix(null, 0), Is.Null);
        }

        [Test]
        public void The_prefix_reader_never_throws_on_damaged_input()
        {
            var good = Elf(symtab: true);
            var rng = new Random(11);
            for (int i = 0; i < 500; i++)
            {
                var bytes = (byte[])good.Clone();
                bytes[rng.Next(bytes.Length)] = (byte)rng.Next(256);
                Assert.DoesNotThrow(() => AndroidNativeLibs.BuildIdFromPrefix(bytes, rng.Next(bytes.Length + 1)));
            }
        }

        static string Fixture(string name)
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            return Path.Combine(dir, "Fixtures", "android-native", name);
        }

        [Test]
        public void Real_clang_built_libraries_classify_as_dwarf_symtab_and_stripped_with_one_reading_each_way()
        {
            // libfixture_*.so: aarch64 NDK clang, -g; _symtab is `llvm-strip -g` of it, _stripped is `llvm-strip --strip-all`.
            foreach (var (name, dwarf, symtab) in new (string, bool, bool?)[] { ("libfixture_dwarf.so", true, true), ("libfixture_symtab.so", false, true), ("libfixture_stripped.so", false, false), ("libfixture_unneeded.so", false, null) })
            {
                var bytes = File.ReadAllBytes(Fixture(name));
                var info = Inspect(bytes);

                Assert.That(info.BuildId, Has.Length.EqualTo(40), name);
                Assert.That(info.HasDebugInfo, Is.EqualTo(dwarf), name);
                if (symtab.HasValue) Assert.That(info.HasSymtab, Is.EqualTo(symtab.Value), name);
                else Assert.That(info.CarriesSymbols, Is.EqualTo(info.HasSymtab), name);
                Assert.That(AndroidNativeLibs.BuildIdFromPrefix(bytes, Math.Min(bytes.Length, AndroidNativeLibs.PrefixBytes)), Is.EqualTo(info.BuildId), name);
            }
        }

        [Test]
        public void ScanLibraries_keeps_the_libraries_that_carry_symbols_and_counts_the_rest()
        {
            var root = Path.Combine(Path.GetTempPath(), "bugsee-nl-" + Guid.NewGuid().ToString("N"));
            try
            {
                var lib = Path.Combine(root, "debug", "out", "lib", "arm64-v8a");
                Directory.CreateDirectory(lib);
                foreach (var n in new[] { "libfixture_dwarf.so", "libfixture_symtab.so", "libfixture_stripped.so" })
                    File.Copy(Fixture(n), Path.Combine(lib, n));
                File.WriteAllText(Path.Combine(lib, "libnotelf.so"), "text, not an elf, long enough to read");
                File.WriteAllText(Path.Combine(root, "libloose.so"), "x");
                var notes = new List<string>();

                var found = AndroidNativeLibs.ScanLibraries(
                    Directory.GetFiles(root, "*.so", SearchOption.AllDirectories), out var without, notes);

                Assert.That(found.Select(l => l.Name).OrderBy(n => n).ToList(), Is.EqualTo(new[] { "libfixture_dwarf.so", "libfixture_symtab.so" }));
                Assert.That(found.All(l => l.Abi == "arm64-v8a" && l.Length > 0), Is.True);
                Assert.That(without, Is.EqualTo(1));
                Assert.That(notes, Is.Empty);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Test]
        public void ScanLibraries_skips_an_unreadable_file_and_goes_on()
        {
            var notes = new List<string>();

            var found = AndroidNativeLibs.ScanLibraries(
                new[] { "/nonexistent/lib/arm64-v8a/libgone.so", "/nonexistent/lib/x86_64/libgone2.so" },
                out _, notes);

            Assert.That(notes.Count, Is.EqualTo(2), "both paths are missing files here");
            Assert.That(found, Is.Empty);
        }

        static byte[] Zip(params (string name, byte[] data)[] entries)
        {
            var ms = new MemoryStream();
            using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
            {
                foreach (var (name, data) in entries)
                {
                    using (var s = zip.CreateEntry(name).Open()) s.Write(data, 0, data.Length);
                }
            }

            return ms.ToArray();
        }

        [Test]
        public void Packaged_build_ids_are_read_from_wanted_apk_and_aab_entries_only()
        {
            var a = File.ReadAllBytes(Fixture("libfixture_dwarf.so"));
            var b = File.ReadAllBytes(Fixture("libfixture_symtab.so"));
            var apk = Zip(("lib/arm64-v8a/libfixture_dwarf.so", a), ("base/lib/arm64-v8a/libfixture_symtab.so", b),
                ("lib/arm64-v8a/libother.so", b), ("lib/x86_64/libfixture_dwarf.so", a), ("assets/x", new byte[3]));
            var into = new HashSet<string>();

            AndroidNativeLibs.ReadPackagedBuildIds(new MemoryStream(apk), new HashSet<string> { "arm64-v8a/libfixture_dwarf.so", "arm64-v8a/libfixture_symtab.so" }, into);

            var idA = Inspect(a).BuildId;
            var idB = Inspect(b).BuildId;
            Assert.That(into, Is.EquivalentTo(new[]
            {
                AndroidNativeLibs.PackagedKey("arm64-v8a", "libfixture_dwarf.so", idA),
                AndroidNativeLibs.PackagedKey("arm64-v8a", "libfixture_symtab.so", idB),
            }));
        }

        [Test]
        public void Unity_zip_keys_are_read_from_a_zip_stream_and_symbol_suffixes_are_stripped()
        {
            var zip = Zip(("arm64-v8a/libil2cpp.so", new byte[1]), ("arm64-v8a/libfoo.so.sym", new byte[1]), ("x86_64/libbar.so.dbg", new byte[1]), ("arm64-v8a/libunity.sym.so", new byte[1]), ("arm64-v8a/libmain.dbg.so", new byte[1]), ("arm64-v8a/", new byte[0]));

            var keys = AndroidNativeLibs.ZipLibraryKeys(new MemoryStream(zip));

            Assert.That(keys, Is.EquivalentTo(new[] { "arm64-v8a/libil2cpp.so", "arm64-v8a/libfoo.so", "x86_64/libbar.so", "arm64-v8a/libunity.so", "arm64-v8a/libmain.so" }));
        }

        [Test]
        public void Staging_keeps_two_builds_of_one_library_name_apart()
        {
            var root = Path.Combine(Path.GetTempPath(), "bugsee-nl-" + Guid.NewGuid().ToString("N"));
            try
            {
                var one = Fixture("libfixture_dwarf.so");
                var two = Fixture("libfixture_symtab.so");
                var a = Lib("arm64-v8a", "libplugin.so", "aa", path: one);
                var b = Lib("arm64-v8a", "libplugin.so", "bb", path: two);

                AndroidNativeLibs.Stage(new[] { a, b }, root);

                Assert.That(File.ReadAllBytes(Path.Combine(root, "arm64-v8a", "aa", "libplugin.so")), Is.EqualTo(File.ReadAllBytes(one)));
                Assert.That(File.ReadAllBytes(Path.Combine(root, "arm64-v8a", "bb", "libplugin.so")), Is.EqualTo(File.ReadAllBytes(two)));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Test]
        public void Two_build_ids_of_one_name_are_both_chosen_when_both_are_packaged()
        {
            var a = Lib("arm64-v8a", "libplugin.so", "aa", path: "/g/base/libplugin.so");
            var b = Lib("arm64-v8a", "libplugin.so", "bb", path: "/g/feature/libplugin.so");

            var chosen = AndroidNativeLibs.Choose(new[] { a, b }, Packaged(a, b), new HashSet<string>(), new HashSet<string> { "arm64-v8a" }, new List<string>());

            Assert.That(chosen, Has.Count.EqualTo(2));
        }

        [Test]
        public void An_oversized_library_is_noted_as_oversized_not_as_unpackaged()
        {
            var big = Lib("arm64-v8a", "libbig.so", "b1", AndroidNativeLibs.MaxFileBytes + 1);
            var notes = new List<string>();

            AndroidNativeLibs.Choose(new[] { big }, new HashSet<string>(), new HashSet<string>(), new HashSet<string> { "arm64-v8a" }, notes);

            Assert.That(notes.Single(), Does.Contain("size limit").And.Not.Contain("not the copy packaged"));
        }

        [Test]
        public void The_upload_time_scales_with_the_staged_size_and_is_capped()
        {
            Assert.That(AndroidNativeLibs.UploadTimeoutMs(0, 300000), Is.EqualTo(300000));
            Assert.That(AndroidNativeLibs.UploadTimeoutMs(100L * 1024 * 1024, 300000), Is.EqualTo(500000));
            Assert.That(AndroidNativeLibs.UploadTimeoutMs(10_000L * 1024 * 1024, 300000), Is.EqualTo(30 * 60 * 1000));
        }

        [Test]
        public void Built_archives_are_the_output_file_the_split_folder_or_the_split_files_beside_a_missing_player()
        {
            var root = Path.Combine(Path.GetTempPath(), "bugsee-nl-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var started = DateTime.UtcNow.AddMinutes(-1);
                var old = DateTime.UtcNow.AddHours(-2);
                string P(string n) => Path.Combine(root, n);

                File.WriteAllText(P("Game.apk"), "x");
                Assert.That(AndroidNativeLibs.BuiltArchivePaths(P("Game.apk"), started), Is.EqualTo(new[] { P("Game.apk") }));
                File.Delete(P("Game.apk"));

                // Split APKs written beside the player path: this build's only, and not another player's.
                File.WriteAllText(P("Game.arm64-v8a.apk"), "x");
                File.WriteAllText(P("Game.armeabi-v7a.apk"), "x");
                File.WriteAllText(P("Game.x86.apk"), "x"); File.SetLastWriteTimeUtc(P("Game.x86.apk"), old);
                File.WriteAllText(P("GameLite.apk"), "x");
                Assert.That(AndroidNativeLibs.BuiltArchivePaths(P("Game.apk"), started).OrderBy(x => x).ToList(),
                    Is.EqualTo(new[] { P("Game.arm64-v8a.apk"), P("Game.armeabi-v7a.apk") }));

                // Split APKs in a folder named like the player: the stale one is left out here too.
                Directory.CreateDirectory(P("Folder.apk"));
                File.WriteAllText(Path.Combine(P("Folder.apk"), "a.apk"), "x");
                File.WriteAllText(Path.Combine(P("Folder.apk"), "b.apk"), "x"); File.SetLastWriteTimeUtc(Path.Combine(P("Folder.apk"), "b.apk"), old);
                Assert.That(AndroidNativeLibs.BuiltArchivePaths(P("Folder.apk"), started), Is.EqualTo(new[] { Path.Combine(P("Folder.apk"), "a.apk") }));

                // An exported project and nothing at all.
                Directory.CreateDirectory(P("proj"));
                Assert.That(AndroidNativeLibs.BuiltArchivePaths(P("proj"), started), Is.Empty);
                Assert.That(AndroidNativeLibs.BuiltArchivePaths(P("None.aab"), started), Is.Empty);
                Assert.That(AndroidNativeLibs.BuiltArchivePaths(null, started), Is.Empty);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        // The Editor scripts are compiled only by Unity: these check the wiring in source text, a real build checks the behaviour.
        static string Source(string relative)
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, "package.json")))
                    return File.ReadAllText(Path.Combine(dir, relative));
                dir = Directory.GetParent(dir)?.FullName;
            }

            throw new InvalidOperationException("Could not locate repo root (package.json).");
        }

        [Test]
        public void The_native_library_upload_runs_after_the_symbols_zip_and_only_past_the_opt_out_and_token_gate()
        {
            var source = Source("Editor/BugseeSymbolUpload.cs");
            int gate = source.IndexOf("BUGSEE_NO_SYMBOL_UPLOAD", StringComparison.Ordinal);
            int zip = source.IndexOf("UploadSymbolsZipOrElf(roots, version", StringComparison.Ordinal);
            int libs = source.IndexOf("BugseeAndroidNativeLibUpload.Upload(", StringComparison.Ordinal);

            Assert.That(gate, Is.GreaterThan(0));
            Assert.That(libs, Is.GreaterThan(zip), "after the symbols zip");
            Assert.That(zip, Is.GreaterThan(gate), "UploadAndroidSymbols is only called past the gate");
            Assert.That(source.Substring(0, libs), Does.Contain("static void UploadAndroidSymbols("));
            Assert.That(source.Substring(source.IndexOf("static void UploadAndroidSymbols(", StringComparison.Ordinal), libs - source.IndexOf("static void UploadAndroidSymbols(", StringComparison.Ordinal)),
                Does.Not.Contain("static void UploadSymbolsZipOrElf("));
        }

        [Test]
        public void The_editor_class_only_finds_files_and_runs_the_cli_through_the_policy()
        {
            var source = Source("Editor/BugseeAndroidNativeLibUpload.cs");

            Assert.That(source, Does.Contain("build continues"));
            Assert.That(source, Does.Contain("AndroidNativeLibs.BuiltArchivePaths("));
            Assert.That(source, Does.Contain("AndroidNativeLibs.Choose("));
            Assert.That(source, Does.Contain("AndroidNativeLibs.Stage("));
            Assert.That(source, Does.Contain("Directory.Delete(staging, true)"));
            Assert.That(Source("Editor/BugseeBuildIdentity.cs"), Does.Contain("AndroidNativeLibs.BuiltArchivePaths("), "one helper for the identity and the upload");
        }
    }
}
