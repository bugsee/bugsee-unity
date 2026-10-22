using System;

namespace Bugsee.Contracts.Options
{
    /// <summary>
    /// The fatal signals the iOS crash reporter intercepts, one bit per signal; combine them to choose which.
    /// iOS only. The values are the iOS SDK's <c>BugseeCrashSignals</c>.
    /// Leave a signal out when the runtime your app hosts uses it for ordinary control flow. A bad memory access
    /// reaches the crash reporter as one Mach exception that the kernel turns into <c>SIGSEGV</c> or <c>SIGBUS</c>,
    /// so it is intercepted if either of the two is selected. Leaving out <see cref="Abrt"/> also stops uncaught
    /// Objective-C exceptions, which end in <c>abort()</c>, from being reported.
    /// </summary>
    [Flags]
    public enum CrashSignals
    {
        /// <summary>No signals.</summary>
        None = 0,

        /// <summary>SIGABRT: abort(), failed assertions, uncaught exceptions.</summary>
        Abrt = 1 << 0,

        /// <summary>SIGSEGV: invalid memory access.</summary>
        Segv = 1 << 1,

        /// <summary>SIGBUS: misaligned or otherwise invalid memory access.</summary>
        Bus = 1 << 2,

        /// <summary>SIGILL: illegal instruction.</summary>
        Ill = 1 << 3,

        /// <summary>SIGFPE: arithmetic exception.</summary>
        Fpe = 1 << 4,

        /// <summary>SIGTRAP: breakpoint or trap instruction, including Swift runtime traps.</summary>
        Trap = 1 << 5,

        /// <summary>Every signal.</summary>
        All = Abrt | Segv | Bus | Ill | Fpe | Trap,

        /// <summary>
        /// Let the SDK choose: every signal. The SDK default. Only honoured on its own: combined with other
        /// bits, the value is used as given and this bit means nothing.
        /// </summary>
        Default = 1 << 7
    }
}
