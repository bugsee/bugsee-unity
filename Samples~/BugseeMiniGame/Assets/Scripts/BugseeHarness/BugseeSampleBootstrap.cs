using System;
using System.Collections.Generic;
using Bugsee.Contracts.Options;
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
        public void Configure(string token, bool launchOnStart)
        {
            appToken = token ?? "";
            this.launchOnStart = launchOnStart;
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
                Bugsee.Launch(appToken, iosOptions);
#else
                // Editor / unsupported: still call Launch (no-op bridge).
                Bugsee.Launch(appToken);
#endif
                if (installReportHandler)
                    Bugsee.SetReportHandler(new SampleReportHandler());
                if (installSampleFilters)
                    ApplyFilters(true);

                SetStatus("Launch ok (token set)");
            }
            catch (Exception ex)
            {
                SetStatus("Launch failed: " + ex.Message);
                Debug.LogException(ex);
            }
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
