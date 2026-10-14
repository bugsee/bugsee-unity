package com.bugsee.unity;

import com.bugsee.library.Bugsee;
import com.bugsee.library.BugseeExtensionInitProviderBase;
import com.bugsee.library.contracts.common.DataRequestResultCallback;
import com.bugsee.library.contracts.internal.BugseeWrapper;
import com.bugsee.library.contracts.internal.BugseeWrapperChannel;
import com.bugsee.library.contracts.lifecycle.LifecycleEventListener;
import com.bugsee.library.contracts.reporting.Report;
import android.os.Handler;
import android.os.Looper;
import android.util.Log;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.Executor;

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
    private String wrapperVersion = UnityWrapperIdentity.VERSION;
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
        // The contract says null when there is no build; "unknown" would be reported as one.
        String build = UnityWrapperIdentity.BUILD;
        return build == null || build.length() == 0 ? null : build;
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

    // Lifecycle events reach C# through ONE path: this wrapper. The SDK calls onLifecycleEvent for every event
    // from the moment the provider registers it - before any C# exists (auto-launch, crash recovery) - so the
    // events that arrive first are held, and attachLifecycleListener() replays them in order to the managed
    // listener and then makes it the destination of everything after. Registering the managed listener with
    // Bugsee.setLifecycleEventsListener as well would leave a window in which an event is either lost (neither
    // path ready) or delivered twice (both ready); with a single path there is none.
    //
    // The replay runs OUTSIDE the lock, on the Android main looper, so app code never runs under the lock or
    // inside the C# Launch call. Events that arrive while it runs queue behind it and are flushed in order
    // before the listener becomes the direct destination, so nothing overtakes it.
    private static final String TAG = "BugseeUnity";
    private static final int MAX_EARLY_LIFECYCLE_EVENTS = 128;
    private static final Object lifecycleLock = new Object();
    private static ArrayList<Object[]> earlyLifecycle = new ArrayList<Object[]>();
    private static ArrayList<Object[]> duringReplay; // non-null while a replay is running
    private static LifecycleEventListener lifecycleListener;
    /** Where the replay runs; tests substitute a direct or capturing executor (there is no Looper on the JVM). */
    static volatile Executor replayExecutor = new Executor() {
        @Override
        public void execute(Runnable task) {
            new Handler(Looper.getMainLooper()).post(task);
        }
    };

    @Override
    public void onLifecycleEvent(String eventType, Object data) {
        if (eventType == null) return;
        LifecycleEventListener target;
        synchronized (lifecycleLock) {
            if (duringReplay != null) {
                if (duringReplay.size() < MAX_EARLY_LIFECYCLE_EVENTS) duringReplay.add(new Object[] { eventType, data });
                return;
            }
            target = lifecycleListener;
            if (target == null) {
                // Keep the earliest events (Launching, RelaunchedAfterCrash...); once full, later ones are
                // dropped rather than the ones that explain how the process started.
                if (earlyLifecycle.size() < MAX_EARLY_LIFECYCLE_EVENTS) {
                    earlyLifecycle.add(new Object[] { eventType, data });
                }
                return;
            }
        }
        deliver(target, eventType, data);
    }

    /**
     * Replays the held events, in order and with their original data, to the managed listener, then forwards
     * every later event to it. Attaching the same listener again does nothing.
     */
    public static void attachLifecycleListener(final LifecycleEventListener listener) {
        if (listener == null) return;
        final ArrayList<Object[]> first;
        synchronized (lifecycleLock) {
            if (lifecycleListener == listener || duringReplay != null) return;
            if (lifecycleListener != null) {
                Log.w(TAG, "Replacing the lifecycle listener; events held earlier were already delivered.");
                lifecycleListener = listener;
                return;
            }
            first = earlyLifecycle;
            earlyLifecycle = new ArrayList<Object[]>();
            duringReplay = new ArrayList<Object[]>();
        }
        replayExecutor.execute(new Runnable() {
            @Override
            public void run() {
                ArrayList<Object[]> batch = first;
                while (true) {
                    for (Object[] event : batch) deliver(listener, (String) event[0], event[1]);
                    synchronized (lifecycleLock) {
                        if (duringReplay.isEmpty()) {
                            lifecycleListener = listener;
                            duringReplay = null;
                            return;
                        }
                        batch = duringReplay;
                        duringReplay = new ArrayList<Object[]>();
                    }
                }
            }
        });
    }

    // The SDK guards the listener it calls; so does this, so one throwing handler cannot lose the events after it.
    private static void deliver(LifecycleEventListener listener, String eventType, Object data) {
        try {
            listener.onEvent(eventType, data);
        } catch (Throwable t) {
            Log.w(TAG, "The lifecycle listener threw for " + eventType, t);
        }
    }

    /** Test hook: back to the state before any listener was attached. */
    static void resetLifecycleForTest() {
        synchronized (lifecycleLock) {
            lifecycleListener = null;
            duringReplay = null;
            earlyLifecycle = new ArrayList<Object[]>();
        }
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
