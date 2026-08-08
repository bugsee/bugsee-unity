using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Bugsee.Editor
{
    /// <summary>
    /// EDM4U's Android resolver spawns <c>Temp/PlayServicesResolverGradle/gradlew</c> with
    /// Gradle 5.1.1. That wrapper uses process <c>JAVA_HOME</c> / PATH <c>java</c>, not
    /// Unity → External Tools → JDK. On machines whose default JDK is 17+, resolve fails with
    /// <c>org.codehaus.groovy.vmplugin.v7.Java7</c>. Pin <c>JAVA_HOME</c> to Unity's Android OpenJDK.
    /// </summary>
    [InitializeOnLoad]
    static class BugseeEdmJavaHomeBootstrap
    {
        const string SessionKey = "Bugsee.EdmJavaHomePinned";

        static BugseeEdmJavaHomeBootstrap()
        {
            try
            {
                string playback = BuildPipeline.GetPlaybackEngineDirectory(BuildTarget.Android, BuildOptions.None);
                if (string.IsNullOrEmpty(playback))
                {
                    return;
                }

                string jdk = Path.GetFullPath(Path.Combine(playback, "OpenJDK"));
                string javaBin = Path.Combine(jdk, "bin", "java");
                if (!Directory.Exists(jdk) || !File.Exists(javaBin))
                {
                    return;
                }

                string current = Environment.GetEnvironmentVariable("JAVA_HOME");
                if (string.Equals(current, jdk, StringComparison.Ordinal))
                {
                    return;
                }

                Environment.SetEnvironmentVariable("JAVA_HOME", jdk);

                if (!SessionState.GetBool(SessionKey, false))
                {
                    SessionState.SetBool(SessionKey, true);
                    Debug.Log(
                        "[Bugsee] Pinned JAVA_HOME to Unity Android OpenJDK so EDM4U's " +
                        $"Gradle 5.1.1 resolver can run:\n{jdk}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Bugsee] Could not pin JAVA_HOME for EDM4U: {ex.Message}");
            }
        }
    }
}
