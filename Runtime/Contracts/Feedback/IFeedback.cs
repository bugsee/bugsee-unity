using System;
using System.Collections.Generic;

namespace Bugsee.Contracts.Feedback
{
    public interface IFeedbackListener
    {
        void OnNewMessagesReceived(IReadOnlyList<string> newMessages);
        void OnNewMessageSent(string message);
    }

    /// <summary>
    /// The in-app feedback chat. It is an optional native add-on (Android <c>bugsee-android-feedback</c>, iOS
    /// <c>BugseeFeedback</c>) that is linked only when the project opts in with the scripting define
    /// <c>BUGSEE_FEEDBACK</c> (or the environment variable <c>BUGSEE_FEEDBACK=1</c> for the export); see Documentation~/index.md.
    /// Without the add-on every member does nothing and logs a warning once per process. Android needs AGP 8.1.1+ and
    /// compileSdk 35+ (Unity 6); in the Editor and in other players the members do nothing and log once.
    /// </summary>
    public interface IFeedback
    {
        /// <summary>Opens the native feedback chat over the game. Needs the add-on and a launched SDK.</summary>
        void ShowFeedbackUi();

        /// <summary>The greeting shown at the top of an empty conversation; null clears it.</summary>
        void SetDefaultGreeting(string greeting);

        /// <summary>Receives new messages and sent messages (on the Unity main thread); null removes the listener.</summary>
        void SetListener(IFeedbackListener listener);
    }
}
