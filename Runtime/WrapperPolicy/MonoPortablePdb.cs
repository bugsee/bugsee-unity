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
    /// <c>at Ns.C.M (System.String a) [0x0000b] in &lt;module-guid&gt;:0</c>. <c>bugsee-cli</c> 0.9.0 can pair the compiler's
    /// assemblies and portable .pdb files with the assemblies the app ships (<c>--shipped-dir</c>) and upload a mapping keyed
    /// by the shipped module id (<c>debug-files upload --type portable-pdb</c>); the worker turns the frame into file:line from it.
    /// This is the decision and the arguments of that upload; the Editor class only finds the folders and runs the CLI.
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
                   "and managed exception frames carry only IL offsets (\"[0x0000b] in <module-guid>:0\"). The Portable PDBs that let the " +
                   "backend show C# file:line are not uploaded because " + reason + ". Upload them from CI with bugsee-cli " + MinCliVersion +
                   " or newer: debug-files upload <Library/Bee/artifacts/...P.dag> --type portable-pdb --shipped-dir " +
                   "<Library/Bee/artifacts/Android/ManagedStripped> --version <version> --build <build>. The build continues.";
        }

        /// <summary>Why the installed CLI cannot do the upload, or null when it can (or its version is unknown: then it is tried).</summary>
        public static string CliTooOld(string cliVersion)
        {
            if (string.IsNullOrEmpty(cliVersion) || BugseeCliPolicy.VersionAtLeast(cliVersion, MinCliVersion))
            {
                return null;
            }

            return "bugsee-cli " + cliVersion + " cannot upload Portable PDBs (--type portable-pdb needs " + MinCliVersion +
                   " or newer): managed frames of this Release Mono build stay IL offsets. Remove it from the search path (or point " +
                   "BUGSEE_CLI_PATH at " + MinCliVersion + "+); without one on the path the package downloads its pinned release (not BUGSEE_CLI_VERSION below " + MinCliVersion + ").";
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
        /// (<see cref="CompilerOutputDirectory.TargetsAndroid"/>). Of several, the one written last. Null when there is none.
        /// </summary>
        public static CompilerOutputDirectory PickCompilerOutput(IEnumerable<CompilerOutputDirectory> candidates)
        {
            CompilerOutputDirectory best = null;
            if (candidates == null)
            {
                return null;
            }

            foreach (var candidate in candidates)
            {
                if (candidate == null || candidate.Name == null || !PlayerDag.IsMatch(candidate.Name) || !candidate.TargetsAndroid || candidate.AssemblyWriteUtc == null)
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
