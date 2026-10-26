using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    /// <summary>
    /// Runs the managed Android conversion code against a fake Java object model on the host, so the logic that
    /// maps C# values to the SDK's Java objects and back is executed without a device. The real source files
    /// (AndroidJavaConverters, AndroidOptionsMapper, AndroidExchangeEvents) are compiled from the repository with
    /// UNITY_ANDROID defined, against a small stand-in for AndroidJavaObject / AndroidJavaClass that forwards every
    /// call to <see cref="FakeJava"/>. This pins what CallbackProxies and the exchange events rely on: boxing and
    /// unboxing, attribute widening, option enum coercion with the SDK's fallback, filter event getters/setters and
    /// the release guard. What it cannot show is that the SDK method really has that name and signature: that is
    /// AndroidSdkBindingTests (against the pinned SDK API snapshot).
    /// </summary>
    public class AndroidFakeJniTests
    {
        // The stand-in for the Unity types. Models are plain objects owned by FakeJava; the wrapper only unwraps arguments
        // and wraps AndroidJavaObject results, like the real class does for Java references.
        const string UnityStub = @"
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Bugsee.Contracts.Exchange;
namespace UnityEngine
{
    public static class Debug
    {
        public static List<string> Warnings = new List<string>();
        public static void LogWarning(object m) { Warnings.Add(Convert.ToString(m)); }
        public static void Log(object m) { }
        public static void LogError(object m) { }
        public static void LogException(Exception e) { }
    }
    public class AndroidJavaObject : IDisposable
    {
        public static Func<string, object[], object> Construct;
        public static Func<object, string, object[], Type, object> Invoke;
        public static Func<string, string, object[], Type, object> InvokeStatic;
        static int _nextHandle;
        public static int DoubleDisposes;
        public object Model;
        readonly IntPtr _handle = (IntPtr)System.Threading.Interlocked.Increment(ref _nextHandle);
        bool _disposed;
        protected void Live() { if (_disposed) throw new ObjectDisposedException(""AndroidJavaObject""); }
        public sealed class Ref { public object M; public Ref(object m) { M = m; } }
        public AndroidJavaObject(Ref r) { Model = r.M; }
        public AndroidJavaObject(string cls, params object[] args) { Model = Construct(cls, args); }
        protected AndroidJavaObject() { }
        static object[] Unwrap(object[] a)
        {
            var r = new object[a.Length];
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] is AndroidJavaObject j) { j.Live(); r[i] = j.Model; } else r[i] = a[i];
            }
            return r;
        }
        internal static T Cast<T>(object r)
        {
            if (typeof(T) == typeof(AndroidJavaObject)) return r == null ? default(T) : (T)(object)new AndroidJavaObject(new Ref(r));
            if (r == null) return default(T);
            // JNI binds the return type to the type argument: a value of another type is a failed call, not a conversion.
            if (r is T t) return t;
            throw new InvalidCastException(""JNI: asked for "" + typeof(T).Name + "", the method returns "" + r.GetType().Name);
        }
        public T Call<T>(string m, params object[] a) { Live(); return Cast<T>(Invoke(Model, m, Unwrap(a), typeof(T))); }
        public void Call(string m, params object[] a) { Live(); Invoke(Model, m, Unwrap(a), typeof(void)); }
        public T StaticCall<T>(string cls, string m, object[] a) { Live(); return Cast<T>(InvokeStatic(cls, m, Unwrap(a), typeof(T))); }
        // Each wrapper holds its own reference, like the global reference a Unity wrapper takes.
        public IntPtr GetRawObject() { Live(); return _handle; }
        public void Dispose() { if (_disposed) DoubleDisposes++; _disposed = true; }
    }
    public class AndroidJavaClass : AndroidJavaObject
    {
        readonly string _cls;
        public AndroidJavaClass(string cls) { _cls = cls; }
        public T CallStatic<T>(string m, params object[] a) => StaticCall<T>(_cls, m, a);
        public void CallStatic(string m, params object[] a) { StaticCall<object>(_cls, m, a); }
    }
}
namespace Probe
{
    // Public surface over the internal classes, in BCL / contract types only.
    public static class P
    {
        static UnityEngine.AndroidJavaObject W(object m) => m == null ? null : new UnityEngine.AndroidJavaObject(new UnityEngine.AndroidJavaObject.Ref(m));
        public static object ToJavaMap(IDictionary<string, object> o) => Bugsee.Platform.Android.AndroidOptionsMapper.ToJavaMap(o).Model;
        public static object Box(object v) => Bugsee.Platform.Android.AndroidJavaConverters.Box(v)?.Model;
        public static object Unbox(object m) => Bugsee.Platform.Android.AndroidJavaConverters.Unbox(W(m));
        public static object UnboxAttribute(object m) => Bugsee.Platform.Android.AndroidJavaConverters.UnboxAttribute(W(m));
        public static Dictionary<string, object> MapToDictionary(object m) => Bugsee.Platform.Android.AndroidJavaConverters.MapToDictionary(W(m));
        public static Dictionary<string, object> AttributesToDictionary(object m) => Bugsee.Platform.Android.AndroidJavaConverters.AttributesToDictionary(W(m));
        public static Dictionary<string, string> MapToStringDictionary(object m) => Bugsee.Platform.Android.AndroidJavaConverters.MapToStringDictionary(W(m));
        public static object StringDictionaryToMap(IDictionary<string, string> d) => Bugsee.Platform.Android.AndroidJavaConverters.StringDictionaryToMap(d).Model;
        public static object ObjectDictionaryToMap(IDictionary<string, object> d) => Bugsee.Platform.Android.AndroidJavaConverters.ObjectDictionaryToMap(d).Model;
        public static INetworkEvent Network(object m) => new Bugsee.Platform.Android.AndroidNetworkEvent(W(m));
        public static ILogEvent Log(object m) => new Bugsee.Platform.Android.AndroidLogEvent(W(m));
        public static IBreadcrumb Breadcrumb(object m) => new Bugsee.Platform.Android.AndroidBreadcrumb(W(m));
        public static void Release(object ev) => ((Bugsee.Platform.Android.IReleasableNativeEvent)ev).ReleaseNative();
        public static List<string> Warnings => UnityEngine.Debug.Warnings;
        public static int DoubleDisposes => UnityEngine.AndroidJavaObject.DoubleDisposes;
        public static void Wire(Func<string, object[], object> c, Func<object, string, object[], Type, object> i, Func<string, string, object[], Type, object> s)
        { UnityEngine.AndroidJavaObject.Construct = c; UnityEngine.AndroidJavaObject.Invoke = i; UnityEngine.AndroidJavaObject.InvokeStatic = s; UnityEngine.AndroidJavaObject.DoubleDisposes = 0; }
    }
}";

        static Type _probe;

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

        [OneTimeSetUp]
        public void Compile()
        {
            var options = new CSharpParseOptions(preprocessorSymbols: new[] { "UNITY_ANDROID" });
            var trees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(UnityStub, options, "UnityStub.cs") };
            foreach (var name in new[] { "AndroidJavaConverters.cs", "AndroidOptionsMapper.cs", "AndroidExchangeEvents.cs" })
            {
                var path = RepoFile("Runtime/Platform/Android/" + name);
                trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(path), options, path));
            }

            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
                .Split(Path.PathSeparator)
                .Select(p => MetadataReference.CreateFromFile(p))
                .Concat(new[] { MetadataReference.CreateFromFile(typeof(AttributeWire).Assembly.Location) });
            var compilation = CSharpCompilation.Create("AndroidFakeJni", trees, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using (var ms = new MemoryStream())
            {
                var emit = compilation.Emit(ms);
                Assert.That(emit.Success, Is.True, string.Join("\n", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
                _probe = Assembly.Load(ms.ToArray()).GetType("Probe.P");
            }
        }

        [SetUp]
        public void Wire()
        {
            Call("Wire",
                (Func<string, object[], object>)FakeJava.Construct,
                (Func<object, string, object[], Type, object>)FakeJava.Invoke,
                (Func<string, string, object[], Type, object>)FakeJava.InvokeStatic);
            ((List<string>)_probe.GetProperty("Warnings").GetValue(null)).Clear();
        }

        static object Call(string name, params object[] args)
        {
            try { return _probe.GetMethod(name).Invoke(null, args); }
            catch (TargetInvocationException e) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
        }

        static T Call<T>(string name, params object[] args) => (T)Call(name, args);

        static FakeJava.J Str(string s) => (FakeJava.J)FakeJava.Construct("java.lang.String", new object[] { s });

        static FakeJava.J MapOf(params (string key, FakeJava.J value)[] entries)
        {
            var map = (FakeJava.J)FakeJava.Construct("java.util.HashMap", new object[0]);
            foreach (var e in entries) FakeJava.Invoke(map, "put", new object[] { Str(e.key), e.value }, typeof(void));
            return map;
        }

        // ---- boxing ----

        [Test]
        public void Box_picks_the_java_wrapper_class_of_each_primitive()
        {
            var cases = new (object value, string cls)[]
            {
                ("s", "java.lang.String"), (true, "java.lang.Boolean"), (7, "java.lang.Integer"), (7L, "java.lang.Long"),
                (1.5f, "java.lang.Double"), (2.5, "java.lang.Double"), ((byte)200, "java.lang.Byte"), ((short)-3, "java.lang.Short"),
            };
            foreach (var c in cases)
                Assert.That(((FakeJava.J)Call("Box", c.value)).Class, Is.EqualTo(c.cls), c.value.GetType().Name);
            Assert.That(Call("Box", new object[] { null }), Is.Null);
        }

        [Test]
        public void Box_sends_a_byte_as_the_signed_java_byte_and_an_unrepresentable_value_as_a_string()
        {
            Assert.That(((FakeJava.J)Call("Box", (byte)200)).Value, Is.EqualTo((sbyte)-56));
            // AttributeWire.ToNative turns a list into JSON text first, so the Java side sees a String, not a type name.
            var list = (FakeJava.J)Call("Box", new List<object> { 1, "a" });
            Assert.That(list.Class, Is.EqualTo("java.lang.String"));
            Assert.That(list.Value, Is.EqualTo("[1,\"a\"]"));
        }

        [Test]
        public void Unbox_returns_each_java_wrapper_as_its_own_width()
        {
            Assert.That(Call("Unbox", new object[] { null }), Is.Null);
            Assert.That(Call("Unbox", Call("Box", "x")), Is.EqualTo("x"));
            Assert.That(Call("Unbox", Call("Box", true)), Is.EqualTo(true));
            Assert.That(Call("Unbox", Call("Box", 7)), Is.EqualTo(7).And.TypeOf<int>());
            Assert.That(Call("Unbox", Call("Box", 7L)), Is.EqualTo(7L).And.TypeOf<long>());
            // A float set by the app is boxed as the double that prints the same (AttributeWire), so Float only arrives from the SDK.
            Assert.That(Call("Unbox", Call("Box", 1.5f)), Is.EqualTo(1.5).And.TypeOf<double>());
            Assert.That(Call("Unbox", new FakeJava.J { Class = "java.lang.Float", Value = 1.5f }), Is.EqualTo(1.5f).And.TypeOf<float>());
            Assert.That(Call("Unbox", Call("Box", (short)-3)), Is.EqualTo((short)-3).And.TypeOf<short>());
            Assert.That(Call("Unbox", Call("Box", (byte)200)), Is.EqualTo((byte)200).And.TypeOf<byte>());
        }

        [Test]
        public void UnboxAttribute_widens_integers_to_long_and_floats_to_the_shortest_double()
        {
            Assert.That(Call("UnboxAttribute", Call("Box", 7)), Is.EqualTo(7L).And.TypeOf<long>());
            Assert.That(Call("UnboxAttribute", Call("Box", (short)5)), Is.EqualTo(5L).And.TypeOf<long>());
            Assert.That(Call("UnboxAttribute", Call("Box", (byte)200)), Is.EqualTo(200L).And.TypeOf<long>());
            // 1.1f is 1.100000023841858 as a double; iOS reads 1.1.
            Assert.That(Call("UnboxAttribute", new FakeJava.J { Class = "java.lang.Float", Value = 1.1f }), Is.EqualTo(1.1).And.TypeOf<double>());
            Assert.That(Call("UnboxAttribute", Call("Box", 1.1f)), Is.EqualTo(1.1).And.TypeOf<double>());
            Assert.That(Call("UnboxAttribute", Call("Box", 2.5)), Is.EqualTo(2.5));
        }

        [Test]
        public void An_unknown_java_object_unboxes_to_its_toString()
        {
            var odd = new FakeJava.J { Class = "com.example.Thing", Value = "thing-1" };
            Assert.That(Call("Unbox", odd), Is.EqualTo("thing-1"));
        }

        // ---- maps ----

        [Test]
        public void An_attribute_dictionary_round_trips_through_a_java_map_with_the_ios_kinds()
        {
            var attrs = new Dictionary<string, object>
            {
                { "s", "text" }, { "b", true }, { "i", 3 }, { "l", 4L }, { "f", 1.1f }, { "d", 0.25 }, { "n", null },
            };
            var map = Call("ObjectDictionaryToMap", attrs);
            var back = Call<Dictionary<string, object>>("AttributesToDictionary", map);

            Assert.That(back["s"], Is.EqualTo("text"));
            Assert.That(back["b"], Is.EqualTo(true));
            Assert.That(back["i"], Is.EqualTo(3L).And.TypeOf<long>());
            Assert.That(back["l"], Is.EqualTo(4L));
            Assert.That(back["f"], Is.EqualTo(1.1).And.TypeOf<double>());
            Assert.That(back["d"], Is.EqualTo(0.25));
            // A null value is put as a Java null; reading it back gives null for that key.
            Assert.That(back.ContainsKey("n"), Is.True);
            Assert.That(back["n"], Is.Null);
            Assert.That(back.Count, Is.EqualTo(7));
        }

        [Test]
        public void MapToDictionary_keeps_native_widths_and_skips_empty_keys()
        {
            var map = MapOf(("i", (FakeJava.J)Call("Box", 3)), ("", Str("dropped")));
            var back = Call<Dictionary<string, object>>("MapToDictionary", map);
            Assert.That(back.Keys, Is.EquivalentTo(new[] { "i" }));
            Assert.That(back["i"], Is.EqualTo(3).And.TypeOf<int>());
        }

        [Test]
        public void A_nested_java_map_reads_back_as_a_nested_dictionary()
        {
            var inner = MapOf(("k", Str("v")));
            var back = Call<Dictionary<string, object>>("MapToDictionary", MapOf(("inner", inner)));
            Assert.That(((Dictionary<string, object>)back["inner"])["k"], Is.EqualTo("v"));
        }

        [Test]
        public void Headers_round_trip_with_a_null_value_and_a_null_or_empty_map_reads_as_empty()
        {
            var headers = new Dictionary<string, string> { { "Accept", "*/*" }, { "X-Null", null }, { "X-A", "b" } };
            var back = Call<Dictionary<string, string>>("MapToStringDictionary", Call("StringDictionaryToMap", headers));
            Assert.That(back["Accept"], Is.EqualTo("*/*"));
            Assert.That(back["X-A"], Is.EqualTo("b"));
            Assert.That(back.ContainsKey("X-Null") && back["X-Null"] == null, Is.True);

            Assert.That(Call<Dictionary<string, string>>("MapToStringDictionary", new object[] { null }), Is.Empty);
            var empty = Call("StringDictionaryToMap", new object[] { null });
            Assert.That(Call<Dictionary<string, string>>("MapToStringDictionary", empty), Is.Empty);
        }

        [Test]
        public void MapToStringDictionary_skips_an_empty_key()
        {
            var back = Call<Dictionary<string, string>>("MapToStringDictionary", MapOf(("", Str("dropped")), ("k", Str("v"))));
            Assert.That(back.Keys, Is.EquivalentTo(new[] { "k" }));
        }

        [Test]
        public void A_nested_attribute_map_widens_its_values_too()
        {
            var inner = MapOf(("i", (FakeJava.J)Call("Box", 3)));
            var back = Call<Dictionary<string, object>>("AttributesToDictionary", MapOf(("inner", inner)));
            Assert.That(((Dictionary<string, object>)back["inner"])["i"], Is.EqualTo(3L).And.TypeOf<long>());
            var plain = Call<Dictionary<string, object>>("MapToDictionary", MapOf(("inner", inner)));
            Assert.That(((Dictionary<string, object>)plain["inner"])["i"], Is.EqualTo(3).And.TypeOf<int>());
        }

        [Test]
        public void A_character_unboxes_as_a_char()
        {
            Assert.That(Call("Unbox", new FakeJava.J { Class = "java.lang.Character", Value = 'c' }), Is.EqualTo('c').And.TypeOf<char>());
        }

        [Test]
        public void A_map_class_outside_java_util_is_read_as_a_map_through_its_entry_set_and_other_objects_as_text()
        {
            var custom = MapOf(("k", Str("v")));
            custom.Class = "com.example.Registry";
            var read = Call("Unbox", custom);
            Assert.That(read, Is.InstanceOf<Dictionary<string, object>>());
            Assert.That(((Dictionary<string, object>)read)["k"], Is.EqualTo("v"));

            // No entrySet: the probe fails inside the fake and the object falls back to toString.
            Assert.That(Call("Unbox", new FakeJava.J { Class = "com.example.Thing", Value = "t" }), Is.EqualTo("t"));
        }

        [Test]
        public void The_stand_in_enforces_the_jni_signatures_the_wrapper_code_relies_on()
        {
            var seven = Call("Box", 7);
            Assert.Throws<InvalidOperationException>(() => FakeJava.Invoke(seven, "intValue", new object[0], typeof(long)));
            Assert.Throws<InvalidOperationException>(() => FakeJava.Construct("java.lang.Short", new object[] { 3 }));
            Assert.Throws<InvalidOperationException>(() => FakeJava.InvokeStatic(
                "com.bugsee.library.contracts.options.VideoMode", "fromIntValue", new object[] { (sbyte)1 }, typeof(object)));
        }

        [Test]
        public void No_conversion_disposes_a_reference_twice_or_uses_one_after_disposing_it()
        {
            // Any use after Dispose throws inside the stand-in, so these also prove nothing is used after release.
            var opts = new Dictionary<string, object>
            {
                { Bugsee.Contracts.Options.Options.CaptureVideoMode, 99 },
                { Bugsee.Contracts.Options.Options.CaptureVideoQuality, 1 },
                { Bugsee.Contracts.Options.Options.CaptureLogsLevel, 99 },
                { Bugsee.Contracts.Options.Options.ReportingDefaultBugPriority, Bugsee.Contracts.Options.IssueSeverity.Blocker },
            };
            Call("ToJavaMap", opts);
            var log = Call<Bugsee.Contracts.Exchange.ILogEvent>("Log", Event());
            log.Level = Bugsee.Contracts.Options.LogLevel.Warning;
            log.Level = Bugsee.Contracts.Options.LogLevel.Verbose;
            Call("ObjectDictionaryToMap", new Dictionary<string, object> { { "a", 1 } });

            Assert.That(Call<int>("get_DoubleDisposes"), Is.EqualTo(0));
        }

        // ---- option coercion ----

        static FakeJava.J OptionValue(IDictionary<string, object> options, string key)
        {
            var map = (FakeJava.J)Call("ToJavaMap", options);
            return map.Map.Single(e => e.Key == key).Value;
        }

        [Test]
        public void Enum_options_become_sdk_enum_objects_from_an_int_a_long_a_byte_or_the_enum()
        {
            var key = Bugsee.Contracts.Options.Options.CaptureVideoMode;
            foreach (var value in new object[] { 20, 20L, (byte)20, Bugsee.Contracts.Options.VideoMode.Fullscreen })
            {
                var j = OptionValue(new Dictionary<string, object> { { key, value } }, key);
                Assert.That(j.Class, Is.EqualTo("com.bugsee.library.contracts.options.VideoMode"), value.GetType().Name);
                Assert.That(j.Value, Is.EqualTo("Fullscreen"), value.GetType().Name);
            }
        }

        [Test]
        public void An_unknown_enum_int_falls_back_to_the_documented_default_passed_to_the_sdk()
        {
            var opts = new Dictionary<string, object>
            {
                { Bugsee.Contracts.Options.Options.CaptureVideoMode, 99 },
                { Bugsee.Contracts.Options.Options.CaptureVideoQuality, 99 },
                { Bugsee.Contracts.Options.Options.CaptureVideoFrameRate, 99 },
                { Bugsee.Contracts.Options.Options.CaptureLogsLevel, 99 },
                { Bugsee.Contracts.Options.Options.ReportingDefaultBugPriority, 99 },
            };
            var map = (FakeJava.J)Call("ToJavaMap", opts);
            string Of(string key) => map.Map.Single(e => e.Key == key).Value.Value.ToString();

            Assert.That(Of(Bugsee.Contracts.Options.Options.CaptureVideoMode), Is.EqualTo("V2"));
            Assert.That(Of(Bugsee.Contracts.Options.Options.CaptureVideoQuality), Is.EqualTo("Default"));
            Assert.That(Of(Bugsee.Contracts.Options.Options.CaptureVideoFrameRate), Is.EqualTo("High"));
            Assert.That(Of(Bugsee.Contracts.Options.Options.CaptureLogsLevel), Is.EqualTo("Info"));
            Assert.That(Of(Bugsee.Contracts.Options.Options.ReportingDefaultBugPriority), Is.EqualTo("High"));
        }

        [Test]
        public void The_three_severity_options_and_the_log_level_enum_value_are_coerced()
        {
            var opts = new Dictionary<string, object>
            {
                { Bugsee.Contracts.Options.Options.ReportingDefaultCrashPriority, Bugsee.Contracts.Options.IssueSeverity.Blocker },
                { Bugsee.Contracts.Options.Options.ReportingDefaultErrorPriority, 1 },
                { Bugsee.Contracts.Options.Options.CaptureLogsLevel, Bugsee.Contracts.Options.LogLevel.Debug },
            };
            var map = (FakeJava.J)Call("ToJavaMap", opts);
            string Of(string key) => map.Map.Single(e => e.Key == key).Value.Value.ToString();

            Assert.That(Of(Bugsee.Contracts.Options.Options.ReportingDefaultCrashPriority), Is.EqualTo("Blocker"));
            Assert.That(Of(Bugsee.Contracts.Options.Options.ReportingDefaultErrorPriority), Is.EqualTo("VeryLow"));
            Assert.That(Of(Bugsee.Contracts.Options.Options.CaptureLogsLevel), Is.EqualTo("Debug"));
        }

        [Test]
        public void Plain_options_are_boxed_and_null_empty_key_and_unsupported_values_are_skipped()
        {
            var opts = new Dictionary<string, object>
            {
                { "b", true }, { "i", 1 }, { "l", 2L }, { "f", 3f }, { "d", 4.0 }, { "s", "x" },
                { "nul", null }, { "", 5 }, { "weird", new object() },
            };
            var map = (FakeJava.J)Call("ToJavaMap", opts);

            Assert.That(map.Map.ToDictionary(e => e.Key, e => e.Value.Class), Is.EqualTo(new Dictionary<string, string>
            {
                { "b", "java.lang.Boolean" }, { "i", "java.lang.Integer" }, { "l", "java.lang.Long" },
                { "f", "java.lang.Float" }, { "d", "java.lang.Double" }, { "s", "java.lang.String" },
            }));
            var warnings = (List<string>)_probe.GetProperty("Warnings").GetValue(null);
            Assert.That(warnings.Count, Is.EqualTo(1));
            Assert.That(warnings[0], Does.Contain("weird"));
            Assert.That(((FakeJava.J)Call("ToJavaMap", new object[] { null })).Map, Is.Empty);
        }

        // ---- exchange events ----

        static FakeJava.J Event(params (string key, object value)[] fields)
        {
            var e = new FakeJava.J { Class = "event" };
            foreach (var f in fields) e.F[f.key] = f.value;
            return e;
        }

        [Test]
        public void A_network_event_reads_and_writes_through_to_the_native_object()
        {
            var native = Event(("id", "n1"), ("mechanism", "UnityWebRequest"), ("method", "GET"), ("url", "http://a"),
                ("size", 10L), ("responseCode", 200), ("override", false));
            var ev = Call<Bugsee.Contracts.Exchange.INetworkEvent>("Network", native);

            Assert.That(ev.Id, Is.EqualTo("n1"));
            Assert.That(ev.Mechanism, Is.EqualTo("UnityWebRequest"));
            Assert.That(ev.Method, Is.EqualTo("GET"));
            Assert.That(ev.Url, Is.EqualTo("http://a"));
            Assert.That(ev.Size, Is.EqualTo(10L));
            Assert.That(ev.ResponseCode, Is.EqualTo(200));

            ev.Url = "http://b";
            ev.Body = "body";
            ev.Size = 99L;
            ev.ResponseCode = 404;
            ev.StatusText = "Not Found";
            ev.ErrorShortMessage = "short";
            ev.ErrorDescription = "long";
            ev.IsOverride = true;
            ev.Headers = new Dictionary<string, string> { { "H", "v" } };

            Assert.That(native.F["url"], Is.EqualTo("http://b"));
            Assert.That(native.F["body"], Is.EqualTo("body"));
            Assert.That(native.F["size"], Is.EqualTo(99L));
            Assert.That(native.F["responseCode"], Is.EqualTo(404));
            Assert.That(native.F["statusText"], Is.EqualTo("Not Found"));
            Assert.That(native.F["errorShortMessage"], Is.EqualTo("short"));
            Assert.That(native.F["errorDescription"], Is.EqualTo("long"));
            Assert.That(native.F["override"], Is.EqualTo(true));
            Assert.That(ev.IsOverride, Is.True);
            Assert.That(ev.Headers["H"], Is.EqualTo("v"));
        }

        [Test]
        public void A_network_event_stage_is_parsed_from_the_enum_name_with_a_completed_fallback()
        {
            Bugsee.Contracts.Exchange.NetworkEventStage StageOf(object stage)
            {
                var ev = Call<Bugsee.Contracts.Exchange.INetworkEvent>("Network", Event(("networkEventType", stage)));
                return ev.Stage;
            }

            Assert.That(StageOf(new FakeJava.J { Class = "NetworkEventType", Value = "Redirect" }),
                Is.EqualTo(Bugsee.Contracts.Exchange.NetworkEventStage.Redirect));
            Assert.That(StageOf(new FakeJava.J { Class = "NetworkEventType", Value = "WebSocket" }),
                Is.EqualTo(Bugsee.Contracts.Exchange.NetworkEventStage.WebSocket));
            Assert.That(StageOf(new FakeJava.J { Class = "NetworkEventType", Value = "SomethingNew" }),
                Is.EqualTo(Bugsee.Contracts.Exchange.NetworkEventStage.RequestCompleted));
            Assert.That(StageOf(null), Is.EqualTo(Bugsee.Contracts.Exchange.NetworkEventStage.RequestCompleted));
        }

        [Test]
        public void A_released_event_throws_ObjectDisposedException_instead_of_touching_the_native_object()
        {
            var ev = Call<Bugsee.Contracts.Exchange.INetworkEvent>("Network", Event(("url", "u")));
            Assert.That(ev.Url, Is.EqualTo("u"));
            Call("Release", ev);
            Assert.Throws<ObjectDisposedException>(() => { var _ = ev.Url; });
            Assert.Throws<ObjectDisposedException>(() => ev.Url = "x");

            var log = Call<Bugsee.Contracts.Exchange.ILogEvent>("Log", Event(("message", "m")));
            Call("Release", log);
            Assert.Throws<ObjectDisposedException>(() => { var _ = log.Message; });

            var crumb = Call<Bugsee.Contracts.Exchange.IBreadcrumb>("Breadcrumb", Event(("message", "m")));
            Call("Release", crumb);
            Assert.Throws<ObjectDisposedException>(() => { var _ = crumb.Message; });
        }

        [Test]
        public void A_log_event_level_goes_through_the_sdk_enum_and_reads_back_its_raw_value()
        {
            var native = Event(("message", "hello"));
            var ev = Call<Bugsee.Contracts.Exchange.ILogEvent>("Log", native);
            Assert.That(ev.Message, Is.EqualTo("hello"));
            ev.Message = "changed";
            Assert.That(native.F["message"], Is.EqualTo("changed"));

            ev.Level = Bugsee.Contracts.Options.LogLevel.Warning;
            var javaLevel = (FakeJava.J)native.F["level"];
            Assert.That(javaLevel.Class, Is.EqualTo("com.bugsee.library.contracts.options.LogLevel"));
            Assert.That(javaLevel.Value, Is.EqualTo("Warning"));
            Assert.That(ev.Level, Is.EqualTo(Bugsee.Contracts.Options.LogLevel.Warning));

            // No level on the native event reads as Info.
            Assert.That(Call<Bugsee.Contracts.Exchange.ILogEvent>("Log", Event()).Level, Is.EqualTo(Bugsee.Contracts.Options.LogLevel.Info));
        }

        [Test]
        public void A_breadcrumb_maps_levels_both_ways_and_round_trips_its_data()
        {
            var native = Event(("timestamp", 5L), ("category", "c"), ("type", "t"), ("message", "m"));
            var ev = Call<Bugsee.Contracts.Exchange.IBreadcrumb>("Breadcrumb", native);
            Assert.That(ev.Timestamp, Is.EqualTo(5L));
            Assert.That(ev.Category, Is.EqualTo("c"));
            Assert.That(ev.Type, Is.EqualTo("t"));

            var read = new Dictionary<string, Bugsee.Contracts.Options.LogLevel?>
            {
                { "DEBUG", Bugsee.Contracts.Options.LogLevel.Debug }, { "INFO", Bugsee.Contracts.Options.LogLevel.Info },
                { "WARNING", Bugsee.Contracts.Options.LogLevel.Warning }, { "ERROR", Bugsee.Contracts.Options.LogLevel.Error },
                { "FATAL", Bugsee.Contracts.Options.LogLevel.Error }, { "TRACE", null },
            };
            foreach (var kv in read)
            {
                native.F["level"] = new FakeJava.J { Class = "Breadcrumb$Level", Value = kv.Key };
                Assert.That(ev.Level, Is.EqualTo(kv.Value), kv.Key);
            }
            native.F["level"] = null;
            Assert.That(ev.Level, Is.Null);

            var write = new[]
            {
                (Bugsee.Contracts.Options.LogLevel.Verbose, "DEBUG"), (Bugsee.Contracts.Options.LogLevel.Debug, "DEBUG"),
                (Bugsee.Contracts.Options.LogLevel.Info, "INFO"), (Bugsee.Contracts.Options.LogLevel.Warning, "WARNING"),
                (Bugsee.Contracts.Options.LogLevel.Error, "ERROR"),
            };
            foreach (var w in write)
            {
                ev.Level = w.Item1;
                Assert.That(((FakeJava.J)native.F["level"]).Value, Is.EqualTo(w.Item2), w.Item1.ToString());
            }
            ev.Level = null;
            Assert.That(native.F["level"], Is.Null);

            ev.Timestamp = 6L;
            ev.Category = "c2";
            ev.Type = "t2";
            ev.Message = "m2";
            Assert.That(native.F["timestamp"], Is.EqualTo(6L));
            Assert.That(native.F["category"], Is.EqualTo("c2"));
            Assert.That(native.F["type"], Is.EqualTo("t2"));
            Assert.That(native.F["message"], Is.EqualTo("m2"));

            ev.Data = new Dictionary<string, object> { { "k", 1 }, { "s", "v" } };
            var data = ev.Data;
            Assert.That(data["k"], Is.EqualTo(1));
            Assert.That(data["s"], Is.EqualTo("v"));
        }

        // ---- the fake Java side ----

        /// <summary>
        /// A deliberately small Java object model: boxed primitives, HashMap and its iterator and entries, the SDK enums
        /// (unknown ints fall back to the default argument like the SDK's fromIntValue / fromRawValue) and plain
        /// bean-like events (getX / isX / setX over a field bag).
        /// </summary>
        internal static class FakeJava
        {
            internal sealed class J
            {
                public string Class;
                public object Value;
                public Dictionary<string, object> F = new Dictionary<string, object>();
                public List<KeyValuePair<string, J>> Map;
                public int Pos;
                public List<J> Items;
            }

            // The one constructor each java.lang wrapper has for a primitive: JNI binds the C# runtime type of the argument.
            static readonly Dictionary<string, Type> WrapperCtor = new Dictionary<string, Type>
            {
                { "java.lang.String", typeof(string) }, { "java.lang.Boolean", typeof(bool) }, { "java.lang.Integer", typeof(int) },
                { "java.lang.Long", typeof(long) }, { "java.lang.Float", typeof(float) }, { "java.lang.Double", typeof(double) },
                { "java.lang.Byte", typeof(sbyte) }, { "java.lang.Short", typeof(short) }, { "java.lang.Character", typeof(char) },
            };

            // The accessor of each wrapper and the type its JNI signature returns.
            static readonly Dictionary<string, Type> WrapperGetter = new Dictionary<string, Type>
            {
                { "booleanValue", typeof(bool) }, { "intValue", typeof(int) }, { "longValue", typeof(long) },
                { "floatValue", typeof(float) }, { "doubleValue", typeof(double) }, { "byteValue", typeof(sbyte) },
                { "shortValue", typeof(short) }, { "charValue", typeof(char) },
            };

            public static object Construct(string cls, object[] args)
            {
                if (cls == "java.util.HashMap") return new J { Class = cls, Map = new List<KeyValuePair<string, J>>() };
                if (WrapperCtor.TryGetValue(cls, out var want) && (args.Length != 1 || args[0] == null || args[0].GetType() != want))
                    throw new InvalidOperationException("JNI: no constructor " + cls + "(" + string.Join(",", args.Select(a => a?.GetType().Name ?? "null")) + ")");
                return new J { Class = cls, Value = args.Length > 0 ? args[0] : null };
            }

            static string KeyOf(object k) => k is J j ? Convert.ToString(j.Value) : (string)k;

            public static object Invoke(object model, string method, object[] args, Type ret)
            {
                var m = (J)model;
                if (m.Class != null && m.Class.StartsWith("java.lang.", StringComparison.Ordinal) && WrapperGetter.TryGetValue(method, out var sig) && sig != ret)
                    throw new InvalidOperationException("JNI: " + m.Class + "." + method + " returns " + sig.Name + ", not " + ret.Name);
                switch (method)
                {
                    case "getClass": return new J { Class = "java.lang.Class", Value = m.Class };
                    case "getName" when m.Class == "java.lang.Class": return m.Value;
                    case "toString": return Convert.ToString(m.Value);
                    case "name": return Convert.ToString(m.Value);
                    case "booleanValue": case "intValue": case "longValue": case "floatValue":
                    case "doubleValue": case "byteValue": case "shortValue": case "charValue":
                        return m.Value;
                    case "put" when m.Map != null:
                        m.Map.Add(new KeyValuePair<string, J>(KeyOf(args[0]), (J)args[1]));
                        return null;
                    case "entrySet" when m.Map != null:
                        return new J
                        {
                            Class = "java.util.HashMap$EntrySet",
                            Items = m.Map.Select(e => new J { Class = "java.util.Map$Entry", F = { { "k", e.Key }, { "v", e.Value } } }).ToList(),
                        };
                    case "iterator": return new J { Class = "java.util.Iterator", Items = m.Items };
                    case "hasNext": return m.Pos < m.Items.Count;
                    case "next": return m.Items[m.Pos++];
                    case "getKey": return new J { Class = "java.lang.String", Value = m.F["k"] };
                    case "getValue" when m.Class == "java.util.Map$Entry": return m.F["v"];
                    case "getValue" when m.F.ContainsKey("raw"): return m.F["raw"];
                    case "getValue" when m.Class != null && m.Class.EndsWith(".LogLevel"):
                        return (sbyte)(int)Enum.Parse(typeof(Bugsee.Contracts.Options.LogLevel), (string)m.Value);
                }

                if (method.StartsWith("set", StringComparison.Ordinal))
                {
                    m.F[Field(method.Substring(3))] = args[0];
                    return null;
                }

                if (method.StartsWith("get", StringComparison.Ordinal))
                    return m.F.TryGetValue(Field(method.Substring(3)), out var v) ? v : null;
                if (method.StartsWith("is", StringComparison.Ordinal))
                    return m.F.TryGetValue(Field(method.Substring(2)), out var b) ? b : false;
                throw new InvalidOperationException("fake Java: no method " + m.Class + "." + method);
            }

            static string Field(string n) => char.ToLowerInvariant(n[0]) + n.Substring(1);

            public static object InvokeStatic(string cls, string method, object[] args, Type ret)
            {
                var enumType = cls.EndsWith(".VideoMode") ? typeof(Bugsee.Contracts.Options.VideoMode)
                    : cls.EndsWith(".VideoQuality") ? typeof(Bugsee.Contracts.Options.VideoQuality)
                    : cls.EndsWith(".FrameRate") ? typeof(Bugsee.Contracts.Options.FrameRate)
                    : cls.EndsWith(".IssueSeverity") ? typeof(Bugsee.Contracts.Options.IssueSeverity)
                    : cls.EndsWith(".LogLevel") ? typeof(Bugsee.Contracts.Options.LogLevel)
                    : null;
                if (enumType != null && (method == "fromIntValue" || method == "fromRawValue"))
                {
                    var wantArg = method == "fromRawValue" ? typeof(sbyte) : typeof(int);
                    if (args.Length < 1 || args.Length > 2 || args[0]?.GetType() != wantArg)
                        throw new InvalidOperationException("JNI: " + cls + "." + method + " takes (" + wantArg.Name + "[, default])");
                    var raw = Convert.ToInt32(args[0]);
                    if (Enum.IsDefined(enumType, raw))
                    {
                        var j = new J { Class = cls, Value = Enum.GetName(enumType, raw) };
                        if (method == "fromRawValue") j.F["raw"] = (sbyte)raw;
                        return j;
                    }

                    // The SDK returns the fallback it was given, or its own when none was.
                    return args.Length > 1 ? args[1] : null;
                }

                if (cls.EndsWith("Breadcrumb$Level") && method == "valueOf")
                    return new J { Class = cls, Value = (string)args[0] };
                throw new InvalidOperationException("fake Java: no static " + cls + "." + method);
            }
        }
    }
}
