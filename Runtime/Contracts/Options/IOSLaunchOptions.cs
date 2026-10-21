using System;

namespace Bugsee.Contracts.Options
{
    /// <summary>
    /// iOS launch options. Shared options live on <see cref="BugseeLaunchOptions"/>;
    /// this type adds iOS-only write-only properties.
    /// </summary>
    /// <remarks>
    /// Every property here is iOS only. The Android SDK does not know these keys, so on Android they are dropped
    /// before launch (<c>OptionPlatformGate</c>) and have no effect. Setting a property to its SDK default has no
    /// visible effect either: iOS drops a host option equal to its default from <c>GetHostLaunchOptions</c>.
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
        /// (every signal). Leave <c>Segv</c> and <c>Bus</c> out only if the runtime you host handles them itself.
        /// </summary>
        public CrashSignals CrashSignals
        {
            set => Set(Options.CaptureCrashSignals, (int)value);
        }

        /// <summary>Symbolicate system frames on the device. Default when omitted: false.</summary>
        public bool OnDeviceSymbolication
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

        /// <summary>Capture AVPlayer videos rendered onto an AVPlayerLayer. Default when omitted: false.</summary>
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
        /// Record the live image of an AVCaptureVideoPreviewLayer. PRIVACY: this puts what the camera sees into bug
        /// reports. It also makes Bugsee take part in your capture session. Default when omitted: false.
        /// </summary>
        public bool CaptureCameraPreview
        {
            set => Set(Options.CaptureCameraPreview, value);
        }

        /// <summary>
        /// Monitor the Bluetooth status (needs <c>NSBluetoothAlwaysUsageDescription</c> on iOS 13+).
        /// Default when omitted: false.
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

        /// <summary>
        /// Show the pending-reports and recording indicator. Only a DEBUG build of the SDK honours it.
        /// Default when omitted: false.
        /// </summary>
        public bool CaptureStatusBarInfo
        {
            set => Set(Options.CaptureStatusBarInfo, value);
        }

        /// <summary>Capture the device and Wi-Fi names, shown on the issue screen. Default when omitted: false.</summary>
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

        /// <summary>Minimum video frame rate in frames per second, not negative. Default when omitted: 0 (SDK's choice).</summary>
        public int CaptureVideoMinFrameRate
        {
            set => Set(Options.CaptureVideoMinFrameRate, NotNegative(value, nameof(CaptureVideoMinFrameRate)));
        }

        /// <summary>Maximum video frame rate in frames per second, not negative. Default when omitted: 0 (SDK's choice).</summary>
        public int CaptureVideoMaxFrameRate
        {
            set => Set(Options.CaptureVideoMaxFrameRate, NotNegative(value, nameof(CaptureVideoMaxFrameRate)));
        }

        // --- Config / UI (iOS) ---

        /// <summary>Encryption of the data the SDK stores. Default when omitted: <c>DataEncryptionMode.None</c>.</summary>
        public DataEncryptionMode DataEncryption
        {
            set => Set(Options.ConfigDataEncryption, (int)value);
        }

        /// <summary>The project target's name, reported with the session. Default when omitted: none.</summary>
        public string BuildTarget
        {
            set => Set(Options.ConfigBuildTarget, value);
        }

        /// <summary>The build type (debug, release, ...), reported with the session. Default when omitted: none.</summary>
        public string BuildType
        {
            set => Set(Options.ConfigBuildType, value);
        }

        /// <summary>The look of the report screen. Default when omitted: <c>ReportingUIStyle.System</c>.</summary>
        public ReportingUIStyle ReportingUIStyle
        {
            set => Set(Options.ReportingUIStyle, (int)value);
        }

        static int NotNegative(int value, string name)
        {
            if (value < 0) throw new ArgumentOutOfRangeException(name, value, "The iOS SDK takes an unsigned value.");
            return value;
        }
    }
}
