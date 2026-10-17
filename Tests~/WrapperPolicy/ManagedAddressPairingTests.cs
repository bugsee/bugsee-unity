using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class ManagedAddressPairingTests
    {
        const string Hash = "<4606fc0550234d47abe3e20e995d92cf>";

        static string Line(string method) => "at " + method + " [0x00000] in " + Hash + ":0";

        static string[] Lines(params string[] methods)
        {
            var l = new string[methods.Length];
            for (var i = 0; i < l.Length; i++) l[i] = Line(methods[i]);
            return l;
        }

        [Test]
        public void Equal_counts_pair_index_for_index_newest_first()
        {
            var a = ManagedAddressPairing.Pair(Lines("A.Throw ()", "B.Caller ()"), new ulong[] { 0x12168c, 0x121420 });
            Assert.That(a, Is.EqualTo(new[] { "0x12168c", "0x121420" }));
        }

        [Test]
        public void Native_fewer_than_managed_sends_nothing()
        {
            var a = ManagedAddressPairing.Pair(Lines("A ()", "B ()", "C ()"), new ulong[] { 0x1000, 0x2000 });
            Assert.That(a, Is.EqualTo(new string[3]));
        }

        const string Sep = "--- End of stack trace from previous location where exception was thrown ---";

        [Test]
        public void Only_the_live_segment_after_a_separator_is_paired_bottom_anchored()
        {
            // text: old segment, separator, live segment; 2 native IPs belong to the live segment.
            var lines = new[] { Line("A.Inner ()"), Sep, Line("B.Outer ()") };
            var a = ManagedAddressPairing.Pair(lines, new ulong[] { 0x1000, 0x2000 });
            Assert.That(a, Is.EqualTo(new[] { null, "0x2000" }));
        }

        [Test]
        public void Await_shape_hidden_frames_at_the_top_of_the_live_segment_do_not_shift_the_rest()
        {
            // live trace: 3 hidden throw helpers (no text) + Caller + Root; the old segment has 2 lines.
            var lines = new[] { Line("X.Fail ()"), Line("X.Run ()"), Sep, Line("C.Caller ()"), Line("C.Root ()") };
            var ips = new ulong[] { 0x10, 0x20, 0x30, 0x40, 0x50 };
            var a = ManagedAddressPairing.Pair(lines, ips);
            Assert.That(a, Is.EqualTo(new[] { null, null, "0x40", "0x50" }));
        }

        [Test]
        public void Hidden_throw_helper_without_any_segment_still_pairs_bottom_anchored()
        {
            var a = ManagedAddressPairing.Pair(Lines("A.Caller ()", "B.Root ()"), new ulong[] { 0x1, 0x2000, 0x3000 });
            Assert.That(a, Is.EqualTo(new[] { "0x2000", "0x3000" }));
        }

        [Test]
        public void Live_segment_longer_than_the_ips_sends_nothing()
        {
            var lines = new[] { Line("A ()"), Sep, Line("B ()"), Line("C ()"), Line("D ()") };
            Assert.That(ManagedAddressPairing.Pair(lines, new ulong[] { 0x1000, 0x2000 }), Is.EqualTo(new string[4]));
        }

        [Test]
        public void A_coroutine_or_iterator_MoveNext_line_does_not_cut_a_single_segment()
        {
            // Device: iterator frames print as "<Coroutine>d__8.MoveNext" and the counts match.
            var lines = new[] { Line("A.Throw ()"), Line("Ns.C+<Coroutine>d__8.MoveNext ()"), Line("UnityEngine.SetupCoroutine.InvokeMoveNext ()") };
            Assert.That(ManagedAddressPairing.Pair(lines, new ulong[] { 0x1000, 0x2000, 0x3000 }), Is.EqualTo(new[] { "0x1000", "0x2000", "0x3000" }));
        }

        [Test]
        public void Inner_exception_banner_counts_as_a_separator()
        {
            var lines = new[] { Line("A ()"), "--->", Line("B ()") };
            Assert.That(ManagedAddressPairing.Pair(lines, new ulong[] { 0x1000, 0x2000 }), Is.EqualTo(new[] { null, "0x2000" }));
        }

        [Test]
        public void Zero_ip_gets_no_address_but_neighbours_still_pair()
        {
            var a = ManagedAddressPairing.Pair(Lines("A ()", "B ()"), new ulong[] { 0, 0x2000 });
            Assert.That(a, Is.EqualTo(new[] { null, "0x2000" }));
        }

        [Test]
        public void No_native_ips_or_no_lines_sends_nothing()
        {
            Assert.That(ManagedAddressPairing.Pair(Lines("A ()"), null), Is.EqualTo(new string[1]));
            Assert.That(ManagedAddressPairing.Pair(Lines("A ()"), new ulong[0]), Is.EqualTo(new string[1]));
            Assert.That(ManagedAddressPairing.Pair(null, new ulong[] { 0x1000 }), Is.Empty);
        }

        [Test]
        public void Thirty_two_bit_offsets_are_formatted_without_sign_extension()
        {
            Assert.That(ManagedAddressPairing.FormatAddress(0xF0001234UL), Is.EqualTo("0xf0001234"));
            var a = ManagedAddressPairing.Pair(Lines("A ()"), new ulong[] { 0x8000abcd });
            Assert.That(a[0], Is.EqualTo("0x8000abcd"));
        }

        [Test]
        public void A_frame_with_a_real_source_location_is_not_given_a_return_address()
        {
            // usym expansion: raw_ip is a line-table address and the text already has the C# line.
            var lines = new[] { "at A.Throw () [0x00000] in /proj/Assets/A.cs:616", Line("B.Caller ()") };
            var a = ManagedAddressPairing.Pair(lines, new ulong[] { 0x1000, 0x2000 });
            Assert.That(a, Is.EqualTo(new[] { null, "0x2000" }));
        }

        [Test]
        public void Source_location_means_a_line_above_zero()
        {
            Assert.That(ManagedAddressPairing.HasSourceLocation(Line("A ()")), Is.False);
            Assert.That(ManagedAddressPairing.HasSourceLocation("at A () [0x00000] in /p/A.cs:0"), Is.False);
            // usym entry without a file name: a line but only the assembly hash
            Assert.That(ManagedAddressPairing.HasSourceLocation("at A () [0x00000] in " + Hash + ":42"), Is.True);
            Assert.That(ManagedAddressPairing.HasSourceLocation("at A () [0x00000] in /p/A.cs:12"), Is.True);
            Assert.That(ManagedAddressPairing.HasSourceLocation("at A ()"), Is.False);
        }

        [Test]
        public void Mono_method_start_is_stripped_so_it_is_never_read_as_an_address()
        {
            var s = ManagedAddressPairing.StripMonoMethodStarts("at A.B () <0x104a125e0 + 0x34> in <hash>:0");
            Assert.That(s, Does.Not.Contain("0x104a125e0"));
            Assert.That(s, Does.Contain("in <hash>:0"));
        }

        [Test]
        public void Thrown_exception_frames_take_addresses_only_from_the_pairing_never_from_text()
        {
            var text = Line("A.Throw () 0x0000000104a125e0") + "\n" + Line("B.Caller () <0x104a125e0 + 0x34>") + "\n";
            var set = ManagedFrameAssembly.FromStackTrace(text, null);
            Assert.That(set.Traces.Length, Is.EqualTo(2));
            Assert.That(set.Addresses, Is.EqualTo(new string[2]));

            var paired = ManagedFrameAssembly.FromStackTrace(text, new ulong[] { 0x10, 0x20 });
            Assert.That(paired.Addresses, Is.EqualTo(new[] { "0x10", "0x20" }));
        }

        [Test]
        public void Stack_text_assembly_drops_separators_and_pairs_the_live_segment()
        {
            var text = Line("A ()") + "\r\n" + Sep + "\r\n\r\n" + Line("B ()") + "\n";
            var set = ManagedFrameAssembly.FromStackTrace(text, new ulong[] { 0x1111, 0x2222 });
            Assert.That(set.Traces, Is.EqualTo(new[] { Line("A ()"), Line("B ()") }));
            Assert.That(set.Addresses, Is.EqualTo(new[] { null, "0x2222" }));
        }

        [Test]
        public void ToString_fallback_skips_the_header_and_never_gets_addresses()
        {
            var set = ManagedFrameAssembly.FromToString("System.Exception: boom\n" + Line("A ()") + "\n --- inner ---\n" + Line("B ()"));
            Assert.That(set.Traces, Is.EqualTo(new[] { Line("A ()"), Line("B ()") }));
            Assert.That(set.Addresses, Is.EqualTo(new string[2]));
        }

        [Test]
        public void Unity_log_text_keeps_native_pcs_but_not_il_offsets_or_mono_method_starts()
        {
            var set = ManagedFrameAssembly.FromLogText(
                "at A.B () [0x00023] in <hash>:0\n#00 pc 000000000012abcd /lib/libil2cpp.so\nat C.D () <0x104a125e0 + 0x34> in <h>:0\n");
            Assert.That(set.Addresses, Is.EqualTo(new[] { null, "0x000000000012abcd", null }));
        }

        [Test]
        public void Cause_json_is_appended_as_the_last_member_at_every_level()
        {
            var inner = ManagedPayloadJson.WithCause("{\"name\":\"B\",\"frames\":[]}", null);
            Assert.That(inner, Is.EqualTo("{\"name\":\"B\",\"frames\":[]}"));
            var outer = ManagedPayloadJson.WithCause("{\"name\":\"A\",\"frames\":[{\"trace\":\"x}\"}]}", inner);
            Assert.That(outer, Is.EqualTo("{\"name\":\"A\",\"frames\":[{\"trace\":\"x}\"}],\"cause\":{\"name\":\"B\",\"frames\":[]}}"));
            Assert.That(ManagedPayloadJson.WithCause("{}", "{\"a\":1}"), Is.EqualTo("{\"cause\":{\"a\":1}}"));
            Assert.That(ManagedPayloadJson.WithCause("not json", "{}"), Is.EqualTo("not json"));
        }

        [Test]
        public void Converted_async_names_are_ordinary_frames()
        {
            // IL2CPP prints an async method by its own name ("Ns.C.Run ()"), never as <Run>d__1.MoveNext.
            var lines = new[] { Line("Ns.C.Run ()"), Line("System.Threading.ExecutionContext.RunInternal ()"), Line("UnityEngine.UnitySynchronizationContext.Exec ()") };
            Assert.That(ManagedAddressPairing.Pair(lines, new ulong[] { 0x1, 0x2, 0x3 }), Is.EqualTo(new[] { "0x1", "0x2", "0x3" }));
        }

        [Test]
        public void A_separator_as_the_last_line_leaves_an_empty_live_segment_and_no_addresses()
        {
            var lines = new[] { Line("A ()"), Line("B ()"), Sep };
            Assert.That(ManagedAddressPairing.Pair(lines, new ulong[] { 0x1, 0x2 }), Is.EqualTo(new string[2]));
        }

        [Test]
        public void Pair_returns_one_entry_per_frame_line_for_every_shape()
        {
            var shapes = new[]
            {
                new[] { Line("A ()"), Sep, Line("B ()"), Line("C ()") },
                new[] { Sep, Line("A ()") },
                new[] { Line("A ()"), "--->", Line("B ()"), Sep },
                new string[0],
            };
            foreach (var lines in shapes)
            {
                var text = string.Join("\n", lines);
                var frames = ManagedFrameAssembly.FrameLines(ManagedFrameAssembly.StackLines(text));
                Assert.That(ManagedAddressPairing.Pair(lines, new ulong[] { 1, 2, 3 }).Length, Is.EqualTo(frames.Length));
                Assert.That(ManagedFrameAssembly.FromStackTrace(text, new ulong[] { 1, 2, 3 }).Addresses.Length, Is.EqualTo(frames.Length));
            }
        }

        static string Level(string name, string reason, string extra = "")
        {
            return "{\"name\":" + JsonSerializer.Serialize(name) + ",\"reason\":" + JsonSerializer.Serialize(reason) + ",\"frames\":[]" + extra + "}";
        }

        [Test]
        public void Chain_round_trips_through_a_real_parser_with_adversarial_strings()
        {
            var nasty = new[] { "q\"uote", "back\\slash", "brace } and { and [ ]", "ctl\u0001", "ls\u2028ps\u2029", "emoji \ud83d\ude00", "\"cause\":{\"x\":1}" };
            var levels = new List<string>();
            foreach (var n in nasty) levels.Add(Level("E", n));
            var doc = JsonDocument.Parse(ManagedPayloadJson.Chain(levels));
            var node = doc.RootElement;
            foreach (var n in nasty)
            {
                Assert.That(node.GetProperty("reason").GetString(), Is.EqualTo(n));
                if (!node.TryGetProperty("cause", out var next)) { Assert.That(n, Is.EqualTo(nasty[nasty.Length - 1])); break; }
                node = next;
            }
        }

        [Test]
        public void An_existing_cause_member_is_replaced_not_duplicated()
        {
            var withCause = Level("A", "r", ",\"cause\":{\"name\":\"old\",\"frames\":[{\"t\":\"}\"}]}");
            var json = ManagedPayloadJson.Chain(new[] { withCause, Level("B", "r2") });
            var doc = JsonDocument.Parse(json);
            Assert.That(doc.RootElement.GetProperty("cause").GetProperty("name").GetString(), Is.EqualTo("B"));
            Assert.That(json.Split(new[] { "\"cause\"" }, System.StringSplitOptions.None).Length, Is.EqualTo(2));
            Assert.That(ManagedPayloadJson.StripCause(withCause), Is.EqualTo(Level("A", "r")));
            Assert.That(ManagedPayloadJson.StripCause("{\"cause\":null,\"a\":1}"), Is.EqualTo("{\"a\":1}"));
        }

        [Test]
        public void Chain_is_linear_for_a_thousand_levels()
        {
            var levels = new List<string>();
            for (var i = 0; i < 1000; i++) levels.Add(Level("E" + i, new string('x', 700)));
            var sw = Stopwatch.StartNew();
            var json = ManagedPayloadJson.Chain(levels);
            sw.Stop();
            Assert.That(sw.ElapsedMilliseconds, Is.LessThan(500));
            Assert.That(json.Length, Is.LessThan(levels.Count * 800 + 100));
        }

        [Test]
        public void Node_budget_caps_the_payload_and_counts_siblings()
        {
            var b = new ManagedNodeBudget();
            var taken = 0;
            for (var i = 0; i < 1000 && b.TryTake(); i++) taken++;
            Assert.That(taken, Is.EqualTo(ManagedNodeBudget.DefaultMax));
            Assert.That(b.IsExhausted, Is.True);
            Assert.That(b.TryTake(), Is.False);
            Assert.That(new ManagedNodeBudget(0).TryTake(), Is.False);
        }
    }
}
