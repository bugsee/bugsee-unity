package com.bugsee.unity;

import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertNotNull;

import java.lang.reflect.Method;
import java.lang.reflect.Modifier;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.ArrayList;
import java.util.List;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import org.junit.Test;

/** Every member the keep rules name must exist, or the rule silently protects nothing. */
public class KeepRulesTests {
    static Class<?> typeOf(String name) throws Exception {
        switch (name) {
            case "int": return int.class;
            case "void": return void.class;
            case "int[]": return int[].class;
            default: return Class.forName(name);
        }
    }

    static String rules() throws Exception {
        return new String(
                Files.readAllBytes(AndroidWrapperTests.repoFile("Plugins/Android/bugsee-unity-wrapper.proguard")),
                StandardCharsets.UTF_8);
    }

    @Test
    public void everyStaticMethodTheRulesKeepExistsOnUnityWrapper() throws Exception {
        Matcher block = Pattern.compile("-keep class com\\.bugsee\\.unity\\.UnityWrapper \\{([^}]*)\\}").matcher(rules());
        int checked = 0;
        while (block.find()) {
            Matcher member = Pattern.compile("static\\s+(\\S+)\\s+(\\w+)\\(([^)]*)\\);").matcher(block.group(1));
            while (member.find()) {
                List<Class<?>> params = new ArrayList<Class<?>>();
                for (String p : member.group(3).split(",")) {
                    if (!p.trim().isEmpty()) params.add(typeOf(p.trim()));
                }
                Method m = UnityWrapper.class.getDeclaredMethod(member.group(2), params.toArray(new Class<?>[0]));
                assertNotNull(m);
                assertFalse(member.group(2) + " is not static", !Modifier.isStatic(m.getModifiers()));
                assertFalse(member.group(2) + " returns " + m.getReturnType() + ", rule says " + member.group(1),
                        !m.getReturnType().equals(typeOf(member.group(1))));
                checked++;
            }
        }
        assertFalse("no static members found in the rules", checked == 0);
    }

    @Test
    public void unityWrapperIsCoveredByTheWildcardRuleAndKeepsInternalsNonPublic() throws Exception {
        assertFalse(!Pattern.compile("-keep class com\\.bugsee\\.unity\\.UnityWrapper \\{\\s*public static <methods>;\\s*\\}")
                .matcher(rules()).find());
        // C# only calls public static methods; install() and the test hooks are package-private on purpose.
        for (Method m : UnityWrapper.class.getDeclaredMethods()) {
            if (m.getName().equals("install") || m.getName().endsWith("ForTest")) {
                assertFalse(m.getName() + " must stay non-public", Modifier.isPublic(m.getModifiers()));
            }
        }
    }

    @Test
    public void managedExceptionHasTheConstructorTheRulesKeep() throws Exception {
        assertFalse(!rules().contains("<init>(java.lang.String);"));
        assertNotNull(UnityManagedException.class.getConstructor(String.class));
    }
}
