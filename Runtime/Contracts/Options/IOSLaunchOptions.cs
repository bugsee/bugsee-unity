namespace Bugsee.Contracts.Options
{
    /// <summary>
    /// iOS launch options. Shared options live on <see cref="BugseeLaunchOptions"/>;
    /// this type adds iOS-only write-only properties.
    /// </summary>
    public sealed class IOSLaunchOptions : BugseeLaunchOptions
    {
        public IOSLaunchOptions() { }

        // --- Capture (iOS) ---

        /// <summary>
        /// How the crash reporter intercepts crashes. Default when omitted:
        /// <c>CrashHandlerType.MachAndBSD</c>.
        /// </summary>
        public CrashHandlerType CrashHandlerType
        {
            set => Set(Options.CaptureCrashHandlerType, (int)value);
        }
    }
}
