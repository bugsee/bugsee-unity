using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    /// <summary>
    /// The typed half of AndroidSdkBindingTests (#205): what an argument expression is at run time (a Java class, a
    /// primitive, a string), resolved through declarations, initialisers, helper methods and the receiver contracts, the
    /// receiver of an instance Call resolved to the SDK class it is, the Java side of UnityWrapper parsed from
    /// Plugins/Android/*.java (its public statics are checked like an SDK class, its overrides against the SDK
    /// interfaces, its calls into the SDK against the snapshot), and the enum constants read by name.
    /// </summary>
    public partial class AndroidSdkBindingTests
    {
        // ---- the shared environment of the checks ---------------------------------------------------------------------------

        sealed class Env
        {
            public Dictionary<string, string> Sources;
            public Dictionary<string, Proxy> Proxies;
            public Dictionary<string, string> Returns;
            public Dictionary<string, JavaClass> Manifest; // the SDK snapshot and the Java classes of Plugins/Android
            public Dictionary<string, string[]> Enums; // C# enum name -> member names
            public Dictionary<string, string> Members; // C# property / field name -> its type, when one type only
            public Dictionary<string, string> ClassMembers; // "Class.Member" -> its type (static members of the runtime's own classes)
            public Dictionary<string, List<Binding>> Bindings;
            public Dictionary<string, string> FieldClass;
        }

        static Env _env;

        static Env BindingEnv()
        {
            if (_env != null) return _env;
            var sources = Sources();
            var bindings = Bindings(sources);
            var fieldBindings = bindings.Values.SelectMany(b => b).Where(b => b.Var.StartsWith("_")).GroupBy(b => b.Var).ToList();
            var enums = new Dictionary<string, string[]>();
            var members = new Dictionary<string, HashSet<string>>();
            var classMembers = new Dictionary<string, string>();
            foreach (var path in Directory.GetFiles(RepoFile("Runtime"), "*.cs", SearchOption.AllDirectories))
            {
                var text = StripComments(File.ReadAllText(path));
                foreach (Match c in Regex.Matches(text, @"\b(?:static\s+)?class\s+(\w+)\b[^{;]*\{"))
                    foreach (Match d in Regex.Matches(ClassBody(text, c.Index), @"\b(?:public|internal)\s+(?:(?:static|readonly|const)\s+)+([\w<>\[\]?.]+)\s+(\w+)\s*(?:=|\{|=>|;)"))
                        classMembers[c.Groups[1].Value + "." + d.Groups[2].Value] = d.Groups[1].Value;
                foreach (Match e in Regex.Matches(text, @"\benum\s+(\w+)\s*(?::\s*\w+)?\s*\{([^}]*)\}"))
                    enums[e.Groups[1].Value] = e.Groups[2].Value.Split(',')
                        .Select(x => Regex.Replace(x, @"\[[^\]]*\]", "").Split('=')[0].Trim()).Where(x => x.Length > 0).ToArray();
                foreach (Match d in Regex.Matches(text, @"\b(?:public|internal|private|protected)\s+(?:(?:static|readonly|override|virtual)\s+)*([\w<>\[\]?.]+)\s+(\w+)\s*(?:\{|=>|=|;)"))
                {
                    if (NotATypeWord.Contains(d.Groups[1].Value)) continue;
                    if (!members.TryGetValue(d.Groups[2].Value, out var set)) members[d.Groups[2].Value] = set = new HashSet<string>();
                    set.Add(d.Groups[1].Value);
                }
            }

            return _env = new Env
            {
                Sources = sources,
                Proxies = Proxies(sources),
                Returns = MethodReturnTypes(),
                Manifest = FullManifest(),
                Enums = enums,
                ClassMembers = classMembers,
                Members = members.Where(m => m.Value.Count == 1).ToDictionary(m => m.Key, m => m.Value.First()),
                Bindings = bindings,
                FieldClass = fieldBindings.ToDictionary(g => g.Key, g => g.First().Class),
            };
        }

        sealed class Site
        {
            public Env E;
            public string File; // the path
            public string Src;
        }

        static Site SiteOf(string path) => new Site { E = BindingEnv(), File = path, Src = BindingEnv().Sources[path] };

        // The classes whose objects are only ever a C# wrapper's `_native` (see ReceiverContracts), by field name.
        static readonly HashSet<string> ContractFields = new HashSet<string> { "_native", "_report", "_span", "_attachment", "span" };

        /// <summary>The SDK classes of the receiver contract of the wrapper class that contains `at`, or null.</summary>
        static string[] ContractClassesAt(Site s, int at)
        {
            foreach (var rc in ReceiverContracts)
            {
                var cm = Regex.Match(s.Src, @"\bclass\s+" + rc.Key + @"\b");
                if (!cm.Success) continue;
                var open = s.Src.IndexOf('{', cm.Index);
                if (at > open && at < open + ClassBody(s.Src, cm.Index).Length)
                    return rc.Value.Select(c => "com.bugsee.library.contracts." + c).ToArray();
            }

            return null;
        }

        // ---- the Java side: Plugins/Android/*.java parsed into the same model as the snapshot ---------------------------------

        sealed class JavaFile
        {
            public string Name;
            public string Text; // comments and string contents removed
            public string Raw; // comments removed, string literals kept
            public string Package;
            public Dictionary<string, string> Imports = new Dictionary<string, string>();
            public List<string> ImportLines = new List<string>();
        }

        static readonly HashSet<string> JavaLang = new HashSet<string>
            { "String", "Object", "Runnable", "Throwable", "Exception", "Integer", "Long", "Boolean", "Class", "Thread", "CharSequence" };

        static string StripJava(string s)
        {
            s = StripComments(s);
            return Regex.Replace(s, "\"(?:[^\"\\\\\\n]|\\\\.)*\"", "\"\"");
        }

        static List<JavaFile> JavaFiles()
        {
            var files = new List<JavaFile>();
            foreach (var path in Directory.GetFiles(RepoFile("Plugins/Android"), "*.java").OrderBy(f => f))
            {
                var f = new JavaFile { Name = Path.GetFileName(path), Text = StripJava(File.ReadAllText(path)), Raw = StripComments(File.ReadAllText(path)) };
                f.Package = Regex.Match(f.Text, @"\bpackage\s+([\w.]+)\s*;").Groups[1].Value;
                foreach (Match m in Regex.Matches(f.Text, @"\bimport\s+([\w.$]+)\s*;"))
                {
                    f.ImportLines.Add(m.Groups[1].Value);
                    f.Imports[m.Groups[1].Value.Substring(m.Groups[1].Value.LastIndexOf('.') + 1)] = m.Groups[1].Value;
                }

                files.Add(f);
            }

            return files;
        }

        /// <summary>A Java type name as written in the file, as the erased dotted name the snapshot uses.</summary>
        static string JavaTypeName(string written, JavaFile f, ICollection<string> sameFileClasses)
        {
            var t = Erase(Regex.Replace(written, @"\b(final)\b|@\w+", " ")).Trim();
            var arrays = "";
            while (t.EndsWith("[]")) { arrays += "[]"; t = t.Substring(0, t.Length - 2).Trim(); }
            t = t.Replace("...", "");
            string full;
            if (JavaPrimitives.Contains(t) || t == "void") full = t;
            else if (t.Contains(".") && char.IsLower(t[0])) full = t;
            else if (f.Imports.TryGetValue(t.Split('.')[0], out var imported)) full = imported + t.Substring(t.Split('.')[0].Length);
            else if (JavaLang.Contains(t)) full = "java.lang." + t;
            else if (sameFileClasses.Contains(t)) full = f.Package + "." + t;
            else full = t;
            return full + arrays;
        }

        static string[] JavaTopLevelFlat(string text, int open)
        {
            // The member declarations of one class body: nested bodies (methods, anonymous classes) emptied to {}.
            var body = ClassBody(text, open);
            var sb = new System.Text.StringBuilder();
            int depth = 0;
            foreach (var c in body)
            {
                if (c == '{') { if (depth == 0) sb.Append('{'); depth++; }
                else if (c == '}') { depth--; if (depth == 0) sb.Append('}'); }
                else if (depth == 0) sb.Append(c);
            }

            return new[] { sb.ToString() };
        }

        static readonly Regex JavaMethodRe = new Regex(
            @"((?:@\w+(?:\([^)]*\))?\s+)*)((?:(?:public|protected|private|static|final|synchronized|abstract|native|default)\s+)*)((?:<[^>]+>\s*)?[\w.]+(?:<[^;(){}]*?>)?(?:\[\])*)\s+(\w+)\s*\(([^)]*)\)\s*(?:throws\s+[\w., ]+)?\s*(\{\}|;)");

        static Dictionary<string, JavaClass> _javaClasses;

        /// <summary>The top-level classes of Plugins/Android/*.java: public and package-visible methods with their types.</summary>
        static Dictionary<string, JavaClass> JavaSourceClasses()
        {
            if (_javaClasses != null) return _javaClasses;
            var result = new Dictionary<string, JavaClass>();
            var files = JavaFiles();
            List<(string name, string extends, string implements, int index)> TopsOf(JavaFile file)
            {
                var found = new List<(string name, string extends, string implements, int index)>();
                foreach (Match m in Regex.Matches(file.Text, @"\b(?:public\s+|final\s+|abstract\s+)*(class|interface)\s+(\w+)(?:\s+extends\s+([\w.<>, ]+?))?(?:\s+implements\s+([\w.<>, ]+?))?\s*\{"))
                {
                    var depth = file.Text.Substring(0, m.Index).Count(c => c == '{') - file.Text.Substring(0, m.Index).Count(c => c == '}');
                    if (depth == 0) found.Add((m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value, m.Index));
                }

                return found;
            }

            // A class of the same package needs no import, whichever file declares it.
            var packageClasses = files.GroupBy(x => x.Package).ToDictionary(g => g.Key, g => g.SelectMany(x => TopsOf(x).Select(t => t.name)).ToList());
            foreach (var f in files)
            {
                var tops = TopsOf(f);
                var names = packageClasses[f.Package];
                foreach (var t in tops)
                {
                    var jc = new JavaClass { Name = f.Package + "." + t.name };
                    foreach (var list in new[] { t.extends, t.implements })
                        jc.Supers.AddRange(SplitParams(list).Select(x => JavaTypeName(x, f, names)).Where(x => x.Length > 0));
                    var flat = JavaTopLevelFlat(f.Text, t.index)[0];
                    foreach (Match fm in Regex.Matches(flat, @"((?:(?:public|protected|private|static|final|volatile)\s+)*)([\w.]+(?:<[^;=(){}]*?>)?(?:\[\])*)\s+(\w+)\s*(?:=[^;]*)?;"))
                        if (fm.Groups[2].Value != "return")
                            jc.Fields[fm.Groups[3].Value] = (JavaTypeName(fm.Groups[2].Value, f, names), Regex.IsMatch(fm.Groups[1].Value, @"\bstatic\b"));
                    foreach (Match m in JavaMethodRe.Matches(flat))
                    {
                        var ret = m.Groups[3].Value.Trim();
                        if (ret == "new" || ret == "return" || ret == "else" || ret == "throw") continue;
                        var mods = m.Groups[2].Value;
                        var args = SplitParams(m.Groups[5].Value);
                        if (m.Groups[4].Value == t.name)
                        {
                            // A constructor: the regex reads its modifier as the return type. Kept like the snapshot's: Ret null, named by the class.
                            jc.Methods.Add(new JavaMethod
                            {
                                Name = jc.Name, Ret = null, Public = ret == "public" || Regex.IsMatch(mods, @"\bpublic\b"),
                                Params = args.Select(a => JavaTypeName(Regex.Replace(a, @"\s+\w+$", ""), f, names)).ToArray(),
                                VarArgs = args.Length > 0 && args[args.Length - 1].Contains("..."),
                            });
                            continue;
                        }

                        jc.Methods.Add(new JavaMethod
                        {
                            Name = m.Groups[4].Value,
                            Static = Regex.IsMatch(mods, @"\bstatic\b"),
                            Abstract = Regex.IsMatch(mods, @"\babstract\b"),
                            Public = Regex.IsMatch(mods, @"\bpublic\b"),
                            Override = m.Groups[1].Value.Contains("@Override"),
                            Ret = JavaTypeName(Regex.Replace(ret, @"^<[^>]*>\s*", ""), f, names),
                            Params = args.Select(a => JavaTypeName(Regex.Replace(a, @"\s+\w+$", ""), f, names)).ToArray(),
                            VarArgs = args.Length > 0 && args[args.Length - 1].Contains("..."),
                        });
                    }

                    result[jc.Name] = jc;
                }
            }

            return _javaClasses = result;
        }

        static Dictionary<string, JavaClass> _full;

        /// <summary>The SDK snapshot plus the Java classes the package ships (UnityWrapper & co).</summary>
        static Dictionary<string, JavaClass> FullManifest()
        {
            if (_full != null) return _full;
            var all = new Dictionary<string, JavaClass>(Manifest());
            foreach (var kv in JavaSourceClasses()) all[kv.Key] = kv.Value;
            foreach (var kv in JdkClasses()) all[kv.Key] = kv.Value;
            return _full = all;
        }

        static JavaMethod Jdk(string ret, string name, params string[] parameters) =>
            new JavaMethod { Name = name, Ret = ret, Params = parameters };

        /// <summary>
        /// The few JDK supertypes the SDK and the Java wrappers derive from, with the members the bridge relies on (the snapshot
        /// only lists what the SDK declares): java.lang.Object and Enum for the calls on contract objects, Throwable and Exception
        /// for UnityManagedException.
        /// </summary>
        static Dictionary<string, JavaClass> JdkClasses() => new Dictionary<string, JavaClass>
        {
            { "java.lang.Object", new JavaClass { Name = "java.lang.Object", Methods = { Jdk("java.lang.String", "toString"), Jdk("int", "hashCode"),
                Jdk("boolean", "equals", "java.lang.Object"), Jdk("java.lang.Class", "getClass") } } },
            { "java.lang.Enum", new JavaClass { Name = "java.lang.Enum", Methods = { Jdk("java.lang.String", "name"), Jdk("int", "ordinal") } } },
            { "java.lang.Throwable", new JavaClass { Name = "java.lang.Throwable", Methods = { Jdk("java.lang.Throwable", "fillInStackTrace"),
                Jdk("java.lang.String", "getMessage") } } },
            { "java.lang.Exception", new JavaClass { Name = "java.lang.Exception", Supers = { "java.lang.Throwable" } } },
        };

        // ---- subtyping --------------------------------------------------------------------------------------------------------

        // JDK classes the bridge builds and hands over: what they are, for the parameter types they must bind to.
        static readonly Dictionary<string, string[]> JdkSupers = new Dictionary<string, string[]>
        {
            { "java.util.ArrayList", new[] { "java.util.List", "java.util.Collection", "java.lang.Iterable" } },
            { "java.util.HashMap", new[] { "java.util.Map" } },
            { "java.lang.String", new[] { "java.io.Serializable", "java.lang.Comparable", "java.lang.CharSequence" } },
            { "java.lang.Boolean", new[] { "java.io.Serializable", "java.lang.Comparable" } },
            { "java.lang.Integer", new[] { "java.lang.Number", "java.io.Serializable", "java.lang.Comparable" } },
            { "java.lang.Long", new[] { "java.lang.Number", "java.io.Serializable", "java.lang.Comparable" } },
            { "java.lang.Float", new[] { "java.lang.Number", "java.io.Serializable", "java.lang.Comparable" } },
            { "java.lang.Double", new[] { "java.lang.Number", "java.io.Serializable", "java.lang.Comparable" } },
            { "java.lang.Byte", new[] { "java.lang.Number", "java.io.Serializable", "java.lang.Comparable" } },
            { "java.lang.Short", new[] { "java.lang.Number", "java.io.Serializable", "java.lang.Comparable" } },
            { "java.lang.Number", new[] { "java.io.Serializable" } },
            { "java.util.LinkedHashMap", new[] { "java.util.HashMap", "java.util.Map" } },
        };

        static bool Reaches(string cls, string target, Dictionary<string, JavaClass> manifest, int depth = 0)
        {
            if (cls == target || target == "java.lang.Object") return true;
            if (depth > 8) return false;
            var supers = new List<string>();
            if (manifest.TryGetValue(cls, out var jc)) supers.AddRange(jc.Supers);
            if (JdkSupers.TryGetValue(cls, out var jdk)) supers.AddRange(jdk);
            return supers.Any(s => Reaches(s, target, manifest, depth + 1));
        }

        static Func<string, bool> ClassPred(IEnumerable<string> classes, Dictionary<string, JavaClass> manifest)
        {
            var list = classes.ToList();
            return p => IsReference(p) && list.All(c => Reaches(c, p, manifest));
        }

        /// <summary>The java types a C# type binds to (Unity maps it by its run-time type); null for a struct, an object or an unknown type.</summary>
        static Func<string, bool> CsTypePredicate(string type, Dictionary<string, Proxy> proxies, Dictionary<string, JavaClass> manifest)
        {
            Func<string, bool> Prim(string name) => p => p == name;
            type = Regex.Replace(type.Trim().TrimEnd('?'), "<.*>$", "");
            switch (type)
            {
                case "bool": return Prim("boolean");
                case "int": return Prim("int");
                case "long": return Prim("long");
                case "float": return Prim("float");
                case "double": return Prim("double");
                case "short": return Prim("short");
                case "char": return Prim("char");
                case "sbyte": return Prim("byte");
                case "string": return p => Reaches("java.lang.String", p, manifest);
                case "int[]": return Prim("int[]");
                case "long[]": return Prim("long[]");
                case "float[]": return Prim("float[]");
                case "double[]": return Prim("double[]");
                case "bool[]": return Prim("boolean[]");
                case "sbyte[]": return Prim("byte[]");
                case "byte[]": return Prim("byte[]"); // Unity maps a byte[] to the Java byte[] too
                case "string[]": return Prim("java.lang.String[]");
                case "AndroidJavaObject":
                case "AndroidJavaClass": return LooseObject;
                case "AndroidJavaRunnable": return p => p == "java.lang.Runnable" || p == "java.lang.Object";
            }

            if (proxies.TryGetValue(type, out var proxy))
                return p => p == "java.lang.Object" || InterfaceReaches(proxy.Interface, p, manifest);
            // A C# enum has no JNI signature: Unity throws "Unknown signature" for it, so it must be cast to its integer.
            if (BindingEnv().Enums.ContainsKey(type)) return p => false;
            return null;
        }

        /// <summary>
        /// "Some Java object": all the text tells about an AndroidJavaObject whose class is not resolved. It is a distinct instance so
        /// the checks can count these arguments and hold them to a named allow-list (LooseObjectArguments).
        /// </summary>
        static readonly Func<string, bool> LooseObject = p => IsReference(p);

        /// <summary>A null reference binds to any reference parameter (Unity cannot pick an overload by it, hence the casts).</summary>
        static readonly Func<string, bool> NullObject = p => IsReference(p);

        // ---- expression typing ------------------------------------------------------------------------------------------------

        /// <summary>The index of `op` at bracket depth 0 outside strings, or -1. `?` is a conditional only (not ?? / ?. / T?).</summary>
        static int TopLevel(string e, string op)
        {
            int depth = 0;
            char quote = '\0';
            for (int i = 0; i < e.Length; i++)
            {
                char c = e[i];
                if (quote != '\0')
                {
                    if (c == '\\') i++;
                    else if (c == quote) quote = '\0';
                    continue;
                }

                if (c == '"' || c == '\'') { quote = c; continue; }
                if (c == '(' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == ']' || c == '}') depth--;
                else if (depth == 0 && string.CompareOrdinal(e, i, op, 0, op.Length) == 0)
                {
                    if (op == "?" && (i + 1 < e.Length && (e[i + 1] == '?' || e[i + 1] == '.') || i > 0 && e[i - 1] == '?')) continue;
                    if (op == "<" && i + 1 < e.Length && e[i + 1] == '<') continue;
                    if ((op == "<" || op == ">") && i + 1 < e.Length && e[i + 1] == '=') continue;
                    return i;
                }
            }

            return -1;
        }

        /// <summary>The initialiser after an `=`: up to the first `;` or unmatched `)` / `}`.</summary>
        static string Initializer(string src, int from)
        {
            int depth = 0, i = from;
            char quote = '\0';
            for (; i < src.Length; i++)
            {
                char c = src[i];
                if (quote != '\0')
                {
                    if (c == '\\') i++;
                    else if (c == quote) quote = '\0';
                    continue;
                }

                if (c == '"' || c == '\'') quote = c;
                else if (c == '(' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == ']' || c == '}') { if (depth == 0) break; depth--; }
                else if (c == ';' && depth == 0) break;
            }

            return src.Substring(from, i - from).Trim();
        }

        sealed class Decl
        {
            public string Type;
            public string Init; // the initialiser, or null
            public int Index;
        }

        /// <summary>The last declaration of a local, parameter or field before `at`; for `value`, the property whose setter it is.</summary>
        static Decl FindDecl(string name, Site s, int at)
        {
            var before = s.Src.Substring(0, Math.Min(at, s.Src.Length));
            var decl = new Regex(@"(?<![\w.])([\w.]+(?:<[^<>]*>)?\??(?:\[\])?)\s+" + name + @"\s*(=(?!=)|,|\)|;|\bin\b)");
            var last = decl.Matches(before).Cast<Match>().LastOrDefault(d => !NotATypeWord.Contains(d.Groups[1].Value));
            if (name == "value")
            {
                var accessor = Regex.Matches(before, @"\bset\s*(?:=>|\{)").Cast<Match>().LastOrDefault();
                if (accessor != null && (last == null || accessor.Index > last.Index))
                {
                    var prop = Regex.Matches(before.Substring(0, accessor.Index),
                        @"\b(?:public|internal|private|protected)\s+(?:(?:static|override|virtual)\s+)*([\w<>\[\]?.]+)\s+\w+\s*\{").Cast<Match>().LastOrDefault();
                    if (prop != null) return new Decl { Type = prop.Groups[1].Value, Index = accessor.Index };
                    return null;
                }
            }

            if (last == null) return null;
            return new Decl
            {
                Type = last.Groups[1].Value,
                Init = last.Groups[2].Value == "=" ? Initializer(s.Src, last.Index + last.Length) : null,
                Index = last.Index,
            };
        }

        static string ClassOfReceiver(Site s, string receiver, int at)
        {
            var prior = s.E.Bindings[s.File].Where(b => b.Var == receiver && b.Index < at).LastOrDefault();
            string cls = prior?.Class;
            if (cls == null) s.E.FieldClass.TryGetValue(receiver, out cls);
            return string.IsNullOrEmpty(cls) ? null : cls;
        }

        static string[] ReturnClasses(Site s, string cls, string name, bool isStatic, Func<int, bool> arity)
        {
            if (!s.E.Manifest.TryGetValue(cls, out var jc)) return null;
            var methods = new List<JavaMethod>();
            if (isStatic) methods.AddRange(jc.Methods.Where(m => m.Static));
            else AddWithSupers(cls, s.E.Manifest, methods);
            var returns = methods.Where(m => m.Name == name && arity(m.Params.Length) && m.Ret != null).Select(m => m.Ret).Distinct().ToList();
            if (returns.Count == 0 || returns.Any(r => !IsReference(r) || r == "java.lang.Object")) return null;
            return returns.ToArray();
        }

        static readonly Regex ReceiverCallRe = new Regex(@"^(\w+)\s*\??\s*\.\s*(CallStatic|Call|GetStatic|Get)<(\w+)>\s*\(");

        /// <summary>
        /// The run-time Java classes an AndroidJavaObject-valued expression can be (every one must fit the parameter);
        /// null when the text does not tell.
        /// </summary>
        static string[] Origin(string expr, Site s, int at, int depth = 0)
        {
            expr = expr.Trim();
            if (depth > 14 || expr.Length == 0) return null;
            var condition = TopLevel(expr, "?");
            if (condition > 0)
            {
                var rest = expr.Substring(condition + 1);
                var colon = TopLevel(rest, ":");
                if (colon < 0) return null;
                var union = new List<string>();
                foreach (var branch in new[] { rest.Substring(0, colon), rest.Substring(colon + 1) })
                {
                    var b = branch.Trim();
                    if (b == "null" || b == "(AndroidJavaObject)null") continue;
                    var o = Origin(b, s, at, depth + 1);
                    if (o == null) return null;
                    union.AddRange(o);
                }

                return union.Count == 0 ? null : union.Distinct().ToArray();
            }

            var coalesce = TopLevel(expr, "??");
            if (coalesce > 0)
            {
                // Either operand can reach the JNI call: both classes must fit. An unresolved fallback leaves the left one.
                var left = Origin(expr.Substring(0, coalesce), s, at, depth + 1);
                var fallback = expr.Substring(coalesce + 2).Trim();
                if (left == null || fallback == "null" || fallback == "(AndroidJavaObject)null") return left;
                var right = Origin(fallback, s, at, depth + 1);
                return right == null ? left : left.Concat(right).Distinct().ToArray();
            }

            var asCast = Regex.Match(expr, @"^(.+?)\s+as\s+\w+$");
            if (asCast.Success) return Origin(asCast.Groups[1].Value, s, at, depth + 1);
            if (expr.StartsWith("(") && expr.EndsWith(")") && TopLevel(expr.Substring(1, expr.Length - 2), ",") < 0 && !Regex.IsMatch(expr, @"^\(\w+\)"))
                return Origin(expr.Substring(1, expr.Length - 2), s, at, depth + 1);
            var ctor = Regex.Match(expr, @"^new\s+AndroidJavaObject\(\s*""([\w.$]+)""");
            if (ctor.Success) return new[] { ctor.Groups[1].Value };

            var recvCall = ReceiverCallRe.Match(expr);
            if (recvCall.Success && recvCall.Groups[3].Value == "AndroidJavaObject")
            {
                var args = SplitArgs(BalancedArgs(expr, recvCall.Length));
                var kind = recvCall.Groups[2].Value;
                var name = args.Length > 0 ? Resolve(args[0], s.Src) : null;
                if (kind == "CallStatic")
                {
                    var cls = ClassOfReceiver(s, recvCall.Groups[1].Value, at);
                    return cls == null || name == null ? null : ReturnClasses(s, cls, name, true, n => n == args.Length - 1);
                }

                if (kind == "GetStatic")
                {
                    // The static fields of the class (a computed name is one of the SpanStatus constants).
                    var cls = ClassOfReceiver(s, recvCall.Groups[1].Value, at);
                    if (cls == null || !s.E.Manifest.TryGetValue(cls, out var jc)) return null;
                    var fields = jc.Fields.Where(f => f.Value.isStatic && (name == null || f.Key == name)).Select(f => f.Value.type).Distinct().ToArray();
                    return fields.Length == 0 || fields.Any(f => !IsReference(f)) ? null : fields;
                }

                if (kind == "Call")
                {
                    var receivers = Origin(recvCall.Groups[1].Value, s, at, depth + 1);
                    if (receivers == null || name == null) return null;
                    var found = receivers.Select(r => ReturnClasses(s, r, name, false, n => n == args.Length - 1)).ToList();
                    return found.Any(f => f == null) ? null : found.SelectMany(f => f).Distinct().ToArray();
                }
            }

            var viaEnum = Regex.Match(expr, @"^EnumFromInt\(\s*(\w+)\s*,\s*""(\w+)""");
            if (viaEnum.Success)
            {
                var cls = Resolve(viaEnum.Groups[1].Value, s.Src);
                return cls == null ? null : ReturnClasses(s, cls, viaEnum.Groups[2].Value, true, n => n == 1 || n == 2);
            }

            var native = Regex.Match(expr, @"^(\w+)\.Native$");
            if (native.Success)
            {
                var type = CsTypeOf(native.Groups[1].Value, s, at, 0);
                if (type != null && ReceiverContracts.TryGetValue(type, out var contract))
                    return contract.Select(c => "com.bugsee.library.contracts." + c).ToArray();
                return null;
            }

            var helper = Regex.Match(expr, @"^(?:[\w.]*\.)?(\w+)\s*\(");
            if (helper.Success && helper.Groups[1].Value != "CallStatic" && helper.Groups[1].Value != "Call")
            {
                var returned = HelperReturnOrigins(helper.Groups[1].Value, s, depth + 1);
                if (returned != null) return returned;
            }

            if (Regex.IsMatch(expr, @"^\w+$"))
            {
                if (ContractFields.Contains(expr))
                {
                    var contract = ContractClassesAt(s, at);
                    if (contract != null) return contract;
                }

                var d = FindDecl(expr, s, at);
                if (d?.Init != null) return Origin(d.Init, s, d.Index, depth + 1);
            }

            return null;
        }

        static readonly HashSet<string> Resolving = new HashSet<string>();

        /// <summary>The union of Origin over the `return` statements of an AndroidJavaObject helper declared in the same file.</summary>
        static string[] HelperReturnOrigins(string name, Site s, int depth)
        {
            // The helper is declared in this file or, when the name is unique among the Android sources, in another one.
            var found = new List<(Site site, Match def)>();
            foreach (var path in s.E.Sources.Keys)
            {
                var site = path == s.File ? s : SiteOf(path);
                foreach (Match d in Regex.Matches(site.Src, @"\bAndroidJavaObject\s+" + name + @"\s*\([^)]*\)\s*(\{|=>)"))
                    found.Add((site, d));
            }

            if (found.Count == 0) return null;
            // A name declared twice (AndroidBridge.Box forwards to AndroidJavaConverters.Box) is every definition: all must fit.
            var origins = new List<string>();
            foreach (var (hs, def) in found)
            {
                // A forwarding definition (Box => AndroidJavaConverters.Box) must not resolve itself again.
                var key = hs.File + ":" + def.Index;
                if (!Resolving.Add(key)) continue;
                try
                {
                var bodyStart = def.Index + def.Length;
                string body;
                if (def.Groups[1].Value == "{") body = ClassBody(hs.Src, def.Index + def.Length - 1);
                else body = "return " + Initializer(hs.Src, bodyStart) + ";";
                var any = false;
                foreach (Match r in Regex.Matches(body, @"\breturn\s+([^;]+);"))
                {
                    if (r.Groups[1].Value.Trim() == "null") continue;
                    var at = def.Index + (def.Groups[1].Value == "{" ? def.Length + r.Index : 0);
                    var o = Origin(r.Groups[1].Value, hs, at, depth + 1);
                    if (o == null) return null;
                    origins.AddRange(o);
                    any = true;
                }

                if (!any) return null;
                }
                finally { Resolving.Remove(key); }
            }

            return origins.Count == 0 ? null : origins.Distinct().ToArray();
        }

        /// <summary>The declared C# type of a simple expression (a local through its declaration, `x as T`, `(T)x`, `new T()`).</summary>
        static string CsTypeOf(string expr, Site s, int at, int depth)
        {
            expr = expr.Trim();
            if (depth > 4) return null;
            var asCast = Regex.Match(expr, @"^.+\s+as\s+(\w+)$");
            if (asCast.Success) return asCast.Groups[1].Value;
            var cast = Regex.Match(expr, @"^\((\w+)\)\s*[\w(]");
            if (cast.Success) return cast.Groups[1].Value;
            var ctor = Regex.Match(expr, @"^new\s+(\w+)");
            if (ctor.Success) return ctor.Groups[1].Value;
            if (!Regex.IsMatch(expr, @"^\w+$")) return null;
            var d = FindDecl(expr, s, at);
            if (d == null) return null;
            if (d.Type != "var") return d.Type;
            return d.Init == null ? null : CsTypeOf(d.Init, s, d.Index, depth + 1);
        }

        /// <summary>
        /// The java parameter types an argument can bind to, from its C# type or, for an AndroidJavaObject, the Java class it is;
        /// null when the text does not tell it.
        /// </summary>
        static Func<string, bool> ArgType(string expr, Site s, int at, int depth = 0)
        {
            expr = expr.Trim();
            if (depth > 6 || expr.Length == 0) return null;
            var manifest = s.E.Manifest;
            Func<string, bool> Prim(string name) => p => p == name;
            Func<string, bool> Str = p => Reaches("java.lang.String", p, manifest);
            Func<string, Func<string, bool>> FromCsType = type => CsTypePredicate(type, s.E.Proxies, manifest);
            Func<string, bool> FromOrigin(string[] o) => o == null ? null : ClassPred(o, manifest);

            if (expr == "(AndroidJavaObject)null") return NullObject;
            if (expr == "true" || expr == "false") return Prim("boolean");
            if (Regex.IsMatch(expr, @"^-?\d+$")) return Prim("int");
            if (Regex.IsMatch(expr, @"^-?\d+[lL]$")) return Prim("long");
            if (Regex.IsMatch(expr, @"^-?[\d.]+[fF]$")) return Prim("float");
            if (Regex.IsMatch(expr, @"^-?\d*\.\d+$")) return Prim("double");
            if (Regex.IsMatch(expr, "^\"[^\"]*\"$") || Regex.IsMatch(expr, @"^nameof\(") || expr == "string.Empty") return Str;
            // A conditional is whichever branch is taken at run time: both must bind; a null branch binds anything.
            var q = TopLevel(expr, "?");
            if (q > 0)
            {
                var rest = expr.Substring(q + 1);
                var colon = TopLevel(rest, ":");
                if (colon < 0) return null;
                var branches = new[] { rest.Substring(0, colon), rest.Substring(colon + 1) }
                    .Where(b => b.Trim() != "null" && b.Trim() != "(AndroidJavaObject)null" && b.Trim() != "(string)null")
                    .Select(b => ArgType(b, s, at, depth + 1)).ToList();
                if (branches.Count == 0 || branches.Any(b => b == null)) return null;
                return p => branches.All(b => b(p));
            }

            var coalesce = TopLevel(expr, "??");
            if (coalesce > 0)
            {
                var left = ArgType(expr.Substring(0, coalesce), s, at, depth + 1);
                var fallback = expr.Substring(coalesce + 2).Trim();
                if (left == null || fallback == "null" || fallback == "(AndroidJavaObject)null" || fallback == "(string)null") return left;
                var right = ArgType(fallback, s, at, depth + 1);
                // Both operands can reach the JNI call; an unresolved fallback leaves the left one.
                return right == null ? left : (Func<string, bool>)(p => left(p) && right(p));
            }
            if (expr.StartsWith("!") || TopLevel(expr, "||") >= 0 || TopLevel(expr, "&&") >= 0 || TopLevel(expr, "==") >= 0
                || TopLevel(expr, "!=") >= 0 || TopLevel(expr, "<=") >= 0 || TopLevel(expr, ">=") >= 0
                || Regex.IsMatch(expr, @"\sis\s")) return Prim("boolean");
            if (TopLevel(expr, " < ") >= 0 || TopLevel(expr, " > ") >= 0) return Prim("boolean");
            // A cast types the expression only when it covers all of it: `(int)x + 1` is not a plain int.
            var cast = Regex.Match(expr, @"^\((\w+(?:\[\])?)\)\s*[\w(]");
            if (cast.Success)
                return new[] { "+", " - ", "*", "/", "%", " & ", " | ", " ^ ", "<<", ">>" }.Any(op => TopLevel(expr, op) >= 0)
                    ? null : FromCsType(cast.Groups[1].Value);
            if (Regex.IsMatch(expr, @"\.ToString\(\)$")) return Str;
            if (expr.StartsWith("(") && expr.EndsWith(")") && TopLevel(expr.Substring(1, expr.Length - 2), ",") < 0 && !Regex.IsMatch(expr, @"^\(\w+\)"))
                return ArgType(expr.Substring(1, expr.Length - 2), s, at, depth + 1);

            var ctor = Regex.Match(expr, @"^new\s+(\w+(?:\[\])?)(?:<[^>]*>)?\s*[\(\[]");
            if (ctor.Success)
            {
                if (ctor.Groups[1].Value == "AndroidJavaObject") return FromOrigin(Origin(expr, s, at, depth + 1)) ?? LooseObject;
                return FromCsType(ctor.Groups[1].Value);
            }

            if (Regex.IsMatch(expr, @"^\w+$"))
            {
                var d = FindDecl(expr, s, at);
                if (d == null) return null;
                var viaClass = d.Type == "AndroidJavaObject" || d.Type == "var" || d.Type == "object" ? FromOrigin(Origin(expr, s, at, depth + 1)) : null;
                if (viaClass != null) return viaClass;
                if (d.Type != "var") return FromCsType(d.Type);
                return d.Init == null ? null : ArgType(d.Init, s, d.Index, depth + 1);
            }

            if (Regex.IsMatch(expr, @"^[\w.]+$"))
            {
                // Member access: an enum constant (no JNI type), a contract's native object, or a member of a unique declared type.
                var parts = expr.Split('.');
                if (parts.Length == 2 && s.E.Enums.ContainsKey(parts[0])) return p => false;
                if (KnownExternal.TryGetValue(expr, out var externalType)) return FromCsType(externalType);
                if (parts.Length == 2 && s.E.ClassMembers.TryGetValue(expr, out var classMember)) return FromCsType(classMember);
                var viaOrigin = FromOrigin(Origin(expr, s, at, depth + 1));
                if (viaOrigin != null) return viaOrigin;
                return s.E.Members.TryGetValue(parts[parts.Length - 1], out var memberType) ? FromCsType(memberType) : null;
            }

            var generic = Regex.Match(expr, @"^[\w.]+\.(?:CallStatic|Call|GetStatic|Get)<(\w+)>\s*\(");
            if (generic.Success)
            {
                if (generic.Groups[1].Value == "AndroidJavaObject")
                {
                    var o = Origin(expr, s, at, depth + 1);
                    if (o != null) return FromOrigin(o);
                }

                return FromCsType(generic.Groups[1].Value);
            }

            var external = KnownExternalCalls.FirstOrDefault(k => expr.EndsWith(k.Key));
            if (external.Key != null) return FromCsType(external.Value);
            var call = Regex.Match(expr, @"^(?:[\w.]*\.)?(\w+)\s*\(");
            if (call.Success)
            {
                var o = Origin(expr, s, at, depth + 1);
                if (o != null) return FromOrigin(o);
                if (s.E.Returns.TryGetValue(call.Groups[1].Value, out var returns)) return FromCsType(returns);
            }

            return null;
        }

        // UnityEngine / System members the bridge passes on: their types are the engine's, not in the runtime sources.
        static readonly Dictionary<string, string> KnownExternal = new Dictionary<string, string>
        {
            { "Application.unityVersion", "string" }, { "Application.productName", "string" },
        };

        static readonly Dictionary<string, string> KnownExternalCalls = new Dictionary<string, string>
        {
            { ".ToUnixTimeMilliseconds()", "long" },
        };

        static bool Accepts(JavaMethod method, int argCount, Func<string, bool>[] argTypes)
        {
            var count = method.Params.Length;
            if (method.VarArgs ? argCount < count - 1 : argCount != count) return false;
            for (int i = 0; i < argCount; i++)
            {
                if (argTypes[i] == null) continue;
                if (!argTypes[i](method.Params[Math.Min(i, count - 1)])) return false;
            }

            return true;
        }

        // ---- the C# calls, typed --------------------------------------------------------------------------------------------

        [Test]
        public void Every_CallStatic_reaches_a_public_static_method_with_matching_argument_and_return_types()
        {
            var sources = Sources();
            var env = BindingEnv();
            var failures = new List<string>();
            var typed = 0;
            var total = 0;
            var onSource = 0;
            var untyped = new List<string>();
            var loose = new List<string>();
            foreach (var call in Calls(sources))
            {
                var isSdk = call.Class.StartsWith("com.bugsee.library.");
                var isWrapper = call.Class.StartsWith("com.bugsee.unity.");
                if (!isSdk && !isWrapper) continue; // the JDK and Unity
                if (!env.Manifest.TryGetValue(call.Class, out var cls))
                {
                    failures.Add(call.File + ": " + call.Class + " is not in the snapshot or in Plugins/Android");
                    continue;
                }

                var path = sources.Keys.First(k => Path.GetFileName(k) == call.File && (call.Src == "" || sources[k] == call.Src));
                var site = SiteOf(path);
                var argTypes = call.Args.Select(a => call.Src == "" ? (a == "(AndroidJavaObject)null" ? NullObject : CsTypePredicate(Regex.Match(a, @"^\((\w+)\)").Groups[1].Value, env.Proxies, env.Manifest)) : ArgType(a, site, call.Index)).ToArray();
                total += argTypes.Length;
                typed += argTypes.Count(t => t != null);
                for (int i = 0; i < argTypes.Length; i++)
                {
                    if (argTypes[i] == null) untyped.Add(call.File + ": " + call.Name + " argument " + call.Args[i]);
                    else if (argTypes[i] == LooseObject) loose.Add(call.File + ": " + call.Name + " " + call.Args[i]);
                }

                if (isWrapper) onSource++;
                var statics = cls.Methods.Where(m => m.Static && m.Public && m.Name == call.Name).ToList();
                if (!statics.Any(m => Accepts(m, call.Args.Length, argTypes) && ReturnFits(m, call.Generic)))
                {
                    failures.Add(call.File + ": " + cls.Name + "." + call.Name + "(" + string.Join(", ", call.Args) + ") as "
                                 + (call.Generic ?? "void") + " has no match among: "
                                 + (statics.Count == 0 ? "(no public static method of that name)" : string.Join(" | ", statics)));
                }
            }

            Assert.That(failures, Is.Empty, "Calls the pinned Android SDK or UnityWrapper does not have:\n" + string.Join("\n", failures));
            Assert.That(onSource, Is.GreaterThanOrEqualTo(7), "the UnityWrapper calls were not found");
            Assert.That(loose.Distinct(), Is.EquivalentTo(LooseObjectArguments.Keys),
                "CallStatic arguments typed only as 'some Java object': resolve them, or name them (with the reason) in LooseObjectArguments");
            Assert.That(untyped, Is.Empty, "CallStatic arguments whose type the text does not tell (" + typed + " of " + total + " known)");
        }

        // CallStatic arguments the text cannot tie to a Java class: a value handed over from outside the bridge's own code
        // ("file: method argument"). They bind to any reference parameter; each is the reason it stays loose.
        static readonly Dictionary<string, string> LooseObjectArguments = new Dictionary<string, string>
        {
            { "AndroidBridge.cs: addSecureWebView view", "an android.webkit.WebView the app gave the bridge" },
            { "AndroidBridge.cs: launch activity", "the app's android.app.Activity (UnityPlayer.currentActivity)" },
            { "AndroidBridge.cs: ext feedbackClass", "a java.lang.Class object (the first of the two lookups)" },
            { "CallbackProxies.cs: replyNoData callback", "the DataRequestResultCallback the SDK passed to a proxy method" },
            { "CallbackProxies.cs: upload report", "the Report the SDK passed to a proxy method" },
        };

        // Instance-call receivers that are not resolved to a snapshot class ("file: name"): JDK objects (Map, List, iterator, boxed
        // numbers, stream, Activity, Class) and SDK objects the text does not type (a contract passed in, `Ext()`, the factory).
        // An SDK receiver that is unresolved is checked by name and arity only (the check below). A renamed ContractFields entry
        // would land here, which is why the set is exact.
        static readonly Dictionary<string, string> UnresolvedReceivers = new Dictionary<string, string>
        {
            { "AndroidBridge.cs: _appearance", "SDK Appearance, passed in by the caller" },
            { "AndroidBridge.cs: activity", "JDK/Android Activity" },
            { "AndroidBridge.cs: boxed", "JDK boxed Integer / Object" },
            { "AndroidBridge.cs: c", "JDK Class" },
            { "AndroidBridge.cs: ext", "SDK feedback extension, a generic return (erased to Object)" },
            { "AndroidBridge.cs: list", "JDK ArrayList" },
            { "AndroidBridge.cs: map", "JDK Map" },
            { "AndroidBridge.cs: newMessages", "JDK List handed to a proxy method" },
            { "AndroidExchangeFactory.cs: _factory", "SDK event factory, passed in by the caller" },
            { "AndroidJavaConverters.cs: clazz", "JDK Class" },
            { "AndroidJavaConverters.cs: entry", "JDK Map.Entry" },
            { "AndroidJavaConverters.cs: entrySet", "JDK Set" },
            { "AndroidJavaConverters.cs: iterator", "JDK Iterator" },
            { "AndroidJavaConverters.cs: javaObject", "any object being unboxed" },
            { "AndroidJavaConverters.cs: map", "JDK Map" },
            { "AndroidOptionsMapper.cs: map", "JDK HashMap" },
            { "AndroidReport.cs: javaList", "JDK List" },
            { "AndroidReport.cs: stream", "JDK OutputStream" },
            { "CallbackProxies.cs: callback", "a Java callback handed to a proxy method" },
            { "CallbackProxies.cs: completion", "a java.lang.Runnable handed to a proxy method" },
            { "CallbackProxies.cs: javaBoolean", "JDK Boolean handed to a proxy method" },
        };

        static readonly Regex InstanceCallRe = new Regex(
            @"(?<![\w.])(?:(?<recv>\w+)\s*\??\s*\.\s*Call(?:<(?<gen>[^>]+)>)?|(?<helper>Set|Chain))\s*\(\s*""(?<name>\w+)""\s*(?=[,)])");

        // The wrapper helpers that call the contract with a computed method name: a fluent Java setter returns the receiver.
        static readonly Dictionary<string, string> InstanceHelpers = new Dictionary<string, string>
        {
            { "Set", "AndroidReport.cs" }, { "Chain", "AndroidSpanHandle.cs" },
        };

        [Test]
        public void Every_instance_Call_on_a_resolved_receiver_matches_the_argument_and_return_types_of_that_class()
        {
            var env = BindingEnv();
            var failures = new List<string>();
            var resolved = 0;
            var typed = 0;
            var total = 0;
            var receivers = new HashSet<string>();
            var untyped = new List<string>();
            var loose = new List<string>();
            var unresolved = new SortedSet<string>();
            foreach (var path in env.Sources.Keys)
            {
                var site = SiteOf(path);
                var file = Path.GetFileName(path);
                foreach (Match m in InstanceCallRe.Matches(site.Src))
                {
                    var name = m.Groups["name"].Value;
                    var args = SplitArgs(BalancedArgs(site.Src, m.Index + m.Value.IndexOf('(') + 1)).Skip(1).ToArray();
                    string[] classes;
                    string generic;
                    if (m.Groups["helper"].Success)
                    {
                        if (!InstanceHelpers.TryGetValue(m.Groups["helper"].Value, out var defFile) || defFile != file) continue;
                        classes = ContractClassesAt(site, m.Index);
                        generic = "AndroidJavaObject";
                    }
                    else
                    {
                        classes = Origin(m.Groups["recv"].Value, site, m.Index);
                        generic = m.Groups["gen"].Success ? m.Groups["gen"].Value.Trim() : null;
                    }

                    // The JDK (maps, lists, boxed numbers, streams, an Activity) is not in the snapshot: not checked here.
                    if (classes == null || classes.Any(c => !env.Manifest.ContainsKey(c)))
                    {
                        unresolved.Add(file + ": " + (m.Groups["recv"].Success ? m.Groups["recv"].Value : m.Groups["helper"].Value));
                        continue;
                    }

                    resolved++;
                    receivers.Add(string.Join("+", classes.Select(c => c.Substring(c.LastIndexOf('.') + 1))));
                    var argTypes = args.Select(a => ArgType(a, site, m.Index)).ToArray();
                    total += argTypes.Length;
                    typed += argTypes.Count(t => t != null);
                    for (int i = 0; i < argTypes.Length; i++)
                    {
                        if (argTypes[i] == null) untyped.Add(file + ": " + name + " argument " + args[i]);
                        else if (argTypes[i] == LooseObject) loose.Add(file + ": " + name + " " + args[i]);
                    }
                    foreach (var cls in classes)
                    {
                        var methods = new List<JavaMethod>();
                        AddWithSupers(cls, env.Manifest, methods);
                        var named = methods.Where(j => j.Name == name).ToList();
                        if (!named.Any(j => Accepts(j, args.Length, argTypes) && ReturnFits(j, generic)))
                            failures.Add(file + ": " + cls + "." + name + "(" + string.Join(", ", args) + ") as " + (generic ?? "void")
                                         + " has no match among: " + (named.Count == 0 ? "(no such method)" : string.Join(" | ", named)));
                    }
                }
            }

            Assert.That(failures, Is.Empty, "Instance calls the receiver's class does not have:\n" + string.Join("\n", failures));
            Assert.That(resolved, Is.GreaterThanOrEqualTo(85), "the receivers were resolved for too few calls");
            Assert.That(loose, Is.Empty, "Instance-call arguments typed only as 'some Java object'");
            Assert.That(unresolved, Is.EquivalentTo(UnresolvedReceivers.Keys),
                "Receivers the check could not resolve to an SDK class (a renamed contract field lands here); name them in UnresolvedReceivers");
            Assert.That(receivers, Is.SupersetOf(new[] { "LogEvent", "Breadcrumb", "Report", "Attachment", "NetworkEvent", "Span+Transaction" }));
            Assert.That(untyped, Is.Empty, "Instance-call arguments whose type the text does not tell (" + typed + " of " + total + " known)");
        }

        [Test]
        public void Every_com_bugsee_class_constructed_from_managed_code_has_a_constructor_taking_those_arguments()
        {
            var env = BindingEnv();
            var failures = new List<string>();
            var constructed = 0;
            foreach (var path in env.Sources.Keys)
            {
                var site = SiteOf(path);
                var file = Path.GetFileName(path);
                foreach (Match m in Regex.Matches(site.Src, @"\bnew\s+AndroidJavaObject\(\s*""(com\.bugsee\.[\w.$]+)""\s*(?=[,)])"))
                {
                    constructed++;
                    var cls = m.Groups[1].Value;
                    var args = SplitArgs(BalancedArgs(site.Src, m.Index + m.Value.IndexOf('(') + 1)).Skip(1).ToArray();
                    if (!env.Manifest.TryGetValue(cls, out var jc)) { failures.Add(file + ": " + cls + " is not in the snapshot or in Plugins/Android"); continue; }
                    var argTypes = args.Select(a => ArgType(a, site, m.Index)).ToArray();
                    var ctors = jc.Methods.Where(j => j.Ret == null && j.Name == cls && j.Public).ToList();
                    if (!ctors.Any(c => Accepts(c, args.Length, argTypes)))
                        failures.Add(file + ": new " + cls + "(" + string.Join(", ", args) + ") has no constructor among: "
                                     + (ctors.Count == 0 ? "(none)" : string.Join(" | ", ctors.Select(c => "(" + string.Join(", ", c.Params) + ")"))));
                }
            }

            Assert.That(failures, Is.Empty);
            Assert.That(constructed, Is.GreaterThanOrEqualTo(1), "UnityManagedException is no longer constructed from managed code");
        }

        // Extension facades the Java wrappers reach by reflection that are not in the snapshot (separate AARs: NDK, leak
        // detection, remoting). Their names are not checked against anything.
        static readonly HashSet<string> ExtensionClassesNotInSnapshot = new HashSet<string>
        {
            "com.bugsee.library.BugseeNdk", "com.bugsee.library.BugseeLeak", "com.bugsee.library.BugseeRemoting",
        };

        [Test]
        public void Every_com_bugsee_library_string_literal_in_the_Java_wrappers_names_a_snapshot_class_and_method()
        {
            // StripJava blanks strings, so the reflective forms (Class.forName("..."), a {class, method} table) are read here.
            var manifest = FullManifest();
            var failures = new List<string>();
            foreach (var f in JavaFiles())
            {
                foreach (Match m in Regex.Matches(f.Raw, @"""(com\.bugsee\.library\.[\w.$]+)"""))
                {
                    var cls = m.Groups[1].Value;
                    if (!manifest.ContainsKey(cls) && !ExtensionClassesNotInSnapshot.Contains(cls))
                    {
                        failures.Add(f.Name + ": \"" + cls + "\" is not in the snapshot");
                        continue;
                    }

                    // The method named right after it: {"cls", "method"} or forName("cls").getMethod("method").
                    var after = Regex.Match(f.Raw.Substring(m.Index + m.Length), @"^\s*(?:,\s*|\)\s*\.\s*(?:getMethod|getDeclaredMethod)\(\s*)""(\w+)""");
                    if (after.Success && manifest.ContainsKey(cls) && !manifest[cls].Methods.Any(j => j.Name == after.Groups[1].Value))
                        failures.Add(f.Name + ": " + cls + " has no method " + after.Groups[1].Value);
                }
            }

            Assert.That(failures, Is.Empty);
        }

        // ---- static fields and enum constants read by name ------------------------------------------------------------------

        // The T of GetStatic<T> the check knows how to compare with the field type (ReturnFits); another one is rejected, not skipped.
        static readonly HashSet<string> SupportedGetStaticTypes = new HashSet<string>
            { "AndroidJavaObject", "string", "bool", "int", "long", "float", "double", "short", "char", "sbyte" };

        // Files that read a static field of an SDK class by a computed name (checked by their own test).
        static readonly HashSet<string> ComputedStaticFieldReaders = new HashSet<string> { "AndroidSpanHandle.cs" };

        [Test]
        public void Every_GetStatic_on_an_sdk_class_reads_a_static_field_of_that_type()
        {
            var env = BindingEnv();
            var failures = new List<string>();
            foreach (var path in env.Sources.Keys)
            {
                var site = SiteOf(path);
                var file = Path.GetFileName(path);
                foreach (Match m in Regex.Matches(site.Src, @"\b(\w+)\s*\??\s*\.\s*GetStatic(?:<([^>]+)>)?\s*\(\s*([^,)]+?)\s*\)"))
                {
                    var cls = ClassOfReceiver(site, m.Groups[1].Value, m.Index);
                    if (cls == null || !cls.StartsWith("com.bugsee.library.")) continue; // UnityPlayer.currentActivity & co: Unity's
                    if (!env.Manifest.TryGetValue(cls, out var fieldOwner))
                    {
                        failures.Add(file + ": " + cls + " is not in the snapshot");
                        continue;
                    }

                    var name = Resolve(m.Groups[3].Value, site.Src);
                    if (name == null)
                    {
                        if (!ComputedStaticFieldReaders.Contains(file))
                            failures.Add(file + ": GetStatic of " + cls + " with a computed name " + m.Groups[3].Value + " is not checked");
                        continue;
                    }

                    // GetStatic<T> reads the field as T: the same fit as a return type (T = AndroidJavaObject: any reference).
                    if (!m.Groups[2].Success || !SupportedGetStaticTypes.Contains(m.Groups[2].Value))
                        failures.Add(file + ": GetStatic of " + cls + "." + name + " with the type argument '" + m.Groups[2].Value + "' (not one of the checked types)");
                    else if (!fieldOwner.Fields.TryGetValue(name, out var field) || !field.isStatic
                             || !ReturnFits(new JavaMethod { Ret = field.type }, m.Groups[2].Value))
                        failures.Add(file + ": " + cls + "." + name + " is not a static field readable as " + m.Groups[2].Value);
                }
            }

            // No SDK GetStatic uses a literal name today: the literal branch is exercised by the mutation proof only (PR #239), not by a unit test.
            Assert.That(failures, Is.Empty);
        }

        [Test]
        public void Every_enum_name_passed_to_an_sdk_enum_valueOf_is_a_constant_of_it()
        {
            var env = BindingEnv();
            var failures = new List<string>();
            var checkedNames = 0;
            foreach (var path in env.Sources.Keys)
            {
                var site = SiteOf(path);
                var file = Path.GetFileName(path);
                foreach (var call in Calls(env.Sources).Where(c => c.File == file && c.Name == "valueOf"))
                {
                    if (!env.Manifest.TryGetValue(call.Class, out var cls) || !cls.IsEnum) continue;
                    var arg = call.Args[0].Trim();
                    List<string> names;
                    var csEnum = Regex.Match(arg, @"^(\w+)\.ToString\(\)$");
                    if (csEnum.Success)
                    {
                        var type = CsTypeOf(csEnum.Groups[1].Value, site, call.Index, 0);
                        Assert.That(type != null && env.Enums.ContainsKey(type), Is.True, file + ": the enum type of " + arg + " is unknown");
                        names = env.Enums[type].ToList();
                    }
                    else if (Regex.IsMatch(arg, @"^\w+$"))
                        names = Regex.Matches(site.Src, @"\b" + arg + @"\s*=\s*""(\w+)""").Cast<Match>().Select(x => x.Groups[1].Value).Distinct().ToList();
                    else
                        names = Resolve(arg, site.Src) is string one ? new List<string> { one } : new List<string>();
                    Assert.That(names, Is.Not.Empty, file + ": valueOf(" + arg + ") names are not known");
                    foreach (var n in names)
                    {
                        checkedNames++;
                        if (!cls.Fields.TryGetValue(n, out var f) || !f.isStatic || f.type != cls.Name)
                            failures.Add(file + ": " + cls.Name + " has no constant " + n + " (valueOf(" + arg + "))");
                    }
                }
            }

            Assert.That(failures, Is.Empty);
            Assert.That(checkedNames, Is.GreaterThanOrEqualTo(10), "too few enum names found");
        }

        // ---- the Java side ---------------------------------------------------------------------------------------------------

        [Test]
        public void The_Java_parser_sees_the_known_surface()
        {
            var classes = JavaSourceClasses();
            Assert.That(classes.Keys, Is.SupersetOf(new[] { "com.bugsee.unity.UnityWrapper", "com.bugsee.unity.UnityWrapperProvider", "com.bugsee.unity.UnityManagedException" }));
            var wrapper = classes["com.bugsee.unity.UnityWrapper"];
            Assert.That(wrapper.Supers, Is.EqualTo(new[] { "com.bugsee.library.contracts.internal.BugseeWrapper" }));
            var channelLog = wrapper.Methods.Single(m => m.Name == "channelLog");
            Assert.That(channelLog.Static && channelLog.Public, Is.True);
            Assert.That(channelLog.Params, Is.EqualTo(new[] { "java.lang.String", "int", "int" }));
            Assert.That(wrapper.Methods.Single(m => m.Name == "channelAddNetwork").Params, Is.EqualTo(new[] { "com.bugsee.library.contracts.exchange.NetworkEvent" }));
            Assert.That(wrapper.Methods.Single(m => m.Name == "setSecureBuffer").Params, Is.EqualTo(new[] { "int", "int[]" }));
            Assert.That(wrapper.Methods.Single(m => m.Name == "getContext").Ret, Is.EqualTo("java.util.Map"));
            Assert.That(wrapper.Methods.Count(m => m.Override), Is.GreaterThanOrEqualTo(8));
        }

        [Test]
        public void Every_override_in_the_Java_wrappers_overrides_a_method_of_its_sdk_supertype_and_every_abstract_one_is_implemented()
        {
            var manifest = FullManifest();
            var failures = new List<string>();
            var overrides = 0;
            foreach (var cls in JavaSourceClasses().Values)
            {
                var inherited = new List<JavaMethod>();
                foreach (var super in cls.Supers) AddWithSupers(super, manifest, inherited);
                foreach (var m in cls.Methods.Where(m => m.Override))
                {
                    overrides++;
                    var same = inherited.Where(j => j.Name == m.Name && j.Params.SequenceEqual(m.Params)).ToList();
                    if (same.Count == 0)
                        failures.Add(cls.Name + "." + m + " overrides nothing in " + string.Join(", ", cls.Supers) + "; same name: "
                                     + string.Join(" | ", inherited.Where(j => j.Name == m.Name)));
                    else if (!same.Any(j => j.Ret == m.Ret || (IsReference(j.Ret) && IsReference(m.Ret) && Reaches(m.Ret, j.Ret, manifest))))
                        failures.Add(cls.Name + "." + m + " returns a type the overridden " + string.Join(" | ", same) + " does not allow");
                }

                if (cls.Supers.Count == 0) continue;
                foreach (var j in inherited.Where(j => j.Abstract && !j.Static))
                    if (!cls.Methods.Any(m => m.Name == j.Name && m.Params.SequenceEqual(j.Params)))
                        failures.Add(cls.Name + " does not implement " + j);
            }

            Assert.That(overrides, Is.GreaterThanOrEqualTo(10));
            Assert.That(failures, Is.Empty);
        }

        static readonly Regex JavaQualifiedCallRe = new Regex(@"(?<![\w.])((?:[a-z]\w*\.)*[A-Z]\w*)\.(\w+)\s*\(");
        static readonly Regex JavaQualifiedFieldRe = new Regex(@"(?<![\w.])((?:[a-z]\w*\.)*[A-Z]\w*)\.(\w+)\b(?!\s*\()");
        static readonly Regex JavaVariableRe = new Regex(@"(?<![\w.])((?:[a-z]\w*\.)*[A-Z][\w.]*)(?:<[^;(){}=]*?>)?(?:\[\])?\s+(\w+)\s*(?==|;|,|\))");
        static readonly Regex JavaMemberCallRe = new Regex(@"(?<![\w.])([a-z_]\w*)\.(\w+)\s*\(");

        static bool JavaArgFits(string arg, string param, Dictionary<string, HashSet<string>> variables, Dictionary<string, JavaClass> manifest)
        {
            arg = arg.Trim();
            // A local or parameter: any of its declared types in the file must fit (scopes are not told apart); "" is a type outside the snapshot.
            if (Regex.IsMatch(arg, @"^\w+$") && variables.TryGetValue(arg, out var declared) && !declared.Contains(""))
                return declared.Any(t => t == param || (IsReference(t) && IsReference(param) && Reaches(t, param, manifest)));
            if (arg == "null") return IsReference(param);
            if (arg == "true" || arg == "false") return param == "boolean";
            if (Regex.IsMatch(arg, @"^-?\d+$")) return param == "int" || param == "long" || param == "float" || param == "double";
            if (arg == "\"\"") return param == "java.lang.String" || param == "java.lang.CharSequence" || param == "java.lang.Object";
            var cast = Regex.Match(arg, @"^\((\w+)\)\s*\w");
            if (cast.Success && JavaPrimitives.Contains(cast.Groups[1].Value)) return param == cast.Groups[1].Value;
            return true;
        }

        /// <summary>The Java classes a qualifier names (an import, an FQN, or a class of the package), or null.</summary>
        static string ResolveJavaQualifier(string qualifier, JavaFile f, Dictionary<string, JavaClass> manifest)
        {
            if (manifest.ContainsKey(qualifier)) return qualifier;
            var first = qualifier.Split('.')[0];
            if (f.Imports.TryGetValue(first, out var imported))
            {
                var full = imported + qualifier.Substring(first.Length).Replace('.', '$');
                return manifest.ContainsKey(full) ? full : null;
            }

            var samePackage = f.Package + "." + qualifier;
            return manifest.ContainsKey(samePackage) ? samePackage : null;
        }

        [Test]
        public void Every_call_the_Java_wrappers_make_into_the_sdk_reaches_a_method_of_the_snapshot()
        {
            var manifest = FullManifest();
            var failures = new List<string>();
            var statics = 0;
            var instances = 0;
            var constants = 0;
            var imports = 0;
            foreach (var f in JavaFiles())
            {
                foreach (var import in f.ImportLines.Where(i => i.StartsWith("com.bugsee.library.")))
                {
                    imports++;
                    if (!manifest.ContainsKey(import) && !manifest.ContainsKey(import.Substring(0, import.LastIndexOf('.')) + "$" + import.Substring(import.LastIndexOf('.') + 1)))
                        failures.Add(f.Name + ": import " + import + " is not in the snapshot");
                }

                var variables = new Dictionary<string, HashSet<string>>();
                foreach (Match v in JavaVariableRe.Matches(f.Text))
                {
                    if (!variables.TryGetValue(v.Groups[2].Value, out var set)) variables[v.Groups[2].Value] = set = new HashSet<string>();
                    set.Add(ResolveJavaQualifier(v.Groups[1].Value, f, manifest) ?? "");
                }

                foreach (Match v in Regex.Matches(f.Text, @"(?<![\w.])(boolean|byte|short|char|int|long|float|double)((?:\[\])?)\s+(\w+)\s*(?==|;|,|\))"))
                {
                    if (!variables.TryGetValue(v.Groups[3].Value, out var set)) variables[v.Groups[3].Value] = set = new HashSet<string>();
                    set.Add(v.Groups[1].Value + v.Groups[2].Value);
                }

                foreach (Match m in JavaQualifiedCallRe.Matches(f.Text))
                {
                    var cls = ResolveJavaQualifier(m.Groups[1].Value, f, manifest);
                    if (cls == null) continue; // android.* / java.*
                    statics++;
                    var args = SplitArgs(BalancedArgs(f.Text, m.Index + m.Length));
                    var named = manifest[cls].Methods.Where(j => j.Static && j.Name == m.Groups[2].Value).ToList();
                    if (!named.Any(j => (j.VarArgs ? args.Length >= j.Params.Length - 1 : j.Params.Length == args.Length)
                                        && args.Select((a, i) => JavaArgFits(a, j.Params[Math.Min(i, j.Params.Length - 1)], variables, manifest)).All(x => x)))
                        failures.Add(f.Name + ": " + cls + "." + m.Groups[2].Value + "(" + string.Join(", ", args) + ") has no match among "
                                     + (named.Count == 0 ? "(no static method of that name)" : string.Join(" | ", named)));
                }

                foreach (Match m in JavaMemberCallRe.Matches(f.Text))
                {
                    if (!variables.TryGetValue(m.Groups[1].Value, out var types) || types.Any(t => !manifest.ContainsKey(t)) || types.Count == 0) continue;
                    instances++;
                    var args = SplitArgs(BalancedArgs(f.Text, m.Index + m.Length));
                    foreach (var cls in types)
                    {
                        var methods = new List<JavaMethod>();
                        AddWithSupers(cls, manifest, methods);
                        var named = methods.Where(j => j.Name == m.Groups[2].Value).ToList();
                        if (!named.Any(j => (j.VarArgs ? args.Length >= j.Params.Length - 1 : j.Params.Length == args.Length)
                                            && args.Select((a, i) => JavaArgFits(a, j.Params[Math.Min(i, j.Params.Length - 1)], variables, manifest)).All(x => x)))
                            failures.Add(f.Name + ": " + m.Groups[1].Value + "." + m.Groups[2].Value + "(" + string.Join(", ", args) + ") on " + cls
                                         + " has no match among " + (named.Count == 0 ? "(no such method)" : string.Join(" | ", named)));
                    }
                }

                foreach (Match m in JavaQualifiedFieldRe.Matches(f.Text))
                {
                    var cls = ResolveJavaQualifier(m.Groups[1].Value, f, manifest);
                    if (cls == null) continue;
                    constants++;
                    var name = m.Groups[2].Value;
                    if (!manifest[cls].Fields.TryGetValue(name, out var field) || !field.isStatic)
                        if (!manifest.ContainsKey(cls + "$" + name))
                            failures.Add(f.Name + ": " + cls + "." + name + " is not a static field of the snapshot");
                }
            }

            Assert.That(failures, Is.Empty);
            Assert.That(statics, Is.GreaterThanOrEqualTo(3));
            Assert.That(instances, Is.GreaterThanOrEqualTo(5));
            Assert.That(constants, Is.GreaterThanOrEqualTo(6));
            Assert.That(imports, Is.GreaterThanOrEqualTo(8));
        }
    }
}
