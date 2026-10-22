package com.bugsee.library;

/** Test stand-in for the real facade of the same name (not on the test classpath): counts register calls. */
public final class BugseeFeedback {
    public static int calls;

    public static void registerFeedbackExtension() {
        calls++;
    }
}
