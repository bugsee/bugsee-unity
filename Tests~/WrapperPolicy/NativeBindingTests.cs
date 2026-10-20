using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    /// <summary>
    /// Links the managed side of each bridge to the native side by parsing the sources: every C# DllImport must
    /// resolve to an Objective-C export with a compatible signature (in the real and the no-SDK stub section),
    /// and every UnityWrapper call made over JNI must reach a public static Java method of the same arity.
    /// A signature drift is otherwise only visible on a device, as a crash or a silently wrong argument.
    /// </summary>
    public class NativeBindingTests
    {
        static string RepoFile(string relative)
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, "package.json")))
                    return Path.Combine(dir, relative);
                dir = Directory.GetParent(dir)?.FullName;
            }

            throw new InvalidOperationException("Could not locate repo root (package.json).");
        }

        static string StripComments(string s)
        {
            s = Regex.Replace(s, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            return Regex.Replace(s, @"//[^\n]*", " ");
        }

        static string ReadAll(string dir, string pattern)
        {
            return string.Join("\n", Directory.GetFiles(RepoFile(dir), pattern).OrderBy(f => f).Select(File.ReadAllText));
        }

        sealed class Sig
        {
            public string Name;
            public string Ret;
            public string[] Params;
            public string Origin;
            public bool Stub;

            public override string ToString() => Ret + " " + Name + "(" + string.Join(", ", Params) + ")";
        }

        static string[] SplitTopLevel(string args)
        {
            var parts = new List<string>();
            int depth = 0, start = 0;
            char quote = '\0';
            for (int i = 0; i < args.Length; i++)
            {
                char c = args[i];
                if (quote != '\0')
                {
                    if (c == '\\') i++;
                    else if (c == quote) quote = '\0';
                    continue;
                }

                if (c == '"' || c == '\'') quote = c;
                else if (c == '(' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == ']' || c == '}') depth--;
                else if (c == ',' && depth == 0)
                {
                    parts.Add(args.Substring(start, i - start).Trim());
                    start = i + 1;
                }
            }

            var last = args.Substring(start).Trim();
            if (last.Length > 0 || parts.Count > 0) parts.Add(last);
            return parts.ToArray();
        }

        // ---------------------------------------------------------------- iOS

        static Dictionary<string, string[]> CsDelegates(string src)
        {
            var map = new Dictionary<string, string[]>();
            foreach (Match m in Regex.Matches(src, @"delegate\s+void\s+(\w+)\s*\(([^)]*)\)\s*;"))
                map[m.Groups[1].Value] = SplitTopLevel(m.Groups[2].Value).Select(CsParamType).ToArray();
            return map;
        }

        static string CsParamType(string p)
        {
            p = Regex.Replace(p.Trim(), @"^(?:ref|out|in)\s+", "");
            var m = Regex.Match(p, @"^(\S+)\s+\w+$");
            return m.Success ? m.Groups[1].Value : p;
        }

        static string StripAttributes(string s) => Regex.Replace(s, @"\[[^\]\s][^\]]*\]", " ");

        static int CountDllImports(string src) => Regex.Matches(src, @"DllImport\s*\(\s*""__Internal""").Count;

        // Column-0 lines that open a _bugsee_ definition or declaration: a looser count than the export parser's.
        static int CountExportLines(string mm) => Regex.Matches(mm, @"(?m)^[^\s#}][^\n;=]*\b_bugsee_\w+\s*\(").Count;

        static List<Sig> CsDllImports(string src)
        {
            var list = new List<Sig>();
            foreach (Match m in Regex.Matches(src, @"DllImport\s*\(\s*""__Internal"""))
            {
                int ext = src.IndexOf("extern", m.Index, StringComparison.Ordinal);
                Assert.That(ext, Is.GreaterThan(0), "DllImport without an extern declaration");
                int open = src.IndexOf('(', ext);
                int depth = 0, close = open;
                for (; close < src.Length; close++)
                {
                    if (src[close] == '(') depth++;
                    else if (src[close] == ')' && --depth == 0) break;
                }

                // "extern <ret> <name>" (attributes such as [return: MarshalAs] removed), then the parameter list.
                var head = StripAttributes(src.Substring(ext + "extern".Length, open - ext - "extern".Length)).Trim();
                var tokens = head.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < 2) continue;
                var args = StripAttributes(src.Substring(open + 1, close - open - 1)).Trim();
                list.Add(new Sig
                {
                    Ret = tokens[tokens.Length - 2],
                    Name = tokens[tokens.Length - 1],
                    Params = args.Length == 0
                        ? new string[0]
                        : SplitTopLevel(args).Select(CsParamType).ToArray(),
                });
            }

            return list;
        }

        static string NormC(string t)
        {
            t = Regex.Replace(t.Trim(), @"\s+", " ");
            t = Regex.Replace(t, @"\s*\*\s*", "*");
            return t;
        }

        static string CParamType(string p)
        {
            p = p.Trim();
            if (p == "void" || p.Length == 0) return p;
            var m = Regex.Match(p, @"^(.*[\s\*])(\w+)$");
            return NormC(m.Success ? m.Groups[1].Value : p);
        }

        // Every definition per name: the SDK and the no-SDK section each declare their own copy.
        static void AddCTypedefs(string mm, Dictionary<string, List<string[]>> map)
        {
            foreach (Match m in Regex.Matches(mm, @"typedef\s+void\s*\(\s*\*\s*(\w+)\s*\)\s*\(([^)]*)\)\s*;"))
            {
                if (!map.TryGetValue(m.Groups[1].Value, out var list)) map[m.Groups[1].Value] = list = new List<string[]>();
                list.Add(SplitTopLevel(m.Groups[2].Value).Select(CParamType).ToArray());
            }
        }

        static List<Sig> CExports(string mm, string origin)
        {
            var list = new List<Sig>();
            // The no-SDK stubs are the last extern "C" block of each file.
            int stubStart = mm.LastIndexOf("extern \"C\" {", StringComparison.Ordinal);
            var re = new Regex(
                @"(?m)^(?<ret>[A-Za-z_][\w \t\*]*?)\s*(?<name>_bugsee_\w+)\s*\((?<args>[^)]*)\)\s*\{");
            foreach (Match m in re.Matches(mm))
            {
                var args = m.Groups["args"].Value.Trim();
                list.Add(new Sig
                {
                    Ret = NormC(m.Groups["ret"].Value),
                    Name = m.Groups["name"].Value,
                    Params = args == "void" || args.Length == 0
                        ? new string[0]
                        : SplitTopLevel(args).Select(CParamType).ToArray(),
                    Origin = origin,
                    Stub = m.Index > stubStart,
                });
            }

            return list;
        }

        static string[] ScalarCTypes(string cs, bool isReturn)
        {
            switch (cs)
            {
                case "void": return new[] { "void" };
                case "int": return new[] { "int" };
                case "uint": return new[] { "unsigned int", "uint32_t" };
                case "long": return new[] { "int64_t" };
                case "ulong": return new[] { "uint64_t" };
                case "float": return new[] { "float" };
                case "double": return new[] { "double" };
                case "bool": return new[] { "bool", "BOOL" };
                case "string": return new[] { "const char*" };
                case "int[]": return new[] { "int*" };
                case "IntPtr": return isReturn ? new[] { "char*" } : new[] { "char*", "void*" };
                default: return null;
            }
        }

        static bool Compatible(
            string cs, string c, bool isReturn,
            Dictionary<string, string[]> csDelegates, Dictionary<string, List<string[]>> cTypedefs, bool stub)
        {
            var scalar = ScalarCTypes(cs, isReturn);
            if (scalar != null) return scalar.Contains(c);

            // A delegate is passed as a C function pointer: the typedef must describe the same callback.
            if (!csDelegates.TryGetValue(cs, out var csArgs)) return false;
            if (stub && c == "void*") return true;
            if (!cTypedefs.TryGetValue(c, out var all)) return false;
            foreach (var cArgs in all)
            {
                if (cArgs.Length != csArgs.Length) return false;
                for (int i = 0; i < csArgs.Length; i++)
                {
                    var allowed = ScalarCTypes(csArgs[i], false);
                    if (allowed == null || !allowed.Contains(cArgs[i])) return false;
                }
            }

            return true;
        }

        static string[] IosCsFiles() => new[] { "IOSBridge.cs", "IosNativeCallbacks.cs", "IosSpanHandle.cs" };

        static string IosCs(string file) => StripComments(File.ReadAllText(RepoFile("Runtime/Platform/IOS/" + file)));

        static List<Sig> AllImports(out Dictionary<string, string[]> delegates)
        {
            delegates = new Dictionary<string, string[]>();
            var imports = new List<Sig>();
            foreach (var f in IosCsFiles())
            {
                var src = IosCs(f);
                foreach (var kv in CsDelegates(src)) delegates[kv.Key] = kv.Value;
                foreach (var s in CsDllImports(src))
                {
                    s.Origin = f;
                    imports.Add(s);
                }
            }

            return imports;
        }

        static List<Sig> AllExports(out Dictionary<string, List<string[]>> typedefs)
        {
            typedefs = new Dictionary<string, List<string[]>>();
            var exports = new List<Sig>();
            foreach (var f in new[] { "BugseeUnityBridge.mm", "BugseeUnityCallbacks.mm" })
            {
                var mm = StripComments(File.ReadAllText(RepoFile("Plugins/iOS/" + f)));
                AddCTypedefs(mm, typedefs);
                exports.AddRange(CExports(mm, f));
            }

            return exports;
        }

        [Test]
        public void SplitTopLevel_ignores_commas_in_literals_and_brackets_and_lambda_arrows()
        {
            Assert.That(SplitTopLevel("\"log\", \"a, b\", 1"), Is.EqualTo(new[] { "\"log\"", "\"a, b\"", "1" }));
            Assert.That(SplitTopLevel("f(1, 2), x => x > 1, ','"), Is.EqualTo(new[] { "f(1, 2)", "x => x > 1", "','" }));
            Assert.That(SplitTopLevel(""), Is.Empty);
        }

        [Test]
        public void Parsers_find_the_known_surface()
        {
            // Guards the parsers themselves: a regex that silently matches nothing would pass every other test.
            var imports = AllImports(out var delegates);
            var exports = AllExports(out var typedefs);

            // Nothing may be skipped silently: parsed counts equal the raw occurrence counts.
            Assert.That(imports.Count, Is.EqualTo(IosCsFiles().Sum(f => CountDllImports(IosCs(f)))));
            var exportLines = new[] { "BugseeUnityBridge.mm", "BugseeUnityCallbacks.mm" }
                .Sum(f => CountExportLines(StripComments(File.ReadAllText(RepoFile("Plugins/iOS/" + f)))));
            Assert.That(exports.Count, Is.EqualTo(exportLines));
            Assert.That(imports.Count, Is.GreaterThanOrEqualTo(55));
            Assert.That(delegates.Keys, Does.Contain("FilterCb").And.Contain("LaunchStartedNativeCallback"));
            Assert.That(typedefs.Keys, Does.Contain("BugseeUnityFilterCb").And.Contain("BugseeLaunchStartedFn"));

            var launch = imports.Single(s => s.Name == "_bugsee_launch");
            Assert.That(launch.Params, Is.EqualTo(new[] { "string", "string", "int", "LaunchStartedNativeCallback" }));
            var export = exports.First(s => s.Name == "_bugsee_launch");
            Assert.That(export.Params, Is.EqualTo(new[] { "const char*", "const char*", "int", "BugseeLaunchStartedFn" }));
            Assert.That(exports.First(s => s.Name == "_bugsee_get_email").Ret, Is.EqualTo("char*"));
        }

        [Test]
        public void Every_DllImport_has_an_export_with_a_compatible_signature_in_every_section()
        {
            var imports = AllImports(out var delegates);
            var exports = AllExports(out var typedefs);
            var problems = new List<string>();

            foreach (var imp in imports)
            {
                var defs = exports.Where(e => e.Name == imp.Name).ToList();
                if (defs.Count == 0)
                {
                    problems.Add(imp.Origin + ": no export for " + imp);
                    continue;
                }

                // One definition per build flavour (SDK present / absent, legacy exception API): all must match.
                foreach (var def in defs)
                {
                    bool stub = def.Stub;
                    if (def.Params.Length != imp.Params.Length)
                    {
                        problems.Add(imp.Origin + ": " + imp + " vs " + def.Origin + ": " + def + " (arity)");
                        continue;
                    }

                    if (!Compatible(imp.Ret, def.Ret, true, delegates, typedefs, false))
                        problems.Add(imp.Origin + ": " + imp + " vs " + def.Origin + ": " + def + " (return)");
                    for (int i = 0; i < imp.Params.Length; i++)
                    {
                        if (!Compatible(imp.Params[i], def.Params[i], false, delegates, typedefs, stub))
                            problems.Add(imp.Origin + ": " + imp + " vs " + def.Origin + ": " + def + " (param " + i + ")");
                    }
                }
            }

            Assert.That(problems, Is.Empty, string.Join("\n", problems));
        }

        [Test]
        public void Every_DllImport_is_defined_in_both_the_sdk_and_the_no_sdk_section()
        {
            // Without the SDK (editor-adjacent or a stripped build) the stub section keeps the link line intact.
            var imports = AllImports(out _);
            var exports = AllExports(out _);
            var missing = imports.Select(i => i.Name).Distinct()
                .Where(n => !exports.Any(e => e.Name == n && !e.Stub) || !exports.Any(e => e.Name == n && e.Stub))
                .ToList();

            Assert.That(missing, Is.Empty, "Missing from the SDK section or from the no-SDK stub section: " + string.Join(", ", missing));
        }

        [Test]
        public void Repeated_DllImports_of_one_symbol_agree()
        {
            var imports = AllImports(out _);
            var bad = imports.GroupBy(i => i.Name)
                .Where(g => g.Select(s => s.ToString()).Distinct().Count() > 1)
                .Select(g => g.Key)
                .ToList();

            Assert.That(bad, Is.Empty, "Declared differently in different files: " + string.Join(", ", bad));
        }

        [Test]
        public void Every_production_export_is_imported_by_managed_code()
        {
            // An export nothing calls is dead weight in every player; a renamed import would otherwise leave one behind.
            var imported = new HashSet<string>(AllImports(out _).Select(i => i.Name));
            // Test hooks are allowed when the native test harness uses them; _bugsee_test_crash is production and is imported.
            var harness = File.ReadAllText(RepoFile("Tests~/iOS/BridgeTests.mm"));
            var orphans = AllExports(out _)
                .Select(e => e.Name)
                .Where(n => !imported.Contains(n)
                    && !(n.StartsWith("_bugsee_test_", StringComparison.Ordinal) && harness.Contains(n)))
                .Distinct()
                .ToList();

            // _bugsee_remove_all_secure_rects is kept as a no-op for the secure-rect API shape.
            orphans.Remove("_bugsee_remove_all_secure_rects");
            orphans.Remove("_bugsee_add_secure_rect");
            orphans.Remove("_bugsee_remove_secure_rect");

            Assert.That(orphans, Is.Empty, "Exported but never imported: " + string.Join(", ", orphans));
        }

        // ------------------------------------------------------------ Android

        static string ClassBody(string src, string className)
        {
            var m = Regex.Match(src, @"\bclass\s+" + className + @"\b[^{]*\{");
            Assert.That(m.Success, Is.True, "class " + className + " not found");
            int depth = 1, i = m.Index + m.Length;
            for (; i < src.Length && depth > 0; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}') depth--;
            }

            return src.Substring(m.Index + m.Length, i - m.Index - m.Length);
        }

        /// <summary>The text between an already consumed "(" at <paramref name="open"/> - 1 and its matching ")".</summary>
        static string BalancedArgs(string src, int open)
        {
            int depth = 1, i = open;
            bool inString = false;
            for (; i < src.Length && depth > 0; i++)
            {
                char c = src[i];
                if (inString)
                {
                    if (c == '\\') i++;
                    else if (c == '"') inString = false;
                    continue;
                }

                if (c == '"') inString = true;
                else if (c == '(') depth++;
                else if (c == ')') depth--;
            }

            return src.Substring(open, i - 1 - open);
        }

        static List<Sig> JavaPublicStatics()
        {
            var java = ClassBody(StripComments(ReadAll("Plugins/Android", "*.java")), "UnityWrapper");
            var list = new List<Sig>();
            var re = new Regex(@"public\s+static\s+(?:final\s+)?[\w<>\[\]\.]+\s+(\w+)\s*\(([^)]*)\)");
            foreach (Match m in re.Matches(java))
            {
                var args = m.Groups[2].Value.Trim();
                list.Add(new Sig
                {
                    Name = m.Groups[1].Value,
                    Params = args.Length == 0 ? new string[0] : SplitTopLevel(args),
                });
            }

            return list;
        }

        /// <summary>Calls of the form wrapper.CallStatic("name", a, b, ...) with the argument count.</summary>
        static List<(string name, int argc, string file)> UnityWrapperCalls()
        {
            var calls = new List<(string, int, string)>();
            foreach (var path in Directory.GetFiles(RepoFile("Runtime/Platform/Android"), "*.cs", SearchOption.AllDirectories))
            {
                var src = StripComments(File.ReadAllText(path));
                // The receiver may be named anything: find the locals bound to the UnityWrapper class, and require that
                // every mention of the class is such a binding so a differently spelled call cannot slip past.
                const string binding = @"\bvar\s+(\w+)\s*=\s*new\s+AndroidJavaClass\(\s*""com\.bugsee\.unity\.UnityWrapper""\s*\)";
                var receivers = Regex.Matches(src, binding).Cast<Match>().Select(r => r.Groups[1].Value).Distinct().ToList();
                Assert.That(Regex.Matches(src, @"""com\.bugsee\.unity\.UnityWrapper""").Count,
                    Is.EqualTo(Regex.Matches(src, binding).Count),
                    Path.GetFileName(path) + ": UnityWrapper is referenced other than as 'var x = new AndroidJavaClass(...)'");
                if (receivers.Count == 0) continue;
                var callRe = new Regex(@"\b(?:" + string.Join("|", receivers) + @")\s*\??\s*\.\s*CallStatic(?:<[^>]+>)?\s*\(");
                foreach (Match m in callRe.Matches(src))
                {
                    var args = SplitTopLevel(BalancedArgs(src, m.Index + m.Length));
                    var nameMatch = Regex.Match(args[0], "^\"(\\w+)\"$");
                    Assert.That(nameMatch.Success, Is.True,
                        Path.GetFileName(path) + ": CallStatic with a non-literal method name cannot be checked: " + args[0]);
                    calls.Add((nameMatch.Groups[1].Value, args.Length - 1, Path.GetFileName(path)));
                }
            }

            return calls;
        }

        [Test]
        public void Every_UnityWrapper_JNI_call_reaches_a_public_static_java_method_of_the_same_arity()
        {
            var statics = JavaPublicStatics();
            var calls = UnityWrapperCalls();

            Assert.That(statics.Select(s => s.Name), Does.Contain("refineContext").And.Contain("channelLog"));
            Assert.That(calls.Select(c => c.name), Does.Contain("refineContext").And.Contain("channelLog"));
            Assert.That(calls.Count, Is.GreaterThanOrEqualTo(6));

            var problems = new List<string>();
            foreach (var (name, argc, file) in calls)
            {
                if (!statics.Any(s => s.Name == name && s.Params.Length == argc))
                    problems.Add(file + ": wrapper.CallStatic(\"" + name + "\") with " + argc +
                                 " argument(s); Java has " + string.Join(" | ",
                                     statics.Where(s => s.Name == name).Select(s => s.Params.Length + " param(s)").DefaultIfEmpty("no such method")));
            }

            Assert.That(problems, Is.Empty, string.Join("\n", problems));
        }

        [Test]
        public void Java_classes_constructed_from_managed_code_have_a_constructor_of_that_arity()
        {
            var java = StripComments(ReadAll("Plugins/Android", "*.java"));
            var cs = StripComments(ReadAll("Runtime/Platform/Android", "*.cs"));

            var m = Regex.Match(cs, "new AndroidJavaObject\\(\\s*\"com\\.bugsee\\.unity\\.UnityManagedException\"\\s*");
            Assert.That(m.Success, Is.True, "UnityManagedException is no longer constructed from managed code");
            // The class name is the first argument; the constructor arguments are the rest (nested calls included).
            int argc = SplitTopLevel(BalancedArgs(cs, cs.IndexOf('(', m.Index) + 1)).Length - 1;

            var ctor = Regex.Match(java, @"public\s+UnityManagedException\s*\(");
            Assert.That(ctor.Success, Is.True);
            Assert.That(SplitTopLevel(BalancedArgs(java, ctor.Index + ctor.Length)).Length, Is.EqualTo(argc));
        }
    }
}
