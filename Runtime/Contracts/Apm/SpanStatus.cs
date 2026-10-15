namespace Bugsee.Contracts.Apm
{
    /// <summary>
    /// Terminal status of a span. The values are the iOS SDK's wire values; Android is mapped by name
    /// (see <c>SpanPolicy.AndroidStatusName</c>).
    /// </summary>
    public enum SpanStatus
    {
        Ok = 0,
        Error = 1,
        Timeout = 2,
        Cancelled = 3,
        DeadlineExceeded = 4,
        Unknown = 5
    }
}
