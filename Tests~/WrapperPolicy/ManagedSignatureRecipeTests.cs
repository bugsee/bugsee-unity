using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class ManagedSignatureRecipeTests
    {
        const string T = "System.InvalidOperationException";

        static string Sig(string[] traces, string[] addresses = null, string uuid = "", string caller = null) =>
            ManagedSignatureRecipe.Compute(T, traces, addresses, uuid, caller);

        static string Mono(string method, string file, int line) => "at " + method + " (System.String m) [0x0000c] in " + file + ":" + line;

        [Test]
        public void Caller_tail_of_a_caught_exception_is_cut_so_two_call_paths_agree()
        {
            var main = new[]
            {
                "App.Site.Throw (System.String m) (at /Users/a/proj/Assets/Site.cs:10)",
                "App.Site.Observe (System.String m) (at /Users/a/proj/Assets/Site.cs:20)",
                "App.Site+<Routine>d__1.MoveNext () (at /Users/a/proj/Assets/Site.cs:30)",
                "UnityEngine.SetupCoroutine.InvokeMoveNext (System.Collections.IEnumerator e) (at <aa>:0)",
            };
            var worker = new[]
            {
                "App.Site.Throw (System.String m) (at /Users/a/proj/Assets/Site.cs:10)",
                "App.Site.Observe (System.String m) (at /Users/a/proj/Assets/Site.cs:20)",
                "App.Site+<>c__DisplayClass.<Run>b__0 () (at /Users/a/proj/Assets/Site.cs:40)",
                "System.Threading.ThreadHelper.ThreadStart ()",
            };
            // The stack live where the payload is built: Observe is the catch frame, its callers differ.
            var liveMain = "at Bugsee.Internal.ManagedExceptionPayload.FromException (System.Exception e) [0x1] in <x>:0\n"
                + "at App.Site.Observe (System.String m) (at Site.cs:22)\n"
                + "at App.Site+<Routine>d__1.MoveNext () (at Site.cs:31)\n"
                + "at UnityEngine.SetupCoroutine.InvokeMoveNext (System.Collections.IEnumerator e) (at <aa>:0)";
            var liveWorker = "at Bugsee.Internal.ManagedExceptionPayload.FromException (System.Exception e) [0x1] in <x>:0\n"
                + "at App.Site.Observe (System.String m) (at Site.cs:22)\n"
                + "at App.Site+<>c__DisplayClass.<Run>b__0 () (at Site.cs:41)\n"
                + "at System.Threading.ThreadHelper.ThreadStart ()";
            // Both signatures must match even if the user caller frames (Routine / b__0) were kept: they are cut by the trim.
            Assert.That(Sig(main, caller: liveMain), Is.EqualTo(Sig(worker, caller: liveWorker)));
            Assert.That(ManagedSignatureRecipe.TrimCallerTail(main, liveMain), Is.EqualTo(2));
        }

        [Test]
        public void Throw_and_catch_in_one_method_keeps_the_frame()
        {
            var t = new[] { "at A.M () (at A.cs:5)", "at Caller.X () (at C.cs:9)" };
            var live = "at Bugsee.Internal.P.F ()\nat A.M () (at A.cs:6)\nat Caller.X () (at C.cs:9)";
            Assert.That(ManagedSignatureRecipe.TrimCallerTail(t, live), Is.EqualTo(1));
            Assert.That(ManagedSignatureRecipe.TrimCallerTail(new[] { "at A.M () (at A.cs:5)" }, live), Is.EqualTo(1));
        }

        [Test]
        public void No_common_tail_or_no_live_stack_keeps_everything()
        {
            var t = new[] { "at A.M () (at A.cs:5)", "at B.N () (at B.cs:5)" };
            Assert.That(ManagedSignatureRecipe.TrimCallerTail(t, null), Is.EqualTo(2));
            Assert.That(ManagedSignatureRecipe.TrimCallerTail(t, "at Other.O ()"), Is.EqualTo(2));
        }

        [Test]
        public void Il_offsets_and_directories_do_not_matter_for_file_line()
        {
            var a = new[] { Mono("App.Site.Throw", "/Users/alice/proj/Assets/Site.cs", 10) };
            var b = new[] { "at App.Site.Throw (System.String m) [0x00099] in C:\\build\\agent\\Assets\\Site.cs:10" };
            Assert.That(Sig(a), Is.EqualTo(Sig(b)));
        }

        [Test]
        public void Distinct_lines_in_one_method_give_distinct_signatures()
        {
            Assert.That(Sig(new[] { "at A.M () (at A.cs:5)" }), Is.Not.EqualTo(Sig(new[] { "at A.M () (at A.cs:6)" })));
        }

        [Test]
        public void Type_is_an_input_and_is_normalised_to_the_short_name()
        {
            var t = new[] { "at A.M () (at A.cs:5)" };
            Assert.That(ManagedSignatureRecipe.Compute("Other", t, null, "", null), Is.Not.EqualTo(Sig(t)));
            var full = ManagedSignatureRecipe.Compute("System.NullReferenceException", t, null, "", null);
            Assert.That(ManagedSignatureRecipe.Compute("NullReferenceException", t, null, "", null), Is.EqualTo(full));
            Assert.That(ManagedSignatureRecipe.Compute("My.Ex`1[[System.Int32, mscorlib, Version=4.0.0.0]]", t, null, "", null),
                Is.EqualTo(ManagedSignatureRecipe.Compute("Other.Ex", t, null, "", null)));
        }

        [Test]
        public void Engine_frames_are_dropped_wherever_they_sit()
        {
            var user = new[] { "at A.M () (at A.cs:5)" };
            var withEngine = new[]
            {
                "at System.Threading.Foo.Bar ()", "at UnityEngine.Debug.Log () (at <h>:0)", "at A.M () (at A.cs:5)",
                "at Bugsee.Internal.X.Y ()", "(wrapper managed-to-native) Foo.Bar ()",
            };
            Assert.That(Sig(withEngine), Is.EqualTo(Sig(user)));
        }

        [Test]
        public void Sample_namespace_counts_as_user_code_but_other_bugsee_frames_do_not()
        {
            Assert.That(ManagedSignatureRecipe.IsEngineFrame("at Bugsee.Sample.X.Y () (at X.cs:1)"), Is.False);
            Assert.That(ManagedSignatureRecipe.IsEngineFrame("at Bugsee.Bugsee.LogException ()"), Is.True);
        }

        [Test]
        public void Addresses_win_when_the_uuid_is_known_and_are_normalized()
        {
            var t = new[] { "at A.M () (at <h>:0)", "at B.N () (at <h>:0)" };
            var a = Sig(t, new[] { "0x00012168C", "0x121420" }, "ABCD");
            Assert.That(Sig(t, new[] { "0x12168c", "0x121420" }, "abcd"), Is.EqualTo(a));
            Assert.That(Sig(t, new[] { "0x12168d", "0x121420" }, "abcd"), Is.Not.EqualTo(a));
            Assert.That(Sig(t, new[] { "0x12168c", "0x121420" }, "other"), Is.Not.EqualTo(a));
            // without a UUID the addresses cannot be used: falls to the weak recipe
            Assert.That(Sig(t, new[] { "0x12168c", "0x121420" }, ""), Is.EqualTo(Sig(t)));
        }

        [Test]
        public void Address_recipe_ignores_file_and_line()
        {
            var one = Sig(new[] { "at A.M () (at A.cs:5)" }, new[] { "0x10" }, "u");
            var two = Sig(new[] { "at A.M () (at A.cs:99)" }, new[] { "0x10" }, "u");
            Assert.That(one, Is.EqualTo(two));
        }

        [Test]
        public void Weak_recipe_uses_method_tokens_only_over_merging_one_method()
        {
            var a = Sig(new[] { "at A.M (System.Int32 x) [0x00001] in <h>:0" });
            var b = Sig(new[] { "at A.M (System.String y) [0x00099] in <h>:0" });
            Assert.That(a, Is.EqualTo(b));
            Assert.That(Sig(new[] { "at A.N () in <h>:0" }), Is.Not.EqualTo(a));
        }

        [Test]
        public void Empty_user_stack_is_type_only()
        {
            Assert.That(Sig(new string[0]), Is.EqualTo(Sig(new[] { "at System.X.Y ()" })));
            Assert.That(Sig(null), Is.EqualTo(Sig(new string[0])));
        }

        [Test]
        public void Only_the_first_eight_user_frames_count()
        {
            var many = new string[10];
            var eight = new string[8];
            for (var i = 0; i < 10; i++)
            {
                many[i] = "at A.M" + i + " () (at A.cs:" + (i + 1) + ")";
                if (i < 8) eight[i] = many[i];
            }
            Assert.That(Sig(many), Is.EqualTo(Sig(eight)));
        }

        [Test]
        public void Output_is_sha256_hex()
        {
            Assert.That(Sig(new[] { "at A.M () (at A.cs:5)" }), Does.Match("^[0-9a-f]{64}$"));
        }

        [Test]
        public void Mixed_il2cpp_frames_address_recipe_needs_the_throw_site_address()
        {
            // Throw site has file:line (so no address was paired), the dispatcher below has an address only.
            var siteA = new[] { "at A.ThrowOne () (at A.cs:5)", "at D.Dispatch () (at <h>:0)" };
            var siteB = new[] { "at A.ThrowTwo () (at A.cs:9)", "at D.Dispatch () (at <h>:0)" };
            var addr = new[] { null, "0x777" };
            Assert.That(Sig(siteA, addr, "u"), Is.Not.EqualTo(Sig(siteB, addr, "u")));
        }

        [Test]
        public void Address_recipe_keeps_the_line_of_addressless_frames()
        {
            var one = new[] { "at A.M () (at <h>:0)", "at B.N () (at B.cs:3)" };
            var two = new[] { "at A.M () (at <h>:0)", "at B.N () (at B.cs:4)" };
            var addr = new[] { "0x10", null };
            Assert.That(Sig(one, addr, "u"), Is.Not.EqualTo(Sig(two, addr, "u")));
        }

        [Test]
        public void Throw_frames_still_on_the_live_stack_are_not_trimmed()
        {
            var t = new[] { "at A.Throw () (at A.cs:5)", "at A.Observe () (at A.cs:9)", "at R.Run () (at R.cs:1)" };
            var live = "at Bugsee.Internal.P.F ()\nat A.Filter () (at A.cs:8)\nat A.Throw () (at A.cs:5)\nat A.Observe () (at A.cs:9)\nat R.Run () (at R.cs:1)";
            Assert.That(ManagedSignatureRecipe.TrimCallerTail(t, live), Is.EqualTo(3));
        }

        [Test]
        public void Matching_stops_at_a_bugsee_frame_of_the_live_stack()
        {
            var t = new[] { "at A.M () (at A.cs:5)", "at Bugsee.Internal.P.F ()" };
            var live = "at Bugsee.Internal.P.F ()";
            Assert.That(ManagedSignatureRecipe.TrimCallerTail(t, live), Is.EqualTo(2));
        }

        [Test]
        public void Recursion_and_same_named_overloads_match_by_method_name()
        {
            var t = new[] { "at A.R (System.Int32 n) (at A.cs:5)", "at A.R (System.Int32 n) (at A.cs:6)", "at A.Top () (at A.cs:9)" };
            var live = "at Bugsee.Internal.P.F ()\nat A.R (System.String s) (at A.cs:6)\nat A.Top () (at A.cs:9)";
            // Documented: overloads match by name, the cut is at most the shared tail below the catch frame.
            Assert.That(ManagedSignatureRecipe.TrimCallerTail(t, live), Is.EqualTo(2));
        }

        [Test]
        public void Separators_and_rethrow_lines_are_not_frames()
        {
            var plain = new[] { "at A.M () (at A.cs:5)" };
            var withSeps = new[]
            {
                "Rethrow as InvalidOperationException: boom", "at A.M () (at A.cs:5)",
                "--- End of stack trace from previous location where exception was thrown ---",
            };
            Assert.That(Sig(withSeps), Is.EqualTo(Sig(plain)));
        }

        [Test]
        public void Log_text_and_thrown_text_of_one_site_agree_when_the_file_and_line_are_there()
        {
            var thrown = new[] { "at App.Site.Throw (System.String m) [0x0000c] in /Users/a/Assets/Site.cs:10" };
            var logged = new[] { "App.Site.Throw (System.String m) (at Assets/Site.cs:10)" };
            Assert.That(ManagedSignatureRecipe.Compute("System.InvalidOperationException", thrown, null, "", null),
                Is.EqualTo(ManagedSignatureRecipe.Compute("InvalidOperationException", logged, null, "", null)));
        }

        [Test]
        public void Method_token_and_file_line_parse_both_unity_formats()
        {
            Assert.That(ManagedSignatureRecipe.MethodToken("at A.B.C (System.String s) [0x0000c] in <h>:0"), Is.EqualTo("A.B.C"));
            Assert.That(ManagedSignatureRecipe.MethodToken("A.B.C () (at Assets/X.cs:3)"), Is.EqualTo("A.B.C"));
            string f; int l;
            Assert.That(ManagedSignatureRecipe.TryFileLine("A.B.C () (at Assets/Scripts/X.cs:3)", out f, out l), Is.True);
            Assert.That(f + ":" + l, Is.EqualTo("X.cs:3"));
            Assert.That(ManagedSignatureRecipe.TryFileLine("A () (at <abc>:0)", out f, out l), Is.False);
            Assert.That(ManagedSignatureRecipe.TryFileLine("A () [0x1] in /p/X.cs:0", out f, out l), Is.False);
        }
    }
}
