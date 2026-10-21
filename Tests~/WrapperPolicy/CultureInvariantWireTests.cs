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
    /// "true" not "True". The per-path runtime tests below, which swap the current culture, are the real guard. The source
    /// scan is a best-effort lint on top: it checks each call's own argument list for the conversions that read the culture
    /// (Convert.To*, Parse/TryParse, string.Format, ToString with a format, ToString() on a few value names, a culture-less
    /// numeric format spec in an interpolated string, a float literal concatenated into text, CurrentCulture). It cannot
    /// see types, so implicit formatting of a variable ("x" + d, StringBuilder.Append(d), $"{d}") is not caught.
    /// </summary>
    public class CultureInvariantWireTests
    {
        static readonly string[] Cultures = { "tr-TR", "de-DE", "ru-RU", "ar-SA", "th-TH", "fa-IR" };

        static void UnderEachCulture(Action check)
        {
            // Under DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 these tests would prove nothing, and a named culture may not even
            // be creatable (PredefinedCulturesOnly), so probe before swapping anything.
            try
            {
                foreach (var name in Cultures) new CultureInfo(name);
                if ((1.5).ToString(new CultureInfo("de-DE")) != "1,5") Assert.Ignore("culture data is not available (invariant globalization)");
            }
            catch (CultureNotFoundException) { Assert.Ignore("culture data is not available (invariant globalization)"); }
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
                Assert.That(InvariantText.Of(DayOfWeek.Friday), Is.EqualTo("5")); // the value, as JsonWire sends it
                Assert.That(InvariantText.Of(0.1 + 0.2), Is.EqualTo("0.30000000000000004"));
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

        [Test]
        public void The_mapping_notice_writes_gregorian_dates_under_every_culture()
        {
            var start = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
            var stale = new MappingFileInfo("/out/mapping.txt", 10, start.AddMinutes(-5));
            UnderEachCulture(() =>
            {
                var text = AndroidMappingNotice.MissingMapping(true, AndroidOutputFormat.ExportProject, "Game_mapping.txt",
                    new[] { "/out" }, new[] { stale }, start, "1.2.3", "45");
                Assert.That(text, Does.Contain("last written 2026-10-20 11:55:00Z").And.Contain("started 2026-10-20 12:00:00Z"));
            });
        }

        // ---- source scan (best effort, see the class comment) --------------------------------------------------------

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

        /// <summary>
        /// Same length as <paramref name="src"/>, with comments, string and char literal contents replaced by spaces (the quotes
        /// stay), so a bracket or ; inside text or a comment cannot confuse the scan. Also returns the interpolated strings.
        /// </summary>
        static string Blank(string src, List<(int Start, int End)> interpolated)
        {
            var o = src.ToCharArray();
            int n = src.Length;
            for (int i = 0; i < n;)
            {
                char c = src[i];
                if (c == '/' && i + 1 < n && src[i + 1] == '/')
                {
                    while (i < n && src[i] != '\n') o[i++] = ' ';
                }
                else if (c == '/' && i + 1 < n && src[i + 1] == '*')
                {
                    int end = src.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    end = end < 0 ? n : end + 2;
                    for (; i < end; i++) if (src[i] != '\n') o[i] = ' ';
                }
                else if (c == '\'')
                {
                    int j = i + 1;
                    if (j < n && src[j] == '\\') j++;
                    j++;
                    while (j < n && src[j] != '\'' && j - i < 12) j++;
                    for (int k = i + 1; k < j && k < n; k++) o[k] = ' ';
                    i = j + 1;
                }
                else if (c == '"')
                {
                    bool verbatim = i > 0 && (src[i - 1] == '@' || (i > 1 && src[i - 1] == '$' && src[i - 2] == '@') || (i > 1 && src[i - 1] == '@' && src[i - 2] == '$'));
                    bool interp = (i > 0 && src[i - 1] == '$') || (i > 1 && src[i - 2] == '$' && src[i - 1] == '@') || (i > 1 && src[i - 2] == '@' && src[i - 1] == '$');
                    int j = i + 1;
                    while (j < n)
                    {
                        if (verbatim) { if (src[j] == '"') { if (j + 1 < n && src[j + 1] == '"') { j += 2; continue; } break; } }
                        else { if (src[j] == '\\') { j += 2; continue; } if (src[j] == '"' || src[j] == '\n') break; }
                        j++;
                    }
                    if (interp) interpolated.Add((i, Math.Min(j, n - 1)));
                    for (int k = i + 1; k < j && k < n; k++) if (src[k] != '\n') o[k] = ' ';
                    i = j + 1;
                }
                else i++;
            }
            return new string(o);
        }

        /// <summary>The text inside the parentheses that open at <paramref name="open"/>, in the blanked and the original source.</summary>
        static (string Blanked, string Original) Args(string blank, string src, int open)
        {
            int depth = 0;
            for (int i = open; i < blank.Length; i++)
            {
                if (blank[i] == '(') depth++;
                else if (blank[i] == ')' && --depth == 0)
                    return (blank.Substring(open + 1, i - open - 1), src.Substring(open + 1, i - open - 1));
            }
            return (blank.Substring(open + 1), src.Substring(open + 1));
        }

        static bool HasInvariant(string args) => args.Contains("InvariantCulture");

        static readonly Regex ConvertCall = new Regex(@"\bConvert\.To(String|Int16|Int32|Int64|UInt16|UInt32|UInt64|Double|Single|Decimal|Boolean|DateTime|Byte|SByte|Char)\s*\(");
        static readonly Regex ParseCall = new Regex(@"\b(int|long|uint|ulong|short|ushort|byte|sbyte|float|double|decimal|Single|Double|Decimal|Int16|Int32|Int64|UInt16|UInt32|UInt64|Byte|SByte|DateTime|DateTimeOffset|TimeSpan)\s*\.\s*(Try)?Parse(Exact)?\s*\(");
        static readonly Regex FormatCall = new Regex(@"\b[sS]tring\.Format\s*\(");
        static readonly Regex ToStringCall = new Regex(@"\.ToString\s*\(");
        static readonly Regex BareToString = new Regex(@"\b(value|Value|raw|obj|arg|args|param|parameter|number|VersionCode|Frequency|Timestamp|timestamp)\s*\.ToString\s*\(\s*\)");
        static readonly Regex RadixOnly = new Regex(@"^\s*(2|8|10|16)\s*$");
        static readonly Regex HexFormat = new Regex(@"^\s*""[xX]\d*""\s*$");
        static readonly Regex RoundTripDateFormat = new Regex(@"^\s*""[oO]""\s*$");
        static readonly Regex GuidFormat = new Regex(@"^\s*""[NDBPX]""\s*$");
        static readonly Regex FloatLiteralConcat = new Regex(@"[+]\s*-?\d+\.\d+[fFdDmM]?\b|\b\d+\.\d+[fFdDmM]?\s*[+]\s*""");
        static readonly Regex AppendFloatLiteral = new Regex(@"\.Append\(\s*-?\d+\.\d+[fFdDmM]?\s*\)");
        // Append(x) of a number formats it with the current culture (a negative sign, a decimal comma). Without types the scan can only
        // judge the argument by its shape: a numeric cast, a DTO field, or a name that is known to hold a number.
        static readonly Regex NumericAppendArg = new Regex(@"^(\(\s*(int|long|short|byte|sbyte|uint|ulong|ushort|float|double|decimal)\s*\)|_?dto\.\w+$|[\w.]*(severity|Severity|timestamp|Timestamp|Ms|Seconds|Count|Index|Number)$)");
        static readonly Regex NumericHole = new Regex(@"\{[^{}:]+:[FfNnEeGgDdPpRrCc]\d*\}");

        /// <summary>The problems one source text has; line numbers are 1-based.</summary>
        internal static List<string> Scan(string src, string name)
        {
            var hits = new List<string>();
            var interpolated = new List<(int Start, int End)>();
            var blank = Blank(src, interpolated);
            int Line(int index) { int l = 1; for (int i = 0; i < index; i++) if (src[i] == '\n') l++; return l; }
            void Hit(int index, string why) => hits.Add(name + ":" + Line(index) + ": " + why);

            foreach (Match m in ConvertCall.Matches(blank))
            {
                var a = Args(blank, src, m.Index + m.Length - 1);
                if (!HasInvariant(a.Original) && !(a.Blanked.Contains(",") && RadixOnly.IsMatch(a.Blanked.Substring(a.Blanked.LastIndexOf(',') + 1))))
                    Hit(m.Index, "Convert." + m.Groups[1].Value + " without CultureInfo.InvariantCulture");
            }
            foreach (Match m in ParseCall.Matches(blank))
            {
                var a = Args(blank, src, m.Index + m.Length - 1);
                if (!HasInvariant(a.Original))
                    Hit(m.Index, m.Groups[1].Value + ".Parse/TryParse without CultureInfo.InvariantCulture");
            }
            foreach (Match m in FormatCall.Matches(blank))
            {
                var a = Args(blank, src, m.Index + m.Length - 1);
                if (!HasInvariant(a.Original))
                    Hit(m.Index, "string.Format without CultureInfo.InvariantCulture");
            }
            foreach (Match m in ToStringCall.Matches(blank))
            {
                var a = Args(blank, src, m.Index + m.Length - 1);
                var arg = a.Original.Trim();
                if (arg.Length == 0 || HasInvariant(a.Original)) continue;
                var first = arg.Split(',')[0];
                if (arg == first && (HexFormat.IsMatch(first) || RoundTripDateFormat.IsMatch(first))) continue;
                // "N" / "D" are culture-dependent on a number or date; only a Guid has them as plain hex layouts.
                if (arg == first && GuidFormat.IsMatch(first) && Regex.IsMatch(blank.Substring(Math.Max(0, m.Index - 40), Math.Min(40, m.Index)), @"[gG]uid[\w.()]*$"))
                    continue;
                Hit(m.Index, "ToString(" + arg + ") reads the current culture");
            }
            foreach (Match m in BareToString.Matches(blank))
                Hit(m.Index, "ToString() on a value that may be a number, bool or date: use InvariantText.Of");
            foreach (Match m in Regex.Matches(blank, @"\bCurrent(UI)?Culture\b"))
                Hit(m.Index, "uses the current culture");
            foreach (Match m in FloatLiteralConcat.Matches(blank))
                Hit(m.Index, "a floating point literal concatenated into text");
            foreach (Match m in Regex.Matches(blank, @"\.Append\s*\("))
            {
                var a = Args(blank, src, m.Index + m.Length - 1);
                var arg = a.Blanked.Trim();
                if (!arg.Contains(",") && NumericAppendArg.IsMatch(arg) && !arg.Contains("\""))
                    Hit(m.Index, "Append(" + arg + ") of a number reads the current culture: append .ToString(CultureInfo.InvariantCulture)");
            }
            foreach (Match m in AppendFloatLiteral.Matches(blank))
                Hit(m.Index, "a floating point literal appended to text");
            foreach (var (start, end) in interpolated)
            {
                var text = src.Substring(start, end - start + 1);
                if (NumericHole.IsMatch(text) && !src.Substring(Math.Max(0, start - 40), Math.Min(40, start)).Contains("Invariant"))
                    Hit(start, "interpolated string with a numeric format spec and no FormattableString.Invariant");
            }
            return hits;
        }

        [Test]
        public void No_bridge_or_build_path_converts_a_value_with_the_current_culture()
        {
            var root = RepoRoot();
            var hits = new List<string>();
            foreach (var folder in new[] { "Runtime", "Editor" })
                foreach (var file in Directory.GetFiles(Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories))
                    hits.AddRange(Scan(File.ReadAllText(file), Path.GetRelativePath(root, file)));
            Assert.That(hits, Is.Empty, "Use CultureInfo.InvariantCulture or InvariantText.Of:\n" + string.Join("\n", hits));
        }

        [TestCase("var s = kv.Value.ToString();")]
        [TestCase("var s = Convert.ToString(value);")]
        [TestCase("var s = Convert.ToString(a) + Convert.ToString(b, CultureInfo.InvariantCulture);")]
        [TestCase("var s = Convert.ToBoolean(x);")]
        [TestCase("var n = Convert.ToUInt32(x);")]
        [TestCase("int.TryParse(x, out var n);")]
        [TestCase("byte.Parse(x);")]
        [TestCase("var t = TimeSpan.Parse(x);")]
        [TestCase("var t = DateTime.Parse(x);")]
        [TestCase("d.ToString(\"F1\");")]
        [TestCase("d.ToString(\"N1\");")]
        [TestCase("n.ToString(\"D\");")]
        [TestCase("date.ToString(\"D\");")]
        [TestCase("DateTime.Now.ToString(\"yyyyMMdd\");")]
        [TestCase("string.Format(\"{0}\", d); var x = \"CultureInfo.InvariantCulture\";")]
        [TestCase("var s = \"a;\"; var t = Convert.ToString(d); // InvariantCulture")]
        [TestCase("var s = d.ToString(CultureInfo.CurrentCulture);")]
        [TestCase("var s = \"x\" + 1.5;")]
        [TestCase("sb.Append(2.5);")]
        [TestCase("sb.Append(\"\\\"severity\\\":\").Append(_dto.severity);")]
        [TestCase("sb.Append((int)level);")]
        [TestCase("sb.Append(timestampMs);")]
        [TestCase("var s = $\"{d:F1}\";")]
        public void The_scan_catches_what_it_is_meant_to_ban(string code)
        {
            Assert.That(Scan(code, "x"), Is.Not.Empty);
        }

        [TestCase("var s = Convert.ToString(v, CultureInfo.InvariantCulture);")]
        [TestCase("var b = Convert.ToByte(hex, 16);")]
        [TestCase("b.ToString(\"x2\");")]
        [TestCase("dt.ToString(\"o\", CultureInfo.InvariantCulture);")]
        [TestCase("Guid.NewGuid().ToString(\"N\");")]
        [TestCase("var s = Convert.ToString(\n  v,\n  CultureInfo.InvariantCulture);")]
        [TestCase("// Convert.ToString(v) and 1.5.ToString(\"F2\") in a comment\nvar x = 1;")]
        [TestCase("var s = \"Convert.ToString(v)\";")]
        [TestCase("var s = sb.ToString();")]
        [TestCase("sb.Append(_dto.severity.ToString(CultureInfo.InvariantCulture));")]
        [TestCase("sb.Append(name).Append(',');")]
        [TestCase("int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n);")]
        public void The_scan_allows_invariant_and_non_culture_uses(string code)
        {
            Assert.That(Scan(code, "x"), Is.Empty);
        }
    }
}
