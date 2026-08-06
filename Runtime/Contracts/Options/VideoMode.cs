namespace Bugsee.Contracts.Options
{
    /// <summary>Mirrors com.bugsee.library.contracts.options.VideoMode.</summary>
    public enum VideoMode
    {
        None = 0,
        V1 = 1,
        V2 = 2,
        Fullscreen = 20,
        DirectBuffers = 21
    }

    public static class VideoModeExtensions
    {
        public static bool IsNone(this VideoMode mode) => mode == VideoMode.None;
        public static bool IsCustomSource(this VideoMode mode) => mode == VideoMode.DirectBuffers;

        public static VideoMode FromIntValue(int value, VideoMode? defaultValue = null)
        {
            switch (value)
            {
                case 0: return VideoMode.None;
                case 1: return VideoMode.V1;
                case 2: return VideoMode.V2;
                case 20: return VideoMode.Fullscreen;
                case 21: return VideoMode.DirectBuffers;
                default: return defaultValue ?? VideoMode.V2;
            }
        }
    }
}
