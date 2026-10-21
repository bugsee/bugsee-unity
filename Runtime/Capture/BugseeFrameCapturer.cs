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

        /// <summary>Log the capture counters and timings every 10 seconds (off by default).</summary>
        public bool LogStats;

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

        /// <summary>Raised on the main thread when a running pusher turned itself off after repeated readback failures.</summary>
        internal static event Action Failed;

        public static BugseeFrameCapturerStats Stats => _instance != null ? _instance.ReadStats() : default;

        /// <summary>
        /// Starts the pusher. Returns false (and does nothing) off Android, in the Editor, on Unity older than 2022.2
        /// (no AndroidJNI.NewDirectByteBuffer), without the Screen Capture module, or when the device cannot do GPU
        /// readback. Call after or around Bugsee.Launch with VideoMode.DirectBuffers; frames are skipped until the SDK
        /// is launched. Called off the main thread it is queued and returns true before it has run: whether it actually
        /// started is then visible through <see cref="IsEnabled"/>. (A launch that asks for DirectBuffers starts the
        /// capturer itself on the main thread and relaunches with the default capture if it cannot start; that path does
        /// not depend on this return value.)
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
            // The compile-time define also matches alpha and beta builds; only release builds are supported.
            if (!UnityCapturePolicy.IsSupportedUnityVersion(Application.unityVersion))
            {
                Debug.LogWarning("[BugseeFrameCapturer] Unity " + Application.unityVersion + " is not supported (release builds of "
                    + UnityCapturePolicy.MinimumUnityVersion + " or newer only); not enabled.");
                return false;
            }
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

        const string BugseeClass = "com/bugsee/library/Bugsee";
        const string ConsumerClass = "com/bugsee/library/contracts/capture/consumers/VideoFrameConsumer";
        const string RowOrderClass = ConsumerClass + "$RowOrder";
        const string AddFrameSignature = "(JILjava/nio/ByteBuffer;IIIL" + RowOrderClass + ";)V";
        const string TimeUtilsClass = "com/bugsee/library/shared/util/TimeUtils";
        const float StatsLogIntervalSeconds = 10f;
        const int AndroidContentViewId = 16908290; // android.R.id.content
        const int ProbeFrames = 3;

        static readonly WaitForEndOfFrame EndOfFrame = new WaitForEndOfFrame();

        BugseeFrameCapturerOptions _options;
        VideoFrameRowOrder _rowOrder;
        bool _rowOrderVerified;
        Action<AsyncGPUReadbackRequest> _onReadback;

        RenderTexture _fullRt, _smallRt;
        int _captureW, _captureH;
        long _requestTimestampMs;

        // Where Unity's surface sits on the screen, in native screen pixels (what secure rectangles are registered in), read
        // at every request and every readback. Width 0 means unknown (fail closed).
        SourceRegion _viewRegion, _requestRegion, _loggedRegion;
        int[] _rects;
        int[] _requestRects;   // rectangles registered when the capture started
        int _requestRectCount;
        int _probeFrames;

        // Timing for the stats line (average ms): request to readback callback, addFrame.
        long _requestTicks, _latencySum, _addSum, _timedFrames;

        // The readback callback (main thread) copies the frame into a persistent native buffer, masks it in place, and
        // hands it to the push thread, which passes the same memory to addFrame as a Java direct ByteBuffer (no Java
        // array, no file). The SDK copies before addFrame returns. Only one request is in flight, so neither side
        // touches the buffer while the other does. Allocated once per size, with one cached global ref to the ByteBuffer.
        NativeArray<byte> _native;
        IntPtr _nativePtr;
        int _nativeLength;
        IntPtr _directBuffer;       // global ref to the ByteBuffer over _native
        IntPtr _bugseeClass, _isBlackoutMethod, _getConsumerMethod; // Bugsee.isBlackout() / getVideoFrameConsumer()
        IntPtr _consumer;           // global ref to the VideoFrameConsumer in use
        IntPtr _addFrameMethod;
        IntPtr _topDown, _bottomUp; // global refs to the RowOrder constants
        IntPtr _timeClass, _timeMethod; // TimeUtils.monotonicWallTimeMillis(), the SDK's own clock
        long _clockOffsetMs;            // fallback when TimeUtils is not found: wall clock minus SystemClock.elapsedRealtime
        IntPtr _elapsedClass, _elapsedMethod;
        readonly jvalue[] _args = new jvalue[7];
        readonly jvalue[] _noArgs = new jvalue[0];
        bool _warnedMode;
        bool _paused;
        bool _releaseDone;
        float _nextStatsLog;

        Thread _worker;
        readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        volatile bool _stopWorker;
        volatile bool _pushing;
        int _nativeReleased;
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
                IntPtr bugsee = FindClassOrClear(BugseeClass);
                if (bugsee != IntPtr.Zero)
                {
                    _bugseeClass = AndroidJNI.NewGlobalRef(bugsee);
                    _isBlackoutMethod = AndroidJNI.GetStaticMethodID(bugsee, "isBlackout", "()Z");
                    _getConsumerMethod = AndroidJNI.GetStaticMethodID(bugsee, "getVideoFrameConsumer", "()L" + ConsumerClass + ";");
                    AndroidJNI.DeleteLocalRef(bugsee);
                }
                if (_addFrameMethod == IntPtr.Zero || _topDown == IntPtr.Zero || _bottomUp == IntPtr.Zero
                    || _isBlackoutMethod == IntPtr.Zero || _getConsumerMethod == IntPtr.Zero)
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
                if (_options.LogStats && Time.unscaledTime >= _nextStatsLog)
                {
                    _nextStatsLog = Time.unscaledTime + StatsLogIntervalSeconds;
                    var s = ReadStats();
                    Debug.Log("[BugseeFrameCapturer] requested=" + s.Requested + " pushed=" + s.Pushed + " failed=" + s.Failed
                        + " droppedInFlight=" + s.DroppedInFlight + " droppedBlackoutOrStopped=" + s.DroppedBlackoutOrStopped
                        + " size=" + _captureW + "x" + _captureH + " rowOrder=" + _rowOrder + (_rowOrderVerified ? "" : " (unverified)") + " api=" + SystemInfo.graphicsDeviceType
                        + " avgMs[readbackLatency/addFrame]=" + AvgMs(Interlocked.Read(ref _latencySum)) + "/" + AvgMs(Interlocked.Read(ref _addSum)));
                }
                if (_policy.IsDisabled)
                {
                    _enabled = false;
                    Debug.LogWarning("[BugseeFrameCapturer] turned itself off after repeated readback failures.");
                    Failed?.Invoke();
                }
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
                    // An exception would end the coroutine and silently stop capture; count it as a failed capture so a
                    // persistent problem turns the pusher off (and the wrapper falls back to the default capture).
                    Debug.LogWarning("[BugseeFrameCapturer] tick failed: " + ex.Message);
                    _policy.RecordFailure();
                }
            }
        }

        void Tick()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (_paused || !_policy.IsDue(now)) return;

            // Raw JNI, no managed allocation: the consumer exists only while the SDK is launched, and a relaunch hands out
            // a new one, so the cached reference is replaced when it differs (only while nothing is in flight).
            IntPtr current = AndroidJNI.CallStaticObjectMethod(_bugseeClass, _getConsumerMethod, _noArgs);
            if (ClearPendingException()) current = IntPtr.Zero;
            bool launched = current != IntPtr.Zero;
            if (launched && !_policy.HasInFlight && (_consumer == IntPtr.Zero || !AndroidJNI.IsSameObject(_consumer, current)))
            {
                if (_consumer != IntPtr.Zero) AndroidJNI.DeleteGlobalRef(_consumer);
                _consumer = AndroidJNI.NewGlobalRef(current);
                CheckLaunchMode();
            }
            AndroidJNI.DeleteLocalRef(current);
            bool blackout = AndroidJNI.CallStaticBooleanMethod(_bugseeClass, _isBlackoutMethod, _noArgs);
            if (ClearPendingException()) blackout = true; // cannot prove it is not blacked out: fail closed
            bool stopped = !launched || blackout || _consumer == IntPtr.Zero;

            if (_policy.TryBegin(now, stopped) != FramePushDecision.Request) return;

            try
            {
                _viewRegion = ReadViewRegion();
                if (!EnsureBuffers()) { _policy.Complete(false); return; }
                _requestTimestampMs = NowMs();
                _requestTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                _requestRegion = _viewRegion;
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

        // Secure rectangles are native screen pixels, and Unity's view need not fill the window (status bar, cutout,
        // multi-window, reduced render resolution), so the rectangles are mapped relative to the Unity surface's position in
        // the window. Read at every request and at every readback (no time-based cache), with raw JNI (no managed garbage).
        // The Unity view is the first SurfaceView under the activity's content view; if there is none, or anything fails,
        // the region is unknown and masking fails closed.
        SourceRegion ReadViewRegion()
        {
            try
            {
                if (_jni.PlayerClass == IntPtr.Zero && !_jni.Init()) return default;
                IntPtr activity = AndroidJNI.GetStaticObjectField(_jni.PlayerClass, _jni.CurrentActivityField);
                if (activity == IntPtr.Zero) return default;
                _jni.IntArgs[0].i = AndroidContentViewId;
                IntPtr content = AndroidJNI.CallObjectMethod(activity, _jni.FindViewById, _jni.IntArgs);
                AndroidJNI.DeleteLocalRef(activity);
                if (content == IntPtr.Zero) { ClearPendingException(); return default; }
                IntPtr surface = FindSurfaceView(content, 0);
                AndroidJNI.DeleteLocalRef(content);
                if (surface == IntPtr.Zero) { ClearPendingException(); return default; }
                _jni.ArrayArgs[0].l = _jni.LocationArray;
                AndroidJNI.CallVoidMethod(surface, _jni.GetLocationOnScreen, _jni.ArrayArgs);
                int left = AndroidJNI.GetIntArrayElement(_jni.LocationArray, 0);
                int top = AndroidJNI.GetIntArrayElement(_jni.LocationArray, 1);
                int width = AndroidJNI.CallIntMethod(surface, _jni.GetWidth, _noArgs);
                int height = AndroidJNI.CallIntMethod(surface, _jni.GetHeight, _noArgs);
                AndroidJNI.DeleteLocalRef(surface);
                if (ClearPendingException()) return default;
                var region = new SourceRegion(left, top, width, height);
                // Not Unity's surface (a plugin's SurfaceView, or a view caught mid-rotation): unknown, so masking fails closed.
                if (!FramePushPolicy.IsPlausibleUnityView(width, height, Screen.width, Screen.height)) return default;
                if (_options.LogStats && (region.Left != _loggedRegion.Left || region.Top != _loggedRegion.Top
                    || region.Width != _loggedRegion.Width || region.Height != _loggedRegion.Height))
                {
                    _loggedRegion = region;
                    Debug.Log("[BugseeFrameCapturer] view region left=" + left + " top=" + top + " size=" + width + "x" + height);
                }
                return region;
            }
            catch (Exception)
            {
                ClearPendingException();
                return default;
            }
        }

        // Depth-first search for the first SurfaceView; returns a new local reference or zero. Depth-limited.
        IntPtr FindSurfaceView(IntPtr view, int depth)
        {
            if (AndroidJNI.IsInstanceOf(view, _jni.SurfaceViewClass)) return AndroidJNI.NewLocalRef(view);
            if (depth > 6 || !AndroidJNI.IsInstanceOf(view, _jni.ViewGroupClass)) return IntPtr.Zero;
            int count = AndroidJNI.CallIntMethod(view, _jni.GetChildCount, _noArgs);
            for (int i = 0; i < count; i++)
            {
                _jni.IntArgs[0].i = i;
                IntPtr child = AndroidJNI.CallObjectMethod(view, _jni.GetChildAt, _jni.IntArgs);
                if (child == IntPtr.Zero) continue;
                IntPtr found = FindSurfaceView(child, depth + 1);
                AndroidJNI.DeleteLocalRef(child);
                if (found != IntPtr.Zero) return found;
            }
            return IntPtr.Zero;
        }

        // Cached JNI handles for ReadViewRegion (main thread only).
        sealed class ViewJni
        {
            public IntPtr PlayerClass, CurrentActivityField, FindViewById, GetLocationOnScreen, GetWidth, GetHeight,
                GetChildCount, GetChildAt, SurfaceViewClass, ViewGroupClass, LocationArray;
            public readonly jvalue[] IntArgs = new jvalue[1];
            public readonly jvalue[] ArrayArgs = new jvalue[1];

            public bool Init()
            {
                IntPtr player = FindClassOrClear("com/unity3d/player/UnityPlayer");
                IntPtr activity = FindClassOrClear("android/app/Activity");
                IntPtr view = FindClassOrClear("android/view/View");
                IntPtr group = FindClassOrClear("android/view/ViewGroup");
                IntPtr surface = FindClassOrClear("android/view/SurfaceView");
                if (player == IntPtr.Zero || activity == IntPtr.Zero || view == IntPtr.Zero || group == IntPtr.Zero || surface == IntPtr.Zero)
                    return false;
                CurrentActivityField = AndroidJNI.GetStaticFieldID(player, "currentActivity", "Landroid/app/Activity;");
                FindViewById = AndroidJNI.GetMethodID(activity, "findViewById", "(I)Landroid/view/View;");
                GetLocationOnScreen = AndroidJNI.GetMethodID(view, "getLocationOnScreen", "([I)V");
                GetWidth = AndroidJNI.GetMethodID(view, "getWidth", "()I");
                GetHeight = AndroidJNI.GetMethodID(view, "getHeight", "()I");
                GetChildCount = AndroidJNI.GetMethodID(group, "getChildCount", "()I");
                GetChildAt = AndroidJNI.GetMethodID(group, "getChildAt", "(I)Landroid/view/View;");
                IntPtr array = AndroidJNI.ToIntArray(new int[2]);
                if (CurrentActivityField == IntPtr.Zero || FindViewById == IntPtr.Zero || GetLocationOnScreen == IntPtr.Zero
                    || GetWidth == IntPtr.Zero || GetHeight == IntPtr.Zero || GetChildCount == IntPtr.Zero
                    || GetChildAt == IntPtr.Zero || array == IntPtr.Zero)
                {
                    AndroidJNI.ExceptionClear();
                    return false;
                }
                LocationArray = AndroidJNI.NewGlobalRef(array);
                AndroidJNI.DeleteLocalRef(array);
                PlayerClass = AndroidJNI.NewGlobalRef(player);
                SurfaceViewClass = AndroidJNI.NewGlobalRef(surface);
                ViewGroupClass = AndroidJNI.NewGlobalRef(group);
                AndroidJNI.DeleteLocalRef(player);
                AndroidJNI.DeleteLocalRef(activity);
                AndroidJNI.DeleteLocalRef(view);
                AndroidJNI.DeleteLocalRef(group);
                AndroidJNI.DeleteLocalRef(surface);
                return true;
            }

            public void Release()
            {
                IntPtr[] refs = { PlayerClass, SurfaceViewClass, ViewGroupClass, LocationArray };
                for (int i = 0; i < refs.Length; i++)
                    if (refs[i] != IntPtr.Zero) AndroidJNI.DeleteGlobalRef(refs[i]);
                PlayerClass = SurfaceViewClass = ViewGroupClass = LocationArray = IntPtr.Zero;
            }
        }

        readonly ViewJni _jni = new ViewJni();

        // Once per consumer: warn if the SDK is not launched with DirectBuffers (it then ignores pushed frames).
        void CheckLaunchMode()
        {
            if (_warnedMode) return;
            _warnedMode = true;
            IReadOnlyDictionary<string, object> launch = Bugsee.GetLaunchOptions();
            // The SDK reports option values as strings.
            if (launch != null && launch.TryGetValue(Options.CaptureVideoMode, out object mode) && mode != null
                && int.TryParse(Convert.ToString(mode, System.Globalization.CultureInfo.InvariantCulture), out int modeValue)
                && VideoModeExtensions.FromIntValue(modeValue) != VideoMode.DirectBuffers)
                Debug.LogWarning("[BugseeFrameCapturer] the SDK is not launched with VideoMode.DirectBuffers; pushed frames are ignored.");
        }

        bool HasPixelBuffer => _nativePtr != IntPtr.Zero;

        // The frame buffer as a span over the persistent native memory.
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
            IntPtr local;
            unsafe { local = AndroidJNI.NewDirectByteBuffer((byte*)_nativePtr, bytes); }
            if (local == IntPtr.Zero)
            {
                ClearPendingException();
                ReleaseNativeBuffer();
                return false;
            }
            _directBuffer = AndroidJNI.NewGlobalRef(local);
            AndroidJNI.DeleteLocalRef(local);
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

            // The SDK does not mask pushed frames: blank the registered secure rectangles first. Mask what was secure when
            // the capture started as well as what is secure now, each mapped with the view position of its own moment. If
            // the row order is unverified the mask could land on mirrored rows and leak the real area, and if a view
            // position is unknown the rectangles cannot be placed: both blank the whole frame.
            _viewRegion = ReadViewRegion();
            int count = SecureRectRegistry.Instance.CopyRects(0, ref _rects);
            if (count > 0 || _requestRectCount > 0)
            {
                if (_rowOrderVerified)
                    FrameRedaction.MaskFrame(pixels, _captureW, _captureH, _rowOrder,
                        _requestRects, _requestRectCount, _requestRegion, _rects, count, _viewRegion);
                else
                    FrameRedaction.BlankAll(pixels, _captureW, _captureH);
            }

            _pushW = _captureW;
            _pushH = _captureH;
            _pushOrder = _rowOrder;
            _pushing = true;
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

        // Push thread: one frame per signal. addFrame converts synchronously on this thread, so the buffer can be reused
        // as soon as it returns.
        void PushLoop()
        {
            AndroidJNI.AttachCurrentThread();
            try
            {
                while (true)
                {
                    _signal.Wait();
                    if (_stopWorker) break;
                    bool ok = PushOne();
                    _pushing = false;
                    _policy.Complete(ok);
                    // A destroy that arrived during the push left the release to us.
                    if (_stopWorker) break;
                }
            }
            finally
            {
                if (_stopWorker) ReleaseNative();
                AndroidJNI.DetachCurrentThread();
            }
        }

        bool PushOne()
        {
            try
            {
                int length = _pushW * _pushH * FramePushPolicy.PixelStride;
                // The SDK reads width * height * stride bytes (stride 4) from the buffer base and needs capacity >= that;
                // the native buffer was sized exactly so.
                if (length > _nativeLength || _directBuffer == IntPtr.Zero || _consumer == IntPtr.Zero) return false;
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                _args[0].j = _requestTimestampMs;
                _args[1].i = 0; // display id
                _args[2].l = _directBuffer;
                _args[3].i = _pushW;
                _args[4].i = _pushH;
                _args[5].i = FramePushPolicy.PixelStride;
                _args[6].l = _pushOrder == VideoFrameRowOrder.BottomUp ? _bottomUp : _topDown;
                AndroidJNI.CallVoidMethod(_consumer, _addFrameMethod, _args);
                long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
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
            if (_directBuffer != IntPtr.Zero) { AndroidJNI.DeleteGlobalRef(_directBuffer); _directBuffer = IntPtr.Zero; }
            _nativePtr = IntPtr.Zero;
            _nativeLength = 0;
            if (_native.IsCreated) _native.Dispose();
        }

        // Safe to call from the main thread when no push is running, or from the push thread as it exits.
        void ReleaseNative()
        {
            // Both the main thread and the push thread may get here when the capturer is destroyed: only the first releases.
            if (Interlocked.Exchange(ref _nativeReleased, 1) == 1) return;
            ReleaseNativeBuffer();
            _jni.Release();
            IntPtr[] refs = { _consumer, _topDown, _bottomUp, _timeClass, _elapsedClass, _bugseeClass };
            for (int i = 0; i < refs.Length; i++)
                if (refs[i] != IntPtr.Zero) AndroidJNI.DeleteGlobalRef(refs[i]);
            _consumer = _topDown = _bottomUp = _timeClass = _elapsedClass = _bugseeClass = IntPtr.Zero;
            _timeMethod = _elapsedMethod = _isBlackoutMethod = _getConsumerMethod = IntPtr.Zero;
        }

        void OnDestroy()
        {
            _enabled = false;
            _stopWorker = true;
            _signal.Release();
            if (_instance == this) _instance = null;
            ReleaseRenderTextures();
            // A push still running owns the buffer and references until it finishes; the push thread then releases them.
            // Otherwise (also with a readback still pending: its callback finds no buffer and gives up) release now.
            if (!_pushing) ReleaseNative();
        }
#else
        void Stop() { _enabled = false; }
#endif
    }
}
