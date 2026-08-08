using System.Collections.Generic;
using System.Text;
using Bugsee.Contracts.Appearance;
using Bugsee.Contracts.Feedback;
using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>Full IMGUI debug panel (secondary harness).</summary>
    public sealed class BugseeDebugPanel : MonoBehaviour
    {
        public static BugseeDebugPanel Instance { get; private set; }

        public bool Visible;

        BugseeSampleBootstrap _bootstrap;
        Vector2 _scroll;
        string _attrKey = "sample.key";
        string _attrValue = "value";
        bool _confirmCrash;
        bool _reportHandlerOn;
        bool _filtersOn;
        bool _feedbackListenerOn;

        public void Init(BugseeSampleBootstrap bootstrap)
        {
            _bootstrap = bootstrap;
            Instance = this;
        }

        void OnDestroy()
        {
            if (ReferenceEquals(Instance, this))
                Instance = null;
        }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.BackQuote) || Input.GetKeyDown(KeyCode.F1))
                Visible = !Visible;
        }

        void OnGUI()
        {
            if (!Visible) return;

            var area = new Rect(12f, 80f, Mathf.Min(420f, Screen.width - 24f), Screen.height - 100f);
            GUI.Box(area, "Bugsee Debug  (` / F1 to close)");
            GUILayout.BeginArea(new Rect(area.x + 8f, area.y + 24f, area.width - 16f, area.height - 32f));
            _scroll = GUILayout.BeginScrollView(_scroll);

            GUILayout.Label(_bootstrap != null ? _bootstrap.StatusLine : "");
            GUILayout.Label("Lifecycle: " + (_bootstrap != null ? _bootstrap.LastLifecycle : "—"));
            GUILayout.Label("UserId: " + (Bugsee.GetUserIdentifier() ?? "(null)"));

            Section("Lifecycle");
            if (GUILayout.Button("Launch")) _bootstrap?.LaunchSdk();
            if (GUILayout.Button("Relaunch")) BugseeActionCatalog.Instance?.Run(BugseeDemoAction.Relaunch);
            if (GUILayout.Button("Stop")) BugseeActionCatalog.Instance?.Run(BugseeDemoAction.Stop);
            if (GUILayout.Button("StartBlackout")) Bugsee.StartBlackout();
            if (GUILayout.Button("EndBlackout")) Bugsee.EndBlackout();
            if (GUILayout.Button("ResetVideoCapturePermission"))
            {
                Bugsee.ResetVideoCapturePermission();
                _bootstrap?.SetStatus("ResetVideoCapturePermission");
            }

            Section("Logging / Reporting");
            if (GUILayout.Button("Log bundle")) BugseeActionCatalog.Instance?.Run(BugseeDemoAction.LogBundle);
            if (GUILayout.Button("LogException")) BugseeActionCatalog.Instance?.Run(BugseeDemoAction.LogException);
            if (GUILayout.Button("ShowReportDialog")) BugseeActionCatalog.Instance?.Run(BugseeDemoAction.ShowReportDialog);
            if (GUILayout.Button("Upload")) BugseeActionCatalog.Instance?.Run(BugseeDemoAction.Upload);
            if (GUILayout.Button("CaptureViewHierarchy")) BugseeActionCatalog.Instance?.Run(BugseeDemoAction.CaptureViewHierarchy);

            _confirmCrash = GUILayout.Toggle(_confirmCrash, "Arm TestCrash");
            GUI.enabled = _confirmCrash;
            if (GUILayout.Button("TestCrash"))
                BugseeActionCatalog.Instance?.Run(BugseeDemoAction.TestCrash);
            GUI.enabled = true;

            Section("Identity / Attributes");
            if (GUILayout.Button("Set identity")) BugseeActionCatalog.Instance?.Run(BugseeDemoAction.SetIdentity);
            if (GUILayout.Button("Clear identity")) BugseeActionCatalog.Instance?.Run(BugseeDemoAction.ClearIdentity);
            if (GUILayout.Button("GetUserIdentifier"))
                _bootstrap?.SetStatus("UserId => " + (Bugsee.GetUserIdentifier() ?? "(null)"));
            GUILayout.BeginHorizontal();
            _attrKey = GUILayout.TextField(_attrKey);
            _attrValue = GUILayout.TextField(_attrValue);
            GUILayout.EndHorizontal();
            if (GUILayout.Button("SetAttribute"))
            {
                Bugsee.SetAttribute(_attrKey, _attrValue);
                _bootstrap?.SetStatus("SetAttribute " + _attrKey);
            }
            if (GUILayout.Button("GetAttribute"))
            {
                var v = Bugsee.GetAttribute(_attrKey);
                _bootstrap?.SetStatus("GetAttribute => " + v);
            }
            if (GUILayout.Button("ClearAttribute"))
            {
                Bugsee.ClearAttribute(_attrKey);
                _bootstrap?.SetStatus("ClearAttribute " + _attrKey);
            }

            Section("Privacy");
            if (GUILayout.Button("Toggle secure rect")) BugseeActionCatalog.Instance?.Run(BugseeDemoAction.ToggleSecureRect);
            if (GUILayout.Button("RemoveAllSecureRectangles"))
            {
                Bugsee.RemoveAllSecureRectangles();
                _bootstrap?.SetStatus("RemoveAllSecureRectangles");
            }

            Section("Appearance");
            if (GUILayout.Button("Set report tint (sample)"))
            {
                Bugsee.Appearance
                    .SetColor(ReportAppearance.BackgroundColor, new Color32(30, 40, 60, 255))
                    .SetColor(ReportAppearance.ActionBarColor, new Color32(40, 90, 160, 255))
                    .SetString(ReportAppearance.SummaryPlaceholder, "MiniGame summary…");
                _bootstrap?.SetStatus("Appearance set");
            }
            if (GUILayout.Button("Appearance.ToMap()"))
            {
                var map = Bugsee.Appearance.ToMap();
                var n = map != null ? map.Count : 0;
                _bootstrap?.SetStatus("appearance.ToMap count=" + n);
            }

            Section("Filters / Handler / Lifecycle");
            var filters = GUILayout.Toggle(_filtersOn, "Sample filters (net+log+breadcrumb)");
            if (filters != _filtersOn)
            {
                _filtersOn = filters;
                _bootstrap?.ApplyFilters(_filtersOn);
            }
            var handler = GUILayout.Toggle(_reportHandlerOn, "Sample report handler");
            if (handler != _reportHandlerOn)
            {
                _reportHandlerOn = handler;
                Bugsee.SetReportHandler(_reportHandlerOn ? new SampleReportHandler() : null);
                _bootstrap?.SetStatus(_reportHandlerOn ? "ReportHandler ON" : "ReportHandler OFF");
            }
            if (GUILayout.Button("Toggle lifecycle listener"))
                BugseeActionCatalog.Instance?.Run(BugseeDemoAction.ToggleLifecycleListener);
            if (GUILayout.Button("Log all levels"))
                BugseeActionCatalog.Instance?.Run(BugseeDemoAction.LogAllLevels);
            if (GUILayout.Button("Appearance demo"))
                BugseeActionCatalog.Instance?.Run(BugseeDemoAction.AppearanceDemo);

            Section("Feedback");
            if (GUILayout.Button("ShowFeedbackUi")) BugseeActionCatalog.Instance?.Run(BugseeDemoAction.ShowFeedback);
            if (GUILayout.Button("SetDefaultGreeting"))
            {
                Bugsee.Feedback.SetDefaultGreeting("Hello from MiniGame sample");
                _bootstrap?.SetStatus("SetDefaultGreeting");
            }
            var fbListen = GUILayout.Toggle(_feedbackListenerOn, "Feedback listener (log)");
            if (fbListen != _feedbackListenerOn)
            {
                _feedbackListenerOn = fbListen;
                Bugsee.Feedback.SetListener(_feedbackListenerOn ? new SampleFeedbackListener(_bootstrap) : null);
                _bootstrap?.SetStatus(_feedbackListenerOn ? "Feedback listener ON" : "Feedback listener OFF");
            }

            Section("Lifecycle log");
            if (_bootstrap != null)
            {
                var sb = new StringBuilder();
                foreach (var line in _bootstrap.LifecycleLog)
                    sb.AppendLine(line);
                GUILayout.TextArea(sb.ToString(), GUILayout.MinHeight(80f));
            }

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        static void Section(string title)
        {
            GUILayout.Space(8f);
            var style = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
            GUILayout.Label(title, style);
        }
    }

    sealed class SampleFeedbackListener : IFeedbackListener
    {
        readonly BugseeSampleBootstrap _bootstrap;

        public SampleFeedbackListener(BugseeSampleBootstrap bootstrap)
        {
            _bootstrap = bootstrap;
        }

        public void OnNewMessagesReceived(IReadOnlyList<string> newMessages)
        {
            var n = newMessages != null ? newMessages.Count : 0;
            _bootstrap?.SetStatus("Feedback messages received: " + n);
        }

        public void OnNewMessageSent(string message)
        {
            _bootstrap?.SetStatus("Feedback sent: " + message);
        }
    }
}
