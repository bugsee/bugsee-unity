using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class AndroidNativeValueTests
    {
        enum Level : short { Low = 3 }
        enum Huge : ulong { Max = ulong.MaxValue }
        [Flags] enum Bits : uint { A = 1, B = 4 }
        enum Wide : long { Neg = -5 }

        static bool IsValidJson(string text)
        {
            try { System.Text.Json.JsonDocument.Parse(text).Dispose(); return true; } catch { return false; }
        }

        [Test]
        public void A_list_and_a_map_cross_as_json_not_a_type_name()
        {
            Assert.That(AttributeWire.ToNative(new List<object> { 1, "a", 2.5 }), Is.EqualTo("[1,\"a\",2.5]"));
            Assert.That(AttributeWire.ToNative(new Dictionary<string, object> { { "k", new[] { 1, 2 } } }), Is.EqualTo("{\"k\":[1,2]}"));
            Assert.That(AttributeWire.ToNative(new[] { "x" }), Is.EqualTo("[\"x\"]"));
        }

        [Test]
        public void A_large_container_is_valid_json_within_the_cap_with_whole_entries_dropped()
        {
            var big = new List<object>();
            for (int i = 0; i < 5000; i++) big.Add("ab\\\"\u00e9cdefgh");
            var json = (string)AttributeWire.ToNative(big);
            Assert.That(json.Length, Is.LessThanOrEqualTo(AttributeWire.MaxContainerJsonLength));
            Assert.That(IsValidJson(json), Is.True);
            Assert.That(json, Does.StartWith("[\"ab").And.EndWith("\"]"));
        }

        [Test]
        public void A_cut_never_splits_a_surrogate_pair_or_an_escape()
        {
            var big = new List<object>();
            for (int i = 0; i < 3000; i++) big.Add("\U0001F600\n\\");
            for (int cap = 20; cap < 60; cap++)
            {
                var json = JsonWire.Bounded(big, cap, out var truncated);
                Assert.That(truncated, Is.True);
                Assert.That(json.Length, Is.LessThanOrEqualTo(cap));
                Assert.That(IsValidJson(json), Is.True, json);
            }
        }

        [Test]
        public void Nodes_are_bounded_so_a_cycle_or_a_huge_tree_is_cheap()
        {
            var self = new List<object>();
            self.Add(self);
            self.Add(self);
            var json = (string)AttributeWire.ToNative(self);
            Assert.That(json, Is.EqualTo("[null,null]"));

            var wide = new List<object>();
            for (int i = 0; i < 100000; i++) wide.Add(1);
            var started = DateTime.UtcNow;
            json = JsonWire.Bounded(wide, 1000000, out var truncated);
            Assert.That(truncated, Is.True);
            Assert.That(IsValidJson(json), Is.True);
            Assert.That(DateTime.UtcNow - started, Is.LessThan(TimeSpan.FromSeconds(2)));

            var map = new Dictionary<string, object>();
            map["me"] = map;
            Assert.That(AttributeWire.ToNative(map), Is.EqualTo("{\"me\":null}"));
        }

        [Test]
        public void Non_finite_numbers_in_containers_are_null_and_null_map_values_are_dropped()
        {
            var list = new List<object> { double.NaN, float.PositiveInfinity, 1.5 };
            Assert.That(AttributeWire.ToNative(list), Is.EqualTo("[null,null,1.5]"));
            var map = new Dictionary<string, object> { { "a", null }, { "b", double.NegativeInfinity }, { "c", 2 } };
            Assert.That(AttributeWire.ToNative(map), Is.EqualTo("{\"b\":null,\"c\":2}"));
        }

        [Test]
        public void A_byte_array_is_bare_base64_and_other_types_are_text()
        {
            Assert.That(AttributeWire.ToNative(new byte[] { 1, 2, 3 }), Is.EqualTo("AQID"));
            var g = Guid.NewGuid();
            Assert.That(AttributeWire.ToNative(g), Is.EqualTo(g.ToString()));
            var o = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.FromHours(2));
            Assert.That(AttributeWire.ToNative(o), Is.EqualTo("2020-01-02T03:04:05.0000000+02:00"));
            Assert.That(AttributeWire.ToNative(new List<object> { 'c', g, new byte[] { 1 } }), Is.EqualTo("[\"c\",\"" + g + "\",\"AQ==\"]"));
        }

        [Test]
        public void A_float_is_sent_as_the_double_that_prints_the_same()
        {
            Assert.That(AttributeWire.ToNative(1.1f), Is.EqualTo(1.1d).And.TypeOf<double>());
        }

        [Test]
        public void Enums_cross_by_exact_integer_value()
        {
            Assert.That(AttributeWire.ToNative(Huge.Max), Is.EqualTo("18446744073709551615"));
            Assert.That(AttributeWire.ToNative(Bits.A | Bits.B), Is.EqualTo(5L).And.TypeOf<long>());
            Assert.That(AttributeWire.ToNative(Wide.Neg), Is.EqualTo(-5L).And.TypeOf<long>());
            Assert.That(AttributeWire.ToNative(new List<object> { Huge.Max, Bits.B }), Is.EqualTo("[18446744073709551615,4]"));
        }

        sealed class TypedOnly : IDictionary<string, object>
        {
            readonly Dictionary<string, object> _d = new Dictionary<string, object> { { "k", 1 } };
            public object this[string key] { get => _d[key]; set => _d[key] = value; }
            public ICollection<string> Keys => _d.Keys;
            public ICollection<object> Values => _d.Values;
            public int Count => _d.Count;
            public bool IsReadOnly => false;
            public void Add(string key, object value) => _d.Add(key, value);
            public void Add(KeyValuePair<string, object> item) => _d.Add(item.Key, item.Value);
            public void Clear() => _d.Clear();
            public bool Contains(KeyValuePair<string, object> item) => _d.ContainsKey(item.Key);
            public bool ContainsKey(string key) => _d.ContainsKey(key);
            public void CopyTo(KeyValuePair<string, object>[] array, int arrayIndex) { }
            public IEnumerator<KeyValuePair<string, object>> GetEnumerator() => _d.GetEnumerator();
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _d.GetEnumerator();
            public bool Remove(string key) => _d.Remove(key);
            public bool Remove(KeyValuePair<string, object> item) => _d.Remove(item.Key);
            public bool TryGetValue(string key, out object value) => _d.TryGetValue(key, out value);
        }

        [Test]
        public void Sets_and_generic_only_dictionaries_are_containers()
        {
            Assert.That(AttributeWire.ToNative(new HashSet<object> { 1 }), Is.EqualTo("[1]"));
            Assert.That(AttributeWire.ToNative(new TypedOnly()), Is.EqualTo("{\"k\":1}"));
            Assert.That(AttributeWire.ToNative(new List<object> { new TypedOnly() }), Is.EqualTo("[{\"k\":1}]"));
        }

        [Test]
        public void The_cap_is_exact_for_nested_values_and_a_cut_ends_every_level()
        {
            var data = new List<object>();
            for (int i = 0; i < 40; i++) data.Add(new List<object> { "xxxxxxxx", 1 });
            for (int cap = 3; cap < 200; cap++)
            {
                var json = JsonWire.Bounded(data, cap, out _);
                Assert.That(IsValidJson(json), Is.True, json);
                Assert.That(json.Length, Is.LessThanOrEqualTo(Math.Max(cap, 2)), json);
            }
            // The cut one inner list holds ends the outer list: later short siblings are not kept.
            var mixed = new List<object> { new List<object> { "aaaaaaaaaaaa", "b" }, 7 };
            var cut = JsonWire.Bounded(mixed, 12, out var truncated);
            Assert.That(truncated, Is.True);
            Assert.That(cut, Does.Not.Contain("7"));
        }

        private sealed class Walker : System.Collections.IEnumerable
        {
            public bool Walked;
            public System.Collections.IEnumerator GetEnumerator() { Walked = true; yield return 1; }
            public override string ToString() => "walker";
        }

        [Test]
        public void An_enumerable_that_is_not_a_collection_is_text_not_walked()
        {
            var w = new Walker();
            Assert.That(AttributeWire.ToNative(w), Is.EqualTo("walker"));
            Assert.That(AttributeWire.ToNative(new List<object> { w }), Is.EqualTo("[\"walker\"]"));
            Assert.That(w.Walked, Is.False);
        }

        [Test]
        public void Scalars_keep_their_kind_and_the_odd_widths_are_mapped()
        {
            Assert.That(AttributeWire.ToNative(5), Is.EqualTo(5).And.TypeOf<int>());
            Assert.That(AttributeWire.ToNative(5L), Is.TypeOf<long>());
            Assert.That(AttributeWire.ToNative(1.5), Is.TypeOf<double>());
            Assert.That(AttributeWire.ToNative(true), Is.EqualTo(true));
            Assert.That(AttributeWire.ToNative("s"), Is.EqualTo("s"));
            Assert.That(AttributeWire.ToNative(null), Is.Null);
            Assert.That(AttributeWire.ToNative((ushort)7), Is.EqualTo(7).And.TypeOf<int>());
            Assert.That(AttributeWire.ToNative(7u), Is.TypeOf<long>());
            Assert.That(AttributeWire.ToNative(1.25m), Is.EqualTo(1.25).And.TypeOf<double>());
        }

        [Test]
        public void An_enum_crosses_by_value_and_other_types_as_invariant_text()
        {
            Assert.That(AttributeWire.ToNative(Level.Low), Is.EqualTo(3).And.TypeOf<short>());
            Assert.That(AttributeWire.ToNative('c'), Is.EqualTo("c"));
            Assert.That(AttributeWire.ToNative(new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc)), Is.EqualTo("2020-01-02T03:04:05.0000000Z"));
        }

        [Test]
        public void Culture_does_not_change_the_text()
        {
            var old = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Assert.That(AttributeWire.ToNative(new TimeSpan(1, 2, 3)), Is.EqualTo("01:02:03"));
                Assert.That(AttributeWire.ToNative(new List<object> { 1.5 }), Is.EqualTo("[1.5]"));
            }
            finally { Thread.CurrentThread.CurrentCulture = old; }
        }
    }
}
