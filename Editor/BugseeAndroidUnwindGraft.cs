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
    /// Gives the libraries of Unity's symbols zip the unwind tables Unity strips from them, taken from the copies in the built APK/AAB
    /// (same build-id; see <see cref="ElfUnwindGraft"/> for why). The upload then carries names and CFI in one file. Nothing here fails
    /// the build: on any problem the original zip is uploaded as before.
    /// </summary>
    static class BugseeAndroidUnwindGraft
    {
        static string ProjectRoot => Path.GetDirectoryName(Path.GetFullPath(Application.dataPath));

        internal static string StagingRoot => Path.Combine(ProjectRoot, "Library", "Bugsee", "unwind-graft");

        /// <summary>
        /// The zip to upload: a copy of <paramref name="symbolsZip"/> with the unwind data added to the libraries that lack it, or
        /// <paramref name="symbolsZip"/> itself when there is nothing to add or it cannot be done. Call <see cref="Cleanup"/> after the upload.
        /// </summary>
        internal static string Prepare(BuildReport report, string symbolsZip)
        {
            try
            {
                return PrepareCore(report, symbolsZip) ?? symbolsZip;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Bugsee: adding unwind data to the symbols failed: {ex.Message} — uploading the symbols as Unity wrote them.");
                Cleanup();
                return symbolsZip;
            }
        }

        internal static void Cleanup()
        {
            try
            {
                if (Directory.Exists(StagingRoot))
                {
                    Directory.Delete(StagingRoot, true);
                }
            }
            catch
            {
                // best effort
            }
        }

        static string PrepareCore(BuildReport report, string symbolsZip)
        {
            if (string.IsNullOrEmpty(symbolsZip) || !File.Exists(symbolsZip))
            {
                Debug.Log("Bugsee: symbols unwind data: no symbols zip to add it to.");
                return null;
            }

            var archives = AndroidNativeLibs.BuiltArchivePaths(report.summary.outputPath, BuildClock.StartedUtc(report.summary.buildStartedAt));
            if (archives.Count == 0)
            {
                // Export Project: no APK/AAB to take the unwind data from.
                Debug.Log($"Bugsee: symbols unwind data: no APK/AAB found for the output '{report.summary.outputPath}' (an Export Project has none), so Unity's symbols are uploaded as they are.");
                return null;
            }

            var staging = StagingRoot;
            Cleanup();
            Directory.CreateDirectory(staging);
            var target = Path.Combine(staging, Path.GetFileName(symbolsZip));
            var grafted = new List<string>();
            var notes = new List<string>();

            using (var input = BugseeLongPathFile.OpenRead(symbolsZip))
            using (var zip = new ZipArchive(input, ZipArchiveMode.Read))
            {
                var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
                var runtimeCopies = new Dictionary<string, string>(StringComparer.Ordinal);
                var counter = 0;
                foreach (var entry in zip.Entries)
                {
                    if (!TryParseZipEntry(entry.FullName, out var abi, out var name)
                        || entry.Length <= 0 || entry.Length > AndroidNativeLibs.MaxFileBytes)
                    {
                        continue;
                    }

                    var symbolsFile = Path.Combine(staging, "sym" + (++counter));
                    Extract(entry, symbolsFile);
                    bool needs;
                    using (var s = File.OpenRead(symbolsFile))
                    {
                        needs = ElfUnwindGraft.NeedsUnwindData(s);
                    }

                    if (!needs)
                    {
                        File.Delete(symbolsFile);
                        continue;
                    }

                    var key = abi + "/" + name;
                    if (!runtimeCopies.TryGetValue(key, out var runtimeFile))
                    {
                        runtimeFile = Path.Combine(staging, "rt" + counter);
                        runtimeCopies[key] = ExtractFromArchives(archives, abi, name, runtimeFile) ? runtimeFile : null;
                        runtimeFile = runtimeCopies[key];
                    }

                    if (runtimeFile == null)
                    {
                        notes.Add(key + ": no copy in the app's archive");
                        File.Delete(symbolsFile);
                        continue;
                    }

                    var graftedFile = Path.Combine(staging, "out" + counter);
                    bool ok;
                    string note;
                    using (var s = File.OpenRead(symbolsFile))
                    using (var r = File.OpenRead(runtimeFile))
                    using (var o = new FileStream(graftedFile, FileMode.Create, FileAccess.ReadWrite))
                    {
                        ok = ElfUnwindGraft.TryGraft(s, r, o, out note);
                    }

                    File.Delete(symbolsFile);
                    if (ok)
                    {
                        replacements[entry.FullName] = graftedFile;
                        grafted.Add(key);
                    }
                    else
                    {
                        notes.Add(key + ": " + note);
                        File.Delete(graftedFile);
                    }
                }

                if (replacements.Count == 0)
                {
                    Debug.Log("Bugsee: symbols unwind data: none of the symbols zip's libraries needs it" + (notes.Count > 0 ? ":" : "."));
                    foreach (var note in notes)
                    {
                        Debug.Log("Bugsee: symbols unwind data: " + note + ".");
                    }

                    Cleanup();
                    return null;
                }

                using (var outStream = new FileStream(target, FileMode.Create, FileAccess.Write))
                using (var outZip = new ZipArchive(outStream, ZipArchiveMode.Create))
                {
                    foreach (var entry in zip.Entries)
                    {
                        if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
                        {
                            outZip.CreateEntry(entry.FullName);
                            continue;
                        }

                        var copy = outZip.CreateEntry(entry.FullName, System.IO.Compression.CompressionLevel.Optimal);
                        copy.LastWriteTime = entry.LastWriteTime;
                        using (var to = copy.Open())
                        {
                            if (replacements.TryGetValue(entry.FullName, out var path))
                            {
                                using (var from = File.OpenRead(path))
                                {
                                    from.CopyTo(to);
                                }
                            }
                            else
                            {
                                using (var from = entry.Open())
                                {
                                    from.CopyTo(to);
                                }
                            }
                        }
                    }
                }
            }

            foreach (var note in notes)
            {
                Debug.Log("Bugsee: symbols unwind data: " + note + ".");
            }

            Debug.Log($"Bugsee: added the unwind data of the app's copy to {grafted.Count} librar{(grafted.Count == 1 ? "y" : "ies")} of the symbols zip " +
                      $"({string.Join(", ", grafted)}), so a minidump crash unwinds through them.");
            return target;
        }

        // Unity's symbols zip holds <abi>/<name>.so (an APK holds lib/<abi>/<name>.so, which TryParseLibPath reads).
        static bool TryParseZipEntry(string entryName, out string abi, out string name)
        {
            abi = null;
            name = null;
            var parts = (entryName ?? string.Empty).Replace('\\', '/').Split('/');
            if (parts.Length < 2)
            {
                return false;
            }

            var file = parts[parts.Length - 1];
            var folder = parts[parts.Length - 2];
            if (folder.Length == 0 || file.Length <= 3 || !file.EndsWith(".so", StringComparison.Ordinal))
            {
                return false;
            }

            abi = folder;
            name = file;
            return true;
        }

        static void Extract(ZipArchiveEntry entry, string path)
        {
            using (var from = entry.Open())
            using (var to = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                from.CopyTo(to);
            }
        }

        // lib/<abi>/<name> of the first archive that has it (one APK per ABI for Split APKs; an AAB has base/lib/<abi>/<name>).
        static bool ExtractFromArchives(List<string> archives, string abi, string name, string path)
        {
            foreach (var archive in archives)
            {
                try
                {
                    using (var stream = BugseeLongPathFile.OpenRead(archive))
                    using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                    {
                        foreach (var entry in zip.Entries)
                        {
                            if (AndroidNativeLibs.TryParseLibPath(entry.FullName, out var entryAbi, out var entryName)
                                && entryAbi == abi && entryName == name && entry.Length > 0 && entry.Length <= AndroidNativeLibs.MaxFileBytes)
                            {
                                Extract(entry, path);
                                return true;
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
                {
                    Debug.LogWarning($"Bugsee: could not read {archive}: {ex.Message}");
                }
            }

            return false;
        }
    }
}
#endif
