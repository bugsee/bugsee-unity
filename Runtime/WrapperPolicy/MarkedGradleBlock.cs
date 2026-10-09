using System;
using System.Collections.Generic;
using System.Text;

namespace Bugsee.WrapperPolicy
{
    public static class MarkedGradleBlock
    {
        internal const string MarkerComment = "// bugsee:gradle-plugin";
        internal const string NdkMarkerComment = "// bugsee:gradle-ndk";
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

            SplitPayload(markedLine, out List<string> pluginLines, out List<string> ndkLines);
            if (IsPluginPayloadSatisfied(existing, pluginLines) &&
                IsNdkPayloadSatisfied(existing, ndkLines))
            {
                return existing;
            }

            var lines = SplitLines(existing, out string newline);
            bool changed = false;

            if (TryUpsertPluginRegion(lines, pluginLines, ndkLines))
            {
                changed = true;
            }

            if (ndkLines != null && ndkLines.Count > 0)
            {
                if (TryUpsertNdkRegion(lines, ndkLines))
                {
                    changed = true;
                }
                else if (TryInsertNdkAfterPlugin(lines, ndkLines))
                {
                    changed = true;
                }
            }

            if (changed)
            {
                return JoinLines(lines, newline, existing.EndsWith("\n", StringComparison.Ordinal));
            }

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

            return InsertAfterLine(existing, lines, newline, anchorIndex, markedLine);
        }

        static string NormalizeNewlines(string text)
        {
            return text.Replace("\r\n", "\n").Replace("\r", "\n");
        }

        static bool IsPluginPayloadSatisfied(string existing, List<string> pluginLines)
        {
            var lines = SplitLines(existing, out _);
            bool required = false;
            for (int i = 0; i < pluginLines.Count; i++)
            {
                if (IsBlank(pluginLines[i]))
                {
                    continue;
                }

                required = true;
                bool found = false;
                for (int j = 0; j < lines.Count; j++)
                {
                    if (string.Equals(lines[j], pluginLines[i], StringComparison.Ordinal))
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    return false;
                }
            }

            return required;
        }

        static bool IsNdkPayloadSatisfied(string existing, List<string> ndkLines)
        {
            if (ndkLines == null || ndkLines.Count == 0)
            {
                return true;
            }

            var lines = SplitLines(existing, out _);
            if (!TryFindNdkRegion(lines, out int start, out int end))
            {
                return false;
            }

            return LinesEqual(lines, start, end, ndkLines);
        }

        static void SplitPayload(string markedLine, out List<string> pluginLines, out List<string> ndkLines)
        {
            var all = SplitMarkedLines(markedLine);
            pluginLines = new List<string>();
            ndkLines = null;
            int ndkIndex = -1;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].TrimStart().StartsWith("bugsee {", StringComparison.Ordinal))
                {
                    ndkIndex = i;
                    break;
                }
            }

            if (ndkIndex < 0)
            {
                pluginLines = all;
                return;
            }

            for (int i = 0; i < ndkIndex; i++)
            {
                if (!IsBlank(all[i]))
                {
                    pluginLines.Add(all[i]);
                }
            }

            ndkLines = new List<string>();
            for (int i = ndkIndex; i < all.Count; i++)
            {
                ndkLines.Add(all[i]);
            }
        }

        static bool TryUpsertPluginRegion(List<string> lines, List<string> pluginLines, List<string> ndkLines)
        {
            if (!TryFindPluginRegion(lines, out int start, out int end))
            {
                return false;
            }

            var replacement = BuildPluginReplacement(lines, start, end, pluginLines, ndkLines);
            if (LinesEqual(lines, start, end, replacement))
            {
                return false;
            }

            ReplaceLines(lines, start, end, replacement);
            return true;
        }

        static List<string> BuildPluginReplacement(
            List<string> lines,
            int start,
            int end,
            List<string> pluginLines,
            List<string> ndkLines)
        {
            var replacement = new List<string>(pluginLines);
            bool regionIncludesBugsee = false;
            for (int i = start; i <= end; i++)
            {
                if (lines[i].TrimStart().StartsWith("bugsee {", StringComparison.Ordinal))
                {
                    regionIncludesBugsee = true;
                    break;
                }
            }

            if (regionIncludesBugsee && ndkLines != null && ndkLines.Count > 0)
            {
                replacement.Add(string.Empty);
                replacement.AddRange(ndkLines);
            }

            return replacement;
        }

        static bool TryUpsertNdkRegion(List<string> lines, List<string> ndkLines)
        {
            if (!TryFindNdkRegion(lines, out int start, out int end))
            {
                return false;
            }

            if (LinesEqual(lines, start, end, ndkLines))
            {
                return false;
            }

            ReplaceLines(lines, start, end, ndkLines);
            return true;
        }

        static bool TryInsertNdkAfterPlugin(List<string> lines, List<string> ndkLines)
        {
            if (TryFindNdkRegion(lines, out _, out _))
            {
                return false;
            }

            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].TrimStart().StartsWith("bugsee {", StringComparison.Ordinal))
                {
                    return false;
                }
            }

            if (!TryFindPluginLineIndex(lines, out int pluginIndex))
            {
                return false;
            }

            var insert = new List<string> { string.Empty };
            insert.AddRange(ndkLines);
            lines.InsertRange(pluginIndex + 1, insert);
            return true;
        }

        static bool TryFindPluginRegion(List<string> lines, out int start, out int end)
        {
            start = -1;
            end = -1;

            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].IndexOf(MarkerComment, StringComparison.Ordinal) >= 0)
                {
                    start = i;
                    end = FindContiguousPluginEnd(lines, start);
                    return true;
                }
            }

            for (int i = 0; i < lines.Count; i++)
            {
                if (string.Equals(lines[i].Trim(), LegacyMarkerComment, StringComparison.Ordinal))
                {
                    start = i;
                    end = i;
                    if (i + 1 < lines.Count && IsActiveBugseePluginLine(lines[i + 1]))
                    {
                        end = i + 1;
                    }

                    end = ExtendThroughContiguousBugsee(lines, end);
                    return true;
                }
            }

            for (int i = 0; i < lines.Count; i++)
            {
                if (IsActiveBugseePluginLine(lines[i]))
                {
                    start = i;
                    end = FindContiguousPluginEnd(lines, start);
                    return true;
                }
            }

            return false;
        }

        static int FindContiguousPluginEnd(List<string> lines, int start)
        {
            return ExtendThroughContiguousBugsee(lines, start);
        }

        static int ExtendThroughContiguousBugsee(List<string> lines, int end)
        {
            int i = end + 1;
            while (i < lines.Count && IsBlank(lines[i]))
            {
                i++;
            }

            if (i >= lines.Count || !lines[i].TrimStart().StartsWith("bugsee {", StringComparison.Ordinal))
            {
                return end;
            }

            return FindBugseeBlockEnd(lines, i);
        }

        static int FindBugseeBlockEnd(List<string> lines, int bugseeStart)
        {
            int depth = 0;
            for (int j = bugseeStart; j < lines.Count; j++)
            {
                depth += CountBraceDelta(lines[j]);
                if (depth <= 0 && lines[j].IndexOf('}') >= 0)
                {
                    return j;
                }
            }

            return bugseeStart;
        }

        static bool TryFindNdkRegion(List<string> lines, out int start, out int end)
        {
            start = -1;
            end = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].IndexOf(NdkMarkerComment, StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                end = i;
                start = i;
                for (int j = i; j >= 0; j--)
                {
                    if (lines[j].TrimStart().StartsWith("bugsee {", StringComparison.Ordinal))
                    {
                        start = j;
                        break;
                    }
                }

                return true;
            }

            for (int i = 0; i < lines.Count; i++)
            {
                if (!lines[i].TrimStart().StartsWith("bugsee {", StringComparison.Ordinal))
                {
                    continue;
                }

                start = i;
                end = FindBugseeBlockEnd(lines, i);
                return true;
            }

            return false;
        }

        static bool TryFindPluginLineIndex(List<string> lines, out int pluginIndex)
        {
            pluginIndex = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].IndexOf(MarkerComment, StringComparison.Ordinal) >= 0 ||
                    IsActiveBugseePluginLine(lines[i]))
                {
                    pluginIndex = i;
                    return true;
                }
            }

            return false;
        }

        static int CountBraceDelta(string line)
        {
            int delta = 0;
            bool inSingle = false;
            bool inDouble = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (!inSingle && !inDouble && c == '/' && i + 1 < line.Length && line[i + 1] == '/')
                {
                    break;
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

                if (inSingle || inDouble)
                {
                    continue;
                }

                if (c == '{')
                {
                    delta++;
                }
                else if (c == '}')
                {
                    delta--;
                }
            }

            return delta;
        }

        static bool LinesEqual(List<string> lines, int start, int end, List<string> replacement)
        {
            if (end - start + 1 != replacement.Count)
            {
                return false;
            }

            for (int i = 0; i < replacement.Count; i++)
            {
                if (!string.Equals(lines[start + i], replacement[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        static void ReplaceLines(List<string> lines, int start, int end, List<string> replacement)
        {
            int removeCount = end - start + 1;
            lines.RemoveRange(start, removeCount);
            lines.InsertRange(start, replacement);
        }

        static string JoinLines(List<string> lines, string newline, bool trailingNewline)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < lines.Count; i++)
            {
                sb.Append(lines[i]);
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

        static bool IsBlank(string line)
        {
            return string.IsNullOrWhiteSpace(line);
        }

        static void AppendMarkedLines(StringBuilder sb, string markedLine, string newline)
        {
            var markedLines = SplitMarkedLines(markedLine);
            for (int m = 0; m < markedLines.Count; m++)
            {
                if (m > 0)
                {
                    sb.Append(newline);
                }

                sb.Append(markedLines[m]);
            }
        }

        static List<string> SplitMarkedLines(string markedLine)
        {
            string normalized = NormalizeNewlines(markedLine);
            var parts = normalized.Split('\n');
            var result = new List<string>(parts.Length);
            for (int i = 0; i < parts.Length; i++)
            {
                result.Add(parts[i]);
            }

            return result;
        }

        static string InsertAfterLine(
            string existing,
            List<string> lines,
            string newline,
            int anchorIndex,
            string markedLine)
        {
            bool trailingNewline = existing.EndsWith("\n", StringComparison.Ordinal);
            var sb = new StringBuilder(existing.Length + markedLine.Length + newline.Length + 8);
            for (int i = 0; i < lines.Count; i++)
            {
                sb.Append(lines[i]);
                if (i == anchorIndex)
                {
                    sb.Append(newline);
                    AppendMarkedLines(sb, markedLine, newline);
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
                    if (StartsWithAt(line, i, token))
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

                if (!inSingle && !inDouble && StartsWithAt(line, i, token))
                {
                    return i;
                }
            }

            return -1;
        }

        static bool StartsWithAt(string line, int index, string token)
        {
            if (index + token.Length > line.Length)
            {
                return false;
            }

            return string.Compare(line, index, token, 0, token.Length, StringComparison.Ordinal) == 0;
        }

        static bool IsActiveBugseePluginLine(string line)
        {
            if (line.IndexOf(BugseeGradlePluginId, StringComparison.Ordinal) < 0)
            {
                return false;
            }

            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                return false;
            }

            return trimmed.IndexOf("id ", StringComparison.Ordinal) >= 0 ||
                   trimmed.IndexOf("apply plugin", StringComparison.Ordinal) >= 0;
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
