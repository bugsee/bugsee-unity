#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Bugsee.WrapperPolicy;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Bugsee.Editor
{
    /// <summary>
    /// Uploads the symbols of the native libraries Unity's symbols zip does not hold: the developer's own plugin
    /// libraries and AAR libraries that still carry a symbol table or DWARF in the Gradle build's pre-strip folder
    /// (<c>merged_native_libs</c>). The APK's copies of those are stripped, so without this a crash frame in them stays
    /// module and offset. What is chosen, and why a library with dynamic symbols only (the Bugsee libraries) is not, is
    /// <see cref="AndroidNativeLibs"/>; nothing here fails the build.
    /// </summary>
    static class BugseeAndroidNativeLibUpload
    {
        static string StagingRoot =>
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Application.dataPath)), "Library", "Bugsee", "native-libs-upload");

        internal static void Upload(BuildReport report, string version, string build, string buildUuid, string unitySymbolsZip)
        {
            try
            {
                UploadCore(report, version, build, buildUuid, unitySymbolsZip);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Bugsee: uploading native library symbols failed: {ex.Message} — build continues.");
            }
        }

        static void UploadCore(BuildReport report, string version, string build, string buildUuid, string unitySymbolsZip)
        {
            var output = report.summary.outputPath;
            var archives = BuiltArchives(output);
            if (archives.Count == 0)
            {
                // Export Project: Gradle has not run, the Bugsee Gradle plugin (AGP 8.6+) or the exported project's own build owns this.
                return;
            }

            var carrying = new List<NativeLibCandidate>();
            var stripped = 0;
            foreach (var dir in MergedNativeLibDirectories())
            {
                foreach (var path in SafeFiles(dir))
                {
                    if (!AndroidNativeLibs.TryParseLibPath(path, out var abi, out var name))
                    {
                        continue;
                    }

                    ElfSymbolInfo info;
                    long length;
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        length = stream.Length;
                        info = AndroidNativeLibs.Inspect(stream);
                    }

                    if (info.BuildId == null)
                    {
                        continue;
                    }

                    if (!info.CarriesSymbols)
                    {
                        stripped++;
                        continue;
                    }

                    carrying.Add(new NativeLibCandidate { Path = path, Abi = abi, Name = name, BuildId = info.BuildId, Length = length });
                }
            }

            if (carrying.Count == 0)
            {
                Debug.Log("Bugsee: no native library besides Unity's own carries a symbol table or debug info" +
                          (stripped > 0 ? $" ({stripped} with dynamic symbols only are not uploaded)." : "."));
                return;
            }

            var wanted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var lib in carrying)
            {
                wanted.Add(lib.Abi + "/" + lib.Name);
            }

            var packaged = ReadPackagedBuildIds(archives, wanted);
            var notes = new List<string>();
            var chosen = AndroidNativeLibs.Choose(
                carrying, packaged, UnityZipKeys(unitySymbolsZip), AndroidNativeLibs.AbiFolders((int)PlayerSettings.Android.targetArchitectures), notes);
            foreach (var note in notes)
            {
                Debug.Log("Bugsee: native library symbols: " + note + ".");
            }

            if (chosen.Count == 0)
            {
                return;
            }

            var staging = StagingRoot;
            try
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, true);
                }

                foreach (var lib in chosen)
                {
                    var target = Path.Combine(staging, lib.Abi, lib.Name);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Copy(lib.Path, target, true);
                }

                Debug.Log($"Bugsee: uploading {chosen.Count} native librar{(chosen.Count == 1 ? "y" : "ies")} with symbols " +
                          $"({string.Join(", ", chosen.ConvertAll(l => l.Abi + "/" + l.Name))}) via {BugseeCliRunner.CliPath}.");
                var exit = BugseeCliRunner.Run(AndroidUploadIdentity.ElfArguments(staging, version, build, buildUuid));
                if (exit != 0)
                {
                    Debug.LogWarning($"Bugsee: bugsee-cli elf upload of the native libraries failed (exit {exit}) — build continues.");
                }
            }
            finally
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { /* best effort */ }
            }
        }

        // The APK, or the APKs of Split APKs by target architecture, or the AAB this build wrote. Empty for an Export Project.
        static List<string> BuiltArchives(string output)
        {
            var archives = new List<string>();
            if (string.IsNullOrEmpty(output))
            {
                return archives;
            }

            if (File.Exists(output))
            {
                archives.Add(output);
            }
            else if (Directory.Exists(output) && output.TrimEnd('/', '\\').EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
            {
                archives.AddRange(Directory.GetFiles(output, "*.apk"));
            }

            return archives;
        }

        // Every merged_native_libs folder of the Gradle project this build ran: <root>/<module>/build/intermediates/merged_native_libs.
        // The root is the generated project when Unity told the package where it is, else Unity's fixed place for it.
        static IEnumerable<string> MergedNativeLibDirectories()
        {
            var roots = new List<string>();
            var generated = BugseeAndroidGradleSetup.GeneratedProjectRoot;
            if (!string.IsNullOrEmpty(generated) && Directory.Exists(generated))
            {
                roots.Add(generated);
            }

            var prj = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Application.dataPath)), "Library", "Bee", "Android", "Prj");
            if (Directory.Exists(prj))
            {
                foreach (var backend in SafeDirectories(prj))
                {
                    var gradle = Path.Combine(backend, "Gradle");
                    if (Directory.Exists(gradle) && !roots.Contains(gradle))
                    {
                        roots.Add(gradle);
                    }
                }
            }

            foreach (var root in roots)
            {
                foreach (var module in SafeDirectories(root))
                {
                    var merged = Path.Combine(module, "build", "intermediates", "merged_native_libs");
                    if (Directory.Exists(merged))
                    {
                        yield return merged;
                    }
                }
            }
        }

        static IEnumerable<string> SafeDirectories(string path)
        {
            try { return Directory.GetDirectories(path); }
            catch { return new string[0]; }
        }

        static IEnumerable<string> SafeFiles(string path)
        {
            try { return Directory.GetFiles(path, "*.so", SearchOption.AllDirectories); }
            catch (Exception ex)
            {
                Debug.LogWarning($"Bugsee: could not scan {path} for native libraries: {ex.Message}");
                return new string[0];
            }
        }

        static ISet<string> UnityZipKeys(string zip)
        {
            if (string.IsNullOrEmpty(zip) || !File.Exists(zip))
            {
                return new HashSet<string>();
            }

            try
            {
                using (var archive = new ZipArchive(File.OpenRead(zip), ZipArchiveMode.Read))
                {
                    var names = new List<string>();
                    foreach (var entry in archive.Entries)
                    {
                        names.Add(entry.FullName);
                    }

                    return AndroidNativeLibs.ZipLibraryKeys(names);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Bugsee: could not list {zip}: {ex.Message}");
                return new HashSet<string>();
            }
        }

        // The (ABI, name, build-id) of every wanted library the built app carries. Only the wanted entries are read.
        static ISet<string> ReadPackagedBuildIds(List<string> archives, ISet<string> wanted)
        {
            var packaged = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in archives)
            {
                try
                {
                    using (var zip = new ZipArchive(File.OpenRead(path), ZipArchiveMode.Read))
                    {
                        foreach (var entry in zip.Entries)
                        {
                            if (!AndroidNativeLibs.TryParseLibPath(entry.FullName, out var abi, out var name)
                                || !wanted.Contains(abi + "/" + name)
                                || entry.Length > AndroidNativeLibs.MaxFileBytes)
                            {
                                continue;
                            }

                            using (var raw = entry.Open())
                            using (var memory = new MemoryStream((int)entry.Length))
                            {
                                raw.CopyTo(memory);
                                var info = AndroidNativeLibs.Inspect(memory);
                                if (info.BuildId != null)
                                {
                                    packaged.Add(AndroidNativeLibs.PackagedKey(abi, name, info.BuildId));
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Bugsee: could not read the native libraries of {path}: {ex.Message}");
                }
            }

            return packaged;
        }
    }
}
#endif
