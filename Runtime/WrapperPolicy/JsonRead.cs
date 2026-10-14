using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Reads the JSON object the iOS bridge returns (launch options) into plain managed values: object to
    /// <c>Dictionary&lt;string, object&gt;</c>, array to <c>List&lt;object&gt;</c>, string, bool, long (or double when
    /// the number is fractional, has an exponent or does not fit a long) and null. Malformed input yields null, never
    /// an exception, and nesting is bounded by <see cref="JsonWire.MaxDepth"/>.
    /// </summary>
    public static class JsonRead
    {
        public static Dictionary<string, object> Object(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                int i = 0;
                SkipSpace(json, ref i);
                if (i >= json.Length || json[i] != '{') return null;
                var map = (Dictionary<string, object>)ReadValue(json, ref i, 0);
                SkipSpace(json, ref i);
                return i == json.Length ? map : null;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        static void SkipSpace(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
        }

        static object ReadValue(string s, ref int i, int depth)
        {
            if (depth > JsonWire.MaxDepth) throw new FormatException();
            SkipSpace(s, ref i);
            if (i >= s.Length) throw new FormatException();
            char c = s[i];
            if (c == '{') return ReadObject(s, ref i, depth);
            if (c == '[') return ReadArray(s, ref i, depth);
            if (c == '"') return ReadString(s, ref i);
            if (Match(s, ref i, "true")) return true;
            if (Match(s, ref i, "false")) return false;
            if (Match(s, ref i, "null")) return null;
            return ReadNumber(s, ref i);
        }

        static bool Match(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) return false;
            i += word.Length;
            return true;
        }

        static Dictionary<string, object> ReadObject(string s, ref int i, int depth)
        {
            var map = new Dictionary<string, object>();
            i++;
            SkipSpace(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return map; }
            while (true)
            {
                SkipSpace(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new FormatException();
                string key = ReadString(s, ref i);
                SkipSpace(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException();
                i++;
                map[key] = ReadValue(s, ref i, depth + 1);
                SkipSpace(s, ref i);
                if (i >= s.Length) throw new FormatException();
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return map; }
                throw new FormatException();
            }
        }

        static List<object> ReadArray(string s, ref int i, int depth)
        {
            var list = new List<object>();
            i++;
            SkipSpace(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return list; }
            while (true)
            {
                list.Add(ReadValue(s, ref i, depth + 1));
                SkipSpace(s, ref i);
                if (i >= s.Length) throw new FormatException();
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return list; }
                throw new FormatException();
            }
        }

        static string ReadString(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++;
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length ||
                            !ushort.TryParse(s.Substring(i, 4), NumberStyles.AllowHexSpecifier,
                                CultureInfo.InvariantCulture, out ushort unit))
                            throw new FormatException();
                        sb.Append((char)unit);
                        i += 4;
                        break;
                    default: throw new FormatException();
                }
            }
            throw new FormatException();
        }

        static void Digits(string s, ref int i)
        {
            int begin = i;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
            if (i == begin) throw new FormatException();
        }

        static object ReadNumber(string s, ref int i)
        {
            // The JSON grammar: -? (0 | [1-9][0-9]*) (. [0-9]+)? ([eE] [+-]? [0-9]+)?
            int start = i;
            if (i < s.Length && s[i] == '-') i++;
            if (i < s.Length && s[i] == '0') i++;
            else Digits(s, ref i);
            if (i < s.Length && s[i] == '.') { i++; Digits(s, ref i); }
            if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
            {
                i++;
                if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
                Digits(s, ref i);
            }
            string text = s.Substring(start, i - start);
            if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l)) return l;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
            throw new FormatException();
        }
    }
}
