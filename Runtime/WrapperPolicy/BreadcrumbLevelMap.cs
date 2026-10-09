using Bugsee.Contracts.Options;

namespace Bugsee.WrapperPolicy
{
    public enum BreadcrumbLevelName
    {
        Debug,
        Info,
        Warning,
        Error,
        Fatal
    }

    public static class BreadcrumbLevelMap
    {
        public static bool TryParse(string name, out BreadcrumbLevelName level)
        {
            switch (name)
            {
                case "debug": level = BreadcrumbLevelName.Debug; return true;
                case "info": level = BreadcrumbLevelName.Info; return true;
                case "warning": level = BreadcrumbLevelName.Warning; return true;
                case "error": level = BreadcrumbLevelName.Error; return true;
                case "fatal": level = BreadcrumbLevelName.Fatal; return true;
                default:
                    level = default;
                    return false;
            }
        }

        public static bool TryFromLogLevel(LogLevel logLevel, out BreadcrumbLevelName level)
        {
            switch (logLevel)
            {
                case LogLevel.Error:
                    level = BreadcrumbLevelName.Error;
                    return true;
                case LogLevel.Warning:
                    level = BreadcrumbLevelName.Warning;
                    return true;
                case LogLevel.Info:
                    level = BreadcrumbLevelName.Info;
                    return true;
                case LogLevel.Debug:
                    level = BreadcrumbLevelName.Debug;
                    return true;
                case LogLevel.Verbose:
                    level = BreadcrumbLevelName.Debug;
                    return true;
                default:
                    level = default;
                    return false;
            }
        }

        public static int ToAndroid(BreadcrumbLevelName level)
        {
            switch (level)
            {
                case BreadcrumbLevelName.Debug: return 1;
                case BreadcrumbLevelName.Info: return 2;
                case BreadcrumbLevelName.Warning: return 3;
                case BreadcrumbLevelName.Error: return 4;
                case BreadcrumbLevelName.Fatal: return 5;
                default:
                    throw new System.ArgumentOutOfRangeException(nameof(level), level, "Unknown breadcrumb level.");
            }
        }

        public static int ToIos(BreadcrumbLevelName level)
        {
            switch (level)
            {
                case BreadcrumbLevelName.Debug: return 4;
                case BreadcrumbLevelName.Info: return 3;
                case BreadcrumbLevelName.Warning: return 2;
                case BreadcrumbLevelName.Error: return 1;
                case BreadcrumbLevelName.Fatal: return 1;
                default:
                    throw new System.ArgumentOutOfRangeException(nameof(level), level, "Unknown breadcrumb level.");
            }
        }
    }
}
