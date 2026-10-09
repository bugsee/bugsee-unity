using Bugsee.Contracts.Options;

namespace Bugsee.WrapperPolicy
{
    public static class IssueSeverityWire
    {
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
