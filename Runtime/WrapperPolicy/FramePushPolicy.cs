using System;
using Bugsee.Contracts.Capture;

namespace Bugsee.WrapperPolicy
{
    public enum FramePushDecision
    {
        /// <summary>Start a capture now.</summary>
        Request,
        /// <summary>Too soon after the previous request for the configured frame rate.</summary>
        Throttled,
        /// <summary>A readback (or the push of its pixels) is still in flight: this frame is dropped, not queued.</summary>
        InFlight,
        /// <summary>The SDK is blacked out or not launched: nothing is captured.</summary>
        Blackout,
        /// <summary>Capture turned itself off after repeated readback failures. It never falls back to a synchronous read.</summary>
        Disabled
    }

    /// <summary>
    /// When the experimental Unity frame pusher (BugseeFrameCapturer) may start a capture, plus the size and pixel
    /// format rules it needs. Pure logic (no Unity types) so it can be unit-tested. Thread-safe: the main thread
    /// begins a request and the push thread completes it.
    /// </summary>
    public sealed class FramePushPolicy
    {
        public const int MinFps = 1;
        public const int MaxFps = 30;
        public const int DefaultFps = 5;

        /// <summary>RGBA32 is the only format the pusher requests; the SDK reads it with a pixel stride of 4.</summary>
        public const int PixelStride = 4;

        readonly object _gate = new object();
        readonly double _intervalSeconds;
        readonly int _maxInFlight;
        readonly int _maxConsecutiveFailures;

        double _nextDueSeconds = double.NegativeInfinity;
        int _inFlight;
        int _consecutiveFailures;
        bool _disabled;
        long _requested, _pushed, _failed, _droppedInFlight, _droppedBlackout;

        public FramePushPolicy(int targetFps = DefaultFps, int maxInFlight = 1, int maxConsecutiveFailures = 5)
        {
            int fps = Math.Min(MaxFps, Math.Max(MinFps, targetFps));
            _intervalSeconds = 1.0 / fps;
            _maxInFlight = Math.Max(1, maxInFlight);
            _maxConsecutiveFailures = Math.Max(1, maxConsecutiveFailures);
        }

        public bool HasInFlight { get { lock (_gate) return _inFlight > 0; } }
        public bool IsDisabled { get { lock (_gate) return _disabled; } }

        /// <summary>Cheap pre-check (no side effects): is the frame-rate interval over? Lets the caller skip JNI work on most game frames.</summary>
        public bool IsDue(double nowSeconds)
        {
            lock (_gate) return !_disabled && nowSeconds >= _nextDueSeconds;
        }

        /// <summary>Decide for this frame and, on <see cref="FramePushDecision.Request"/>, record the request as in flight.</summary>
        public FramePushDecision TryBegin(double nowSeconds, bool blackout)
        {
            lock (_gate)
            {
                if (_disabled) return FramePushDecision.Disabled;
                if (nowSeconds < _nextDueSeconds) return FramePushDecision.Throttled;
                if (_inFlight >= _maxInFlight)
                {
                    _droppedInFlight++;
                    return FramePushDecision.InFlight;
                }
                if (blackout)
                {
                    _droppedBlackout++;
                    return FramePushDecision.Blackout;
                }
                _inFlight++;
                _requested++;
                _nextDueSeconds = nowSeconds + _intervalSeconds;
                return FramePushDecision.Request;
            }
        }

        /// <summary>A started request finished: pushed (true) or failed/dropped after starting (false).</summary>
        public void Complete(bool pushed)
        {
            lock (_gate)
            {
                if (_inFlight > 0) _inFlight--;
                if (pushed)
                {
                    _pushed++;
                    _consecutiveFailures = 0;
                    return;
                }
                _failed++;
                if (++_consecutiveFailures >= _maxConsecutiveFailures) _disabled = true;
            }
        }

        public void Snapshot(out long requested, out long pushed, out long failed, out long droppedInFlight, out long droppedBlackout)
        {
            lock (_gate)
            {
                requested = _requested;
                pushed = _pushed;
                failed = _failed;
                droppedInFlight = _droppedInFlight;
                droppedBlackout = _droppedBlackout;
            }
        }

        /// <summary>Capture size: the screen scaled down so its longer side is at most <paramref name="maxDimension"/>, never up, even and at least 2.</summary>
        public static void ComputeCaptureSize(int screenWidth, int screenHeight, int maxDimension, out int width, out int height)
        {
            int sw = Math.Max(2, screenWidth);
            int sh = Math.Max(2, screenHeight);
            int cap = Math.Max(2, maxDimension);
            double scale = Math.Min(1.0, (double)cap / Math.Max(sw, sh));
            width = Even((int)Math.Round(sw * scale));
            height = Even((int)Math.Round(sh * scale));
        }

        static int Even(int v) => Math.Max(2, v & ~1);

        /// <summary>Bytes the SDK expects for a pushed frame (width * height * stride); it drops a buffer smaller than this.</summary>
        public static long FrameBytes(int width, int height) => (long)width * height * PixelStride;

        /// <summary>
        /// True when GPU readback may be used. Unity on Android with Vulkan crashed in AsyncGPUReadback from 2020.3
        /// through 2021.3.17 (fixed in 2021.3.18f1), so Vulkan needs 2021.3.18+ (or any later major). Unparsable
        /// versions are rejected on Vulkan. The pusher never falls back to a synchronous ReadPixels.
        /// </summary>
        public static bool IsReadbackUsable(bool supportsAsyncReadback, bool isVulkan, string unityVersion)
        {
            if (!supportsAsyncReadback) return false;
            if (!isVulkan) return true;
            return TryParseUnityVersion(unityVersion, out int major, out int minor, out int patch)
                && (major > 2021 || (major == 2021 && (minor > 3 || (minor == 3 && patch >= 18))));
        }

        /// <summary>Parses "2021.3.45f1" (and "6000.0.1b2") into major, minor, patch.</summary>
        public static bool TryParseUnityVersion(string version, out int major, out int minor, out int patch)
        {
            major = minor = patch = 0;
            if (string.IsNullOrEmpty(version)) return false;
            string[] parts = version.Split('.');
            if (parts.Length < 3) return false;
            int end = 0;
            while (end < parts[2].Length && char.IsDigit(parts[2][end])) end++;
            return int.TryParse(parts[0], out major)
                && int.TryParse(parts[1], out minor)
                && end > 0
                && int.TryParse(parts[2].Substring(0, end), out patch);
        }

        /// <summary>
        /// Row order of the readback buffer for a graphics API, or false when it was never measured. Measured
        /// empirically with a known pattern (top red, bottom blue) through the whole capture path; see the tests.
        /// </summary>
        public static bool TryMeasuredRowOrder(string graphicsApi, out VideoFrameRowOrder order)
        {
            order = VideoFrameRowOrder.BottomUp;
            switch (graphicsApi)
            {
                // Measured on an Android 16 arm64 emulator, Unity 2021.3.45: the first buffer row was the screen's bottom.
                case "OpenGLES3": order = VideoFrameRowOrder.BottomUp; return true;
                default: return false;
            }
        }

        /// <summary>
        /// Row order to push with, and whether it is verified. An explicit override counts as verified (the host
        /// declared it). An API without a measurement gets a guess and verified=false: the caller must not rely on
        /// it for masking and blanks the whole frame instead when a secure rectangle is registered.
        /// </summary>
        public static VideoFrameRowOrder ResolveRowOrder(string graphicsApi, VideoFrameRowOrder? overrideOrder, out bool verified)
        {
            if (overrideOrder.HasValue) { verified = true; return overrideOrder.Value; }
            if (TryMeasuredRowOrder(graphicsApi, out VideoFrameRowOrder measured)) { verified = true; return measured; }
            verified = false;
            return VideoFrameRowOrder.TopDown;
        }
    }
}
