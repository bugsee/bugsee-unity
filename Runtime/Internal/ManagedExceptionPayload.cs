using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Bugsee.WrapperPolicy;
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

        [Serializable]
        public class Frame
        {
            public string trace;
            /// <summary>
            /// For a thrown exception: return address of this frame as hex, MODULE-RELATIVE to the image named by
            /// the payload's <c>moduleUUID</c> (IL2CPP only); absent when it cannot be paired with certainty
            /// (see <see cref="ManagedAddressPairing"/>). On the Unity log path (<c>FromUnityLog</c>) a hex found
            /// in the log text is forwarded as written.
            /// </summary>
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

        /// <summary>Frame lines of a stack text (separators dropped); see <see cref="ManagedFrameAssembly"/>.</summary>
        public static string[] GetRawFrames(string stackTrace)
        {
            return ManagedFrameAssembly.FrameLines(ManagedFrameAssembly.StackLines(stackTrace));
        }

        static Frame[] ToFrames(ManagedFrameSet set)
        {
            var frames = new Frame[set.Traces.Length];
            for (var i = 0; i < frames.Length; i++)
            {
                frames[i] = new Frame { trace = set.Traces[i] ?? "", address = set.Addresses[i] };
            }
            return frames;
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

        public const int MaxPayloadNodes = ManagedNodeBudget.DefaultMax;

        public static Payload FromException(Exception exception)
        {
            return FromException(exception, 0, new ManagedNodeBudget());
        }

        static Payload FromException(Exception exception, int depth, ManagedNodeBudget budget)
        {
            if (exception == null || depth > 8)
            {
                return null;
            }

            // Prefer a concrete inner over Aggregate wrapper when possible.
            // Each unwrap counts as a level, so a deep chain of single-inner aggregates cannot overflow the stack
            // (past the limit the aggregate itself becomes the node).
            if (exception is AggregateException ae && ae.InnerExceptions != null && ae.InnerExceptions.Count == 1 && depth < 8)
            {
                return FromException(ae.InnerExceptions[0], depth + 1, budget);
            }

            if (!budget.TryTake())
            {
                return null;
            }

            // Exception.StackTrace is formatted anew on every read: read it once.
            var stackTrace = exception.StackTrace;
            string moduleUuid = string.IsNullOrEmpty(ModuleUuid) ? null : ModuleUuid;

            // IL2CPP native IPs, only for an exception that was thrown.
            var native = Il2CppNativeStack.TryCapture(exception);
            ulong[] ips = null;
            if (native != null && native.Frames != null && native.Frames.Length > 0)
            {
                ips = new ulong[native.Frames.Length];
                for (var i = 0; i < ips.Length; i++)
                {
                    // Module-relative offsets, never negative: zero-extend 32-bit values.
                    ips[i] = IntPtr.Size == 8
                        ? unchecked((ulong)native.Frames[i].ToInt64())
                        : unchecked((uint)native.Frames[i].ToInt32());
                }
                if (!string.IsNullOrEmpty(native.ImageUuid))
                {
                    // Prefer the runtime image UUID (matches uploaded symbols) over CSV.
                    moduleUuid = native.ImageUuid;
                }
            }

            var set = ManagedFrameAssembly.FromStackTrace(stackTrace, ips);
            if (set.Traces.Length == 0)
            {
                set = ManagedFrameAssembly.FromToString(exception.ToString());
            }
            var raw = set.Traces;
            var frames = ToFrames(set);

            Payload cause = null;
            if (exception is AggregateException agg && agg.InnerExceptions != null && agg.InnerExceptions.Count > 1)
            {
                // Linearize siblings after each inner's own cause chain so we never
                // overwrite nested.cause (InnerException) when linking aggregates.
                Payload head = null;
                Payload tail = null;
                for (var i = 0; i < agg.InnerExceptions.Count; i++)
                {
                    if (budget.IsExhausted) break;
                    var nested = FromException(agg.InnerExceptions[i], depth + 1, budget);
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
                cause = FromException(exception.InnerException, depth + 1, budget);
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

        public static Payload FromUnityLog(string message, string stackTrace)
        {
            var name = message ?? "";
            var colon = name.IndexOf(':');
            if (colon > 0)
            {
                name = name.Substring(0, colon);
            }
            var set = ManagedFrameAssembly.FromLogText(stackTrace);
            var raw = set.Traces;
            return new Payload
            {
                name = name,
                reason = message ?? "",
                frames = ToFrames(set),
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
            // JsonUtility drops the self-typed `cause` field, so each level is serialized without it and the
            // levels are joined once (see ManagedPayloadJson).
            var levels = new List<string>();
            for (var p = payload; p != null && levels.Count < MaxPayloadNodes; p = p.cause)
            {
                levels.Add(JsonUtility.ToJson(new Payload
                {
                    name = p.name,
                    reason = p.reason,
                    frames = p.frames,
                    signature = p.signature,
                    buildID = p.buildID,
                    moduleUUID = p.moduleUUID,
                }));
            }
            return ManagedPayloadJson.Chain(levels);
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
