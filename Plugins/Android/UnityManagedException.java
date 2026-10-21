package com.bugsee.unity;

/**
 * Marker Throwable so worker managed routing sees {@code UnityManagedException}
 * in the exception name (same pattern as FlutterManagedException).
 * The constructor argument is the JSON-in-reason payload.
 *
 * <p>It carries no Java stack. The Android SDK hashes the Java stack of the throwable it is given into the
 * client signature of the report, and this one is only ever built by the wrapper, at the point it hands the
 * payload over: that stack (the JNI entry, Unity's player loop, whichever thread called) says nothing about
 * the managed exception, and it differs with the calling thread. Without it the signature is the class name,
 * the domain, {@code handled} (the SDK adds it for a handled report, so a handled and an unhandled report of one
 * exception still differ) and the payload's own {@code signature}, which comes from the managed exception alone.
 */
public final class UnityManagedException extends Exception {
    private static final long serialVersionUID = 1L;

    public UnityManagedException(String reasonJson) {
        super(reasonJson == null ? "" : reasonJson);
    }

    @Override
    public synchronized Throwable fillInStackTrace() {
        // Keeps the empty stack Throwable starts with: the stack is never captured.
        return this;
    }
}
