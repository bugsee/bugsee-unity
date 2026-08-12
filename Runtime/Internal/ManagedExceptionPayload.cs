using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Bugsee.Internal
{
    /// <summary>
    /// Builds the UnityManagedException JSON-in-reason contract consumed by
    /// worker <c>unity.py</c>: name, reason, frames[{trace,address?}],
    /// signature, buildID, moduleUUID, optional nested <c>cause</c>.
    /// </summary>
    public static class ManagedExceptionPayload
    {
        static string _buildGuid;
        static string _moduleUuid;

        // Prefer native-looking PCs (8+ hex digits). Avoid Mono/IL "[0x00023]" IL offsets.
        static readonly Regex NativePcRegex = new Regex(
            @"(?:(?:^|[^\w./])(?:pc\s+)?(0x[0-9a-fA-F]{8,16})(?![0-9a-fA-F]))|(?:#\d+\s+pc\s+([0-9a-fA-F]{8,16})\b)",
            RegexOptions.Compiled);

        static readonly Regex IlOffsetBracketRegex = new Regex(
            @"\[0x[0-9a-fA-F]{1,6}\]",
            RegexOptions.Compiled);

        [Serializable]
        public class Frame
        {
            public string trace;
            /// <summary>Native instruction address / module offset when known (IL2CPP).</summary>
            public string address;
        }

        [Serializable]
        public class Payload
        {
            public string name;
            public string reason;
            public Frame[] frames;
            public string signature;
            public string buildID;
            public string moduleUUID;
            /// <summary>Nested inner / aggregate exception (worker walks <c>cause</c>).</summary>
            public Payload cause;
        }

        public static void EnsureBuildIdentity()
        {
            if (_buildGuid == null)
            {
                try { _buildGuid = Application.buildGUID; }
                catch { _buildGuid = Application.version; }
            }

            if (_moduleUuid == null)
            {
                try
                {
                    var asset = Resources.Load<TextAsset>("BugseeIl2CppModuleUuids");
                    if (asset != null && !string.IsNullOrEmpty(asset.text))
                    {
                        var lines = asset.text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        if (lines.Length > 0)
                        {
                            var cleaned = new List<string>(lines.Length);
                            foreach (var line in lines)
                            {
                                var t = line.Trim();
                                if (t.Length > 0) cleaned.Add(t);
                            }
                            _moduleUuid = string.Join(",", cleaned.ToArray());
                        }
                    }
                }
                catch { /* ignore */ }
                if (_moduleUuid == null)
                {
                    _moduleUuid = "";
                }
            }
        }

        public static string BuildGuid
        {
            get
            {
                EnsureBuildIdentity();
                return _buildGuid ?? "";
            }
        }

        public static string ModuleUuid
        {
            get
            {
                EnsureBuildIdentity();
                return _moduleUuid ?? "";
            }
        }

        /// <summary>Comma-separated IL2CPP module UUID(s) for all ABIs when known.</summary>
        public static string ModuleUuidsCsv => ModuleUuid;

        public static string[] GetRawFrames(string stackTrace)
        {
            if (string.IsNullOrEmpty(stackTrace))
            {
                return Array.Empty<string>();
            }
            var lines = stackTrace.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var list = new List<string>(lines.Length);
            foreach (var line in lines)
            {
                var t = line.Trim();
                if (t.Length == 0)
                {
                    continue;
                }
                if (t.StartsWith("---", StringComparison.Ordinal))
                {
                    continue;
                }
                // Skip AggregateException / inner separators that are not frames.
                if (t.StartsWith("--->", StringComparison.Ordinal))
                {
                    continue;
                }
                list.Add(t);
            }
            return list.ToArray();
        }

        public static Frame[] ToFrames(string[] rawFrames)
        {
            if (rawFrames == null || rawFrames.Length == 0)
            {
                return Array.Empty<Frame>();
            }
            var frames = new Frame[rawFrames.Length];
            for (var i = 0; i < rawFrames.Length; i++)
            {
                var trace = rawFrames[i] ?? "";
                frames[i] = new Frame { trace = trace, address = ExtractInstructionAddress(trace) };
            }
            return frames;
        }

        /// <summary>
        /// Pull a native instruction address from a stack line when present.
        /// Skips short bracketed IL offsets like <c>[0x00023]</c> which are not LNM PCs.
        /// </summary>
        public static string ExtractInstructionAddress(string stackLine)
        {
            if (string.IsNullOrEmpty(stackLine))
            {
                return null;
            }

            var scrubbed = IlOffsetBracketRegex.Replace(stackLine, " ");
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

        public static string CalculateSignature(string name, string[] rawFrames)
        {
            EnsureBuildIdentity();
            var sb = new StringBuilder();
            sb.Append(name ?? "");
            if (rawFrames != null)
            {
                foreach (var f in rawFrames)
                {
                    sb.Append(f);
                }
            }
            sb.Append(_buildGuid ?? "");
            return Sha256Hex(sb.ToString());
        }

        public static Payload FromException(Exception exception)
        {
            return FromException(exception, depth: 0);
        }

        static Payload FromException(Exception exception, int depth)
        {
            if (exception == null || depth > 8)
            {
                return null;
            }

            // Prefer a concrete inner over Aggregate wrapper when possible.
            if (exception is AggregateException ae && ae.InnerExceptions != null && ae.InnerExceptions.Count == 1)
            {
                return FromException(ae.InnerExceptions[0], depth);
            }

            var raw = GetRawFrames(exception.StackTrace);
            if (raw.Length == 0)
            {
                raw = GetRawFramesFromExceptionToString(exception.ToString());
            }
            EnrichAddressesFromDiagnostics(exception, raw);

            var frames = ToFrames(raw);
            string moduleUuid = string.IsNullOrEmpty(ModuleUuid) ? null : ModuleUuid;

            // Attach IL2CPP native IPs when the exception was thrown.
            var native = Il2CppNativeStack.TryCapture(exception);
            if (native != null && native.Frames != null && native.Frames.Length > 0)
            {
                MergeNativeAddresses(frames, native.Frames);
                if (!string.IsNullOrEmpty(native.ImageUuid))
                {
                    // Prefer the runtime image UUID (matches uploaded symbols) over CSV.
                    moduleUuid = native.ImageUuid;
                }
            }

            Payload cause = null;
            if (exception is AggregateException agg && agg.InnerExceptions != null && agg.InnerExceptions.Count > 1)
            {
                // Linearize siblings after each inner's own cause chain so we never
                // overwrite nested.cause (InnerException) when linking aggregates.
                Payload head = null;
                Payload tail = null;
                for (var i = 0; i < agg.InnerExceptions.Count; i++)
                {
                    var nested = FromException(agg.InnerExceptions[i], depth + 1);
                    if (nested == null) continue;
                    if (head == null)
                    {
                        head = nested;
                        tail = nested;
                    }
                    else
                    {
                        tail.cause = nested;
                        tail = nested;
                    }
                    while (tail.cause != null)
                        tail = tail.cause;
                }
                cause = head;
            }
            else if (exception.InnerException != null)
            {
                cause = FromException(exception.InnerException, depth + 1);
            }

            return new Payload
            {
                name = exception.GetType().FullName,
                reason = exception.Message ?? "",
                frames = frames,
                signature = CalculateSignature(exception.GetType().FullName, raw),
                buildID = BuildGuid,
                moduleUUID = moduleUuid,
                cause = cause,
            };
        }

        /// <summary>
        /// Merge IL2CPP native IPs onto managed frames. Both
        /// <c>Exception.StackTrace</c> and <c>il2cpp_native_stack_trace</c> are
        /// throw-site-first (callee → caller). Pair index-for-index — do <b>not</b>
        /// reverse (Sentry reverses because its managed frames are oldest-first).
        /// </summary>
        static void MergeNativeAddresses(Frame[] frames, IntPtr[] nativeFrames)
        {
            if (frames == null || nativeFrames == null || frames.Length == 0 || nativeFrames.Length == 0)
            {
                return;
            }

            var len = Math.Min(frames.Length, nativeFrames.Length);
            for (var i = 0; i < len; i++)
            {
                var native = nativeFrames[i];
                if (native == IntPtr.Zero) continue;
                var hex = "0x" + native.ToInt64().ToString("x");
                if (frames[i] == null)
                {
                    frames[i] = new Frame { trace = "", address = hex };
                }
                else if (string.IsNullOrEmpty(frames[i].address))
                {
                    frames[i].address = hex;
                }
            }
        }

        /// <summary>
        /// Frames from <see cref="Exception.ToString"/> — skip the
        /// <c>Type: message</c> header and inner-exception banners.
        /// </summary>
        static string[] GetRawFramesFromExceptionToString(string toString)
        {
            if (string.IsNullOrEmpty(toString))
            {
                return Array.Empty<string>();
            }
            var lines = toString.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var list = new List<string>(lines.Length);
            var skippedHeader = false;
            foreach (var line in lines)
            {
                var t = line.Trim();
                if (t.Length == 0) continue;
                if (t.StartsWith("---", StringComparison.Ordinal)) continue;
                if (t.StartsWith("--->", StringComparison.Ordinal)) continue;
                // First non-empty line is usually "Type: message".
                if (!skippedHeader)
                {
                    skippedHeader = true;
                    if (t.IndexOf(':') > 0 && !t.StartsWith("at ", StringComparison.Ordinal))
                    {
                        continue;
                    }
                }
                list.Add(t);
            }
            return list.ToArray();
        }

        /// <summary>
        /// When Unity embeds a native IP in StackFrame.ToString(), fold it into the
        /// matching raw line so <see cref="ExtractInstructionAddress"/> can pick it up.
        /// </summary>
        static void EnrichAddressesFromDiagnostics(Exception exception, string[] rawFrames)
        {
            if (exception == null || rawFrames == null || rawFrames.Length == 0)
            {
                return;
            }
            try
            {
                var st = new System.Diagnostics.StackTrace(exception, true);
                var count = Math.Min(st.FrameCount, rawFrames.Length);
                for (var i = 0; i < count; i++)
                {
                    if (!string.IsNullOrEmpty(ExtractInstructionAddress(rawFrames[i])))
                    {
                        continue;
                    }
                    var frame = st.GetFrame(i);
                    if (frame == null)
                    {
                        continue;
                    }
                    var text = frame.ToString();
                    var addr = ExtractInstructionAddress(text);
                    if (string.IsNullOrEmpty(addr))
                    {
                        continue;
                    }
                    if (rawFrames[i].IndexOf(addr, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        rawFrames[i] = rawFrames[i] + " " + addr;
                    }
                }
            }
            catch
            {
                // Diagnostics stack walk is best-effort on IL2CPP.
            }
        }

        public static Payload FromUnityLog(string message, string stackTrace)
        {
            var name = message ?? "";
            var colon = name.IndexOf(':');
            if (colon > 0)
            {
                name = name.Substring(0, colon);
            }
            var raw = GetRawFrames(stackTrace);
            return new Payload
            {
                name = name,
                reason = message ?? "",
                frames = ToFrames(raw),
                signature = CalculateSignature(name, raw),
                buildID = BuildGuid,
                moduleUUID = string.IsNullOrEmpty(ModuleUuid) ? null : ModuleUuid,
            };
        }

        public static string ToJson(Payload payload)
        {
            if (payload == null)
            {
                return "{}";
            }
            // JsonUtility serializes nested [Serializable] cause.
            return JsonUtility.ToJson(payload);
        }

        static string Sha256Hex(string input)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input ?? ""));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes)
                {
                    sb.Append(b.ToString("x2"));
                }
                return sb.ToString();
            }
        }
    }
}
