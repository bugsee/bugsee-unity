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
    /// worker <c>unity.py</c> / MethodMap: name, reason, frames[{trace}],
    /// signature, buildID, and optional moduleUUID + instruction addresses.
    /// </summary>
    public static class ManagedExceptionPayload
    {
        static string _buildGuid;
        static string _moduleUuid;
        static readonly Regex AddressRegex = new Regex(
            @"0x[0-9a-fA-F]+",
            RegexOptions.Compiled);

        [Serializable]
        public class Frame
        {
            public string trace;
            /// <summary>Optional instruction address when present in the stack line.</summary>
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
                            // Preserve all ABI UUIDs as a CSV (matches CLI --uuid).
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
                string address = null;
                var m = AddressRegex.Match(trace);
                if (m.Success)
                {
                    address = m.Value;
                }
                frames[i] = new Frame { trace = trace, address = address };
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

        public static Payload FromException(Exception exception)
        {
            if (exception == null)
            {
                return null;
            }
            var raw = GetRawFrames(exception.StackTrace);
            return new Payload
            {
                name = exception.GetType().FullName,
                reason = exception.Message ?? "",
                frames = ToFrames(raw),
                signature = CalculateSignature(exception.GetType().FullName, raw),
                buildID = BuildGuid,
                moduleUUID = string.IsNullOrEmpty(ModuleUuid) ? null : ModuleUuid,
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
            // JsonUtility drops null moduleUUID; rebuild a minimal object when needed.
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
