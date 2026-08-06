namespace Bugsee.Contracts.Options
{
    public enum VideoQuality
    {
        Default = 0,
        Medium = 1,
        High = 2
    }

    public static class VideoQualityExtensions
    {
        public static VideoQuality FromIntValue(int value, VideoQuality? defaultValue = null)
        {
            switch (value)
            {
                case 0: return VideoQuality.Default;
                case 1: return VideoQuality.Medium;
                case 2: return VideoQuality.High;
                default: return defaultValue ?? VideoQuality.Default;
            }
        }
    }
}
