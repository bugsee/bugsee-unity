using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Bugsee.Contracts.Lifecycle;
using Bugsee.Contracts.Options;
using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>
    /// Device check for the interaction hills (BUGSEE_E2E_SCENARIO=interactions, issue #116): runs every new action through
    /// the same BugseeActionCatalog.Run item a bubble calls and the same BugseeInteractions methods the forms call, then
    /// reads the effect back, logging <c>[Sample] interactions PASS|FAIL name :: detail</c> and a final <c>done failures=N</c>.
    /// Taps on bubbles and typing into the IMGUI forms are not driven; the forms are opened and closed, and their apply step
    /// is the BugseeInteractions method called here.
    /// </summary>
    static class InteractionsE2E
    {
        static int _failures;
        static readonly RectInt MyRect = new RectInt(30, 30, 200, 100);
        // What the scenario changed and must put back exactly (also when it throws half way).
        static string _textKey;
        static string _textOriginal;
        static bool _textTouched;

        public static IEnumerator Run(BugseeSampleBootstrap b)
        {
            _failures = 0;
            _textTouched = false;
            var c = b.Catalog;
            var origRectActive = c.SecureRectActive;
            var origRect = c.SecureRect;
            Debug.Log("[Sample] interactions e2e start launched=" + Bugsee.IsLaunched + " status=" + Bugsee.Status);
            var body = Body(b, c);
            while (true)
            {
                bool more;
                try
                {
                    more = body.MoveNext();
                }
                catch (Exception ex)
                {
                    Check("scenario completed without an exception", false, ex.GetType().Name + ": " + ex.Message);
                    break;
                }
                if (!more) break;
                yield return body.Current;
            }

            // Restore global state, whatever happened.
            try { BugseeActionForms.Instance?.Close(); } catch { }
            try { if (c.Interactions.Transaction != null && !c.Interactions.Transaction.IsFinished) c.Interactions.FinishTransaction(); } catch { }
            try { if (c.Interactions.Draft != null) c.Interactions.DiscardDraft(); } catch { }
            try { Bugsee.RemoveSecureRectangle(MyRect); c.RememberSecureRect(origRect, origRectActive); } catch { } // only mine
            try { if (_textTouched) c.Interactions.SetAppearanceText(_textKey, _textOriginal); } catch { }
            if (Bugsee.Status == BugseeStatus.Launching || Bugsee.Status == BugseeStatus.Stopping)
                yield return WaitFor(() => Bugsee.Status == BugseeStatus.Launched || Bugsee.Status == BugseeStatus.Stopped, 40f);
            if (Bugsee.Status == BugseeStatus.Stopped) // not while Launching: a second launch would race the first
            {
                try { var t = Bugsee.LaunchAsync(b.AppToken, b.LastLaunchOptions); }
                catch (Exception ex) { Debug.Log("[Sample] interactions relaunch in cleanup threw " + ex.Message); }
                yield return WaitFor(() => Bugsee.IsLaunched, 40f);
            }
            Debug.Log("[Sample] interactions e2e done failures=" + _failures + " launched=" + Bugsee.IsLaunched);
        }

        static IEnumerator Body(BugseeSampleBootstrap b, BugseeActionCatalog c)
        {
            var x = c.Interactions;

            // 0. Every new action sits on a hill, and every hill got a summit.
            var onHills = BugseeStationSpawner.WaypointOrder;
            var missing = Enum.GetValues(typeof(BugseeDemoAction)).Cast<BugseeDemoAction>().Where(a => !onHills.Contains(a)).ToArray();
            Check("every demo action is on a hill", missing.Length == 0, "missing=" + string.Join(",", missing.Select(m => m.ToString()).ToArray()));
            var hills = UnityEngine.Object.FindObjectsOfType<BugseeHillStation>().Length;
            Check("every action group has a hill in the world", hills == BugseeStationSpawner.Groups.Length,
                hills + " hills for " + BugseeStationSpawner.Groups.Length + " groups");

            // 1. Exchange: breadcrumb (form opens, then the apply step), network events from the SDK's factory.
            c.Run(BugseeDemoAction.Breadcrumb);
            Check("Breadcrumb bubble opens its form", BugseeActionForms.IsOpen, "open=" + BugseeActionForms.IsOpen);
            BugseeActionForms.Instance.Close();
            foreach (var level in BreadcrumbSpec.Levels)
            {
                x.BreadcrumbForm.Level = level;
                x.AddBreadcrumb("e2e", "interactions " + level, level);
            }
            Check("AddBreadcrumb at every level", c.LastStatus.StartsWith("AddBreadcrumb fatal"), c.LastStatus);
            c.Run(BugseeDemoAction.NetworkEvent);
            Check("AddNetworkEvent via GetExchangeFactory", c.LastStatus.StartsWith("AddNetworkEvent x2 id=sample-net-") && c.LastStatus.Contains("password"), c.LastStatus);

            // 2. Reports: create, edit (every IReport member), upload, discard.
            c.Run(BugseeDemoAction.ReportCreate);
            var d = x.Draft;
            // A draft's Id is empty on both platforms (observed): the native report is created at upload.
            Check("CreateReport returns a report", d != null, "id=" + (d != null ? "'" + d.Id + "'" : "(null)"));
            c.Run(BugseeDemoAction.ReportEdit);
            Check("Edit Report bubble opens its form", BugseeActionForms.IsOpen, "open=" + BugseeActionForms.IsOpen);
            BugseeActionForms.Instance.Close();
            if (d != null)
            {
                var spec = x.ReportSpec;
                spec.Description = "e2e description";
                spec.Email = "e2e@example.com";
                spec.Severity = IssueSeverity.Critical;
                spec.Labels = "e2e, interactions";
                spec.AddExtraLabel = true;
                spec.AttrKey = "e2e.attr";
                spec.AttrValue = "e2e-value";
                spec.ClearAttributesFirst = false;
                spec.RemoveAttr = false;
                spec.ClearAttachmentsFirst = false;
                spec.AttachBytes = true;
                spec.AttachFile = true;
                x.ApplyDraft(spec);
                Check("report Description reads back", d.Description == "e2e description", "value=" + d.Description);
                Check("report Email reads back", d.Email == "e2e@example.com", "value=" + d.Email);
                Check("report Severity reads back", d.Severity == IssueSeverity.Critical, "value=" + d.Severity);
                Check("report labels (SetLabels + AddLabel)", d.Labels.Count == 3 && d.Labels.Contains("e2e") && d.Labels.Contains("extra"),
                    "labels=" + string.Join("|", d.Labels.ToArray()));
                Check("report attribute reads back (SetAttribute/GetAttribute)", "e2e-value".Equals(d.GetAttribute("e2e.attr")),
                    "value=" + (d.GetAttribute("e2e.attr") ?? "(null)") + " count=" + d.Attributes.Count);
                Check("report has the bytes and the file attachment", d.Attachments.Count == 2, "count=" + d.Attachments.Count);
                if (d.Attachments.Count > 0)
                    Check("bytes attachment keeps its name", d.Attachments[0].Name == "sample-bytes.txt", "name=" + d.Attachments[0].Name);

                spec.RemoveAttr = true;
                spec.AttachBytes = false;
                spec.AttachFile = false;
                spec.ClearAttachmentsFirst = true;
                spec.Labels = "";
                spec.AddExtraLabel = false;
                x.ApplyDraft(spec);
                Check("RemoveAttribute removes it", d.GetAttribute("e2e.attr") == null, "value=" + (d.GetAttribute("e2e.attr") ?? "(null)"));
                Check("ClearAttachments empties the list", d.Attachments.Count == 0, "count=" + d.Attachments.Count);
                Check("ClearLabels empties the labels", d.Labels.Count == 0, "count=" + d.Labels.Count);
                d.SetAttribute("e2e.second", "2");
                spec.ClearAttributesFirst = true;
                spec.RemoveAttr = false;
                spec.AttrKey = "";
                x.ApplyDraft(spec);
                Check("ClearAllAttributes empties the attributes", d.Attributes.Count == 0, "count=" + d.Attributes.Count);
            }
            c.Run(BugseeDemoAction.ReportUpload);
            Check("Send Report uploads the draft and releases it", x.Draft == null && c.LastStatus.StartsWith("Upload(IReport) id="), c.LastStatus);
            c.Run(BugseeDemoAction.ReportCreate);
            var id2 = x.Draft != null ? x.Draft.Id : "(null)";
            c.Run(BugseeDemoAction.ReportDiscard);
            Check("Discard Report releases the draft", x.Draft == null && c.LastStatus == "Discard id=" + id2, c.LastStatus);
            c.Run(BugseeDemoAction.ReportUpload);
            Check("Send Report without a draft says so", c.LastStatus.StartsWith("Send Report: no draft"), c.LastStatus);

            // 3. Notify: through the form's apply step; an empty title is ignored without throwing.
            c.Run(BugseeDemoAction.Notify);
            Check("Notify bubble opens its form", BugseeActionForms.IsOpen, "open=" + BugseeActionForms.IsOpen);
            BugseeActionForms.Instance.Close();
            x.NotifyForm.Title = "e2e notify";
            x.NotifyForm.Urgent = true;
            var sent = x.SendNotify(x.NotifyForm);
            Check("Notify (urgent, with fields) is sent", sent && c.LastStatus == "Notify sent \"e2e notify\" " + x.NotifyForm.Severity + " urgent", c.LastStatus);
            Check("Notify fields parse", BugseeInteractions.ParseFields(x.NotifyForm.Fields).Count == 2, "fields=" + x.NotifyForm.Fields);
            x.NotifyForm.Title = "";
            x.NotifyForm.Urgent = false;
            var ignored = x.SendNotify(x.NotifyForm);
            Check("Notify with an empty title is reported as ignored", !ignored && c.LastStatus == "Notify ignored: a title is required", c.LastStatus);
            x.NotifyForm.Title = "Anteater ping";

            // 4. APM: a transaction across orb pickups with a child span.
            c.Run(BugseeDemoAction.ApmStart);
            var txn = x.Transaction;
            Check("StartTransaction returns an open span", txn != null && !txn.IsFinished && !string.IsNullOrEmpty(txn.TraceId),
                "trace=" + (txn != null ? txn.TraceId : "(null)"));
            c.Run(BugseeDemoAction.ApmStart);
            Check("a second Start Txn is refused while one is open", c.LastStatus.StartsWith("Transaction already open"), c.LastStatus);
            c.Run(BugseeDemoAction.ApmSpan);
            var span = x.Span;
            Check("Child Span starts a child", span != null && !span.IsFinished && !string.IsNullOrEmpty(span.SpanId), "span=" + (span != null ? span.SpanId : "(null)"));
            x.OnOrbPickup(1);
            x.OnOrbPickup(2);
            Check("orb pickups become child spans", x.Pickups == 2, "pickups=" + x.Pickups);
            c.Run(BugseeDemoAction.ApmFinish);
            Check("Finish Txn finishes the transaction and its open child",
                txn != null && txn.IsFinished && span != null && span.IsFinished, "txn=" + (txn != null && txn.IsFinished) + " span=" + (span != null && span.IsFinished));
            c.Run(BugseeDemoAction.ApmFinish);
            Check("Finish Txn without one open says so", c.LastStatus == "Finish Txn: no open transaction", c.LastStatus);
            c.Run(BugseeDemoAction.ApmSpan);
            Check("Child Span without a transaction says so", c.LastStatus.StartsWith("Child Span: no open transaction"), c.LastStatus);

            // 5. Logging and privacy.
            c.Run(BugseeDemoAction.LogUnhandled);
            Check("LogUnhandledException", c.LastStatus.StartsWith("LogUnhandledException #") && c.LastStatus.EndsWith("sent"), c.LastStatus);
            var rect = MyRect;
            Bugsee.AddSecureRectangle(rect);
            c.RememberSecureRect(rect, true); // what the Set Secure Rect form does on apply
            c.Run(BugseeDemoAction.RemoveSecureRect);
            Check("RemoveSecureRectangle removes the added rect", c.LastStatus.StartsWith("RemoveSecureRectangle LTRB=(30,30,230,130)") && !c.SecureRectActive, c.LastStatus);
            c.Run(BugseeDemoAction.RemoveSecureRect);
            Check("Remove Rect without a rect says so", c.LastStatus.StartsWith("RemoveSecureRectangle: no rect"), c.LastStatus);

            // 6. Appearance strings.
            c.Run(BugseeDemoAction.AppearanceTexts);
            Check("Texts bubble opens its form", BugseeActionForms.IsOpen, "open=" + BugseeActionForms.IsOpen);
            BugseeActionForms.Instance.Close();
            var key = BugseeInteractions.AppearanceTextKeys[0];
            var before = x.GetAppearanceText(key);
            _textKey = key;
            _textOriginal = before;
            _textTouched = true;
            x.SetAppearanceText(key, "e2e summary placeholder");
            Check("Appearance.SetString/GetString round trip", x.GetAppearanceText(key) == "e2e summary placeholder", "value=" + x.GetAppearanceText(key) + " before=" + (before ?? "(null)"));
            try { x.SetAppearanceText(key, before); Check("Appearance string restored", x.GetAppearanceText(key) == before, "value=" + (x.GetAppearanceText(key) ?? "(null)")); }
            catch (Exception ex) { Check("Appearance string restored", false, ex.GetType().Name + ": " + ex.Message); }

            // 7. GetLaunchOptions: the sample launches with CaptureLogs = true.
            c.Run(BugseeDemoAction.LaunchOptions);
            var options = Bugsee.GetLaunchOptions();
            Check("GetLaunchOptions is not empty", options != null && options.Count > 0 && c.LastStatus.StartsWith("GetLaunchOptions: "), c.LastStatus);
            object logs = null;
            var hasLogs = options != null && options.TryGetValue(global::Bugsee.Contracts.Options.Options.CaptureLogs, out logs);
            Check("GetLaunchOptions carries a known key", hasLogs && true.Equals(logs), "capture.logs=" + (logs ?? "(absent)"));

            // 8. Async lifecycle. Each step waits for its Task, then for the SDK state; it ends launched again.
            c.Run(BugseeDemoAction.StopAsync);
            yield return WaitTask(x);
            Check("StopAsync completes", x.LastAsync != null && x.LastAsync.IsCompleted && !x.LastAsync.IsFaulted, Describe(x.LastAsync));
            yield return WaitFor(() => !Bugsee.IsLaunched, 10f);
            Check("the SDK is stopped after StopAsync", !Bugsee.IsLaunched, "status=" + Bugsee.Status);

            c.Run(BugseeDemoAction.LaunchAsync);
            yield return WaitTask(x);
            Check("LaunchAsync completes with true", ResultOf(x.LastAsync) == true, Describe(x.LastAsync));
            yield return WaitFor(() => Bugsee.IsLaunched, 40f);
            Check("the SDK is launched after LaunchAsync", Bugsee.IsLaunched, "status=" + Bugsee.Status);

            c.Run(BugseeDemoAction.RelaunchAsync);
            yield return WaitTask(x);
            Check("RelaunchAsync completes with true", ResultOf(x.LastAsync) == true, Describe(x.LastAsync));
            yield return WaitFor(() => Bugsee.IsLaunched, 40f);
            Check("the SDK is launched after RelaunchAsync", Bugsee.IsLaunched, "status=" + Bugsee.Status);
            yield return null;
            Check("the bubble reports the async result", c.LastStatus.StartsWith("RelaunchAsync -> "), c.LastStatus);

            // 8b. Stop and Delete Data close the SDK's open report: a draft made before must not wedge the Reports bubbles.
            foreach (var action in new[] { BugseeDemoAction.ReportCreate, BugseeDemoAction.ReportUpload, BugseeDemoAction.ReportDiscard })
                yield return StaleDraft(b, c, action);

            // 9. Delete Data behind its confirm: the scenario presses the confirm, then launches again.
            c.Run(BugseeDemoAction.DeleteData);
            Check("Delete Data bubble opens a confirm, nothing is deleted yet", BugseeActionForms.IsOpen && Bugsee.IsLaunched,
                "open=" + BugseeActionForms.IsOpen + " launched=" + Bugsee.IsLaunched);
            BugseeActionForms.Instance.Confirm();
            Check("confirming runs DeleteCollectedDataOnDevice", c.LastStatus.StartsWith("DeleteCollectedDataOnDevice requested"), c.LastStatus);
            yield return WaitFor(() => !Bugsee.IsLaunched, 30f);
            Check("the SDK is left stopped by the delete", !Bugsee.IsLaunched, "status=" + Bugsee.Status);
            c.Run(BugseeDemoAction.LaunchAsync);
            yield return WaitTask(x);
            yield return WaitFor(() => Bugsee.IsLaunched, 40f);
            Check("Launch Async resumes the SDK after the delete", Bugsee.IsLaunched, "status=" + Bugsee.Status + " " + Describe(x.LastAsync));
        }

        // A draft is made, the SDK is stopped and launched behind the sample's back, then the bubble runs on the stale draft.
        static IEnumerator StaleDraft(BugseeSampleBootstrap b, BugseeActionCatalog c, BugseeDemoAction action)
        {
            var x = c.Interactions;
            c.Run(BugseeDemoAction.ReportCreate);
            var stale = x.Draft;
            var stop = Bugsee.StopAsync();
            yield return WaitFor(() => stop.IsCompleted, 20f);
            yield return WaitFor(() => !Bugsee.IsLaunched, 10f);
            var launch = Bugsee.LaunchAsync(b.AppToken, b.LastLaunchOptions);
            yield return WaitFor(() => Bugsee.IsLaunched, 40f);
            c.Run(action);
            var status = c.LastStatus;
            var ok = !status.StartsWith("Error") &&
                     (action == BugseeDemoAction.ReportCreate ? x.Draft != null && !ReferenceEquals(x.Draft, stale) : x.Draft == null);
            Check("stale draft (SDK stopped since New Report): " + action + " works", ok, "launched=" + Bugsee.IsLaunched + " :: " + status);
            if (x.Draft != null) x.DiscardDraft();
        }

        static IEnumerator WaitTask(BugseeInteractions x)
        {
            yield return WaitFor(() => x.LastAsync == null || x.LastAsync.IsCompleted, 20f);
            yield return null; // let the bubble's status coroutine report
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

        static bool? ResultOf(Task task) => task is Task<bool> t && t.IsCompleted && !t.IsFaulted ? t.Result : (bool?)null;

        static string Describe(Task task)
        {
            if (task == null) return "no task";
            if (task.IsFaulted) return "faulted: " + task.Exception.GetBaseException().Message;
            if (!task.IsCompleted) return "pending";
            return task is Task<bool> t ? "result=" + t.Result : "completed";
        }

        static void Check(string name, bool ok, string detail)
        {
            if (!ok) _failures++;
            Debug.Log("[Sample] interactions " + (ok ? "PASS " : "FAIL ") + name + " :: " + detail);
        }
    }
}
