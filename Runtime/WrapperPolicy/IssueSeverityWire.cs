using Bugsee.Contracts.Options;

namespace Bugsee.WrapperPolicy
{
    public static class IssueSeverityWire
    {
        /// <summary>The wire value of an optional severity: 0 when omitted (the SDK then applies its default).</summary>
        public static int ToWire(IssueSeverity? severity) => severity.HasValue ? (int)severity.Value : 0;

        public static bool TryFromWire(int value, out IssueSeverity severity)
        {
            switch (value)
            {
                case 1:
                    severity = IssueSeverity.VeryLow;
                    return true;
                case 2:
                    severity = IssueSeverity.Medium;
                    return true;
                case 3:
                    severity = IssueSeverity.High;
                    return true;
                case 4:
                    severity = IssueSeverity.Critical;
                    return true;
                case 5:
                    severity = IssueSeverity.Blocker;
                    return true;
                default:
                    severity = default;
                    return false;
            }
        }
    }
}
