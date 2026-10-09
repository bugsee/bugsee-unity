using System;
using System.Collections;
using System.Collections.Generic;
using Bugsee.Contracts.Options;
using Bugsee.Internal;
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

        /// <summary>Called from <see cref="MiniGameBootstrap"/> when building the scene at runtime.</summary>
        public void Configure(string token, bool launchOnStart, string endpoint = null)
        {
            appToken = token ?? "";
            this.launchOnStart = launchOnStart;
            if (!string.IsNullOrWhiteSpace(endpoint))
                endpointOverride = endpoint.Trim();
        }

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
#if UNITY_ANDROID && !UNITY_EDITOR
                var options = new AndroidLaunchOptions
                {
                    CaptureVideo = true,
                    CaptureVideoMode = VideoMode.DirectBuffers,
                    CaptureVideoFrameRate = FrameRate.High,
                    CaptureLogs = true,
                    CaptureNetwork = true,
                    ReportingTriggerByShake = true,
                    ReportingTriggerByScreenshot = true,
                    DetectAndReportCrash = true,
                    CaptureManagedExceptions = true,
                    DetectAndReportHang = true,
                    DetectAndReportExitNotResponding = true,
                    DetectAndReportExitLowMemory = true,
                    Debug = true
                };
                if (!string.IsNullOrWhiteSpace(endpointOverride))
                    options.Endpoint = endpointOverride.Trim();
                Bugsee.Launch(appToken, options);
#elif UNITY_IOS && !UNITY_EDITOR
                var iosOptions = new IOSLaunchOptions
                {
                    CaptureVideo = true,
                    CaptureLogs = true,
                    CaptureNetwork = true,
                    ReportingTriggerByShake = true,
                    DetectAndReportCrash = true,
                    CaptureManagedExceptions = true,
                    DetectAndReportHang = true,
                    Debug = true
                };
                if (!string.IsNullOrWhiteSpace(endpointOverride))
                    iosOptions.Endpoint = endpointOverride.Trim();
                Bugsee.Launch(appToken, iosOptions);
#else
                // Editor / unsupported: still call Launch (no-op bridge).
                Bugsee.Launch(appToken);
#endif
                if (installReportHandler)
                    Bugsee.SetReportHandler(new SampleReportHandler());
                if (installSampleFilters)
                    ApplyFilters(true);

                // Auto e2e matrix when endpoint override is set (device → local appserver).
                // Scenarios via Android intent extra "bugsee_e2e" (default: sig).
                //   handled | unhandled | crash | sig | pipeline
                if (!string.IsNullOrWhiteSpace(endpointOverride))
                {
                    _e2eScenario = ReadAndroidE2EScenario();
                    _e2eArmed = true;
                    _e2ePhase = 0;
                    _e2eTimer = 0f;
                    _e2eRunId = DateTime.UtcNow.ToString("HHmmss");
                    Debug.Log("[BugseeSample] E2E armed scenario=" + _e2eScenario + " runId=" + _e2eRunId);
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
        string _e2eRunId;
        bool _e2eArmed;
        int _e2ePhase;
        float _e2eTimer;
        string _pendingE2EFire;

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

        void PumpPendingE2EFire(string pending)
        {
            if (string.IsNullOrEmpty(pending) || pending.Length < 2) return;
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
                case 'U':
                    FireUnhandledStableA(marker);
                    break;
                case 'P':
                    FirePipelineLogException(marker);
                    break;
                case 'C':
                    SetStatus("E2E native TestCrash");
                    Bugsee.TestCrash();
                    break;
            }
        }

        static string ReadAndroidE2EScenario()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var intent = activity.Call<AndroidJavaObject>("getIntent"))
                {
                    var scenario = intent?.Call<string>("getStringExtra", "bugsee_e2e");
                    if (!string.IsNullOrWhiteSpace(scenario))
                        return scenario.Trim().ToLowerInvariant();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[BugseeSample] bugsee_e2e intent read failed: " + ex.Message);
            }
#endif
            return "sig";
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

        public void RelaunchSdk()
        {
            try
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                Bugsee.Relaunch(new AndroidLaunchOptions
                {
                    CaptureVideo = true,
                    CaptureVideoMode = VideoMode.DirectBuffers
                });
#else
                Bugsee.Relaunch();
#endif
                SetStatus("Relaunch");
            }
            catch (Exception ex)
            {
                SetStatus("Relaunch failed: " + ex.Message);
            }
        }

        public void ApplyFilters(bool enabled)
        {
            SampleFilters.NetworkRedactEnabled = enabled;
            SampleFilters.LogDropDebugEnabled = enabled;
            SampleFilters.BreadcrumbDropEnabled = enabled;
            Bugsee.SetNetworkEventFilter(enabled ? SampleFilters.NetworkFilter : null);
            Bugsee.SetLogEventFilter(enabled ? SampleFilters.LogFilter : null);
            Bugsee.SetBreadcrumbFilter(enabled ? SampleFilters.BreadcrumbFilter : null);
            SetStatus(enabled ? "Sample filters ON" : "Sample filters OFF");
        }

        void OnLifecycle(string eventType, object data)
        {
            _lastLifecycle = eventType;
            var line = DateTime.Now.ToString("HH:mm:ss") + " " + eventType +
                       (data != null ? " data=" + data : "");
            _lifecycleLog.Enqueue(line);
            while (_lifecycleLog.Count > MaxLifecycleEntries)
                _lifecycleLog.Dequeue();
        }
    }
}
