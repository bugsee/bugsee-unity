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
        public void Segments_joined_without_a_separator_are_cut_after_the_state_machine_line()
        {
            // old segment ended in <Run>d__1.MoveNext so StackTrace printed no separator; old_visible (2) equals
            // the live segment's hidden frames (2), the accidental case: counts match, yet the old lines must not
            // get the live IPs.
            var lines = new[]
            {
                Line("Old.Throw ()"), Line("Ns.Old.<Run>d__1.MoveNext ()"),
                Line("New.Caller ()"), Line("New.Root ()"),
            };
            var a = ManagedAddressPairing.Pair(lines, new ulong[] { 0x1, 0x2, 0x3000, 0x4000 });
            Assert.That(a, Is.EqualTo(new[] { null, null, "0x3000", "0x4000" }));
        }

        [Test]
        public void A_state_machine_line_at_the_bottom_does_not_cut_the_segment()
        {
            var lines = new[] { Line("A.Throw ()"), Line("Ns.C.<Go>d__2.MoveNext ()") };
            Assert.That(ManagedAddressPairing.Pair(lines, new ulong[] { 0x1000, 0x2000 }), Is.EqualTo(new[] { "0x1000", "0x2000" }));
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
    }
}
