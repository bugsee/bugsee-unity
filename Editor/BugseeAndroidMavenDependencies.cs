using System;
using System.IO;
using Bugsee.WrapperPolicy;
using UnityEditor.Android;
using UnityEditor.Build;
using UnityEngine;

namespace Bugsee.Editor
{
    /// <summary>
    /// Declares the Bugsee Android SDK (<c>bugsee-android</c> + <c>bugsee-android-ndk</c>, Maven Central) in the
    /// generated Gradle project, so the package needs neither EDM4U nor a custom mainTemplate.gradle.
    /// The core AAR publishes no Maven transitives (empty POM and Gradle .module) and does not pull the NDK
    /// artifact, so both are declared. androidx.fragment is not needed: the fragment secure-view overloads are
    /// typed as Object so Unity's JNI reflection loads Bugsee without Fragment.
    ///
    /// The dependencies go into unityLibrary/build.gradle between marker comments (a re-run or an append export
    /// does not duplicate them). A Bugsee dependency the project declares itself (EDM4U, a custom template, a
    /// local AAR) is kept and nothing is added next to it; a version that differs from
    /// <see cref="BugseePackageVersion.AndroidSdkVersion"/> is reported. <c>mavenCentral()</c> is added where the
    /// generated project declares its repositories (settings.gradle <c>dependencyResolutionManagement</c>, or the
    /// root build.gradle <c>allprojects</c> block) if it is missing.
    ///
    /// The edits themselves are <see cref="GradleMavenInjection"/> (unit-tested); this class only reads and writes
    /// the files. Not behind a UNITY_ANDROID guard: <c>IPostGenerateGradleAndroidProject</c> is defined in
    /// UnityEditor.CoreModule (checked with monodis on 2021.3.45), not in the Android module's
    /// UnityEditor.Android.Extensions.dll, so this compiles without Android Build Support and for every active
    /// build target, and only fires for Android builds. A guard would drop the step silently whenever the
    /// editor's active target is not Android when the scripts compile.
    /// </summary>
    sealed class BugseeAndroidMavenDependencies : IPostGenerateGradleAndroidProject
    {
        static readonly string[] Artifacts =
        {
            BugseePackageVersion.AndroidMavenArtifact,
            BugseePackageVersion.AndroidNdkArtifact,
        };

        // Late, so other post-generate steps that add their own declarations run first.
        public int callbackOrder => 1000;

        public void OnPostGenerateGradleAndroidProject(string unityLibraryPath)
        {
            try
            {
                InjectDependencies(unityLibraryPath);
                EnsureMavenCentral(Path.GetDirectoryName(Path.GetFullPath(unityLibraryPath)));
            }
            catch (Exception ex) when (!(ex is BuildFailedException))
            {
                // Without these dependencies the app builds but Bugsee cannot start: fail loudly instead.
                throw new BuildFailedException("[Bugsee] Could not add the Bugsee Android SDK to the Gradle project: " + ex);
            }
        }

        static void InjectDependencies(string unityLibraryPath)
        {
            string path = Path.Combine(unityLibraryPath, "build.gradle");
            if (!File.Exists(path))
            {
                throw new BuildFailedException(
                    "[Bugsee] " + path + " not found; cannot declare com.bugsee:bugsee-android. " +
                    "(Kotlin DSL Gradle files are not supported.)");
            }

            string text = File.ReadAllText(path);
            var result = GradleMavenInjection.ApplyDependencies(
                text,
                BugseePackageVersion.AndroidMavenGroup,
                Artifacts,
                BugseePackageVersion.AndroidSdkVersion);

            foreach (string warning in result.Warnings)
            {
                Debug.LogWarning("[Bugsee] " + warning);
            }

            if (!string.Equals(result.Text, text, StringComparison.Ordinal))
            {
                File.WriteAllText(path, result.Text);
            }

            if (result.Status == GradleMavenInjection.DependencyStatus.Injected)
            {
                Debug.Log(
                    "[Bugsee] Declared " + BugseePackageVersion.AndroidMavenGroup + ":" +
                    string.Join("/", Artifacts) + ":" + BugseePackageVersion.AndroidSdkVersion + " in " + path);
            }
        }

        static void EnsureMavenCentral(string rootPath)
        {
            var settings = Path.Combine(rootPath, "settings.gradle");
            if (TryEnsureMavenCentral(settings, "dependencyResolutionManagement"))
            {
                return;
            }

            var rootBuild = Path.Combine(rootPath, "build.gradle");
            if (TryEnsureMavenCentral(rootBuild, "allprojects"))
            {
                return;
            }

            Debug.LogWarning(
                "[Bugsee] Could not find the project repositories in " + settings + " or " + rootBuild +
                "; make sure mavenCentral() is declared so com.bugsee:bugsee-android resolves.");
        }

        static bool TryEnsureMavenCentral(string path, string outerBlock)
        {
            if (!File.Exists(path))
            {
                return false;
            }

            string text = File.ReadAllText(path);
            var status = GradleMavenInjection.EnsureMavenCentral(text, outerBlock, out string updated);
            if (status == GradleMavenInjection.RepositoryStatus.Inserted)
            {
                File.WriteAllText(path, updated);
                Debug.Log("[Bugsee] Added mavenCentral() to " + outerBlock + " repositories in " + path);
            }

            return status != GradleMavenInjection.RepositoryStatus.BlockNotFound;
        }
    }
}
