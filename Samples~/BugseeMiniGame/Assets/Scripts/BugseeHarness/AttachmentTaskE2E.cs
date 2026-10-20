using System;
using System.Collections;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Bugsee.Contracts.Reporting;
using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>
    /// Scenarios "attach" and "unobserved" (tagged "[ATTACH]" / "[UNOBS]" in the log).
    /// attach: adds an empty byte attachment, a bytes attachment and a file attachment, both without a MIME type, to a
    /// report from CreateReport (the managed path) and to the live report a ReportHandler gets, logs what the SDK
    /// accepted (returned object, MIME type, attachment count) and uploads both.
    /// unobserved: faults a fire-and-forget Task, drops it, forces a GC and logs whether TaskScheduler.UnobservedTaskException
    /// fired; the wrapper's own handler is installed by the launch option CaptureUnobservedTaskExceptions (env/extra
    /// bugsee_unobserved=0 turns it off), so the native log line for the exception tells whether the wrapper filed it.
    /// </summary>
    public static class AttachmentTaskE2E
    {
        static int _unobservedCount;

        static string Describe(string what, IAttachment att)
        {
            return "[ATTACH] " + what + " -> " + (att == null ? "null" : "accepted mime=" + (att.MimeType ?? "<unset>") + " name=" + att.Name);
        }

        static void AddThree(string tag, IReport report, string filePath)
        {
            Debug.Log(Describe(tag + " empty bytes", report.AddAttachmentBytes(new byte[0], "empty.bin", "application/x-empty")));
            Debug.Log(Describe(tag + " bytes without mime", report.AddAttachmentBytes(Encoding.UTF8.GetBytes("no mime"), "nomime.bin", null)));
            Debug.Log(Describe(tag + " file without mime", report.AddAttachmentFile(filePath, "file.txt", null)));
            Debug.Log("[ATTACH] " + tag + " attachments=" + report.Attachments.Count);
        }

        sealed class Handler : ReportHandlerBase
        {
            public string FilePath;

            public override void OnBeforeReportCreated(IReport report, bool isTerminating, Action completionCallback)
            {
                try
                {
                    report.Summary = "attach-live";
                    AddThree("live", report, FilePath);
                }
                catch (Exception ex)
                {
                    Debug.Log("[ATTACH] live threw " + ex.GetType().Name + ": " + ex.Message);
                }
                completionCallback?.Invoke();
            }
        }

        public static void RunAttach(MonoBehaviour host, string marker) => host.StartCoroutine(AttachRoutine(marker));

        static IEnumerator WaitLaunched(string tag)
        {
            for (var i = 0; i < 120 && !Bugsee.IsLaunched; i++) yield return new WaitForSeconds(0.5f);
            Debug.Log("[" + tag + "] launched=" + Bugsee.IsLaunched + " status=" + Bugsee.Status);
        }

        static IEnumerator AttachRoutine(string marker)
        {
            yield return WaitLaunched("ATTACH");
            var path = Path.Combine(Application.temporaryCachePath, "attach-e2e.txt");
            File.WriteAllText(path, "file attachment " + marker);

            var report = Bugsee.CreateReport();
            report.Summary = "attach-managed-" + marker;
            try { AddThree("managed", report, path); }
            catch (Exception ex) { Debug.Log("[ATTACH] managed threw " + ex.GetType().Name + ": " + ex.Message); }
            Bugsee.Upload(report);
            Debug.Log("[ATTACH] managed uploaded");

            yield return new WaitForSeconds(3f);
            Bugsee.SetReportHandler(new Handler { FilePath = path });
            Bugsee.Upload("attach-live-" + marker, "live report attachments");
            Debug.Log("[ATTACH] live upload requested");
        }

        public static void RunUnobserved(MonoBehaviour host, string marker) => host.StartCoroutine(UnobservedRoutine(marker));

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void FireAndForget(string marker)
        {
            // The Task is not kept: once it is garbage-collected its exception is unobserved.
            Task.Run(() => { throw new InvalidOperationException("e2e unobserved " + marker); });
        }

        static IEnumerator UnobservedRoutine(string marker)
        {
            yield return WaitLaunched("UNOBS");
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                Interlocked.Increment(ref _unobservedCount);
                Debug.Log("[UNOBS] TaskScheduler.UnobservedTaskException fired: " + e.Exception.InnerException?.Message);
            };
            FireAndForget(marker);
            yield return new WaitForSeconds(1f);
            Debug.Log("[UNOBS] before GC: fired=" + _unobservedCount);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            yield return new WaitForSeconds(2f);
            Debug.Log("[UNOBS] after GC: fired=" + _unobservedCount);
        }
    }
}
