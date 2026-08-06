namespace Bugsee.Contracts.Exchange
{
    /// <summary>
    /// Filter for exchange events. Return the (possibly mutated) event to keep it,
    /// or <c>null</c> to drop it. Mirrors Android EventFilter with C#-idiomatic sync Func.
    /// </summary>
    public delegate T EventFilter<T>(T data) where T : class;
}
