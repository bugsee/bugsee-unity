using System.Collections;
using Bugsee.Contracts.Exchange;
using UnityEngine;
using UnityEngine.Networking;

namespace Bugsee.Sample
{
    /// <summary>
    /// Scenario "netfilter": a pass-through network event filter and then a modifying one (redacts the url query and a
    /// header, clears the body), each over real UnityWebRequest traffic. The filter logs what it was handed and what it
    /// left, tagged "[NETFILTER]"; what native applied is read from the iOS bridge's own log of the event it hands to the
    /// SDK (a local, uncommitted build patch for the run). Waits for the SDK to launch before the first SDK call.
    /// </summary>
    public static class NetFilterE2E
    {
        const string Url = "https://example.com/?token=secret";

        static string Describe(INetworkEvent e) =>
            "stage=" + e.Stage + " url=" + (e.Url ?? "<null>") + " bodyLen=" + (e.Body == null ? -1 : e.Body.Length) +
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
            yield return Request("pass");

            Bugsee.SetNetworkEventFilter(e =>
            {
                Debug.Log("[NETFILTER] redact sees " + Describe(e));
                if (e.Url != null) e.Url = e.Url.Split('?')[0] + "?redacted";
                if (e.Headers.ContainsKey("Authorization")) e.Headers["Authorization"] = "redacted";
                e.Headers["X-Filtered"] = "1";
                e.Body = null;
                Debug.Log("[NETFILTER] redact leaves " + Describe(e));
                return e;
            });
            yield return Request("redact");
            Debug.Log("[NETFILTER] done");
        }

        static IEnumerator Request(string tag)
        {
            using (var req = UnityWebRequest.Get(Url))
            {
                req.SetRequestHeader("Authorization", "Bearer abc");
                yield return req.SendWebRequest();
                Debug.Log("[NETFILTER] " + tag + " request finished code=" + req.responseCode + " result=" + req.result);
            }
            yield return new WaitForSeconds(2f);
        }
    }
}
