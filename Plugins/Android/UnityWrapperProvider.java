package com.bugsee.unity;

import com.bugsee.library.Bugsee;
import com.bugsee.library.BugseeExtensionInitProviderBase;
import com.bugsee.library.contracts.common.DataRequestProvider;
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
        registerExtensions();
        UnityWrapper.install();
        return false;
    }

    // The extensions the Bugsee Gradle plugin may consolidate (its ExtensionSpec.KNOWN) and that a Unity app can carry:
    // each AAR's own init provider is stripped from the merged manifest and the plugin is meant to register it from
    // BugseeInitProvider#initializeExtensions(). Seen with plugin 4.0.8 on Unity 6000.0.84f1 / AGP 9.0.0: the providers
    // were gone and that method was empty, so nothing registered (#226). Registering here runs before
    // Application.onCreate and Bugsee.Launch, is idempotent (a no-op when the AAR's provider already registered) and
    // costs nothing without the plugin. Registration also contributes the extension's launch options (NDK, Leak), which
    // the SDK drops when the extension is not registered at Launch. Reflective: an AAR the app does not carry is skipped.
    static final String[][] EXTENSIONS = {
        { "com.bugsee.library.BugseeNdk", "registerNdkExtension" },
        { "com.bugsee.library.BugseeFeedback", "registerFeedbackExtension" },
        { "com.bugsee.library.BugseeLeak", "registerLeakExtension" },
        { "com.bugsee.library.BugseeRemoting", "registerRemotingExtension" },
    };

    static void registerExtensions() {
        for (String[] extension : EXTENSIONS) registerExtension(extension[0], extension[1]);
    }

    /** Calls {@code facade.method()}; true when it ran, false when the facade is absent or the call failed (logged). */
    static boolean registerExtension(String facade, String method) {
        try {
            Class.forName(facade).getMethod(method).invoke(null);
            return true;
        } catch (ClassNotFoundException e) {
            // Expected for Leak and Remoting (not shipped by the wrapper); for NDK it would mean native crash capture is off.
            Log.i("BugseeUnity", "Extension facade not present, not registered: " + facade);
            return false;
        } catch (Throwable t) {
            Log.w("BugseeUnity", "Could not register " + facade + "." + method + "()", t);
            return false;
        }
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
        return normalizeBuild(UnityWrapperIdentity.BUILD);
    }

    /**
     * The contract says null when there is no build; "unknown" would be reported as one, and so would the
     * "dev" placeholder a checkout (and every release, until a build id is stamped) carries.
     */
    static String normalizeBuild(String build) {
        return build == null || build.length() == 0 || build.equals("dev") ? null : build;
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

    // The one flow the SDK asks for is "vh", the managed view hierarchy (specs: sdk/wrapper-data-requests). It is
    // asked on the Android main thread and only Unity's own thread can read the scene, so C# registers a provider
    // that answers later from there; until it does, and for every other type, the answer is "nothing to contribute".
    // Never throws and never stays silent: the SDK holds the report for the reply up to its budget.
    private static final String DATA_TYPE_VIEW_HIERARCHY = "vh";
    private static volatile DataRequestProvider managedDataProvider;

    public static void setManagedDataProvider(DataRequestProvider provider) {
        managedDataProvider = provider;
    }

    /** C# cannot pass a null String through JNI overload resolution, so "no data" has its own entry point. */
    public static void replyNoData(DataRequestResultCallback callback) {
        if (callback != null) callback.onResult(null);
    }

    @Override
    public void requestData(String dataType, DataRequestResultCallback callback) {
        if (callback == null) return;
        DataRequestProvider provider = DATA_TYPE_VIEW_HIERARCHY.equals(dataType) ? managedDataProvider : null;
        if (provider == null) {
            callback.onResult(null);
            return;
        }
        try {
            provider.requestData(dataType, callback);
        } catch (Throwable t) {
            Log.w(TAG, "The managed data provider threw for " + dataType, t);
            callback.onResult(null);
        }
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

    /**
     * Bugsee.testCrash() throws a RuntimeException. Called from Unity's thread over JNI it would
     * only surface as a managed AndroidJavaException that Unity catches and logs, so no crash
     * would ever be reported. Throwing from the main looper leaves it uncaught, like a real crash.
     */
    public static void testCrash() {
        new android.os.Handler(android.os.Looper.getMainLooper()).post(new Runnable() {
            @Override
            public void run() {
                Bugsee.testCrash();
            }
        });
    }

    /**
     * The display's screenshot of a live report as PNG, or null when it has none or cannot be encoded. Blocks the
     * caller until the SDK has enumerated (blockAndWait runs the callback before it returns). It does not use
     * getScreenshot(int, Callback1): that posts its answer to the calling thread's looper or the main one, which
     * a Unity script thread blocking for the reply cannot rely on.
     */
    public static byte[] getReportScreenshotPng(Report report, int displayId) {
        if (report == null) return null;
        PngCollector collector = new PngCollector(displayId);
        try {
            report.enumerateScreenshots(collector, true);
        } catch (Throwable t) {
            Log.w("BugseeUnity", "Reading a report screenshot failed: " + t);
        }
        return collector.png;
    }

    /** Keeps the PNG of one display out of a screenshot enumeration. */
    private static final class PngCollector implements com.bugsee.library.contracts.common.Callback2<Integer, android.graphics.Bitmap> {
        private final int displayId;
        byte[] png;

        PngCollector(int displayId) {
            this.displayId = displayId;
        }

        @Override
        public void run(Integer id, android.graphics.Bitmap bitmap) {
            if (id == null || id != displayId || bitmap == null || bitmap.isRecycled() || png != null) return;
            // A decoded bitmap past the managed pixel limit (4 bytes a pixel) is not encoded and copied across JNI.
            if (bitmap.getByteCount() > 4L * 32 * 1024 * 1024) return;
            java.io.ByteArrayOutputStream out = new java.io.ByteArrayOutputStream();
            if (bitmap.compress(android.graphics.Bitmap.CompressFormat.PNG, 100, out)) {
                png = out.toByteArray();
            }
            // Not recycled: whether the SDK hands out a private copy here is its business, not verified for 7.4.2.
        }
    }

    /**
     * Stores the PNG as the display's screenshot of a live report; null or empty bytes remove it. Returns 1 when
     * applied, 0 when the bytes are not an image Android can decode (nothing changes). Removing goes through here
     * because JNI from C# cannot name the Bitmap overload with a null argument.
     */
    public static int setReportScreenshotPng(Report report, int displayId, byte[] png) {
        if (report == null) return 0;
        android.graphics.Bitmap bitmap = null;
        try {
            if (png == null || png.length == 0) {
                report.setScreenshot(displayId, (android.graphics.Bitmap) null);
                return 1;
            }
            bitmap = android.graphics.BitmapFactory.decodeByteArray(png, 0, png.length);
            if (bitmap == null) return 0;
            report.setScreenshot(displayId, bitmap);
            return 1;
        } catch (Throwable t) {
            // An OutOfMemoryError from a decode, or anything the SDK throws, must not cross JNI into the Unity thread.
            Log.w("BugseeUnity", "Setting a report screenshot failed: " + t);
            return 0;
        } finally {
            // setScreenshot writes the bitmap to its own file before it returns; the decoded copy is not needed.
            if (bitmap != null) bitmap.recycle();
        }
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
