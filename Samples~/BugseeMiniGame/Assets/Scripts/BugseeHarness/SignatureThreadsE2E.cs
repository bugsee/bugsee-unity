using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Bugsee.Internal;
using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>
    /// Scenario "sigthreads" (issue #107): one managed exception is reported from Unity's main thread and from a worker thread.
    /// On Android the SDK's own signature function (BugseeExceptionProcessor.generateSignature, the one the report is filed under)
    /// is asked (it is an internal SDK class: if it is gone the verdict is INCONCLUSIVE, not PASS) what it computes for the throwable the wrapper hands over, with the SAME payload JSON from each thread:
    ///   [SIGTHREADS] main client=... sdk=... control=...   (and the same for worker)
    ///   [SIGTHREADS] PASS|FAIL|INCONCLUSIVE (SKIP off Android): SDK signatures equal and the control differs
    /// "control" is a plain java.lang.Exception carrying the same JSON, built where UnityManagedException is: it shows the Java
    /// stack the SDK would have hashed had the wrapper's throwable captured one (it differs per thread). "client" is the payload
    /// signature of each thread's own exception: Mono's StackTrace of a caught exception runs on to the thread's root, so it
    /// still differs between the threads (issue #217); it is logged for information, not judged. Both reports are really sent with
    /// Bugsee.LogException, each from its own thread.
    /// </summary>
    public static class SignatureThreadsE2E
    {
        public static void Run(MonoBehaviour host, string marker)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            host.StartCoroutine(Routine(marker));
#else
            // Nothing to probe off Android: send no reports.
            Debug.Log("[SIGTHREADS] SKIP: Android only");
#endif
        }

        static IEnumerator Routine(string marker)
        {
            var waited = 0f;
            while (!Bugsee.IsLaunched && waited < 60f)
            {
                waited += 0.5f;
                yield return new WaitForSeconds(0.5f);
            }

            var main = Observe("main", marker, null);
            Observed worker = null;
            var thread = new Thread(() => worker = Observe("worker", marker, main.Json)) { Name = "sig-worker" };
            thread.Start();
            while (thread.IsAlive) yield return null;

            var clientEqual = worker != null && main.Client == worker.Client;
            var sdkEqual = worker != null && main.Sdk == worker.Sdk;
            // PASS only when both SDK signatures are real hashes, equal, and the control (a stack-carrying throwable) differs between the
            // threads, i.e. the probe could have told. INCONCLUSIVE when the probe did not run; SKIP off Android.
#if UNITY_ANDROID && !UNITY_EDITOR
            var real = worker != null && IsHash(main.Sdk) && IsHash(worker.Sdk) && IsHash(main.Control) && IsHash(worker.Control);
            var controlDiffers = real && main.Control != worker.Control;
            // Equal SDK signatures with equal controls: the probe could not have told, so neither PASS nor FAIL.
            var verdict = !real || (sdkEqual && !controlDiffers) ? "INCONCLUSIVE" : (sdkEqual ? "PASS" : "FAIL");
#else
            var verdict = "SKIP";
#endif
            Debug.Log("[SIGTHREADS] " + verdict
                + ": sdkEqual=" + sdkEqual + " clientEqual(info)=" + clientEqual
                + " controlEqual=" + (worker != null && main.Control == worker.Control)
                + " mainSdk=" + main.Sdk + " workerSdk=" + (worker == null ? "-" : worker.Sdk));
            // Observe() made the real reports from both threads already.
        }

        static bool IsHash(string s) => !string.IsNullOrEmpty(s) && s.Length == 40;

        sealed class Observed
        {
            public string Client = "";
            public string Json = "";
            public string Sdk = "-";
            public string Control = "-";
        }

        // Runs on the calling thread: throws at the one site, reads the client signature, asks the SDK, and files the report.
        static Observed Observe(string thread, string marker, string sharedJson)
        {
            var result = new Observed();
#if UNITY_ANDROID && !UNITY_EDITOR
            var attached = false;
#endif
            try { ThrowAtTheSite(marker); }
            catch (Exception ex)
            {
                var payload = ManagedExceptionPayload.FromException(ex);
                result.Client = payload != null ? payload.signature : "";
#if UNITY_ANDROID && !UNITY_EDITOR
                try
                {
                    AndroidJNI.AttachCurrentThread();
                    attached = true;
                    var json = sharedJson ?? ManagedExceptionPayload.ToJson(payload);
                    result.Json = json;
                    result.Sdk = SdkSignature("com.bugsee.unity.UnityManagedException", json, true);
                    result.Control = SdkSignature("java.lang.Exception", json, true);
                }
                catch (Exception probe)
                {
                    Debug.LogWarning("[SIGTHREADS] probe failed on " + thread + ": " + probe);
                }
#endif
                Debug.Log("[SIGTHREADS] " + thread + " client=" + result.Client + " sdk=" + result.Sdk + " control=" + result.Control);
                Bugsee.LogException(ex, new Dictionary<string, object>
                {
                    { "source", "e2e-sigthreads" },
                    { "marker", marker },
                    { "thread", thread },
                });
            }
#if UNITY_ANDROID && !UNITY_EDITOR
            // Only a worker is detached: the main thread belongs to Unity.
            if (attached && thread != "main") AndroidJNI.DetachCurrentThread();
#endif
            return result;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        // What the SDK would sign a handled report of this throwable with (domain UnityManagedException, as the bridge sets).
        static string SdkSignature(string throwableClass, string reasonJson, bool handled)
        {
            using (var throwable = new AndroidJavaObject(throwableClass, reasonJson))
            using (var map = new AndroidJavaObject("java.util.HashMap"))
            using (var processor = new AndroidJavaClass("com.bugsee.library.reporting.exceptions.BugseeExceptionProcessor"))
            {
                map.Call<AndroidJavaObject>("put", "domain", "UnityManagedException");
                return processor.CallStatic<string>("generateSignature", throwable, handled, map);
            }
        }
#endif

        static void ThrowAtTheSite(string marker)
        {
            throw new InvalidOperationException("E2E sigthreads " + marker);
        }
    }
}
