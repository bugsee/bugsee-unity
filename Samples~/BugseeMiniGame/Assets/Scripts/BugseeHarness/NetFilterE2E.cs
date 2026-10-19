using System;
using System.Collections;
using System.Collections.Generic;
using Bugsee.Contracts.Exchange;
using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>
    /// Scenario "netfilter": four network event filters over events submitted through the exchange factory: a
    /// pass-through, a redacting one (url query, a header), one that deliberately empties the body, and one that
    /// RETURNS a replacement event (cleaned url, no body, no Authorization header). Each filter logs what it was handed
    /// and what it left, tagged "[NETFILTER]", and the run ends with "[NETFILTER] managed checks: ..." for what can be
    /// asserted from the managed side. What native applied is NOT visible from C#: it needs a diagnostic log of the
    /// event the bridge hands to the SDK (a local, uncommitted build patch, see the PR). The sample has no
    /// UnityWebRequest module, hence the factory events. Waits for the SDK to launch before the first SDK call.
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
            var factory = Bugsee.IsLaunched ? Bugsee.GetExchangeFactory() : null;
            if (factory == null)
            {
                Debug.LogError("[NETFILTER] the SDK did not launch: no exchange factory, nothing to filter");
                yield break;
            }

            var seen = new Dictionary<string, string>();

            Bugsee.SetNetworkEventFilter(e =>
            {
                seen["pass"] = Describe(e);
                Debug.Log("[NETFILTER] pass-through sees " + seen["pass"]);
                return e;
            });
            yield return Submit(factory, "pass", null);

            Bugsee.SetNetworkEventFilter(e =>
            {
                Debug.Log("[NETFILTER] redact sees " + Describe(e));
                if (e.Url != null) e.Url = e.Url.Split('?')[0] + "?redacted";
                if (e.Headers.ContainsKey("Authorization")) e.Headers["Authorization"] = "redacted";
                Debug.Log("[NETFILTER] redact leaves " + Describe(e));
                return e;
            });
            yield return Submit(factory, "redact", null);

            Bugsee.SetNetworkEventFilter(e =>
            {
                Debug.Log("[NETFILTER] empty-body sees " + Describe(e));
                e.Body = "";
                Debug.Log("[NETFILTER] empty-body leaves " + Describe(e));
                return e;
            });
            yield return Submit(factory, "empty", "secret body");

            Bugsee.SetNetworkEventFilter(e =>
            {
                seen["replace"] = Describe(e);
                Debug.Log("[NETFILTER] replace sees " + seen["replace"] + " Authorization=" + (e.Headers.ContainsKey("Authorization") ? "yes" : "no"));
                var r = factory.CreateNetworkEvent(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), e.Stage, e.Id, e.Mechanism, e.Method);
                r.Url = (e.Url ?? "https://example.com/").Split('?')[0] + "?replaced";
                r.ResponseCode = e.ResponseCode;
                Debug.Log("[NETFILTER] replace returns " + Describe(r) + " Authorization=" + (r.Headers.ContainsKey("Authorization") ? "yes" : "no"));
                return r;
            });
            yield return Submit(factory, "replace", "secret body", "boom");

            var ok = seen.ContainsKey("pass") && seen["pass"].Contains("url=https://example.com/pass?token=secret") && seen["pass"].Contains("code=200")
                && seen.ContainsKey("replace") && seen["replace"].Contains("len11");
            Debug.Log("[NETFILTER] managed checks: filters saw the submitted events = " + ok + " (what native applied needs the diagnostic log)");
            Bugsee.SetNetworkEventFilter(null); // leave no filter installed after the scenario
            Debug.Log("[NETFILTER] done");
        }

        static IEnumerator Submit(IBugseeExchangeFactory factory, string tag, string body, string error = null)
        {
            var e = factory.CreateNetworkEvent(
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), NetworkEventStage.RequestCompleted, "nf-" + tag, "NSURLSession", "GET");
            e.Url = "https://example.com/" + tag + "?token=secret";
            e.ResponseCode = 200;
            e.Size = 1234;
            e.Body = body;
            if (error != null) e.ErrorShortMessage = error;
            e.Headers = new Dictionary<string, string> { { "Authorization", "Bearer abc" }, { "Accept", "*/*" } };
            Bugsee.AddNetworkEvent(e);
            Debug.Log("[NETFILTER] submitted " + tag);
            yield return new WaitForSeconds(3f);
        }
    }
}
