using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Bugsee.Contracts.Apm;
using Bugsee.Contracts.Appearance;
using Bugsee.Contracts.Exchange;
using Bugsee.Contracts.Options;
using Bugsee.Contracts.Reporting;
using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>What the Edit Report form collects: applied to the draft report by <see cref="BugseeInteractions.ApplyDraft"/>.</summary>
    public sealed class ReportDraftSpec
    {
        public string Description = "Created from the Reports hill";
        public string Email = "anteater@example.com";
        public IssueSeverity Severity = IssueSeverity.Medium;
        /// <summary>Comma separated; empty clears the labels.</summary>
        public string Labels = "sample, draft";
        public bool AddExtraLabel;
        public string AttrKey = "sample.report";
        public string AttrValue = "draft";
        public bool ClearAttributesFirst;
        /// <summary>Remove the attribute key instead of setting it.</summary>
        public bool RemoveAttr;
        public bool AttachBytes = true;
        public bool AttachFile = true;
        public bool ClearAttachmentsFirst;
    }

    /// <summary>What the Notify form collects.</summary>
    public sealed class NotifySpec
    {
        public string Title = "Anteater ping";
        public string Body = "Sent from the sample";
        public IssueSeverity Severity = IssueSeverity.Medium;
        /// <summary>key=value pairs, comma separated.</summary>
        public string Fields = "source=sample, hill=reporting";
        public bool Urgent;
    }

    /// <summary>What the Breadcrumb form collects.</summary>
    public sealed class BreadcrumbSpec
    {
        public string Category = "sample";
        public string Message = "Anteater reached a hill";
        /// <summary>One of <see cref="Levels"/>.</summary>
        public string Level = "info";
        public static readonly string[] Levels = { "debug", "info", "warning", "error", "fatal" };
    }

    /// <summary>
    /// Sample actions for SDK APIs the first sample hills did not reach: breadcrumbs and network events, report drafts
    /// (CreateReport / Upload(IReport) / Discard), Notify, APM transactions and spans, the async lifecycle calls,
    /// GetLaunchOptions, DeleteCollectedDataOnDevice, LogUnhandledException and Appearance strings.
    /// The catalog dispatches to these; the IMGUI forms and the <c>interactions</c> e2e scenario call the same methods.
    /// </summary>
    public sealed class BugseeInteractions
    {
        readonly BugseeSampleBootstrap _bootstrap;
        int _unhandledCount;
        int _networkCount;
        int _pickups;
        ISpan _transaction;
        ISpan _span;

        public BugseeInteractions(BugseeSampleBootstrap bootstrap)
        {
            _bootstrap = bootstrap;
        }

        public readonly ReportDraftSpec ReportSpec = new ReportDraftSpec();
        public readonly NotifySpec NotifyForm = new NotifySpec();
        public readonly BreadcrumbSpec BreadcrumbForm = new BreadcrumbSpec();

        /// <summary>The report made by New Report, until Send or Discard releases it.</summary>
        public IReport Draft { get; private set; }

        public int Pickups => _pickups;
        public ISpan Transaction => _transaction;
        public ISpan Span => _span;

        /// <summary>The Task of the last Launch/Relaunch/Stop Async bubble (the e2e scenario awaits it).</summary>
        public Task LastAsync { get; private set; }

        void Status(string message) => _bootstrap.SetStatus(message);

        // ---- Exchange -------------------------------------------------------------------------------------------

        public void AddBreadcrumb(string category, string message, string level)
        {
            Bugsee.AddBreadcrumb(category, message, level);
            Status("AddBreadcrumb " + level + " " + category + ": " + message);
        }

        /// <summary>
        /// Two network events (started, completed) built with the SDK's exchange factory. The URL contains
        /// "password" so the sample network filter (Filters bubble) has something to redact.
        /// </summary>
        public void AddNetworkEvents()
        {
            var factory = Bugsee.GetExchangeFactory();
            if (factory == null)
            {
                Status("GetExchangeFactory returned null (SDK not launched?)");
                return;
            }

            var id = "sample-net-" + (++_networkCount);
            var url = "https://example.com/account/password-reset?attempt=" + _networkCount;
            var started = factory.CreateNetworkEvent(NowMs(), NetworkEventStage.RequestStarted, id, "sample", "POST");
            started.Url = url;
            started.Body = "{\"user\":\"anteater\"}";
            started.Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } };
            Bugsee.AddNetworkEvent(started);

            var completed = factory.CreateNetworkEvent(NowMs(), NetworkEventStage.RequestCompleted, id, "sample", "POST");
            completed.Url = url;
            completed.ResponseCode = 200;
            completed.StatusText = "OK";
            completed.Size = 42;
            completed.Body = "{\"ok\":true}";
            Bugsee.AddNetworkEvent(completed);
            Status("AddNetworkEvent x2 id=" + id + " url=" + url);
        }

        static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // ---- Reports --------------------------------------------------------------------------------------------

        public IReport CreateDraft()
        {
            if (Draft != null)
                Bugsee.Discard(Draft); // one draft at a time: a replaced one is released, not leaked
            Draft = Bugsee.CreateReport();
            Status(Draft == null ? "CreateReport returned null (SDK not launched?)" : "CreateReport id=" + Draft.Id);
            return Draft;
        }

        /// <summary>Fills the draft (creating it first when there is none) from the spec; returns a one-line summary.</summary>
        public string ApplyDraft(ReportDraftSpec spec)
        {
            var report = Draft ?? CreateDraft();
            if (report == null) return "no report";

            report.Description = spec.Description;
            report.Email = spec.Email;
            report.Severity = spec.Severity;
            var labels = SplitList(spec.Labels);
            if (labels.Count == 0)
                report.ClearLabels();
            else
                report.SetLabels(labels);
            if (spec.AddExtraLabel)
                report.AddLabel("extra");
            if (spec.ClearAttributesFirst)
                report.ClearAllAttributes();
            if (!string.IsNullOrWhiteSpace(spec.AttrKey))
            {
                if (spec.RemoveAttr)
                    report.RemoveAttribute(spec.AttrKey.Trim());
                else
                    report.SetAttribute(spec.AttrKey.Trim(), spec.AttrValue ?? "");
            }
            if (spec.ClearAttachmentsFirst)
                report.ClearAttachments();
            if (spec.AttachBytes)
            {
                var att = report.AddAttachmentBytes(Encoding.UTF8.GetBytes("bytes attachment"), "sample-bytes.txt", "text/plain");
                att?.SetData("bytes attachment, text updated via IAttachment.SetData(string)");
            }
            if (spec.AttachFile)
            {
                var path = Path.Combine(Application.persistentDataPath, "sample-attachment.txt");
                File.WriteAllText(path, "file attachment written by the sample at " + DateTime.UtcNow.ToString("o"));
                report.AddAttachmentFile(path, "sample-file.txt", "text/plain");
            }

            var summary = "Edit Report id=" + report.Id + " severity=" + report.Severity + " labels=" + report.Labels.Count +
                          " attachments=" + report.Attachments.Count;
            Status(summary);
            return summary;
        }

        public void UploadDraft()
        {
            if (Draft == null)
            {
                Status("Send Report: no draft (New Report first)");
                return;
            }
            var id = Draft.Id;
            Bugsee.Upload(Draft);
            Draft = null;
            Status("Upload(IReport) id=" + id);
        }

        public void DiscardDraft()
        {
            if (Draft == null)
            {
                Status("Discard Report: no draft (New Report first)");
                return;
            }
            var id = Draft.Id;
            Bugsee.Discard(Draft);
            Draft = null;
            Status("Discard id=" + id);
        }

        // ---- Notify ---------------------------------------------------------------------------------------------

        public void SendNotify(NotifySpec spec)
        {
            Bugsee.Notify(spec.Title, spec.Body, spec.Severity, ParseFields(spec.Fields), spec.Urgent);
            Status("Notify \"" + spec.Title + "\" " + spec.Severity + (spec.Urgent ? " urgent" : ""));
        }

        // ---- APM ------------------------------------------------------------------------------------------------

        public void StartTransaction()
        {
            if (_transaction != null && !_transaction.IsFinished)
            {
                Status("Transaction already open (Finish Txn first)");
                return;
            }
            _pickups = 0;
            _transaction = Bugsee.StartTransaction("sample.field-walk", "sample.walk",
                new Dictionary<string, object> { { "hill", "apm" } });
            Status("StartTransaction trace=" + _transaction.TraceId + "; pick up orbs to add child spans");
        }

        public void StartSpan()
        {
            if (_transaction == null || _transaction.IsFinished)
            {
                Status("Child Span: no open transaction (Start Txn first)");
                return;
            }
            if (_span != null && !_span.IsFinished)
                _span.Finish();
            _span = _transaction.StartChild("sample.step", "manual span");
            Status("StartChild span=" + _span.SpanId + " (Finish Txn ends it)");
        }

        public void FinishTransaction()
        {
            if (_transaction == null || _transaction.IsFinished)
            {
                Status("Finish Txn: no open transaction");
                return;
            }
            if (_span != null && !_span.IsFinished)
                _span.Finish(SpanStatus.Ok);
            _transaction.SetStatus(SpanStatus.Ok);
            _transaction.Finish();
            Status("Transaction finished: " + _pickups + " orb spans");
        }

        /// <summary>An orb pickup becomes a finished child span while a transaction is open.</summary>
        public void OnOrbPickup(int score)
        {
            if (_transaction == null || _transaction.IsFinished) return;
            _pickups++;
            var child = _transaction.StartChild("orb.pickup", "orb #" + score);
            child.SetAttribute("score", score);
            child.Finish(SpanStatus.Ok);
        }

        // ---- Session --------------------------------------------------------------------------------------------

        public void LaunchAsync()
        {
            var token = _bootstrap.AppToken;
            if (string.IsNullOrWhiteSpace(token))
            {
                Status("LaunchAsync: no app token set");
                return;
            }
            Track("LaunchAsync", Bugsee.LaunchAsync(token, _bootstrap.LastLaunchOptions));
        }

        public void RelaunchAsync() => Track("RelaunchAsync", Bugsee.RelaunchAsync(_bootstrap.LastLaunchOptions));

        public void StopAsync() => Track("StopAsync", Bugsee.StopAsync());

        void Track(string name, Task task)
        {
            LastAsync = task;
            Status(name + " requested");
            _bootstrap.StartCoroutine(Report(name, task));
        }

        IEnumerator Report(string name, Task task)
        {
            while (!task.IsCompleted) yield return null;
            string result;
            if (task.IsFaulted) result = "faulted: " + task.Exception.GetBaseException().Message;
            else if (task is Task<bool> b) result = b.Result ? "true (accepted)" : "false (refused or cancelled)";
            else result = "done";
            Status(name + " -> " + result + " status=" + Bugsee.Status);
        }

        public void GetLaunchOptions()
        {
            var options = Bugsee.GetLaunchOptions();
            if (options == null)
            {
                Status("GetLaunchOptions: null (SDK not launched)");
                return;
            }
            var sb = new StringBuilder();
            foreach (var pair in options)
                sb.Append("\n  ").Append(pair.Key).Append(" = ").Append(pair.Value);
            Debug.Log("[BugseeSample] GetLaunchOptions " + options.Count + " keys:" + sb);
            Status("GetLaunchOptions: " + options.Count + " keys (see log)");
        }

        public void RequestDeleteData() =>
            BugseeActionForms.Ensure(_bootstrap).ShowConfirm(
                "Delete Collected Data",
                "Deletes the data the SDK collected on this device, including finished reports, and leaves the SDK stopped. Launch it again afterwards.",
                ConfirmDeleteData);

        /// <summary>What the confirm dialog's Delete button runs.</summary>
        public void ConfirmDeleteData()
        {
            Bugsee.DeleteCollectedDataOnDevice();
            Status("DeleteCollectedDataOnDevice requested; the SDK stops (tap Launch to resume)");
        }

        // ---- Logging / Appearance -------------------------------------------------------------------------------

        public void LogUnhandled()
        {
            var n = ++_unhandledCount;
            try
            {
                throw new InvalidOperationException("Sample unhandled exception #" + n + " (reported via LogUnhandledException)");
            }
            catch (Exception ex)
            {
                Bugsee.LogUnhandledException(ex, new Dictionary<string, object> { { "source", "BugseeInteractions" }, { "count", n } });
            }
            Status("LogUnhandledException #" + n + " sent");
        }

        public static readonly string[] AppearanceTextKeys =
        {
            ReportAppearance.SummaryPlaceholder,
            ReportAppearance.DescriptionPlaceholder,
            ReportAppearance.LabelsPlaceholder,
            ReportAppearance.EmailPlaceholder
        };

        /// <summary>null restores the SDK's default text.</summary>
        public void SetAppearanceText(string key, string value)
        {
            Bugsee.Appearance.SetString(key, value);
            Status("Appearance.SetString " + key + "=" + (value ?? "(default)"));
        }

        public string GetAppearanceText(string key) => Bugsee.Appearance.GetString(key);

        // ---- Helpers --------------------------------------------------------------------------------------------

        public static List<string> SplitList(string text)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return list;
            foreach (var part in text.Split(','))
            {
                var t = part.Trim();
                if (t.Length > 0) list.Add(t);
            }
            return list;
        }

        public static Dictionary<string, string> ParseFields(string text)
        {
            var fields = new Dictionary<string, string>();
            foreach (var pair in SplitList(text))
            {
                var eq = pair.IndexOf('=');
                if (eq > 0) fields[pair.Substring(0, eq).Trim()] = pair.Substring(eq + 1).Trim();
            }
            return fields;
        }
    }
}
