using System;
using System.Collections.Generic;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// The IL2CPP arguments that make a build write <c>Symbols/LineNumberMappings.json</c> (C# file:line) and
    /// <c>Symbols/MethodMap.tsv</c> (mangled C++ name to C# signature) next to the generated C++. Neither file exists
    /// unless the arguments are passed, and without them the il2cpp-linemap upload has nothing (or no MethodMap) to send.
    /// </summary>
    public static class Il2CppMappingArgs
    {
        public const string SourceMapping = "--emit-source-mapping";
        public const string MethodMap = "--emit-method-map";

        /// <summary>
        /// Whether a build writes the mapping files: any IL2CPP build, with or without a Bugsee app token (a later CI job, or
        /// a developer, uploads the files; the upload itself still needs the token), unless BUGSEE_NO_IL2CPP_MAPPING is set.
        /// BUGSEE_IL2CPP_MAPPING (an explicit file to upload) does not turn the emission off.
        /// </summary>
        public static bool ShouldEmit(bool il2cppBackend, bool optOutEnvironmentSet) => il2cppBackend && !optOutEnvironmentSet;

        /// <summary>
        /// <paramref name="existing"/> (Player Settings > Additional IL2CPP Arguments) with the mapping arguments it is
        /// missing appended; the same string when it already has both. Existing arguments are never reordered or removed.
        /// </summary>
        public static string Ensure(string existing)
        {
            string result = existing ?? string.Empty;
            foreach (string flag in new[] { SourceMapping, MethodMap })
            {
                if (!Has(result, flag))
                {
                    result = result.Length == 0 || char.IsWhiteSpace(result[result.Length - 1]) ? result + flag : result + " " + flag;
                }
            }

            return result;
        }

        /// <summary>
        /// The value for the IL2CPP_ADDITIONAL_ARGS environment variable (Unity adds it to the il2cpp command line next to
        /// Player Settings > Additional IL2CPP Arguments, so the build does not have to edit the VCS-tracked ProjectSettings.asset):
        /// <paramref name="envArgs"/> (the variable's current value) with the mapping arguments that neither it nor
        /// <paramref name="playerArgs"/> has appended; null when nothing is missing and the variable needs no change.
        /// </summary>
        public static string EnvironmentValue(string playerArgs, string envArgs)
        {
            string seen = (playerArgs ?? string.Empty) + " " + (envArgs ?? string.Empty);
            string appended = Ensure(seen).Substring(seen.Length).Trim();
            if (appended.Length == 0)
            {
                return null;
            }

            return string.IsNullOrWhiteSpace(envArgs) ? appended : envArgs.TrimEnd() + " " + appended;
        }

        /// <summary>
        /// Unity writes the IL2CPP_ADDITIONAL_ARGS value into the exported Gradle project as ONE argument
        /// (<c>commandLineArgs.add("--emit-source-mapping --emit-method-map")</c>), so il2cpp rejects it ("unknown arguments
        /// were used") and <c>:unityLibrary:buildIl2Cpp</c> fails; Unity splits Player Settings > Additional IL2CPP Arguments
        /// itself, but not the environment variable. This puts every such argument that holds several <c>--</c> options, one of
        /// them a mapping option, on separate <c>add</c> lines (indentation, quote style, line endings, a trailing <c>;</c> and a
        /// trailing <c>// comment</c> are kept). Any other line is left alone, so a project that already has one argument per
        /// line, or a Gradle file without these lines, comes back unchanged.
        /// </summary>
        /// <param name="unsplit">1-based numbers of the lines that hold a mapping option among several words but could not be
        /// split safely (a value with a space, a backslash, a quote or <c>$</c>); the caller should tell the developer.</param>
        /// <returns>the text with the lines split; the same string when nothing needed splitting.</returns>
        public static string SplitGradleArguments(string gradle, out int[] unsplit)
        {
            var notSplit = new List<int>();
            unsplit = new int[0];
            if (string.IsNullOrEmpty(gradle) || gradle.IndexOf("commandLineArgs.add(", StringComparison.Ordinal) < 0)
            {
                return gradle;
            }

            var result = new System.Text.StringBuilder(gradle.Length + 64);
            bool changed = false;
            int pos = 0;
            int lineNumber = 0;
            while (pos < gradle.Length)
            {
                int end = gradle.IndexOf('\n', pos);
                int next = end < 0 ? gradle.Length : end + 1;
                string line = gradle.Substring(pos, next - pos);
                pos = next;
                lineNumber++;

                string replaced = SplitGradleLine(line, out bool mappingNotSplit);
                if (mappingNotSplit)
                {
                    notSplit.Add(lineNumber);
                }

                changed |= !ReferenceEquals(replaced, line);
                result.Append(replaced);
            }

            unsplit = notSplit.ToArray();
            return changed ? result.ToString() : gradle;
        }

        public static string SplitGradleArguments(string gradle) => SplitGradleArguments(gradle, out _);

        /// <summary>
        /// Splits the Gradle file at <paramref name="path"/> (<c>build.gradle</c> or <c>build.gradle.kts</c> of an exported module)
        /// and writes it back only when something changed (the text is read and written as is, so line endings and a missing
        /// final newline survive). <paramref name="unsplit"/> as in <see cref="SplitGradleArguments(string, out int[])"/>.
        /// </summary>
        /// <returns>true when the file was rewritten; false when it is missing, unreadable text aside, or needed nothing.</returns>
        public static bool SplitGradleFile(string path, out int[] unsplit)
        {
            unsplit = new int[0];
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            {
                return false;
            }

            string text = System.IO.File.ReadAllText(path);
            string updated = SplitGradleArguments(text, out unsplit);
            if (string.Equals(text, updated, StringComparison.Ordinal))
            {
                return false;
            }

            System.IO.File.WriteAllText(path, updated, new System.Text.UTF8Encoding(false));
            return true;
        }

        static string SplitGradleLine(string line, out bool mappingNotSplit)
        {
            mappingNotSplit = false;
            const string Call = "commandLineArgs.add(";
            int call = line.IndexOf(Call, StringComparison.Ordinal);
            if (call < 0 || line.Substring(0, call).Trim().Length != 0)
            {
                return line;
            }

            string indent = line.Substring(0, call);
            string body = line.Substring(call + Call.Length);
            string trimmed = body.TrimEnd('\r', '\n');
            string eol = body.Substring(trimmed.Length);

            int open = 0;
            while (open < trimmed.Length && (trimmed[open] == ' ' || trimmed[open] == '\t'))
            {
                open++;
            }

            if (open >= trimmed.Length || (trimmed[open] != '"' && trimmed[open] != '\''))
            {
                return line;
            }

            char quote = trimmed[open];
            int close = trimmed.IndexOf(quote, open + 1);
            if (close < 0)
            {
                return line;
            }

            string value = trimmed.Substring(open + 1, close - open - 1);
            string tail = trimmed.Substring(close + 1);
            int paren = 0;
            while (paren < tail.Length && (tail[paren] == ' ' || tail[paren] == '\t'))
            {
                paren++;
            }

            // After the closing parenthesis only an optional ';' and an optional // comment may follow.
            if (paren >= tail.Length || tail[paren] != ')')
            {
                return line;
            }

            string after = tail.Substring(paren + 1);
            string afterTrim = after.TrimStart(' ', '\t');
            if (afterTrim.StartsWith(";", StringComparison.Ordinal))
            {
                afterTrim = afterTrim.Substring(1).TrimStart(' ', '\t');
            }

            if (afterTrim.Trim().Length != 0 && !afterTrim.StartsWith("//", StringComparison.Ordinal))
            {
                return line;
            }

            string[] tokens = value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2)
            {
                return line;
            }

            bool mapping = false;
            bool plain = value.IndexOf('\\') < 0 && value.IndexOf('$') < 0;
            foreach (string token in tokens)
            {
                plain &= token.StartsWith("--", StringComparison.Ordinal);
                mapping |= token == SourceMapping || token == MethodMap ||
                           token.StartsWith(SourceMapping + "=", StringComparison.Ordinal) ||
                           token.StartsWith(MethodMap + "=", StringComparison.Ordinal);
            }

            if (!mapping)
            {
                return line;
            }

            if (!plain)
            {
                mappingNotSplit = true;
                return line;
            }

            // The text after ')' (';', spaces, comment) moves to the last line, the ';' to every line.
            string terminator = after.TrimStart(' ', '\t').StartsWith(";", StringComparison.Ordinal) ? ";" : string.Empty;
            string lastTail = after;
            string separator = eol.Length == 0 ? "\n" : eol;
            var split = new System.Text.StringBuilder();
            for (int i = 0; i < tokens.Length; i++)
            {
                split.Append(indent).Append(Call).Append(quote).Append(tokens[i]).Append(quote).Append(')');
                if (i < tokens.Length - 1)
                {
                    split.Append(terminator).Append(separator);
                }
                else
                {
                    split.Append(lastTail);
                }
            }

            return split.Append(eol).ToString();
        }

        static bool Has(string args, string flag)
        {
            var separators = new[] { ' ', '\t', '\r', '\n' };
            foreach (string token in args.Split(separators, StringSplitOptions.RemoveEmptyEntries))
            {
                if (token == flag || token.StartsWith(flag + "=", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
