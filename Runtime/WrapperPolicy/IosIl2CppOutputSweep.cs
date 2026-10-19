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
    /// holds), and one that differs from it, or that this build did not write at all and is older than the export, is
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
        /// True when this build's own output (the il2cppOutput/cpp folder; its Symbols folder is absent when the build emitted no
        /// map) is where <see cref="BuildSymbolsDirectory"/> says. When it is not (another Unity layout) nothing can be proven
        /// about the exported files, and the Run Script must not apply its age check either: a valid map that an unchanged
        /// Append export did not copy again would be skipped.
        /// </summary>
        public static bool BuildOutputFound(string buildSymbols)
        {
            string cpp = string.IsNullOrEmpty(buildSymbols) ? null : Path.GetDirectoryName(buildSymbols);
            return !string.IsNullOrEmpty(cpp) && Directory.Exists(cpp);
        }

        public static Verdict Classify(bool exists, DateTime fileWriteUtc, DateTime exportStartUtc, bool sourceExists, bool sameAsSource)
        {
            if (!exists) return Verdict.Absent;
            bool written = BuildArtifactChoice.WrittenByExport(fileWriteUtc, exportStartUtc);
            if (sourceExists && sameAsSource) return written ? Verdict.Current : Verdict.Touch;
            return written ? Verdict.Current : Verdict.Stale;
        }

        /// <summary>
        /// Applies <see cref="Classify"/> to the three files. Only the exact Unity location is looked at, nothing behind a
        /// link is touched, and a file is deleted only when <paramref name="mayDelete"/> (Bugsee is handling the map for this
        /// build). Does nothing when the build's own folder is not there (a Unity layout this does not know). Returns what it did.
        /// </summary>
        public static List<string> Run(string exportRoot, string buildSymbols, DateTime exportStartUtc, bool mayDelete)
        {
            var actions = new List<string>();
            string dir = string.IsNullOrEmpty(exportRoot) ? null : ExportSymbolsDirectory(exportRoot);
            // Unity removes the build's Symbols folder when a build emits no map, so a missing folder is an answer, provided
            // its parent (il2cppOutput/cpp) is there: without that this build's output is not where this looks.
            if (dir == null || !Directory.Exists(dir) || IsLink(dir) || !BuildOutputFound(buildSymbols))
            {
                return actions;
            }

            foreach (string name in Names)
            {
                string file = Path.Combine(dir, name);
                if (!File.Exists(file) || IsLink(file)) continue;
                string source = Path.Combine(buildSymbols, name);
                bool sourceExists = File.Exists(source);
                Verdict verdict = Classify(true, File.GetLastWriteTimeUtc(file), exportStartUtc, sourceExists,
                    sourceExists && SameContent(file, source));
                if (verdict == Verdict.Touch)
                {
                    File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
                    actions.Add("touched " + file);
                }
                else if (verdict == Verdict.Stale && mayDelete)
                {
                    File.Delete(file);
                    actions.Add("deleted " + file);
                }
                else if (verdict == Verdict.Stale)
                {
                    actions.Add("left stale " + file);
                }
            }

            return actions;
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
