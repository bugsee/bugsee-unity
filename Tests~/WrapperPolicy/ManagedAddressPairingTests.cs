using System.IO;
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

        [Test]
        public void Native_more_than_managed_sends_nothing()
        {
            var a = ManagedAddressPairing.Pair(Lines("A ()"), new ulong[] { 0x1000, 0x2000 });
            Assert.That(a, Is.EqualTo(new string[1]));
        }

        [Test]
        public void Rethrow_or_async_segment_separator_sends_nothing_even_when_counts_match()
        {
            var lines = new[]
            {
                Line("A.Inner ()"),
                "--- End of stack trace from previous location where exception was thrown ---",
                Line("B.Outer ()"),
            };
            var a = ManagedAddressPairing.Pair(lines, new ulong[] { 0x1000, 0x2000 });
            Assert.That(a, Is.EqualTo(new string[2]));
        }

        [Test]
        public void Inner_exception_banner_counts_as_a_separator()
        {
            var lines = new[] { Line("A ()"), "--->", Line("B ()") };
            Assert.That(ManagedAddressPairing.Pair(lines, new ulong[] { 0x1000, 0x2000 }), Is.EqualTo(new string[2]));
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
        public void Source_location_detection_ignores_assembly_hash_and_line_zero()
        {
            Assert.That(ManagedAddressPairing.HasSourceLocation(Line("A ()")), Is.False);
            Assert.That(ManagedAddressPairing.HasSourceLocation("at A () [0x00000] in /p/A.cs:0"), Is.False);
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
        public void Payload_pairs_only_through_the_policy_and_the_contract_is_documented()
        {
            var root = FindRoot();
            var payload = File.ReadAllText(Path.Combine(root, "Runtime", "Internal", "ManagedExceptionPayload.cs"));
            Assert.That(payload, Does.Contain("ManagedAddressPairing.Pair(stackLines, ips)"));
            Assert.That(payload, Does.Not.Contain("Math.Min(frames.Length, nativeFrames.Length)"));
            Assert.That(payload, Does.Not.Contain("EnrichAddressesFromDiagnostics"));
            Assert.That(payload, Does.Contain("if (fromStackTrace)"));
            Assert.That(payload, Does.Contain("StripMonoMethodStarts"));
        }

        static string FindRoot()
        {
            var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "package.json"))) dir = dir.Parent;
            return dir.FullName;
        }
    }
}
