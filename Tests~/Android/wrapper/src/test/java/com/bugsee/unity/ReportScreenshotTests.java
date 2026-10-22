package com.bugsee.unity;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertNull;

import com.bugsee.library.contracts.common.Callback2;
import com.bugsee.library.contracts.reporting.Report;
import java.lang.reflect.InvocationHandler;
import java.lang.reflect.Method;
import java.lang.reflect.Proxy;
import java.util.ArrayList;
import java.util.List;
import org.junit.Test;

/** The report screenshot helpers C# calls (UnityWrapper.getReportScreenshotPng / setReportScreenshotPng). */
public class ReportScreenshotTests {
    /** A Report that records its calls and enumerates the given display ids with a null bitmap. */
    static Report recordingReport(final List<String> calls, final int... displays) {
        return (Report) Proxy.newProxyInstance(Report.class.getClassLoader(), new Class<?>[] { Report.class },
                new InvocationHandler() {
                    @Override
                    @SuppressWarnings("unchecked")
                    public Object invoke(Object proxy, Method method, Object[] args) {
                        if (method.getName().equals("enumerateScreenshots")) {
                            calls.add("enumerate:" + args[1]);
                            for (int d : displays) ((Callback2<Integer, android.graphics.Bitmap>) args[0]).run(d, null);
                        } else if (method.getName().equals("setScreenshot") && args.length == 2) {
                            calls.add("set:" + args[0] + ":" + args[1]);
                        }
                        return null;
                    }
                });
    }

    @Test
    public void aNullOrEmptyPngRemovesTheDisplaysScreenshot() {
        List<String> calls = new ArrayList<String>();
        Report report = recordingReport(calls);
        assertEquals(1, UnityWrapper.setReportScreenshotPng(report, 0, null));
        assertEquals(1, UnityWrapper.setReportScreenshotPng(report, 2, new byte[0]));
        assertEquals(java.util.Arrays.asList("set:0:null", "set:2:null"), calls);
    }

    @Test
    public void aNullReportIsRefusedWithoutAnException() {
        assertEquals(0, UnityWrapper.setReportScreenshotPng(null, 0, null));
        assertNull(UnityWrapper.getReportScreenshotPng(null, 0));
    }

    @Test
    public void readingEnumeratesWithBlockAndWaitAndTellsAnAbsentDisplayFromAnUnreadableOne() {
        List<String> calls = new ArrayList<String>();
        Report report = recordingReport(calls, 0, 1);
        // Display 1 is listed but its bitmap is null: present and unreadable is an empty array, not absent.
        assertEquals(0, UnityWrapper.getReportScreenshotPng(report, 1).length);
        assertNull(UnityWrapper.getReportScreenshotPng(report, 7));
        assertEquals(java.util.Arrays.asList("enumerate:true", "enumerate:true"), calls);
    }
}
