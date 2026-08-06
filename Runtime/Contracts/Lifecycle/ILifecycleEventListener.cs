namespace Bugsee.Contracts.Lifecycle
{
    public interface ILifecycleEventListener
    {
        /// <summary>Invoked on a background/SDK worker thread; marshal to main thread if touching Unity APIs.</summary>
        void OnEvent(string eventType, object data);
    }
}
