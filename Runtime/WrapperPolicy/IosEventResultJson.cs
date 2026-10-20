using System;
using System.Globalization;
using System.Text;

namespace Bugsee.WrapperPolicy
{
    /// <summary>The log event fields a filter can set.</summary>
    [Flags]
    public enum IosLogField
    {
        None = 0,
        Message = 1,
        Level = 2,
    }

    /// <summary>The breadcrumb fields a filter can set (data is handled by its own object writer).</summary>
    [Flags]
    public enum IosBreadcrumbField
    {
        None = 0,
        Category = 1,
        Message = 2,
        Type = 4,
        Level = 8,
    }

    /// <summary>
    /// The JSON a log event or breadcrumb filter result hands to native (<c>_bugsee_complete_filter</c>). Native applies
    /// only the keys present, and a JSON null applies as an empty string (the SDK does not tell nil from empty: an empty log
    /// message drops the line, an empty breadcrumb category, type or message ends up omitted). Unity's JsonUtility writes a null string as "" and cannot
    /// omit a field, so serializing the whole event applied an empty category, message and type to every filtered
    /// event. Only the fields the filter assigned go back, whatever value they hold.
    /// </summary>
    public static class IosEventResultJson
    {
        /// <summary>Every field of a log event and of a breadcrumb: for an event the filter returned in place of the one it was handed.</summary>
        public const IosLogField AllLog = IosLogField.Message | IosLogField.Level;
        public const IosBreadcrumbField AllBreadcrumb = IosBreadcrumbField.Category | IosBreadcrumbField.Message | IosBreadcrumbField.Type | IosBreadcrumbField.Level;

        public static string ForLog(string message, int level, IosLogField touched)
        {
            var w = new Writer();
            if ((touched & IosLogField.Message) != 0) w.Str("message", message);
            if ((touched & IosLogField.Level) != 0) w.Raw("level", Num(level));
            return w.End();
        }

        public static string ForBreadcrumb(string category, string message, string type, int level, IosBreadcrumbField touched)
        {
            var w = new Writer();
            if ((touched & IosBreadcrumbField.Category) != 0) w.Str("category", category);
            if ((touched & IosBreadcrumbField.Message) != 0) w.Str("message", message);
            if ((touched & IosBreadcrumbField.Type) != 0) w.Str("type", type);
            if ((touched & IosBreadcrumbField.Level) != 0) w.Raw("level", Num(level));
            return w.End();
        }

        // Invariant: a culture's minus sign would make the JSON invalid and native would drop the whole result.
        static string Num(long n) => n.ToString(CultureInfo.InvariantCulture);

        sealed class Writer
        {
            readonly StringBuilder _sb = new StringBuilder("{");
            bool _any;

            public void Raw(string key, string json)
            {
                if (_any) _sb.Append(',');
                _any = true;
                _sb.Append('"').Append(key).Append("\":").Append(json);
            }

            public void Str(string key, string value) => Raw(key, value == null ? "null" : JsonWire.String(value));

            public string End() => _sb.Append('}').ToString();
        }
    }
}
