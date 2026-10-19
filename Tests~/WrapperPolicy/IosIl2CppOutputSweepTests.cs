using System;
using System.IO;
using NUnit.Framework;
using static Bugsee.WrapperPolicy.IosIl2CppOutputSweep;

namespace Bugsee.WrapperPolicy.Tests
{
    public class IosIl2CppOutputSweepTests
    {
        static readonly DateTime Start = new DateTime(2027, 1, 15, 8, 0, 0, DateTimeKind.Utc);
        static readonly DateTime Old = Start.AddHours(-2);

        [TestCase(false, false, false, false, Verdict.Absent)]
        // Unchanged Append export: Unity did not copy the file again, the build's own output is the same.
        [TestCase(true, false, true, true, Verdict.Touch)]
        [TestCase(true, true, true, true, Verdict.Current)]
        // This build wrote something else, or nothing, and the exported file predates the export.
        [TestCase(true, false, true, false, Verdict.Stale)]
        [TestCase(true, false, false, false, Verdict.Stale)]
        [TestCase(true, true, true, false, Verdict.Current)]
        [TestCase(true, true, false, false, Verdict.Current)]
        public void Classification(bool exists, bool writtenByExport, bool sourceExists, bool same, Verdict expected)
        {
            var written = writtenByExport ? Start.AddSeconds(30) : Old;
            Assert.That(Classify(exists, written, Start, sourceExists, same), Is.EqualTo(expected));
        }

        // Root/Library plays "il2cppOutput/cpp/Symbols" of the build (its parent, Root, is the cpp folder).
        sealed class Layout : IDisposable
        {
            public readonly string Root = Path.Combine(Path.GetTempPath(), "bugsee-sweep-" + Guid.NewGuid().ToString("N"));
            public string Export => Path.Combine(Root, "export");
            public string Build => Path.Combine(Root, "Library");
            public string Symbols => ExportSymbolsDirectory(Export);

            public Layout(bool withBuildCpp = true)
            {
                Directory.CreateDirectory(Symbols);
                Directory.CreateDirectory(Build);
                if (withBuildCpp)
                {
                    // A generated source this build wrote, copied into the export unchanged.
                    Put(Root, "Assembly-CSharp.cpp", "// generated", Old);
                    Put(Path.GetDirectoryName(Symbols), "Assembly-CSharp.cpp", "// generated", Old);
                }
            }

            public string Put(string dir, string name, string content, DateTime mtime)
            {
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, name);
                File.WriteAllText(path, content);
                File.SetLastWriteTimeUtc(path, mtime);
                return path;
            }

            public void Dispose()
            {
                try
                {
                    foreach (var f in Directory.GetFiles(Root, "*", SearchOption.AllDirectories))
                        File.SetAttributes(f, FileAttributes.Normal);
                    Directory.Delete(Root, true);
                }
                catch (Exception) { }
            }
        }

        // The everyday loop: Append into the same folder with nothing changed. Unity copies nothing, so the exported map keeps
        // the earlier export's time; deleting it (or the script skipping it) would lose the line map.
        [Test]
        public void An_unchanged_append_keeps_the_map_and_brings_its_time_up_to_date()
        {
            using (var l = new Layout())
            {
                // The export that is running began a moment ago; the files are from the export before.
                var start = DateTime.UtcNow.AddMinutes(-1);
                var old = start.AddHours(-2);
                var map = l.Put(l.Symbols, "LineNumberMappings.json", "{\"a\":1}", old);
                var methods = l.Put(l.Symbols, "MethodMap.tsv", "m\tn", old);
                l.Put(l.Build, "LineNumberMappings.json", "{\"a\":1}", old);
                l.Put(l.Build, "MethodMap.tsv", "m\tn", old);

                var result = Run(l.Export, l.Build, start, mayDelete: true);

                Assert.That(File.Exists(map) && File.Exists(methods), Is.True, string.Join("\n", result.Actions));
                Assert.That(BuildArtifactChoice.WrittenByExport(File.GetLastWriteTimeUtc(map), start), Is.True);
                Assert.That(BuildArtifactChoice.WrittenByExport(File.GetLastWriteTimeUtc(methods), start), Is.True);
                Assert.That(result.Actions, Has.Count.EqualTo(2));
                Assert.That(result.AgeCheckSafe, Is.True);
            }
        }

        [Test]
        public void A_file_this_build_did_not_write_is_deleted_and_its_current_neighbour_is_kept()
        {
            using (var l = new Layout())
            {
                var map = l.Put(l.Symbols, "LineNumberMappings.json", "{}", Old);
                var methods = l.Put(l.Symbols, "MethodMap.tsv", "old", Old);
                l.Put(l.Build, "LineNumberMappings.json", "{}", Old); // built with --emit-source-mapping only

                Run(l.Export, l.Build, Start, mayDelete: true);

                Assert.That(File.Exists(map), Is.True);
                Assert.That(File.Exists(methods), Is.False);
            }
        }

        [Test]
        public void A_map_that_differs_from_this_builds_and_predates_the_export_is_deleted()
        {
            using (var l = new Layout())
            {
                var map = l.Put(l.Symbols, "LineNumberMappings.json", "{\"old\":1}", Old);
                l.Put(l.Build, "LineNumberMappings.json", "{\"new\":1}", Old);

                Run(l.Export, l.Build, Start, mayDelete: true);

                Assert.That(File.Exists(map), Is.False);
            }
        }

        [Test]
        public void Nothing_is_deleted_when_bugsee_is_not_handling_the_map()
        {
            using (var l = new Layout())
            {
                var map = l.Put(l.Symbols, "LineNumberMappings.json", "{}", Old);
                l.Put(l.Build, "MethodMap.tsv", "x", Old);

                var result = Run(l.Export, l.Build, Start, mayDelete: false);

                Assert.That(File.Exists(map), Is.True);
                Assert.That(result.Actions, Has.Some.StartWith("left stale"));
            }
        }

        [Test]
        public void A_file_written_by_the_export_is_left_alone_whatever_the_build_output_holds()
        {
            using (var l = new Layout())
            {
                var map = l.Put(l.Symbols, "LineNumberMappings.json", "{}", Start.AddSeconds(5));

                Assert.That(Run(l.Export, l.Build, Start, true).Actions, Is.Empty);
                Assert.That(File.Exists(map), Is.True);
            }
        }

        // Unity removes the build's Symbols folder when the build emits no map: the exported files are then an earlier build's.
        [Test]
        public void A_build_without_a_symbols_folder_emitted_no_map_so_old_exported_files_go()
        {
            using (var l = new Layout())
            {
                var map = l.Put(l.Symbols, "LineNumberMappings.json", "{}", Old);
                Run(l.Export, Path.Combine(l.Root, "Symbols"), Start, true);
                Assert.That(File.Exists(map), Is.False);
            }
        }

        // The build's own folder is not where this knows to look (another Unity layout): nothing can be proven, nothing is
        // touched and the Run Script is not given an export time.
        [Test]
        public void Nothing_happens_without_the_builds_own_output_folder()
        {
            using (var l = new Layout())
            {
                var map = l.Put(l.Symbols, "LineNumberMappings.json", "{}", Old);

                foreach (var build in new[] { Path.Combine(l.Root, "nodir", "Symbols"), null })
                {
                    var result = Run(l.Export, build, Start, true);
                    Assert.That(result.Actions, Is.Empty);
                    Assert.That(result.AgeCheckSafe, Is.False);
                }
                Assert.That(File.Exists(map), Is.True);
            }
        }

        // M1: a cpp folder in Library that another build left (nothing in it equals the export's sources) is not this build's.
        [Test]
        public void A_library_folder_from_another_build_is_not_trusted()
        {
            using (var l = new Layout(withBuildCpp: false))
            {
                l.Put(l.Root, "Assembly-CSharp.cpp", "// an older build", Old);
                l.Put(Path.GetDirectoryName(l.Symbols), "Assembly-CSharp.cpp", "// this build", Old);
                var map = l.Put(l.Symbols, "LineNumberMappings.json", "{\"valid\":1}", Old);
                l.Put(l.Build, "LineNumberMappings.json", "{\"other\":1}", Old);

                Assert.That(BuildOutputMatches(l.Export, l.Build), Is.False);
                var result = Run(l.Export, l.Build, Start, true);

                Assert.That(File.Exists(map), Is.True, "a valid map is not deleted on the word of another build's folder");
                Assert.That(result.AgeCheckSafe, Is.False);
            }
        }

        [Test]
        public void A_library_folder_without_any_generated_source_is_not_trusted()
        {
            using (var l = new Layout(withBuildCpp: false))
            {
                Assert.That(BuildOutputMatches(l.Export, l.Build), Is.False);
            }
        }

        [Test]
        public void A_library_folder_holding_a_source_identical_to_the_exports_is_this_builds()
        {
            using (var l = new Layout())
            {
                Assert.That(BuildOutputMatches(l.Export, l.Build), Is.True);
                Assert.That(BuildOutputMatches(l.Export, Path.Combine(l.Root, "nodir", "Symbols")), Is.False);
                Assert.That(BuildOutputMatches(null, l.Build), Is.False);
                Assert.That(BuildOutputMatches(l.Export, null), Is.False);
            }
        }

        // M2: a file whose time cannot be set does not stop the others, and the Run Script gets no export time.
        [Test]
        public void A_file_that_fails_does_not_stop_the_other_files()
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT) Assert.Ignore("needs unix file modes");
            using (var l = new Layout())
            {
                var start = DateTime.UtcNow.AddMinutes(-1);
                var old = start.AddHours(-2);
                var map = l.Put(l.Symbols, "LineNumberMappings.json", "{}", old);
                var methods = l.Put(l.Symbols, "MethodMap.tsv", "m", old);
                l.Put(l.Build, "LineNumberMappings.json", "{}", old);
                l.Put(l.Build, "MethodMap.tsv", "m", old);
                var root = l.Put(l.Symbols, "il2cppFileRoot.txt", "stale", old);

                // A folder the user cannot write to: deleting the stale file there fails with an exception, which must not
                // stop the files after it (the order is map, MethodMap, il2cppFileRoot: put the failing one first by
                // making the map itself the stale one is not possible, so the failing delete comes last and the
                // two touches before it must still have happened).
                File.SetUnixFileMode(l.Symbols, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                try
                {
                    var result = Run(l.Export, l.Build, start, mayDelete: true);
                    Assert.That(result.Actions, Has.Some.StartWith("touched"), string.Join("\n", result.Actions));
                    Assert.That(File.Exists(map) && File.Exists(methods), Is.True);
                    if (Environment.UserName != "root")
                    {
                        Assert.That(result.Actions, Has.Some.StartWith("failed"), string.Join("\n", result.Actions));
                        Assert.That(File.Exists(root), Is.True);
                    }
                }
                finally
                {
                    File.SetUnixFileMode(l.Symbols, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }
        }

        // Only the exact folder Unity writes to: a map of the user's own elsewhere in the export (or vendored) is not Bugsee's.
        [Test]
        public void Files_with_the_same_names_elsewhere_in_the_export_are_not_touched()
        {
            using (var l = new Layout())
            {
                l.Put(l.Build, "MethodMap.tsv", "x", Old);
                var mine = l.Put(Path.Combine(l.Export, "Vendor", "Symbols"), "LineNumberMappings.json", "{}", Old);
                var beside = l.Put(Path.Combine(l.Export, "Il2CppOutputProject"), "MethodMap.tsv", "y", Old);

                Run(l.Export, l.Build, Start, true);

                Assert.That(File.Exists(mine) && File.Exists(beside), Is.True);
            }
        }

        // L3: a link in the way disables the touch, so the age check must go too.
        [Test]
        public void Nothing_behind_a_link_is_touched_and_the_age_check_is_withdrawn()
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT) Assert.Ignore("needs symbolic links");
            using (var l = new Layout())
            {
                l.Put(l.Build, "MethodMap.tsv", "x", Old);
                // A linked file: its target outside the export must survive.
                var outside = l.Put(Path.Combine(l.Root, "outside"), "LineNumberMappings.json", "{}", Old);
                File.CreateSymbolicLink(Path.Combine(l.Symbols, "LineNumberMappings.json"), outside);
                // A linked folder in place of the Symbols folder of a second export (it needs the matching source too).
                var export2 = Path.Combine(l.Root, "export2");
                var elsewhere = Path.Combine(l.Root, "elsewhere");
                var victim = l.Put(elsewhere, "MethodMap.tsv", "old", Old);
                l.Put(Path.Combine(export2, "Il2CppOutputProject", "Source", "il2cppOutput"), "Assembly-CSharp.cpp", "// generated", Old);
                Directory.CreateSymbolicLink(ExportSymbolsDirectory(export2), elsewhere);

                var r1 = Run(l.Export, l.Build, Start, true);
                var r2 = Run(export2, l.Build, Start, true);

                Assert.That(File.Exists(outside), Is.True);
                Assert.That(File.Exists(victim), Is.True);
                Assert.That(r1.AgeCheckSafe, Is.False);
                Assert.That(r2.AgeCheckSafe, Is.False);

                // A link higher up: Il2CppOutputProject itself points outside the export.
                var export3 = Path.Combine(l.Root, "export3");
                var realProject = Path.Combine(l.Root, "realProject");
                var victim2 = l.Put(Path.Combine(realProject, "Source", "il2cppOutput", "Symbols"), "MethodMap.tsv", "old", Old);
                l.Put(Path.Combine(realProject, "Source", "il2cppOutput"), "Assembly-CSharp.cpp", "// generated", Old);
                Directory.CreateDirectory(export3);
                Directory.CreateSymbolicLink(Path.Combine(export3, "Il2CppOutputProject"), realProject);
                var r3 = Run(export3, l.Build, Start, true);

                Assert.That(File.Exists(victim2), Is.True);
                Assert.That(r3.AgeCheckSafe, Is.False);
            }
        }

        // A file server whose clock lags: the time of a file the export copied is before the build's start. The content
        // comparison, not the clock, says it is current.
        [Test]
        public void A_clock_that_runs_behind_does_not_make_a_current_map_stale()
        {
            using (var l = new Layout())
            {
                var map = l.Put(l.Symbols, "LineNumberMappings.json", "{}", Start.AddMinutes(-10));
                l.Put(l.Build, "LineNumberMappings.json", "{}", Start.AddMinutes(-10));

                Run(l.Export, l.Build, Start, true);

                Assert.That(File.Exists(map), Is.True);
            }
        }

        [Test]
        public void The_injector_takes_the_export_time_from_the_sweep_and_the_sweep_runs_first()
        {
            var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "package.json"))) dir = dir.Parent;
            var source = File.ReadAllText(Path.Combine(dir.FullName, "Editor", "BugseeSymbolUpload.cs"));
            int check = source.IndexOf("BugseeIl2CppLinemapUpload.IosAgeCheckSafe");
            int zero = source.IndexOf("exportStartEpoch = 0;");
            Assert.That(check, Is.GreaterThan(0));
            Assert.That(zero, Is.GreaterThan(check));
            var linemap = File.ReadAllText(Path.Combine(dir.FullName, "Editor", "BugseeIl2CppLinemapUpload.cs"));
            Assert.That(linemap, Does.Contain("public int callbackOrder => 900;"));
            Assert.That(source, Does.Contain("public int callbackOrder => 910;"));
        }

        [Test]
        public void The_build_and_export_folders_are_the_ones_unity_uses()
        {
            Assert.That(ExportSymbolsDirectory("/x"),
                Is.EqualTo(Path.Combine("/x", "Il2CppOutputProject", "Source", "il2cppOutput", "Symbols")));
            Assert.That(BuildSymbolsDirectory("/p", "iOS"),
                Is.EqualTo(Path.Combine("/p", "Library", "Bee", "artifacts", "iOS", "il2cppOutput", "cpp", "Symbols")));
        }
    }
}
