#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using Bugsee.WrapperPolicy;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Bugsee.Editor
{
    /// <summary>
    /// Uploads the symbols of the native libraries Unity's symbols zip does not hold: the developer's own plugin
    /// libraries and AAR libraries that still carry a symbol table or DWARF in the Gradle build's pre-strip folder
    /// (<c>merged_native_libs</c>). What is chosen, and why a library with dynamic symbols only (the Bugsee libraries)
    /// is not, is <see cref="AndroidNativeLibs"/>; this class only finds the files and runs the CLI. Nothing here
    /// fails the build.
    /// </summary>
    static class BugseeAndroidNativeLibUpload
    {
        static string ProjectRoot => Path.GetDirectoryName(Path.GetFullPath(Application.dataPath));

        static string StagingRoot => Path.Combine(ProjectRoot, "Library", "Bugsee", "native-libs-upload");

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
            var archives = AndroidNativeLibs.BuiltArchivePaths(report.summary.outputPath, BuildClock.StartedUtc(report.summary.buildStartedAt));
            if (archives.Count == 0)
            {
                // Export Project: Gradle has not run; the Bugsee Gradle plugin (AGP 8.6+) or the exported project's own build owns this.
                return;
            }

            var notes = new List<string>();
            var carrying = AndroidNativeLibs.ScanLibraries(MergedNativeLibFiles(), out var withoutSymbols, notes, BugseeLongPathFile.OpenRead);
            if (carrying.Count == 0)
            {
                Log(notes);
                Debug.Log("Bugsee: no native library besides Unity's own carries a symbol table or debug info" +
                          (withoutSymbols > 0 ? $" ({withoutSymbols} with dynamic symbols only are not uploaded)." : "."));
                return;
            }

            var abis = AndroidNativeLibs.AbiFolders((int)PlayerSettings.Android.targetArchitectures);
            var inUnityZip = UnityZipKeys(unitySymbolsZip);
            // Read the archives only for what could still be chosen.
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var lib in carrying)
            {
                var key = lib.Abi + "/" + lib.Name;
                if (abis.Contains(lib.Abi) && !inUnityZip.Contains(key) && lib.Length <= AndroidNativeLibs.MaxFileBytes)
                {
                    wanted.Add(key);
                }
            }

            var packaged = new HashSet<string>(StringComparer.Ordinal);
            var unreadable = new List<string>();
            if (wanted.Count > 0)
            {
                foreach (var archive in archives)
                {
                    try
                    {
                        using (var stream = BugseeLongPathFile.OpenRead(archive))
                        {
                            AndroidNativeLibs.ReadPackagedBuildIds(stream, wanted, packaged);
                        }
                    }
                    catch (Exception ex)
                    {
                        var why = LongPathPolicy.Describe(archive, ex.Message);
                        unreadable.Add(Path.GetFileName(archive) + ": " + why);
                        Debug.LogWarning($"Bugsee: could not read the native libraries of {archive}: {why}");
                    }
                }
            }

            var chosen = AndroidNativeLibs.Choose(carrying, packaged, inUnityZip, abis, notes,
                unreadable.Count == 0 ? null : "the app's archive could not be read: " + string.Join("; ", unreadable));
            Log(notes);
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

                AndroidNativeLibs.Stage(chosen, staging, BugseeLongPathFile.OpenRead);
                long bytes = 0;
                foreach (var lib in chosen)
                {
                    bytes += lib.Length;
                }

                Debug.Log($"Bugsee: uploading {chosen.Count} native librar{(chosen.Count == 1 ? "y" : "ies")} with symbols " +
                          $"({string.Join(", ", chosen.ConvertAll(l => l.Abi + "/" + l.Name))}) via {BugseeCliRunner.CliPath}.");
                var exit = BugseeCliRunner.Run(
                    AndroidUploadIdentity.ElfArguments(staging, version, build, buildUuid),
                    AndroidNativeLibs.UploadTimeoutMs(bytes, BugseeCliRunner.DefaultTimeoutMs));
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

        static void Log(List<string> notes)
        {
            foreach (var note in notes)
            {
                Debug.Log("Bugsee: native library symbols: " + note + ".");
            }
        }

        // Every library under <root>/<module>/build/intermediates/merged_native_libs of the Gradle project this build ran.
        // The root is the generated project when Unity told the package where it is, else Unity's fixed place for it.
        static IEnumerable<string> MergedNativeLibFiles()
        {
            var roots = new List<string>();
            var generated = BugseeAndroidGradleSetup.GeneratedProjectRoot;
            if (!string.IsNullOrEmpty(generated) && Directory.Exists(generated))
            {
                roots.Add(generated);
            }

            var prj = Path.Combine(ProjectRoot, "Library", "Bee", "Android", "Prj");
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
                    if (!Directory.Exists(merged))
                    {
                        continue;
                    }

                    string[] files;
                    try { files = Directory.GetFiles(merged, "*.so", SearchOption.AllDirectories); }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"Bugsee: could not scan {merged} for native libraries: {ex.Message}");
                        continue;
                    }

                    foreach (var file in files)
                    {
                        yield return file;
                    }
                }
            }
        }

        static string[] SafeDirectories(string path)
        {
            try { return Directory.GetDirectories(path); }
            catch { return new string[0]; }
        }

        static ISet<string> UnityZipKeys(string zip)
        {
            if (string.IsNullOrEmpty(zip) || !File.Exists(zip))
            {
                return new HashSet<string>();
            }

            try
            {
                using (var stream = BugseeLongPathFile.OpenRead(zip))
                {
                    return AndroidNativeLibs.ZipLibraryKeys(stream);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Bugsee: could not list {zip}: {ex.Message}");
                return new HashSet<string>();
            }
        }
    }
}
#endif
