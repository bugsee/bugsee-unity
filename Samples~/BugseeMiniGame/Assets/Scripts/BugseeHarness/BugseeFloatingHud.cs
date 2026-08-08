using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>Floating Bugsee action buttons (path 1 of twofold UX).</summary>
    public sealed class BugseeFloatingHud : MonoBehaviour
    {
        BugseeSampleBootstrap _bootstrap;
        BugseeDebugPanel _debugPanel;
        bool _expanded = true;

        public void Init(BugseeSampleBootstrap bootstrap, BugseeDebugPanel debugPanel)
        {
            _bootstrap = bootstrap;
            _debugPanel = debugPanel;
        }

        void OnGUI()
        {
            const float w = 150f;
            float x = Screen.width - w - 12f;
            float y = 12f;

            GUI.Box(new Rect(x - 8f, y - 4f, w + 16f, _expanded ? 340f : 90f), GUIContent.none);

            if (_bootstrap != null)
            {
                GUI.Label(new Rect(x, y, w, 22f), Truncate(_bootstrap.StatusLine, 42));
                y += 22f;
                GUI.Label(new Rect(x, y, w, 18f), Truncate("lc: " + _bootstrap.LastLifecycle, 42));
                y += 22f;
            }

            if (GUI.Button(new Rect(x, y, w, 28f), _expanded ? "Hide Bugsee HUD" : "Show Bugsee HUD"))
                _expanded = !_expanded;
            y += 32f;

            if (!_expanded) return;

            y = Button(x, y, w, "Report", BugseeDemoAction.ShowReportDialog);
            y = Button(x, y, w, "Upload", BugseeDemoAction.Upload);
            y = Button(x, y, w, "Blackout", BugseeDemoAction.ToggleBlackout);
            y = Button(x, y, w, "Log", BugseeDemoAction.LogBundle);
            y = Button(x, y, w, "Feedback", BugseeDemoAction.ShowFeedback);
            y = Button(x, y, w, "Identity", BugseeDemoAction.ToggleIdentity);

            if (GUI.Button(new Rect(x, y, w, 28f), "Debug…"))
            {
                if (_debugPanel != null)
                    _debugPanel.Visible = !_debugPanel.Visible;
            }
        }

        float Button(float x, float y, float w, string label, BugseeDemoAction action)
        {
            if (GUI.Button(new Rect(x, y, w, 28f), label))
                BugseeActionCatalog.Instance?.Run(action);
            return y + 32f;
        }

        static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s.Substring(0, max - 1) + "…";
        }
    }
}
