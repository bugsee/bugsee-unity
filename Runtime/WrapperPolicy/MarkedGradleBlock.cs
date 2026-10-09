using System;
using System.Collections.Generic;
using System.Text;

namespace Bugsee.WrapperPolicy
{
    public static class MarkedGradleBlock
    {
        const string MarkerComment = "// bugsee:gradle-plugin";
        const string LegacyMarkerComment = "// Bugsee Gradle plugin";
        const string BugseeGradlePluginId = "com.bugsee.android.gradle";

        public static string Apply(string existing, string anchorLine, string markedLine)
        {
            if (existing == null)
            {
                throw new ArgumentNullException(nameof(existing));
            }

            if (anchorLine == null)
            {
                throw new ArgumentNullException(nameof(anchorLine));
            }

            if (markedLine == null)
            {
                throw new ArgumentNullException(nameof(markedLine));
            }

            if (IsAlreadyPatched(existing, markedLine))
            {
                return existing;
            }

            var lines = SplitLines(existing, out string newline);
            int depth = 0;
            bool inBlockComment = false;
            int anchorIndex = -1;
            int matchCount = 0;

            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                if (!inBlockComment && depth == 0 &&
                    string.Equals(line, anchorLine, StringComparison.Ordinal))
                {
                    if (HasUnclosedBlockCommentOnLine(line))
                    {
                        throw Refuse(anchorLine);
                    }

                    matchCount++;
                    anchorIndex = i;
                }

                ScanLine(line, ref depth, ref inBlockComment);
            }

            if (matchCount != 1)
            {
                throw Refuse(anchorLine);
            }

            bool trailingNewline = existing.EndsWith("\n", StringComparison.Ordinal);
            var sb = new StringBuilder(existing.Length + markedLine.Length + newline.Length + 8);
            for (int i = 0; i < lines.Count; i++)
            {
                sb.Append(lines[i]);
                if (i == anchorIndex)
                {
                    sb.Append(newline);
                    sb.Append(markedLine);
                }

                if (i < lines.Count - 1)
                {
                    sb.Append(newline);
                }
                else if (trailingNewline)
                {
                    sb.Append(newline);
                }
            }

            return sb.ToString();
        }

        static InvalidOperationException Refuse(string anchorLine)
        {
            return new InvalidOperationException(
                "Gradle edit refused; expected a single anchor line: " + anchorLine);
        }

        static bool IsAlreadyPatched(string existing, string markedLine)
        {
            if (existing.IndexOf(markedLine, StringComparison.Ordinal) >= 0)
            {
                return true;
            }

            if (existing.IndexOf(MarkerComment, StringComparison.Ordinal) >= 0)
            {
                return true;
            }

            if (existing.IndexOf(LegacyMarkerComment, StringComparison.Ordinal) >= 0)
            {
                return true;
            }

            var lines = SplitLines(existing, out _);
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                if (line.IndexOf(BugseeGradlePluginId, StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                if (line.IndexOf("id ", StringComparison.Ordinal) >= 0 ||
                    line.IndexOf("apply plugin", StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        static bool HasUnclosedBlockCommentOnLine(string line)
        {
            int search = 0;
            while (search < line.Length)
            {
                int open = IndexOfOutsideStrings(line, "/*", search);
                if (open < 0)
                {
                    return false;
                }

                int close = IndexOfOutsideStrings(line, "*/", open + 2);
                if (close < 0)
                {
                    return true;
                }

                search = close + 2;
            }

            return false;
        }

        static int IndexOfOutsideStrings(string line, string token, int start)
        {
            bool inSingle = false;
            bool inDouble = false;
            for (int i = start; i <= line.Length - token.Length; i++)
            {
                char c = line[i];
                if (!inSingle && !inDouble && c == '/' && i + 1 < line.Length && line[i + 1] == '/')
                {
                    break;
                }

                if (!inSingle && !inDouble && c == '/' && i + 1 < line.Length && line[i + 1] == '*')
                {
                    if (line.AsSpan(i).StartsWith(token))
                    {
                        return i;
                    }
                }

                if (c == '\'' && !inDouble)
                {
                    inSingle = !inSingle;
                    continue;
                }

                if (c == '"' && !inSingle)
                {
                    inDouble = !inDouble;
                    continue;
                }

                if (!inSingle && !inDouble && line.AsSpan(i).StartsWith(token))
                {
                    return i;
                }
            }

            return -1;
        }

        static List<string> SplitLines(string text, out string newline)
        {
            newline = text.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
            var lines = new List<string>();
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n')
                {
                    continue;
                }

                int end = i;
                if (end > 0 && text[end - 1] == '\r')
                {
                    end--;
                }

                lines.Add(text.Substring(start, end - start));
                start = i + 1;
            }

            if (start <= text.Length)
            {
                lines.Add(text.Substring(start));
            }

            return lines;
        }

        static void ScanLine(string line, ref int depth, ref bool inBlockComment)
        {
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inBlockComment)
                {
                    if (c == '*' && i + 1 < line.Length && line[i + 1] == '/')
                    {
                        inBlockComment = false;
                        i++;
                    }

                    continue;
                }

                if (c == '/' && i + 1 < line.Length)
                {
                    if (line[i + 1] == '/')
                    {
                        return;
                    }

                    if (line[i + 1] == '*')
                    {
                        inBlockComment = true;
                        i++;
                        continue;
                    }
                }

                if (c == '\'' || c == '"')
                {
                    char quote = c;
                    i++;
                    while (i < line.Length)
                    {
                        if (line[i] == '\\' && i + 1 < line.Length)
                        {
                            i += 2;
                            continue;
                        }

                        if (line[i] == quote)
                        {
                            break;
                        }

                        i++;
                    }

                    continue;
                }

                if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                }
            }
        }
    }
}
