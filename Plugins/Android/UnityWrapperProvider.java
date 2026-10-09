package com.bugsee.unity;

import com.bugsee.library.Bugsee;
import com.bugsee.library.BugseeExtensionInitProviderBase;
import com.bugsee.library.contracts.common.DataRequestResultCallback;
import com.bugsee.library.contracts.internal.BugseeWrapper;
import com.bugsee.library.contracts.internal.BugseeWrapperChannel;
import com.bugsee.library.contracts.reporting.Report;
import java.util.HashMap;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;

// initOrder 200
public final class UnityWrapperProvider extends BugseeExtensionInitProviderBase {
    @Override
    protected boolean onExtensionCreate() {
        UnityWrapper.install();
        return false;
    }
}

final class UnityWrapper implements BugseeWrapper {
    private static final UnityWrapper INSTANCE = new UnityWrapper();
    private static volatile boolean installed;

    private String unityVersion = "unknown";
    private String platform = "unknown";
    private String scriptingBackend = "unknown";
    private String productName = "unknown";
    private String wrapperVersion = "unknown";
    private static volatile BugseeWrapperChannel channel;
    private static final ConcurrentHashMap<Integer, int[]> secureBuffers =
            new ConcurrentHashMap<Integer, int[]>();

    static void install() {
        if (installed) return;
        installed = true;
        Bugsee.setWrapper(INSTANCE);
    }

    public static void refineContext(
            String unityVersion,
            String platform,
            String scriptingBackend,
            String productName,
            String wrapperVersion) {
        install();
        if (unityVersion != null && unityVersion.length() > 0) INSTANCE.unityVersion = unityVersion;
        if (platform != null && platform.length() > 0) INSTANCE.platform = platform;
        if (scriptingBackend != null && scriptingBackend.length() > 0) {
            INSTANCE.scriptingBackend = scriptingBackend;
        }
        if (productName != null && productName.length() > 0) INSTANCE.productName = productName;
        if (wrapperVersion != null && wrapperVersion.length() > 0) {
            INSTANCE.wrapperVersion = wrapperVersion;
        }
    }

    @Override
    public String getWrapperType() {
        return "unity";
    }

    @Override
    public String getWrapperVersion() {
        return wrapperVersion;
    }

    @Override
    public String getWrapperBuild() {
        return "unknown";
    }

    @Override
    public Map<String, String> getContext() {
        HashMap<String, String> map = new HashMap<String, String>();
        map.put("unity_version", unityVersion);
        map.put("unity_platform", platform);
        map.put("scripting_backend", scriptingBackend);
        map.put("product_name", productName);
        return map;
    }

    @Override
    public void requestData(String dataType, DataRequestResultCallback callback) {
        if (callback != null) callback.onResult(null);
    }

    @Override
    public void onLifecycleEvent(String eventType, Object data) {
        // Wrapper lifecycle is forwarded via setLifecycleEventsListener on the Bugsee facade.
    }

    @Override
    public void onWrapperChannelAvailable(BugseeWrapperChannel value) {
        channel = value;
    }

    public static void clearWrapperChannel() {
        channel = null;
    }

    public static void channelLog(String message, int level, int source) {
        BugseeWrapperChannel current = channel;
        if (current == null || message == null) return;
        com.bugsee.library.contracts.options.LogLevel nativeLevel;
        switch (level) {
            case 1: nativeLevel = com.bugsee.library.contracts.options.LogLevel.Error; break;
            case 2: nativeLevel = com.bugsee.library.contracts.options.LogLevel.Warning; break;
            case 4: nativeLevel = com.bugsee.library.contracts.options.LogLevel.Debug; break;
            case 5: nativeLevel = com.bugsee.library.contracts.options.LogLevel.Verbose; break;
            default: nativeLevel = com.bugsee.library.contracts.options.LogLevel.Info; break;
        }
        com.bugsee.library.contracts.internal.LogSource nativeSource =
                com.bugsee.library.contracts.internal.LogSource.fromRawValue((byte) source, com.bugsee.library.contracts.internal.LogSource.Custom);
        if (nativeSource == null) nativeSource = com.bugsee.library.contracts.internal.LogSource.Custom;
        current.log(null, message, nativeLevel, nativeSource);
    }

    public static void channelAddNetwork(com.bugsee.library.contracts.exchange.NetworkEvent event) {
        com.bugsee.library.contracts.internal.BugseeWrapperChannel current = channel;
        if (current == null || event == null) return;
        current.addNetworkEvent(event, true);
    }

    public static void setSecureBuffer(int display, int[] packed) {
        if (packed == null || packed.length < 2) return;
        secureBuffers.put(display, packed.clone());
    }

    @Override
    public int[] getSecureRectangles(int display) {
        int[] cached = secureBuffers.get(display);
        if (cached != null) return cached.clone();
        return new int[] { 1, 0 };
    }

    @Override
    public void onBeforeReportCreated(Report report, boolean isTerminating, Runnable completion) {
        if (isTerminating) {
            if (completion != null) completion.run();
            return;
        }
        if (completion != null) completion.run();
    }

    @Override
    public void onAfterReportCreated(Report report, boolean isTerminating, Runnable completion) {
        if (isTerminating) {
            if (completion != null) completion.run();
            return;
        }
        if (completion != null) completion.run();
    }
}
