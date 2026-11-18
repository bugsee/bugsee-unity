using System;

namespace Bugsee.Contracts.Options
{
    /// <summary>
    /// The fatal signals the native crash reporter intercepts, one bit per signal; combine them to choose which.
    /// Both platforms: the values are the iOS SDK's <c>BugseeCrashSignals</c> and, identically, the constants of the
    /// Android SDK's <c>CrashSignals</c> class (<c>bugsee-android-ndk</c>, checked in 7.6.0). On Android the key is
    /// known only once the NDK extension is registered. A value is read slightly differently by the two SDKs:
    /// <see cref="Default"/> means every signal on both, but an Android value that is not <see cref="Default"/> and
    /// selects none of the six signals (for example <c>64</c>) is also taken as every signal, while the iOS header says any other value
    /// is used as given; <see cref="None"/> (0) selects no signal on both. Bits above <see cref="Trap"/> other than
    /// <see cref="Default"/> are ignored on Android.
    /// Leave a signal out when the runtime your app hosts uses it for ordinary control flow. A bad memory access
    /// reaches the crash reporter as one Mach exception that the kernel turns into <c>SIGSEGV</c> or <c>SIGBUS</c>,
    /// so it is intercepted if either of the two is selected (iOS). Leaving out <see cref="Abrt"/> also stops uncaught
    /// Objective-C exceptions, which end in <c>abort()</c>, from being reported (iOS).
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
