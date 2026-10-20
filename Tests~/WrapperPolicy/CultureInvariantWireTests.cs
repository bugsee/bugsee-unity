using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    /// <summary>
    /// A decimal-comma or non-Gregorian device must send the same wire text as any other (#143): 1.5 not "1,5",
    /// "true" not "True". The runtime tests swap the current culture; the source scan bans the conversions that read it.
    /// </summary>
    public class CultureInvariantWireTests
    {
        static readonly string[] Cultures = { "tr-TR", "de-DE", "ru-RU", "ar-SA", "th-TH", "fa-IR" };

        static void UnderEachCulture(Action check)
        {
            var saved = CultureInfo.CurrentCulture;
            try
            {
                foreach (var name in Cultures)
                {
                    CultureInfo.CurrentCulture = new CultureInfo(name);
                    try { check(); }
                    catch (AssertionException e) { throw new AssertionException(name + ": " + e.Message, e); }
                }
            }
            finally { CultureInfo.CurrentCulture = saved; }
        }

        [Test]
        public void Invariant_text_is_the_same_under_every_culture()
        {
            var date = new DateTime(2026, 10, 21, 1, 2, 3, DateTimeKind.Utc);
            UnderEachCulture(() =>
            {
                Assert.That(InvariantText.Of(1.5), Is.EqualTo("1.5"));
                Assert.That(InvariantText.Of(1.5f), Is.EqualTo("1.5"));
                Assert.That(InvariantText.Of(1.5m), Is.EqualTo("1.5"));
                Assert.That(InvariantText.Of(-7), Is.EqualTo("-7"));
                Assert.That(InvariantText.Of(true), Is.EqualTo("true"));
                Assert.That(InvariantText.Of(false), Is.EqualTo("false"));
                Assert.That(InvariantText.Of(date), Is.EqualTo("2026-10-21T01:02:03.0000000Z"));
                Assert.That(InvariantText.Of(DayOfWeek.Friday), Is.EqualTo("Friday"));
                Assert.That(InvariantText.Of('x'), Is.EqualTo("x"));
                Assert.That(InvariantText.Of(null), Is.EqualTo(""));
            });
        }

        [Test]
        public void Json_and_the_attribute_wire_do_not_follow_the_culture()
        {
            UnderEachCulture(() =>
            {
                Assert.That(JsonWire.Value(1.5), Is.EqualTo("1.5"));
                Assert.That(JsonWire.Value(new Dictionary<string, object> { { "a", 2.25f }, { "b", true }, { "c", 3.5m } }),
                    Is.EqualTo("{\"a\":2.25,\"b\":true,\"c\":3.5}"));
                Assert.That(AttributeWire.ToJson(2.0), Is.EqualTo("2.0"));
                Assert.That(AttributeWire.ToNative(1.5m), Is.EqualTo(1.5).And.TypeOf<double>());
                Assert.That(AttributeWire.ToNative(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)), Is.EqualTo("2026-01-02T00:00:00.0000000Z"));
                Assert.That(AttributeWire.Decode("d:1.5"), Is.EqualTo(1.5));
                Assert.That(AttributeWire.Decode("i:-12"), Is.EqualTo(-12L));
            });
        }

        // ---- source scan -------------------------------------------------------------------------------------------

        static string RepoRoot()
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, "package.json"))) return dir;
                dir = Directory.GetParent(dir)?.FullName;
            }
            throw new InvalidOperationException("package root not found");
        }

        // One entry per statement (lines joined up to the next ; { or }), so a call split over lines is judged whole.
        static IEnumerable<(string File, int Line, string Text)> Lines()
        {
            var root = RepoRoot();
            foreach (var folder in new[] { "Runtime", "Editor" })
                foreach (var file in Directory.GetFiles(Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories))
                {
                    var lines = File.ReadAllLines(file);
                    var rel = Path.GetRelativePath(root, file);
                    var pending = new System.Text.StringBuilder();
                    int start = 0;
                    for (int i = 0; i < lines.Length; i++)
                    {
                        var t = lines[i].Trim();
                        if (t.StartsWith("//") || t.StartsWith("*")) continue;
                        if (pending.Length == 0) start = i + 1;
                        pending.Append(lines[i]).Append(' ');
                        if (t.EndsWith(";") || t.EndsWith("{") || t.EndsWith("}") || t.Length == 0)
                        {
                            yield return (rel, start, pending.ToString());
                            pending.Clear();
                        }
                    }
                    if (pending.Length > 0) yield return (rel, start, pending.ToString());
                }
        }

        const string Inv = "InvariantCulture";

        static readonly (string Why, Regex Pattern)[] Banned =
        {
            ("Convert.ToString/ToInt32/ToDouble/ToSingle/ToInt64 without a culture", new Regex(@"Convert\.To(String|Int32|Int64|Double|Single|Decimal)\((?![^;]*" + Inv + ")")),
            ("number Parse/TryParse without a culture", new Regex(@"\b(int|long|uint|ulong|short|ushort|float|double|decimal|Single|Double|Int32|Int64)\.(Try)?Parse\((?![^;]*" + Inv + ")")),
            ("string.Format without a culture", new Regex(@"\b[sS]tring\.Format\((?!\s*(CultureInfo|System\.Globalization))")),
            ("ToString with a non-hex, non-GUID format and no culture", new Regex(@"\.ToString\(""(?![xXNDo]\d*""\))[^""]*""\)")),
            ("ToString() on a value that is not known to be text", new Regex(@"\b(value|Value|raw|obj|arg|VersionCode|kv\.Value|param|parameter)\.ToString\(\)")),
        };

        [Test]
        public void No_bridge_or_build_path_converts_a_value_with_the_current_culture()
        {
            var hits = new List<string>();
            foreach (var (file, line, text) in Lines())
                foreach (var (why, pattern) in Banned)
                    if (pattern.IsMatch(text))
                        hits.Add($"{file}:{line}: {why}: {text.Trim()}");
            Assert.That(hits, Is.Empty, "Use CultureInfo.InvariantCulture or InvariantText.Of:\n" + string.Join("\n", hits));
        }

        [Test]
        public void The_scan_catches_what_it_is_meant_to_ban()
        {
            Assert.That(Banned.Any(b => b.Pattern.IsMatch("var s = kv.Value.ToString();")));
            Assert.That(Banned.Any(b => b.Pattern.IsMatch("var s = Convert.ToString(value);")));
            Assert.That(Banned.Any(b => b.Pattern.IsMatch("int.TryParse(x, out var n)")));
            Assert.That(Banned.Any(b => b.Pattern.IsMatch("d.ToString(\"F1\")")));
            Assert.That(Banned.Any(b => b.Pattern.IsMatch("string.Format(\"{0}\", d)")));
            Assert.That(Banned.Any(b => b.Pattern.IsMatch("DateTime.Now.ToString(\"yyyyMMdd\")")));
            Assert.That(Banned.Any(b => b.Pattern.IsMatch("Convert.ToString(v, CultureInfo.InvariantCulture)")), Is.False);
            Assert.That(Banned.Any(b => b.Pattern.IsMatch("b.ToString(\"x2\")")), Is.False);
            Assert.That(Banned.Any(b => b.Pattern.IsMatch("Guid.NewGuid().ToString(\"N\")")), Is.False);
        }
    }
}
