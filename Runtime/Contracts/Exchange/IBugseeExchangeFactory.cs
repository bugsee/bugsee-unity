namespace Bugsee.Contracts.Exchange
{
    /// <summary>Mirrors <c>Bugsee.getExchangeFactory()</c> on Android 7.x.</summary>
    public interface IBugseeExchangeFactory
    {
        INetworkEvent CreateNetworkEvent(
            long timestamp,
            NetworkEventStage stage,
            string url,
            string method,
            string mechanism);
    }
}
