namespace Bugsee.Contracts.Options
{
    public enum IssueType
    {
        Bug,
        Crash,
        Error
    }

    public static class IssueTypeExtensions
    {
        public static string ToWireValue(this IssueType type)
        {
            switch (type)
            {
                case IssueType.Crash: return "crash";
                case IssueType.Error: return "error";
                default: return "bug";
            }
        }

        public static IssueType FromWireValue(string value, IssueType defaultType = IssueType.Bug)
        {
            if (string.IsNullOrEmpty(value)) return defaultType;
            switch (value)
            {
                case "crash": return IssueType.Crash;
                case "error": return IssueType.Error;
                case "bug": return IssueType.Bug;
                default: return defaultType;
            }
        }
    }
}
