namespace Bugsee.Contracts.Options
{
    /// <summary>
    /// The look of the iOS report screen. iOS only. The values are the iOS SDK's <c>BugseeStyle</c>.
    /// </summary>
    public enum ReportingUIStyle
    {
        /// <summary>Light.</summary>
        Light = 0,

        /// <summary>Dark ("dusk").</summary>
        Dusk = 1,

        /// <summary>Follows the status bar style.</summary>
        BasedOnStatusBar = 2,

        /// <summary>Follows the system appearance. The SDK default.</summary>
        System = 3
    }
}
