namespace Bugsee.Contracts.Lifecycle
{
    /// <summary>
    /// How much <c>Bugsee.DeleteCollectedDataOnDevice</c> deletes. Each value maps to one call of the native SDK
    /// on both platforms; the default of the facade is <see cref="Everything"/>.
    /// </summary>
    public enum DeleteScope
    {
        /// <summary>
        /// The recorded capture only. Keeps the report snapshots and the report bundles waiting for upload
        /// (Android <c>includingIntermediate=false, includingBundles=false</c>, iOS <c>includingIntermediate:NO</c>).
        /// </summary>
        CaptureOnly,

        /// <summary>
        /// The recorded capture and the report snapshots (the data set aside for a report that is not bundled yet).
        /// Keeps the report bundles waiting for upload (Android <c>true, false</c>, iOS <c>includingIntermediate:YES</c>).
        /// </summary>
        CaptureAndIntermediate,

        /// <summary>
        /// Everything the SDK collected on disk, including the report bundles not uploaded yet (their uploads are
        /// cancelled). Android <c>true, true</c>. iOS also deletes the crashes not reported yet and the performance
        /// baselines anomaly detection has learned. The default.
        /// </summary>
        Everything
    }
}
