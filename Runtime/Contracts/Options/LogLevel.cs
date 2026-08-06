namespace Bugsee.Contracts.Options
{
    public enum LogLevel
    {
        Error = 1,
        Warning = 2,
        Info = 3,
        Debug = 4,
        Verbose = 5
    }

    public static class LogLevelExtensions
    {
        public static LogLevel FromRawValue(int value, LogLevel? defaultValue = null)
        {
            switch (value)
            {
                case 1: return LogLevel.Error;
                case 2: return LogLevel.Warning;
                case 3: return LogLevel.Info;
                case 4: return LogLevel.Debug;
                case 5: return LogLevel.Verbose;
                default: return defaultValue ?? LogLevel.Info;
            }
        }
    }
}
