namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Diagnostic for the sample's device checks: when <see cref="Enabled"/>, the platform bridge records what its last
    /// <c>Trace</c> call handed to the native SDK (iOS: the JSON string passed to the bridge; Android: the boxed Java
    /// value's class and text, <c>java.lang.String:...</c>). The SDKs cannot be read back from C#. Off by default.
    /// </summary>
    public static class TraceProbe
    {
        public static bool Enabled;
        public static string Last;
    }
}
