using System;
using System.Text.RegularExpressions;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Which native instruction pointers may be attached to the frames of a managed stack trace. Pure logic (no
    /// Unity types) so it can be unit-tested.
    ///
    /// IL2CPP fills <c>Exception.trace_ips</c> (what <c>Exception.StackTrace</c> prints) and
    /// <c>native_trace_ips</c> (what <c>il2cpp_native_stack_trace</c> returns) in one loop over the same native
    /// stack walk (<c>vm/Exception.cpp</c> <c>PrepareExceptionForThrow</c>), newest frame first, so they pair
    /// one-to-one for the throw that produced them. The printed text can drift from that: frames hidden from
    /// the text, <c>--- End of stack trace from previous location ---</c> segments (rethrow through
    /// <c>ExceptionDispatchInfo</c>, async/await) and frames of inner exceptions all change the line count. This
    /// policy pairs only when the counts prove nothing drifted, and otherwise sends no address at all, because a
    /// wrong address makes the worker map a plausible but wrong C# line.
    ///
    /// Address contract: every address sent is a RETURN address, hex, MODULE-RELATIVE (the instruction pointer
    /// minus the load address of the image that holds IL2CPP code: UnityFramework on iOS, libil2cpp.so on Android;
    /// <c>vm/StackTrace.cpp</c> subtracts <c>os::Image::GetImageBase()</c>), 32-bit values zero-extended. It is
    /// resolved against the <c>moduleUUID</c> sent with the exception (the UUID of that same image).
    /// </summary>
    public static class ManagedAddressPairing
    {
        static readonly Regex MonoMethodStart = new Regex(
            @"<0x[0-9a-fA-F]+\s*\+\s*0x[0-9a-fA-F]+>",
            RegexOptions.Compiled);

        // "... in /path/File.cs:123": a real location. Release IL2CPP prints "in <32 hex>:0".
        static readonly Regex SourceLocation = new Regex(
            @"\sin\s+(?<file>.+):(?<line>\d+)\s*$",
            RegexOptions.Compiled);

        static readonly Regex AssemblyHash = new Regex(
            @"^<[0-9a-fA-F]{32}>$",
            RegexOptions.Compiled);

        /// <summary>A line of the stack text that separates segments instead of being a frame.</summary>
        public static bool IsSeparator(string line)
        {
            return line != null && line.StartsWith("---", StringComparison.Ordinal);
        }

        /// <summary>
        /// Removes Mono's <c>&lt;0xSTART + 0xOFFSET&gt;</c>: START is a method start and absolute, not a return
        /// address, so it must never be read as one.
        /// </summary>
        public static string StripMonoMethodStarts(string line)
        {
            return string.IsNullOrEmpty(line) ? line : MonoMethodStart.Replace(line, " ");
        }

        /// <summary>True when the line carries a real source file and line (a line above 0, not an assembly hash).</summary>
        public static bool HasSourceLocation(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            var m = SourceLocation.Match(line);
            if (!m.Success) return false;
            int number;
            if (!int.TryParse(m.Groups["line"].Value, out number) || number <= 0) return false;
            return !AssemblyHash.IsMatch(m.Groups["file"].Value.Trim());
        }

        public static string FormatAddress(ulong moduleRelative)
        {
            return "0x" + moduleRelative.ToString("x");
        }

        /// <summary>
        /// The address for each frame line (separators are not frames and are not in the result), null where none
        /// may be sent. <paramref name="stackLines"/> are the trimmed, non-empty lines of
        /// <c>Exception.StackTrace</c> in order; <paramref name="nativeIps"/> are the module-relative values of
        /// <c>il2cpp_native_stack_trace</c> in order (newest first, like the text).
        /// </summary>
        public static string[] Pair(string[] stackLines, ulong[] nativeIps)
        {
            var frameLines = new System.Collections.Generic.List<string>();
            var separated = false;
            if (stackLines != null)
            {
                foreach (var line in stackLines)
                {
                    if (IsSeparator(line)) separated = true;
                    else frameLines.Add(line);
                }
            }

            var result = new string[frameLines.Count];
            if (nativeIps == null || nativeIps.Length == 0 || separated || nativeIps.Length != frameLines.Count)
            {
                return result;
            }

            for (var i = 0; i < result.Length; i++)
            {
                // A frame that already has a C# location (IL2CPP with a usym file) was expanded from the symbol file
                // and its raw_ip is a line-table address, not a return address: the worker's `- 1` would be wrong.
                if (nativeIps[i] == 0 || HasSourceLocation(frameLines[i])) continue;
                result[i] = FormatAddress(nativeIps[i]);
            }
            return result;
        }
    }
}
