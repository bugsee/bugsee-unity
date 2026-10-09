using System;
using Bugsee.WrapperPolicy;

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
            if (IssueSeverityWire.TryFromWire(value, out var severity))
                return severity;

            throw new ArgumentOutOfRangeException(
                nameof(value),
                "severity wire value is unset or out of range " + value);
        }
    }
}
