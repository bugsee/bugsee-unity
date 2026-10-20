namespace Bugsee.Contracts.Lifecycle
{
    public interface ILifecycleEventListener
    {
        /// <summary>
        /// Invoked on the Unity main thread, on iOS and Android alike. <paramref name="data"/> is null or a string
        /// (snapshot or request id); the native SDKs send nothing else today (a map would arrive as a
        /// <c>Dictionary&lt;string, object&gt;</c>, with platform-specific number types).
        /// </summary>
        void OnEvent(string eventType, object data);
    }
}
