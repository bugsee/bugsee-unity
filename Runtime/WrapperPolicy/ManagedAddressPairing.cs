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
    /// the text and segments of earlier throws (rethrow through <c>ExceptionDispatchInfo</c>, async/await)
    /// all change the line count. The text is: earlier segments first, then the live segment (the frames of the
    /// current <c>trace_ips</c>, newest first). A <c>--- End of stack trace ...</c> separator between segments is
    /// NOT reliable: it is skipped when nothing was printed before it or the last printed frame was an async
    /// state machine, so segments can be concatenated silently.
    ///
    /// Rule: only the live segment is paired, anchored at the BOTTOM (oldest text frame with the oldest native IP).
    /// Hidden frames (<c>[StackTraceHidden]</c> throw helpers, awaiter <c>GetResult</c>/<c>ThrowForNonSuccess</c>,
    /// <c>ExceptionDispatchInfo.Throw</c>) are native frames with no text and sit at the top of the live segment,
    /// so the bottom anchor is right exactly when the counts differ. The live segment starts after the last
    /// separator and after the last async state machine <c>MoveNext</c> line that has frames below it (a
    /// possible silent boundary); lines above that cannot be proven to belong to it and get no address. Anything
    /// else (no native IPs, a live segment longer than the IP list) sends no address, because a wrong address
    /// makes the worker map a plausible but wrong C# line; the worker's per-frame method-name check is only the
    /// backstop.
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

        // "... in /path/File.cs:123" (a usym entry), or "in <32 hex>:123" (usym entry without a file name).
        // Release IL2CPP without a usym file prints "in <32 hex>:0".
        static readonly Regex SourceLocation = new Regex(
            @"\sin\s+(?<file>.+):(?<line>\d+)\s*$",
            RegexOptions.Compiled);

        // "Ns.Type.<Foo>d__3.MoveNext ()": an async state machine, after which a silent segment boundary may be.
        static readonly Regex StateMachineMoveNext = new Regex(
            @"d__\d+[^\s(]*\.MoveNext\b",
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

        /// <summary>True when the line carries a source line above 0, which only a symbol-file (usym) entry has.</summary>
        public static bool HasSourceLocation(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            var m = SourceLocation.Match(line);
            if (!m.Success) return false;
            int number;
            return int.TryParse(m.Groups["line"].Value, out number) && number > 0;
        }

        public static bool IsStateMachineMoveNext(string line)
        {
            return !string.IsNullOrEmpty(line) && StateMachineMoveNext.IsMatch(line);
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
            var liveStart = 0;
            if (stackLines != null)
            {
                foreach (var line in stackLines)
                {
                    if (IsSeparator(line)) liveStart = frameLines.Count;
                    else frameLines.Add(line);
                }
            }

            var result = new string[frameLines.Count];
            if (nativeIps == null || nativeIps.Length == 0 || frameLines.Count == 0)
            {
                return result;
            }

            // A state machine line with frames below it may be the end of an earlier, silently joined segment.
            for (var i = liveStart; i < frameLines.Count - 1; i++)
            {
                if (IsStateMachineMoveNext(frameLines[i])) liveStart = i + 1;
            }

            var live = frameLines.Count - liveStart;
            if (live > nativeIps.Length)
            {
                return result;
            }

            var offset = nativeIps.Length - live;
            for (var j = 0; j < live; j++)
            {
                var ip = nativeIps[offset + j];
                var line = frameLines[liveStart + j];
                // A frame that already has a C# location (IL2CPP with a usym file) was expanded from the symbol file
                // and its raw_ip is a line-table address, not a return address: the worker's `- 1` would be wrong.
                if (ip == 0 || HasSourceLocation(line)) continue;
                result[liveStart + j] = FormatAddress(ip);
            }
            return result;
        }
    }
}
