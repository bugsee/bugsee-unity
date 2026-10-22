using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Bugsee.Contracts.Exchange;
using Bugsee.Contracts.Feedback;
using Bugsee.Contracts.Lifecycle;
using Bugsee.Contracts.Options;
using Bugsee.Contracts.Reporting;
using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>Named demo actions shared by HUD buttons and in-world totems.</summary>
    public enum BugseeDemoAction
    {
        ShowReportDialog,
        Upload,
        ToggleBlackout,
        LogBundle,
        LogAllLevels,
        LogException,
        TestCrash,
        SetSecureRect,
        ClearSecureRects,
        ShowFeedback,
        FeedbackGreeting,
        ToggleFeedbackListener,
        SetUserIdentifier,
        ClearIdentity,
        ToggleIdentity,
        GetIdentity,
        SetAttribute,
        GetAttribute,
        ClearAttribute,
        ClearAllAttributes,
        CaptureViewHierarchy,
        ResetVideoPermission,
        ToggleFilters,
        ToggleReportHandler,
        ToggleLifecycleListener,
        Appearance,
        StatusReadout,
        ObsoletePauseResume,
        Launch,
        Relaunch,
        Stop,
        Breadcrumb,
        NetworkEvent,
        ReportCreate,
        ReportEdit,
        ReportUpload,
        ReportDiscard,
        Notify,
        ApmStart,
        ApmSpan,
        ApmFinish,
        LaunchAsync,
        RelaunchAsync,
        StopAsync,
        LaunchOptions,
        DeleteData,
        LogUnhandled,
        RemoveSecureRect,
        AppearanceTexts
    }

    /// <summary>Centralizes Bugsee facade calls so HUD and stations stay in sync.</summary>
    public sealed class BugseeActionCatalog
    {
        public static BugseeActionCatalog Instance { get; private set; }

        readonly BugseeSampleBootstrap _bootstrap;
        bool _secureRectActive;
        RectInt _secureRect = new RectInt(40, 40, 220, 120);
        bool _filtersOn;
        bool _reportHandlerOn;
        bool _lifecycleListenerOn;
        bool _feedbackListenerOn;
        SampleLifecycleListener _lifecycleListener;
        int _exceptionCount;

        /// <summary>Attribute key used by Set/Get/Clear Attr (form, bubbles, debug panel); the last key set wins.</summary>
        public const string DefaultAttributeKey = "sample.key";
        public static string AttributeKey { get; set; } = DefaultAttributeKey;

        public BugseeActionCatalog(BugseeSampleBootstrap bootstrap)
        {
            _bootstrap = bootstrap;
            Interactions = new BugseeInteractions(bootstrap);
            Instance = this;
        }

        /// <summary>Report drafts, Notify, APM, async lifecycle and the other actions of the later hills.</summary>
        public BugseeInteractions Interactions { get; }

        internal static void ClearInstance(BugseeActionCatalog catalog)
        {
            if (ReferenceEquals(Instance, catalog))
                Instance = null;
        }

        /// <summary>The last rect Set Secure Rect added and Remove Rect has not removed yet.</summary>
        public bool SecureRectActive => _secureRectActive;
        public RectInt SecureRect => _secureRect;

        public string LastStatus => _bootstrap != null ? _bootstrap.LastStatus : "";

        /// <summary>Keeps the last applied secure rect so Clear/Remove stay consistent with the form.</summary>
        public void RememberSecureRect(RectInt rect, bool active)
        {
            _secureRect = rect;
            _secureRectActive = active;
        }

        public void Run(BugseeDemoAction action)
        {
            try
            {
                switch (action)
                {
                    case BugseeDemoAction.ShowReportDialog:
                        Bugsee.ShowReportDialog(
                            "Field report",
                            "Triggered from anteater fields sample.",
                            IssueSeverity.High,
                            new List<string> { "sample", "labyrinth" });
                        _bootstrap.SetStatus("ShowReportDialog");
                        break;

                    case BugseeDemoAction.Upload:
                        Bugsee.Upload(
                            "Field silent upload",
                            "Uploaded from sample without UI.",
                            IssueSeverity.Medium,
                            new List<string> { "sample", "upload" });
                        _bootstrap.SetStatus("Upload");
                        break;

                    case BugseeDemoAction.ToggleBlackout:
                        if (Bugsee.IsBlackout)
                        {
                            Bugsee.EndBlackout();
                            DayNightController.Instance?.SetNight(false);
                            _bootstrap.SetStatus("EndBlackout → day");
                        }
                        else
                        {
                            Bugsee.StartBlackout();
                            DayNightController.Instance?.SetNight(true);
                            _bootstrap.SetStatus("StartBlackout → night");
                        }
                        break;

                    case BugseeDemoAction.LogBundle:
                        Bugsee.Log("Field log @ Info", LogLevel.Info);
                        Bugsee.Log("Field log @ Warning", LogLevel.Warning);
                        Bugsee.Trace("field.score", _bootstrap.Score);
                        Bugsee.Event("field.ping", new Dictionary<string, object>
                        {
                            { "score", _bootstrap.Score },
                            { "source", "catalog" }
                        });
                        _bootstrap.SetStatus("Log + Trace + Event");
                        break;

                    case BugseeDemoAction.LogAllLevels:
                        Bugsee.Log("level Error", LogLevel.Error);
                        Bugsee.Log("level Warning", LogLevel.Warning);
                        Bugsee.Log("level Info", LogLevel.Info);
                        Bugsee.Log("level Debug", LogLevel.Debug);
                        Bugsee.Log("level Verbose", LogLevel.Verbose);
                        _bootstrap.SetStatus("Log all LogLevels");
                        break;

                    case BugseeDemoAction.LogException:
                    {
                        var n = ++_exceptionCount;
                        try
                        {
                            throw new InvalidOperationException("Sample exception #" + n + " from fields sample");
                        }
                        catch (Exception ex)
                        {
                            Bugsee.LogException(ex, new Dictionary<string, object>
                            {
                                { "source", "BugseeActionCatalog" },
                                { "count", n }
                            });
                        }
                        _bootstrap.SetStatus("LogException #" + n + " sent");
                        break;
                    }

                    case BugseeDemoAction.TestCrash:
                        _bootstrap.SetStatus("TestCrash (native)");
                        Bugsee.TestCrash();
                        break;

                    case BugseeDemoAction.SetSecureRect:
                        BugseeActionForms.Ensure(_bootstrap)
                            .ShowSecureRect(_secureRectActive ? (RectInt?)_secureRect : null);
                        break;

                    case BugseeDemoAction.ClearSecureRects:
                        Bugsee.RemoveAllSecureRectangles();
                        _secureRectActive = false;
                        _bootstrap.SetStatus("RemoveAllSecureRectangles");
                        break;

                    case BugseeDemoAction.ShowFeedback:
                        Bugsee.Feedback.ShowFeedbackUi();
                        _bootstrap.SetStatus("Feedback.ShowFeedbackUi");
                        break;

                    case BugseeDemoAction.FeedbackGreeting:
                        Bugsee.Feedback.SetDefaultGreeting("Hello from the anteater fields!");
                        _bootstrap.SetStatus("Feedback.SetDefaultGreeting");
                        break;

                    case BugseeDemoAction.ToggleFeedbackListener:
                        _feedbackListenerOn = !_feedbackListenerOn;
                        Bugsee.Feedback.SetListener(
                            _feedbackListenerOn ? new SampleFeedbackListener(_bootstrap) : null);
                        _bootstrap.SetStatus(_feedbackListenerOn
                            ? "Feedback listener ON"
                            : "Feedback listener OFF");
                        break;

                    case BugseeDemoAction.SetUserIdentifier:
                        BugseeActionForms.Ensure(_bootstrap).ShowUserIdentifier();
                        break;

                    case BugseeDemoAction.ClearIdentity:
                        Bugsee.ClearUserIdentifier();
                        _bootstrap.SetStatus("ClearUserIdentifier (attributes kept)");
                        break;

                    case BugseeDemoAction.ToggleIdentity:
                    {
                        var id = Bugsee.GetUserIdentifier();
                        if (string.IsNullOrEmpty(id))
                            Run(BugseeDemoAction.SetUserIdentifier);
                        else
                            Run(BugseeDemoAction.ClearIdentity);
                        break;
                    }

                    case BugseeDemoAction.GetIdentity:
                        _bootstrap.SetStatus("GetUserIdentifier=" + (Bugsee.GetUserIdentifier() ?? "(null)"));
                        break;

                    case BugseeDemoAction.SetAttribute:
                        BugseeActionForms.Ensure(_bootstrap).ShowAttribute();
                        break;

                    case BugseeDemoAction.GetAttribute:
                    {
                        var v = Bugsee.GetAttribute(AttributeKey);
                        _bootstrap.SetStatus("GetAttribute " + AttributeKey + "=" + (v ?? "(null)"));
                        break;
                    }

                    case BugseeDemoAction.ClearAttribute:
                        Bugsee.ClearAttribute(AttributeKey);
                        _bootstrap.SetStatus("ClearAttribute " + AttributeKey);
                        break;

                    case BugseeDemoAction.ClearAllAttributes:
                        Bugsee.ClearAllAttributes();
                        _bootstrap.SetStatus("ClearAllAttributes");
                        break;

                    case BugseeDemoAction.CaptureViewHierarchy:
                        Bugsee.CaptureViewHierarchy();
                        _bootstrap.SetStatus("CaptureViewHierarchy");
                        break;

                    case BugseeDemoAction.ResetVideoPermission:
                        Bugsee.ResetVideoCapturePermission();
                        _bootstrap.SetStatus("ResetVideoCapturePermission");
                        break;

                    case BugseeDemoAction.ToggleFilters:
                        _filtersOn = !_filtersOn;
                        _bootstrap.ApplyFilters(_filtersOn);
                        break;

                    case BugseeDemoAction.ToggleReportHandler:
                        _reportHandlerOn = !_reportHandlerOn;
                        Bugsee.SetReportHandler(_reportHandlerOn ? new SampleReportHandler() : null);
                        _bootstrap.SetStatus(_reportHandlerOn ? "ReportHandler ON" : "ReportHandler OFF");
                        break;

                    case BugseeDemoAction.ToggleLifecycleListener:
                        _lifecycleListenerOn = !_lifecycleListenerOn;
                        if (_lifecycleListenerOn)
                        {
                            _lifecycleListener = new SampleLifecycleListener(_bootstrap);
                            Bugsee.SetLifecycleEventListener(_lifecycleListener);
                            _bootstrap.SetStatus("LifecycleListener ON");
                        }
                        else
                        {
                            Bugsee.SetLifecycleEventListener(null);
                            _lifecycleListener = null;
                            _bootstrap.SetStatus("LifecycleListener OFF");
                        }
                        break;

                    case BugseeDemoAction.Appearance:
                        BugseeActionForms.Ensure(_bootstrap).ShowAppearance();
                        break;

                    case BugseeDemoAction.StatusReadout:
                        _bootstrap.SetStatus(_bootstrap.StatusLine);
                        break;

                    case BugseeDemoAction.ObsoletePauseResume:
#pragma warning disable CS0618
                        // Pause()/Resume() are obsolete aliases of StartBlackout()/EndBlackout(): alternate per tap.
                        var wasPaused = Bugsee.IsBlackout;
                        if (wasPaused)
                            Bugsee.Resume();
                        else
                            Bugsee.Pause();
#pragma warning restore CS0618
                        _bootstrap.SetStatus(wasPaused
                            ? "Resume() (obsolete) -> resumed"
                            : "Pause() (obsolete) -> paused; tap again to Resume()");
                        break;

                    case BugseeDemoAction.Launch:
                        _bootstrap.LaunchSdk();
                        break;

                    case BugseeDemoAction.Relaunch:
                        _bootstrap.RelaunchSdk();
                        break;

                    case BugseeDemoAction.Stop:
                        _bootstrap.SetStatus("Stop requested");
                        Bugsee.Stop(() => _bootstrap.SetStatus("Stopped"));
                        break;

                    case BugseeDemoAction.Breadcrumb:
                        BugseeActionForms.Ensure(_bootstrap).ShowBreadcrumb();
                        break;

                    case BugseeDemoAction.NetworkEvent:
                        Interactions.AddNetworkEvents();
                        break;

                    case BugseeDemoAction.ReportCreate:
                        Interactions.CreateDraft();
                        break;

                    case BugseeDemoAction.ReportEdit:
                        BugseeActionForms.Ensure(_bootstrap).ShowReportDraft();
                        break;

                    case BugseeDemoAction.ReportUpload:
                        Interactions.UploadDraft();
                        break;

                    case BugseeDemoAction.ReportDiscard:
                        Interactions.DiscardDraft();
                        break;

                    case BugseeDemoAction.Notify:
                        BugseeActionForms.Ensure(_bootstrap).ShowNotify();
                        break;

                    case BugseeDemoAction.ApmStart:
                        Interactions.StartTransaction();
                        break;

                    case BugseeDemoAction.ApmSpan:
                        Interactions.StartSpan();
                        break;

                    case BugseeDemoAction.ApmFinish:
                        Interactions.FinishTransaction();
                        break;

                    case BugseeDemoAction.LaunchAsync:
                        Interactions.LaunchAsync();
                        break;

                    case BugseeDemoAction.RelaunchAsync:
                        Interactions.RelaunchAsync();
                        break;

                    case BugseeDemoAction.StopAsync:
                        Interactions.StopAsync();
                        break;

                    case BugseeDemoAction.LaunchOptions:
                        Interactions.GetLaunchOptions();
                        break;

                    case BugseeDemoAction.DeleteData:
                        Interactions.RequestDeleteData();
                        break;

                    case BugseeDemoAction.LogUnhandled:
                        Interactions.LogUnhandled();
                        break;

                    case BugseeDemoAction.RemoveSecureRect:
                        if (!_secureRectActive)
                        {
                            _bootstrap.SetStatus("RemoveSecureRectangle: no rect added by Set Secure Rect");
                            break;
                        }
                        Bugsee.RemoveSecureRectangle(_secureRect);
                        _secureRectActive = false;
                        _bootstrap.SetStatus(string.Format("RemoveSecureRectangle LTRB=({0},{1},{2},{3})",
                            _secureRect.xMin, _secureRect.yMin, _secureRect.xMax, _secureRect.yMax));
                        break;

                    case BugseeDemoAction.AppearanceTexts:
                        BugseeActionForms.Ensure(_bootstrap).ShowTexts();
                        break;

                    default:
                        _bootstrap.SetStatus("Unknown action: " + action);
                        break;
                }
            }
            catch (Exception ex)
            {
                _bootstrap.SetStatus("Error: " + ex.Message);
                Debug.LogException(ex);
            }
        }

        public static string DisplayName(BugseeDemoAction action)
        {
            switch (action)
            {
                case BugseeDemoAction.ShowReportDialog: return "Report";
                case BugseeDemoAction.Upload: return "Upload";
                case BugseeDemoAction.ToggleBlackout: return "Blackout";
                case BugseeDemoAction.LogBundle: return "Log Bundle";
                case BugseeDemoAction.LogAllLevels: return "Log Levels";
                case BugseeDemoAction.LogException: return "Exception";
                case BugseeDemoAction.TestCrash: return "Test Crash";
                case BugseeDemoAction.SetSecureRect: return "Set Secure Rect";
                case BugseeDemoAction.ClearSecureRects: return "Clear Rects";
                case BugseeDemoAction.ShowFeedback: return "Feedback";
                case BugseeDemoAction.FeedbackGreeting: return "Greeting";
                case BugseeDemoAction.ToggleFeedbackListener: return "FB Listen";
                case BugseeDemoAction.SetUserIdentifier: return "Set User ID";
                case BugseeDemoAction.ClearIdentity: return "Clear ID";
                case BugseeDemoAction.ToggleIdentity: return "Identity";
                case BugseeDemoAction.GetIdentity: return "Get ID";
                case BugseeDemoAction.SetAttribute: return "Set Attr";
                case BugseeDemoAction.GetAttribute: return "Get Attr";
                case BugseeDemoAction.ClearAttribute: return "Clear Attr";
                case BugseeDemoAction.ClearAllAttributes: return "Clear Attrs";
                case BugseeDemoAction.CaptureViewHierarchy: return "Hierarchy";
                case BugseeDemoAction.ResetVideoPermission: return "Reset Video";
                case BugseeDemoAction.ToggleFilters: return "Filters";
                case BugseeDemoAction.ToggleReportHandler: return "Report Hdlr";
                case BugseeDemoAction.ToggleLifecycleListener: return "Lifecycle";
                case BugseeDemoAction.Appearance: return "Appearance";
                case BugseeDemoAction.StatusReadout: return "Status";
                case BugseeDemoAction.ObsoletePauseResume: return "Pause/Resume (old)";
                case BugseeDemoAction.Launch: return "Launch";
                case BugseeDemoAction.Relaunch: return "Relaunch";
                case BugseeDemoAction.Stop: return "Stop";
                case BugseeDemoAction.Breadcrumb: return "Breadcrumb";
                case BugseeDemoAction.NetworkEvent: return "Network Event";
                case BugseeDemoAction.ReportCreate: return "New Report";
                case BugseeDemoAction.ReportEdit: return "Edit Report";
                case BugseeDemoAction.ReportUpload: return "Send Report";
                case BugseeDemoAction.ReportDiscard: return "Discard Report";
                case BugseeDemoAction.Notify: return "Notify";
                case BugseeDemoAction.ApmStart: return "Start Txn";
                case BugseeDemoAction.ApmSpan: return "Child Span";
                case BugseeDemoAction.ApmFinish: return "Finish Txn";
                case BugseeDemoAction.LaunchAsync: return "Launch Async";
                case BugseeDemoAction.RelaunchAsync: return "Relaunch Async";
                case BugseeDemoAction.StopAsync: return "Stop Async";
                case BugseeDemoAction.LaunchOptions: return "Launch Options";
                case BugseeDemoAction.DeleteData: return "Delete Data";
                case BugseeDemoAction.LogUnhandled: return "Log Unhandled";
                case BugseeDemoAction.RemoveSecureRect: return "Remove Rect";
                case BugseeDemoAction.AppearanceTexts: return "Texts";
                default: return action.ToString();
            }
        }
    }

    /// <summary>Selection rule for the E/Space + Q/Tab keyboard path on a hill.</summary>
    public static class HillSelection
    {
        /// <summary>Next selected index, wrapping; 0 when there are no actions.</summary>
        public static int Next(int current, int count) => count <= 0 ? 0 : (current + 1) % count;

        /// <summary>The selection clamped into range (the action list can change).</summary>
        public static int Clamp(int current, int count) => count <= 0 ? 0 : Mathf.Clamp(current, 0, count - 1);
    }

    sealed class SampleLifecycleListener : ILifecycleEventListener
    {
        readonly BugseeSampleBootstrap _bootstrap;

        public SampleLifecycleListener(BugseeSampleBootstrap bootstrap)
        {
            _bootstrap = bootstrap;
        }

        public void OnEvent(string eventType, object data)
        {
            _bootstrap?.SetStatus("LifecycleListener: " + eventType);
        }
    }

    sealed class SampleReportHandler : ReportHandlerBase
    {
        public override void OnBeforeReportCreated(IReport report, bool isTerminating, Action completionCallback)
        {
            try
            {
                if (report != null)
                {
                    report.Summary = "[Sample] " + (report.Summary ?? "");
                    report.AddAttachmentBytes(
                        Encoding.UTF8.GetBytes("Attached by MiniGame SampleReportHandler"),
                        "sample-note.txt",
                        "text/plain");
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
            completionCallback?.Invoke();
        }

        public override void OnAfterReportCreated(IReport report, bool isTerminating, Action completionCallback)
        {
            Debug.Log("[Sample] OnAfterReportCreated");
            completionCallback?.Invoke();
        }
    }

    /// <summary>Logs the severity each report was created with (the "severity" e2e scenario).</summary>
    sealed class SeverityLoggingReportHandler : ReportHandlerBase
    {
        public override void OnBeforeReportCreated(IReport report, bool isTerminating, Action completionCallback)
        {
            Debug.Log("[Sample] severity-check summary=" + report?.Summary + " severity=" + report?.Severity);
            completionCallback?.Invoke();
        }
    }

    /// <summary>
    /// Optional filters used by the debug panel / filter totem. Each filter counts what it saw and what it dropped or
    /// redacted (the HUD shows the totals, <see cref="Summary"/>), and the network and breadcrumb filters log each one.
    /// The wrapper calls the filters on the main thread; the counters are interlocked anyway. The log filter does not log:
    /// the SDK captures the Unity log, so a log line written from it would reach it again.
    /// </summary>
    public static class SampleFilters
    {
        public static bool NetworkRedactEnabled;
        public static bool LogDropDebugEnabled;
        public static bool BreadcrumbDropEnabled;

        /// <summary>The level and length of the last log line the filter dropped (for a check's detail; the text is not kept, so it cannot be echoed into the log).</summary>
        public static volatile string LastLogDropped;

        /// <summary>
        /// When set, the drops of a breadcrumb in that category (<see cref="BreadcrumbMarkerDropped"/>) and of a log line containing
        /// <see cref="LogMarker"/> (<see cref="LogMarkerDropped"/>) are counted apart, so a check can tell its own events from the SDK's.
        /// </summary>
        public static volatile string BreadcrumbMarker;

        /// <summary>When set, a breadcrumb in that category keeps flowing but its message is edited (the only field touched).</summary>
        public static volatile string BreadcrumbEditMarker;
        public static volatile string LogMarker;

        /// <summary>Breadcrumbs of category "e2e-pass" the filter let through and of "e2e-edit" whose message it edited (filters scenario, phase 3).</summary>
        public static int PassSeen => Volatile.Read(ref _passSeen);
        public static int EditApplied => Volatile.Read(ref _editApplied);
        static int _passSeen, _editApplied;
        static int _breadcrumbMarkerDropped, _logMarkerDropped;

        /// <summary>Text of the two Bugsee.Log lines the filters scenario writes (#190): one without and one with requiresFiltering.</summary>
        public const string LogDefaultText = "e2e-log-default";
        public const string LogOptInText = "e2e-log-optin";
        static int _logDefaultSeen, _logOptInSeen, _logOptInDropped;
        /// <summary>Times the log filter saw the Bugsee.Log line written without requiresFiltering (must stay 0).</summary>
        public static int LogDefaultSeen => Volatile.Read(ref _logDefaultSeen);
        /// <summary>Times the log filter saw / dropped the Bugsee.Log line written with requiresFiltering.</summary>
        public static int LogOptInSeen => Volatile.Read(ref _logOptInSeen);
        public static int LogOptInDropped => Volatile.Read(ref _logOptInDropped);
        public static int BreadcrumbMarkerDropped => Volatile.Read(ref _breadcrumbMarkerDropped);
        public static int LogMarkerDropped => Volatile.Read(ref _logMarkerDropped);

        static int _networkSeen, _networkRedacted, _logSeen, _logDropped, _breadcrumbSeen, _breadcrumbDropped;

        public static int NetworkSeen => Volatile.Read(ref _networkSeen);
        public static int NetworkRedacted => Volatile.Read(ref _networkRedacted);
        public static int LogSeen => Volatile.Read(ref _logSeen);
        public static int LogDropped => Volatile.Read(ref _logDropped);
        public static int BreadcrumbSeen => Volatile.Read(ref _breadcrumbSeen);
        public static int BreadcrumbDropped => Volatile.Read(ref _breadcrumbDropped);

        /// <summary>"n2/2 l1/9 b3/3": redacted or dropped / seen, for the network, log and breadcrumb filter.</summary>
        public static string Summary =>
            "n" + NetworkRedacted + "/" + NetworkSeen + " l" + LogDropped + "/" + LogSeen + " b" + BreadcrumbDropped + "/" + BreadcrumbSeen;

        public static void ResetCounters()
        {
            Interlocked.Exchange(ref _networkSeen, 0);
            Interlocked.Exchange(ref _networkRedacted, 0);
            Interlocked.Exchange(ref _logSeen, 0);
            Interlocked.Exchange(ref _logDropped, 0);
            Interlocked.Exchange(ref _breadcrumbSeen, 0);
            Interlocked.Exchange(ref _breadcrumbDropped, 0);
            Interlocked.Exchange(ref _breadcrumbMarkerDropped, 0);
            Interlocked.Exchange(ref _logMarkerDropped, 0);
            Interlocked.Exchange(ref _logDefaultSeen, 0);
            Interlocked.Exchange(ref _logOptInSeen, 0);
            Interlocked.Exchange(ref _logOptInDropped, 0);
            Interlocked.Exchange(ref _passSeen, 0);
            Interlocked.Exchange(ref _editApplied, 0);
        }

        public static INetworkEvent NetworkFilter(INetworkEvent e)
        {
            if (e == null) return null;
            Interlocked.Increment(ref _networkSeen);
            if (!NetworkRedactEnabled) return e;
            var redacted = false;
            if (!string.IsNullOrEmpty(e.Url) && e.Url.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                e.Url = "[redacted]";
                redacted = true;
            }
            if (!string.IsNullOrEmpty(e.Body))
            {
                e.Body = "[redacted-body]";
                redacted = true;
            }
            if (redacted)
            {
                var n = Interlocked.Increment(ref _networkRedacted);
                Debug.Log("[BugseeSample] network filter redacted #" + n + " id=" + e.Id + " stage=" + e.Stage);
            }
            return e;
        }

        public static ILogEvent LogFilter(ILogEvent e)
        {
            if (e == null) return null;
            Interlocked.Increment(ref _logSeen);
            var text = e.Message;
            var isDefault = text != null && text.Contains(LogDefaultText);
            var isOptIn = text != null && text.Contains(LogOptInText);
            if (isDefault) Interlocked.Increment(ref _logDefaultSeen);
            if (isOptIn) Interlocked.Increment(ref _logOptInSeen);
            if (LogDropDebugEnabled && (e.Level >= LogLevel.Debug || isOptIn))
            {
                Interlocked.Increment(ref _logDropped);
                if (isOptIn) Interlocked.Increment(ref _logOptInDropped);
                LastLogDropped = e.Level + " (" + (e.Message?.Length ?? 0) + " chars)";
                var logMarker = LogMarker;
                if (logMarker != null && e.Message != null && e.Message.Contains(logMarker))
                    Interlocked.Increment(ref _logMarkerDropped);
                return null;
            }
            return e;
        }

        public static IBreadcrumb BreadcrumbFilter(IBreadcrumb e)
        {
            if (e == null) return null;
            Interlocked.Increment(ref _breadcrumbSeen);
            if (BreadcrumbDropEnabled)
            {
                var n = Interlocked.Increment(ref _breadcrumbDropped);
                var crumbMarker = BreadcrumbMarker;
                if (crumbMarker != null && e.Category == crumbMarker)
                    Interlocked.Increment(ref _breadcrumbMarkerDropped);
                Debug.Log("[BugseeSample] breadcrumb filter dropped #" + n + " category=" + e.Category);
                return null;
            }
            var editMarker = BreadcrumbEditMarker;
            if (editMarker != null && e.Category == editMarker)
            {
                e.Message = "[edited]";
                Interlocked.Increment(ref _editApplied);
            }
            else if (e.Category == "e2e-pass") Interlocked.Increment(ref _passSeen);
            return e;
        }
    }
}
