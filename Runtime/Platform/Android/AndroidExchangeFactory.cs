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
            string id,
            string mechanism,
            string method)
        {
            using (var stageClass = new AndroidJavaClass(
                       "com.bugsee.library.contracts.exchange.NetworkEvent$NetworkEventStage"))
            using (var javaStage = stageClass.CallStatic<AndroidJavaObject>("valueOf", stage.ToString()))
            {
                var native = _factory.Call<AndroidJavaObject>(
                    "createNetworkEvent",
                    timestamp,
                    javaStage,
                    id,
                    mechanism ?? "",
                    method ?? "");
                if (native == null) return null;
                return new AndroidNetworkEvent(native);
            }
        }
    }
}
#endif
