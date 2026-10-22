using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// The client signature of a managed exception (issue #217), per <c>Documentation~/exception-signatures.md</c>.
    /// Pure logic (no Unity types). Only the exception type and the USER frames feed the hash (engine and framework frames are
    /// dropped, at most <see cref="MaxFrames"/> are used, throw site first), never the message, never the build GUID, never an IL
    /// offset or a directory. The first recipe that applies wins, and its tag is part of the hash so recipes cannot collide:
    /// <list type="number">
    /// <item><c>A</c> (addresses): a user frame has a module-relative address and the image UUID is known: type, UUID, the addresses.</item>
    /// <item><c>F</c> (file:line): a user frame has a file name and a line above 0: type, (file name, line) of those frames.</item>
    /// <item><c>W</c> (weak): type and the user method tokens (<c>Type.Method</c>, no arguments); type only when there is no user frame.</item>
    /// </list>
    /// Mono's <c>Exception.StackTrace</c> of a caught exception does not stop at the catch frame, it runs on to the thread's root,
    /// so one throw site gets a different trace per calling path. <see cref="TrimCallerTail"/> removes that tail by comparing it
    /// with the stack that is live where the exception is turned into a payload.
    /// </summary>
    public static class ManagedSignatureRecipe
    {
        /// <summary>Most user frames that feed the hash (throw site first).</summary>
        public const int MaxFrames = 8;

        static readonly string[] EnginePrefixes =
        {
            "UnityEngine.", "UnityEditor.", "System.", "Mono.", "Microsoft.", "Bugsee.",
            "Unity.Collections.", "Unity.Jobs.", "Unity.Burst.",
            "UnityPlayer", "com.unity3d.", "com.bugsee.", "java.", "android.", "dalvik.", "libcore.",
        };

        // Wrapper namespaces are engine; the sample app's own code lives in Bugsee.Sample and counts as user code.
        const string SampleNamespace = "Bugsee.Sample.";

        static readonly Regex AtLocation = new Regex(@"\(at\s+(?<file>.+):(?<line>\d+)\)\s*$", RegexOptions.Compiled);
        static readonly Regex InLocation = new Regex(@"\sin\s+(?<file>.+):(?<line>\d+)\s*$", RegexOptions.Compiled);

        /// <summary><c>Type.Method</c> of a frame line ("at " prefix, arguments, offsets and location dropped); "" when none.</summary>
        public static string MethodToken(string frame)
        {
            if (string.IsNullOrEmpty(frame)) return "";
            var t = frame.Trim();
            if (t.StartsWith("at ", StringComparison.Ordinal)) t = t.Substring(3).TrimStart();
            var paren = t.IndexOf(" (", StringComparison.Ordinal);
            if (paren >= 0) t = t.Substring(0, paren);
            else
            {
                var sp = t.IndexOf(' ');
                if (sp >= 0) t = t.Substring(0, sp);
            }
            return t.Trim();
        }

        /// <summary>True for a frame of the engine, the framework, the native layer or the wrapper itself.</summary>
        public static bool IsEngineFrame(string frame)
        {
            var token = MethodToken(frame);
            if (token.Length == 0 || token[0] == '(') return true; // "(wrapper managed-to-native)" and the like
            if (token.StartsWith(SampleNamespace, StringComparison.Ordinal)) return false;
            foreach (var p in EnginePrefixes)
            {
                if (token.StartsWith(p, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// File NAME (no directory) and line of a frame that names a source file with a line above 0; false for
        /// <c>in &lt;hash&gt;:0</c> and the like.
        /// </summary>
        public static bool TryFileLine(string frame, out string file, out int line)
        {
            file = null;
            line = 0;
            if (string.IsNullOrEmpty(frame)) return false;
            var m = AtLocation.Match(frame);
            if (!m.Success) m = InLocation.Match(frame);
            if (!m.Success) return false;
            if (!int.TryParse(m.Groups["line"].Value, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out line) || line <= 0)
            {
                return false;
            }
            var path = m.Groups["file"].Value.Trim();
            var cut = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
            var name = cut >= 0 ? path.Substring(cut + 1) : path;
            if (name.Length == 0 || name[0] == '<') return false;
            file = name;
            return true;
        }

        /// <summary>
        /// How many leading frames to keep: drops the frames the exception's trace shares with the stack that is live where the
        /// payload is built (its caller chain), keeping the one frame that is the catch frame itself. <paramref name="callerStack"/>
        /// is the text of the live stack; null or no common tail keeps every frame.
        /// </summary>
        public static int TrimCallerTail(string[] frames, string callerStack)
        {
            if (frames == null) return 0;
            if (string.IsNullOrEmpty(callerStack)) return frames.Length;
            var live = ManagedFrameAssembly.FrameLines(ManagedFrameAssembly.StackLines(callerStack));
            var common = 0;
            while (common < frames.Length && common < live.Length)
            {
                var a = MethodToken(frames[frames.Length - 1 - common]);
                var b = MethodToken(live[live.Length - 1 - common]);
                if (a.Length == 0 || a != b) break;
                common++;
            }
            // The first shared frame (from the throw side) is the method that caught the exception: keep it.
            return common <= 1 ? frames.Length : frames.Length - (common - 1);
        }

        /// <summary>The signature (lowercase SHA-256 hex) of an exception type and its frames; see the class summary.</summary>
        /// <param name="traces">Frame lines, throw site first.</param>
        /// <param name="addresses">Module-relative hex address per frame (null or empty entries for none), or null.</param>
        /// <param name="moduleUuid">UUID of the image the addresses belong to, "" when unknown.</param>
        /// <param name="callerStack">Text of the live stack where the payload is built (see <see cref="TrimCallerTail"/>), or null.</param>
        public static string Compute(string type, string[] traces, string[] addresses, string moduleUuid, string callerStack)
        {
            type = type ?? "";
            traces = traces ?? new string[0];
            var count = TrimCallerTail(traces, callerStack);

            var rvas = new List<string>();
            var files = new List<string>();
            var tokens = new List<string>();
            for (var i = 0; i < count; i++)
            {
                if (IsEngineFrame(traces[i])) continue;
                if (tokens.Count < MaxFrames) tokens.Add(MethodToken(traces[i]));
                var rva = NormalizeAddress(addresses != null && i < addresses.Length ? addresses[i] : null);
                if (rva != null && rvas.Count < MaxFrames) rvas.Add(rva);
                string file;
                int line;
                if (TryFileLine(traces[i], out file, out line) && files.Count < MaxFrames)
                {
                    files.Add(file + ":" + line.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }

            string input;
            if (rvas.Count > 0 && !string.IsNullOrEmpty(moduleUuid))
            {
                input = "A|" + type + "|" + moduleUuid.ToLowerInvariant() + "|" + string.Join(",", rvas.ToArray());
            }
            else if (files.Count > 0)
            {
                input = "F|" + type + "|" + string.Join("|", files.ToArray());
            }
            else
            {
                input = "W|" + type + "|" + string.Join("|", tokens.ToArray());
            }
            return Sha256Hex(input);
        }

        static string NormalizeAddress(string address)
        {
            if (string.IsNullOrEmpty(address)) return null;
            var a = address.Trim();
            if (a.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) a = a.Substring(2);
            a = a.TrimStart('0').ToLowerInvariant();
            return a.Length == 0 ? "0" : a;
        }

        static string Sha256Hex(string input)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
