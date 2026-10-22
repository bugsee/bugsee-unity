namespace Bugsee.Contracts.Options
{
    /// <summary>
    /// iOS launch options. Shared options live on <see cref="BugseeLaunchOptions"/>;
    /// this type adds iOS-only write-only properties.
    /// </summary>
    /// <remarks>
    /// Every property here is iOS only. The Android SDK does not know these keys, so on Android they are dropped
    /// before launch (<c>OptionPlatformGate</c>) and have no effect. Setting a property to its SDK default has no
    /// visible effect either: iOS drops a host option equal to its default from the host launch options (read the effective
    /// value with <c>GetLaunchOptions</c>). <c>capture.logs.oslog</c> (permanently disabled), <c>capture.status-bar-info</c>
    /// (read only by a DEBUG build of the SDK, never the published one) and <c>capture.mach-exceptions</c> (removed from the
    /// SDK, which ignores it with a warning; use <see cref="CrashHandlerType"/>) have no property.
    /// </remarks>
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

        /// <summary>
        /// The fatal signals the crash reporter intercepts. Default when omitted: <c>CrashSignals.Default</c>
        /// (every signal; the SDK drops <c>Segv</c>/<c>Bus</c> only for its Xamarin/.NET wrappers, not for Unity). Keep
        /// <c>Segv</c> and <c>Bus</c>: Unity's IL2CPP raises <c>NullReferenceException</c> with explicit null checks, not
        /// from a signal, so managed null references never reach the crash reporter, while a real fault would go unreported
        /// without them.
        /// </summary>
        public CrashSignals CrashSignals
        {
            set => Set(Options.CaptureCrashSignals, (int)value);
        }

        /// <summary>Symbolicate system frames on the device. Default when omitted: false.</summary>
        public bool CaptureOnDeviceSymbolication
        {
            set => Set(Options.CaptureOnDeviceSymbolication, value);
        }

        /// <summary>
        /// Stitch Swift async stacks into C++ throw stacks (iOS 15+). Default when omitted: true.
        /// </summary>
        public bool CrashSwiftAsyncStackTraces
        {
            set => Set(Options.DetectCrashSwiftAsyncStackTraces, value);
        }

        /// <summary>
        /// Fill the stack of an uncaught NSException that has none, from the handler's own thread (it can change crash
        /// grouping). Default when omitted: false.
        /// </summary>
        public bool CrashFillEmptyExceptionStacks
        {
            set => Set(Options.DetectCrashFillEmptyExceptionStacks, value);
        }

        /// <summary>Capture WebSocket traffic. Default when omitted: true.</summary>
        public bool CaptureWebSocket
        {
            set => Set(Options.CaptureWebSocket, value);
        }

        /// <summary>Capture AVPlayer videos rendered onto a native AVPlayerLayer (not Unity's VideoPlayer). Default when omitted: false.</summary>
        public bool CaptureAVPlayer
        {
            set => Set(Options.CaptureAVPlayer, value);
        }

        /// <summary>
        /// Capture video rendered onto an AVSampleBufferDisplayLayer (WebRTC, custom players). Default when omitted: false.
        /// </summary>
        public bool CaptureSampleBufferDisplayLayer
        {
            set => Set(Options.CaptureSampleBufferDisplayLayer, value);
        }

        /// <summary>
        /// Record the live image of a native AVCaptureVideoPreviewLayer (not Unity's WebCamTexture). PRIVACY: this puts what the camera sees into bug
        /// reports. It also makes Bugsee take part in your capture session. Default when omitted: false.
        /// </summary>
        public bool CaptureCameraPreview
        {
            set => Set(Options.CaptureCameraPreview, value);
        }

        /// <summary>
        /// Report the Bluetooth status. The SDK only reads a status the app itself asked for and got permission for
        /// (it never asks, and never creates a Bluetooth manager otherwise): without that it reports unknown or
        /// unauthorized. Default when omitted: false.
        /// </summary>
        public bool CaptureBluetoothStatus
        {
            set => Set(Options.CaptureBluetoothStatus, value);
        }

        /// <summary>
        /// Report free and total disk space. Apple's declared reason for the API forbids sending it off the device,
        /// so the SDK leaves it out of reports unless this is set; use it in development builds. Default when omitted: false.
        /// </summary>
        public bool CaptureDiskSpace
        {
            set => Set(Options.CaptureDiskSpace, value);
        }

        /// <summary>Send the device name and Wi-Fi network name (personal data; without it the SDK sends a redacted value).
        /// The Wi-Fi name also needs the Apple entitlement and location permission. Default when omitted: false.</summary>
        public bool CaptureDeviceNetworkNames
        {
            set => Set(Options.CaptureDeviceNetworkNames, value);
        }

        // --- Video (iOS) ---

        /// <summary>
        /// Blur transition frames in the recorded video. Off, the video freezes for the transition instead; it never
        /// records the transition unprotected. Default when omitted: true.
        /// </summary>
        public bool CaptureVideoPrivacyBlur
        {
            set => Set(Options.CaptureVideoPrivacyBlur, value);
        }

        /// <summary>Minimum video frame rate. The SDK clamps to 0..30, needs a maximum at or above it (with none the minimum is dropped) and derives 0 from the frame rate. Default when omitted: 0.</summary>
        public int CaptureVideoMinFrameRate
        {
            set => Set(Options.CaptureVideoMinFrameRate, value);
        }

        /// <summary>Maximum video frame rate. The SDK clamps to 0..30; 0 is derived from the frame rate. Default when omitted: 0.</summary>
        public int CaptureVideoMaxFrameRate
        {
            set => Set(Options.CaptureVideoMaxFrameRate, value);
        }

        // --- Config / UI (iOS) ---

        /// <summary>Encryption of the data the SDK stores. Default when omitted: <c>DataEncryptionMode.None</c>.</summary>
        public DataEncryptionMode DataEncryption
        {
            set => Set(Options.DataEncryption, (int)value);
        }

        /// <summary>The project target's name, reported with the session. Default when omitted: none.</summary>
        public string BuildTarget
        {
            set => Set(Options.BuildTarget, value);
        }

        /// <summary>The build type (debug, release, ...), reported with the session. Default when omitted: none.</summary>
        public string BuildType
        {
            set => Set(Options.BuildType, value);
        }

        /// <summary>The look of the report screen. Default when omitted: <c>ReportingUIStyle.System</c>.</summary>
        public ReportingUIStyle ReportingUIStyle
        {
            set => Set(Options.ReportingUIStyle, (int)value);
        }
    }
}
