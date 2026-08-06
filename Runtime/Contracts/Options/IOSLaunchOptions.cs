namespace Bugsee.Contracts.Options
{
    /// <summary>
    /// iOS launch options. Shared options live on <see cref="BugseeLaunchOptions"/>.
    /// Platform-specific properties will be added when the iOS SDK reaches RC/stable
    /// and option naming is unified.
    /// </summary>
    public sealed class IOSLaunchOptions : BugseeLaunchOptions
    {
        public IOSLaunchOptions() { }
    }
}
