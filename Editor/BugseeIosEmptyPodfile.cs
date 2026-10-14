using System;
using System.IO;
using System.Reflection;
using Bugsee.WrapperPolicy;
using UnityEditor;
using UnityEngine;

namespace Bugsee.Editor
{
    /// <summary>
    /// The iOS SDK is linked through Swift Package Manager, but the package depends on EDM4U for Android, and
    /// EDM4U's iOS resolver defaults to generating a Podfile even when no pod is declared. That adds an empty
    /// CocoaPods integration to every export: a workspace, a Pods project and two empty Pods_* frameworks, and a
    /// build that must go through the workspace.
    ///
    /// Turns that default off, once, for a project that never set it. A project that configured the key keeps its
    /// choice, and a project that declares pods still gets its Podfile (the setting only covers the empty case).
    /// Known limits: EDM4U writes the setting to the project file and to the user's EditorPrefs, and "Restore
    /// defaults" in its settings window removes the key, so the next load turns the empty Podfile off again; tick
    /// the box explicitly to keep it.
    /// Reflection keeps this from requiring the iOS resolver assembly, which is absent when EDM4U is.
    /// </summary>
    [InitializeOnLoad]
    static class BugseeIosEmptyPodfile
    {
        const string ResolverType = "Google.IOSResolver";
        const string Property = "AllowEmptyPodfileGeneration";
        const string SettingKey = "Google.IOSResolver.AllowEmptyPodfileGeneration";
        const string UseProjectSettingsKey = "Google.IOSResolver.UseProjectSettings";
        const string SettingsFile = "ProjectSettings/GvhProjectSettings.xml";

        static BugseeIosEmptyPodfile()
        {
            // Not deferred: a batchmode build can run its build method before any editor update, so a delayed call
            // never happens. Reading the property runs the resolver's own static initialisation first.
            Apply();
        }

        static void Apply()
        {
            try
            {
                string xml = null;
                string path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", SettingsFile));
                if (File.Exists(path)) xml = File.ReadAllText(path);
                // A developer who turned off "use project settings" keeps the choice in EditorPrefs instead.
                bool useProjectSettings = EditorPrefs.GetBool(UseProjectSettingsKey, true);
                if (EdmSettingPolicy.IsConfigured(xml, SettingKey, useProjectSettings, EditorPrefs.HasKey(SettingKey))) return;

                Type resolver = null;
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    resolver = assembly.GetType(ResolverType, false);
                    if (resolver != null) break;
                }

                var property = resolver?.GetProperty(Property, BindingFlags.Public | BindingFlags.Static);
                if (property == null || !property.CanWrite)
                {
                    Debug.LogWarning("[Bugsee] EDM4U no longer exposes " + ResolverType + "." + Property + "; an iOS export may still get an empty Podfile.");
                    return;
                }
                if (property.GetValue(null) is bool allowed && !allowed) return;

                property.SetValue(null, false);
                Debug.Log("[Bugsee] Turned off EDM4U's empty Podfile generation for iOS (Bugsee links through Swift Package Manager). " +
                          "Set " + SettingKey + " in " + SettingsFile + " to keep it.");
            }
            catch (Exception ex)
            {
                // Cosmetic: the build is correct either way, only an empty CocoaPods integration remains.
                var cause = (ex as TargetInvocationException)?.InnerException ?? ex;
                Debug.LogWarning("[Bugsee] Could not adjust EDM4U's empty Podfile setting: " + cause.Message);
            }
        }
    }
}
