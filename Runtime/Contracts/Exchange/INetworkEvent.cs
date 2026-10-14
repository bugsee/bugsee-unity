using System.Collections.Generic;

namespace Bugsee.Contracts.Exchange
{
    public enum NetworkEventStage
    {
        RequestStarted,
        RequestCompleted,
        Redirect,
        RequestErrored,
        RequestAborted,
        RequestTimingsReceived,
        WebSocket
    }

    public interface INetworkEvent
    {
        string Id { get; }
        string Mechanism { get; }
        string Url { get; set; }
        string Method { get; }
        string Body { get; set; }
        long Size { get; set; }
        int ResponseCode { get; set; }
        string StatusText { get; set; }
        string ErrorShortMessage { get; set; }
        string ErrorDescription { get; set; }
        IDictionary<string, string> Headers { get; set; }
        NetworkEventStage Stage { get; }

        /// <summary>
        /// Android only (Bugsee Android 7.4+ <c>NetworkEvent.isOverride</c>). iOS has no equivalent: the value is
        /// kept on the managed event but never sent to the native SDK. Defaults to false.
        /// </summary>
        bool IsOverride { get; set; }
    }
}
