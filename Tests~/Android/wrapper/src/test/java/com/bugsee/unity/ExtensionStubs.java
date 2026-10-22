package com.bugsee.unity;

/** Facades for UnityWrapperProvider.registerExtension tests. */
public final class ExtensionStubs {
    public static int calls;

    public static void register() {
        calls++;
    }

    public static void explode() {
        throw new IllegalStateException("boom");
    }
}
