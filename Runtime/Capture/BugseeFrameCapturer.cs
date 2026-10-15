using System;
using System.Collections.Generic;
using Bugsee.Contracts.Capture;
using Bugsee.Contracts.Options;
using Bugsee.WrapperPolicy;
using UnityEngine;

#if UNITY_ANDROID && !UNITY_EDITOR && BUGSEE_HAS_SCREENCAPTURE
using System.Threading;
using Unity.Collections;
using UnityEngine.Rendering;
#endif

namespace Bugsee.Capture
{
    /// <summary>Settings for the experimental Unity frame pusher.</summary>
    public sealed class BugseeFrameCapturerOptions
    {
        /// <summary>Frames per second to capture, 1..30. The SDK also caps pushed frames by its own FrameRate option.</summary>
        public int TargetFps = FramePushPolicy.DefaultFps;

        /// <summary>Longer side of the captured frame in pixels. The screen is scaled down to this, never up.</summary>
        public int MaxDimension = 640;

        /// <summary>
        /// Row order of the readback buffer. Null picks it from the graphics API (see FramePushPolicy.RowOrderFor).
        /// Set it if recorded video comes out upside down.
        /// </summary>
        public VideoFrameRowOrder? RowOrder;
    }

    /// <summary>Counters of the experimental frame pusher.</summary>
    public struct BugseeFrameCapturerStats
    {
        public long Requested, Pushed, Failed, DroppedInFlight, DroppedBlackoutOrStopped;
    }

    /// <summary>
    /// EXPERIMENTAL, Android only, off unless <see cref="Enable"/> is called. Captures Unity's final composite
    /// (ScreenCapture + AsyncGPUReadback, screen-space overlay UI included, native Android views not) and pushes it to
    /// the SDK through VideoFrameConsumer.addFrame. Needs the SDK launched with VideoMode.DirectBuffers; in any other
    /// mode the SDK ignores the frames. Not benchmarked on devices.
    ///
    /// One request is in flight at a time and frames that arrive meanwhile are dropped. Buffers are reused. Readback
    /// failures turn the pusher off; it never falls back to a synchronous read. Secure rectangles registered with
    /// Bugsee.AddSecureRectangle are blanked here, because the SDK does not mask pushed frames (blackout it does honour).
    /// </summary>
    public sealed class BugseeFrameCapturer : MonoBehaviour
    {
        static BugseeFrameCapturer _instance;

        /// <summary>True once <see cref="Enable"/> started the pusher on this platform and it has not been disabled.</summary>
        public static bool IsEnabled => _instance != null && _instance._enabled;

        public static BugseeFrameCapturerStats Stats =>
            _instance != null ? _instance.ReadStats() : default;

        /// <summary>
        /// Starts the pusher. Returns false (and does nothing) off Android, in the Editor, or when the device cannot do
        /// GPU readback. Call after or around Bugsee.Launch with VideoMode.DirectBuffers; frames are skipped until the
        /// SDK is launched. Main thread only.
        /// </summary>
        public static bool Enable(BugseeFrameCapturerOptions options = null)
        {
#if UNITY_ANDROID && !UNITY_EDITOR && BUGSEE_HAS_SCREENCAPTURE
            if (!Internal.MainThreadDispatcher.IsMainThread)
            {
                Internal.MainThreadDispatcher.Run(() => Enable(options));
                return true;
            }
            if (_instance != null && _instance._enabled) return true;

            bool vulkan = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Vulkan;
            if (!FramePushPolicy.IsReadbackUsable(SystemInfo.supportsAsyncGPUReadback, vulkan, Application.unityVersion))
            {
                Debug.LogWarning("[BugseeFrameCapturer] GPU readback is not usable here (unsupported, or Vulkan on Unity older than 2021.3.18); not enabled.");
                return false;
            }

            var go = new GameObject("Bugsee.FrameCapturer") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            var capturer = go.AddComponent<BugseeFrameCapturer>();
            if (!capturer.Begin(options ?? new BugseeFrameCapturerOptions()))
            {
                Destroy(go);
                return false;
            }
            _instance = capturer;
            return true;
#else
            Debug.LogWarning("[BugseeFrameCapturer] Android player builds with the Screen Capture module only; not enabled.");
            return false;
#endif
        }

        /// <summary>Stops the pusher and releases its buffers once any push in flight has finished.</summary>
        public static void Disable()
        {
            if (_instance == null) return;
            _instance.Stop();
        }

        bool _enabled;
        FramePushPolicy _policy;

        BugseeFrameCapturerStats ReadStats()
        {
            if (_policy == null) return default;
            _policy.Snapshot(out long req, out long pushed, out long failed, out long inFlight, out long blackout);
            return new BugseeFrameCapturerStats
            {
                Requested = req, Pushed = pushed, Failed = failed,
                DroppedInFlight = inFlight, DroppedBlackoutOrStopped = blackout
            };
        }

#if UNITY_ANDROID && !UNITY_EDITOR && BUGSEE_HAS_SCREENCAPTURE
        const string ConsumerClass = "com/bugsee/library/contracts/capture/consumers/VideoFrameConsumer";
        const string RowOrderClass = ConsumerClass + "$RowOrder";
        const string AddFrameSignature = "(JILjava/nio/ByteBuffer;IIIL" + RowOrderClass + ";)V";
        const float StatsLogIntervalSeconds = 10f;

        static readonly WaitForEndOfFrame EndOfFrame = new WaitForEndOfFrame();

        BugseeFrameCapturerOptions _options;
        VideoFrameRowOrder _rowOrder;
        Action<AsyncGPUReadbackRequest> _onReadback;

        RenderTexture _fullRt, _smallRt;
        int _captureW, _captureH;
        long _requestTimestampMs;

        // Pixels: the readback callback (main thread) fills _pixels, the push thread copies them into the Java
        // direct buffer and calls addFrame. Unity's AndroidJNI cannot wrap or address native memory as a ByteBuffer,
        // so the copy goes through a Java byte[] (one Java-heap array per pushed frame, no managed allocation). Only one request is in flight, so neither side overwrites the other.
        byte[] _pixels;
        IntPtr _directBuffer;      // global ref to a reused direct java.nio.ByteBuffer
        IntPtr _bufferClear, _bufferRewind, _bufferPut; // method ids on java.nio.Buffer / ByteBuffer
        IntPtr _consumer;          // global ref to the VideoFrameConsumer
        IntPtr _addFrameMethod;
        IntPtr _topDown, _bottomUp; // global refs to the RowOrder constants
        readonly jvalue[] _args = new jvalue[7];
        readonly jvalue[] _putArgs = new jvalue[1];
        readonly jvalue[] _noArgs = new jvalue[0];
        bool _wasLaunched;
        bool _warnedMode;
        bool _paused;
        bool _releaseDone;
        float _nextStatsLog;

        Thread _worker;
        readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        volatile bool _stopWorker;
        int _pushW, _pushH;
        VideoFrameRowOrder _pushOrder;

        bool Begin(BugseeFrameCapturerOptions options)
        {
            _options = options;
            _policy = new FramePushPolicy(options.TargetFps);
            _rowOrder = FramePushPolicy.RowOrderFor(SystemInfo.graphicsUVStartsAtTop, options.RowOrder);
            _onReadback = OnReadback;
            try
            {
                _topDown = RowOrderConstant("TOP_DOWN");
                _bottomUp = RowOrderConstant("BOTTOM_UP");
                IntPtr cls = AndroidJNI.FindClass(ConsumerClass);
                _addFrameMethod = AndroidJNI.GetMethodID(cls, "addFrame", AddFrameSignature);
                AndroidJNI.DeleteLocalRef(cls);
                IntPtr bufferCls = AndroidJNI.FindClass("java/nio/Buffer");
                _bufferClear = AndroidJNI.GetMethodID(bufferCls, "clear", "()Ljava/nio/Buffer;");
                _bufferRewind = AndroidJNI.GetMethodID(bufferCls, "rewind", "()Ljava/nio/Buffer;");
                AndroidJNI.DeleteLocalRef(bufferCls);
                IntPtr byteBufferCls = AndroidJNI.FindClass("java/nio/ByteBuffer");
                _bufferPut = AndroidJNI.GetMethodID(byteBufferCls, "put", "([B)Ljava/nio/ByteBuffer;");
                AndroidJNI.DeleteLocalRef(byteBufferCls);
                if (_bufferClear == IntPtr.Zero || _bufferRewind == IntPtr.Zero || _bufferPut == IntPtr.Zero
                    || _addFrameMethod == IntPtr.Zero || _topDown == IntPtr.Zero || _bottomUp == IntPtr.Zero)
                {
                    AndroidJNI.ExceptionClear();
                    Debug.LogWarning("[BugseeFrameCapturer] VideoFrameConsumer.addFrame not found; this needs Bugsee Android 7.4.1 or newer.");
                    ReleaseNative();
                    return false;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[BugseeFrameCapturer] JNI setup failed: " + ex.Message);
                ReleaseNative();
                return false;
            }

            _enabled = true;
            _worker = new Thread(PushLoop) { IsBackground = true, Name = "Bugsee.FramePush" };
            _worker.Start();
            StartCoroutine(CaptureLoop());
            return true;
        }

        static IntPtr RowOrderConstant(string name)
        {
            IntPtr cls = AndroidJNI.FindClass(RowOrderClass);
            IntPtr field = AndroidJNI.GetStaticFieldID(cls, name, "L" + RowOrderClass + ";");
            IntPtr local = AndroidJNI.GetStaticObjectField(cls, field);
            IntPtr global = local == IntPtr.Zero ? IntPtr.Zero : AndroidJNI.NewGlobalRef(local);
            AndroidJNI.DeleteLocalRef(local);
            AndroidJNI.DeleteLocalRef(cls);
            return global;
        }

        void Stop()
        {
            _enabled = false;
            StopAllCoroutines();
        }

        void OnApplicationPause(bool paused) => _paused = paused;

        // Releases buffers once disabled and nothing is in flight. Runs on the main thread (RenderTexture.Release needs it).
        void Update()
        {
            if (_enabled)
            {
                if (Time.unscaledTime >= _nextStatsLog)
                {
                    _nextStatsLog = Time.unscaledTime + StatsLogIntervalSeconds;
                    var s = ReadStats();
                    Debug.Log("[BugseeFrameCapturer] requested=" + s.Requested + " pushed=" + s.Pushed + " failed=" + s.Failed
                        + " droppedInFlight=" + s.DroppedInFlight + " droppedBlackoutOrStopped=" + s.DroppedBlackoutOrStopped
                        + " size=" + _captureW + "x" + _captureH + " rowOrder=" + _rowOrder);
                }
                if (_policy.IsDisabled) _enabled = false;
                return;
            }
            if (_releaseDone || _policy.HasInFlight) return;
            _releaseDone = true;
            _stopWorker = true;
            _signal.Release();
            ReleaseRenderTextures();
            ReleaseNative();
            if (_instance == this) _instance = null;
            Destroy(gameObject);
        }

        System.Collections.IEnumerator CaptureLoop()
        {
            while (_enabled)
            {
                yield return EndOfFrame;
                if (!_enabled) break;
                try
                {
                    Tick();
                }
                catch (Exception ex)
                {
                    // An exception would end the coroutine and silently stop capture.
                    Debug.LogWarning("[BugseeFrameCapturer] tick failed: " + ex.Message);
                }
            }
        }

        void Tick()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (_paused || !_policy.IsDue(now)) return;

            // JNI calls only once per due frame, not on every game frame.
            bool launched = Bugsee.IsLaunched;
            bool stopped = !launched || Bugsee.IsBlackout;
            if (launched && !_wasLaunched && !_policy.HasInFlight) RefreshConsumer();
            _wasLaunched = launched;
            stopped |= _consumer == IntPtr.Zero;

            if (_policy.TryBegin(now, stopped) != FramePushDecision.Request) return;

            try
            {
                if (!EnsureBuffers()) { _policy.Complete(false); return; }
                _requestTimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                ScreenCapture.CaptureScreenshotIntoRenderTexture(_fullRt);
                Graphics.Blit(_fullRt, _smallRt);
                AsyncGPUReadback.Request(_smallRt, 0, TextureFormat.RGBA32, _onReadback);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[BugseeFrameCapturer] capture request failed: " + ex.Message);
                _policy.Complete(false);
            }
        }

        void RefreshConsumer()
        {
            if (_consumer != IntPtr.Zero) { AndroidJNI.DeleteGlobalRef(_consumer); _consumer = IntPtr.Zero; }
            using (var bugsee = new AndroidJavaClass("com.bugsee.library.Bugsee"))
            using (var consumer = bugsee.CallStatic<AndroidJavaObject>("getVideoFrameConsumer"))
            {
                if (consumer != null) _consumer = AndroidJNI.NewGlobalRef(consumer.GetRawObject());
            }
            if (_warnedMode) return;
            _warnedMode = true;
            IReadOnlyDictionary<string, object> launch = Bugsee.GetLaunchOptions();
            // The SDK reports option values as strings.
            if (launch != null && launch.TryGetValue(Options.CaptureVideoMode, out object mode) && mode != null
                && int.TryParse(Convert.ToString(mode, System.Globalization.CultureInfo.InvariantCulture), out int modeValue)
                && VideoModeExtensions.FromIntValue(modeValue) != VideoMode.DirectBuffers)
                Debug.LogWarning("[BugseeFrameCapturer] the SDK is not launched with VideoMode.DirectBuffers; pushed frames are ignored.");
        }

        bool EnsureBuffers()
        {
            FramePushPolicy.ComputeCaptureSize(Screen.width, Screen.height, _options.MaxDimension, out int w, out int h);
            bool fullStale = _fullRt == null || _fullRt.width != Screen.width || _fullRt.height != Screen.height;
            if (!fullStale && w == _captureW && h == _captureH && _smallRt != null && _pixels != null) return true;

            // Nothing is in flight here (this request is the only one), so the push thread is idle.
            ReleaseRenderTextures();
            _fullRt = new RenderTexture(Mathf.Max(2, Screen.width), Mathf.Max(2, Screen.height), 0, RenderTextureFormat.ARGB32);
            _smallRt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
            _fullRt.Create();
            _smallRt.Create();
            _captureW = w;
            _captureH = h;

            int bytes = (int)FramePushPolicy.FrameBytes(w, h);
            _pixels = new byte[bytes];
            ReleaseDirectBuffer();
            IntPtr cls = AndroidJNI.FindClass("java/nio/ByteBuffer");
            IntPtr alloc = AndroidJNI.GetStaticMethodID(cls, "allocateDirect", "(I)Ljava/nio/ByteBuffer;");
            var arg = new jvalue[1];
            arg[0].i = bytes;
            IntPtr local = AndroidJNI.CallStaticObjectMethod(cls, alloc, arg);
            AndroidJNI.DeleteLocalRef(cls);
            if (local == IntPtr.Zero) { AndroidJNI.ExceptionClear(); return false; }
            _directBuffer = AndroidJNI.NewGlobalRef(local);
            AndroidJNI.DeleteLocalRef(local);
            return true;
        }

        void OnReadback(AsyncGPUReadbackRequest request)
        {
            if (!_enabled || request.hasError || _pixels == null)
            {
                _policy.Complete(false);
                return;
            }
            NativeArray<byte> data = request.GetData<byte>();
            if (data.Length < _pixels.Length) { _policy.Complete(false); return; }
            NativeArray<byte>.Copy(data, _pixels, _pixels.Length);

            // The SDK does not mask pushed frames: blank the registered secure rectangles first.
            int[] rects = SecureRectRegistry.Instance.Snapshot(0, 1f);
            FrameRedaction.BlankRects(_pixels, _captureW, _captureH, _rowOrder, rects, Screen.width, Screen.height);

            _pushW = _captureW;
            _pushH = _captureH;
            _pushOrder = _rowOrder;
            _signal.Release();
        }

        // Push thread: one frame per signal. addFrame converts synchronously on this thread, so the direct buffer can
        // be reused as soon as it returns.
        void PushLoop()
        {
            AndroidJNI.AttachCurrentThread();
            try
            {
                while (true)
                {
                    _signal.Wait();
                    if (_stopWorker) break;
                    _policy.Complete(PushOne());
                }
            }
            finally
            {
                AndroidJNI.DetachCurrentThread();
            }
        }

        bool PushOne()
        {
            try
            {
                IntPtr java = AndroidJNI.ToByteArray(_pixels);
                try
                {
                    _putArgs[0].l = java;
                    AndroidJNI.DeleteLocalRef(AndroidJNI.CallObjectMethod(_directBuffer, _bufferClear, _noArgs));
                    AndroidJNI.DeleteLocalRef(AndroidJNI.CallObjectMethod(_directBuffer, _bufferPut, _putArgs));
                    if (AndroidJNI.ExceptionOccurred() != IntPtr.Zero)
                    {
                        AndroidJNI.ExceptionClear();
                        return false;
                    }
                    AndroidJNI.DeleteLocalRef(AndroidJNI.CallObjectMethod(_directBuffer, _bufferRewind, _noArgs));
                }
                finally
                {
                    AndroidJNI.DeleteLocalRef(java);
                }
                _args[0].j = _requestTimestampMs;
                _args[1].i = 0; // display id
                _args[2].l = _directBuffer;
                _args[3].i = _pushW;
                _args[4].i = _pushH;
                _args[5].i = FramePushPolicy.PixelStride;
                _args[6].l = _pushOrder == VideoFrameRowOrder.BottomUp ? _bottomUp : _topDown;
                AndroidJNI.CallVoidMethod(_consumer, _addFrameMethod, _args);
                if (AndroidJNI.ExceptionOccurred() != IntPtr.Zero)
                {
                    AndroidJNI.ExceptionClear();
                    return false;
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        void ReleaseRenderTextures()
        {
            if (_fullRt != null) { _fullRt.Release(); Destroy(_fullRt); _fullRt = null; }
            if (_smallRt != null) { _smallRt.Release(); Destroy(_smallRt); _smallRt = null; }
        }

        void ReleaseDirectBuffer()
        {
            if (_directBuffer != IntPtr.Zero) { AndroidJNI.DeleteGlobalRef(_directBuffer); _directBuffer = IntPtr.Zero; }
        }

        void ReleaseNative()
        {
            ReleaseDirectBuffer();
            if (_consumer != IntPtr.Zero) { AndroidJNI.DeleteGlobalRef(_consumer); _consumer = IntPtr.Zero; }
            if (_topDown != IntPtr.Zero) { AndroidJNI.DeleteGlobalRef(_topDown); _topDown = IntPtr.Zero; }
            if (_bottomUp != IntPtr.Zero) { AndroidJNI.DeleteGlobalRef(_bottomUp); _bottomUp = IntPtr.Zero; }
            _pixels = null;
        }

        void OnDestroy()
        {
            _enabled = false;
            _stopWorker = true;
            _signal.Release();
            if (_instance == this) _instance = null;
        }
#else
        void Stop() { _enabled = false; }
#endif
    }
}
