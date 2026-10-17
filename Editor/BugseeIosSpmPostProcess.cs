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
        const string LocalPackageMarker = ".bugsee-local-package";
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
            if (target != BuildTarget.iOS)
            {
                return;
            }

            string projectPath = PBXProject.GetPBXProjectPath(pathToBuiltProject);
            // An export into an existing project (Append) keeps the links an earlier export added; drop them so the
            // package is linked once, whichever way (published tag or local package) the earlier export linked it.
            string existing = File.ReadAllText(projectPath);
            string cleaned = XcodeLocalPackage.RemoveBugseePackage(existing, RemoteSpmUrl, LocalPackageFolderName, out var removedPackageIds);
            if (cleaned != existing)
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

            // An explicit local package (env var) beats everything; UseRemoteSpm = false falls back to the in-package stub.
            string overridePackage = LocalPackageOverride(development);
            string localPackageSource = overridePackage;
            if (localPackageSource == null && !UseRemoteSpm)
            {
                localPackageSource = ResolveLocalPackagePath();
                if (string.IsNullOrEmpty(localPackageSource) || !File.Exists(Path.Combine(localPackageSource, "Package.swift")))
                {
                    Debug.LogError(
                        "[Bugsee] Local SPM package not found. Expected Package.swift under " +
                        $"{LocalPackageRelativePath}. Run Tools~/scripts/update-native-sdks.sh first.");
                    return;
                }
            }

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

            string packageFolder = Path.Combine(pathToBuiltProject, LocalPackageFolderName);
            if (localPackageSource == null)
            {
                // An earlier export that linked a local package left a copy next to the project; drop it (only ours).
                if (File.Exists(Path.Combine(packageFolder, LocalPackageMarker)))
                {
                    Directory.Delete(packageFolder, true);
                }

                Debug.Log($"[Bugsee] Linked remote SPM {RemoteSpmUrl}@{RemoteSpmVersion} (iOS {MinIosVersion}+)");
                return;
            }

            // Asked for explicitly, so a package that cannot be used fails the build instead of exporting a project with no SDK.
            string problem = LocalPackageProblem(localPackageSource);
            if (problem != null)
            {
                string message = $"[Bugsee] Local iOS SDK package {localPackageSource}: {problem}";
                if (overridePackage != null)
                {
                    throw new BuildFailedException($"{message} ({LocalPackageEnvVar})");
                }

                Debug.LogWarning(message + " iOS linking will fail until you run Tools~/scripts/update-native-sdks.sh.");
            }

            string destPackageDir = packageFolder;
            if (overridePackage != null)
            {
                // Replace only a copy this export (or the offline mode) made; a folder of the user's own named Bugsee stays.
                if (Directory.Exists(destPackageDir))
                {
                    bool ours = File.Exists(Path.Combine(destPackageDir, LocalPackageMarker)) ||
                        (File.Exists(Path.Combine(destPackageDir, "Package.swift")) &&
                         Directory.Exists(Path.Combine(destPackageDir, "Bugsee.xcframework")));
                    if (!ours)
                    {
                        throw new BuildFailedException(
                            $"[Bugsee] {destPackageDir} exists and is not a copy of the Bugsee package made by an earlier export; " +
                            $"move it away, or export into a project without a '{LocalPackageFolderName}' folder at its root ({LocalPackageEnvVar}).");
                    }

                    Directory.Delete(destPackageDir, true);
                }

                CopyPackagePreservingLinks(localPackageSource, destPackageDir);
                File.WriteAllText(Path.Combine(destPackageDir, LocalPackageMarker), "Copied by the Bugsee iOS export; removed when the export links the published package again.\n");
            }
            else
            {
                CopyDirectory(localPackageSource, destPackageDir);
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
                Debug.Log($"[Bugsee] Linked local SPM package at {destPackageDir}");
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
            if (value == "~" || value.StartsWith("~/"))
            {
                value = Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
                    value.Substring(1).TrimStart('/'));
            }

            return Path.GetFullPath(value);
        }

        /// <summary>Why <paramref name="packageDir"/> cannot be used as the SDK package, or null when it can.</summary>
        static string LocalPackageProblem(string packageDir)
        {
            if (!File.Exists(Path.Combine(packageDir, "Package.swift")))
            {
                return "no Package.swift there.";
            }

            if (!Directory.Exists(Path.Combine(packageDir, "Bugsee.xcframework")))
            {
                return "no Bugsee.xcframework there.";
            }

            return null;
        }

        /// <summary>
        /// Copies what the package needs to resolve (not the zip, Tools/ or build notes the assembled package also holds).
        /// ditto keeps the symlinks inside the xcframework's frameworks, which a file-by-file copy would turn into duplicate trees.
        /// </summary>
        static void CopyPackagePreservingLinks(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);
            foreach (string item in new[] { "Package.swift", "Bugsee.xcframework", Path.Combine("Sources", "BugseeSwiftUI") })
            {
                string source = Path.Combine(sourceDir, item);
                if (!File.Exists(source) && !Directory.Exists(source))
                {
                    continue;
                }

                string dest = Path.Combine(destDir, item);
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                var info = new ProcessStartInfo("/usr/bin/ditto")
                {
                    UseShellExecute = false,
                    RedirectStandardError = true,
                };
                info.ArgumentList.Add(source);
                info.ArgumentList.Add(dest);
                using (var process = Process.Start(info))
                {
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    if (process.ExitCode != 0)
                    {
                        throw new BuildFailedException($"[Bugsee] Copying {source} failed: {error}");
                    }
                }
            }
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

        static void CopyDirectory(string sourceDir, string destDir)
        {
            if (Directory.Exists(destDir))
            {
                Directory.Delete(destDir, true);
            }

            Directory.CreateDirectory(destDir);

            foreach (string file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}") ||
                    Path.GetFileName(file) == ".DS_Store")
                {
                    continue;
                }

                string relative = file.Substring(sourceDir.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string destFile = Path.Combine(destDir, relative);
                string destParent = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(destParent))
                {
                    Directory.CreateDirectory(destParent);
                }

                File.Copy(file, destFile, true);
            }
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
