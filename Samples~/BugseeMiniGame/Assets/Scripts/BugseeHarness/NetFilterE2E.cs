using System;
using System.Collections;
using Bugsee.Contracts.Exchange;
using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>
    /// Scenario "netfilter": three network event filters over successful (200, no error) events submitted through
    /// the exchange factory: a pass-through, a redacting one (url query, a header) and one that deliberately empties
    /// the body. Each filter logs what it was handed and what it left, tagged "[NETFILTER]". What native applied is
    /// read from the iOS bridge's own log of the event it hands to the SDK (a local, uncommitted build patch for the
    /// run). The sample has no UnityWebRequest module, hence the factory events. Waits for the SDK to launch before
    /// the first SDK call.
    /// </summary>
    public static class NetFilterE2E
    {
        static string Describe(INetworkEvent e) =>
            "stage=" + e.Stage + " url=" + (e.Url ?? "<null>") + " body=" + (e.Body == null ? "<null>" : "len" + e.Body.Length) +
            " err=" + (e.ErrorShortMessage ?? "<null>") + "/" + (e.ErrorDescription ?? "<null>") +
            " code=" + e.ResponseCode + " headers=" + e.Headers.Count;

        public static IEnumerator Run()
        {
            var waits = 0;
            while (!Bugsee.IsLaunched && waits++ < 120) yield return new WaitForSeconds(0.5f);
            Debug.Log("[NETFILTER] launched=" + Bugsee.IsLaunched + " status=" + Bugsee.Status);

            Bugsee.SetNetworkEventFilter(e =>
            {
                Debug.Log("[NETFILTER] pass-through sees " + Describe(e));
                return e;
            });
            yield return Submit("pass", null);

            Bugsee.SetNetworkEventFilter(e =>
            {
                Debug.Log("[NETFILTER] redact sees " + Describe(e));
                if (e.Url != null) e.Url = e.Url.Split('?')[0] + "?redacted";
                if (e.Headers.ContainsKey("Authorization")) e.Headers["Authorization"] = "redacted";
                Debug.Log("[NETFILTER] redact leaves " + Describe(e));
                return e;
            });
            yield return Submit("redact", null);

            Bugsee.SetNetworkEventFilter(e =>
            {
                Debug.Log("[NETFILTER] empty-body sees " + Describe(e));
                e.Body = "";
                Debug.Log("[NETFILTER] empty-body leaves " + Describe(e));
                return e;
            });
            yield return Submit("empty", "secret body");
            Debug.Log("[NETFILTER] done");
        }

        static IEnumerator Submit(string tag, string body)
        {
            var e = Bugsee.GetExchangeFactory().CreateNetworkEvent(
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), NetworkEventStage.RequestCompleted, "nf-" + tag, "NSURLSession", "GET");
            e.Url = "https://example.com/" + tag + "?token=secret";
            e.ResponseCode = 200;
            e.Size = 1234;
            e.Body = body;
            e.Headers = new System.Collections.Generic.Dictionary<string, string> { { "Authorization", "Bearer abc" }, { "Accept", "*/*" } };
            Bugsee.AddNetworkEvent(e);
            Debug.Log("[NETFILTER] submitted " + tag);
            yield return new WaitForSeconds(3f);
        }
    }
}
