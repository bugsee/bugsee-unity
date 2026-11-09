using System;
using System.Collections.Generic;
using System.Text;

namespace Bugsee.WrapperPolicy
{
    /// <summary>The on/off setting of the iOS native early launch. Off is the default: nothing changes unless it is asked for.</summary>
    public enum IosEarlyLaunchMode { Off, On }

    public sealed class IosEarlyLaunchInputs
    {
        public IosEarlyLaunchMode Mode;
        /// <summary>The iOS app token (environment variable, else the setting). Embedded in the app bundle: it is not a secret there either way.</summary>
        public string Token;
        /// <summary>A JSON object with the launch options to start the early session with (the same keys as the C# options); blank for the SDK defaults.</summary>
        public string OptionsJson;
        public string WrapperVersion;
        public string WrapperBuild;
        public string UnityVersion;
        public string ProductName;
        /// <summary>The first graphics API of the iOS player is Metal (what <c>SystemInfo.graphicsDeviceType</c> reports at run time).</summary>
        public bool Metal = true;
    }

    public sealed class IosEarlyLaunchDecision
    {
        public bool Enabled;
        /// <summary>One line for the build log: what is done, or why it is not.</summary>
        public string Reason;
        public bool IsWarning;
        /// <summary>The Info.plist entries (all strings) when <see cref="Enabled"/>.</summary>
        public Dictionary<string, string> Entries;
    }

    /// <summary>What the first C# Launch finds when it asks the native side about an early session.</summary>
    public enum IosEarlyAdoption
    {
        /// <summary>No early session is waiting to be adopted: launch as usual.</summary>
        None = 0,
        /// <summary>An early session is launching or launched with the same token: the C# Launch completes it instead of starting another.</summary>
        Adopt = 1,
        /// <summary>As <see cref="Adopt"/>, but the C# token differs from the one the early session runs with.</summary>
        AdoptTokenDiffers = 2,
    }

    /// <summary>
    /// Native early launch for iOS: the iOS SDK is started from native code during the app's startup, before Unity runs
    /// any C#, with a token and a JSON subset of the launch options fixed at build time; the later <c>Bugsee.Launch</c>
    /// adopts that session instead of starting a second one. The build step writes the configuration into the app's
    /// Info.plist (key <see cref="InfoPlistKey"/>); the native side reads it. Decisions, texts and the Info.plist edit live
    /// here, pure, so they can be unit-tested. See DESIGN.md.
    /// </summary>
    public static class IosEarlyLaunchPolicy
    {
        public const string EnabledEnvVar = "BUGSEE_IOS_EARLY_LAUNCH";
        public const string TokenEnvVar = "BUGSEE_IOS_APP_TOKEN";
        public const string OptionsEnvVar = "BUGSEE_IOS_EARLY_LAUNCH_OPTIONS";

        public const string InfoPlistKey = "BugseeEarlyLaunch";
        public const string TokenKey = "Token";
        public const string OptionsKey = "Options";
        public const string WrapperVersionKey = "WrapperVersion";
        public const string WrapperBuildKey = "WrapperBuild";
        public const string UnityVersionKey = "UnityVersion";
        public const string ProductNameKey = "ProductName";

        public const int MaxTokenLength = 256;
        public const int MaxOptionsLength = 16 * 1024;

        const string WrapperInfoKey = "wrapper_info";
        /// <summary>The frame rate told to the SDK in <c>wrapper_info</c>: Unity's mobile default, as the C# Launch uses when no target frame rate is set.</summary>
        public const int DefaultFrameRate = 30;

        /// <summary>"1"/"on"/"true"/"yes" turn it on; anything else (blank, "0", "off", typos) leaves it off.</summary>
        public static IosEarlyLaunchMode ParseMode(string value)
        {
            switch ((value ?? "").Trim().ToLowerInvariant())
            {
                case "1":
                case "on":
                case "true":
                case "yes":
                    return IosEarlyLaunchMode.On;
                default:
                    return IosEarlyLaunchMode.Off;
            }
        }

        /// <summary>A token is accepted when it is a single non-blank line of printable characters (a UUID in practice).</summary>
        public static bool IsUsableToken(string token)
        {
            if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength) return false;
            foreach (char c in token)
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c) || c == '<' || c == '>' || c == '&' || c == '"' || c == '\'') return false;
            }
            return true;
        }

        public static IosEarlyLaunchDecision Decide(IosEarlyLaunchInputs inputs)
        {
            if (inputs == null || inputs.Mode != IosEarlyLaunchMode.On)
                return new IosEarlyLaunchDecision { Reason = "native early launch is off (default)" };

            string token = (inputs.Token ?? "").Trim();
            if (token.Length == 0)
                return new IosEarlyLaunchDecision
                {
                    IsWarning = true,
                    Reason = "native early launch is on but no iOS app token was given (" + TokenEnvVar + " or the Bugsee/iOS/Early Launch window); it is not added",
                };
            if (!IsUsableToken(token))
                return new IosEarlyLaunchDecision
                {
                    IsWarning = true,
                    Reason = "native early launch is on but the iOS app token is not a single printable token; it is not added",
                };

            string warning = null;
            string optionsJson = null;
            string text = (inputs.OptionsJson ?? "").Trim();
            if (text.Length > MaxOptionsLength)
            {
                warning = "the early launch options are longer than " + MaxOptionsLength + " characters and were ignored";
            }
            else if (text.Length > 0)
            {
                var parsed = JsonRead.Object(text);
                if (parsed == null)
                    warning = "the early launch options are not a JSON object and were ignored (the SDK defaults are used)";
                else
                {
                    optionsJson = EarlyOptionsJson(parsed, inputs);
                }
            }
            if (optionsJson == null) optionsJson = EarlyOptionsJson(new Dictionary<string, object>(), inputs);

            var entries = new Dictionary<string, string>
            {
                [TokenKey] = token,
                [OptionsKey] = PlistSafeJson(optionsJson),
                [WrapperVersionKey] = inputs.WrapperVersion ?? "",
                [WrapperBuildKey] = inputs.WrapperBuild ?? "",
                [UnityVersionKey] = inputs.UnityVersion ?? "",
                [ProductNameKey] = inputs.ProductName ?? "",
            };
            return new IosEarlyLaunchDecision
            {
                Enabled = true,
                Entries = entries,
                IsWarning = warning != null,
                Reason = warning != null
                    ? "native early launch is added; " + warning
                    : "native early launch is added: the iOS SDK starts before Unity's first scene with the build-time token and options",
            };
        }

        /// <summary>
        /// The JSON with every <c>$</c> written as the JSON escape <c>\u0024</c>. Xcode expands build settings in Info.plist
        /// strings and turns <c>$$</c> into <c>$</c> (and would expand <c>$(NAME)</c>), which silently renamed the SDK's internal
        /// option keys such as <c>com.bugsee.option.$$ENDPOINT</c>; the escape has no <c>$</c> for Xcode to touch, and the JSON
        /// parsers (native and C#) decode it back. <c>$</c> only occurs inside JSON strings, so the document stays valid.
        /// </summary>
        public static string PlistSafeJson(string json) => json == null ? null : json.Replace("$", "\\u0024");

        /// <summary>
        /// The options the early session starts with: the build-time subset, minus what the iOS SDK has no counterpart for
        /// (see <see cref="OptionPlatformGate"/>), plus the same <c>wrapper_info</c> the C# Launch adds. Everything is JSON,
        /// so there is nothing to serialise that a callback would need: callbacks and filters are set from C# later.
        /// </summary>
        public static string EarlyOptionsJson(IDictionary<string, object> subset, IosEarlyLaunchInputs inputs)
        {
            var options = new Dictionary<string, object>(OptionPlatformGate.ForIos(subset));
            options.TryGetValue(WrapperInfoKey, out var supplied);
            if (!IosHostPolicy.IsUsableWrapperInfo(supplied))
            {
                options[WrapperInfoKey] = new Dictionary<string, object>
                {
                    { "type", "UNITY" },
                    { "version", inputs?.WrapperVersion ?? "" },
                    { "build", inputs?.WrapperBuild ?? "" },
                    { "metal", inputs == null || inputs.Metal },
                    { "fps", DefaultFrameRate },
                };
            }
            return JsonWire.Object(options);
        }

        /// <summary>
        /// The keys the C# <c>Launch</c> asked for that the running early session does not have (or has with another
        /// value). They are launch-only: the session is not restarted for them. <c>wrapper_info</c> is the wrapper's own and
        /// is not compared, nor are the internal keys (they carry <c>$$</c> after the <c>com.bugsee.option.</c> prefix, e.g. the endpoint). Sorted, so the warning is stable.
        /// </summary>
        public static List<string> DifferingKeys(IDictionary<string, object> requested, string earlyOptionsJson)
        {
            var differing = new List<string>();
            if (requested == null) return differing;
            var early = JsonRead.Object(earlyOptionsJson) ?? new Dictionary<string, object>();
            foreach (var kv in requested)
            {
                if (kv.Key == null || kv.Key == WrapperInfoKey || kv.Key.Contains(LaunchOptionsWire.InternalKeyPrefix)) continue;
                if (!early.TryGetValue(kv.Key, out var have) || !SameValue(kv.Value, have)) differing.Add(kv.Key);
            }
            differing.Sort(StringComparer.Ordinal);
            return differing;
        }

        static bool SameValue(object a, object b)
        {
            // Both sides go through the wire format, so 2 and 2L, 1.0f and 1.0 compare as the native side sees them.
            var left = LaunchOptionsWire.Normalize(new Dictionary<string, object> { ["v"] = a });
            var right = LaunchOptionsWire.Normalize(new Dictionary<string, object> { ["v"] = b });
            return JsonWire.Value(left["v"]) == JsonWire.Value(right["v"]);
        }

        /// <summary>
        /// What the C# Launch does with the native answer (0 none, 1 ran with the same token, 2 ran with another) and the SDK status
        /// (BugseeStatus raw: 0 Stopped, 1 Launching, 2 Launched, 3 Stopping). The early launch having run is what counts, not the status:
        /// the SDK defers its real start until the app is active, so Stopped is expected right after it. Only a stop in flight is left to
        /// the normal Launch (which waits for it).
        /// </summary>
        public static IosEarlyAdoption ResolveAdoption(int nativeAnswer, int nativeStatus)
        {
            if (nativeAnswer != 1 && nativeAnswer != 2) return IosEarlyAdoption.None;
            if (nativeStatus == 3) return IosEarlyAdoption.None;
            return nativeAnswer == 2 ? IosEarlyAdoption.AdoptTokenDiffers : IosEarlyAdoption.Adopt;
        }

        /// <summary>The Warning logged by the adopting C# Launch; null when there is nothing to say.</summary>
        public static string AdoptionWarning(IosEarlyAdoption adoption, IList<string> differingKeys)
        {
            var parts = new List<string>();
            if (adoption == IosEarlyAdoption.AdoptTokenDiffers)
                parts.Add("the token passed to Launch differs from the one the native early launch started with (the build-time token), "
                    + "so this session reports to the build-time token's app; make them equal, or turn the early launch off");
            if (differingKeys != null && differingKeys.Count > 0)
                parts.Add("these launch options differ from the early session's and are launch-only, so they are not applied: "
                    + string.Join(", ", differingKeys)
                    + " (put them into the early launch options, or Stop and Launch again)");
            return parts.Count == 0 ? null : "[Bugsee] Launch adopted the session the native early launch started: " + string.Join("; ", parts) + ".";
        }

        // ------------------------------------------------------------------------------------------------ Info.plist text

        /// <summary>
        /// Info.plist text with the early-launch dictionary set (replacing an earlier one), or the text unchanged when the
        /// plist is not recognisable. Idempotent: the same entries give the same text. The entries are strings, written
        /// in a fixed order and XML-escaped.
        /// </summary>
        public static string SetInfoPlistEntry(string plist, IDictionary<string, string> entries)
        {
            if (plist == null) return null;
            string removed = RemoveInfoPlistEntry(plist);
            int close = removed.LastIndexOf("</dict>", StringComparison.Ordinal);
            if (close < 0 || removed.IndexOf("</plist>", close, StringComparison.Ordinal) < 0) return plist;
            string newline = removed.Contains("\r\n") ? "\r\n" : "\n";
            var sb = new StringBuilder();
            sb.Append("\t<key>").Append(InfoPlistKey).Append("</key>").Append(newline);
            sb.Append("\t<dict>").Append(newline);
            foreach (string key in new[] { TokenKey, OptionsKey, WrapperVersionKey, WrapperBuildKey, UnityVersionKey, ProductNameKey })
            {
                if (!entries.TryGetValue(key, out string value)) continue;
                sb.Append("\t\t<key>").Append(key).Append("</key>").Append(newline);
                sb.Append("\t\t<string>").Append(XmlEscape(value ?? "")).Append("</string>").Append(newline);
            }
            sb.Append("\t</dict>").Append(newline);
            return removed.Substring(0, close) + sb + removed.Substring(close);
        }

        /// <summary>Info.plist text without the early-launch dictionary; unchanged (same string) when it has none.</summary>
        public static string RemoveInfoPlistEntry(string plist)
        {
            if (plist == null) return null;
            string key = "<key>" + InfoPlistKey + "</key>";
            int at = plist.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) return plist;
            int dictOpen = plist.IndexOf("<dict>", at + key.Length, StringComparison.Ordinal);
            int dictClose = dictOpen < 0 ? -1 : plist.IndexOf("</dict>", dictOpen, StringComparison.Ordinal);
            if (dictOpen < 0 || dictClose < 0) return plist;
            // Only whitespace may sit between the key and its dictionary, or this is not the entry we wrote.
            if (plist.Substring(at + key.Length, dictOpen - at - key.Length).Trim().Length != 0) return plist;
            int end = dictClose + "</dict>".Length;
            int start = at;
            // Take the indentation before the key and the line break after the dictionary with it.
            while (start > 0 && (plist[start - 1] == '\t' || plist[start - 1] == ' ')) start--;
            if (end < plist.Length && plist[end] == '\r') end++;
            if (end < plist.Length && plist[end] == '\n') end++;
            return plist.Substring(0, start) + plist.Substring(end);
        }

        public static bool HasInfoPlistEntry(string plist) =>
            plist != null && plist.IndexOf("<key>" + InfoPlistKey + "</key>", StringComparison.Ordinal) >= 0;

        static string XmlEscape(string s)
        {
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&apos;"); break;
                    default:
                        // Control characters other than tab/newline are not valid XML 1.0 text.
                        if (c < 0x20 && c != '\t' && c != '\n' && c != '\r') break;
                        sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>The native early-launch bookkeeping of BugseeEarlyLaunch.mm as a pure model, so its rules are unit-tested.</summary>
    public sealed class EarlyLaunchSession
    {
        bool _ran, _asked;
        string _token;

        /// <summary>The early launch called launchWithToken.</summary>
        public void Ran(string token) { _ran = true; _token = token; }

        /// <summary>The first C# Launch's question: 1 same token, 2 other token, 0 nothing (not run, or already asked).</summary>
        public int Adopt(string givenToken)
        {
            if (!_ran || _asked || _token == null) return 0;
            _asked = true;
            return givenToken != null && givenToken == _token ? 1 : 2;
        }
    }

    /// <summary>
    /// After the C# Launch adopted an early session, the lifecycle events the native side delivered before the managed callback
    /// existed are replayed (<c>Launching</c>, <c>Launched</c>), and a live delivery of the same event that races the replay is
    /// dropped, so a listener sees each of them once. Delivery is recorded always (cheap), but filtered only while armed by an
    /// adopting Launch: otherwise every event passes. Both calls run on the main thread, in delivery order.
    /// </summary>
    public sealed class EarlyLifecycleReplay
    {
        const string Launching = "com.bugsee.lifecycle.Launching";
        const string Launched = "com.bugsee.lifecycle.Launched";
        const string Stopped = "com.bugsee.lifecycle.Stopped";

        readonly object _gate = new object();
        bool _armed;
        bool _launchingDelivered, _launchedDelivered;

        /// <summary>The events still to replay for a native status (<c>BugseeStatus</c> raw value: 1 Launching, 2 Launched), minus the ones already delivered live; arms the filter.</summary>
        public IList<string> Arm(int nativeStatus)
        {
            lock (_gate)
            {
                _armed = true;
                var replay = new List<string>(2);
                if ((nativeStatus == 1 || nativeStatus == 2) && !_launchingDelivered) replay.Add(Launching);
                if (nativeStatus == 2 && !_launchedDelivered) replay.Add(Launched);
                return replay;
            }
        }

        /// <summary>Whether a lifecycle event is delivered to the listeners now; records it. False only while armed, for a repeat of Launching/Launched.</summary>
        public bool Admit(string eventType)
        {
            lock (_gate)
            {
                bool repeat = false;
                if (eventType == Launching) { repeat = _launchingDelivered; _launchingDelivered = true; }
                else if (eventType == Launched) { repeat = _launchedDelivered; _launchedDelivered = true; }
                else if (eventType == Stopped)
                {
                    // The session ended: what follows belongs to a new one.
                    _armed = false;
                    _launchingDelivered = _launchedDelivered = false;
                }
                return !(_armed && repeat);
            }
        }

        /// <summary>A Launch or Relaunch that starts a session of its own: its events pass.</summary>
        public void Disarm()
        {
            lock (_gate)
            {
                _armed = false;
                _launchingDelivered = _launchedDelivered = false;
            }
        }
    }
}
