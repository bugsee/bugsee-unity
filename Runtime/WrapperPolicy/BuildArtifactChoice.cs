using System;
using System.IO;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Decides which of several build artifacts in one folder (the symbols zip, the R8 mapping, the IL2CPP line
    /// map folder) belong to the build that just finished.
    ///
    /// Modification time cannot decide it: an incremental build copies cached outputs and keeps their old
    /// timestamps, so another configuration's file can look newer than this build's own. A name prefix cannot
    /// either: "Game" is a prefix of "Game2", "Game-min" and "Game-2024". So a file counts as this build's only if
    /// it has exactly the name Unity gives it - "&lt;player&gt;_mapping.txt" and
    /// "&lt;player&gt;-&lt;bundleVersion&gt;-v&lt;versionCode&gt;-&lt;backend&gt;.symbols.zip" - and nothing falls
    /// back to "newest".
    ///
    /// Generic names (Gradle's own mapping.txt / native-debug-symbols.zip) are accepted only when the output is an
    /// exported Gradle project, where Gradle names things itself.
    /// </summary>
    public static class BuildArtifactChoice
    {
        public const int Reject = 0;
        public const int Generic = 1;
        public const int Named = 2;

        public static int MappingRank(string path, string stem, bool allowGeneric)
        {
            var name = Path.GetFileName(path ?? "");
            if (name.Length == 0) return Reject;
            if (!string.IsNullOrEmpty(stem) && Eq(name, stem + "_mapping.txt")) return Named;
            return allowGeneric && Eq(name, "mapping.txt") ? Generic : Reject;
        }

        /// <summary>
        /// <paramref name="backend"/> is this build's scripting backend as Unity spells it in the name
        /// (<c>ScriptingImplementation</c>: "IL2CPP" / "Mono2x"). It is part of the exact name, so the other
        /// backend's zip left in a reused output folder - same player, version and code - is never this build's.
        /// </summary>
        public static int SymbolsZipRank(string path, string stem, bool allowGeneric, string bundleVersion, int versionCode, string backend)
        {
            var name = Path.GetFileName(path ?? "");
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return Reject;
            if (!string.IsNullOrEmpty(stem) && !string.IsNullOrEmpty(backend)
                && Eq(name, SymbolsZipName(stem, bundleVersion, versionCode, backend)))
            {
                return Named;
            }

            return allowGeneric && name.StartsWith("native-debug", StringComparison.OrdinalIgnoreCase) ? Generic : Reject;
        }

        /// <summary>Unity's Android symbols zip name, "{0}-{1}-v{2}-{3}.symbols.zip" (UnityEditor.Android.Extensions).</summary>
        public static string SymbolsZipName(string stem, string bundleVersion, int versionCode, string backend) =>
            stem + "-" + bundleVersion + "-v" + versionCode + "-" + backend + ".symbols.zip";

        /// <summary>
        /// The settings this build's file names are made from. Unity names the symbols zip from Player Settings as
        /// they are during the build, but a postprocess callback that runs before ours (a version-code incrementor)
        /// can change them before we look. So the values recorded at the end of preprocess for this same output are
        /// used, and the live ones only when none were recorded for it (a call outside a build).
        /// </summary>
        public static PlayerNameSettings BuildTimeSettings(PlayerNameSettings recorded, string outputPath, PlayerNameSettings live) =>
            recorded != null && SameOutput(recorded.OutputPath, outputPath) ? recorded : live;

        static bool SameOutput(string a, string b) =>
            !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
            && string.Equals(a.TrimEnd('/', '\\'), b.TrimEnd('/', '\\'), StringComparison.Ordinal);

        /// <summary>Unity's folder for the IL2CPP output of a player: "&lt;player&gt;_BackUpThisFolder_ButDontShipItWithYourGame".</summary>
        public static string Il2CppBackupFolderName(string stem) => stem + "_BackUpThisFolder_ButDontShipItWithYourGame";

        /// <summary>
        /// The one folder that holds this build's IL2CPP output (line map, unstripped native library): an exported
        /// project (Xcode, Gradle) is that folder; a single APK/AAB's is the "&lt;player&gt;_BackUpThisFolder..."
        /// folder Unity creates beside it. Never the parent, where sibling builds live: an iOS export beside an
        /// Android Gradle export would otherwise take the Android libil2cpp.so or line map as its own.
        /// Null when there is no output path.
        /// </summary>
        public static string Il2CppOutputRoot(string outputPath, bool outputIsDirectory)
        {
            if (string.IsNullOrEmpty(outputPath)) return null;
            if (outputIsDirectory) return outputPath;
            var parent = Path.GetDirectoryName(outputPath);
            var stem = Path.GetFileNameWithoutExtension(outputPath);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(stem)) return null;
            return Path.Combine(parent, Il2CppBackupFolderName(stem));
        }

        /// <summary>
        /// Whole seconds since the Unix epoch of a UTC time (rounded down), the unit the iOS upload script compares file
        /// modification times in.
        /// </summary>
        public static long UnixSeconds(DateTime utc)
        {
            // BuildSummary.buildStartedAt holds UTC with Kind Unspecified: ToUniversalTime would read it as local time and
            // shift it by the machine's offset (hours early east of Greenwich, late west of it).
            DateTime value = utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            return (long)Math.Floor((value - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds);
        }

        /// <summary>
        /// True when a file in an exported project was written by the export that began at <paramref name="exportStartUtc"/>
        /// (not before it, to the second). An "Append" export keeps the project's earlier files, and Unity does not delete the
        /// line map of an earlier build when this one wrote none.
        /// </summary>
        public static bool WrittenByExport(DateTime fileWriteUtc, DateTime exportStartUtc) =>
            UnixSeconds(fileWriteUtc) >= UnixSeconds(exportStartUtc);

        /// <summary>Higher rank wins; modification time only breaks a tie between equally ranked files.</summary>
        public static bool Better(int rank, DateTime mtime, int bestRank, DateTime bestMtime) =>
            rank != bestRank ? rank > bestRank : mtime >= bestMtime;

        /// <summary>libil2cpp.so, and the debug-file spellings Unity and the CLI use for it.</summary>
        public static bool IsIl2CppLibraryName(string name) =>
            !string.IsNullOrEmpty(name)
            && (name == "libil2cpp.so" || name == "libil2cpp.sym.so" || name == "libil2cpp.dbg.so"
                || name == "libil2cpp.so.sym" || name == "libil2cpp.so.dbg" || name == "libil2cpp.so.debug");

        /// <summary>True for the ELF magic: 0x7f 'E' 'L' 'F'.</summary>
        public static bool LooksLikeElf(byte[] data) =>
            data != null && data.Length >= 4 && data[0] == 0x7f && data[1] == (byte)'E' && data[2] == (byte)'L' && data[3] == (byte)'F';

        static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Player Settings that Unity puts in an Android player's symbols zip name, for one output path.</summary>
    public sealed class PlayerNameSettings
    {
        public PlayerNameSettings(string outputPath, string bundleVersion, int versionCode, string backend)
        {
            OutputPath = outputPath;
            BundleVersion = bundleVersion;
            VersionCode = versionCode;
            Backend = backend;
        }

        public string OutputPath { get; }
        public string BundleVersion { get; }
        public int VersionCode { get; }
        public string Backend { get; }
    }
}
