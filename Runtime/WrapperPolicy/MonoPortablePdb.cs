using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Bugsee.WrapperPolicy
{
    /// <summary>One folder of Unity's Bee output (<c>Library/Bee/artifacts/*.dag</c>) and when its newest assembly with a pdb was written.</summary>
    public sealed class CompilerOutputDirectory
    {
        public string Name;
        public string Path;
        /// <summary>Newest write time of an assembly in it that has a .pdb beside it, or null when there is none.</summary>
        public DateTime? AssemblyWriteUtc;
        /// <summary>The folder was compiled for Android: a compiler response file in it defines <c>UNITY_ANDROID</c> (see <see cref="DefinesAndroid"/>).</summary>
        public bool TargetsAndroid;
    }

    /// <summary>
    /// Release builds on the Mono scripting backend ship no .pdb, so the runtime prints a managed frame as
    /// <c>at Ns.C.M (System.String a) [0x0000b] in &lt;module-guid&gt;:0</c>. <c>bugsee-cli</c> 0.9.0
    /// (<c>debug-files upload --type portable-pdb</c>) takes the compiler's assemblies, each with its portable .pdb beside it,
    /// converts every one to Bugsee's BMF mapping (<c>&lt;module id&gt;.bmf</c>, the output of <c>debug-files convert --to bmf</c>) and
    /// uploads the BMFs. <c>--shipped-dir</c> names the assemblies the app ships: the BMF is registered under their module id (the one
    /// the runtime prints) and methods the linker removed or rewrote are left out. The worker resolves the frame to file:line from the
    /// BMF; the .pdb files themselves are never uploaded. This is the decision and the arguments of that call; the Editor class only
    /// finds the folders and runs the CLI.
    /// </summary>
    public static class MonoPortablePdb
    {
        /// <summary>The first <c>bugsee-cli</c> with <c>--type portable-pdb</c> (0.8.x rejects it).</summary>
        public const string MinCliVersion = "0.9.0";

        /// <summary>Why nothing is uploaded: the answer of <see cref="Decide"/>.</summary>
        public enum Outcome
        {
            /// <summary>Not a Release Mono Android build: IL2CPP has its own line map, a Development build ships its .pdb files.</summary>
            NotApplicable,
            /// <summary>Upload.</summary>
            Upload,
            /// <summary>BUGSEE_NO_SYMBOL_UPLOAD is set.</summary>
            OptedOut,
            /// <summary>No BUGSEE_APP_TOKEN in the Editor's environment.</summary>
            NoToken,
        }

        public static Outcome Decide(string scriptingBackend, bool development, bool noSymbolUploadSet, bool hasAppToken)
        {
            if (!AndroidMonoSymbolPolicy.IsMono(scriptingBackend) || development)
            {
                return Outcome.NotApplicable;
            }

            if (noSymbolUploadSet)
            {
                return Outcome.OptedOut;
            }

            return hasAppToken ? Outcome.Upload : Outcome.NoToken;
        }

        /// <summary>The line for a Release Mono build that is not uploading (once per domain load, like the native warning).</summary>
        public static string NotUploadedNotice(Outcome outcome)
        {
            string reason;
            switch (outcome)
            {
                case Outcome.OptedOut: reason = "BUGSEE_NO_SYMBOL_UPLOAD is set"; break;
                case Outcome.NoToken: reason = "BUGSEE_APP_TOKEN is not set in the Unity Editor's environment"; break;
                default: return null;
            }

            return "Bugsee: this Android build uses the Mono scripting backend without Development Build, so the player ships no .pdb " +
                   "and managed exception frames carry only IL offsets (\"[0x0000b] in <module-guid>:0\"). The BMF mapping (made from the " +
                   "portable .pdb files) that lets the backend show C# file:line is not uploaded because " + reason + ". Upload it from CI with bugsee-cli " + MinCliVersion +
                   " or newer (the same command with convert --to bmf --output <dir> instead of upload makes the files locally, to look at): " +
                   "debug-files upload <Library/Bee/artifacts/...P.dag> --type portable-pdb --shipped-dir " +
                   "<Library/Bee/artifacts/Android/ManagedStripped> --version <version> --build <build>. The build continues.";
        }

        /// <summary>Why the installed CLI cannot do the conversion and upload, or null when it can (or its version is unknown: then it is tried).</summary>
        public static string CliTooOld(string cliVersion, string cliPath = null)
        {
            if (string.IsNullOrEmpty(cliVersion) || BugseeCliPolicy.VersionAtLeast(cliVersion, MinCliVersion))
            {
                return null;
            }

            return "bugsee-cli " + cliVersion + (string.IsNullOrEmpty(cliPath) ? "" : " (" + cliPath + ")") + " cannot convert and upload the Mono mapping (--type portable-pdb needs " + MinCliVersion +
                   " or newer): managed frames of this Release Mono build stay IL offsets. Replace it, or point " +
                   "BUGSEE_CLI_PATH at " + MinCliVersion + "+ (the package downloads its pinned release only when it finds no CLI of " + BugseeCliPolicy.MinVersion + " or newer).";
        }

        /// <summary>Whether the text of a compiler response file (<c>Assembly-CSharp.rsp</c>) defines <c>UNITY_ANDROID</c> and not <c>UNITY_IOS</c>.</summary>
        public static bool DefinesAndroid(string responseFileText)
        {
            if (string.IsNullOrEmpty(responseFileText))
            {
                return false;
            }

            bool android = false;
            foreach (var raw in responseFileText.Split('\n'))
            {
                var line = raw.Trim();
                if (line == "-define:UNITY_ANDROID") android = true;
                else if (line == "-define:UNITY_IOS" || line == "-define:UNITY_TVOS") return false;
            }

            return android;
        }

        static readonly Regex PlayerDag = new Regex("^[0-9a-fA-F]+P\\.dag$", RegexOptions.CultureInvariant);

        /// <summary>
        /// The Android Release player's compiler output among the Bee folders. Folder names are "&lt;prefix&gt;P.dag" for a Release
        /// player, "&lt;prefix&gt;PDevDbg.dag" for a Development player and "&lt;prefix&gt;E.dag" for the Editor; the Editor's compile has
        /// Editor-only code whose module ids the app never has. The prefix differs by build target (the same project holds an
        /// Android and an iOS folder once both were built), so a folder counts only when it was compiled for Android
        /// (<see cref="CompilerOutputDirectory.TargetsAndroid"/>). Of several, the one written last, but not one written after the newest shipped assembly (the linker runs after the compile, so a folder newer than the shipped one was not what this build linked; Bee leaves a folder it did not need to recompile untouched, so the newest is not always the current one). Null when there is none.
        /// </summary>
        public static CompilerOutputDirectory PickCompilerOutput(IEnumerable<CompilerOutputDirectory> candidates, DateTime? shippedNewestUtc = null)
        {
            var picked = PickNewest(candidates, shippedNewestUtc);
            // Nothing consistent with the shipped folder: the newest one, as the shipped folder may be the stale side.
            return picked ?? (shippedNewestUtc == null ? null : PickNewest(candidates, null));
        }

        /// <summary>
        /// The names of the Android Release folders that could be the build's compile (all of them: the times cannot tell which one Bee
        /// reused). More than one means the choice is a guess, which the Editor says in a warning.
        /// </summary>
        public static List<string> AndroidReleaseFolders(IEnumerable<CompilerOutputDirectory> candidates)
        {
            var names = new List<string>();
            if (candidates != null)
            {
                foreach (var candidate in candidates)
                {
                    if (candidate != null && candidate.Name != null && candidate.TargetsAndroid && candidate.AssemblyWriteUtc != null
                        && PlayerDag.IsMatch(candidate.Name))
                    {
                        names.Add(candidate.Name);
                    }
                }
            }

            names.Sort(StringComparer.Ordinal);
            return names;
        }

        static CompilerOutputDirectory PickNewest(IEnumerable<CompilerOutputDirectory> candidates, DateTime? shippedNewestUtc)
        {
            CompilerOutputDirectory best = null;
            if (candidates == null)
            {
                return null;
            }

            foreach (var candidate in candidates)
            {
                if (candidate == null || candidate.Name == null || !PlayerDag.IsMatch(candidate.Name) || !candidate.TargetsAndroid || candidate.AssemblyWriteUtc == null
                    || (shippedNewestUtc != null && candidate.AssemblyWriteUtc.Value > shippedNewestUtc.Value))
                {
                    continue;
                }

                if (best == null
                    || candidate.AssemblyWriteUtc.Value > best.AssemblyWriteUtc.Value
                    || (candidate.AssemblyWriteUtc.Value == best.AssemblyWriteUtc.Value
                        && string.CompareOrdinal(candidate.Name, best.Name) > 0))
                {
                    best = candidate;
                }
            }

            return best;
        }

        /// <summary>
        /// Whether the shipped folder (newest assembly <paramref name="shippedNewestUtc"/>) can be the output of the linker run that
        /// followed the compile of <paramref name="compiled"/>: the linker runs after the compile, so a shipped folder older than the
        /// compiler folder is left over from an earlier build (for instance when the linker did not run).
        /// </summary>
        public static bool ShippedMatchesCompile(DateTime? shippedNewestUtc, DateTime? compiledUtc) =>
            shippedNewestUtc != null && (compiledUtc == null || shippedNewestUtc.Value >= compiledUtc.Value);

        /// <summary>
        /// <c>debug-files upload &lt;compiler output&gt; --type portable-pdb</c>. <paramref name="shippedDir"/> (the folder of the
        /// assemblies the app is made from, which the linker may have rewritten under a new module id) is passed when it exists;
        /// without it the compiler's copies are taken to be what ships. No <c>--uuid</c>: the CLI rejects it for this type.
        /// </summary>
        public static List<string> Arguments(string compilerOutput, string shippedDir, string version, string build)
        {
            var args = new List<string>
            {
                "debug-files", "upload", compilerOutput,
                "--type", "portable-pdb",
                "--version", version,
                "--build", build,
            };
            if (!string.IsNullOrEmpty(shippedDir))
            {
                args.Add("--shipped-dir");
                args.Add(shippedDir);
            }

            return args;
        }
    }
}
