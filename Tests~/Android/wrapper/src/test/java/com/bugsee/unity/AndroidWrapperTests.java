package com.bugsee.unity;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertNotEquals;
import static org.junit.Assert.assertNull;
import static org.junit.Assert.assertSame;
import static org.junit.Assert.assertTrue;

import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
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
    public void wrapperReportsItsRealIdentityBeforeAnyManagedCode() {
        UnityWrapper wrapper = new UnityWrapper();
        assertEquals("unity", wrapper.getWrapperType());
        assertEquals(UnityWrapperIdentity.VERSION, wrapper.getWrapperVersion());
        assertNotEquals("unknown", wrapper.getWrapperVersion());
        // The contract says null for "no build"; the literal "unknown" would be reported as one.
        String build = UnityWrapperIdentity.BUILD;
        if (build == null || build.length() == 0) {
            assertNull(wrapper.getWrapperBuild());
        } else {
            assertEquals(build, wrapper.getWrapperBuild());
        }
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
