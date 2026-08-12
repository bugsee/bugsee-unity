package com.bugsee.unity;

/**
 * Marker Throwable so worker managed routing sees {@code UnityManagedException}
 * in the exception name (same pattern as FlutterManagedException).
 * The constructor argument is the JSON-in-reason payload.
 */
public final class UnityManagedException extends Exception {
    private static final long serialVersionUID = 1L;

    public UnityManagedException(String reasonJson) {
        super(reasonJson == null ? "" : reasonJson);
    }
}
