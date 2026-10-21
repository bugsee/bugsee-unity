using System;
using System.Collections;
using System.Collections.Generic;
using Bugsee.Contracts.Options;
using Bugsee.Internal;
using Bugsee.WrapperPolicy;
using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>
    /// Launches Bugsee, keeps lifecycle history, and owns the shared action catalog.
    /// Attach to the scene root (or let <see cref="MiniGameBootstrap"/> create it).
    /// </summary>
    public sealed class BugseeSampleBootstrap : MonoBehaviour
    {
        [SerializeField] string appToken = "";
        /// <summary>
        /// Optional Bugsee API endpoint override (device → local appserver via tunnel).
        /// Android: host only (SDK appends /v2). iOS: include /v2 suffix.
        /// Example Android: https://xxxx.ngrok-free.app
        /// Example iOS: https://xxxx.ngrok-free.app/v2
        /// </summary>
        [SerializeField] string endpointOverride = "";
        [SerializeField] bool launchOnStart = true;
        [SerializeField] bool installReportHandler;
        [SerializeField] bool installSampleFilters;
        /// <summary>Experimental Android frame pusher (off by default); an intent extra "bugsee_frame_pusher" = 1 also turns it on.</summary>
        [SerializeField] bool experimentalFramePusher;
        bool _framePusherRequested; // the opt-in as resolved at launch; a relaunch keeps it

        readonly Queue<string> _lifecycleLog = new Queue<string>();
        const int MaxLifecycleEntries = 24;

        BugseeActionCatalog _catalog;
        string _lastStatus = "Idle";
        string _lastLifecycle = "—";

        public int Score { get; set; }
        public string LastStatus => _lastStatus;
        public string LastLifecycle => _lastLifecycle;
        public IEnumerable<string> LifecycleLog => _lifecycleLog;
        public BugseeActionCatalog Catalog => _catalog;
        internal string AppToken => appToken;
        /// <summary>What the last LaunchSdk used (null in the Editor): scenarios and the async bubbles launch again the same way (endpoint, video, debug).</summary>
        internal Dictionary<string, object> LastLaunchOptions { get; private set; }

        string _preset = LaunchPresets.Default;
        bool _presetResolved;
        /// <summary>The launch preset the options of the last launch were built with (see <see cref="LaunchPresets"/>).</summary>
        public string LaunchPreset => _preset;
        /// <summary>The task of the last <see cref="RelaunchSdk"/> or <see cref="RelaunchWithPreset"/> (null before one).</summary>
        internal System.Threading.Tasks.Task<bool> LastRelaunch { get; private set; }

        string _optionsLine = "";
        float _optionsLineAt = -10f;

        /// <summary>What the SDK reports as its launch options, for the debug panel (read from native at most once a second).</summary>
        public string LaunchOptionsLine
        {
            get
            {
                if (Time.unscaledTime - _optionsLineAt < 1f && _optionsLine.StartsWith("preset=" + _preset + " "))
                    return _optionsLine;
                _optionsLineAt = Time.unscaledTime;
                try
                {
                    var options = Bugsee.GetLaunchOptions();
                    _optionsLine = "preset=" + _preset + " | " + (options == null ? "not launched" : options.Count + " launch option keys");
                }
                catch (Exception ex)
                {
                    _optionsLine = "preset=" + _preset + " | launch options error: " + ex.Message;
                }
                return _optionsLine;
            }
        }

        /// <summary>Called from <see cref="MiniGameBootstrap"/> when building the scene at runtime.</summary>
        public void Configure(string token, bool launchOnStart, string endpoint = null)
        {
            appToken = token ?? "";
            this.launchOnStart = launchOnStart;
            if (!string.IsNullOrWhiteSpace(endpoint))
                endpointOverride = endpoint.Trim();
        }

        /// <summary>What the sample filters saw and dropped or redacted so far, for the HUD.</summary>
        public string FilterLine => "flt " + (SampleFilters.NetworkRedactEnabled ? "on " : "off ") + SampleFilters.Summary;

        public string StatusLine
        {
            get
            {
                try
                {
                    return string.Format(
                        "launched={0} status={1} blackout={2} | {3}",
                        Bugsee.IsLaunched,
                        Bugsee.Status,
                        Bugsee.IsBlackout,
                        _lastStatus);
                }
                catch (Exception ex)
                {
                    return "status error: " + ex.Message;
                }
            }
        }

        void Awake()
        {
            // Stay scene-scoped so MiniGameBootstrap can rebuild cleanly on reload.
            _catalog = new BugseeActionCatalog(this);
            Bugsee.LifecycleEvent += OnLifecycle;
        }

        void Start()
        {
            if (launchOnStart)
                LaunchSdk();
        }

        void OnDestroy()
        {
            Bugsee.LifecycleEvent -= OnLifecycle;
            _catalog?.Interactions.Dispose();
            if (ReferenceEquals(BugseeActionCatalog.Instance, _catalog))
                BugseeActionCatalog.ClearInstance(_catalog);
        }

        public void SetStatus(string message)
        {
            _lastStatus = message ?? "";
            Debug.Log("[BugseeSample] " + _lastStatus);
        }

        public void LaunchSdk()
        {
            if (string.IsNullOrWhiteSpace(appToken))
            {
                SetStatus("Set Bugsee App Token on MiniGameBootstrap (Inspector), then Launch");
                return;
            }

            try
            {
                if (!_presetResolved)
                {
                    // "bugsee_preset" = minimal | detect | privacy launches with that option preset (default: the sample's own options).
                    _preset = LaunchPresets.Resolve(ReadE2EValue("bugsee_preset", "BUGSEE_PRESET"));
                    _presetResolved = true;
                }
#if UNITY_ANDROID && !UNITY_EDITOR
                // Asking for DirectBuffers means the Unity-side frame pusher; the wrapper starts it, or falls back to the
                // default capture (with a warning) when this Unity cannot run it. Without the switch: the default capture.
                _framePusherRequested = experimentalFramePusher || ReadE2EValue("bugsee_frame_pusher", "BUGSEE_FRAME_PUSHER") == "1";
                // "bugsee_frame_pattern" = 1 also paints a red-over-blue test pattern and logs the readback rows.
                var pattern = ReadE2EValue("bugsee_frame_pattern", "BUGSEE_FRAME_PATTERN") == "1";
                // "bugsee_frame_row_order" = topdown | bottomup declares the readback row order (for measuring an unmeasured graphics API
                // together with the probe; a wrong value only affects where secure rectangles are masked).
                var rowOrder = ReadE2EValue("bugsee_frame_row_order", "BUGSEE_FRAME_ROW_ORDER");
                global::Bugsee.Capture.BugseeFrameCapturer.DefaultOptions = new global::Bugsee.Capture.BugseeFrameCapturerOptions
                {
                    LogOrientationProbe = pattern,
                    LogStats = true,
                    RowOrder = rowOrder == "topdown" ? global::Bugsee.Contracts.Capture.VideoFrameRowOrder.TopDown
                        : rowOrder == "bottomup" ? global::Bugsee.Contracts.Capture.VideoFrameRowOrder.BottomUp
                        : (global::Bugsee.Contracts.Capture.VideoFrameRowOrder?)null
                };
                var options = BuildLaunchOptions(_preset);
                LastLaunchOptions = options;
                // "bugsee_launch_thread" = 1 launches from a worker thread (checks that a launch from any thread works).
                if (ReadE2EValue("bugsee_launch_thread", "BUGSEE_LAUNCH_THREAD") == "1")
                {
                    var touched = Bugsee.IsLaunched; // first use of Bugsee initialises its statics, which must happen on the main thread
                    var launchOptions = options;
                    var token = appToken;
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try { Bugsee.Launch(token, launchOptions); Debug.Log("[BugseeSample] off-thread Launch returned"); }
                        catch (Exception ex) { Debug.LogError("[BugseeSample] off-thread Launch threw " + ex); }
                    });
                }
                else
                    Bugsee.Launch(appToken, options);
                // "bugsee_status_bar" = 1 shows the status bar, so Unity's view no longer fills the window.
                if (ReadE2EValue("bugsee_status_bar", "BUGSEE_STATUS_BAR") == "1")
                    Screen.fullScreen = false;
                // "bugsee_orient_after_s" = "12" rotates the activity to the other orientation after that many seconds (no sensor needed).
                var orientAfter = ReadE2EValue("bugsee_orient_after_s", "BUGSEE_ORIENT_AFTER_S");
                if (!string.IsNullOrEmpty(orientAfter))
                    StartCoroutine(RotateAfter(orientAfter));
                var reportAfter = ReadE2EValue("bugsee_report_after_s", "BUGSEE_REPORT_AFTER_S");
                if (!string.IsNullOrEmpty(reportAfter))
                    StartCoroutine(ReportAfter(reportAfter));
                if (ReadE2EValue("bugsee_frame_stats", "BUGSEE_FRAME_STATS") == "1")
                    gameObject.AddComponent<FrameTimeLogger>();
                if (_framePusherRequested || pattern)
                {
                    if (pattern)
                        gameObject.AddComponent<OrientationPatternOverlay>();
                    // "bugsee_secure_rect" = "l,t,r,b" as fractions of the screen (top-left origin); it is removed after
                    // "bugsee_secure_rect_remove_s" seconds when that is set; "bugsee_secure_rect_delay_s" (default 2) is the wait before adding it. For checking the mask in the recorded video.
                    var rect = ReadE2EValue("bugsee_secure_rect", "BUGSEE_SECURE_RECT"); // "l,t,r,b;l,t,r,b"
                    if (!string.IsNullOrEmpty(rect))
                        StartCoroutine(SecureRectDemo(rect, ReadE2EValue("bugsee_secure_rect_remove_s", "BUGSEE_SECURE_RECT_REMOVE_S"),
                            ReadE2EValue("bugsee_secure_rect_delay_s", "BUGSEE_SECURE_RECT_DELAY_S")));
                }
#elif UNITY_IOS && !UNITY_EDITOR
                var iosOptions = BuildLaunchOptions(_preset);
                LastLaunchOptions = iosOptions;
                // BUGSEE_E2E_RELAUNCH_FIRST = 1: Relaunch is the first call, with no Launch before it.
                if (Environment.GetEnvironmentVariable("BUGSEE_E2E_RELAUNCH_FIRST") == "1")
                    Bugsee.Relaunch(iosOptions);
                else
                    Bugsee.Launch(appToken, iosOptions);
#else
                // Editor / unsupported: still call Launch (no-op bridge).
                Bugsee.Launch(appToken);
#endif
                var severityScenario = ReadE2EValue("bugsee_e2e", "BUGSEE_E2E_SCENARIO");
                if (severityScenario == "severity" || severityScenario == "severitylabels")
                    Bugsee.SetReportHandler(new SeverityLoggingReportHandler());
                else if (installReportHandler)
                    Bugsee.SetReportHandler(new SampleReportHandler());
                if (installSampleFilters)
                    ApplyFilters(true);
                // "bugsee_handler_mode" = prompt | slow<seconds>: a report handler that records when it ran (issue #136 checks).
                var handlerMode = ReadE2EValue("bugsee_handler_mode", "BUGSEE_HANDLER_MODE");
                if (!string.IsNullOrEmpty(handlerMode))
                    HandlerDelayE2E.Install(this, handlerMode, ReadE2EValue("bugsee_handler_report_s", "BUGSEE_HANDLER_REPORT_S"));

                // Auto e2e matrix when endpoint override is set (device → local appserver).
                // Scenarios via Android intent extra "bugsee_e2e" (default: sig).
                //   handled | unhandled | crash | sig | pipeline | native | abort | fatal | java | hang | upload | blackout | none
                // Armed when an endpoint override is set or the extra is given explicitly.
                // Only the launch a running scenario makes itself arms nothing; the next one reads the environment again.
                var skipArming = _skipE2EArming;
                _skipE2EArming = false;
                var requestedScenario = skipArming ? "none" : ReadE2EValue("bugsee_e2e", "BUGSEE_E2E_SCENARIO");
                var launchRun = ReadE2EValue("bugsee_e2e_run", "BUGSEE_E2E_RUN");
                if (requestedScenario != null && !ConsumeE2ERun(launchRun))
                {
                    // Android restarts the activity after a crash with the SAME intent extras, so without this a
                    // killer scenario would fire again in every restarted process (a crash loop).
                    Debug.Log("[BugseeSample] E2E run " + launchRun + " already ran (restart after a crash): arming nothing");
                    requestedScenario = "none";
                }
                if (skipArming)
                {
                    // A relaunch from a scenario that is already running arms nothing.
                }
                else if (!string.IsNullOrWhiteSpace(endpointOverride) || requestedScenario != null)
                {
                    _e2eScenario = requestedScenario ?? "sig";
                    _e2eArmed = true;
                    _e2ePhase = 0;
                    _e2eTimer = 0f;
                    _e2eRunId = DateTime.UtcNow.ToString("HHmmss");
                    Debug.Log("[BugseeSample] E2E armed scenario=" + _e2eScenario + " runId=" + _e2eRunId + " run=" + (launchRun ?? "-"));
                }

                SetStatus("Launch ok (token set)");
            }
            catch (Exception ex)
            {
                SetStatus("Launch failed: " + ex.Message);
                Debug.LogException(ex);
            }
        }

        // Drive e2e from Update (not a coroutine). Fires are deferred to a single
        // call site (`PumpPendingE2EFire`) so identical throws share the same
        // Update() IL offset — worker error signatures hash cleaned frame traces.
        string _e2eScenario;
        bool _skipE2EArming;
        string _e2eRunId;
        bool _e2eArmed;
        int _e2ePhase;
        float _e2eTimer;
        string _pendingE2EFire;
        int _apmLaunchWaits;

        void Update()
        {
            if (!_e2eArmed) return;

            // Always pump from the same call site so A1/A2 stacks match.
            if (_pendingE2EFire != null)
            {
                var pending = _pendingE2EFire;
                _pendingE2EFire = null;
                PumpPendingE2EFire(pending);
                return;
            }

            _e2eTimer += Time.unscaledDeltaTime;

            switch (_e2eScenario)
            {
                case "handled":
                    if (_e2ePhase == 0 && _e2eTimer >= 5f)
                    {
                        _e2ePhase = 99;
                        _pendingE2EFire = "A:e2e-handled-" + _e2eRunId;
                    }
                    break;
                case "nested":
                    if (_e2ePhase == 0 && _e2eTimer >= 5f)
                    {
                        _e2ePhase = 99;
                        _pendingE2EFire = "E:e2e-nested-" + _e2eRunId;
                    }
                    break;
                case "unhandled":
                    if (_e2ePhase == 0 && _e2eTimer >= 5f)
                    {
                        _e2ePhase = 99;
                        _pendingE2EFire = "U:e2e-unhandled-" + _e2eRunId;
                    }
                    break;
                case "pipeline":
                    if (_e2ePhase == 0 && _e2eTimer >= 5f)
                    {
                        _e2ePhase = 99;
                        _pendingE2EFire = "P:e2e-pipeline-" + _e2eRunId;
                    }
                    break;
                case "crash":
                    if (_e2ePhase == 0 && _e2eTimer >= 5f)
                    {
                        _e2ePhase = 99;
                        _pendingE2EFire = "C:crash";
                    }
                    break;
                // Real process-killing failures, one per run, for the crash/symbolication matrix:
                //   native = SIGSEGV in libunity, abort = SIGABRT, fatal = Unity fatal error,
                //   java = uncaught RuntimeException on the Android main thread, hang = blocked main thread.
                case "native":
                case "abort":
                case "fatal":
                case "java":
                case "hang":
                    if (_e2ePhase == 0 && _e2eTimer >= 5f)
                    {
                        _e2ePhase = 99;
                        _pendingE2EFire = "K:" + _e2eScenario;
                    }
                    break;
                case "labels":
                case "logerror":
                case "sigraise":
                case "nullderef":
                case "attrsnoop":
                case "attrsedit":
                case "delete":
                case "netfilter":
                    if (_e2ePhase == 0 && _e2eTimer >= 5f)
                    {
                        _e2ePhase = 99;
                        _pendingE2EFire = "X:" + _e2eScenario + "-" + _e2eRunId;
                    }
                    break;
                case "interactions":
                    // Drives every action of the interaction hills (issue #116), once the SDK is launched.
                    if (_e2ePhase == 0 && _e2eTimer >= 5f && (Bugsee.IsLaunched || _e2eTimer >= 60f))
                    {
                        _e2ePhase = 99;
                        StartCoroutine(InteractionsE2E.Run(this));
                    }
                    break;
                case "deletescope":
                    // Deletes with every DeleteScope and checks the result and what is left on disk (issue #101).
                    if (_e2ePhase == 0 && _e2eTimer >= 5f && (Bugsee.IsLaunched || _e2eTimer >= 60f))
                    {
                        _e2ePhase = 99;
                        StartCoroutine(DeleteScopeE2E.Run(this));
                    }
                    break;
                case "filters":
                    // Turns the sample filters on and checks each one ran on events the sample sends (issue #117).
                    if (_e2ePhase == 0 && _e2eTimer >= 5f && (Bugsee.IsLaunched || _e2eTimer >= 60f))
                    {
                        _e2ePhase = 99;
                        StartCoroutine(FiltersE2E.Run(this));
                    }
                    break;
                case "presets":
                    // Relaunches with each launch preset and reads the launch options back (issue #118).
                    if (_e2ePhase == 0 && _e2eTimer >= 5f && (Bugsee.IsLaunched || _e2eTimer >= 60f))
                    {
                        _e2ePhase = 99;
                        StartCoroutine(PresetsE2E.Run(this));
                    }
                    break;
                case "actions":
                    // Drives the action-catalog items the bubbles call (issue #115), once the SDK is launched.
                    if (_e2ePhase == 0 && _e2eTimer >= 5f && (Bugsee.IsLaunched || _e2eTimer >= 60f))
                    {
                        _e2ePhase = 99;
                        if (Bugsee.IsLaunched)
                            ActionsE2E.Run(this);
                        else
                        {
                            Debug.Log("[Sample] actions FAIL not launched :: status=" + Bugsee.Status);
                            Debug.Log("[Sample] actions e2e done failures=1");
                        }
                    }
                    break;
                case "upload":
                    // A plain manual report: tells "no report is ever sent" from "only handled exceptions are not".
                    if (_e2ePhase == 0 && _e2eTimer >= 5f)
                    {
                        _e2ePhase = 99;
                        _pendingE2EFire = "L:e2e-upload-" + _e2eRunId;
                    }
                    break;
                case "severity":
                    // Reports created without a severity must take ReportingDefaultBugPriority (BUGSEE_E2E_BUG_PRIORITY,
                    // 1..5); an explicit severity must still win. SeverityLoggingReportHandler logs what each report got.
                    // The first call waits for the SDK to be launched (a call made while it is still Launching is ignored natively).
                    // The scenario ends on an open report dialog: dismiss it before the next run.
                    if (_e2ePhase == 0 && _e2eTimer >= 5f && Bugsee.IsLaunched)
                    {
                        _e2ePhase = 1;
                        _e2eTimer = 0f;
                        _pendingE2EFire = "V:omitted";
                    }
                    else if (_e2ePhase == 1 && _e2eTimer >= 4f)
                    {
                        _e2ePhase = 2;
                        _pendingE2EFire = "V:explicit";
                    }
                    else if (_e2ePhase == 2 && _e2eTimer >= 8f)
                    {
                        _e2ePhase = 99;
                        _pendingE2EFire = "V:dialog";
                    }
                    break;
                case "severitylabels":
                    // The same with labels and no severity: the native SDKs have no such overload on iOS.
                    if (_e2ePhase == 0 && _e2eTimer >= 5f && Bugsee.IsLaunched)
                    {
                        _e2ePhase = 1;
                        _e2eTimer = 0f;
                        _pendingE2EFire = "V:labelled-upload";
                    }
                    else if (_e2ePhase == 1 && _e2eTimer >= 4f)
                    {
                        _e2ePhase = 99;
                        _pendingE2EFire = "V:labelled-dialog";
                    }
                    break;
                case "apm":
                case "apmoff":
                case "apmleak":
                case "apmpool":
                case "apmthrnew":
                case "apmthrjava":
                case "apmthrui":
                    if (_e2ePhase == 0 && _e2eTimer >= 5f)
                    {
                        _e2ePhase = 99;
                        _pendingE2EFire = "M:" + _e2eRunId;
                    }
                    break;
                case "shapes":
                    // Throw shapes that change frame/IP pairing (see ShapesE2E).
                    if (_e2ePhase == 0 && _e2eTimer >= 5f)
                    {
                        _e2ePhase = 99;
                        _pendingE2EFire = "S:shapes";
                    }
                    break;
                case "logpath":
                    // FromUnityLog on the text Unity renders for a logged exception: its frames carry no address (issue #105).
                    if (_e2ePhase == 0 && _e2eTimer >= 5f)
                    {
                        _e2ePhase = 99;
                        _pendingE2EFire = "Y:e2e-logpath-" + _e2eRunId;
                    }
                    break;
                case "attach":
                case "unobserved":
                    if (_e2ePhase == 0 && _e2eTimer >= 5f)
                    {
                        _e2ePhase = 99;
                        _pendingE2EFire = (_e2eScenario == "attach" ? "T:" : "N:") + _e2eRunId;
                    }
                    break;
                case "options":
                    // Which keys of the platform gate (OptionPlatformGate) reach the native launch options.
                    if (_e2ePhase == 0 && _e2eTimer >= 5f)
                    {
                        _e2ePhase = 99;
                        _pendingE2EFire = "O:options";
                    }
                    break;
                case "blackout":
                    if (_e2ePhase == 0 && _e2eTimer >= 5f)
                    {
                        _e2ePhase = 99;
                        _pendingE2EFire = "D:blackout";
                    }
                    break;
                case "sigthreads":
                    if (_e2ePhase == 0 && _e2eTimer >= 5f)
                    {
                        _e2ePhase = 99;
                        _pendingE2EFire = "G:e2e-sigthreads-" + _e2eRunId;
                    }
                    break;
                case "none":
                    // Recovery launch: arm nothing, only let the SDK recover and upload what the last run left.
                    break;
                case "sig":
                default:
                    if (_e2ePhase == 0 && _e2eTimer >= 5f)
                    {
                        _e2ePhase = 1;
                        _e2eTimer = 0f;
                        _pendingE2EFire = "A:e2e-sig-a1-" + _e2eRunId;
                    }
                    else if (_e2ePhase == 1 && _e2eTimer >= 6f)
                    {
                        _e2ePhase = 2;
                        _e2eTimer = 0f;
                        _pendingE2EFire = "A:e2e-sig-a2-" + _e2eRunId;
                    }
                    else if (_e2ePhase == 2 && _e2eTimer >= 6f)
                    {
                        _e2ePhase = 99;
                        _pendingE2EFire = "B:e2e-sig-b1-" + _e2eRunId;
                    }
                    break;
            }
        }

        // Scenario "options": checks the platform gate (OptionPlatformGate) against the native launch options.
        // iOS keeps every key it is given, so there the check is real: every Android-only key (including the ones
        // Options has no constant for) is sent and must be absent from GetLaunchOptions, the iOS-only key present.
        // Android's GetLaunchOptions returns every registered key with its default, so presence proves nothing there:
        // only keys with type-correct values are sent, and each returned value is compared with what was sent
        // (a mismatch means the value did not reach the SDK). Android drops the iOS-only key itself, so its absence
        // there is not evidence for the gate.
        static readonly Dictionary<string, object> ProbeSent = new Dictionary<string, object>();

        static void Probe(BugseeLaunchOptions options, string key, object value)
        {
            ProbeSent[key] = value;
            options.SetCustomOption(key, value);
        }

        static void AddGatingProbeOptions(BugseeLaunchOptions options)
        {
#if UNITY_IOS
            foreach (var key in OptionPlatformGate.AndroidOnly)
                Probe(options, key, true);
#else
            Probe(options, Options.CaptureVideoSecureScrolling, true);
            Probe(options, Options.CaptureVideoFullscreenRememberUserDecision, true);
            Probe(options, Options.CaptureVideoFullscreenKeepRunning, true);
            Probe(options, Options.CaptureLogsUseAllSources, true);
            Probe(options, Options.CaptureRespectFlagSecure, false);
            Probe(options, Options.DetectAndReportExitUserRequested, true);
            Probe(options, Options.DetectAndReportExitOther, true);
            Probe(options, Options.ReportHandlerCallbackTimeout, 7000);
#endif
            Probe(options, Options.CaptureCrashHandlerType, 3);
        }

        static void LogGatingProbe()
        {
            var launched = Bugsee.GetLaunchOptions();
            var keys = new List<string>(OptionPlatformGate.AndroidOnly);
            keys.AddRange(OptionPlatformGate.IosOnly);
            keys.Sort(StringComparer.Ordinal);
            var present = 0;
            var mismatches = 0;
            foreach (var key in keys)
            {
                var has = launched != null && launched.ContainsKey(key);
                if (has) present++;
                var sent = ProbeSent.TryGetValue(key, out var sentValue) ? sentValue.ToString() : "-";
                var got = has ? Convert.ToString(launched[key]) : "absent";
#if UNITY_IOS
                // iOS: an Android-only key must be absent, the iOS-only key must come back as sent.
                var ok = key == Options.CaptureCrashHandlerType ? got == sent : !has;
#else
                var ok = key == Options.CaptureCrashHandlerType ? !has : sent == "-" || string.Equals(sent, got, StringComparison.OrdinalIgnoreCase);
#endif
                if (!ok) mismatches++;
                Debug.Log("[OPTIONS] " + key + " sent=" + sent + " native=" + got + (ok ? " ok" : " MISMATCH"));
            }
            Debug.Log("[OPTIONS] launched=" + Bugsee.IsLaunched + " total-native-keys=" + (launched == null ? -1 : launched.Count)
                + " gated-keys-present=" + present + "/" + keys.Count + " mismatches=" + mismatches);
        }

        void PumpPendingE2EFire(string pending)
        {
            if (string.IsNullOrEmpty(pending) || pending.Length < 2) return;
            // The SDK's state at the moment of the call: a report made while it is still Launching is retained, not sent.
            Debug.Log("[BugseeSample] sdk status=" + Bugsee.Status + " launched=" + Bugsee.IsLaunched + " fire=" + pending);
            var kind = pending[0];
            var marker = pending.Length > 2 ? pending.Substring(2) : pending;
            switch (kind)
            {
                case 'A':
                    FireHandledStableA(marker);
                    break;
                case 'B':
                    FireHandledStableB(marker);
                    SetStatus("E2E signature matrix sent (A,A,B)");
                    break;
                case 'E':
                    FireHandledNested(marker);
                    break;
                case 'U':
                    FireUnhandledStableA(marker);
                    break;
                case 'P':
                    FirePipelineLogException(marker);
                    break;
                case 'Y':
                    StartCoroutine(LogPathScenario(marker));
                    break;
                case 'O':
                    // GetLaunchOptions is only meaningful once the SDK is launched: wait (bounded), then say so if it never was.
                    if (!Bugsee.IsLaunched && _apmLaunchWaits++ < 120)
                    {
                        _e2ePhase = 0;
                        _e2eTimer = 4f;
                        break;
                    }
                    if (!Bugsee.IsLaunched)
                        Debug.Log("[OPTIONS] NOT LAUNCHED after waiting: no comparison made, status=" + Bugsee.Status);
                    else
                        LogGatingProbe();
                    break;
                case 'S':
                    ShapesE2E.Run(this);
                    break;
                case 'T':
                    AttachmentTaskE2E.RunAttach(this, marker);
                    break;
                case 'N':
                    AttachmentTaskE2E.RunUnobserved(this, marker);
                    break;
                case 'D':
                    BlackoutE2E.Run(this, appToken, LastLaunchOptions);
                    break;
                case 'G':
                    SignatureThreadsE2E.Run(this, marker);
                    break;
                case 'C':
                    SetStatus("E2E native TestCrash");
                    Bugsee.TestCrash();
                    break;
                case 'K':
                    FireProcessFailure(marker);
                    break;
                case 'M':
                    // Spans started before the SDK reaches Launched are inert wrapper spans, so wait for it.
                    if (!Bugsee.IsLaunched && _apmLaunchWaits++ < 120)
                    {
                        _e2ePhase = 0;
                        _e2eTimer = 4f;
                        _e2eRunId = marker;
                        break;
                    }
                    SetStatus("E2E APM " + marker);
                    Debug.Log("[ApmE2E] sdk status=" + Bugsee.Status + " waits=" + _apmLaunchWaits);
                    if (_e2eScenario.StartsWith("apmthr")) ApmE2E.Threads(marker, _e2eScenario);
                    else ApmE2E.Run(marker, _e2eScenario == "apmoff", _e2eScenario == "apmleak", _e2eScenario == "apmpool");
                    // The report bundle carries every transaction, which is what the backend check reads back.
                    if (_e2eScenario == "apm") Bugsee.Upload("apm-e2e-" + marker, "APM e2e run " + marker);
                    break;
                case 'X':
                    FireExtra(marker);
                    break;
                case 'V':
                    SetStatus("E2E severity " + marker);
                    if (marker == "omitted")
                        Bugsee.Upload("severity-omitted", "no severity given");
                    else if (marker == "explicit")
                        Bugsee.Upload("severity-explicit", "Blocker given", IssueSeverity.Blocker);
                    else if (marker == "labelled-upload")
                        Bugsee.Upload("severity-labelled-upload", "labels, no severity", labels: new List<string> { "sev", "labelled" });
                    else if (marker == "labelled-dialog")
                        Bugsee.ShowReportDialog("severity-labelled-dialog", "labels, no severity", labels: new List<string> { "sev", "labelled" });
                    else
                        Bugsee.ShowReportDialog("severity-dialog", "no severity given");
                    break;
                case 'L':
                    SetStatus("E2E manual upload " + marker);
                    Bugsee.Upload(marker, "uploaded by the e2e harness");
                    break;
            }
        }

        // BUGSEE_E2E_BUG_PRIORITY (1..5) sets the ReportingDefaultBugPriority launch option.
        static bool TryReadBugPriority(out IssueSeverity severity)
        {
            severity = IssueSeverity.High;
            if (!int.TryParse(ReadE2EValue("bugsee_bug_priority", "BUGSEE_E2E_BUG_PRIORITY"), out var level) || !Enum.IsDefined(typeof(IssueSeverity), level))
                return false;
            severity = (IssueSeverity)level;
            return true;
        }

#if UNITY_IOS && !UNITY_EDITOR
        [System.Runtime.InteropServices.DllImport("__Internal")]
        static extern int raise(int signal);
#endif

        // Device validation scenarios for the iOS 7.0.0-beta7 SDK: labels, Unity LogError, a raised signal, delete.
        void FireExtra(string marker)
        {
            SetStatus("E2E extra " + marker);
            if (marker.StartsWith("labels"))
            {
                try { throw new InvalidOperationException("E2E labels " + marker); }
                catch (Exception ex)
                {
                    Bugsee.LogException(ex, new Dictionary<string, object>
                    {
                        { "labels", new List<string> { "checkout", "beta7" } },
                        { "includeVideo", false },
                        { "mergingRules", new Dictionary<string, object> { { "k", 1 } } },
                        { "skipFrames", 1 },
                    });
                }
            }
            else if (marker.StartsWith("attrs"))
            {
                // Report-handler attribute kinds (see ReportAttributesE2E).
                ReportAttributesE2E.Run(this, marker.StartsWith("attrsedit"), _e2eRunId);
            }
            else if (marker.StartsWith("logerror"))
            {
                Debug.LogError("E2E LogError " + marker);
            }
            else if (marker.StartsWith("sigraise"))
            {
#if UNITY_IOS && !UNITY_EDITOR
                raise(11);
#endif
            }
            else if (marker.StartsWith("nullderef"))
            {
                // A raw write to an unmapped address: a hardware fault (EXC_BAD_ACCESS), not a raised signal.
                System.Runtime.InteropServices.Marshal.WriteInt32((IntPtr)16, 1);
            }
            else if (marker.StartsWith("delete"))
            {
                StartCoroutine(DeleteThenLaunch(marker));
            }
            else if (marker.StartsWith("netfilter"))
            {
                StartCoroutine(NetFilterE2E.Run());
            }
        }

        IEnumerator DeleteThenLaunch(string marker)
        {
            Debug.Log("[BugseeSample] delete: calling DeleteCollectedDataOnDevice, status=" + Bugsee.Status);
            Bugsee.DeleteCollectedDataOnDevice();
            yield return new WaitForSeconds(8f);
            Debug.Log("[BugseeSample] delete: after 8 s status=" + Bugsee.Status + " launched=" + Bugsee.IsLaunched);
            // The relaunch reads the scenario again; without a run id it would arm `delete` once more.
            _e2eArmed = false;
            _e2eScenario = "none";
            _skipE2EArming = true;
            LaunchSdk();
            yield return new WaitForSeconds(8f);
            Debug.Log("[BugseeSample] delete: relaunched status=" + Bugsee.Status + " launched=" + Bugsee.IsLaunched);
            Bugsee.Upload("e2e-after-delete-" + marker, "report after the delete and a launch");
        }

        void FireProcessFailure(string scenario)
        {
            SetStatus("E2E process failure: " + scenario);
            switch (scenario)
            {
                case "native":
                    UnityEngine.Diagnostics.Utils.ForceCrash(UnityEngine.Diagnostics.ForcedCrashCategory.AccessViolation);
                    break;
                case "abort":
                    UnityEngine.Diagnostics.Utils.ForceCrash(UnityEngine.Diagnostics.ForcedCrashCategory.Abort);
                    break;
                case "fatal":
                    UnityEngine.Diagnostics.Utils.ForceCrash(UnityEngine.Diagnostics.ForcedCrashCategory.FatalError);
                    break;
                case "java":
                    Bugsee.TestCrash();
                    break;
                case "hang":
                    // The SDK's hang/ANR detection watches the Android MAIN looper. This code runs on Unity's own
                    // thread (UnityMain), so sleeping here would stall the game but never be seen as a hang.
                    // Block the Android UI thread instead; longer than the SDK's hang threshold. That can also raise
                    // an ANR, and the process may then be killed: the run script does not judge liveness for hang.
#if UNITY_ANDROID && !UNITY_EDITOR
                    using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                    using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                    {
                        activity.Call("runOnUiThread", new AndroidJavaRunnable(() => System.Threading.Thread.Sleep(20000)));
                    }
#else
                    System.Threading.Thread.Sleep(20000);
#endif
                    break;
            }
        }

        // "bugsee_report_after_s" = "7,14": uploads a manual report after each delay, so the recorded video can be checked later.
        IEnumerator ReportAfter(string secondsList)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            float previous = 0f;
            foreach (var item in secondsList.Split(','))
            {
                if (!float.TryParse(item, System.Globalization.NumberStyles.Float, inv, out var at)) continue;
                yield return new WaitForSeconds(Mathf.Max(0f, at - previous));
                previous = at;
                Bugsee.Upload("frame-pusher-check-" + item, "manual report at t+" + item + "s");
                Debug.Log("[BugseeSample] t=" + Time.realtimeSinceStartup.ToString("F2", inv) + " report uploaded");
            }
        }

        IEnumerator RotateAfter(string seconds)
        {
            if (!float.TryParse(seconds, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var at)) yield break;
            yield return new WaitForSeconds(at);
            // To the other orientation than the one it is in now.
            var toPortrait = Screen.width > Screen.height;
            Screen.orientation = toPortrait ? ScreenOrientation.Portrait : ScreenOrientation.LandscapeLeft;
            Debug.Log("[BugseeSample] t=" + Time.realtimeSinceStartup.ToString("F2") + (toPortrait ? " rotated to portrait" : " rotated to landscape"));
        }

        IEnumerator SecureRectDemo(string fractionsList, string removeAfterSeconds, string delaySeconds)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            float.TryParse(delaySeconds, System.Globalization.NumberStyles.Float, inv, out var delay);
            yield return new WaitForSeconds(delay > 0f ? delay : 2f);
            var added = new List<RectInt>();
            foreach (var fractions in fractionsList.Split(';'))
            {
                // "px:l,t,r,b" is absolute window pixels, else fractions of the Unity screen.
                bool absolute = fractions.StartsWith("px:");
                var p = (absolute ? fractions.Substring(3) : fractions).Split(',');
                if (p.Length != 4) continue;
                float l = float.Parse(p[0], inv), t = float.Parse(p[1], inv), r = float.Parse(p[2], inv), b = float.Parse(p[3], inv);
                var rect = absolute
                    ? new RectInt((int)l, (int)t, (int)(r - l), (int)(b - t))
                    : new RectInt((int)(l * Screen.width), (int)(t * Screen.height), (int)((r - l) * Screen.width), (int)((b - t) * Screen.height));
                Bugsee.AddSecureRectangle(rect);
                added.Add(rect);
                Debug.Log("[BugseeSample] t=" + Time.realtimeSinceStartup.ToString("F2", inv) + " secure rect added " + rect + " screen=" + Screen.width + "x" + Screen.height);
            }
            if (float.TryParse(removeAfterSeconds, System.Globalization.NumberStyles.Float, inv, out var seconds))
            {
                yield return new WaitForSeconds(seconds);
                foreach (var rect in added)
                    Bugsee.RemoveSecureRectangle(rect);
                Debug.Log("[BugseeSample] t=" + Time.realtimeSinceStartup.ToString("F2", inv) + " secure rect removed");
            }
        }

        // An intent extra (Android) or environment variable (iOS), trimmed and lower-cased, or null when there is
        // none. "bugsee_e2e" / BUGSEE_E2E_SCENARIO names the scenario; "bugsee_e2e_run" / BUGSEE_E2E_RUN is a
        // unique id per launch from the run script.
        static string ReadE2EValue(string androidExtra, string iosVariable)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var intent = activity.Call<AndroidJavaObject>("getIntent"))
                {
                    var value = intent?.Call<string>("getStringExtra", androidExtra);
                    if (!string.IsNullOrWhiteSpace(value))
                        return value.Trim().ToLowerInvariant();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[BugseeSample] " + androidExtra + " intent read failed: " + ex.Message);
            }
#endif
#if UNITY_IOS && !UNITY_EDITOR
            // iOS has no intent extras: `SIMCTL_CHILD_BUGSEE_E2E_SCENARIO=native xcrun simctl launch ...` (or the
            // scheme's environment variables on a device run) reaches the app as BUGSEE_E2E_SCENARIO.
            var fromEnvironment = Environment.GetEnvironmentVariable(iosVariable);
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
                return fromEnvironment.Trim().ToLowerInvariant();
#endif
            return null;
        }

        // Records the launch's run id as done BEFORE its scenario fires, in a file written synchronously so it
        // survives the crash. False when this run id already ran, i.e. this process is the OS restarting the
        // activity with the same extras. A launch without a run id (a manual `am start`) always fires, and is not
        // protected against that restart.
        static bool ConsumeE2ERun(string run)
        {
            if (string.IsNullOrEmpty(run)) return true;
            var path = System.IO.Path.Combine(Application.persistentDataPath, "bugsee_e2e_last_run");
            try
            {
                if (System.IO.File.Exists(path) && System.IO.File.ReadAllText(path).Trim() == run)
                    return false;
                System.IO.File.WriteAllText(path, run);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[BugseeSample] e2e run marker failed: " + ex.Message);
            }
            return true;
        }

        // Dedicated throw sites so stack frames (hence worker signatures) stay stable.
        void FireHandledStableA(string marker)
        {
            try { E2EThrowStableA(marker); }
            catch (Exception ex)
            {
                var payload = ManagedExceptionPayload.FromException(ex);
                var clientSig = payload != null ? payload.signature : "";
                Debug.Log("[BugseeSample] clientSig(A)=" + clientSig + " marker=" + marker);
                Bugsee.LogException(ex, new Dictionary<string, object>
                {
                    { "source", "e2e-sig-A" },
                    { "marker", marker },
                    { "clientSig", clientSig },
                });
                SetStatus("E2E handled StableA " + marker);
            }
        }

        void FireHandledStableB(string marker)
        {
            try { E2EThrowStableB(marker); }
            catch (Exception ex)
            {
                var payload = ManagedExceptionPayload.FromException(ex);
                var clientSig = payload != null ? payload.signature : "";
                Debug.Log("[BugseeSample] clientSig(B)=" + clientSig + " marker=" + marker);
                Bugsee.LogException(ex, new Dictionary<string, object>
                {
                    { "source", "e2e-sig-B" },
                    { "marker", marker },
                    { "clientSig", clientSig },
                });
                SetStatus("E2E handled StableB " + marker);
            }
        }

        void FireUnhandledStableA(string marker)
        {
            try { E2EThrowStableA(marker); }
            catch (Exception ex)
            {
                Bugsee.LogUnhandledException(ex, new Dictionary<string, object>
                {
                    { "source", "e2e-unhandled" },
                    { "marker", marker },
                });
                SetStatus("E2E unhandled StableA " + marker);
            }
        }

        // Issue #105: ManagedExceptionPayload.FromUnityLog, the payload built when Unity's text is the only copy of an
        // exception (a normal Debug.LogException or uncaught exception takes the throw path instead, whose text copy
        // is dropped as an echo). It is fed the text Unity really rendered for a logged exception, captured from
        // logMessageReceivedThreaded, and synthetic frames with every kind of hex a log line can hold. Logs
        // [Sample] logpath PASS|FAIL; the throw path's payload and image are logged for comparison, tagged [LOGPATH].
        IEnumerator LogPathScenario(string marker)
        {
            var failures = 0;
            Action<string, bool, string> check = (name, ok, detail) =>
            {
                if (!ok) failures++;
                Debug.Log("[Sample] logpath " + (ok ? "PASS " : "FAIL ") + name + " :: " + detail);
            };
            string text = null, condition = null;
            Application.LogCallback handler = (c, stackTrace, type) =>
            {
                if (type != LogType.Exception || text != null || c == null || c.IndexOf(marker, StringComparison.Ordinal) < 0) return;
                condition = c;
                text = stackTrace ?? "";
            };
            Application.logMessageReceivedThreaded += handler;
            try
            {
                var thrown = new InvalidOperationException("E2E LogPath " + marker);
                try { E2EThrowStableA(marker + "-x"); } catch (Exception) { }
                try { throw thrown; } catch (Exception) { }
                var native = Il2CppNativeStack.TryCapture(thrown);
                Debug.Log("[LOGPATH] throwPath image=" + (native?.ImageName ?? "none") + " uuid=" + (native?.ImageUuid ?? "none")
                    + " json=" + ManagedExceptionPayload.ToJson(ManagedExceptionPayload.FromException(thrown)));
                yield return null;
                Debug.LogException(thrown);
                for (var i = 0; i < 120 && text == null; i++) yield return null;
            }
            finally
            {
                Application.logMessageReceivedThreaded -= handler;
            }

            var expectModule = Il2CppNativeStack.IsSupported;
            if (text == null)
            {
                check("Unity text received", false, "no LogType.Exception callback within 120 frames");
            }
            else
            {
                Debug.Log("[LOGPATH] text=" + text.Replace("\n", " | "));
                CheckLogPathPayload(check, "unity text", ManagedExceptionPayload.FromUnityLog(condition, text), expectModule);
            }
            var synthetic = string.Join("\n", new[]
            {
                "Ns.C.Run () (at /proj/Assets/C.cs:12)",
                "Ns.C.Run () [0x00023] in <0123456789abcdef0123456789abcdef>:0",
                "Ns.C.Run () <0x104a125e0 + 0x34> in <0123456789abcdef0123456789abcdef>:0",
                "#00 pc 000000000012abcd /data/app/lib/arm64/libil2cpp.so",
                "#01 pc 00000000004f3a10 /data/app/lib/arm64/libunity.so",
                "0x0000000104a125e0 in UnityFramework",
            });
            CheckLogPathPayload(check, "synthetic hex", ManagedExceptionPayload.FromUnityLog("System.Exception: " + marker, synthetic), expectModule);
            Debug.Log("[Sample] logpath e2e done failures=" + failures);
            SetStatus("E2E logpath failures=" + failures);
        }

        static void CheckLogPathPayload(Action<string, bool, string> check, string label, ManagedExceptionPayload.Payload payload, bool expectModule)
        {
            var frames = payload?.frames ?? new ManagedExceptionPayload.Frame[0];
            var withAddress = 0;
            foreach (var f in frames) if (!string.IsNullOrEmpty(f.address)) withAddress++;
            check(label + ": frames present", frames.Length > 0, frames.Length + " frames");
            check(label + ": no frame carries an address", withAddress == 0, withAddress + " with an address");
            check(label + ": moduleUUID", !expectModule || !string.IsNullOrEmpty(payload?.moduleUUID), payload?.moduleUUID ?? "null");
        }

        // A three-level InnerException chain (issue #104): the payload's `cause` nesting on a device. The JSON logged here is
        // what the bridge sends as the exception's reason.
        void FireHandledNested(string marker)
        {
            try { E2EThrowNested(marker); }
            catch (Exception ex)
            {
                var payload = ManagedExceptionPayload.FromException(ex);
                var depth = 0;
                for (var p = payload; p != null; p = p.cause)
                    Debug.Log("[BugseeSample] nested level " + depth++ + ": " + p.name + " | " + p.reason + " | frames=" + p.frames.Length);
                var json = ManagedExceptionPayload.ToJson(payload);
                Debug.Log("[BugseeSample] nested levels=" + depth + " json=" + json);
                var causes = json.Split(new[] { "\"cause\":" }, StringSplitOptions.None).Length - 1;
                var ok = depth == 3 && causes == 2;
                Debug.Log("[Sample] nested " + (ok ? "PASS" : "FAIL") + " levels=" + depth + " causes=" + causes);
                Debug.Log("[Sample] nested e2e done failures=" + (ok ? 0 : 1));
                Bugsee.LogException(ex, new Dictionary<string, object> { { "source", "e2e-nested" }, { "marker", marker } });
                SetStatus("E2E handled nested " + marker);
            }
        }

        static void E2EThrowNested(string marker)
        {
            try { E2EThrowMiddle(marker); }
            catch (Exception ex) { throw new InvalidOperationException("E2E nested outer " + marker, ex); }
        }

        static void E2EThrowMiddle(string marker)
        {
            try { throw new ArgumentException("E2E nested inner " + marker); }
            catch (Exception ex) { throw new ApplicationException("E2E nested middle " + marker, ex); }
        }

        void FirePipelineLogException(string marker)
        {
            try { E2EThrowStableA(marker); }
            catch (Exception ex)
            {
                // Routes through ExceptionPipeline ILogHandler (handled unless TreatFatal).
                Debug.LogException(ex);
                SetStatus("E2E pipeline LogException " + marker);
            }
        }

        static void E2EThrowStableA(string marker)
        {
            throw new InvalidOperationException("E2E StableA " + marker);
        }

        static void E2EThrowStableB(string marker)
        {
            throw new ArgumentException("E2E StableB " + marker);
        }

        /// <summary>
        /// The sample's launch options (Android and iOS; null in the Editor) with the preset on top. The iOS crash handler goes through
        /// the typed <see cref="IOSLaunchOptions.CrashHandlerType"/>, and BUGSEE_E2E_CRASH_HANDLER = 1 (BSD) | 2 (Mach) | 3 (Mach + BSD)
        /// overrides the preset's value.
        /// </summary>
        BugseeLaunchOptions BuildLaunchOptions(string preset)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            var options = new AndroidLaunchOptions
            {
                CaptureVideo = true,
                CaptureVideoMode = _framePusherRequested ? VideoMode.DirectBuffers : VideoMode.V2,
                CaptureVideoFrameRate = FrameRate.High,
                CaptureLogs = true,
                CaptureBreadcrumbs = true,
                CaptureNetwork = true,
                ReportingTriggerByShake = true,
                ReportingTriggerByScreenshot = true,
                DetectAndReportCrash = true,
                CaptureManagedExceptions = true,
                // "bugsee_unobserved" = 0 turns off reporting of unobserved task exceptions (scenario "unobserved").
                CaptureUnobservedTaskExceptions = ReadE2EValue("bugsee_unobserved", "BUGSEE_E2E_UNOBSERVED") != "0",
                DetectAndReportHang = true,
                DetectAndReportExitNotResponding = true,
                DetectAndReportExitLowMemory = true,
                PerformanceMonitoring = ReadE2EValue("bugsee_e2e", "BUGSEE_E2E_SCENARIO") != "apmoff",
                PerformanceSampleRate = ReadE2EValue("bugsee_e2e", "BUGSEE_E2E_SCENARIO") == "apmleak" || ReadE2EValue("bugsee_e2e", "BUGSEE_E2E_SCENARIO") == "apmpool" || (ReadE2EValue("bugsee_e2e", "BUGSEE_E2E_SCENARIO") ?? "").StartsWith("apmthr") ? 0f : 1f,
                Debug = true
            };
            LaunchPresets.Apply(preset, options);
            if (TryReadBugPriority(out var bugPriority))
                options.ReportingDefaultBugPriority = bugPriority;
            if (!string.IsNullOrWhiteSpace(endpointOverride))
                options.Endpoint = endpointOverride.Trim();
            if (ReadE2EValue("bugsee_e2e", "BUGSEE_E2E_SCENARIO") == "options")
                AddGatingProbeOptions(options);
            return options;
#elif UNITY_IOS && !UNITY_EDITOR
            var options = new IOSLaunchOptions
            {
                // The iOS Simulator crashes the SDK's Unity video capture (it reads a private "iosurface" key the
                // simulator's texture lacks), so a simulator run sets BUGSEE_E2E_VIDEO=0.
                CaptureVideo = Environment.GetEnvironmentVariable("BUGSEE_E2E_VIDEO") != "0",
                CaptureLogs = true,
                CaptureBreadcrumbs = true,
                CaptureNetwork = true,
                ReportingTriggerByShake = true,
                DetectAndReportCrash = true,
                CaptureManagedExceptions = true,
                // "bugsee_unobserved" = 0 turns off reporting of unobserved task exceptions (scenario "unobserved").
                CaptureUnobservedTaskExceptions = ReadE2EValue("bugsee_unobserved", "BUGSEE_E2E_UNOBSERVED") != "0",
                DetectAndReportHang = true,
                Debug = true
            };
            LaunchPresets.Apply(preset, options);
            if (TryReadBugPriority(out var bugPriority))
                options.ReportingDefaultBugPriority = bugPriority;
            if (!string.IsNullOrWhiteSpace(endpointOverride))
                options.Endpoint = endpointOverride.Trim();
            var crashHandler = Environment.GetEnvironmentVariable("BUGSEE_E2E_CRASH_HANDLER");
            if (int.TryParse(crashHandler, out var handlerType)) // any integer goes to the SDK as given, also an invalid one
                options.CrashHandlerType = (CrashHandlerType)handlerType;
            if (Environment.GetEnvironmentVariable("BUGSEE_E2E_SCENARIO") == "options")
                AddGatingProbeOptions(options);
            return options;
#else
            return null;
#endif
        }

        /// <summary>
        /// Relaunches with the options of <paramref name="preset"/>. The preset and its options are remembered for the next
        /// Launch/Relaunch only once the SDK accepted the relaunch (a coroutine on the main thread, a frame after the answer).
        /// Returns the SDK's answer.
        /// </summary>
        public System.Threading.Tasks.Task<bool> RelaunchWithPreset(string preset)
        {
            preset = LaunchPresets.Resolve(preset);
            try
            {
                var options = BuildLaunchOptions(preset);
                var task = Bugsee.RelaunchAsync(options);
                LastRelaunch = task;
                StartCoroutine(CommitPreset(task, preset, options, ++_relaunchSeq));
                SetStatus("Relaunch preset " + preset);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                SetStatus("Relaunch preset failed: " + ex.Message);
                LastRelaunch = System.Threading.Tasks.Task.FromResult(false);
            }
            return LastRelaunch;
        }

        int _relaunchSeq;

        // Only the newest relaunch request commits: an older one that is answered later was replaced by it.
        IEnumerator CommitPreset(System.Threading.Tasks.Task<bool> task, string preset, BugseeLaunchOptions options, int seq)
        {
            while (!task.IsCompleted) yield return null;
            if (task.IsFaulted || !task.Result || options == null || seq != _relaunchSeq) yield break;
            _preset = preset;
            LastLaunchOptions = options;
            _optionsLineAt = -10f;
        }

        /// <summary>
        /// Relaunches with the options of the preset in use (through <see cref="RelaunchWithPreset"/>, so it is ordered with it). A native relaunch that gets only a few options may not keep the rest
        /// (a relaunch replaces the whole option set, and without options resets it to the defaults), so the whole set is passed again.
        /// </summary>
        public void RelaunchSdk() => RelaunchWithPreset(_preset);

        public void ApplyFilters(bool enabled)
        {
            SampleFilters.NetworkRedactEnabled = enabled;
            SampleFilters.LogDropDebugEnabled = enabled;
            SampleFilters.BreadcrumbDropEnabled = enabled;
            Bugsee.SetNetworkEventFilter(enabled ? SampleFilters.NetworkFilter : null);
            Bugsee.SetLogEventFilter(enabled ? SampleFilters.LogFilter : null);
            Bugsee.SetBreadcrumbFilter(enabled ? SampleFilters.BreadcrumbFilter : null);
            SetStatus((enabled ? "Sample filters ON" : "Sample filters OFF") + " (" + SampleFilters.Summary + ")");
        }

        void OnLifecycle(string eventType, object data)
        {
            _lastLifecycle = eventType;
            Debug.Log("[BugseeSample] lifecycle " + eventType + " data=" + (data == null ? "null" : data.GetType().Name + ":" + data));
            var line = DateTime.Now.ToString("HH:mm:ss") + " " + eventType +
                       (data != null ? " data=" + data : "");
            _lifecycleLog.Enqueue(line);
            while (_lifecycleLog.Count > MaxLifecycleEntries)
                _lifecycleLog.Dequeue();
        }
    }
}
