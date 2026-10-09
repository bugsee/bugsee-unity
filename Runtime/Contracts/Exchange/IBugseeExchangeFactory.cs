namespace Bugsee.Contracts.Exchange
{
    /// <summary>Mirrors <c>Bugsee.getExchangeFactory()</c> on Android 7.x.</summary>
    public interface IBugseeExchangeFactory
    {
        /// <summary>
        /// Short Android 7.x create: <c>(timestamp, stage, id, mechanism, method)</c>.
        /// Pass <paramref name="id"/> null for an SDK-generated UUID; set URL via <see cref="INetworkEvent.Url"/> after create.
        /// </summary>
        INetworkEvent CreateNetworkEvent(
            long timestamp,
            NetworkEventStage stage,
            string id,
            string mechanism,
            string method);
    }
}
