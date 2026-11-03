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
    /// Pins the frame-location parsing that used to be three Regex objects (built on the first managed report, where a GC
    /// fault inside the Regex static constructor was seen on Unity 2021.3 Mono). The Legacy* members are the original
    /// patterns and the original surrounding code, kept here as the reference; the public API of ManagedAddressPairing and
    /// ManagedSignatureRecipe must agree with them on real frames and on a generated corpus.
    /// </summary>
    public class ManagedFrameLocationTests
    {
        static readonly Regex LegacySharedLibrary = new Regex(@"^lib[^\s/()]*\.so(?![A-Za-z0-9_])");
        static readonly Regex LegacyAtLocation = new Regex(@"\(at\s+(?<file>.+):(?<line>\d+)\)\s*$");
        static readonly Regex LegacyInLocation = new Regex(@"\sin\s+(?<file>.+):(?<line>\d+)\s*$");

        static bool LegacyHasSourceLocation(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            var m = LegacyInLocation.Match(line);
            if (!m.Success) return false;
            int number;
            return int.TryParse(m.Groups["line"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number > 0;
        }

        static bool LegacyTryFileLine(string frame, out string file, out int line)
        {
            file = null;
            line = 0;
            if (string.IsNullOrEmpty(frame)) return false;
            var m = LegacyAtLocation.Match(frame);
            if (!m.Success) m = LegacyInLocation.Match(frame);
            if (!m.Success) return false;
            if (!int.TryParse(m.Groups["line"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out line) || line <= 0) return false;
            var path = m.Groups["file"].Value.Trim();
            var cut = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
            var name = cut >= 0 ? path.Substring(cut + 1) : path;
            if (name.Length == 0 || name[0] == '<') return false;
            file = name;
            return true;
        }

        static void AssertSame(string text)
        {
            Assert.That(ManagedAddressPairing.HasSourceLocation(text), Is.EqualTo(LegacyHasSourceLocation(text)), "HasSourceLocation: " + Show(text));

            string f1, f2;
            int l1, l2;
            var r1 = ManagedSignatureRecipe.TryFileLine(text, out f1, out l1);
            var r2 = LegacyTryFileLine(text, out f2, out l2);
            Assert.That(r1, Is.EqualTo(r2), "TryFileLine result: " + Show(text));
            Assert.That(f1, Is.EqualTo(f2), "TryFileLine file: " + Show(text));
            Assert.That(l1, Is.EqualTo(l2), "TryFileLine line: " + Show(text));

            // Raw captures, including values the int parse rejects.
            var a = LegacyAtLocation.Match(text ?? "");
            string file, line;
            Assert.That(ManagedFrameLocation.TryAtLocation(text, out file, out line), Is.EqualTo(a.Success), "At: " + Show(text));
            if (a.Success)
            {
                Assert.That(file, Is.EqualTo(a.Groups["file"].Value), "At file: " + Show(text));
                Assert.That(line, Is.EqualTo(a.Groups["line"].Value), "At line: " + Show(text));
            }
            var n = LegacyInLocation.Match(text ?? "");
            Assert.That(ManagedFrameLocation.TryInLocation(text, out file, out line), Is.EqualTo(n.Success), "In: " + Show(text));
            if (n.Success)
            {
                Assert.That(file, Is.EqualTo(n.Groups["file"].Value), "In file: " + Show(text));
                Assert.That(line, Is.EqualTo(n.Groups["line"].Value), "In line: " + Show(text));
            }

            Assert.That(ManagedFrameLocation.IsSharedLibrary(text), Is.EqualTo(LegacySharedLibrary.IsMatch(text ?? "")), "SharedLibrary: " + Show(text));
        }

        static string Show(string s)
        {
            if (s == null) return "null";
            var sb = new System.Text.StringBuilder("\"");
            foreach (var c in s) sb.Append(c < ' ' || c > '~' ? "\\u" + ((int)c).ToString("x4") : c.ToString());
            return sb.Append('"').ToString();
        }

        static readonly string[] Handwritten =
        {
            null, "", " ", "in", " in ", " in x:1", " in x:0", " in :1", " in  :1", " in   :1", " in a:1:2", " in a:b:2", " in a:12:", " in a:١٢",
            "at A.B () [0x00019] in /p/File.cs:123", "at A.B () [0x00019] in /p/File.cs:123  ", "at A.B () [0x00019] in /p/File.cs:123\n", "at A.B () [0x00019] in /p/File.cs:123\r\n",
            "at A.B () [0x00019] in <35bfffb23b81439da9f3a1f0fc9a9a07>:0", "at A.B () [0x00019] in <35bfffb23b81439da9f3a1f0fc9a9a07>:0 ",
            "at A.B () [0x00019] in <35bfffb23b81439da9f3a1f0fc9a9a07>:7", "at A.B () in C:\\dir\\File.cs:9", "at A.B () in C:\\dir\\File.cs:9999999999",
            "at A.B () in /a in b/File.cs:5", "at A.B () in /a\nb/File.cs:5", "at A.B () in a.cs:5\nin b.cs:6", "at A.B () in a.cs:5 in b.cs:6", "x\tin\tfile.cs:3", "x\nin\nfile.cs:3",
            "A.B (at /p/File.cs:12)", "A.B (at /p/File.cs:12) ", "A.B (at /p/File.cs:0)", "A.B (at   /p/File.cs:12)", "A.B (at /p/File.cs:12)x", "A.B (at /p/File.cs:)", "A.B (at :12)", "A.B (at  :12)",
            "A.B (at x:1) (at y:2)", "A.B (atx:1)", "A.B (at x:1", "A.B (at x:12))", "A.B (at a\nb:1)", "A.B at x:1)",
            "0x00007f in UnityFramework", "#00 pc 000000000012abcd /lib/libil2cpp.so",
            "libunity.so", "libunity.so(+0x1a2b)", "libil2cpp.so ", "libmono-2.0.so", "libc.so.6", "libfoo.sox", "libfoo.so_x", "libfoo.so1", "lib.so", "lib", "libso", "libx/y.so", "libx(y.so", "lib x.so", "libx.so.so", "libx.sox.so", "libx.so-", "LIBX.so", "libé.so", "libx.so\u00a0",
            "at A.B () in a\u00a0b.cs:5\u2003", "at A.B () in a\u0085b.cs:5\u0085", "at A.B () in x.cs:5\u001f", "at A.B () in x.cs:5\u200b", "at A.B () in x:\u0664", "at A.B () in x:5\u0664",
        };

        [Test]
        public void ParserAssumptionsHoldForEveryChar()
        {
            // The parsers use char.IsWhiteSpace for \s and char.IsDigit for \d.
            var space = new Regex(@"^\s$");
            var digit = new Regex(@"^\d$");
            for (var i = 0; i <= 0xFFFF; i++)
            {
                var c = (char)i;
                var s = c.ToString();
                Assert.That(char.IsWhiteSpace(c), Is.EqualTo(space.IsMatch(s)), "\\s for U+" + i.ToString("X4"));
                Assert.That(char.IsDigit(c), Is.EqualTo(digit.IsMatch(s)), "\\d for U+" + i.ToString("X4"));
            }
        }

        [Test]
        public void HandwrittenInputsAgreeWithTheRegexes()
        {
            foreach (var s in Handwritten) AssertSame(s);
        }

        [Test]
        public void KnownFrames()
        {
            string file;
            int line;
            Assert.That(ManagedSignatureRecipe.TryFileLine("at A.B () [0x00019] in /p/q/File.cs:123", out file, out line), Is.True);
            Assert.That(file, Is.EqualTo("File.cs"));
            Assert.That(line, Is.EqualTo(123));
            Assert.That(ManagedSignatureRecipe.TryFileLine("A.B (at /p/q/File.cs:12)", out file, out line), Is.True);
            Assert.That(file, Is.EqualTo("File.cs"));
            Assert.That(ManagedSignatureRecipe.TryFileLine("at A.B () in <35bf>:0", out file, out line), Is.False);
            Assert.That(ManagedAddressPairing.HasSourceLocation("at A.B () in /p/File.cs:7"), Is.True);
            Assert.That(ManagedAddressPairing.HasSourceLocation("at A.B () in <hash>:0"), Is.False);
            Assert.That(ManagedSignatureRecipe.IsEngineFrame("libunity.so(+0x1a2b)"), Is.True);
            Assert.That(ManagedSignatureRecipe.IsEngineFrame("libfoo.sox.Bar"), Is.False);
        }

        [Test]
        public void RealFramesAgreeWithTheRegexes()
        {
            var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "frame-lines-corpus.txt");
            var lines = File.ReadAllLines(path);
            Assert.That(lines.Length, Is.GreaterThan(300));
            var withLocation = 0;
            foreach (var raw in lines)
            {
                AssertSame(raw);
                var text = raw;
                var marker = text.IndexOf("]   ", StringComparison.Ordinal);
                var stripped = marker >= 0 ? text.Substring(marker + 4) : text;
                AssertSame(stripped);
                AssertSame(stripped.Trim());
                string file;
                int l;
                if (ManagedSignatureRecipe.TryFileLine(stripped, out file, out l)) withLocation++;
                // The first token of every frame, as IsEngineFrame sees it.
                AssertSame(ManagedSignatureRecipe.MethodToken(stripped));
            }
            Assert.That(withLocation, Is.GreaterThan(20), "the corpus should hold frames with a file and a line");
            Assert.That(lines.Count(l => ManagedFrameLocation.TryAtLocation(l, out _, out _)), Is.GreaterThan(10), "Unity-log frames: Type:Method (args) (at file:line)");
            Assert.That(lines.Count(l => ManagedFrameLocation.IsSharedLibrary(l)), Is.GreaterThan(10), "native frames: libX.so lines");
        }

        [Test]
        public void GeneratedCorpusAgreesWithTheRegexes()
        {
            // Small alphabet that exercises every branch: the keywords, separators, whitespace kinds, digits of two scripts, newlines.
            var pieces = new[] { " ", "  ", "\t", "\n", "\r", "\u00a0", "\u2003", "\u0085", "\u2028", "\u2029", "\v", "\f", "\u3000", "\ufeff", "\u0966", "\uff11", "in", "in ", " in ", "(at", "(at ", "at ", ")", "(", ":", "::", "/", "\\", ".so", ".", "lib", "libx", "s", "o", "x", "ab", "_", "0", "7", "123", "\u0664", "<", ">" };
            var rng = new Random(20260511);
            for (var n = 0; n < 200000; n++)
            {
                var len = rng.Next(1, 9);
                var sb = new System.Text.StringBuilder();
                for (var i = 0; i < len; i++) sb.Append(pieces[rng.Next(pieces.Length)]);
                AssertSame(sb.ToString());
            }
            // Frame-shaped: prefix, in/at form, path (maybe with " in " or newline), line, trailer.
            var paths = new[] { "/p/F.cs", "C:\\a\\F.cs", "<abc>", "a in b/F.cs", "F.cs", "", " ", "x:y", "a\nb", "/p/F.cs:12" };
            var lines = new[] { "0", "5", "12", "\u0664", "", "x" };
            var trailers = new[] { "", " ", "\n", "\r\n", "  \t", "\u00a0", "x", ")", ") ", ")\n" };
            foreach (var p in paths)
            foreach (var l in lines)
            foreach (var t in trailers)
            {
                AssertSame("at A.B () [0x1] in " + p + ":" + l + t);
                AssertSame("in " + p + ":" + l + t);
                AssertSame(" in  " + p + ":" + l + t);
                AssertSame("A.B (at " + p + ":" + l + ")" + t);
                AssertSame("(at" + p + ":" + l + ")" + t);
            }
        }
    }
}
