using System;

namespace Bugsee.Contracts.Reporting
{
    /// <summary>Mirrors com.bugsee.library.contracts.reporting.ReportHandler.</summary>
    public interface IReportHandler
    {
        void OnBeforeReportCreated(IReport report, bool isTerminating, Action completionCallback);
        void OnAfterReportCreated(IReport report, bool isTerminating, Action completionCallback);
    }

    /// <summary>Default no-op handler that always continues the pipeline.</summary>
    public abstract class ReportHandlerBase : IReportHandler
    {
        public virtual void OnBeforeReportCreated(IReport report, bool isTerminating, Action completionCallback)
        {
            completionCallback?.Invoke();
        }

        public virtual void OnAfterReportCreated(IReport report, bool isTerminating, Action completionCallback)
        {
            completionCallback?.Invoke();
        }
    }
}
