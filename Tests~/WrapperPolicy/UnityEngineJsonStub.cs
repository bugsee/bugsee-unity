using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace UnityEngine
{
    /// <summary>
    /// Host-side stand-in for the part of <c>UnityEngine.JsonUtility</c> the iOS wire code uses (<c>ToJson</c>),
    /// checked against Unity 2022.3 output. It reproduces: public instance fields in declaration order, an enum as
    /// its integer, a null string as "", a null array as [], a null nested object as a default instance, and the
    /// omission of readonly fields, [NonSerialized] fields, fields of a class without [Serializable], and a field
    /// whose type is its own declaring class. It does not reproduce float, double or char formatting, so those throw
    /// instead of silently differing.
    /// </summary>
    static class JsonUtility
    {
        public static string ToJson(object value)
        {
            var sb = new StringBuilder();
            WriteObject(sb, value);
            return sb.ToString();
        }

        static void WriteObject(StringBuilder sb, object value)
        {
            sb.Append('{');
            bool first = true;
            foreach (var f in value.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (f.IsInitOnly || f.IsNotSerialized) continue;
                var elem = f.FieldType.IsArray ? f.FieldType.GetElementType() : f.FieldType;
                if (elem == value.GetType()) continue;
                if (!elem.IsPrimitive && !elem.IsEnum && elem != typeof(string) && !elem.IsSerializable) continue;
                if (!first) sb.Append(',');
                first = false;
                WriteString(sb, f.Name);
                sb.Append(':');
                WriteValue(sb, f.GetValue(value), f.FieldType);
            }
            sb.Append('}');
        }

        static void WriteValue(StringBuilder sb, object v, Type t)
        {
            if (t == typeof(string)) WriteString(sb, (string)v ?? "");
            else if (t == typeof(bool)) sb.Append((bool)v ? "true" : "false");
            else if (t == typeof(char) || t == typeof(float) || t == typeof(double))
                throw new NotSupportedException("JsonUtility stand-in does not reproduce " + t.Name + " formatting");
            else if (t.IsEnum) sb.Append(Convert.ToString(Convert.ChangeType(v, Enum.GetUnderlyingType(t)), CultureInfo.InvariantCulture));
            else if (t.IsPrimitive) sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
            else if (t.IsArray)
            {
                sb.Append('[');
                bool first = true;
                var arr = (IEnumerable)v;
                if (arr != null)
                {
                    foreach (var e in arr)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteValue(sb, e, t.GetElementType());
                    }
                }
                sb.Append(']');
            }
            else WriteObject(sb, v ?? Activator.CreateInstance(t, true));
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
