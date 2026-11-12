using System;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Android builds on the Mono scripting backend lose the native frames inside the Mono runtime
    /// (libmonobdwgc-2.0.so, libmono-native.so, libMonoPosixHelper.so): Unity 2021.3 has no Mono ARM64 (ARMv7
    /// only) and those libraries carry no GNU build-id, so their frames cannot be matched to symbols. libunity.so
    /// and libmain.so do have build-ids, so frames in them still symbolicate. IL2CPP is the supported configuration
    /// for fully symbolicated native crashes. The Editor warns once per domain load; it never fails the build.
    /// The managed stack text has the same split: a Development build ships a portable .pdb next to each assembly
    /// in assets/bin/Data/Managed and the Mono runtime prints <c>[0x00001] in /path/File.cs:713</c>; a Release build
    /// ships none and prints <c>[0x0000b] in &lt;module-guid&gt;:0</c> (an IL offset, no file or line). The backend turns
    /// that offset into a line from the Portable PDBs the Editor uploads after the build (<see cref="MonoPortablePdb"/>,
    /// bugsee-cli 0.9.0+), and warns when it cannot upload them.
    /// </summary>
    public static class AndroidMonoSymbolPolicy
    {
        public const string Warning =
            "Bugsee: this Android build uses the Mono scripting backend. Native frames inside the Mono runtime " +
            "(libmonobdwgc-2.0.so, libmono-native.so, libMonoPosixHelper.so) cannot be symbolicated: Unity 2021.3 " +
            "has no Mono ARM64 and those libraries have no GNU build-id. Use the IL2CPP scripting backend for " +
            "fully symbolicated native crashes. The build continues.";

        /// <summary>The value of <c>ScriptingImplementation.Mono2x</c> (or any name starting with "Mono").</summary>
        public static bool IsMono(string scriptingBackend)
        {
            return scriptingBackend != null
                && scriptingBackend.StartsWith("Mono", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns the warning text the first time it is called with a Mono backend, then null. The caller keeps
        /// <paramref name="alreadyWarned"/> for the session.
        /// </summary>
        public static string TakeWarning(string scriptingBackend, ref bool alreadyWarned)
        {
            if (alreadyWarned || !IsMono(scriptingBackend)) return null;
            alreadyWarned = true;
            return Warning;
        }
    }
}
