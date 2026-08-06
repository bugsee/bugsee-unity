namespace Bugsee.Contracts.Options
{
    public enum FrameRate
    {
        Low = 1,
        Medium = 2,
        High = 3,
        /// <summary>No cap; meaningful for DirectBuffers only.</summary>
        Raw = 4
    }

    public static class FrameRateExtensions
    {
        public static FrameRate FromIntValue(int value, FrameRate? defaultValue = null)
        {
            switch (value)
            {
                case 1: return FrameRate.Low;
                case 2: return FrameRate.Medium;
                case 3: return FrameRate.High;
                case 4: return FrameRate.Raw;
                default: return defaultValue ?? FrameRate.High;
            }
        }
    }
}
