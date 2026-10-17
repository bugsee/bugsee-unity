namespace Bugsee.Contracts.Options
{
    /// <summary>
    /// How the iOS crash reporter intercepts crashes. iOS only: Android has no Mach exceptions,
    /// and tvOS runs every value as <c>BSD</c>. The values are the iOS SDK's
    /// <c>BugseeCrashHandlerType</c>.
    /// </summary>
    public enum CrashHandlerType
    {
        /// <summary>BSD signal handlers.</summary>
        BSD = 1,

        /// <summary>
        /// A Mach exception handler, plus a BSD signal handler for SIGABRT. Other signals raised
        /// by software (<c>raise()</c>, <c>kill()</c>) are not reported.
        /// </summary>
        Mach = 2,

        /// <summary>
        /// Both, so hardware faults and signals raised by software are reported. The SDK default.
        /// </summary>
        MachAndBSD = 3
    }
}
