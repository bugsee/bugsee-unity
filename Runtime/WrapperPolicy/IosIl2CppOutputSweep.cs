using System;
using System.Collections.Generic;
using System.IO;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Settles, at the end of an iOS/tvOS Unity export, which of the IL2CPP symbol files in the exported project belong to
    /// this build. Unity copies a file into an Xcode export only when it changed, so an "Append" export into the same folder
    /// with unchanged code leaves this build's map there with the modification time of the earlier export: a time older than
    /// the export's start does not make a file stale. What decides is the build's own output (Library/Bee/artifacts/&lt;platform&gt;/
    /// il2cppOutput/cpp/Symbols): a file equal to it is current (its time is brought up to date, so the Run Script's check
    /// holds), and one that differs from it, or that this build did not write at all, and is older than the export, is
    /// left over from an earlier build.
    /// </summary>
    public static class IosIl2CppOutputSweep
    {
        public static readonly string[] Names = { "LineNumberMappings.json", "MethodMap.tsv", "il2cppFileRoot.txt" };

        public enum Verdict { Absent, Current, Touch, Stale }

        /// <summary>The only folder Unity writes them to in an Xcode export.</summary>
        public static string ExportSymbolsDirectory(string exportRoot) =>
            Path.Combine(exportRoot, "Il2CppOutputProject", "Source", "il2cppOutput", "Symbols");

        /// <summary>This build's own copy, in the Unity project's Library.</summary>
        public static string BuildSymbolsDirectory(string projectRoot, string platformFolder) =>
            Path.Combine(projectRoot, "Library", "Bee", "artifacts", platformFolder, "il2cppOutput", "cpp", "Symbols");

        /// <summary>
        /// True when the folder <see cref="BuildSymbolsDirectory"/> names (its parent, il2cppOutput/cpp; the Symbols folder is
        /// absent when the build emitted no map) holds THIS build's output: one of its generated .cpp files is identical to the
        /// export's copy under Il2CppOutputProject/Source/il2cppOutput. A folder left behind by another build, or another Unity
        /// layout, proves nothing about the exported files, and then neither the sweep nor the Run Script's age check may act: a
        /// valid map that an unchanged Append export did not copy again would be deleted or skipped.
        /// </summary>
        public static bool BuildOutputMatches(string exportRoot, string buildSymbols)
        {
            string cpp = string.IsNullOrEmpty(buildSymbols) ? null : Path.GetDirectoryName(buildSymbols);
            if (string.IsNullOrEmpty(exportRoot) || string.IsNullOrEmpty(cpp) || !Directory.Exists(cpp)) return false;
            string exported = Path.Combine(exportRoot, "Il2CppOutputProject", "Source", "il2cppOutput");
            if (!Directory.Exists(exported)) return false;
            try
            {
                var files = new List<FileInfo>(new DirectoryInfo(cpp).GetFiles("*.cpp"));
                files.RemoveAll(f => f.Length == 0);
                files.Sort((a, b) => b.Length.CompareTo(a.Length));
                for (int i = 0; i < files.Count && i < 4; i++)
                {
                    string counterpart = Path.Combine(exported, files[i].Name);
                    if (File.Exists(counterpart) && SameContent(files[i].FullName, counterpart)) return true;
                }
            }
            catch (Exception)
            {
            }

            return false;
        }

        public static Verdict Classify(bool exists, DateTime fileWriteUtc, DateTime exportStartUtc, bool sourceExists, bool sameAsSource)
        {
            if (!exists) return Verdict.Absent;
            bool written = BuildArtifactChoice.WrittenByExport(fileWriteUtc, exportStartUtc);
            if (sourceExists && sameAsSource) return written ? Verdict.Current : Verdict.Touch;
            return written ? Verdict.Current : Verdict.Stale;
        }

        public sealed class Result
        {
            public readonly List<string> Actions = new List<string>();

            /// <summary>
            /// True when the exported files could be checked against this build's own output and every current one is fresh:
            /// the Run Script's age check is then safe. False (the export time is not given to the script) when this build's
            /// output was not found, a link is in the way, or a current file's time could not be brought up to date.
            /// </summary>
            public bool AgeCheckSafe;
        }

        /// <summary>
        /// Applies <see cref="Classify"/> to the three files. Only the exact Unity location is looked at, nothing behind a
        /// link is touched, and a file is deleted only when <paramref name="mayDelete"/> (Bugsee is handling the map for this
        /// build). Does nothing when this build's own output is not found. Each file is handled on its own: one that cannot
        /// be touched does not stop the others.
        /// </summary>
        public static Result Run(string exportRoot, string buildSymbols, DateTime exportStartUtc, bool mayDelete)
        {
            var result = new Result();
            if (string.IsNullOrEmpty(exportRoot) || !BuildOutputMatches(exportRoot, buildSymbols))
            {
                return result;
            }

            string dir = ExportSymbolsDirectory(exportRoot);
            if (!Directory.Exists(dir))
            {
                result.AgeCheckSafe = true;
                return result;
            }

            if (HasLinkBetween(exportRoot, dir))
            {
                return result;
            }

            result.AgeCheckSafe = true;
            foreach (string name in Names)
            {
                string file = Path.Combine(dir, name);
                Verdict verdict = Verdict.Absent;
                try
                {
                    if (!File.Exists(file)) continue;
                    if (IsLink(file))
                    {
                        result.AgeCheckSafe = false;
                        continue;
                    }

                    string source = Path.Combine(buildSymbols, name);
                    bool sourceExists = File.Exists(source);
                    verdict = Classify(true, File.GetLastWriteTimeUtc(file), exportStartUtc, sourceExists,
                        sourceExists && SameContent(file, source));
                    if (verdict == Verdict.Touch)
                    {
                        File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
                        result.Actions.Add("touched " + file);
                    }
                    else if (verdict == Verdict.Stale && mayDelete)
                    {
                        File.Delete(file);
                        result.Actions.Add("deleted " + file);
                    }
                    else if (verdict == Verdict.Stale)
                    {
                        result.Actions.Add("left stale " + file);
                    }
                }
                catch (Exception ex)
                {
                    // A current file whose time could not be set would be skipped by the age check.
                    if (verdict == Verdict.Touch) result.AgeCheckSafe = false;
                    result.Actions.Add("failed (" + ex.Message + ") on " + file);
                }
            }

            return result;
        }

        // A link on any folder from the export's root down to the Symbols folder could send a delete outside the export.
        static bool HasLinkBetween(string exportRoot, string dir)
        {
            string root = Path.GetFullPath(exportRoot).TrimEnd('/', '\\');
            string current = Path.GetFullPath(dir).TrimEnd('/', '\\');
            while (current.Length > root.Length && current.StartsWith(root, StringComparison.Ordinal))
            {
                if (IsLink(current)) return true;
                current = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(current)) return true;
            }
            return false;
        }

        static bool IsLink(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch (Exception) { return true; }
        }

        static bool SameContent(string a, string b)
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (fa.Length != fb.Length) return false;
            using (var sa = fa.OpenRead())
            using (var sb = fb.OpenRead())
            {
                var ba = new byte[81920];
                var bb = new byte[81920];
                while (true)
                {
                    int ra = ReadFull(sa, ba);
                    int rb = ReadFull(sb, bb);
                    if (ra != rb) return false;
                    if (ra == 0) return true;
                    for (int i = 0; i < ra; i++) if (ba[i] != bb[i]) return false;
                }
            }
        }

        static int ReadFull(Stream s, byte[] buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int n = s.Read(buffer, total, buffer.Length - total);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }
    }
}
