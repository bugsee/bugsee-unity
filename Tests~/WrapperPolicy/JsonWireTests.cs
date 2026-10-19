using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class JsonWireTests
    {
        enum Color : int { Red = 7, Green = 9 }
        enum Big : ulong { Max = ulong.MaxValue }

        // NSJSONSerialization rejects anything a real parser rejects, so every output is parsed by one.
        static JsonElement Parse(string json)
        {
            Assert.That(json, Is.Not.Null);
            try { return JsonDocument.Parse(json).RootElement.Clone(); }
            catch (JsonException ex) { Assert.Fail("not valid JSON: " + json + " (" + ex.Message + ")"); throw; }
        }

        [Test]
        public void An_empty_or_null_map_has_no_object()
        {
            Assert.That(JsonWire.Object(null), Is.Null);
            Assert.That(JsonWire.Object(new Dictionary<string, object>()), Is.Null);
        }

        [Test]
        public void Control_characters_are_escaped()
        {
            var json = JsonWire.Object(new Dictionary<string, object> { { "k", "tab\there\nnew\u0001\u001f\"q\"\\" } });
            Assert.That(Parse(json).GetProperty("k").GetString(), Is.EqualTo("tab\there\nnew\u0001\u001f\"q\"\\"));
        }

        [Test]
        public void A_valid_surrogate_pair_is_passed_through_and_an_unpaired_one_is_escaped()
        {
            Assert.That(Parse(JsonWire.Value("\U0001F600")).GetString(), Is.EqualTo("\U0001F600"));
            var lone = JsonWire.Value("a\uD800b");
            Assert.That(lone, Does.Contain("\\ud800"));
            Parse(lone);
            Parse(JsonWire.Value("a\uDC00"));
        }

        [Test]
        public void Non_finite_numbers_become_null()
        {
            foreach (var v in new object[] { float.NaN, float.PositiveInfinity, double.NaN, double.NegativeInfinity })
                Assert.That(JsonWire.Value(v), Is.EqualTo("null"));
        }

        [Test]
        public void Numbers_use_the_invariant_culture()
        {
            var old = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                Assert.That(JsonWire.Value(1.5), Is.EqualTo("1.5"));
                Assert.That(JsonWire.Value(1.5f), Is.EqualTo("1.5"));
                Assert.That(JsonWire.Value(2.25m), Is.EqualTo("2.25"));
                Parse(JsonWire.Object(new Dictionary<string, object> { { "d", 0.1 + 0.2 } }));
            }
            finally { CultureInfo.CurrentCulture = old; }
        }

        [Test]
        public void An_enum_crosses_by_value_not_name_or_ordinal()
        {
            Assert.That(JsonWire.Value(Color.Red), Is.EqualTo("7"));
            Assert.That(JsonWire.Value(Color.Green), Is.EqualTo("9"));
            Assert.That(JsonWire.Value(Big.Max), Is.EqualTo("18446744073709551615"));
        }

        [Test]
        public void Collections_and_dictionaries_nest()
        {
            var json = JsonWire.Object(new Dictionary<string, object>
            {
                { "list", new List<object> { 1, "two", null, 3.5 } },
                { "array", new[] { 1, 2 } },
                { "map", new Dictionary<string, object> { { "a", 1 }, { "gone", null } } },
                { "strings", new Dictionary<string, string> { { "x", "y" } } },
                { "nonGeneric", new System.Collections.Hashtable { { "k", "v" } } },
            });
            var root = Parse(json);
            Assert.That(root.GetProperty("list").GetArrayLength(), Is.EqualTo(4));
            Assert.That(root.GetProperty("list")[2].ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(root.GetProperty("map").TryGetProperty("gone", out _), Is.False, "a null value is omitted from a map");
            Assert.That(root.GetProperty("strings").GetProperty("x").GetString(), Is.EqualTo("y"));
            Assert.That(root.GetProperty("nonGeneric").GetProperty("k").GetString(), Is.EqualTo("v"));
        }

        [Test]
        public void A_null_value_is_omitted_from_the_object_and_an_empty_nested_map_is_an_empty_object()
        {
            var json = JsonWire.Object(new Dictionary<string, object>
            {
                { "keep", 1 }, { "drop", null }, { "empty", new Dictionary<string, object>() },
            });
            var root = Parse(json);
            Assert.That(root.TryGetProperty("drop", out _), Is.False);
            Assert.That(root.GetProperty("empty").ValueKind, Is.EqualTo(JsonValueKind.Object));
        }

        [Test]
        public void Other_types_become_text_instead_of_invalid_bare_tokens()
        {
            var guid = Guid.NewGuid();
            var json = JsonWire.Object(new Dictionary<string, object>
            {
                { "char", 'x' }, { "guid", guid }, { "span", TimeSpan.FromSeconds(90) }, { "uri", new Uri("https://a.b/c?d=e") },
            });
            var root = Parse(json);
            Assert.That(root.GetProperty("char").GetString(), Is.EqualTo("x"));
            Assert.That(root.GetProperty("guid").GetString(), Is.EqualTo(guid.ToString()));
        }

        [Test]
        public void Dates_are_iso_8601_and_bytes_are_base64()
        {
            var when = new DateTime(2026, 10, 14, 1, 2, 3, DateTimeKind.Utc);
            Assert.That(Parse(JsonWire.Value(when)).GetString(), Is.EqualTo("2026-10-14T01:02:03.0000000Z"));
            Assert.That(Parse(JsonWire.Value(new byte[] { 1, 255 })).GetString(), Is.EqualTo("Af8="));
        }

        [Test]
        public void Dictionary_keys_are_culture_invariant()
        {
            var old = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                var json = JsonWire.Value(new Dictionary<double, int> { { 1.5, 1 } });
                Assert.That(Parse(json).GetProperty("1.5").GetInt32(), Is.EqualTo(1));
            }
            finally { CultureInfo.CurrentCulture = old; }
        }

        [Test]
        public void A_collection_that_contains_itself_stops_at_the_depth_limit()
        {
            var loop = new List<object>();
            loop.Add(loop);
            var json = JsonWire.Value(loop);
            Parse(json);

            var map = new Dictionary<string, object>();
            map["self"] = map;
            Parse(JsonWire.Object(map));
        }

        [Test]
        public void String_arrays_skip_nulls_and_are_null_when_empty()
        {
            Assert.That(JsonWire.StringArray(null), Is.Null);
            Assert.That(JsonWire.StringArray(new List<string>()), Is.Null);
            Assert.That(JsonWire.StringArray(new List<string> { null }), Is.Null);
            Assert.That(Parse(JsonWire.StringArray(new List<string> { "a", null, "b\"" })).GetArrayLength(), Is.EqualTo(2));
        }

        [Test]
        public void A_nested_wrapper_info_shaped_map_round_trips()
        {
            var json = JsonWire.Object(new Dictionary<string, object>
            {
                { "wrapper_info", new Dictionary<string, object>
                    { { "type", "UNITY" }, { "version", "0.1.0" }, { "build", "dev" }, { "metal", true }, { "fps", 30 } } },
            });
            var info = Parse(json).GetProperty("wrapper_info");
            Assert.That(info.GetProperty("metal").GetBoolean(), Is.True);
            Assert.That(info.GetProperty("fps").GetInt32(), Is.EqualTo(30));
        }

        // A class that is only an IDictionary<string, object> (no non-generic IDictionary) still crosses.
        sealed class TypedOnlyMap : IDictionary<string, object>
        {
            readonly Dictionary<string, object> _inner = new Dictionary<string, object>();
            public object this[string key] { get => _inner[key]; set => _inner[key] = value; }
            public ICollection<string> Keys => _inner.Keys;
            public ICollection<object> Values => _inner.Values;
            public int Count => _inner.Count;
            public bool IsReadOnly => false;
            public void Add(string key, object value) => _inner.Add(key, value);
            public void Add(KeyValuePair<string, object> item) => _inner.Add(item.Key, item.Value);
            public void Clear() => _inner.Clear();
            public bool Contains(KeyValuePair<string, object> item) => _inner.ContainsKey(item.Key);
            public bool ContainsKey(string key) => _inner.ContainsKey(key);
            public void CopyTo(KeyValuePair<string, object>[] array, int arrayIndex) =>
                ((ICollection<KeyValuePair<string, object>>)_inner).CopyTo(array, arrayIndex);
            public IEnumerator<KeyValuePair<string, object>> GetEnumerator() => _inner.GetEnumerator();
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _inner.GetEnumerator();
            public bool Remove(string key) => _inner.Remove(key);
            public bool Remove(KeyValuePair<string, object> item) => _inner.Remove(item.Key);
            public bool TryGetValue(string key, out object value) => _inner.TryGetValue(key, out value);
        }

        [Test]
        public void A_typed_only_dictionary_crosses_with_its_entries()
        {
            var map = new TypedOnlyMap { { "a", 1 }, { "b", "x" } };
            var root = Parse(JsonWire.Object(map));
            Assert.That(root.GetProperty("a").GetInt32(), Is.EqualTo(1));
            Assert.That(root.GetProperty("b").GetString(), Is.EqualTo("x"));
        }

        // IL2CPP managed stripping can remove KeyValuePair<,>.get_Key/get_Value when only reflection reaches
        // them, which silently turned launch options and event parameters into {} (issue #133).
        [Test]
        public void JsonWire_does_not_reflect_on_key_value_pairs()
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (!System.IO.File.Exists(System.IO.Path.Combine(dir, "package.json")))
                dir = System.IO.Directory.GetParent(dir).FullName;
            var source = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "Runtime", "WrapperPolicy", "JsonWire.cs"));
            Assert.That(source, Does.Not.Contain("GetProperty("));
            Assert.That(source, Does.Not.Contain(".GetValue("));
        }
    }
}
