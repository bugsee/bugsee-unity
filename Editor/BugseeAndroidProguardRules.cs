using System;
using System.IO;
using Bugsee.WrapperPolicy;
using UnityEditor.Android;
using UnityEngine;

namespace Bugsee.Editor
{
    /// <summary>
    /// Makes R8 keep the wrapper classes C# reaches over JNI.
    ///
    /// Unity does not read a loose proguard file from a package's <c>Plugins/Android</c> (the Custom Proguard File in
    /// Player Settings is user-level), so with Minify enabled R8 stripped <c>UnityWrapper</c> and
    /// <c>UnityManagedException</c> and the SDK failed with ClassNotFoundException. Every Unity version generates
    /// <c>unityLibrary/proguard-unity.txt</c> and lists it as the library's <c>consumerProguardFiles</c>, so the rules
    /// (<c>Plugins/Android/bugsee-unity-wrapper.proguard</c>) are appended there: no project setup, and it applies
    /// to the app's R8 run.
    ///
    /// Like <see cref="BugseeAndroidWrapperManifest"/> this is not behind a UNITY_ANDROID guard: the callback only
    /// fires for Android builds.
    /// </summary>
    sealed class BugseeAndroidProguardRules : IPostGenerateGradleAndroidProject
    {
        public int callbackOrder => 21;

        public void OnPostGenerateGradleAndroidProject(string unityLibraryPath)
        {
            try
            {
                var template = ResolveRulesPath();
                if (template == null)
                {
                    Debug.LogWarning(
                        "[Bugsee] Could not find Plugins/Android/bugsee-unity-wrapper.proguard in com.bugsee.unity; " +
                        "with Minify enabled R8 will strip the wrapper classes and the SDK will not launch.");
                    return;
                }

                WarnIfNotConsumerRules(unityLibraryPath);
                var target = Path.Combine(unityLibraryPath, "proguard-unity.txt");
                var existing = File.Exists(target) ? File.ReadAllText(target) : "";
                var updated = ProguardRules.Apply(existing, File.ReadAllText(template));
                if (updated != existing)
                {
                    File.WriteAllText(target, updated);
                    Debug.Log("[Bugsee] Added the wrapper keep rules to " + target);
                }
            }
            catch (Exception ex)
            {
                // Never fail the build, but be loud: a missing rule is a launch failure in minified builds.
                Debug.LogWarning("[Bugsee] Failed to add the wrapper keep rules: " + ex);
            }
        }

        // A custom main Gradle template can drop consumerProguardFiles, which silently undoes this step.
        static void WarnIfNotConsumerRules(string unityLibraryPath)
        {
            foreach (var name in new[] { "build.gradle", "build.gradle.kts" })
            {
                var script = Path.Combine(unityLibraryPath, name);
                if (!File.Exists(script)) continue;
                if (ProguardRules.DeclaresConsumerRules(File.ReadAllText(script))) return;
                Debug.LogWarning(
                    "[Bugsee] " + script + " does not declare consumerProguardFiles 'proguard-unity.txt'; the wrapper keep rules " +
                    "will not reach R8 and a Minify build will fail to launch the SDK. Add it to the library's defaultConfig.");
                return;
            }
        }

        static string ResolveRulesPath()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(BugseeAndroidProguardRules).Assembly);
            if (info == null) return null;
            var path = Path.Combine(info.resolvedPath, "Plugins", "Android", "bugsee-unity-wrapper.proguard");
            return File.Exists(path) ? path : null;
        }
    }
}
