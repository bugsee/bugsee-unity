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
            "UnityPlayer.", "com.unity3d.", "com.bugsee.", "java.", "android.", "dalvik.", "libcore.",
        };

        // Wrapper namespaces are engine; the sample app's own code lives in Bugsee.Sample and counts as user code.
        const string SampleNamespace = "Bugsee.Sample.";

        // libunity.so, libil2cpp.so, libmono-2.0.so, optionally followed by "(+0x...)": the ".so" must end the name.
        static readonly Regex SharedLibrary = new Regex(@"^lib[^\s/()]*\.so(?![A-Za-z0-9_])", RegexOptions.Compiled);
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
            if (token[0] == '#' || token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return true; // native lines of a Unity log: "#00 pc ...", "0x... in UnityFramework"
            // A line that starts with a shared library name ("libunity.so(+0x1a2b)"); never a substring of a managed frame's class or path.
            if (SharedLibrary.IsMatch(token)) return true;
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
        /// Type name as hashed: no namespace, no generic arguments or assembly versions. The Unity log path only has the short
        /// name Unity prints ("NullReferenceException"), the throw path has the full name; both must hash alike.
        /// </summary>
        public static string NormalizeType(string type)
        {
            if (string.IsNullOrEmpty(type)) return "";
            var t = type.Trim();
            var bracket = t.IndexOf('[');
            if (bracket >= 0) t = t.Substring(0, bracket);
            var tick = t.IndexOf('`');
            if (tick >= 0) t = t.Substring(0, tick);
            var dot = t.LastIndexOf('.');
            return dot >= 0 ? t.Substring(dot + 1) : t;
        }

        /// <summary>A line that is not a frame: segment separators ("--- End of stack trace ...") and Unity's "Rethrow as X: message".</summary>
        public static bool IsSeparatorLine(string frame)
        {
            if (string.IsNullOrEmpty(frame)) return false;
            var t = frame.TrimStart();
            return t.StartsWith("---", StringComparison.Ordinal) || t.StartsWith("Rethrow as ", StringComparison.Ordinal);
        }

        /// <summary>How many of the first <paramref name="count"/> frames are user frames (diagnostics).</summary>
        public static int CountUserFrames(string[] frames, int count)
        {
            var n = 0;
            if (frames == null) return 0;
            for (var i = 0; i < count && i < frames.Length; i++)
            {
                if (!IsSeparatorLine(frames[i]) && !IsEngineFrame(frames[i])) n++;
            }
            return n;
        }

        /// <summary>
        /// How many leading frames to keep: drops the frames the exception's trace shares with the stack that is live where the
        /// payload is built (its caller chain), keeping the one frame that is the catch frame itself. <paramref name="callerStack"/>
        /// is the text of the live stack; null or no common tail keeps every frame. Frames are matched by <c>Type.Method</c> only
        /// (no arguments or lines), so same-named overloads match each other; at worst a few extra frames are cut. Nothing is cut
        /// when the shared tail is the whole trace and the live stack, without its leading Bugsee and engine frames (the logger on the way in), is longer (the throw frames are still on the stack, as in an
        /// exception filter or before the unwind), and matching stops at a Bugsee frame of the live stack.
        /// </summary>
        public static int TrimCallerTail(string[] frames, string callerStack)
        {
            if (frames == null) return 0;
            if (string.IsNullOrEmpty(callerStack)) return frames.Length;
            var live = ManagedFrameAssembly.FrameLines(ManagedFrameAssembly.StackLines(callerStack));
            var common = 0;
            while (common < frames.Length && common < live.Length)
            {
                var liveFrame = live[live.Length - 1 - common];
                var a = MethodToken(frames[frames.Length - 1 - common]);
                var b = MethodToken(liveFrame);
                if (a.Length == 0 || a != b || IsBugseeFrame(liveFrame)) break;
                common++;
            }
            var top = 0;
            while (top < live.Length && (IsBugseeFrame(live[top]) || IsEngineFrame(live[top]))) top++;
            if (common == frames.Length && live.Length - top > common) return frames.Length;
            // The first shared frame (from the throw side) is the method that caught the exception: keep it.
            return common <= 1 ? frames.Length : frames.Length - (common - 1);
        }

        static bool IsBugseeFrame(string frame)
        {
            var t = MethodToken(frame);
            return t.StartsWith("Bugsee.", StringComparison.Ordinal) && !t.StartsWith(SampleNamespace, StringComparison.Ordinal);
        }

        /// <summary>
        /// The signature (lowercase SHA-256 hex) of an exception type and its frames; see the class summary. Takes the first
        /// <see cref="MaxFrames"/> user frames (after the caller-tail trim) and describes each by its best identity: the
        /// module-relative address when it has one, else file:line, else the method token. The address recipe applies when the
        /// throw site (first user frame) has an address and the UUID is known; else the file:line recipe when any user frame has a
        /// file and line; else the weak recipe (method tokens, or the type alone).
        /// </summary>
        /// <param name="traces">Frame lines, throw site first.</param>
        /// <param name="addresses">Module-relative hex address per frame (null or empty entries for none), or null.</param>
        /// <param name="moduleUuid">UUID of the image the addresses belong to, "" when unknown.</param>
        /// <param name="callerStack">Text of the live stack where the payload is built (see <see cref="TrimCallerTail"/>), or null.</param>
        public static string Compute(string type, string[] traces, string[] addresses, string moduleUuid, string callerStack)
        {
            type = NormalizeType(type);
            traces = traces ?? new string[0];
            var count = TrimCallerTail(traces, callerStack);

            var addressTokens = new List<string>();
            var lineTokens = new List<string>();
            var weakTokens = new List<string>();
            var anyLine = false;
            var throwSiteHasAddress = false;
            for (var i = 0; i < count && weakTokens.Count < MaxFrames; i++)
            {
                if (IsSeparatorLine(traces[i]) || IsEngineFrame(traces[i])) continue;
                var method = MethodToken(traces[i]);
                var rva = NormalizeAddress(addresses != null && i < addresses.Length ? addresses[i] : null);
                string file;
                int line;
                var hasLine = TryFileLine(traces[i], out file, out line);
                var lineToken = hasLine ? "f:" + file + ":" + line.ToString(System.Globalization.CultureInfo.InvariantCulture) : "m:" + method;
                if (weakTokens.Count == 0) throwSiteHasAddress = rva != null;
                anyLine |= hasLine;
                weakTokens.Add(method);
                lineTokens.Add(lineToken);
                addressTokens.Add(rva != null ? "r:" + rva : lineToken);
            }

            string input;
            if (throwSiteHasAddress && !string.IsNullOrEmpty(moduleUuid))
            {
                input = "A|" + type + "|" + moduleUuid.ToLowerInvariant() + "|" + string.Join(",", addressTokens.ToArray());
            }
            else if (anyLine)
            {
                input = "F|" + type + "|" + string.Join("|", lineTokens.ToArray());
            }
            else
            {
                input = "W|" + type + "|" + string.Join("|", weakTokens.ToArray());
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
