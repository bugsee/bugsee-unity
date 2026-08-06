using Bugsee.Contracts.Options;

namespace Bugsee.Contracts.Exchange
{
    public interface ILogEvent
    {
        string Message { get; set; }
        LogLevel Level { get; set; }
    }
}
