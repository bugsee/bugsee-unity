package com.bugsee.unity;

import com.bugsee.library.Bugsee;
import com.bugsee.library.BugseeExtensionInitProviderBase;
import com.bugsee.library.contracts.common.DataRequestResultCallback;
import com.bugsee.library.contracts.internal.BugseeWrapper;
import com.bugsee.library.contracts.internal.BugseeWrapperChannel;
import com.bugsee.library.contracts.reporting.Report;
import java.util.HashMap;
import java.util.Map;

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
        // Task 9 stores the channel for host log/network submission.
    }

    @Override
    public int[] getSecureRectangles(int display) {
        return new int[] { 1, 0 };
    }

    @Override
    public void onBeforeReportCreated(Report report, boolean isTerminating, Runnable completion) {
        if (completion != null) completion.run();
    }

    @Override
    public void onAfterReportCreated(Report report, boolean isTerminating, Runnable completion) {
        if (completion != null) completion.run();
    }
}
