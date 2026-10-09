#if UNITY_ANDROID && !UNITY_EDITOR
using Bugsee.Contracts.Exchange;
using UnityEngine;

namespace Bugsee.Platform.Android
{
    sealed class AndroidExchangeFactory : IBugseeExchangeFactory
    {
        readonly AndroidJavaObject _factory;

        internal AndroidExchangeFactory(AndroidJavaObject factory)
        {
            _factory = factory;
        }

        public INetworkEvent CreateNetworkEvent(
            long timestamp,
            NetworkEventStage stage,
            string url,
            string method,
            string mechanism)
        {
            using (var stageClass = new AndroidJavaClass(
                       "com.bugsee.library.contracts.exchange.NetworkEventStage"))
            using (var javaStage = stageClass.CallStatic<AndroidJavaObject>("valueOf", stage.ToString()))
            {
                var native = _factory.Call<AndroidJavaObject>(
                    "createNetworkEvent",
                    timestamp,
                    javaStage,
                    url ?? "",
                    method ?? "",
                    mechanism ?? "");
                return new AndroidNetworkEvent(native);
            }
        }
    }
}
#endif
