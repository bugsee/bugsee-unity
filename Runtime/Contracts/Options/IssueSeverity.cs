namespace Bugsee.Contracts.Options
{
    public enum IssueSeverity
    {
        VeryLow = 1,
        Medium = 2,
        High = 3,
        Critical = 4,
        Blocker = 5
    }

    public static class IssueSeverityExtensions
    {
        public static IssueSeverity FromIntValue(int value, IssueSeverity defaultValue = IssueSeverity.VeryLow)
        {
            switch (value)
            {
                case 1: return IssueSeverity.VeryLow;
                case 2: return IssueSeverity.Medium;
                case 3: return IssueSeverity.High;
                case 4: return IssueSeverity.Critical;
                case 5: return IssueSeverity.Blocker;
                default: return defaultValue;
            }
        }
    }
}
