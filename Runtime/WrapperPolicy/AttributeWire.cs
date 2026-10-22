using System;
using System.Globalization;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// iOS attribute wire. Set sends the value as a JSON fragment; get returns a one-letter type tag, a colon and
    /// the payload (s string, b bool as 1/0, i integer, d floating point, j JSON container), so the native
    /// SDK's value kind survives the trip as it does on Android. Text without a known tag is returned as is.
    /// </summary>
    public static class AttributeWire
    {
        public static string ToJson(object value)
        {
            // JsonWire writes 1.0 as 1, which the SDK would store as an integer.
            // A decimal is floating point on Android (ToBoxable), so it must be on iOS too.
            if (value is decimal m) value = (double)m;
            string text = JsonWire.Value(value);
            if ((value is float || value is double) && text.IndexOfAny(new[] { '.', 'E' }) < 0)
                return text + ".0";
            return text;
        }

        /// <summary>
        /// Maps the numeric types the Android boxer has no Java class for onto ones it has (sbyte and ushort to int,
        /// uint and ulong to long, decimal to double), so they read back as numbers rather than as text. Call after
        /// <see cref="AttributePolicy.Evaluate"/>, which bounds ulong and decimal to the signed 64-bit range.
        /// </summary>
        public static object ToBoxable(object value)
        {
            switch (value)
            {
                case sbyte sb: return (int)sb;
                case ushort us: return (int)us;
                case uint ui: return (long)ui;
                case ulong ul: return ul <= long.MaxValue ? (object)(long)ul : (double)ul;
                case decimal m: return (double)m;
                default: return value;
            }
        }

        /// <summary>Longest JSON text a container value may send to Android.</summary>
        public const int MaxContainerJsonLength = 8192;

        /// <summary>
        /// The value the Android boxer can carry: null, string, bool, or a number (the boxable widths of
        /// <see cref="ToBoxable"/>; a float is sent as the double that prints the same, so 1.1f reads back 1.1). A
        /// map, list or array crosses as JSON text (<see cref="JsonWire.Bounded"/>: valid JSON of at most
        /// <see cref="MaxContainerJsonLength"/> characters, whole trailing entries dropped when it does not fit), a
        /// byte[] as base64, an enum by its integer value, and anything else as its culture-invariant text, never a type
        /// name or a text that depends on the device's culture. Other enumerables (a Transform) are text, not walked.
        /// </summary>
        public static object ToNative(object value)
        {
            switch (value)
            {
                case null:
                case string _:
                case bool _:
                case int _:
                case long _:
                case double _:
                case byte _:
                case short _:
                    return value;
                case float f:
                    return float.IsNaN(f) || float.IsInfinity(f)
                        ? (object)f
                        : double.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
                case sbyte _:
                case ushort _:
                case uint _:
                case ulong _:
                case decimal _:
                    return ToBoxable(value);
                case Enum e:
                    var underlying = Convert.ChangeType(e, Enum.GetUnderlyingType(e.GetType()), CultureInfo.InvariantCulture);
                    if (underlying is ulong big && big > long.MaxValue)
                        return big.ToString(CultureInfo.InvariantCulture); // exact, as JsonWire writes it
                    return ToBoxable(underlying);
                case byte[] bytes:
                    return Convert.ToBase64String(bytes);
                case DateTime dt:
                    return dt.ToString("o", CultureInfo.InvariantCulture);
                case DateTimeOffset dto:
                    return dto.ToString("o", CultureInfo.InvariantCulture);
                default:
                    if (JsonWire.IsContainer(value))
                        return JsonWire.Bounded(value, MaxContainerJsonLength, out _);
                    return Convert.ToString(value, CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// The JSON the iOS bridge passes to <c>Trace</c>. A map, list or array is the same bounded, always valid JSON
        /// Android receives as text (<see cref="JsonWire.Bounded"/>, at most <see cref="MaxContainerJsonLength"/> UTF-16 units,
        /// whole trailing entries dropped), here as JSON the SDK decodes to a native collection. Other enumerables are
        /// text, as on Android; scalars are their JSON value with a float staying floating (<see cref="ToJson"/>).
        /// </summary>
        public static string ToTraceJson(object value)
        {
            if (JsonWire.IsContainer(value))
                return JsonWire.Bounded(value, MaxContainerJsonLength, out _);
            if (value is System.Collections.IEnumerable && !(value is string) && !(value is byte[]))
                return JsonWire.String(Convert.ToString(value, CultureInfo.InvariantCulture));
            // NaN and the infinities have no JSON form: JsonWire writes null (ToJson would make it "null.0").
            if ((value is float f && (float.IsNaN(f) || float.IsInfinity(f)))
                || (value is double d && (double.IsNaN(d) || double.IsInfinity(d))))
                return "null";
            return ToJson(value);
        }

        public static object Decode(string wire)
        {
            if (wire == null) return null;
            if (wire.Length < 2 || wire[1] != ':') return wire;

            string body = wire.Substring(2);
            switch (wire[0])
            {
                case 's':
                case 'j':
                    return body;
                case 'b':
                    if (body == "1") return true;
                    if (body == "0") return false;
                    break;
                case 'i':
                    if (long.TryParse(body, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l))
                        return l;
                    if (ulong.TryParse(body, NumberStyles.None, CultureInfo.InvariantCulture, out ulong u))
                        return (double)u;
                    break;
                case 'd':
                    if (double.TryParse(body, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                        return d;
                    break;
            }
            return wire;
        }
    }
}
