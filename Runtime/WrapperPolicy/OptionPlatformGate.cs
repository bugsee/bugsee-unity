using System.Collections.Generic;
using Bugsee.Contracts.Options;

namespace Bugsee.WrapperPolicy
{
    public static class OptionPlatformGate
    {
        static readonly HashSet<string> AndroidOnlyKeys = BuildAndroidOnlyKeys();

        public static readonly IReadOnlyCollection<string> AndroidOnly = AndroidOnlyKeys;

        static HashSet<string> BuildAndroidOnlyKeys()
        {
            return new HashSet<string>
            {
                Options.DetectAndReportExitLowMemory,
                Options.DetectAndReportExitNotResponding,
                Options.DetectAndReportExitNotRespondingAsCrash,
                Options.DetectAndReportExitExcessiveResourceUsage,
                Options.DetectAndReportExitDependencyDied,
                Options.DetectAndReportExitUserRequested,
                Options.DetectAndReportExitUserWasStopped,
                Options.DetectAndReportExitPermissionChanged,
                Options.DetectAndReportExitPackageUpdated,
                Options.DetectAndReportExitPackageStateChanged,
                Options.DetectAndReportExitOther,
                Options.DetectAndReportExitUnknown,
                Options.ReportingTriggerByNotification,
                Options.ReportingTriggerByBroadcast,
            };
        }

        public static IDictionary<string, object> ForIos(IDictionary<string, object> options)
        {
            if (options == null || options.Count == 0)
                return new Dictionary<string, object>();

            var result = new Dictionary<string, object>(options.Count);
            foreach (var kv in options)
            {
                if (kv.Key == null || AndroidOnlyKeys.Contains(kv.Key))
                    continue;
                result[kv.Key] = kv.Value;
            }

            return result;
        }
    }
}
