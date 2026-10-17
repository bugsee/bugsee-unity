using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Bugsee.WrapperPolicy
{
    /// <summary>The frame texts of one exception and the address (or null) sent with each.</summary>
    public sealed class ManagedFrameSet
    {
        public string[] Traces = new string[0];
        public string[] Addresses = new string[0];
    }

    /// <summary>
    /// Turns stack text (and native IPs) into the frames of the UnityManagedException payload. Pure logic (no
    /// Unity types) so the whole assembly can be unit-tested; <c>ManagedExceptionPayload</c> only wraps it.
    /// </summary>
    public static class ManagedFrameAssembly
    {
        // Prefer native-looking PCs (8+ hex digits). Avoid Mono/IL "[0x00023]" IL offsets.
        static readonly Regex NativePcRegex = new Regex(
            @"(?:(?:^|[^\w./])(?:pc\s+)?(0x[0-9a-fA-F]{8,16})(?![0-9a-fA-F]))|(?:#\d+\s+pc\s+([0-9a-fA-F]{8,16})\b)",
            RegexOptions.Compiled);

        static readonly Regex IlOffsetBracketRegex = new Regex(
            @"\[0x[0-9a-fA-F]{1,6}\]",
            RegexOptions.Compiled);

        /// <summary>Trimmed non-empty lines of a stack trace text, separators included.</summary>
        public static string[] StackLines(string stackTrace)
        {
            if (string.IsNullOrEmpty(stackTrace))
            {
                return new string[0];
            }
            var lines = stackTrace.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var list = new List<string>(lines.Length);
            foreach (var line in lines)
            {
                var t = line.Trim();
                if (t.Length > 0) list.Add(t);
            }
            return list.ToArray();
        }

        /// <summary>The frame lines of a stack trace text: no segment separators ("--- End of stack trace ...", "--->").</summary>
        public static string[] FrameLines(string[] stackLines)
        {
            var list = new List<string>(stackLines.Length);
            foreach (var t in stackLines)
            {
                if (!ManagedAddressPairing.IsSeparator(t)) list.Add(t);
            }
            return list.ToArray();
        }

        /// <summary>
        /// Frames of a thrown exception. The only addresses are the paired native IPs
        /// (<see cref="ManagedAddressPairing.Pair"/>); nothing is read out of the text, so no absolute or Mono
        /// address can leak through. Empty when the text has no frames.
        /// </summary>
        public static ManagedFrameSet FromStackTrace(string stackTrace, ulong[] nativeIps)
        {
            var lines = StackLines(stackTrace);
            var set = new ManagedFrameSet { Traces = FrameLines(lines) };
            set.Addresses = nativeIps != null && nativeIps.Length > 0
                ? ManagedAddressPairing.Pair(lines, nativeIps)
                : new string[set.Traces.Length];
            return set;
        }

        /// <summary>
        /// Frames from <c>Exception.ToString()</c> (used when <c>StackTrace</c> is empty): skips the
        /// <c>Type: message</c> header and inner-exception banners. Inner exceptions' frames may be mixed in, so no
        /// address is ever paired with them.
        /// </summary>
        public static ManagedFrameSet FromToString(string toString)
        {
            var list = new List<string>();
            if (!string.IsNullOrEmpty(toString))
            {
                var skippedHeader = false;
                foreach (var line in toString.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var t = line.Trim();
                    if (t.Length == 0) continue;
                    if (ManagedAddressPairing.IsSeparator(t)) continue;
                    // First non-empty line is usually "Type: message".
                    if (!skippedHeader)
                    {
                        skippedHeader = true;
                        if (t.IndexOf(':') > 0 && !t.StartsWith("at ", StringComparison.Ordinal)) continue;
                    }
                    list.Add(t);
                }
            }
            var traces = list.ToArray();
            return new ManagedFrameSet { Traces = traces, Addresses = new string[traces.Length] };
        }

        /// <summary>
        /// Frames of a Unity log stack text. There are no native IPs here, so a native-looking hex in the line
        /// (Android <c>#00 pc 000...</c>, <c>0x...</c>) is forwarded as written.
        /// </summary>
        public static ManagedFrameSet FromLogText(string stackTrace)
        {
            var traces = FrameLines(StackLines(stackTrace));
            var addresses = new string[traces.Length];
            for (var i = 0; i < traces.Length; i++) addresses[i] = ExtractInstructionAddress(traces[i]);
            return new ManagedFrameSet { Traces = traces, Addresses = addresses };
        }

        /// <summary>
        /// Native instruction address from a stack line when present. Skips short bracketed IL offsets like
        /// <c>[0x00023]</c> and Mono <c>&lt;0xSTART + 0xOFFSET&gt;</c> (a method start, not a return address).
        /// </summary>
        public static string ExtractInstructionAddress(string stackLine)
        {
            if (string.IsNullOrEmpty(stackLine))
            {
                return null;
            }

            var scrubbed = IlOffsetBracketRegex.Replace(ManagedAddressPairing.StripMonoMethodStarts(stackLine), " ");
            var m = NativePcRegex.Match(scrubbed);
            if (!m.Success)
            {
                return null;
            }

            var hex = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            if (string.IsNullOrEmpty(hex))
            {
                return null;
            }
            return hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex : ("0x" + hex);
        }
    }
}
