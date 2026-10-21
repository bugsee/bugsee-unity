using System;
using System.Collections;
using System.Collections.Generic;
using Bugsee.Contracts.Exchange;
using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>
    /// Device check for the native extras (BUGSEE_E2E_SCENARIO=extras, issue #103): the default network sanitizer
    /// (<see cref="Bugsee.ApplyDefaultNetworkSanitizer"/>, Android only) on a hand-built event and inside a network filter, and the
    /// WebView secure views (<see cref="Bugsee.AddSecureWebView"/> / <see cref="Bugsee.RemoveSecureWebView"/>) on a real native
    /// WebView made by <see cref="NativeExtrasWebView"/>. Logs <c>[Sample] extras PASS|FAIL name :: detail</c> and a final
    /// <c>done failures=N</c>. Whether the secure view redacts the video is not observable from here; the checks cover the call
    /// reaching the SDK and the argument checks.
    /// </summary>
    static class NativeExtrasE2E
    {
        const float WaitSeconds = 15f;
        const string Secret = "SECRETTOKEN123";
        const string Password = "hunter2hunter2";
        const string Bearer = "Bearer SECRETBEARER456";
        static int _failures;

        // What a filter saw, written on the thread the SDK calls it on.
        static readonly object Gate = new object();
        static string _plainUrl, _plainAuth, _plainBody;
        static string _sanitizedUrl, _sanitizedAuth, _sanitizedBody;
        static bool _sanitizerReturned;
        static bool _sanitizerRan;
        static string _filterError;

        public static IEnumerator Run(BugseeSampleBootstrap b)
        {
            _failures = 0;
            Debug.Log("[Sample] extras e2e start launched=" + Bugsee.IsLaunched + " status=" + Bugsee.Status
                + " platform=" + Application.platform);
            var body = Body();
            while (true)
            {
                bool more;
                try { more = body.MoveNext(); }
                catch (Exception ex)
                {
                    Check("scenario completed without an exception", false, ex.GetType().Name + ": " + ex);
                    break;
                }
                if (!more) break;
                yield return body.Current;
            }
            try { Bugsee.SetNetworkEventFilter(null); } catch { }
            Debug.Log("[Sample] extras e2e done failures=" + _failures);
        }

        static IEnumerator Body()
        {
            bool android = Application.platform == RuntimePlatform.Android;

            // 1. The sanitizer on an event the app built itself.
            var factory = Bugsee.GetExchangeFactory();
            Check("exchange factory available", factory != null, "");
            var ev = NewSensitive(factory, "extras-direct");
            bool ran = Bugsee.ApplyDefaultNetworkSanitizer(ev);
            Check("sanitizer returns " + android + " on this platform", ran == android, "ran=" + ran);
            if (android)
            {
                Check("URL token value redacted, other query kept", !ev.Url.Contains(Secret) && ev.Url.Contains("x=1"), ev.Url);
                Check("URL userinfo password redacted", !ev.Url.Contains("pw12345"), ev.Url);
                string auth; ev.Headers.TryGetValue("Authorization", out auth);
                Check("Authorization header redacted", auth != null && !auth.Contains("SECRETBEARER456"), "Authorization=" + auth);
                string ctype; ev.Headers.TryGetValue("Content-Type", out ctype);
                Check("harmless header kept", ctype == "application/json", "Content-Type=" + ctype);
                Check("body password redacted, other field kept", !ev.Body.Contains(Password) && ev.Body.Contains("anteater"), ev.Body);
            }
            else
            {
                Check("iOS leaves the event as it was", ev.Url.Contains(Secret) && ev.Body.Contains(Password), ev.Url + " " + ev.Body);
            }
            Check("null event is refused", !Bugsee.ApplyDefaultNetworkSanitizer(null), "");

            // 2. Inside a network filter. A filter turns the built-in redaction off on Android (documented on the native API), so the plain
            // filter must see the raw values and the one that calls the sanitizer must not. On iOS the SDK's own redaction is not
            // observable the same way, so only what the filter saw is logged.
            Bugsee.SetNetworkEventFilter(PlainFilter);
            yield return Send("extras-plain");
            yield return WaitFor(() => { lock (Gate) return _plainUrl != null; }, WaitSeconds);
            string plainUrl, plainAuth, plainBody;
            lock (Gate) { plainUrl = _plainUrl; plainAuth = _plainAuth; plainBody = _plainBody; }
            Check("filter received the event", plainUrl != null, "filterError=" + _filterError);
            if (android)
                Check("with a filter and no sanitizer the raw values are recorded (why the API exists)",
                    plainUrl != null && plainUrl.Contains(Secret) && (plainBody ?? "").Contains(Password), plainUrl + " " + plainBody);
            else
                Debug.Log("[Sample] extras INFO iOS filter saw url=" + plainUrl + " auth=" + plainAuth);

            Bugsee.SetNetworkEventFilter(SanitizingFilter);
            yield return Send("extras-sanitized");
            yield return WaitFor(() => { lock (Gate) return _sanitizedUrl != null; }, WaitSeconds);
            string sUrl, sAuth, sBody;
            lock (Gate) { sUrl = _sanitizedUrl; sAuth = _sanitizedAuth; sBody = _sanitizedBody; }
            Check("sanitizing filter received the event", sUrl != null, "filterError=" + _filterError);
            Check("sanitizer inside the filter returned " + android, _sanitizerRan && _sanitizerReturned == android,
                "ran=" + _sanitizerRan + " returned=" + _sanitizerReturned);
            if (android)
            {
                Check("sanitizing filter: URL redacted", sUrl != null && !sUrl.Contains(Secret), sUrl);
                Check("sanitizing filter: Authorization redacted", sAuth != null && !sAuth.Contains("SECRETBEARER456"), "Authorization=" + sAuth);
                Check("sanitizing filter: body redacted", sBody != null && !sBody.Contains(Password), sBody);
            }
            Bugsee.SetNetworkEventFilter(null);

            // 3. WebView secure views on a real native WebView. On iOS the SDK's hidden-view set can be read back
            // (UIView.bugseeProtectedView), so the result is checked against the SDK's state, not only against the return value. Android
            // has no such read-back: its checks are the return values and no failure.
            var handle = NativeExtrasWebView.Create();
            Check("native WebView created", handle != null, NativeExtrasWebView.Describe(handle));
            yield return null;
            if (handle != null)
            {
                Check("view is not protected before the call", NativeExtrasWebView.IsProtected(handle) != true, "protected=" + NativeExtrasWebView.IsProtected(handle));
                Check("AddSecureWebView accepts a WebView", NativeExtrasWebView.Add(handle), NativeExtrasWebView.Describe(handle));
                var prot = NativeExtrasWebView.IsProtected(handle);
                if (prot.HasValue)
                    Check("the SDK holds the WebView in its hidden-view set", prot.Value, "bugseeProtectedView=" + prot.Value);
                Check("AddSecureWebView again is harmless", NativeExtrasWebView.Add(handle), "");
                Check("RemoveSecureWebView accepts it", NativeExtrasWebView.Remove(handle), "");
                prot = NativeExtrasWebView.IsProtected(handle);
                if (prot.HasValue)
                    Check("the SDK no longer holds the WebView", !prot.Value, "bugseeProtectedView=" + prot.Value);
            }
            var notWeb = NativeExtrasWebView.CreateNotAWebView();
            Check("a native object that is not a WebView is refused", notWeb != null && !NativeExtrasWebView.Add(notWeb) && !NativeExtrasWebView.Remove(notWeb),
                NativeExtrasWebView.Describe(notWeb));
            Check("a refused view is not protected", NativeExtrasWebView.IsProtected(notWeb) != true, "protected=" + NativeExtrasWebView.IsProtected(notWeb));
            Check("null and zero handles are refused",
                !Bugsee.AddSecureWebView((AndroidJavaObject)null) && !Bugsee.RemoveSecureWebView((AndroidJavaObject)null)
                && !Bugsee.AddSecureWebView(IntPtr.Zero) && !Bugsee.RemoveSecureWebView(IntPtr.Zero), "");
            NativeExtrasWebView.Destroy(handle);
            NativeExtrasWebView.Destroy(notWeb);
            yield return new WaitForSeconds(1f);
            Check("SDK still launched afterwards", Bugsee.IsLaunched, "status=" + Bugsee.Status);
        }

        static INetworkEvent NewSensitive(IBugseeExchangeFactory factory, string id)
        {
            var e = factory.CreateNetworkEvent(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), NetworkEventStage.RequestStarted, id, "sample", "POST");
            e.Url = "https://user:pw12345@example.com/login?token=" + Secret + "&x=1";
            e.Body = "{\"password\":\"" + Password + "\",\"name\":\"anteater\"}";
            e.Headers = new Dictionary<string, string>
            {
                { "Authorization", Bearer },
                { "Content-Type", "application/json" },
            };
            return e;
        }

        static IEnumerator Send(string id)
        {
            Bugsee.AddNetworkEvent(NewSensitive(Bugsee.GetExchangeFactory(), id));
            yield return null;
        }

        static INetworkEvent PlainFilter(INetworkEvent e)
        {
            try
            {
                if (e.Id == "extras-plain")
                    lock (Gate)
                    {
                        _plainUrl = e.Url;
                        _plainBody = e.Body;
                        _plainAuth = Header(e, "Authorization");
                    }
            }
            catch (Exception ex) { _filterError = ex.Message; }
            return e;
        }

        static INetworkEvent SanitizingFilter(INetworkEvent e)
        {
            try
            {
                if (e.Id == "extras-sanitized")
                {
                    var returned = Bugsee.ApplyDefaultNetworkSanitizer(e);
                    lock (Gate)
                    {
                        _sanitizerRan = true;
                        _sanitizerReturned = returned;
                        _sanitizedUrl = e.Url;
                        _sanitizedBody = e.Body;
                        _sanitizedAuth = Header(e, "Authorization");
                    }
                }
            }
            catch (Exception ex) { _filterError = ex.Message; }
            return e;
        }

        static string Header(INetworkEvent e, string name)
        {
            var h = e.Headers;
            string v;
            return h != null && h.TryGetValue(name, out v) ? v : null;
        }

        static IEnumerator WaitFor(Func<bool> condition, float seconds)
        {
            var until = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < until)
            {
                if (condition()) yield break;
                yield return null;
            }
        }

        static void Check(string name, bool ok, string detail)
        {
            if (!ok) _failures++;
            Debug.Log("[Sample] extras " + (ok ? "PASS " : "FAIL ") + name + " :: " + detail);
        }
    }
}
