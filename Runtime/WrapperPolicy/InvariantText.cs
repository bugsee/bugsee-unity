using System;
using System.Globalization;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Text for a value that is about to cross to native or into an identifier: the same on every device, whatever
    /// its culture (1.5 stays "1.5", not "1,5"; a bool is "true", not "True"; a date is ISO 8601).
    /// </summary>
    public static class InvariantText
    {
        public static string Of(object value)
        {
            switch (value)
            {
                case null: return string.Empty;
                case string s: return s;
                case bool b: return b ? "true" : "false";
                case DateTime dt: return dt.ToString("o", CultureInfo.InvariantCulture);
                case DateTimeOffset dto: return dto.ToString("o", CultureInfo.InvariantCulture);
                case Enum e: return e.ToString();
                default: return Convert.ToString(value, CultureInfo.InvariantCulture);
            }
        }
    }
}
