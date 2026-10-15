using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using UnityEditor.Android;
using UnityEngine;

namespace Bugsee.Editor
{
    /// <summary>
    /// Puts <c>UnityWrapperProvider</c> into the merged app manifest.
    ///
    /// The provider has to exist before Bugsee launches, otherwise a crash recovered on the next
    /// start is assembled without the wrapper (no identity, no secure rectangles, no report hooks).
    /// Unity does not merge a loose <c>AndroidManifest.xml</c> shipped in a package's
    /// <c>Plugins/Android</c> (verified on 2021.3: the Java compiles into the APK, the manifest entry
    /// does not), so the entry is injected into the generated unityLibrary manifest here.
    ///
    /// <c>Plugins/Android/AndroidManifest.xml</c> stays the single description of the provider. Only its
    /// <c>&lt;provider&gt;</c> elements are copied: another kind of entry added to it is not injected.
    ///
    /// Deliberately not wrapped in a UNITY_ANDROID define guard: <c>IPostGenerateGradleAndroidProject</c> is defined in
    /// UnityEditor.CoreModule (checked on 2021.3.45), so it compiles for every build target, and the callback only
    /// fires for Android builds. Guarding it would drop it silently whenever the editor's active target is not
    /// Android when the scripts compile.
    /// </summary>
    sealed class BugseeAndroidWrapperManifest : IPostGenerateGradleAndroidProject
    {
        static readonly XNamespace Android = "http://schemas.android.com/apk/res/android";

        public int callbackOrder => 20;

        public void OnPostGenerateGradleAndroidProject(string unityLibraryPath)
        {
            try
            {
                var template = ResolveTemplatePath();
                if (template == null)
                {
                    Debug.LogWarning(
                        "[Bugsee] Could not find Plugins/Android/AndroidManifest.xml in com.bugsee.unity; " +
                        "the wrapper provider was not registered, so crashes recovered on relaunch will lack wrapper data.");
                    return;
                }

                var target = Path.Combine(unityLibraryPath, "src", "main", "AndroidManifest.xml");
                if (!File.Exists(target))
                {
                    Debug.LogWarning("[Bugsee] unityLibrary manifest not found at " + target + "; wrapper provider not registered.");
                    return;
                }

                var added = InjectProviders(XDocument.Load(template), target);
                if (added > 0)
                    Debug.Log("[Bugsee] Registered " + added + " wrapper provider(s) in " + target);
            }
            catch (Exception ex)
            {
                // Never fail the build, but be loud: a missing provider is a silent data-loss bug.
                Debug.LogWarning("[Bugsee] Failed to register the wrapper provider: " + ex);
            }
        }

        static int InjectProviders(XDocument template, string targetPath)
        {
            var providers = template.Descendants("provider").ToList();
            var target = XDocument.Load(targetPath);
            var application = target.Root?.Element("application");
            if (application == null)
            {
                application = new XElement("application");
                target.Root?.Add(application);
            }

            var added = 0;
            foreach (var provider in providers)
            {
                var name = (string)provider.Attribute(Android + "name");
                var exists = application.Elements("provider").Any(p => (string)p.Attribute(Android + "name") == name);
                if (exists) continue;
                application.Add(new XElement(provider));
                added++;
            }

            if (added > 0) target.Save(targetPath);
            return added;
        }

        static string ResolveTemplatePath()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(BugseeAndroidWrapperManifest).Assembly);
            if (info == null) return null;
            var path = Path.Combine(info.resolvedPath, "Plugins", "Android", "AndroidManifest.xml");
            return File.Exists(path) ? path : null;
        }
    }
}
