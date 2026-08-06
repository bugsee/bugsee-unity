using System.Collections.Generic;
using Bugsee.Contracts.Options;

namespace Bugsee.Contracts.Exchange
{
    public interface IBreadcrumb
    {
        long Timestamp { get; set; }
        string Category { get; set; }
        string Message { get; set; }
        string Type { get; set; }
        LogLevel? Level { get; set; }
        IDictionary<string, object> Data { get; set; }
    }
}
