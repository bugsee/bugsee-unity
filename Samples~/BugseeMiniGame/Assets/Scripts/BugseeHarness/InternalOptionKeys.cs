namespace Bugsee.Sample
{
    /// <summary>
    /// SDK-internal launch option keys the native option manifests mark hidden (bugsee-unity#108). The package has no typed
    /// member for them; the sample and the e2e builds still need them (endpoint override for staging, SDK debug logging), so
    /// they set the raw keys through <c>SetCustomOption</c>. <c>$$DEBUG</c> exists on Android only.
    /// </summary>
    static class InternalOptionKeys
    {
        public const string Debug = "com.bugsee.option.$$DEBUG";
        public const string Endpoint = "com.bugsee.option.$$ENDPOINT";
    }
}
