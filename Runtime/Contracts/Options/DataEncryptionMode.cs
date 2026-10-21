namespace Bugsee.Contracts.Options
{
    /// <summary>
    /// How the iOS SDK encrypts the data it collects. iOS only. The values are the iOS SDK's
    /// <c>BugseeDataEncryptionMode</c>.
    /// </summary>
    public enum DataEncryptionMode
    {
        /// <summary>No encryption. Collected data is stored as is. The SDK default.</summary>
        None = 0,

        /// <summary>Encryption keys are stored on the device in the Secure Enclave, so they cannot be extracted.</summary>
        OnDevice = 1,

        /// <summary>
        /// Data is encrypted at rest and in transit with a one-time key that exists only in memory during
        /// recording and is never stored on the device. Only the server can decrypt it, so some functionality
        /// (for example the video preview) may be missing.
        /// </summary>
        EndToEnd = 2
    }
}
