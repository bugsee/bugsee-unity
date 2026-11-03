using System;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Hand-written parsers for the source-location suffix of a managed stack frame and for a shared-library name.
    /// They replace four <c>Regex</c> objects (three distinct patterns, one used twice) that were built on the FIRST managed
    /// report. On Unity 2021.3 Mono (ARMv7) the stack of a SIGSEGV in <c>GC_mark_from</c> showed that Regex construction
    /// (<c>RegexCharClass</c> static constructor) running when the fault happened. Behaviour is identical to the patterns they replace, which
    /// the host tests keep as the reference and compare over a generated corpus:
    /// <list type="bullet">
    /// <item><c>\sin\s+(?&lt;file&gt;.+):(?&lt;line&gt;\d+)\s*$</c> (<see cref="TryInLocation"/>)</item>
    /// <item><c>\(at\s+(?&lt;file&gt;.+):(?&lt;line&gt;\d+)\)\s*$</c> (<see cref="TryAtLocation"/>)</item>
    /// <item><c>^lib[^\s/()]*\.so(?![A-Za-z0-9_])</c> (<see cref="IsSharedLibrary"/>)</item>
    /// </list>
    /// Nothing here may allocate more than the two result substrings or touch System.Text.RegularExpressions.
    /// </summary>
    static class ManagedFrameLocation
    {
        /// <summary>"... in FILE:LINE" at the end of the text (trailing whitespace allowed); file and the digits of the line.</summary>
        public static bool TryInLocation(string text, out string file, out string line)
        {
            return TryLocation(text, false, out file, out line);
        }

        /// <summary>"... (at FILE:LINE)" at the end of the text (trailing whitespace allowed); file and the digits of the line.</summary>
        public static bool TryAtLocation(string text, out string file, out string line)
        {
            return TryLocation(text, true, out file, out line);
        }

        // Regex \s and \d: whitespace as char.IsWhiteSpace, decimal digits (any script) as char.IsDigit.
        static bool Space(char c) { return char.IsWhiteSpace(c); }

        static bool TryLocation(string text, bool parenthesized, out string file, out string line)
        {
            file = null;
            line = null;
            if (string.IsNullOrEmpty(text)) return false;

            // Tail: ":" digits [")"] whitespace* end. The file is greedy, so the colon is the one before the final digit run.
            var end = text.Length;
            while (end > 0 && Space(text[end - 1])) end--;
            if (parenthesized)
            {
                if (end == 0 || text[end - 1] != ')') return false;
                end--;
            }
            var digits = end;
            while (digits > 0 && char.IsDigit(text[digits - 1])) digits--;
            if (digits == end || digits == 0 || text[digits - 1] != ':') return false;
            var colon = digits - 1;
            if (colon == 0) return false; // no room for the opener and a file

            // Head, leftmost first: ws "in" ws+ (or "(at" ws+) before at least one file char; the file has no line feed.
            var lastLineFeed = text.LastIndexOf('\n', colon - 1); 
            var opener = parenthesized ? "(at" : "in";
            var skip = parenthesized ? 0 : 1; // the leading whitespace of the "in" form
            for (var i = 0; ; i++)
            {
                var wordStart = i + skip;
                var after = wordStart + opener.Length;
                if (after + 1 >= colon) return false; // needs whitespace and at least one file character
                if (parenthesized ? text[i] != '(' : !Space(text[i])) continue;
                if (string.CompareOrdinal(text, wordStart, opener, 0, opener.Length) != 0 || !Space(text[after])) continue;
                var wsEnd = after;
                while (wsEnd < colon && Space(text[wsEnd])) wsEnd++;
                var start = Math.Min(wsEnd, colon - 1); // \s+ is greedy but must leave one character for the file
                if (start <= lastLineFeed) continue;
                file = text.Substring(start, colon - start);
                line = text.Substring(digits, end - digits);
                return true;
            }
        }

        /// <summary>"libXXX.so" at the start: no whitespace, slash or parenthesis before ".so", and no letter, digit or underscore after it.</summary>
        public static bool IsSharedLibrary(string token)
        {
            if (token == null || !token.StartsWith("lib", StringComparison.Ordinal)) return false;
            for (var p = 3; p < token.Length; p++)
            {
                var c = token[p];
                if (Space(c) || c == '/' || c == '(' || c == ')') return false;
                if (c == '.' && p + 3 <= token.Length && token[p + 1] == 's' && token[p + 2] == 'o')
                {
                    if (p + 3 == token.Length) return true;
                    var n = token[p + 3];
                    if (!((n >= 'A' && n <= 'Z') || (n >= 'a' && n <= 'z') || (n >= '0' && n <= '9') || n == '_')) return true;
                }
            }
            return false;
        }
    }
}
