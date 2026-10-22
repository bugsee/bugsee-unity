using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.iOS.Xcode;
using Bugsee.WrapperPolicy;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Bugsee.Editor
{
    /// <summary>
    /// Wires the Bugsee iOS SDK into the generated Xcode project via Swift Package Manager.
    ///
    /// iOS 7.x is SPM-only: Xcode resolves <see cref="RemoteSpmUrl"/> at
    /// <see cref="RemoteSpmVersion"/> (github.com/bugsee/spm). The local
    /// Native~/ios/Bugsee wrapper is unused unless <see cref="UseRemoteSpm"/> is flipped off.
    ///
    /// To build against an SDK that is not published yet, set <see cref="LocalPackageEnvVar"/> to a local SPM
    /// package directory (Tools~/scripts/assemble-local-ios-sdk.sh makes one). It is copied next to the Xcode
    /// project and replaces the remote reference for that export; unset, the pinned tag is used as before.
    /// </summary>
    public static class BugseeIosSpmPostProcess
    {
        /// <summary>
        /// When true (default), adds a remote reference to github.com/bugsee/spm.
        /// When false, copies Native~/ios/Bugsee next to the Xcode project (offline / local xcframework).
        /// </summary>
        public static readonly bool UseRemoteSpm = true;

        public const string RemoteSpmUrl = BugseePackageVersion.IosSdkSource;
        public const string RemoteSpmVersion = BugseePackageVersion.IosSdkVersion;
        public const string SpmProductName = "Bugsee";

        /// <summary>
        /// Directory of a local SPM package (a Package.swift next to Bugsee.xcframework) to use instead of the
        /// published one. Meant for developing against an unreleased SDK, never for a release build.
        /// </summary>
        public const string LocalPackageEnvVar = "BUGSEE_IOS_SDK_LOCAL_PACKAGE";

        /// <summary>Set to 1 to let <see cref="LocalPackageEnvVar"/> apply to a non-development (release) build too.</summary>
        public const string LocalPackageAllowReleaseEnvVar = "BUGSEE_IOS_SDK_LOCAL_PACKAGE_ALLOW_RELEASE";

        /// <summary>Bugsee 7.x / github.com/bugsee/spm requires iOS 15.</summary>
        public const string MinIosVersion = "15.0";

        const string LocalPackageFolderName = "Bugsee";
        const string LocalPackageMarker = LocalPackageCopy.Marker;
        const string LocalPackageRelativePath = "Native~/ios/Bugsee";
        const string PrefNoDsymForce = "Bugsee.Ios.NoDsymForce";
        const string MenuNoDsymForce = "Bugsee/iOS/Keep Xcode Debug Info Format (skip dSYM force)";

        [MenuItem(MenuNoDsymForce)]
        static void MenuToggleNoDsymForce()
        {
            bool next = !EditorPrefs.GetBool(PrefNoDsymForce, false);
            EditorPrefs.SetBool(PrefNoDsymForce, next);
            Debug.Log(
                "[Bugsee] Skip dSYM force = " + next +
                ". Applies to the next iOS export; Debug then keeps Unity's plain DWARF and has no dSYM to upload.");
        }

        [MenuItem(MenuNoDsymForce, true)]
        static bool MenuToggleNoDsymForceValidate()
        {
            Menu.SetChecked(MenuNoDsymForce, EditorPrefs.GetBool(PrefNoDsymForce, false));
            return true;
        }

        [System.Obsolete("Run by the build pipeline with the build's own options; do not call.")]
        public static void OnPostProcessBuild(BuildTarget target, string pathToBuiltProject) =>
            Run(target, pathToBuiltProject, EditorUserBuildSettings.development);

        /// <param name="development">Whether this build was started with BuildOptions.Development (the build's own options,
        /// not the Build Settings checkbox, which a script that builds with explicit options leaves alone).</param>
        internal static void Run(BuildTarget target, string pathToBuiltProject, bool development)
        {
            // tvOS exports are not covered (nor are the other iOS steps here): an Append export there could list a phase twice
            // as well; delete the export folder or export with Replace.
            if (target != BuildTarget.iOS)
            {
                return;
            }

            // Only a BuildFailedException fails a Unity build from a post-processor; any other exception is logged and the
            // build still reports success, with whatever the project links by then. Turn every failure into one.
            try
            {
                LocalPackageCopy.Warn = message => Debug.LogWarning($"[Bugsee] {message}");
                Link(pathToBuiltProject, development);
            }
            catch (Exception e) when (!(e is BuildFailedException))
            {
                Debug.LogException(e);
                throw new BuildFailedException($"[Bugsee] Linking the iOS SDK failed: {e.GetType().Name}: {e.Message}");
            }
        }

        static void Link(string pathToBuiltProject, bool development)
        {
            string packageFolder = Path.Combine(pathToBuiltProject, LocalPackageFolderName);

            // Everything that can refuse the export, and the copy of the local package, happen before the Xcode project is
            // touched, so a refused or failed export leaves the SPM link as it was (later post-processors, such as the
            // symbol-upload Run Script, still run). An explicit local package (env var) beats everything; UseRemoteSpm = false
            // falls back to the in-package stub.
            string overridePackage = LocalPackageOverride(development);
            string localPackageSource = overridePackage;
            if (localPackageSource == null && !UseRemoteSpm)
            {
                localPackageSource = ResolveLocalPackagePath();
                if (string.IsNullOrEmpty(localPackageSource))
                {
                    throw new BuildFailedException(
                        "[Bugsee] Local SPM package not found. Expected Package.swift under " +
                        $"{LocalPackageRelativePath}. Set {LocalPackageEnvVar} to a local SPM package (Tools~/scripts/assemble-local-ios-sdk.sh makes one; a non-development build also needs {LocalPackageAllowReleaseEnvVar}=1), or leave UseRemoteSpm on.");
                }
            }

            if (localPackageSource != null)
            {
                // A package that cannot be used fails the build instead of exporting a project with no SDK (the offline stub
                // Native~/ios/Bugsee has no xcframework: none is committed, and it must be 7.0.0-beta7 or later).
                string problem = LocalPackageCopy.Problem(localPackageSource);
                if (problem != null)
                {
                    string how = overridePackage != null ? $" ({LocalPackageEnvVar})" : $" Set {LocalPackageEnvVar} to a 7.0.0-beta7 package (Tools~/scripts/assemble-local-ios-sdk.sh makes one; a non-development build also needs {LocalPackageAllowReleaseEnvVar}=1), or leave UseRemoteSpm on.";
                    throw new BuildFailedException($"[Bugsee] Local iOS SDK package {localPackageSource}: {problem}{how}");
                }

                // The package may not live inside the export (the copy there is replaced, and so would be the source). Compared
                // as physical paths: symlinks (/tmp, aliases) and the volume's case are resolved first.
                string sourcePhysical = LocalPackageCopy.PhysicalPath(localPackageSource);
                string exportPhysical = LocalPackageCopy.PhysicalPath(pathToBuiltProject);
                if ((sourcePhysical + "/").StartsWith(exportPhysical.TrimEnd('/') + "/", StringComparison.Ordinal))
                {
                    throw new BuildFailedException($"[Bugsee] The local iOS SDK package {localPackageSource} is inside the export {pathToBuiltProject}; point {LocalPackageEnvVar} at the assembled package instead.");
                }

                if (Directory.Exists(packageFolder) && !File.Exists(Path.Combine(packageFolder, LocalPackageMarker)))
                {
                    throw new BuildFailedException(
                        $"[Bugsee] {packageFolder} exists and is not a copy of the Bugsee package made by an earlier export; " +
                        $"move it away, or export into a project without a '{LocalPackageFolderName}' folder at its root.");
                }

                LocalPackageCopy.CopyAndSwapIn(localPackageSource, packageFolder, overridePackage != null);
            }

            string projectPath = PBXProject.GetPBXProjectPath(pathToBuiltProject);
            // An export into an existing project (Append) keeps the links an earlier export added; drop them so the
            // package is linked once, whichever way (published tag or local package) the earlier export linked it.
            string existing = File.ReadAllText(projectPath);
            // Unity's own Append export can list the GameAssembly target's Run Script phase twice (xcodebuild then fails the
            // archive with "Unexpected duplicate tasks"): list each phase once.
            existing = XcodeBuildPhaseList.RemoveDuplicates(existing, out int duplicatePhases);
            if (duplicatePhases > 0)
            {
                Debug.Log($"[Bugsee] Removed {duplicatePhases} build phase(s) that Unity's Append export listed twice in a target " +
                          "(the next archive would fail with 'Unexpected duplicate tasks').");
            }

            string cleaned = XcodeLocalPackage.RemoveBugseePackage(existing, RemoteSpmUrl, LocalPackageFolderName, out var removedPackageIds);
            if (cleaned != existing || duplicatePhases > 0)
            {
                File.WriteAllText(projectPath, cleaned);
            }

            var project = new PBXProject();
            project.ReadFromFile(projectPath);

            string mainTargetGuid = project.GetUnityMainTargetGuid();
            string frameworkTargetGuid = project.GetUnityFrameworkTargetGuid();
            EnsureMinIosDeploymentTarget(project, mainTargetGuid);
            EnsureMinIosDeploymentTarget(project, frameworkTargetGuid);
            EnsureDsymGeneration(project, mainTargetGuid);
            EnsureDsymGeneration(project, frameworkTargetGuid);

            // Unity's PBXProject API only knows remote packages. It writes everything a link needs (the project's
            // package list, both targets' product dependencies and Frameworks entries), so the local case lets it
            // add the remote reference and then turns that one reference into a path reference.
            string packageGuid = project.AddRemotePackageReferenceAtVersion(RemoteSpmUrl, RemoteSpmVersion);
            project.AddRemotePackageFrameworkToProject(mainTargetGuid, SpmProductName, packageGuid, false);
            project.AddRemotePackageFrameworkToProject(frameworkTargetGuid, SpmProductName, packageGuid, false);
            project.WriteToFile(projectPath);
            // The user's other products of the package an earlier export linked (BugseeSwiftUI) follow the new reference.
            string linked = XcodeLocalPackage.RepointProducts(File.ReadAllText(projectPath), packageGuid, removedPackageIds);
            File.WriteAllText(projectPath, linked);

            if (localPackageSource == null)
            {
                if (File.Exists(Path.Combine(packageFolder, LocalPackageMarker)))
                {
                    // An earlier export that linked a local package left a copy next to the project; the link no longer
                    // points at it, so drop it (only ours). Best effort: a folder that cannot be removed is left, unused.
                    LocalPackageCopy.DeleteTree(packageFolder, bestEffort: true);
                }

                Debug.Log($"[Bugsee] Linked remote SPM {RemoteSpmUrl}@{RemoteSpmVersion} (iOS {MinIosVersion}+)");
                return;
            }

            string converted = XcodeLocalPackage.FromRemote(File.ReadAllText(projectPath), RemoteSpmUrl, LocalPackageFolderName);
            if (converted == null)
            {
                throw new BuildFailedException($"[Bugsee] Could not turn the {RemoteSpmUrl} package reference into a local one in {projectPath}.");
            }

            File.WriteAllText(projectPath, converted);
            if (overridePackage != null)
            {
                Debug.LogWarning(
                    $"[Bugsee] Linked the LOCAL iOS SDK package {overridePackage} ({LocalPackageEnvVar}); " +
                    $"not the published {RemoteSpmVersion}. Do not ship this build.");
            }
            else
            {
                Debug.Log($"[Bugsee] Linked local SPM package at {packageFolder}");
            }
        }

        /// <summary>
        /// Debug configurations default to plain DWARF, which leaves no dSYM to upload.
        /// Symbolication needs one for every configuration, on both the app and UnityFramework.
        /// </summary>
        static void EnsureDsymGeneration(PBXProject project, string targetGuid)
        {
            if (string.IsNullOrEmpty(targetGuid))
            {
                return;
            }

            // Opt-out for teams that keep Debug on plain DWARF to save build time (dsymutil on a large
            // UnityFramework is slow). The menu toggle covers a Finder-launched Editor, which never
            // sees a shell export; the env var covers CI.
            if (System.Environment.GetEnvironmentVariable("BUGSEE_NO_DSYM_FORCE") != null ||
                EditorPrefs.GetBool(PrefNoDsymForce, false))
            {
                return;
            }

            foreach (string configName in project.BuildConfigNames())
            {
                string configGuid = project.BuildConfigByName(targetGuid, configName);
                if (string.IsNullOrEmpty(configGuid))
                {
                    continue;
                }

                project.SetBuildPropertyForConfig(configGuid, "DEBUG_INFORMATION_FORMAT", "dwarf-with-dsym");
                project.SetBuildPropertyForConfig(configGuid, "GCC_GENERATE_DEBUGGING_SYMBOLS", "YES");
            }
        }

        static void EnsureMinIosDeploymentTarget(PBXProject project, string targetGuid)
        {
            if (string.IsNullOrEmpty(targetGuid))
            {
                return;
            }

            bool bumped = false;
            foreach (string configName in project.BuildConfigNames())
            {
                string configGuid = project.BuildConfigByName(targetGuid, configName);
                if (string.IsNullOrEmpty(configGuid))
                {
                    continue;
                }

                string current = project.GetBuildPropertyForConfig(configGuid, "IPHONEOS_DEPLOYMENT_TARGET");
                if (NeedsVersionBump(current, MinIosVersion))
                {
                    project.SetBuildPropertyForConfig(configGuid, "IPHONEOS_DEPLOYMENT_TARGET", MinIosVersion);
                    bumped = true;
                }
            }

            if (!bumped)
            {
                string current = project.GetBuildPropertyForAnyConfig(targetGuid, "IPHONEOS_DEPLOYMENT_TARGET");
                if (NeedsVersionBump(current, MinIosVersion))
                {
                    project.SetBuildProperty(targetGuid, "IPHONEOS_DEPLOYMENT_TARGET", MinIosVersion);
                    bumped = true;
                }
            }

            if (bumped)
            {
                Debug.Log($"[Bugsee] Raised IPHONEOS_DEPLOYMENT_TARGET to {MinIosVersion} (Bugsee 7.x SPM requires iOS 15).");
            }
        }

        static bool NeedsVersionBump(string current, string minimum)
        {
            if (string.IsNullOrEmpty(current))
            {
                return true;
            }

            if (!System.Version.TryParse(NormalizeOsVersion(current), out var have))
            {
                return true;
            }

            return !System.Version.TryParse(NormalizeOsVersion(minimum), out var need) || have < need;
        }

        static string NormalizeOsVersion(string version)
        {
            string v = version.Trim();
            int parts = 0;
            for (int i = 0; i < v.Length; i++)
            {
                if (v[i] == '.') parts++;
            }

            if (parts == 0) return v + ".0";
            return v;
        }

        /// <summary>The directory named by <see cref="LocalPackageEnvVar"/>, made absolute; null when it is unset or blank.</summary>
        static string LocalPackageOverride(bool development)
        {
            string value = System.Environment.GetEnvironmentVariable(LocalPackageEnvVar);
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (!development &&
                System.Environment.GetEnvironmentVariable(LocalPackageAllowReleaseEnvVar) != "1")
            {
                Debug.LogWarning(
                    $"[Bugsee] {LocalPackageEnvVar} is set but this is not a development build, so the published " +
                    $"iOS SDK is used. Set {LocalPackageAllowReleaseEnvVar}=1 to link the local package anyway.");
                return null;
            }

            value = value.Trim();
            if (value == "~" || value.StartsWith("~/", StringComparison.Ordinal))
            {
                value = Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
                    value.Substring(1).TrimStart('/'));
            }

            return Path.GetFullPath(value);
        }

        static string ResolveLocalPackagePath()
        {
            foreach (var info in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
            {
                if (info.name != "com.bugsee.unity")
                {
                    continue;
                }

                string candidate = Path.Combine(info.resolvedPath, "Native~", "ios", "Bugsee");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            // Fallback when developing with the package embedded / opened as the project root.
            string fromCwd = Path.GetFullPath(Path.Combine(Application.dataPath, "..", LocalPackageRelativePath));
            return Directory.Exists(fromCwd) ? fromCwd : null;
        }
    }

    /// <summary>Runs <see cref="BugseeIosSpmPostProcess"/> after the Xcode project is exported, with the build's own options.</summary>
    class BugseeIosSpmPostProcessEntry : UnityEditor.Build.IPostprocessBuildWithReport
    {
        public int callbackOrder => 999;

        public void OnPostprocessBuild(UnityEditor.Build.Reporting.BuildReport report)
        {
            BugseeIosSpmPostProcess.Run(
                report.summary.platform,
                report.summary.outputPath,
                (report.summary.options & BuildOptions.Development) != 0);
        }
    }
}
