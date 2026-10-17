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
