namespace Bugsee.Contracts.Lifecycle
{
    public interface ILifecycleEventListener
    {
        /// <summary>
        /// Invoked on the Unity main thread, on iOS and Android alike. <paramref name="data"/> is null, a string
        /// (snapshot or request id) or a <c>Dictionary&lt;string, object&gt;</c>.
        /// </summary>
        void OnEvent(string eventType, object data);
    }
}
