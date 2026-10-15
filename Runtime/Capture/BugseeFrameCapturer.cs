// Unity-side capture (ScreenCapture + AsyncGPUReadback) handed to the SDK as a native-memory Java ByteBuffer. That needs
// AndroidJNI.NewDirectByteBuffer, which exists from Unity 2022.2.0b1 (all of 2022.3 LTS and 6000.x), not in 2021.3 or
// 2022.1: there the capturer is unsupported (Enable() returns false) and the SDK's own capture stays in effect.
#if BUGSEE_UNITY_CAPTURE && UNITY_ANDROID && !UNITY_EDITOR && BUGSEE_HAS_SCREENCAPTURE
#define BUGSEE_READBACK
#endif
using System;
using System.Collections.Generic;
using Bugsee.Contracts.Capture;
using Bugsee.Contracts.Options;
using Bugsee.WrapperPolicy;
using UnityEngine;

#if BUGSEE_READBACK
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
        /// Row order of the readback buffer. Null uses the order measured for the running graphics API; for an API
        /// without a measurement the pusher is fail-closed (see FramePushPolicy.ResolveRowOrder). Setting it declares
        /// the order verified, so check it with <see cref="LogOrientationProbe"/> first.
        /// </summary>
        public VideoFrameRowOrder? RowOrder;

        /// <summary>Log the colours of the first and last readback rows (and the graphics API) for the first few frames, to check orientation.</summary>
        public bool LogOrientationProbe;
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

        /// <summary>True once <see cref="Enable"/> started the pusher and it has not been disabled.</summary>
        public static bool IsEnabled => _instance != null && _instance._enabled;

        /// <summary>
        /// Options the wrapper uses when it starts the pusher itself (a launch that asks for VideoMode.DirectBuffers).
        /// Set before <c>Bugsee.Launch</c>.
        /// </summary>
        public static BugseeFrameCapturerOptions DefaultOptions = new BugseeFrameCapturerOptions();

        public static BugseeFrameCapturerStats Stats => _instance != null ? _instance.ReadStats() : default;

        /// <summary>
        /// Starts the pusher. Returns false (and does nothing) off Android, in the Editor, on Unity older than 2022.2
        /// (no AndroidJNI.NewDirectByteBuffer), without the Screen Capture module, or when the device cannot do GPU
        /// readback. Call after or around Bugsee.Launch with VideoMode.DirectBuffers; frames are skipped until the SDK
        /// is launched. Main thread only.
        /// </summary>
        public static bool Enable(BugseeFrameCapturerOptions options = null)
        {
#if BUGSEE_READBACK
            if (!Internal.MainThreadDispatcher.IsMainThread)
            {
                Internal.MainThreadDispatcher.Run(() => Enable(options));
                return true;
            }
            if (IsEnabled) return true;
            return EnableReadback(options ?? new BugseeFrameCapturerOptions());
#else
            if (!_warnedUnsupported)
            {
                _warnedUnsupported = true;
                Debug.LogWarning("[BugseeFrameCapturer] unsupported here: it needs an Android player build on Unity 2022.2 or newer "
                    + "(AndroidJNI.NewDirectByteBuffer) with the Screen Capture module. The SDK's own capture stays in effect.");
            }
            return false;
#endif
        }

#if !BUGSEE_READBACK
        static bool _warnedUnsupported;
#endif

        /// <summary>Stops the pusher and releases its buffers once any push in flight has finished.</summary>
        public static void Disable()
        {
#if BUGSEE_READBACK
            if (!Internal.MainThreadDispatcher.IsMainThread)
            {
                Internal.MainThreadDispatcher.Run(Disable);
                return;
            }
#endif
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

#if BUGSEE_READBACK
        static bool EnableReadback(BugseeFrameCapturerOptions options)
        {
            bool vulkan = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Vulkan;
            if (!FramePushPolicy.IsReadbackUsable(SystemInfo.supportsAsyncGPUReadback, vulkan, Application.unityVersion))
            {
                Debug.LogWarning("[BugseeFrameCapturer] GPU readback is not usable here (unsupported, or Vulkan on Unity older than 2021.3.18); not enabled.");
                return false;
            }

            var go = new GameObject("Bugsee.FrameCapturer") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            var capturer = go.AddComponent<BugseeFrameCapturer>();
            if (!capturer.Begin(options))
            {
                Destroy(go);
                return false;
            }
            _instance = capturer;
            return true;
        }

        const string ConsumerClass = "com/bugsee/library/contracts/capture/consumers/VideoFrameConsumer";
        const string RowOrderClass = ConsumerClass + "$RowOrder";
        const string AddFrameSignature = "(JILjava/nio/ByteBuffer;IIIL" + RowOrderClass + ";)V";
        const string TimeUtilsClass = "com/bugsee/library/shared/util/TimeUtils";
        const float StatsLogIntervalSeconds = 10f;
        const double ConsumerRetrySeconds = 2.0;
        const double ConsumerWarnSeconds = 30.0;
        const int ProbeFrames = 3;

        static readonly WaitForEndOfFrame EndOfFrame = new WaitForEndOfFrame();

        BugseeFrameCapturerOptions _options;
        VideoFrameRowOrder _rowOrder;
        bool _rowOrderVerified;
        Action<AsyncGPUReadbackRequest> _onReadback;

        RenderTexture _fullRt, _smallRt;
        int _captureW, _captureH;
        long _requestTimestampMs;

        // Size of the screen at request time, in native window pixels (what secure rectangles are registered in).
        int _lastScreenW, _lastScreenH, _nativeW, _nativeH, _requestNativeW, _requestNativeH;
        int[] _rects;
        int[] _requestRects;   // rectangles registered when the capture started
        int _requestRectCount;
        int _probeFrames;

        // Timing for the stats line (average ms): request to readback callback, copy into the Java buffer, addFrame.
        long _requestTicks, _latencySum, _addSum, _timedFrames;

        // The readback callback (main thread) copies the frame into a persistent native buffer, masks it in place, and
        // hands it to the push thread, which wraps the same memory as a Java direct ByteBuffer for addFrame (no Java
        // array, no file). The SDK copies before addFrame returns. Only one request is in flight, so neither side
        // touches the buffer while the other does.
        // Allocated once per size.
        NativeArray<byte> _native;
        IntPtr _nativePtr;
        int _nativeLength;
        readonly object _nativeLock = new object();
        IntPtr _consumer;          // global ref to the VideoFrameConsumer
        IntPtr _addFrameMethod;
        IntPtr _topDown, _bottomUp; // global refs to the RowOrder constants
        IntPtr _timeClass, _timeMethod; // TimeUtils.monotonicWallTimeMillis(), the SDK's own clock
        long _clockOffsetMs;            // fallback when TimeUtils is not found: wall clock minus SystemClock.elapsedRealtime
        IntPtr _elapsedClass, _elapsedMethod;
        readonly jvalue[] _args = new jvalue[7];
        readonly jvalue[] _noArgs = new jvalue[0];
        bool _wasLaunched;
        bool _warnedMode;
        bool _paused;
        bool _releaseDone;
        float _nextStatsLog;
        double _nextConsumerTry, _nextConsumerWarn;

        Thread _worker;
        readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        volatile bool _stopWorker;
        int _pushW, _pushH;
        VideoFrameRowOrder _pushOrder;

        bool Begin(BugseeFrameCapturerOptions options)
        {
            _options = options;
            _policy = new FramePushPolicy(options.TargetFps);
            _rowOrder = FramePushPolicy.ResolveRowOrder(SystemInfo.graphicsDeviceType.ToString(), options.RowOrder, out _rowOrderVerified);
            if (!_rowOrderVerified)
                Debug.LogWarning("[BugseeFrameCapturer] readback row order is unverified for " + SystemInfo.graphicsDeviceType
                    + ": while a secure rectangle is registered, pushed frames are fully blacked out. Set BugseeFrameCapturerOptions.RowOrder after checking it with LogOrientationProbe.");
            _onReadback = OnReadback;
            try
            {
                _topDown = RowOrderConstant("TOP_DOWN");
                _bottomUp = RowOrderConstant("BOTTOM_UP");
                IntPtr cls = FindClassOrClear(ConsumerClass);
                _addFrameMethod = cls == IntPtr.Zero ? IntPtr.Zero : AndroidJNI.GetMethodID(cls, "addFrame", AddFrameSignature);
                AndroidJNI.DeleteLocalRef(cls);
                const bool bufferMethodsMissing = false;
                if (bufferMethodsMissing
                    || _addFrameMethod == IntPtr.Zero || _topDown == IntPtr.Zero || _bottomUp == IntPtr.Zero)
                {
                    // On an SDK older than 7.4.1 the lookups above leave a pending NoSuchMethodError/ClassNotFoundException.
                    AndroidJNI.ExceptionClear();
                    Debug.LogWarning("[BugseeFrameCapturer] VideoFrameConsumer.addFrame not found; this needs Bugsee Android 7.4.1 or newer.");
                    ReleaseNative();
                    return false;
                }
                InitClock();
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

        // JNI FindClass leaves a pending exception when the class is missing; clear it so later calls are legal.
        static IntPtr FindClassOrClear(string name)
        {
            IntPtr cls = AndroidJNI.FindClass(name);
            if (cls == IntPtr.Zero) AndroidJNI.ExceptionClear();
            return cls;
        }

        static IntPtr RowOrderConstant(string name)
        {
            IntPtr cls = FindClassOrClear(RowOrderClass);
            if (cls == IntPtr.Zero) return IntPtr.Zero;
            IntPtr field = AndroidJNI.GetStaticFieldID(cls, name, "L" + RowOrderClass + ";");
            IntPtr local = field == IntPtr.Zero ? IntPtr.Zero : AndroidJNI.GetStaticObjectField(cls, field);
            IntPtr global = local == IntPtr.Zero ? IntPtr.Zero : AndroidJNI.NewGlobalRef(local);
            AndroidJNI.DeleteLocalRef(local);
            AndroidJNI.DeleteLocalRef(cls);
            return global;
        }

        // The SDK stamps frames with TimeUtils.monotonicWallTimeMillis() (boot time + elapsedRealtime); a wall-clock
        // timestamp could jump relative to that and make the SDK drop or reorder frames.
        void InitClock()
        {
            IntPtr cls = FindClassOrClear(TimeUtilsClass);
            IntPtr method = cls == IntPtr.Zero ? IntPtr.Zero : AndroidJNI.GetStaticMethodID(cls, "monotonicWallTimeMillis", "()J");
            if (method != IntPtr.Zero)
            {
                _timeClass = AndroidJNI.NewGlobalRef(cls);
                _timeMethod = method;
            }
            else
            {
                AndroidJNI.ExceptionClear();
                IntPtr clock = AndroidJNI.FindClass("android/os/SystemClock");
                _elapsedClass = AndroidJNI.NewGlobalRef(clock);
                _elapsedMethod = AndroidJNI.GetStaticMethodID(clock, "elapsedRealtime", "()J");
                AndroidJNI.DeleteLocalRef(clock);
                _clockOffsetMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    - AndroidJNI.CallStaticLongMethod(_elapsedClass, _elapsedMethod, _noArgs);
            }
            AndroidJNI.DeleteLocalRef(cls);
        }

        long NowMs() => _timeMethod != IntPtr.Zero
            ? AndroidJNI.CallStaticLongMethod(_timeClass, _timeMethod, _noArgs)
            : _clockOffsetMs + AndroidJNI.CallStaticLongMethod(_elapsedClass, _elapsedMethod, _noArgs);


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
                        + " size=" + _captureW + "x" + _captureH + " rowOrder=" + _rowOrder + (_rowOrderVerified ? "" : " (unverified)") + " api=" + SystemInfo.graphicsDeviceType
                        + " avgMs[readbackLatency/addFrame]=" + AvgMs(Interlocked.Read(ref _latencySum)) + "/" + AvgMs(Interlocked.Read(ref _addSum)));
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
            if (launched && (!_wasLaunched || _consumer == IntPtr.Zero) && now >= _nextConsumerTry && !_policy.HasInFlight)
            {
                _nextConsumerTry = now + ConsumerRetrySeconds;
                RefreshConsumer();
                if (_consumer == IntPtr.Zero && now >= _nextConsumerWarn)
                {
                    _nextConsumerWarn = now + ConsumerWarnSeconds;
                    Debug.LogWarning("[BugseeFrameCapturer] the SDK has no video frame consumer yet (not launched with video capture?); retrying.");
                }
            }
            _wasLaunched = launched;
            stopped |= _consumer == IntPtr.Zero;

            if (_policy.TryBegin(now, stopped) != FramePushDecision.Request) return;

            try
            {
                UpdateNativeSize();
                if (!EnsureBuffers()) { _policy.Complete(false); return; }
                _requestTimestampMs = NowMs();
                _requestTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                _requestNativeW = _nativeW;
                _requestNativeH = _nativeH;
                _requestRectCount = SecureRectRegistry.Instance.CopyRects(0, ref _requestRects);
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

        // Secure rectangles are native window pixels, which differ from Screen.width/height when Unity renders at a
        // reduced resolution. Re-read the activity window size only when the Unity screen size changes.
        void UpdateNativeSize()
        {
            if (Screen.width == _lastScreenW && Screen.height == _lastScreenH && _nativeW > 0) return;
            _lastScreenW = Screen.width;
            _lastScreenH = Screen.height;
            int w = 0, h = 0;
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var window = activity.Call<AndroidJavaObject>("getWindow"))
                using (var decor = window.Call<AndroidJavaObject>("getDecorView"))
                {
                    w = decor.Call<int>("getWidth");
                    h = decor.Call<int>("getHeight");
                }
            }
            catch (Exception)
            {
                // fall back to the Unity size below
            }
            _nativeW = w > 0 ? w : Screen.width;
            _nativeH = h > 0 ? h : Screen.height;
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

        bool HasPixelBuffer =>
            _nativePtr != IntPtr.Zero;

        // The frame buffer as a span (managed array, or the persistent native memory), so masking and probing do not
        // care which one is in use.
        Span<byte> PixelSpan()
        {
            unsafe { return new Span<byte>((void*)_nativePtr, _nativeLength); }
        }

        bool EnsureBuffers()
        {
            FramePushPolicy.ComputeCaptureSize(Screen.width, Screen.height, _options.MaxDimension, out int w, out int h);
            bool fullStale = _fullRt == null || _fullRt.width != Screen.width || _fullRt.height != Screen.height;
            if (!fullStale && w == _captureW && h == _captureH && _smallRt != null && HasPixelBuffer) return true;

            // Nothing is in flight here (this request is the only one), so the push thread is idle.
            ReleaseRenderTextures();
            _fullRt = new RenderTexture(Mathf.Max(2, Screen.width), Mathf.Max(2, Screen.height), 0, RenderTextureFormat.ARGB32);
            _smallRt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
            _fullRt.Create();
            _smallRt.Create();
            _captureW = w;
            _captureH = h;

            int bytes = (int)FramePushPolicy.FrameBytes(w, h);
            ReleaseNativeBuffer();
            _native = new NativeArray<byte>(bytes, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            unsafe { _nativePtr = (IntPtr)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafePtr(_native); }
            _nativeLength = bytes;
            return true;
        }

        void OnReadback(AsyncGPUReadbackRequest request)
        {
            if (!_enabled || request.hasError || !HasPixelBuffer)
            {
                _policy.Complete(false);
                return;
            }
            Interlocked.Add(ref _latencySum, System.Diagnostics.Stopwatch.GetTimestamp() - _requestTicks);
            NativeArray<byte> data = request.GetData<byte>();
            Span<byte> pixels = PixelSpan();
            if (data.Length < pixels.Length) { _policy.Complete(false); return; }
            // Not in flight (one request at a time), so the persistent buffer is ours: one native copy.
            NativeArray<byte>.Copy(data, 0, _native, 0, pixels.Length);

            if (_options.LogOrientationProbe && _probeFrames < ProbeFrames) LogProbe(pixels);

            // The SDK does not mask pushed frames: blank the registered secure rectangles first. If the row order is
            // unverified the mask could land on mirrored rows and leak the real area, so blank the whole frame instead.
            // Mask what was secure when the capture started as well as what is secure now. Live rectangles are in the
            // window size of now (it may have changed since the request), request-time ones in the size of the request.
            UpdateNativeSize();
            int count = SecureRectRegistry.Instance.CopyRects(0, ref _rects);
            if (count > 0 || _requestRectCount > 0)
            {
                if (_rowOrderVerified)
                    FrameRedaction.BlankRequestAndLive(pixels, _captureW, _captureH, _rowOrder,
                        _requestRects, _requestRectCount, _requestNativeW, _requestNativeH,
                        _rects, count, _nativeW, _nativeH);
                else
                    FrameRedaction.BlankAll(pixels, _captureW, _captureH);
            }

            _pushW = _captureW;
            _pushH = _captureH;
            _pushOrder = _rowOrder;
            _signal.Release();
        }

        string AvgMs(long ticks)
        {
            long n = Interlocked.Read(ref _timedFrames);
            return n > 0 ? (ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / n).ToString("F1") : "-";
        }

        // Colour of the centre pixel of the first and last buffer rows, to tell which row is the screen's top.
        void LogProbe(Span<byte> pixels)
        {
            _probeFrames++;
            int x = _captureW / 2;
            int last = _captureH - 1;
            int a = (0 * _captureW + x) * 4;
            int b = (last * _captureW + x) * 4;
            Debug.Log("[BugseeFrameCapturer] probe api=" + SystemInfo.graphicsDeviceType + " uvStartsAtTop=" + SystemInfo.graphicsUVStartsAtTop
                + " firstRow rgba=" + pixels[a] + "," + pixels[a + 1] + "," + pixels[a + 2] + "," + pixels[a + 3]
                + " lastRow rgba=" + pixels[b] + "," + pixels[b + 1] + "," + pixels[b + 2] + "," + pixels[b + 3]);
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
                int length = _pushW * _pushH * FramePushPolicy.PixelStride;
                // The SDK reads width * height * stride bytes (stride 4) from the buffer base and needs capacity >= that;
                // the native buffer was sized exactly so. It copies before addFrame returns, and the buffer is not
                // reused until Complete() below lets the next request start.
                if (length > _nativeLength) return false;
                IntPtr buffer;
                unsafe { buffer = AndroidJNI.NewDirectByteBuffer((byte*)_nativePtr, length); }
                if (buffer == IntPtr.Zero) { ClearPendingException(); return false; }
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                _args[0].j = _requestTimestampMs;
                _args[1].i = 0; // display id
                _args[2].l = buffer;
                _args[3].i = _pushW;
                _args[4].i = _pushH;
                _args[5].i = FramePushPolicy.PixelStride;
                _args[6].l = _pushOrder == VideoFrameRowOrder.BottomUp ? _bottomUp : _topDown;
                AndroidJNI.CallVoidMethod(_consumer, _addFrameMethod, _args);
                long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
                AndroidJNI.DeleteLocalRef(buffer);
                Interlocked.Add(ref _addSum, t2 - t1);
                Interlocked.Increment(ref _timedFrames);
                return !ClearPendingException();
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ExceptionOccurred returns a local reference: free it, then clear the exception.
        static bool ClearPendingException()
        {
            IntPtr exception = AndroidJNI.ExceptionOccurred();
            if (exception == IntPtr.Zero) return false;
            AndroidJNI.DeleteLocalRef(exception);
            AndroidJNI.ExceptionClear();
            return true;
        }

        void ReleaseRenderTextures()
        {
            if (_fullRt != null) { _fullRt.Release(); Destroy(_fullRt); _fullRt = null; }
            if (_smallRt != null) { _smallRt.Release(); Destroy(_smallRt); _smallRt = null; }
        }

        void ReleaseNativeBuffer()
        {
            _nativePtr = IntPtr.Zero;
            _nativeLength = 0;
            if (_native.IsCreated) _native.Dispose();
        }


        void ReleaseNative()
        {
            ReleaseNativeBuffer();
            lock (_nativeLock)
            {
                IntPtr[] refs = { _consumer, _topDown, _bottomUp, _timeClass, _elapsedClass };
                for (int i = 0; i < refs.Length; i++)
                    if (refs[i] != IntPtr.Zero) AndroidJNI.DeleteGlobalRef(refs[i]);
                _consumer = _topDown = _bottomUp = _timeClass = _elapsedClass = IntPtr.Zero;
                _timeMethod = _elapsedMethod = IntPtr.Zero;
            }
        }

        void OnDestroy()
        {
            _enabled = false;
            _stopWorker = true;
            _signal.Release();
            if (_instance == this) _instance = null;
            ReleaseRenderTextures();
            // A push still running owns the references until it finishes; the worker detaches on exit.
            if (_policy == null || !_policy.HasInFlight) ReleaseNative();
        }
#else
        void Stop() { _enabled = false; }
#endif
    }
}
