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
            bool sectionHeaders = true)
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

            if (symtab) sections.Add((".symtab", 2, new byte[48]));
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

            int ehsize = is64 ? 64 : 52, shentsize = is64 ? 64 : 40;
            var body = new MemoryStream();
            var offsets = new List<long>();
            foreach (var s in sections)
            {
                offsets.Add(ehsize + body.Length);
                body.Write(s.data, 0, s.data.Length);
            }

            long namesOffset = ehsize + body.Length;
            var nb = names.ToArray();
            body.Write(nb, 0, nb.Length);
            long shoff = ehsize + body.Length;
            int shnum = sections.Count + 2; // null + sections + shstrtab

            var file = new MemoryStream();
            var h = new byte[ehsize];
            h[0] = 0x7f; h[1] = (byte)'E'; h[2] = (byte)'L'; h[3] = (byte)'F';
            h[4] = (byte)(is64 ? 2 : 1); h[5] = (byte)(le ? 1 : 2); h[6] = 1;
            Put(h, is64 ? 40 : 32, shoff, is64 ? 8 : 4, le);
            Put(h, is64 ? 58 : 46, shentsize, 2, le);
            Put(h, is64 ? 60 : 48, sectionHeaders ? shnum : 0, 2, le);
            Put(h, is64 ? 62 : 50, shnum - 1, 2, le);
            file.Write(h, 0, h.Length);
            body.WriteTo(file);

            var table = new byte[shnum * shentsize];
            for (int i = 0; i < sections.Count; i++) Section(table, i + 1, nameOffsets[i], sections[i].type, offsets[i], sections[i].data.Length);
            Section(table, shnum - 1, nameOffsets[nameOffsets.Count - 1], 3, namesOffset, nb.Length);
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
            int zip = source.IndexOf("UploadSymbolsZipOrElf(report, roots", StringComparison.Ordinal);
            int libs = source.IndexOf("BugseeAndroidNativeLibUpload.Upload(", StringComparison.Ordinal);

            Assert.That(gate, Is.GreaterThan(0));
            Assert.That(libs, Is.GreaterThan(zip), "after the symbols zip");
            Assert.That(zip, Is.GreaterThan(gate), "UploadAndroidSymbols is only called past the gate");
            Assert.That(source.Substring(0, libs), Does.Contain("static void UploadAndroidSymbols("));
            Assert.That(source.Substring(source.IndexOf("static void UploadAndroidSymbols(", StringComparison.Ordinal), libs - source.IndexOf("static void UploadAndroidSymbols(", StringComparison.Ordinal)),
                Does.Not.Contain("static void UploadSymbolsZipOrElf("));
        }

        [Test]
        public void The_native_library_upload_never_fails_the_build_and_cleans_up_its_staging_folder()
        {
            var source = Source("Editor/BugseeAndroidNativeLibUpload.cs");

            Assert.That(source, Does.Contain("catch (Exception ex)"));
            Assert.That(source, Does.Contain("build continues"));
            Assert.That(source, Does.Contain("Directory.Delete(staging, true)"));
            Assert.That(source, Does.Contain("AndroidNativeLibs.Choose("));
            Assert.That(source, Does.Contain("AndroidUploadIdentity.ElfArguments(staging"));
        }
    }
}
