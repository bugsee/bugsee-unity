using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Bugsee.Contracts.Feedback;
using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>
    /// Scenario "feedback" (tagged "[FEEDBACK]", issue #98): calls Bugsee.Feedback.SetDefaultGreeting, SetListener and
    /// ShowFeedbackUi, then shows what happened. Feedback is an opt-in native add-on, so the expectation is passed in:
    /// bugsee_feedback / BUGSEE_E2E_FEEDBACK = on (the build has the add-on: no "Feedback is not available" warning and
    /// the native UI opens, which the scenario asserts) or off (the build has no add-on: the wrapper says so once for the three calls).
    /// Ends with "RESULT PASS" or "RESULT FAIL". The UI itself is checked by the run script (screenshot / dumpsys).
    /// </summary>
    public static class FeedbackE2E
    {
        const string Needle = "Feedback is not available";
#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")] static extern System.IntPtr _sample_presented_chain();
#endif

        // iOS: the view controllers presented over Unity (the Feedback chat shows up here); elsewhere "n/a".
        static string Presented()
        {
#if UNITY_IOS && !UNITY_EDITOR
            var ptr = _sample_presented_chain();
            var text = Marshal.PtrToStringAnsi(ptr);
            Marshal.FreeHGlobal(ptr);
            return text;
#else
            return "n/a";
#endif
        }

        static readonly List<string> Warnings = new List<string>();

        sealed class Listener : IFeedbackListener
        {
            public void OnNewMessagesReceived(IReadOnlyList<string> newMessages) =>
                Debug.Log("[FEEDBACK] listener received " + newMessages.Count + " message(s)");
            public void OnNewMessageSent(string message) =>
                Debug.Log("[FEEDBACK] listener sent: " + message);
        }

        static bool _shownEvent;
        static string _lastPresented = "";

        static void OnLifecycle(string type, object data)
        {
            if (type != null && type.EndsWith("AfterFeedbackShown")) _shownEvent = true;
        }

        static void OnLog(string condition, string stack, LogType type)
        {
            if (type == LogType.Warning && condition != null && condition.Contains(Needle)) Warnings.Add(condition);
        }

        public static void Run(MonoBehaviour host, string expected) => host.StartCoroutine(Steps(expected));

        static IEnumerator Steps(string expected)
        {
            for (var i = 0; i < 120 && !Bugsee.IsLaunched; i++) yield return new WaitForSeconds(0.5f);
            var on = expected == "on";
            Debug.Log("[FEEDBACK] platform=" + Application.platform + " launched=" + Bugsee.IsLaunched
                + " status=" + Bugsee.Status + " expected add-on=" + (expected ?? "(unset)"));
            Warnings.Clear();
            _shownEvent = false;
            _lastPresented = "";
            Application.logMessageReceived += OnLog;
            Bugsee.LifecycleEvent += OnLifecycle;
            Bugsee.Feedback.SetDefaultGreeting("Hello from the anteater fields!");
            Bugsee.Feedback.SetListener(new Listener());
            Debug.Log("[FEEDBACK] greeting and listener set, warnings so far=" + Warnings.Count);
            yield return new WaitForSeconds(1f);
            Bugsee.Feedback.ShowFeedbackUi();
            Debug.Log("[FEEDBACK] ShowFeedbackUi called, warnings so far=" + Warnings.Count);
            for (var i = 1; i <= 3; i++)
            {
                yield return new WaitForSeconds(1f);
                Debug.Log("[FEEDBACK] +" + i + "s focused=" + Application.isFocused + " presented=" + (_lastPresented = Presented()));
            }
            Application.logMessageReceived -= OnLog;
            Bugsee.LifecycleEvent -= OnLifecycle;
            // On: the native UI really opened. Android reports it as the AfterFeedbackShown lifecycle event (the chat is another
            // activity); iOS shows it as a presented view controller of the add-on.
            var uiOpened = Application.platform == RuntimePlatform.Android ? _shownEvent : _lastPresented.Contains("BugseeFeedback");
            Debug.Log("[FEEDBACK] native UI opened=" + uiOpened + " (shown event=" + _shownEvent + ", presented=" + _lastPresented + ")");
            foreach (var w in Warnings) Debug.Log("[FEEDBACK] warning: " + w);
            // On: nothing to warn about. Off: the three calls produce the one warning (once per process).
            var ok = Bugsee.IsLaunched && (on ? Warnings.Count == 0 && uiOpened : Warnings.Count == 1 && !uiOpened);
            Debug.Log("[FEEDBACK] RESULT " + (ok ? "PASS" : "FAIL") + " addon=" + (on ? "on" : "off") + " warnings=" + Warnings.Count);
        }
    }
}
