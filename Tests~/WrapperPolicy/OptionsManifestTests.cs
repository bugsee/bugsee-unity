using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bugsee.Contracts.Options;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    /// <summary>
    /// Checks the wrapper's option surface (<c>Options</c> constants, typed setters, enums, defaults the wrapper
    /// hardcodes) against the option manifests the native SDKs publish (bugsee/specs sdk/options): vendored under
    /// Fixtures/options-manifest by Tools~/scripts/update-options-manifest.sh for the versions pinned in
    /// Tools~/versions.env. A removed or renamed native option, a new one nobody decided about, or an enum that no
    /// longer agrees fails here instead of being dropped silently on the device (an unknown key is not an error
    /// on either SDK).
    ///
    /// Every allow-list below freezes a known gap with its reason and issue. An entry that is no longer needed fails
    /// too, so the lists only shrink: delete the entry when the gap is closed.
    /// </summary>
    [TestFixture]
    public class OptionsManifestTests
    {
        // ---- Allow-lists (key -> reason) ----

        // Android keys the manifest lists with no C# constant and no typed setter. bugsee/bugsee-unity#96.
        // Add a constant and a setter (and gate it per platform) to remove an entry.
        const string NoConstantAndroid = "#96: Android key without a C# constant";
        static readonly Dictionary<string, string> AndroidNotExposed = Keys(NoConstantAndroid,
            "config.max-data-size", "config.max-pending-report-age", "config.max-pending-reports", "config.notify-flush-delay", "crash-ndk.tombstone-logs",
            "detect.anr.sampling", "detect.crash-ndk", "detect.exit.bg_low_memory_as_error", "detect.hang.sampling",
            "detect.memory_leaks", "detect.memory_leaks.deep_sample_rate", "detect.memory_leaks.mode",
            "detect.thread_leaks");

        // iOS keys the iOS manifest lists that the wrapper deliberately does not expose. bugsee/bugsee-unity#97 exposed the rest
        // as typed IOSLaunchOptions properties (iOS-only, gated off Android by OptionPlatformGate).
        static readonly Dictionary<string, string> IosNotExposed = Keys("#97: not exposed on purpose",
            "capture.logs.oslog", "capture.status-bar-info");
        static readonly Dictionary<string, string> IosNotExposedReasons = new Dictionary<string, string>
        {
            ["com.bugsee.option.capture.logs.oslog"] = "permanently disabled in the iOS SDK (accepted and ignored): a property would do nothing",
            ["com.bugsee.option.capture.status-bar-info"] = "read only by a DEBUG build of the SDK, never the published one: a property would do nothing",
        };

        // Hidden (internal) options that a public C# constant and setter expose anyway. bugsee/bugsee-unity#108.
        static readonly Dictionary<string, string> HiddenButExposed = Keys("#108: hidden option exposed publicly",
            "$$DEBUG", "$$ENDPOINT");

        // Wrapper-only keys (not native options, not in any manifest): the wrapper reads them from the launch
        // options and they must never be forwarded as if the SDK knew them.
        static readonly Dictionary<string, string> WrapperOnly = Keys("wrapper-only key (unity.*)",
            "unity.capture-managed-exceptions", "unity.treat-log-exceptions-as-fatal",
            "unity.capture-unobserved-task-exceptions");

        // Native options with a constant but no typed setter on the launch options (reachable through Set/OptionsBuilder).
        static readonly Dictionary<string, string> NoTypedSetter = new Dictionary<string, string>();
        // (Keys of NoTypedSetter are checked for staleness in Setter_types_agree_with_the_manifest_types.)

        // A wrapper decode fallback (FromIntValue/FromRawValue for an int the enum does not know) that is not the
        // native default. Not a launch default: the value is only used for an unparseable int.
        static readonly Dictionary<string, string> FallbackDiffersFromDefault = new Dictionary<string, string>
        {
            ["LogLevel"] = "LogLevelExtensions.FromRawValue falls back to Info for an unknown int; the SDK default is Verbose",
            ["IssueSeverity"] = "IssueSeverityExtensions.FromIntValue falls back to VeryLow; the SDK defaults are per key (bug High, crash Blocker, error High)",
        };

        // Constant names that differ between the C# enum and the iOS manifest for the same value
        // (manifest enum.name:constant -> C# constant). Spec: compare enums by value, surface name differences.
        static readonly Dictionary<string, string> IosEnumNameDifferences = new Dictionary<string, string>
        {
            ["BugseeSeverityLevel:Low"] = "VeryLow",
        };

        // C# enum values the iOS enum lacks, although the shared setter sends them to iOS (manifest enum.name:constant -> decision).
        // Open question for the maintainers: gate FrameRate.Raw on iOS, or document that iOS ignores or clamps it.
        static readonly Dictionary<string, string> CsharpEnumValuesIosLacks = new Dictionary<string, string>
        {
            ["BugseeFrameRate:Raw"] = "FrameRate.Raw = 4 is Android-only (meaningful for DirectBuffers); BugseeFrameRate has Low, Medium, High; not gated yet",
        };

        static Dictionary<string, string> Keys(string reason, params string[] suffixes)
        {
            var map = new Dictionary<string, string>();
            foreach (var s in suffixes)
                if (s != null) map["com.bugsee.option." + s] = reason;
            return map;
        }

        // ---- Fixtures ----

        sealed class Option
        {
            public string Key, Type, Module, EnumName;
            public bool Hidden, Deprecated, StringValued;
            public Dictionary<string, long> EnumValues;
            public string EnumDefault, DefaultText;
        }

        sealed class Manifest
        {
            public string Sdk, SdkVersion;
            public Dictionary<string, Option> Options = new Dictionary<string, Option>();
        }

        static string RepoFile(string relative)
        {
            var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "package.json"))) dir = dir.Parent;
            Assert.That(dir, Is.Not.Null, "repo root (package.json) not found");
            return Path.Combine(dir.FullName, relative);
        }

        const string FixtureDir = "Tests~/WrapperPolicy/Fixtures/options-manifest/";
        const string Update = "Run Tools~/scripts/update-options-manifest.sh.";

        static Manifest Load(string sdk)
        {
            var path = RepoFile(FixtureDir + sdk + ".json");
            Assert.That(File.Exists(path), Is.True, path + " is missing. " + Update);
            var manifest = new Manifest();
            using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
            {
                var root = doc.RootElement;
                Assert.That(root.GetProperty("manifestVersion").GetInt32(), Is.EqualTo(1),
                    "a manifest format this test does not understand must be rejected, not parsed partially");
                manifest.Sdk = root.GetProperty("sdk").GetString();
                manifest.SdkVersion = root.GetProperty("sdkVersion").GetString();
                foreach (var o in root.GetProperty("options").EnumerateArray())
                {
                    // A field the spec requires but the file lacks must name the SDK and option, not throw out of the Lazy.
                    JsonElement Required(string name)
                    {
                        if (o.TryGetProperty(name, out var value)) return value;
                        Assert.Fail(sdk + " manifest option " + (o.TryGetProperty("key", out var k) ? k.ToString() : o.ToString()) + " has no \"" + name +
                            "\" (required by sdk/options/manifest.md). The vendored file is not a valid manifest: " + Update);
                        return default;
                    }
                    Required("default");
                    var option = new Option
                    {
                        Key = Required("key").GetString(),
                        Type = Required("type").GetString(),
                        Module = Required("module").GetString(),
                        Hidden = Required("hidden").GetBoolean(),
                        Deprecated = o.TryGetProperty("deprecated", out _),
                        DefaultText = o.GetProperty("default").ToString().ToLowerInvariant(),
                    };
                    if (option.Type == "enum")
                    {
                        var e = o.GetProperty("enum");
                        option.EnumName = e.GetProperty("name").GetString();
                        option.EnumDefault = o.GetProperty("default").ValueKind == JsonValueKind.Null ? null : o.GetProperty("default").GetString();
                        option.EnumValues = new Dictionary<string, long>();
                        foreach (var v in e.GetProperty("values").EnumerateObject())
                        {
                            // A string-valued enum (Android MemoryLeakMode) has no int: only its names are kept.
                            option.StringValued = v.Value.ValueKind == JsonValueKind.String;
                            option.EnumValues[v.Name] = option.StringValued ? -1 : v.Value.GetInt64();
                        }
                    }
                    Assert.That(manifest.Options.ContainsKey(option.Key), Is.False, sdk + " manifest lists " + option.Key + " twice");
                    manifest.Options[option.Key] = option;
                }
            }
            return manifest;
        }

        static readonly Lazy<Manifest> AndroidLazy = new Lazy<Manifest>(() => Load("android"));
        static readonly Lazy<Manifest> IosLazy = new Lazy<Manifest>(() => Load("ios"));
        static Manifest Android => AndroidLazy.Value;
        static Manifest Ios => IosLazy.Value;

        // The public string constants of Options: name -> key.
        static Dictionary<string, string> Constants()
        {
            var map = new Dictionary<string, string>();
            foreach (var f in typeof(Bugsee.Contracts.Options.Options).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (f.IsLiteral && f.FieldType == typeof(string)) map[f.Name] = (string)f.GetRawConstantValue();
            return map;
        }

        sealed class Setter { public string Type, Property; }

        // Typed setters: constant name -> property type, from the launch option classes and the builder.
        static Dictionary<string, Setter> Setters()
        {
            var map = new Dictionary<string, Setter>();
            // Expression or block body, qualified or not. A setter written another way is not seen: the "no typed setter" message says so.
            var pattern = new Regex(@"public\s+([\w.]+)\s+(\w+)\s*\{\s*set\s*(?:=>\s*|\{\s*)Set\(\s*(?:[\w.]*\.)?Options\.(\w+)\s*,");
            foreach (var file in new[] { "BugseeLaunchOptions.cs", "AndroidLaunchOptions.cs", "IOSLaunchOptions.cs" })
                foreach (Match m in pattern.Matches(File.ReadAllText(RepoFile("Runtime/Contracts/Options/" + file))))
                    map[m.Groups[3].Value] = new Setter { Type = m.Groups[1].Value.Substring(m.Groups[1].Value.LastIndexOf('.') + 1), Property = m.Groups[2].Value };
            return map;
        }

        static string SuffixOf(string key) => key.StartsWith("com.bugsee.option.") ? key.Substring("com.bugsee.option.".Length) : key;

        static string Plural(int n, string noun) => n + " " + noun + (n == 1 ? "" : "s");

        // ---- Tests ----

        [Test]
        public void Vendored_manifests_are_verbatim_copies_of_the_pinned_versions()
        {
            var pins = File.ReadAllText(RepoFile("Tools~/versions.env"));
            var expected = new Dictionary<string, string>
            {
                ["android"] = Regex.Match(pins, "ANDROID_SDK_VERSION=\"([^\"]+)\"").Groups[1].Value,
                ["ios"] = Regex.Match(pins, "IOS_SDK_VERSION=\"([^\"]+)\"").Groups[1].Value,
            };
            var sources = new Dictionary<string, string[]>();
            foreach (var raw in File.ReadAllLines(RepoFile(FixtureDir + "sources.txt")))
            {
                if (raw.Length == 0 || raw.StartsWith("#")) continue;
                var parts = raw.Split('|');
                Assert.That(parts, Has.Length.EqualTo(4), "sources.txt line: " + raw);
                sources[parts[0]] = parts;
            }

            foreach (var sdk in new[] { "android", "ios" })
            {
                Assert.That(sources.ContainsKey(sdk), Is.True, "sources.txt has no " + sdk + " line. " + Update);
                Assert.That(sources[sdk][1], Is.EqualTo(expected[sdk]),
                    sdk + " manifest is for " + sources[sdk][1] + " but Tools~/versions.env pins " + expected[sdk] +
                    ": the pin moved without its manifest. " + Update);
                var manifest = sdk == "android" ? Android : Ios;
                Assert.That(manifest.Sdk, Is.EqualTo(sdk));
                Assert.That(manifest.SdkVersion, Is.EqualTo(expected[sdk]), sdk + " manifest describes another version. " + Update);
                byte[] bytes = File.ReadAllBytes(RepoFile(FixtureDir + sdk + ".json"));
                var hash = BitConverter.ToString(SHA256.HashData(bytes)).Replace("-", "").ToLowerInvariant();
                Assert.That(hash, Is.EqualTo(sources[sdk][3]),
                    sdk + ".json was edited by hand (the published manifest is immutable; a fake option added to test the " +
                    "check would also land here). " + Update);
            }
        }

        // The format has no JSON schema, only the prose in bugsee/specs sdk/options/manifest.md: check what it requires.
        [Test]
        public void Manifests_follow_the_documented_shape()
        {
            var types = new HashSet<string> { "boolean", "int", "float", "string", "enum", "map", "list" };
            foreach (var manifest in new[] { Android, Ios })
            {
                Assert.That(manifest.Options, Is.Not.Empty, manifest.Sdk);
                foreach (var o in manifest.Options.Values)
                {
                    var where = manifest.Sdk + " " + o.Key;
                    Assert.That(o.Key, Does.StartWith("com.bugsee.option."), where);
                    Assert.That(types, Does.Contain(o.Type), where + ": unknown type " + o.Type);
                    Assert.That(o.Module, Is.Not.Empty, where);
                    if (o.Type != "enum") continue;
                    Assert.That(o.EnumValues, Is.Not.Null.And.Not.Empty, where + ": enum without values");
                    if (o.EnumDefault != null) // the spec allows "default": null
                        Assert.That(o.EnumValues.Keys, Does.Contain(o.EnumDefault), where + ": default is not a constant name of its enum");
                }
            }
        }

        // Spec, "strict on keys": nothing the wrapper emits may be a key no SDK knows. The wrapper-only keys are listed.
        [Test]
        public void Every_options_constant_is_a_native_key_or_a_listed_wrapper_key()
        {
            var problems = new List<string>();
            foreach (var kv in Constants())
            {
                bool native = Android.Options.ContainsKey(kv.Value) || Ios.Options.ContainsKey(kv.Value);
                bool wrapper = WrapperOnly.ContainsKey(kv.Value);
                if (!native && !wrapper)
                    problems.Add("Options." + kv.Key + " = \"" + kv.Value + "\" is in neither manifest (Android " + Android.SdkVersion +
                        ", iOS " + Ios.SdkVersion + "). The native SDK renamed or removed it: fix or remove the constant. If it is a " +
                        "wrapper-only key, add it to WrapperOnly in OptionsManifestTests.");
                if (native && wrapper)
                    problems.Add("Options." + kv.Key + " is listed in WrapperOnly but a native SDK now has that key: remove it from WrapperOnly.");
            }
            foreach (var key in WrapperOnly.Keys.Where(k => !Constants().ContainsValue(k)))
                problems.Add("WrapperOnly lists " + key + " but Options has no such constant: remove the entry.");
            Assert.That(problems, Is.Empty, string.Join("\n", problems));
        }

        // Spec, "strict on types and enum values", for the platform the key is emitted to.
        [Test]
        public void Setter_types_agree_with_the_manifest_types()
        {
            var setters = Setters();
            var problems = new List<string>();
            foreach (var key in NoTypedSetter.Keys)
            {
                var name = Constants().Where(c => c.Value == key).Select(c => c.Key).FirstOrDefault();
                if (name == null || setters.ContainsKey(name))
                    problems.Add("NoTypedSetter lists " + key + ", which has no constant or now has a typed setter: remove the entry.");
            }
            foreach (var kv in Constants())
            {
                var manifests = new[] { Android, Ios }.Where(m => m.Options.ContainsKey(kv.Value)).ToList();
                if (manifests.Count == 0) continue;
                if (!setters.TryGetValue(kv.Key, out var setter))
                {
                    if (!NoTypedSetter.ContainsKey(kv.Value))
                        problems.Add("Options." + kv.Key + " has no typed setter on the launch options (a setter not written as `set => Set(Options.X, ...)` or `set { Set(Options.X, ...); }` is not recognised by this test: reformat it or extend the pattern). Add one, or list the key in NoTypedSetter with a reason.");
                    continue;
                }
                foreach (var m in manifests)
                {
                    var o = m.Options[kv.Value];
                    var enumType = EnumTypeOf(setter.Type);
                    var ok = o.Type == "enum" ? enumType != null
                        : o.Type == "boolean" ? setter.Type == "bool"
                        : o.Type == "int" ? setter.Type == "int" || (enumType != null && enumType.IsDefined(typeof(FlagsAttribute), false))
                        : o.Type == "float" ? setter.Type == "float"
                        : o.Type == "string" ? setter.Type == "string"
                        : false;
                    if (!ok)
                        problems.Add(setter.Property + " (" + setter.Type + ") sets " + kv.Value + ", which the " + m.Sdk + " manifest declares as " +
                            o.Type + (o.EnumName != null ? " " + o.EnumName : "") + ". Change the setter type or the constant.");
                }
                if (Android.Options.TryGetValue(kv.Value, out var a) && Ios.Options.TryGetValue(kv.Value, out var i) && a.Type != i.Type)
                    problems.Add(kv.Value + " is " + a.Type + " on Android and " + i.Type + " on iOS: the wrapper sends one value to both.");
            }
            Assert.That(problems, Is.Empty, string.Join("\n", problems));
        }

        static Type EnumTypeOf(string name)
        {
            var t = typeof(Bugsee.Contracts.Options.Options).Assembly.GetType("Bugsee.Contracts.Options." + name);
            return t != null && t.IsEnum ? t : null;
        }

        // Enum values are the SDK's internal values, never ordinals (spec, Enums): the wrapper sends the int.
        [Test]
        public void Enum_values_agree_with_the_manifests()
        {
            var setters = Setters();
            var problems = new List<string>();
            var usedIosGaps = new HashSet<string>();
            var usedRenames = new HashSet<string>();
            foreach (var kv in Constants())
            {
                if (!setters.TryGetValue(kv.Key, out var setter) || EnumTypeOf(setter.Type) == null) continue;
                var csharp = new Dictionary<string, long>();
                foreach (var name in Enum.GetNames(EnumTypeOf(setter.Type)))
                    csharp[name] = Convert.ToInt64(Enum.Parse(EnumTypeOf(setter.Type), name));
                foreach (var m in new[] { Android, Ios })
                {
                    if (!m.Options.TryGetValue(kv.Value, out var o) || o.Type != "enum") continue;
                    Assert.That(o.StringValued, Is.False, kv.Value + " is a string-valued enum with a typed int setter: the SDK expects strings");
                    var where = setter.Type + " vs " + m.Sdk + " " + o.EnumName + " (" + kv.Value + ")";
                    foreach (var native in o.EnumValues)
                    {
                        var byValue = csharp.Where(c => c.Value == native.Value).Select(c => c.Key).ToList();
                        if (byValue.Count == 0)
                        {
                            problems.Add(where + ": the SDK has " + native.Key + " = " + native.Value + ", " + setter.Type + " has no such value. Add it.");
                            continue;
                        }
                        // iOS names may differ for the same value; Android names are the C# names.
                        var allowedRename = IosEnumNameDifferences.TryGetValue(o.EnumName + ":" + native.Key, out var renamed) && byValue.Contains(renamed);
                        if (allowedRename && !byValue.Contains(native.Key)) usedRenames.Add(o.EnumName + ":" + native.Key);
                        if (!byValue.Contains(native.Key) && !allowedRename)
                            problems.Add(where + ": value " + native.Value + " is " + native.Key + " in the SDK and " + string.Join("/", byValue) +
                                " in C#. Align the name, or list it in IosEnumNameDifferences if iOS names it differently on purpose.");
                    }
                    if (m == Android)
                    {
                        foreach (var c in csharp.Where(c => !o.EnumValues.ContainsKey(c.Key) || o.EnumValues[c.Key] != c.Value))
                            problems.Add(where + ": C# has " + c.Key + " = " + c.Value + ", which the Android SDK does not define.");
                    }
                    else
                    {
                        // The shared setter sends every C# value to iOS too (spec: the value maps of a cross-platform key must agree).
                        foreach (var c in csharp.Where(c => !o.EnumValues.Values.Contains(c.Value)))
                        {
                            var key = o.EnumName + ":" + c.Key;
                            usedIosGaps.Add(key);
                            if (!CsharpEnumValuesIosLacks.ContainsKey(key))
                                problems.Add(where + ": C# has " + c.Key + " = " + c.Value + " and the shared setter sends it to iOS, whose enum has no such value. " +
                                    "Gate it per platform, or list \"" + key + "\" in CsharpEnumValuesIosLacks with the decision.");
                        }
                    }
                }
            }
            foreach (var key in IosEnumNameDifferences.Keys.Where(k => !usedRenames.Contains(k)))
                problems.Add("IosEnumNameDifferences lists " + key + ", but iOS no longer names that value differently from C#: remove the entry.");
            foreach (var key in CsharpEnumValuesIosLacks.Keys.Where(k => !usedIosGaps.Contains(k)))
                problems.Add("CsharpEnumValuesIosLacks lists " + key + ", but iOS now has that value or C# no longer does: remove the entry.");
            Assert.That(problems, Is.Empty, string.Join("\n", problems));
        }

        // Every public native option is exposed or deliberately not, so a new native option fails here until decided.
        [Test]
        public void Every_native_option_is_exposed_or_listed_as_not_exposed()
        {
            var constants = new HashSet<string>(Constants().Values);
            var problems = new List<string>();
            Check(Android, AndroidNotExposed, constants, problems, "#96");
            Check(Ios, IosNotExposed, constants, problems, "#97");
            Assert.That(problems, Is.Empty, string.Join("\n", problems));
        }

        static void Check(Manifest manifest, Dictionary<string, string> notExposed, HashSet<string> constants, List<string> problems, string issue)
        {
            var other = manifest == Android ? Ios : Android;
            var missing = manifest.Options.Values
                .Where(o => !o.Hidden && !constants.Contains(o.Key) && !notExposed.ContainsKey(o.Key))
                // An option both SDKs list is judged once, on the Android side if it is public there (its entry covers iOS too),
                // otherwise on the iOS side (hidden on Android, public on iOS).
                .Where(o => manifest == Android || !(Android.Options.TryGetValue(o.Key, out var a) && !a.Hidden) || constants.Contains(o.Key))
                .Select(o => o.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
            if (missing.Count > 0)
                problems.Add(manifest.Sdk + " " + manifest.SdkVersion + " has " + Plural(missing.Count, "option") + " the wrapper neither exposes nor " +
                    "lists: " + string.Join(", ", missing.Select(SuffixOf)) + ". Decide for each: add an Options constant + typed setter (with its " +
                    "platform gate), or add it to " + (manifest == Android ? "AndroidNotExposed" : "IosNotExposed") + " in OptionsManifestTests with a reason (see " + issue + ").");
            foreach (var key in notExposed.Keys)
            {
                if (!manifest.Options.ContainsKey(key))
                    problems.Add((manifest == Android ? "AndroidNotExposed" : "IosNotExposed") + " lists " + SuffixOf(key) + ", which the " + manifest.Sdk +
                        " " + manifest.SdkVersion + " manifest no longer has: remove the entry.");
                else if (constants.Contains(key))
                    problems.Add((manifest == Android ? "AndroidNotExposed" : "IosNotExposed") + " lists " + SuffixOf(key) + ", which Options now has a constant for: remove the entry.");
            }
        }

        // Spec: a deprecated option is still honoured and listed "so a consumer can warn". The wrapper does not warn yet.
        static readonly Dictionary<string, string> DeprecatedButExposed = new Dictionary<string, string>
        {
            ["com.bugsee.option.capture.webview.domain-allowlist"] = "deprecated on Android since 7.1.2 (no replacement), still honoured; the C# constant is not marked [Obsolete]",
        };

        [Test]
        public void Deprecated_native_options_are_not_exposed_unless_listed()
        {
            var constants = Constants();
            var deprecated = new HashSet<string>(Android.Options.Values.Concat(Ios.Options.Values).Where(o => o.Deprecated).Select(o => o.Key));
            var problems = new List<string>();
            foreach (var kv in constants.Where(c => deprecated.Contains(c.Value) && !DeprecatedButExposed.ContainsKey(c.Value)))
                problems.Add("Options." + kv.Key + " (" + kv.Value + ") is deprecated in a manifest. Mark the constant and setter [Obsolete] and list the key in DeprecatedButExposed, or remove it.");
            foreach (var key in DeprecatedButExposed.Keys.Where(k => !deprecated.Contains(k) || !constants.ContainsValue(k)))
                problems.Add("DeprecatedButExposed lists " + key + ", which is no longer deprecated in a manifest or has no constant: remove the entry.");
            Assert.That(problems, Is.Empty, string.Join("\n", problems));
        }

        // Spec, "hidden options are not public surface".
        [Test]
        public void Hidden_native_options_are_not_exposed_unless_listed()
        {
            var constants = Constants();
            var hidden = new HashSet<string>(Android.Options.Values.Concat(Ios.Options.Values).Where(o => o.Hidden).Select(o => o.Key));
            var problems = new List<string>();
            foreach (var kv in constants.Where(c => hidden.Contains(c.Value) && !HiddenButExposed.ContainsKey(c.Value)))
                problems.Add("Options." + kv.Key + " exposes the hidden (internal) option " + kv.Value + ". Remove it from the public API, or list it in HiddenButExposed with an issue.");
            foreach (var key in HiddenButExposed.Keys)
            {
                if (!hidden.Contains(key)) problems.Add("HiddenButExposed lists " + key + ", which is no longer hidden in the manifests: remove the entry.");
                else if (!constants.ContainsValue(key)) problems.Add("HiddenButExposed lists " + key + " but Options no longer has it: remove the entry.");
            }
            // A key hidden on one SDK and public on the other is a decision for a human, whichever side hides it.
            foreach (var a in Android.Options.Values)
                if (Ios.Options.TryGetValue(a.Key, out var i) && a.Hidden != i.Hidden &&
                    !AndroidNotExposed.ContainsKey(a.Key) && !IosNotExposed.ContainsKey(a.Key) && !HiddenButExposed.ContainsKey(a.Key))
                    problems.Add(a.Key + " is " + (a.Hidden ? "hidden on Android but public on iOS" : "public on Android but hidden on iOS") +
                        ": decide whether the wrapper exposes it. HiddenButExposed (with a constant) or the not-exposed lists (which also silence this) record the decision.");
            Assert.That(problems, Is.Empty, string.Join("\n", problems));
        }

        // The platform split of the constants is derived from the manifests; keep the wrapper-only and
        // iOS-only keys classified (spec, "strict on keys": a key sent to an SDK must be in its manifest).
        [Test]
        public void Platform_only_constants_are_classified()
        {
            var constants = Constants();
            Assert.That(Ios.Options.ContainsKey(Bugsee.Contracts.Options.Options.CaptureCrashHandlerType), Is.True,
                "CaptureCrashHandlerType is iOS-only and must stay a key of the iOS manifest");
            Assert.That(Android.Options.ContainsKey(Bugsee.Contracts.Options.Options.CaptureCrashHandlerType), Is.False,
                "Android now has capture.crash-handler-type: it is no longer iOS-only, so the platform gate must stop dropping it");
            var iosOnly = new HashSet<string>(OptionPlatformGate.IosOnly);
            foreach (var key in iosOnly)
            {
                Assert.That(Ios.Options.ContainsKey(key), Is.True, key + " is gated as iOS-only but is not a key of the iOS manifest");
                Assert.That(Android.Options.ContainsKey(key), Is.False,
                    "Android now has " + key + ": it is no longer iOS-only, so the platform gate must stop dropping it");
            }
            var extra = constants.Where(c => Ios.Options.ContainsKey(c.Value) && !Android.Options.ContainsKey(c.Value) &&
                !iosOnly.Contains(c.Value)).Select(c => "Options." + c.Key + " (" + SuffixOf(c.Value) + ")").ToList();
            Assert.That(extra, Is.Empty,
                "These constants are in the iOS manifest but not the Android one (Android " + Android.SdkVersion + " dropped or never had them), so they are iOS-only now: " +
                string.Join(", ", extra) + ". They must be dropped from the Android launch (OptionPlatformGate.ForAndroid / IosOnly): update the gate and this test together.");
        }

        // The documented "Default when omitted" of each bool, int and enum property of IOSLaunchOptions is the iOS manifest default.
        [Test]
        public void Ios_bool_property_docs_state_the_manifest_default()
        {
            var keyOf = new Dictionary<string, string>();
            foreach (var kv in Setters())
                keyOf[kv.Value.Property] = Constants()[kv.Key];
            var problems = new List<string>();
            var doc = new StringBuilder();
            int checkedCount = 0;
            foreach (var line in File.ReadAllLines(RepoFile("Runtime/Contracts/Options/IOSLaunchOptions.cs")))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("///")) { doc.Append(' ').Append(trimmed.Substring(3)); continue; }
                var m = Regex.Match(trimmed, @"^public (\w+) (\w+)$");
                if (m.Success && m.Groups[1].Value != "string")
                {
                    var type = m.Groups[1].Value;
                    var prop = m.Groups[2].Value;
                    var docText = doc.ToString();
                    if (!keyOf.TryGetValue(prop, out var key) || !Ios.Options.TryGetValue(key, out var o))
                        problems.Add(prop + " is not a typed setter of an iOS manifest key");
                    else if (type == "bool" || type == "int")
                    {
                        var d = Regex.Match(docText, type == "bool" ? @"Default when omitted:\s*(true|false)\b" : @"Default when omitted:\s*(\d+)\b");
                        var enumForm = Regex.Match(docText, @"Default when omitted:\s*<c>(\w+)\.(\w+)</c>");
                        string documented = d.Success ? d.Groups[1].Value : null;
                        if (documented == null && type == "int" && enumForm.Success)
                            documented = ((int)Enum.Parse(EnumTypeOf(enumForm.Groups[1].Value), enumForm.Groups[2].Value)).ToString();
                        if (documented == null) problems.Add(prop + " does not document its default as `Default when omitted: X.`");
                        else
                        {
                            checkedCount++;
                            if (o.DefaultText != documented) problems.Add(prop + " documents default " + documented + ", the iOS manifest says " + o.DefaultText);
                        }
                    }
                    else if (EnumTypeOf(type) != null)
                    {
                        var d = Regex.Match(docText, @"Default when omitted:\s*<c>" + type + @"\.(\w+)</c>");
                        if (!d.Success) problems.Add(prop + " does not document its default as `Default when omitted: <c>" + type + ".X</c>`");
                        else
                        {
                            checkedCount++;
                            var documentedDefault = d.Groups[1].Value;
                            var expected = o.Type == "int" ? o.DefaultText : o.EnumDefault;
                            var actual = o.Type == "int" ? ((int)Enum.Parse(EnumTypeOf(type), documentedDefault)).ToString() : documentedDefault;
                            if (!string.Equals(expected, actual, StringComparison.Ordinal)) problems.Add(prop + " documents default " + documentedDefault + " (" + actual + "), the iOS manifest says " + expected);
                        }
                    }
                }
                doc.Clear();
            }
            Assert.That(checkedCount, Is.GreaterThan(10));
            Assert.That(problems, Is.Empty, string.Join("\n", problems));
        }

        [Test]
        public void Ios_not_exposed_keys_have_a_reason()
        {
            Assert.That(IosNotExposedReasons.Keys, Is.EquivalentTo(IosNotExposed.Keys));
        }

        // Defaults the wrapper hardcodes are compared with the manifest of the platform they apply to (spec,
        // "tolerant on defaults, but explicit": a difference between the two SDKs is information, not a failure).
        [Test]
        public void Wrapper_defaults_agree_with_the_native_manifest()
        {
            var problems = new List<string>();
            var fallbacks = new Dictionary<string, string>
            {
                ["VideoMode"] = Enum.GetName(typeof(VideoMode), VideoModeExtensions.FromIntValue(int.MinValue)),
                ["VideoQuality"] = Enum.GetName(typeof(VideoQuality), VideoQualityExtensions.FromIntValue(int.MinValue)),
                ["FrameRate"] = Enum.GetName(typeof(FrameRate), FrameRateExtensions.FromIntValue(int.MinValue)),
                ["LogLevel"] = Enum.GetName(typeof(LogLevel), LogLevelExtensions.FromRawValue(int.MinValue)),
                ["IssueSeverity"] = Enum.GetName(typeof(IssueSeverity), IssueSeverityExtensions.FromIntValue(int.MinValue)),
            };
            var differing = new HashSet<string>();
            foreach (var option in Android.Options.Values.Where(o => o.Type == "enum" && fallbacks.ContainsKey(o.EnumName)))
                if (fallbacks[option.EnumName] != option.EnumDefault)
                {
                    differing.Add(option.EnumName);
                    if (!FallbackDiffersFromDefault.ContainsKey(option.EnumName))
                        problems.Add(option.EnumName + ": the wrapper's fallback is " + fallbacks[option.EnumName] + " but the Android default of " + SuffixOf(option.Key) +
                            " is " + option.EnumDefault + ". Align the fallback, or list the enum in FallbackDiffersFromDefault with the reason.");
                }
            foreach (var name in FallbackDiffersFromDefault.Keys.Where(n => !differing.Contains(n)))
                problems.Add("FallbackDiffersFromDefault lists " + name + ", whose fallback now equals the Android default: remove the entry.");

            // The Android mapper's fallbacks for an int the enum does not know.
            var mapper = File.ReadAllText(RepoFile("Runtime/Platform/Android/AndroidOptionsMapper.cs"));
            var seen = new Dictionary<string, int>();
            foreach (Match m in Regex.Matches(mapper, @"EnumFromInt\(\w+, ""fromIntValue"", [^,]+, \(int\)(\w+)\.(\w+)\)"))
            {
                seen[m.Groups[1].Value] = seen.TryGetValue(m.Groups[1].Value, out var n) ? n + 1 : 1;
                var enumName = m.Groups[1].Value;
                var option = Android.Options.Values.Where(o => o.EnumName == enumName).ToList();
                Assert.That(option, Is.Not.Empty, "AndroidOptionsMapper falls back for " + enumName + ", which no Android option uses");
                Assert.That(option[0].EnumValues.Keys, Does.Contain(m.Groups[2].Value),
                    "AndroidOptionsMapper falls back to " + enumName + "." + m.Groups[2].Value + ", which the SDK does not define");
                if (enumName != "IssueSeverity")
                    Assert.That(m.Groups[2].Value, Is.EqualTo(option[0].EnumDefault), "AndroidOptionsMapper fallback for " + enumName + " differs from the SDK default");
            }
            // Every call must have been read (a reformatted one would otherwise escape), twice per enum: the key branch and the typed-value branch.
            Assert.That(Regex.Matches(mapper, "EnumFromInt\\(").Count - 1, Is.EqualTo(seen.Values.Sum()), // minus the method definition
                "an EnumFromInt call in AndroidOptionsMapper no longer matches this test's pattern: its fallback is unchecked. Keep the form `EnumFromInt(XClass, \"fromIntValue\", value, (int)X.Member)` or update the pattern");
            foreach (var enumName in new[] { "VideoMode", "VideoQuality", "FrameRate", "IssueSeverity" })
                Assert.That(seen.TryGetValue(enumName, out var calls) ? calls : 0, Is.EqualTo(2), "AndroidOptionsMapper: expected 2 fallbacks for " + enumName + " (key branch and typed-value branch)");

            // IOSLaunchOptions documents the iOS default of the crash handler.
            var documented = Regex.Match(File.ReadAllText(RepoFile("Runtime/Contracts/Options/IOSLaunchOptions.cs")), @"Default when omitted:\s*(?:///\s*)?<c>CrashHandlerType\.(\w+)</c>");
            Assert.That(documented.Success, Is.True, "IOSLaunchOptions no longer documents the crash handler default: update this test");
            Assert.That(documented.Groups[1].Value, Is.EqualTo(Ios.Options[Bugsee.Contracts.Options.Options.CaptureCrashHandlerType].EnumDefault));
            Assert.That(problems, Is.Empty, string.Join("\n", problems));
        }
    }
}
