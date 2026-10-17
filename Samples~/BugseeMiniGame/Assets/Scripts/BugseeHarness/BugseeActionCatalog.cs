using System;
using System.Collections.Generic;
using System.Text;
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
        Stop
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

        public BugseeActionCatalog(BugseeSampleBootstrap bootstrap)
        {
            _bootstrap = bootstrap;
            Instance = this;
        }

        internal static void ClearInstance(BugseeActionCatalog catalog)
        {
            if (ReferenceEquals(Instance, catalog))
                Instance = null;
        }

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
                        try
                        {
                            throw new InvalidOperationException("Sample exception from fields sample");
                        }
                        catch (Exception ex)
                        {
                            Bugsee.LogException(ex, new Dictionary<string, object>
                            {
                                { "source", "BugseeActionCatalog" }
                            });
                        }
                        _bootstrap.SetStatus("LogException");
                        break;

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
                        Bugsee.ClearAllAttributes();
                        _bootstrap.SetStatus("Clear identity + attributes");
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
                        var v = Bugsee.GetAttribute("field.note");
                        _bootstrap.SetStatus("GetAttribute field.note=" + (v ?? "(null)"));
                        break;
                    }

                    case BugseeDemoAction.ClearAttribute:
                        Bugsee.ClearAttribute("field.note");
                        _bootstrap.SetStatus("ClearAttribute field.note");
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
                        Bugsee.Pause();
                        Bugsee.Resume();
#pragma warning restore CS0618
                        _bootstrap.SetStatus("Pause+Resume (obsolete → blackout)");
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
                case BugseeDemoAction.ObsoletePauseResume: return "Pause/Resume";
                case BugseeDemoAction.Launch: return "Launch";
                case BugseeDemoAction.Relaunch: return "Relaunch";
                case BugseeDemoAction.Stop: return "Stop";
                default: return action.ToString();
            }
        }
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

    /// <summary>Optional filters used by the debug panel / filter totem.</summary>
    public static class SampleFilters
    {
        public static bool NetworkRedactEnabled;
        public static bool LogDropDebugEnabled;
        public static bool BreadcrumbDropEnabled;

        public static INetworkEvent NetworkFilter(INetworkEvent e)
        {
            if (e == null) return null;
            if (!NetworkRedactEnabled) return e;
            if (!string.IsNullOrEmpty(e.Url) && e.Url.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0)
                e.Url = "[redacted]";
            if (!string.IsNullOrEmpty(e.Body))
                e.Body = "[redacted-body]";
            return e;
        }

        public static ILogEvent LogFilter(ILogEvent e)
        {
            if (e == null) return null;
            if (LogDropDebugEnabled && e.Level >= LogLevel.Debug)
                return null;
            return e;
        }

        public static IBreadcrumb BreadcrumbFilter(IBreadcrumb e)
        {
            if (e == null) return null;
            if (BreadcrumbDropEnabled) return null;
            return e;
        }
    }
}
