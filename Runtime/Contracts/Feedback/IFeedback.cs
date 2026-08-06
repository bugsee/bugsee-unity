using System;
using System.Collections.Generic;

namespace Bugsee.Contracts.Feedback
{
    public interface IFeedbackListener
    {
        void OnNewMessagesReceived(IReadOnlyList<string> newMessages);
        void OnNewMessageSent(string message);
    }

    public interface IFeedback
    {
        void ShowFeedbackUi();
        void SetDefaultGreeting(string greeting);
        void SetListener(IFeedbackListener listener);
    }
}
