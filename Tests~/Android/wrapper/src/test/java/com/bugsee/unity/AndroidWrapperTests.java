package com.bugsee.unity;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertNotEquals;
import static org.junit.Assert.assertNull;
import static org.junit.Assert.assertSame;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import com.bugsee.library.contracts.common.DataRequestProvider;
import com.bugsee.library.contracts.common.DataRequestResultCallback;
import com.bugsee.library.contracts.lifecycle.LifecycleEventListener;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.concurrent.Executor;
import java.util.stream.Stream;
import org.junit.Test;

public class AndroidWrapperTests {
    @Test
    public void managedExceptionKeepsReasonJson() {
        assertEquals("{\"signature\":\"abc\"}", new UnityManagedException("{\"signature\":\"abc\"}").getMessage());
    }

    // The SDK hashes the Java stack of the throwable it is given into the client signature; the wrapper builds this one
    // wherever its caller is, so the stack must be empty or the same managed exception would group by calling thread.
    @Test
    public void managedExceptionHasNoJavaStackOnAnyThread() throws Exception {
        assertEquals(0, new UnityManagedException("{}").getStackTrace().length);
        final StackTraceElement[][] fromWorker = new StackTraceElement[1][];
        Thread worker = new Thread(() -> fromWorker[0] = new UnityManagedException("{}").getStackTrace(), "worker");
        worker.start();
        worker.join();
        assertEquals(0, fromWorker[0].length);
        // Control: a plain exception made in this test does carry the caller's stack.
        assertTrue(new Exception("{}").getStackTrace().length > 0);
    }

    @Test
    public void managedExceptionNullReasonBecomesEmpty() {
        assertEquals("", new UnityManagedException(null).getMessage());
    }

    @Test
    public void manifestRegistersTheProviderBeforeLaunch() throws Exception {
        String manifest = new String(
                Files.readAllBytes(repoFile("Plugins/Android/AndroidManifest.xml")),
                StandardCharsets.UTF_8);
        assertTrue(manifest.contains("com.bugsee.unity.UnityWrapperProvider"));
        assertTrue(manifest.contains("android:directBootAware=\"true\""));
        assertTrue(manifest.contains("android:initOrder=\"200\""));
    }

    @Test
    public void ndkRegistrationIsANoOpWhenTheNdkArtifactIsAbsent() {
        // The test module only has bugsee-android; a project that excludes bugsee-android-ndk must still start.
        assertFalse(UnityWrapperProvider.registerNdkExtension());
    }

    @Test
    public void providerRegistersTheNdkExtensionByName() throws Exception {
        String source = new String(
                Files.readAllBytes(repoFile("Plugins/Android/UnityWrapperProvider.java")),
                StandardCharsets.UTF_8);
        assertTrue(source.contains("\"com.bugsee.library.BugseeNdk\""));
        assertTrue(source.contains("\"registerNdkExtension\""));
        String rules = new String(
                Files.readAllBytes(repoFile("Plugins/Android/bugsee-unity-wrapper.proguard")),
                StandardCharsets.UTF_8);
        assertTrue(rules.contains("com.bugsee.library.BugseeNdk"));
    }

    @Test
    public void wrapperReportsItsRealIdentityBeforeAnyManagedCode() {
        UnityWrapper wrapper = new UnityWrapper();
        assertEquals("unity", wrapper.getWrapperType());
        assertEquals(UnityWrapperIdentity.VERSION, wrapper.getWrapperVersion());
        assertNotEquals("unknown", wrapper.getWrapperVersion());
        // The contract says null for "no build"; the literal "unknown" would be reported as one.
        if (UnityWrapperIdentity.BUILD.equals("dev") || UnityWrapperIdentity.BUILD.length() == 0) {
            assertNull(wrapper.getWrapperBuild());
        } else {
            assertEquals(UnityWrapperIdentity.BUILD, wrapper.getWrapperBuild());
        }
        // "dev" is the placeholder of every checkout and release, not a build id.
        assertNull(UnityWrapper.normalizeBuild("dev"));
        assertNull(UnityWrapper.normalizeBuild(""));
        assertNull(UnityWrapper.normalizeBuild(null));
        assertEquals("a1b2c3d", UnityWrapper.normalizeBuild("a1b2c3d"));
    }

    private static final class Recorder implements LifecycleEventListener {
        final ArrayList<Object[]> seen = new ArrayList<Object[]>();

        @Override
        public void onEvent(String eventType, Object data) {
            seen.add(new Object[] { eventType, data });
        }

        String names() {
            StringBuilder out = new StringBuilder();
            for (Object[] event : seen) out.append(event[0]).append(' ');
            return out.toString().trim();
        }
    }

    // There is no Looper on the JVM: run the replay inline, or hold it to interleave events with it.
    private static final Executor INLINE = new Executor() {
        @Override
        public void execute(Runnable task) {
            task.run();
        }
    };

    private static void freshWrapperState() {
        UnityWrapper.resetLifecycleForTest();
        UnityWrapper.replayExecutor = INLINE;
    }

    @Test
    public void earlyLifecycleEventsAreReplayedInOrderWithTheirDataThenForwardedExactlyOnce() {
        freshWrapperState();
        UnityWrapper wrapper = new UnityWrapper();
        HashMap<String, String> data = new HashMap<String, String>();
        data.put("reportId", "r1");
        wrapper.onLifecycleEvent("com.bugsee.lifecycle.Launching", null);
        wrapper.onLifecycleEvent("com.bugsee.lifecycle.SomeFutureEvent", data);

        Recorder listener = new Recorder();
        UnityWrapper.attachLifecycleListener(listener);

        // Replayed in order and with the ORIGINAL data object (not a string rendering of it).
        assertEquals(2, listener.seen.size());
        assertEquals("com.bugsee.lifecycle.Launching", listener.seen.get(0)[0]);
        assertEquals("com.bugsee.lifecycle.SomeFutureEvent", listener.seen.get(1)[0]);
        assertSame(data, listener.seen.get(1)[1]);

        // After attaching, later events go straight through, once each.
        wrapper.onLifecycleEvent("com.bugsee.lifecycle.Launched", null);
        assertEquals(3, listener.seen.size());
        assertEquals("com.bugsee.lifecycle.Launched", listener.seen.get(2)[0]);

        // Attaching again must not replay anything a second time.
        UnityWrapper.attachLifecycleListener(listener);
        assertEquals(3, listener.seen.size());
        freshWrapperState();
    }

    @Test
    public void eventsThatArriveDuringTheReplayAreDeliveredAfterItInOrder() {
        freshWrapperState();
        final ArrayList<Runnable> held = new ArrayList<Runnable>();
        UnityWrapper.replayExecutor = new Executor() {
            @Override
            public void execute(Runnable task) {
                held.add(task);
            }
        };
        UnityWrapper wrapper = new UnityWrapper();
        wrapper.onLifecycleEvent("early1", null);
        wrapper.onLifecycleEvent("early2", null);

        Recorder listener = new Recorder();
        UnityWrapper.attachLifecycleListener(listener);
        assertEquals("the replay has not run yet", 0, listener.seen.size());

        // Live events while the replay is pending must queue behind it, not overtake it or be lost.
        wrapper.onLifecycleEvent("live1", null);
        wrapper.onLifecycleEvent("live2", null);
        assertEquals(0, listener.seen.size());

        held.get(0).run();
        assertEquals("early1 early2 live1 live2", listener.names());

        wrapper.onLifecycleEvent("live3", null);
        assertEquals("early1 early2 live1 live2 live3", listener.names());
        freshWrapperState();
    }

    @Test
    public void aListenerThatCallsBackIntoTheWrapperDuringTheReplayDoesNotDeadlockOrReorder() {
        freshWrapperState();
        final UnityWrapper wrapper = new UnityWrapper();
        wrapper.onLifecycleEvent("early1", null);
        wrapper.onLifecycleEvent("early2", null);
        final Recorder inner = new Recorder();
        LifecycleEventListener reentrant = new LifecycleEventListener() {
            @Override
            public void onEvent(String eventType, Object data) {
                inner.onEvent(eventType, data);
                if (eventType.equals("early1")) wrapper.onLifecycleEvent("raised-by-handler", null);
            }
        };
        UnityWrapper.attachLifecycleListener(reentrant);
        assertEquals("early1 early2 raised-by-handler", inner.names());
        freshWrapperState();
    }

    @Test
    public void aThrowingListenerDoesNotLoseTheEventsAfterIt() {
        freshWrapperState();
        UnityWrapper wrapper = new UnityWrapper();
        wrapper.onLifecycleEvent("early1", null);
        wrapper.onLifecycleEvent("early2", null);
        final Recorder seen = new Recorder();
        UnityWrapper.attachLifecycleListener(new LifecycleEventListener() {
            @Override
            public void onEvent(String eventType, Object data) {
                seen.onEvent(eventType, data);
                if (eventType.equals("early1")) throw new IllegalStateException("handler bug");
            }
        });
        assertEquals("early1 early2", seen.names());
        wrapper.onLifecycleEvent("live", null);
        assertEquals("early1 early2 live", seen.names());
        freshWrapperState();
    }

    @Test
    public void whenTheEarlyBufferIsFullTheEarliestEventsAreKept() {
        freshWrapperState();
        UnityWrapper wrapper = new UnityWrapper();
        for (int i = 0; i < 130; i++) {
            wrapper.onLifecycleEvent("e" + i, null);
        }

        Recorder listener = new Recorder();
        UnityWrapper.attachLifecycleListener(listener);
        assertEquals(128, listener.seen.size());
        assertEquals("e0", listener.seen.get(0)[0]);
        assertEquals("e127", listener.seen.get(127)[0]);
        freshWrapperState();
    }

    @Test
    public void eventsWithoutANameAreIgnoredAndNullListenersRejected() {
        freshWrapperState();
        UnityWrapper wrapper = new UnityWrapper();
        wrapper.onLifecycleEvent(null, null);
        UnityWrapper.attachLifecycleListener(null);
        Recorder listener = new Recorder();
        UnityWrapper.attachLifecycleListener(listener);
        assertEquals(0, listener.seen.size());
        freshWrapperState();
    }

    @Test
    public void compiledWrapperDeclaresTheSdkContract() throws Exception {
        String pool = classPool("com/bugsee/unity/UnityWrapper.class");
        assertTrue(pool.contains("refineContext"));
        assertTrue(pool.contains("getWrapperVersion"));
        assertTrue(pool.contains("getWrapperBuild"));
        assertTrue(pool.contains("getContext"));
        assertTrue(pool.contains("onWrapperChannelAvailable"));
        assertTrue(pool.contains("getSecureRectangles"));
        assertTrue(classPool("com/bugsee/unity/UnityWrapperProvider.class").contains("UnityWrapperProvider"));
    }

    @Test
    public void testCrashIsPostedFromJavaAndKeptForJni() throws Exception {
        // C# calls UnityWrapper.testCrash() by name over JNI; a rename on either side would go unnoticed.
        assertTrue(classPool("com/bugsee/unity/UnityWrapper.class").contains("testCrash"));
        String rules = new String(
                Files.readAllBytes(repoFile("Plugins/Android/bugsee-unity-wrapper.proguard")),
                StandardCharsets.UTF_8);
        assertTrue(rules.contains("static void testCrash();"));
        String bridge = new String(
                Files.readAllBytes(repoFile("Runtime/Platform/Android/AndroidBridge.cs")),
                StandardCharsets.UTF_8);
        assertTrue(bridge.contains("\"testCrash\""));
    }

    private static final class DataReply implements DataRequestResultCallback {
        int count;
        String last = "unset";

        @Override
        public void onResult(String data) {
            count++;
            last = data;
        }
    }

    @Test
    public void requestDataAnswersNullWhenNoProviderIsRegisteredOrTheTypeIsUnknown() {
        UnityWrapper.setManagedDataProvider(null);
        UnityWrapper wrapper = new UnityWrapper();
        DataReply noProvider = new DataReply();
        wrapper.requestData("vh", noProvider);
        assertEquals(1, noProvider.count);
        assertNull(noProvider.last);

        final ArrayList<String> asked = new ArrayList<String>();
        UnityWrapper.setManagedDataProvider(new DataRequestProvider() {
            @Override
            public void requestData(String dataType, DataRequestResultCallback callback) {
                asked.add(dataType);
            }
        });
        try {
            DataReply unknown = new DataReply();
            wrapper.requestData("other", unknown);
            wrapper.requestData(null, unknown);
            assertEquals(2, unknown.count);
            assertNull(unknown.last);
            assertTrue("an unknown type never reaches managed code", asked.isEmpty());
        } finally {
            UnityWrapper.setManagedDataProvider(null);
        }
    }

    @Test
    public void requestDataHandsViewHierarchyToTheProviderAndLetsItAnswerLater() {
        final ArrayList<DataRequestResultCallback> pending = new ArrayList<DataRequestResultCallback>();
        UnityWrapper.setManagedDataProvider(new DataRequestProvider() {
            @Override
            public void requestData(String dataType, DataRequestResultCallback callback) {
                assertEquals("vh", dataType);
                pending.add(callback);
            }
        });
        try {
            DataReply reply = new DataReply();
            new UnityWrapper().requestData("vh", reply);
            assertEquals("the provider owns the answer", 0, reply.count);
            assertEquals(1, pending.size());
            pending.get(0).onResult("{\"class_name\":\"UnityPlayer\"}");
            assertEquals(1, reply.count);
            assertEquals("{\"class_name\":\"UnityPlayer\"}", reply.last);
            DataReply none = new DataReply();
            UnityWrapper.replyNoData(none);
            assertEquals(1, none.count);
            assertNull(none.last);
        } finally {
            UnityWrapper.setManagedDataProvider(null);
        }
    }

    @Test
    public void requestDataAnswersNullWhenTheProviderThrows() {
        UnityWrapper.setManagedDataProvider(new DataRequestProvider() {
            @Override
            public void requestData(String dataType, DataRequestResultCallback callback) {
                throw new IllegalStateException("boom");
            }
        });
        try {
            DataReply reply = new DataReply();
            new UnityWrapper().requestData("vh", reply);
            assertEquals(1, reply.count);
            assertNull(reply.last);
        } finally {
            UnityWrapper.setManagedDataProvider(null);
        }
    }

    @Test
    public void viewHierarchyProviderIsRegisteredFromCSharpByName() throws Exception {
        // C# registers the provider and answers the callback by these names over JNI; a rename on either side would go unnoticed.
        String bridge = new String(
                Files.readAllBytes(repoFile("Runtime/Platform/Android/AndroidBridge.cs")),
                StandardCharsets.UTF_8);
        assertTrue(bridge.contains("\"setManagedDataProvider\""));
        String proxies = new String(
                Files.readAllBytes(repoFile("Runtime/Platform/Android/Proxies/CallbackProxies.cs")),
                StandardCharsets.UTF_8);
        assertTrue(proxies.contains("\"replyNoData\""));
        assertTrue(proxies.contains("com.bugsee.library.contracts.common.DataRequestProvider"));
    }

    @Test
    public void loggedExceptionsTellTheWrapperTheUnityThreadIsBlocked() throws Exception {
        // The SDK files a logged exception on the calling thread and waits for the managed hierarchy reply there; the wrapper must be
        // told so it answers none at once. Both SDK calls sit between the enter and the exit, and the exit is in a finally.
        String bridge = new String(
                Files.readAllBytes(repoFile("Runtime/Platform/Android/AndroidBridge.cs")),
                StandardCharsets.UTF_8);
        int enter = bridge.indexOf("ManagedViewRequests.EnterBlockingNativeCall()");
        int handled = bridge.indexOf("\"logException\", throwable");
        int unhandled = bridge.indexOf("\"logUnhandledException\", throwable");
        int exit = bridge.indexOf("ManagedViewRequests.ExitBlockingNativeCall()");
        assertTrue(enter > 0 && enter < handled && handled < exit && enter < unhandled && unhandled < exit);
        int fin = bridge.lastIndexOf("finally", exit);
        assertTrue("the exit runs in the finally that follows both SDK calls", fin > handled && fin > unhandled && fin < exit);
    }

    static String classPool(String relative) throws Exception {
        byte[] bytes = Files.readAllBytes(findUnderBuild(relative));
        return new String(bytes, StandardCharsets.ISO_8859_1);
    }

    static Path findUnderBuild(String relative) throws Exception {
        Path tail = Paths.get(relative);
        try (Stream<Path> walk = Files.walk(Paths.get("build"))) {
            return walk.filter(path -> path.endsWith(tail)).findFirst()
                    .orElseThrow(() -> new AssertionError("missing " + relative));
        }
    }

    static Path repoFile(String relative) {
        Path dir = Paths.get("").toAbsolutePath();
        while (dir != null && !Files.exists(dir.resolve("package.json"))) {
            dir = dir.getParent();
        }
        if (dir == null) {
            throw new AssertionError("Could not find repo root (package.json)");
        }
        return dir.resolve(relative);
    }
}
