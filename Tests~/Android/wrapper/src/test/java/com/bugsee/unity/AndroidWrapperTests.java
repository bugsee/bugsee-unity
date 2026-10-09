package com.bugsee.unity;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertTrue;

import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
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
