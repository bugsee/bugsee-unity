package com.bugsee.unity;

import static org.junit.Assert.assertArrayEquals;
import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertNotSame;
import static org.junit.Assert.assertNull;
import static org.junit.Assert.assertSame;
import static org.junit.Assert.assertTrue;

import com.bugsee.library.contracts.exchange.NetworkEvent;
import com.bugsee.library.contracts.internal.BugseeWrapperChannel;
import com.bugsee.library.contracts.internal.LogSource;
import com.bugsee.library.contracts.options.LogLevel;
import com.bugsee.library.contracts.reporting.Report;
import java.lang.reflect.Field;
import java.lang.reflect.InvocationHandler;
import java.lang.reflect.Method;
import java.lang.reflect.Proxy;
import java.util.ArrayList;
import java.util.Map;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicReference;
import org.junit.After;
import org.junit.Test;

/**
 * The static entry points C# reaches over JNI (channelLog, channelAddNetwork, setSecureBuffer, refineContext) and the
 * report callbacks the SDK calls on the wrapper. Not covered here: the Android filter and report proxies. They are all C#
 * AndroidJavaProxy classes (Runtime/Platform/Android/Proxies/CallbackProxies.cs), so they cannot run on a JVM and are not
 * compiled into Tests~/WrapperPolicy either; only their policy and source text are checked there (issue #204 stays open
 * for C# round-trip tests of them).
 */
public class AndroidChannelTests {
    private static final class LogCall {
        String tag;
        String message;
        LogLevel level;
        LogSource source;
    }

    private static final class RecordingChannel implements BugseeWrapperChannel {
        final ArrayList<LogCall> logs = new ArrayList<LogCall>();
        final ArrayList<Object[]> network = new ArrayList<Object[]>();

        @Override
        public void log(String tag, String message, LogLevel level, LogSource source) {
            LogCall call = new LogCall();
            call.tag = tag;
            call.message = message;
            call.level = level;
            call.source = source;
            logs.add(call);
        }

        @Override
        public void addNetworkEvent(NetworkEvent event, boolean requiresFiltering) {
            network.add(new Object[] { event, requiresFiltering });
        }
    }

    private final UnityWrapper wrapper = new UnityWrapper();

    @After
    public void detachChannel() {
        wrapper.onWrapperChannelAvailable(null);
    }

    private RecordingChannel attach() {
        RecordingChannel channel = new RecordingChannel();
        wrapper.onWrapperChannelAvailable(channel);
        return channel;
    }

    // ---- channelLog ----

    @Test
    public void channelLogMapsEveryManagedLevelToTheNativeOne() {
        RecordingChannel channel = attach();
        int[] managed = { 1, 2, 3, 4, 5 };
        LogLevel[] expected = { LogLevel.Error, LogLevel.Warning, LogLevel.Info, LogLevel.Debug, LogLevel.Verbose };
        for (int i = 0; i < managed.length; i++) UnityWrapper.channelLog("m" + i, managed[i], 6);
        assertEquals(5, channel.logs.size());
        for (int i = 0; i < managed.length; i++) {
            assertEquals("m" + i, channel.logs.get(i).message);
            assertSame("level " + managed[i], expected[i], channel.logs.get(i).level);
        }
    }

    @Test
    public void channelLogTreatsAnUnknownLevelAsInfo() {
        RecordingChannel channel = attach();
        UnityWrapper.channelLog("a", 0, 6);
        UnityWrapper.channelLog("b", -1, 6);
        UnityWrapper.channelLog("c", 99, 6);
        assertEquals(3, channel.logs.size());
        for (LogCall call : channel.logs) assertSame(LogLevel.Info, call.level);
    }

    // The values C# sends are WrapperLogSourcePolicy's: 0, 1, 2, 5 and 98 (Custom, also what anything else resolves to).
    @Test
    public void channelLogMapsEverySourceCSharpSends() {
        RecordingChannel channel = attach();
        int[] sent = { 0, 1, 2, 5, 98 };
        LogSource[] expected = { LogSource.Unknown, LogSource.StdOut, LogSource.StdErr, LogSource.WebView, LogSource.Custom };
        for (int value : sent) UnityWrapper.channelLog("s" + value, 3, value);
        assertEquals(sent.length, channel.logs.size());
        for (int i = 0; i < sent.length; i++) {
            assertNull("Unity messages carry no tag", channel.logs.get(i).tag);
            assertSame("source " + sent[i], expected[i], channel.logs.get(i).source);
        }
    }

    @Test
    public void channelLogFallsBackToCustomForAnOutOfRangeSource() {
        RecordingChannel channel = attach();
        UnityWrapper.channelLog("x", 3, 42);
        UnityWrapper.channelLog("y", 3, -5);
        assertSame(LogSource.Custom, channel.logs.get(0).source);
        assertSame(LogSource.Custom, channel.logs.get(1).source);
    }

    @Test
    public void channelLogKeepsTheMessageVerbatimIncludingEmpty() {
        RecordingChannel channel = attach();
        UnityWrapper.channelLog("  spaced \n multié ", 3, 6);
        UnityWrapper.channelLog("", 3, 6);
        assertEquals("  spaced \n multié ", channel.logs.get(0).message);
        assertEquals("", channel.logs.get(1).message);
    }

    @Test
    public void channelLogIgnoresNullMessagesAndMissingChannel() {
        UnityWrapper.channelLog("nobody listens", 3, 6); // no channel yet: must not throw
        RecordingChannel channel = attach();
        UnityWrapper.channelLog(null, 3, 6);
        assertTrue(channel.logs.isEmpty());
        wrapper.onWrapperChannelAvailable(null);
        UnityWrapper.channelLog("after detach", 3, 6);
        assertTrue(channel.logs.isEmpty());
    }

    @Test
    public void channelLogFollowsTheLatestChannel() {
        RecordingChannel first = attach();
        UnityWrapper.channelLog("one", 3, 6);
        RecordingChannel second = attach();
        UnityWrapper.channelLog("two", 3, 6);
        assertEquals(1, first.logs.size());
        assertEquals(1, second.logs.size());
        assertEquals("two", second.logs.get(0).message);
    }

    // ---- channelAddNetwork ----

    private static NetworkEvent networkEvent(final String url) {
        return (NetworkEvent) Proxy.newProxyInstance(
                NetworkEvent.class.getClassLoader(),
                new Class<?>[] { NetworkEvent.class },
                new InvocationHandler() {
                    @Override
                    public Object invoke(Object proxy, Method method, Object[] args) {
                        if (method.getName().equals("getUrl")) return url;
                        if (method.getName().equals("hashCode")) return System.identityHashCode(proxy);
                        if (method.getName().equals("equals")) return proxy == args[0];
                        return null;
                    }
                });
    }

    @Test
    public void channelAddNetworkForwardsTheSameEventAndAsksForFiltering() {
        RecordingChannel channel = attach();
        NetworkEvent event = networkEvent("https://example.com/a");
        UnityWrapper.channelAddNetwork(event);
        assertEquals(1, channel.network.size());
        assertSame("the native event is handed over untouched", event, channel.network.get(0)[0]);
        assertEquals("events from Unity still go through the user's network filter", Boolean.TRUE, channel.network.get(0)[1]);
        assertEquals("https://example.com/a", ((NetworkEvent) channel.network.get(0)[0]).getUrl());
    }

    @Test
    public void channelAddNetworkIgnoresNullEventAndMissingChannel() {
        UnityWrapper.channelAddNetwork(networkEvent("u")); // no channel: must not throw
        RecordingChannel channel = attach();
        UnityWrapper.channelAddNetwork(null);
        assertTrue(channel.network.isEmpty());
    }

    @Test
    public void channelAddNetworkSendsEachEventOnce() {
        RecordingChannel channel = attach();
        NetworkEvent a = networkEvent("a");
        NetworkEvent b = networkEvent("b");
        UnityWrapper.channelAddNetwork(a);
        UnityWrapper.channelAddNetwork(b);
        assertEquals(2, channel.network.size());
        assertSame(a, channel.network.get(0)[0]);
        assertSame(b, channel.network.get(1)[0]);
    }

    // ---- secure buffers ----

    // Buffer layout: [version, count, rects...]. The SDK treats equal versions as "same rectangles", so every distinct
    // non-empty set below carries its own version (2, 3, ...); version 1 is only the empty set, as in C#'s
    // SecureRectRegistry.Snapshot.
    // The buffers are static; every test uses displays of its own so none sees another's rectangles.
    @Test
    public void secureRectanglesDefaultToVersionOneWithNoRectangles() {
        // Must equal C#'s initial empty snapshot (version 1, no rectangles), or the first real set would look unchanged.
        assertArrayEquals(new int[] { 1, 0 }, wrapper.getSecureRectangles(9100));
    }

    @Test
    public void setSecureBufferThenReadBackThenClear() {
        int display = 9101;
        UnityWrapper.setSecureBuffer(display, new int[] { 2, 1, 10, 20, 30, 40 });
        assertArrayEquals(new int[] { 2, 1, 10, 20, 30, 40 }, wrapper.getSecureRectangles(display));
        // An empty set is how managed code clears the display.
        UnityWrapper.setSecureBuffer(display, new int[] { 1, 0 });
        assertArrayEquals(new int[] { 1, 0 }, wrapper.getSecureRectangles(display));
    }

    @Test
    public void secureBuffersAreKeptPerDisplay() {
        UnityWrapper.setSecureBuffer(9102, new int[] { 2, 1, 1, 2, 3, 4 });
        UnityWrapper.setSecureBuffer(9103, new int[] { 3, 1, 5, 6, 7, 8 });
        assertArrayEquals(new int[] { 2, 1, 1, 2, 3, 4 }, wrapper.getSecureRectangles(9102));
        assertArrayEquals(new int[] { 3, 1, 5, 6, 7, 8 }, wrapper.getSecureRectangles(9103));
        assertArrayEquals("an untouched display is unaffected", new int[] { 1, 0 }, wrapper.getSecureRectangles(9104));
    }

    @Test
    public void nullAndTooShortBuffersAreIgnoredAndKeepTheLastValidOne() {
        int display = 9105;
        UnityWrapper.setSecureBuffer(display, null);
        assertArrayEquals(new int[] { 1, 0 }, wrapper.getSecureRectangles(display));
        UnityWrapper.setSecureBuffer(display, new int[] {});
        UnityWrapper.setSecureBuffer(display, new int[] { 1 });
        assertArrayEquals(new int[] { 1, 0 }, wrapper.getSecureRectangles(display));
        UnityWrapper.setSecureBuffer(display, new int[] { 2, 1, 9, 9, 9, 9 });
        UnityWrapper.setSecureBuffer(display, new int[] { 1 });
        UnityWrapper.setSecureBuffer(display, null);
        assertArrayEquals(new int[] { 2, 1, 9, 9, 9, 9 }, wrapper.getSecureRectangles(display));
    }

    @Test
    public void secureBuffersAreCopiedOnTheWayInAndOut() {
        // A wrapper choice (the SDK does not require copies): neither side can change the stored buffer afterwards.
        int display = 9106;
        int[] given = { 2, 1, 1, 1, 1, 1 };
        UnityWrapper.setSecureBuffer(display, given);
        given[2] = 777; // the caller reuses its array
        int[] first = wrapper.getSecureRectangles(display);
        assertArrayEquals(new int[] { 2, 1, 1, 1, 1, 1 }, first);
        first[3] = 888; // the SDK mutates what it was given
        int[] second = wrapper.getSecureRectangles(display);
        assertNotSame(first, second);
        assertArrayEquals(new int[] { 2, 1, 1, 1, 1, 1 }, second);
    }

    @Test
    public void defaultSecureBufferIsAFreshArrayEachTime() {
        int[] a = wrapper.getSecureRectangles(9107);
        a[1] = 5;
        assertArrayEquals(new int[] { 1, 0 }, wrapper.getSecureRectangles(9107));
    }

    @Test
    public void everyConcurrentReaderGetsAWholeBufferOfItsOwn() throws Exception {
        final int display = 9108;
        final int rounds = 2000;
        final AtomicReference<Throwable> failure = new AtomicReference<Throwable>();
        final AtomicInteger torn = new AtomicInteger();
        final CountDownLatch start = new CountDownLatch(1);
        ArrayList<Thread> threads = new ArrayList<Thread>();
        for (int w = 0; w < 4; w++) {
            final int tag = w + 1;
            threads.add(new Thread(new Runnable() {
                @Override
                public void run() {
                    try {
                        start.await();
                        for (int i = 0; i < rounds; i++) {
                            // Every writer's buffer is internally uniform after the header, so a torn read shows.
                            UnityWrapper.setSecureBuffer(display, new int[] { 1 + tag, 2, tag, tag, tag, tag, tag, tag, tag, tag });
                        }
                    } catch (Throwable t) {
                        failure.compareAndSet(null, t);
                    }
                }
            }));
        }
        for (int r = 0; r < 4; r++) {
            threads.add(new Thread(new Runnable() {
                @Override
                public void run() {
                    try {
                        start.await();
                        for (int i = 0; i < rounds; i++) {
                            int[] seen = wrapper.getSecureRectangles(display);
                            boolean ok = seen.length == 2 ? seen[0] == 1 && seen[1] == 0 : seen.length == 10 && seen[0] >= 2 && seen[1] == 2;
                            for (int k = 3; ok && seen.length == 10 && k < 10; k++) ok = seen[k] == seen[2];
                            if (!ok) torn.incrementAndGet();
                            seen[0] = -1; // scribbling on a copy must never reach another reader
                        }
                    } catch (Throwable t) {
                        failure.compareAndSet(null, t);
                    }
                }
            }));
        }
        for (Thread thread : threads) thread.start();
        start.countDown();
        for (Thread thread : threads) {
            thread.join(30000);
            assertFalse("a worker did not finish", thread.isAlive());
        }
        assertNull(failure.get());
        assertEquals("a reader saw a partial or shared buffer", 0, torn.get());
    }

    // ---- refineContext ----

    // refineContext installs the wrapper into the real SDK, whose static state cannot start on a JVM; marking it
    // installed first skips only that registration and leaves the context logic under test.
    private static boolean INSTALLED_BEFORE;

    private static void restoreInstalled() throws Exception {
        Field installed = UnityWrapper.class.getDeclaredField("installed");
        installed.setAccessible(true);
        installed.setBoolean(null, INSTALLED_BEFORE);
    }

    private static UnityWrapper instance() throws Exception {
        Field installed = UnityWrapper.class.getDeclaredField("installed");
        installed.setAccessible(true);
        INSTALLED_BEFORE = installed.getBoolean(null);
        installed.setBoolean(null, true);
        Field field = UnityWrapper.class.getDeclaredField("INSTANCE");
        field.setAccessible(true);
        return (UnityWrapper) field.get(null);
    }

    private static String[] snapshot(UnityWrapper w) {
        Map<String, String> c = w.getContext();
        return new String[] { c.get("unity_version"), c.get("unity_platform"), c.get("scripting_backend"), c.get("product_name"), w.getWrapperVersion() };
    }

    private static void restore(String[] saved) {
        // refineContext ignores empty values by design, so put the previous state back through it.
        UnityWrapper.refineContext(saved[0], saved[1], saved[2], saved[3], saved[4]);
    }

    @Test
    public void refineContextPublishesTheUnityKeysAndVersion() throws Exception {
        UnityWrapper live = instance();
        String[] saved = snapshot(live);
        try {
            UnityWrapper.refineContext("2022.3.10f1", "Android", "il2cpp", "My Game", "9.9.9");
            Map<String, String> context = live.getContext();
            assertEquals(4, context.size());
            assertEquals("2022.3.10f1", context.get("unity_version"));
            assertEquals("Android", context.get("unity_platform"));
            assertEquals("il2cpp", context.get("scripting_backend"));
            assertEquals("My Game", context.get("product_name"));
            assertEquals("9.9.9", live.getWrapperVersion());
            assertEquals("unity", live.getWrapperType());
        } finally {
            restore(saved);
            restoreInstalled();
        }
    }

    @Test
    public void refineContextKeepsThePreviousValueForNullOrEmptyArguments() throws Exception {
        UnityWrapper live = instance();
        String[] saved = snapshot(live);
        try {
            UnityWrapper.refineContext("1.0", "Android", "mono", "Game", "2.0");
            UnityWrapper.refineContext(null, "", null, "", null);
            UnityWrapper.refineContext("", null, "", null, "");
            Map<String, String> context = live.getContext();
            assertEquals("1.0", context.get("unity_version"));
            assertEquals("Android", context.get("unity_platform"));
            assertEquals("mono", context.get("scripting_backend"));
            assertEquals("Game", context.get("product_name"));
            assertEquals("2.0", live.getWrapperVersion());
            UnityWrapper.refineContext("1.1", null, null, null, null);
            assertEquals("1.1", live.getContext().get("unity_version"));
            assertEquals("Android", live.getContext().get("unity_platform"));
        } finally {
            restore(saved);
            restoreInstalled();
        }
    }

    @Test
    public void freshWrapperContextStartsAtUnknownAndReturnsACopy() {
        UnityWrapper fresh = new UnityWrapper();
        Map<String, String> context = fresh.getContext();
        assertEquals("unknown", context.get("unity_version"));
        assertEquals("unknown", context.get("unity_platform"));
        assertEquals("unknown", context.get("scripting_backend"));
        assertEquals("unknown", context.get("product_name"));
        context.put("unity_version", "tampered");
        assertEquals("unknown", fresh.getContext().get("unity_version"));
    }

    // ---- report callbacks ----

    // The SDK contract marks the report and the completion non-null; the null report here is just a stand-in (the
    // wrapper never reads it) and the null-completion test only checks the wrapper stays defensive.
    private static Report noReport() {
        return null;
    }

    @Test
    public void reportCallbacksRunTheCompletionExactlyOnce() {
        for (boolean terminating : new boolean[] { false, true }) {
            final AtomicInteger before = new AtomicInteger();
            final AtomicInteger after = new AtomicInteger();
            wrapper.onBeforeReportCreated(noReport(), terminating, new Runnable() {
                @Override
                public void run() {
                    before.incrementAndGet();
                }
            });
            wrapper.onAfterReportCreated(noReport(), terminating, new Runnable() {
                @Override
                public void run() {
                    after.incrementAndGet();
                }
            });
            assertEquals("before, terminating=" + terminating, 1, before.get());
            assertEquals("after, terminating=" + terminating, 1, after.get());
        }
    }

    @Test
    public void reportCallbacksCompleteInlineOnTheCallingThread() throws Exception {
        final AtomicReference<Thread> ranOn = new AtomicReference<Thread>();
        final Thread[] caller = new Thread[1];
        Thread worker = new Thread(new Runnable() {
            @Override
            public void run() {
                caller[0] = Thread.currentThread();
                wrapper.onBeforeReportCreated(noReport(), false, new Runnable() {
                    @Override
                    public void run() {
                        ranOn.set(Thread.currentThread());
                    }
                });
            }
        }, "sdk-report-thread");
        worker.start();
        worker.join();
        assertSame("the wrapper completes inline (a wrapper choice; the SDK would also accept a later completion)", caller[0], ranOn.get());
    }

    @Test
    public void reportCallbacksTolerateANullCompletionDefensively() {
        wrapper.onBeforeReportCreated(noReport(), false, null);
        wrapper.onAfterReportCreated(noReport(), true, null);
    }
}
