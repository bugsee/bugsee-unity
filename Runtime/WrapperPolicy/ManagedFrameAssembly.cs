using System;
using System.Collections.Generic;

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
        /// Frames of a Unity log stack text (<c>Application.logMessageReceived</c>, for an exception Unity logged
        /// itself). The text is all there is: there are no native IPs and no way to name the image a number in it
        /// belongs to, so NO address is ever sent. A hex in such a line is not a module-relative return address: it
        /// is an IL offset (<c>[0x00023]</c>), a Mono method start (<c>&lt;0xSTART + 0xOFFSET&gt;</c>), or an
        /// absolute process address or a pc of some other library (<c>#00 pc ...</c> of <c>libunity.so</c>). The
        /// worker would resolve any of them against <c>moduleUUID</c> as an RVA and file a plausible but wrong C#
        /// line, so the frames stay symbolic (the method names, and the file:line a usym entry already printed).
        /// </summary>
        public static ManagedFrameSet FromLogText(string stackTrace)
        {
            var traces = FrameLines(StackLines(stackTrace));
            return new ManagedFrameSet { Traces = traces, Addresses = new string[traces.Length] };
        }
    }
}
