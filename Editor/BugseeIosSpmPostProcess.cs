using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;
using UnityEngine;

namespace Bugsee.Editor
{
    /// <summary>
    /// Wires the Bugsee iOS SDK into the generated Xcode project via Swift Package Manager.
    ///
    /// iOS 7.x is SPM-only: Xcode resolves <see cref="RemoteSpmUrl"/> at
    /// <see cref="RemoteSpmVersion"/> (github.com/bugsee/spm). The local
    /// Native~/ios/Bugsee wrapper is unused unless <see cref="UseRemoteSpm"/> is flipped off.
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

        /// <summary>Bugsee 7.x / github.com/bugsee/spm requires iOS 15.</summary>
        public const string MinIosVersion = "15.0";

        const string LocalPackageFolderName = "Bugsee";
        const string LocalPackageRelativePath = "Native~/ios/Bugsee";

        [PostProcessBuild(999)]
        public static void OnPostProcessBuild(BuildTarget target, string pathToBuiltProject)
        {
            if (target != BuildTarget.iOS)
            {
                return;
            }

            string projectPath = PBXProject.GetPBXProjectPath(pathToBuiltProject);
            var project = new PBXProject();
            project.ReadFromFile(projectPath);

            string mainTargetGuid = project.GetUnityMainTargetGuid();
            string frameworkTargetGuid = project.GetUnityFrameworkTargetGuid();
            EnsureMinIosDeploymentTarget(project, mainTargetGuid);
            EnsureMinIosDeploymentTarget(project, frameworkTargetGuid);

            if (UseRemoteSpm)
            {
                string packageGuid = project.AddRemotePackageReferenceAtVersion(RemoteSpmUrl, RemoteSpmVersion);
                project.AddRemotePackageFrameworkToProject(mainTargetGuid, SpmProductName, packageGuid, false);
                project.AddRemotePackageFrameworkToProject(frameworkTargetGuid, SpmProductName, packageGuid, false);
                project.WriteToFile(projectPath);
                Debug.Log($"[Bugsee] Linked remote SPM {RemoteSpmUrl}@{RemoteSpmVersion} (iOS {MinIosVersion}+)");
                return;
            }

            string localPackageSource = ResolveLocalPackagePath();
            if (string.IsNullOrEmpty(localPackageSource) || !File.Exists(Path.Combine(localPackageSource, "Package.swift")))
            {
                Debug.LogError(
                    "[Bugsee] Local SPM package not found. Expected Package.swift under " +
                    $"{LocalPackageRelativePath}. Run Tools~/scripts/update-native-sdks.sh first.");
                return;
            }

            string destPackageDir = Path.Combine(pathToBuiltProject, LocalPackageFolderName);
            CopyDirectory(localPackageSource, destPackageDir);

            if (!Directory.Exists(Path.Combine(destPackageDir, "Bugsee.xcframework")))
            {
                Debug.LogWarning(
                    "[Bugsee] Bugsee.xcframework is missing from the local SPM package. " +
                    "iOS linking will fail until you run Tools~/scripts/update-native-sdks.sh.");
            }

            project.WriteToFile(projectPath);
            InjectLocalSwiftPackageReference(projectPath, LocalPackageFolderName, SpmProductName, mainTargetGuid, frameworkTargetGuid);
            Debug.Log($"[Bugsee] Linked local SPM package at {destPackageDir}");
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

        /// <summary>
        /// Unity's PBXProject API exposes remote SPM helpers but not local path packages.
        /// Inject XCLocalSwiftPackageReference + product dependencies into project.pbxproj.
        /// </summary>
        static void InjectLocalSwiftPackageReference(
            string projectPath,
            string relativePackagePath,
            string productName,
            string mainTargetGuid,
            string frameworkTargetGuid)
        {
            string contents = File.ReadAllText(projectPath);
            if (contents.Contains($"XCLocalSwiftPackageReference \"{relativePackagePath}\""))
            {
                return;
            }

            string packageRefGuid = Guid24();
            string mainProductGuid = Guid24();
            string frameworkProductGuid = Guid24();

            string localRefSection =
                "/* Begin XCLocalSwiftPackageReference section */\n" +
                $"\t\t{packageRefGuid} /* XCLocalSwiftPackageReference \"{relativePackagePath}\" */ = {{\n" +
                "\t\t\tisa = XCLocalSwiftPackageReference;\n" +
                $"\t\t\trelativePath = {relativePackagePath};\n" +
                "\t\t};\n" +
                "/* End XCLocalSwiftPackageReference section */\n";

            string productSection =
                "/* Begin XCSwiftPackageProductDependency section */\n" +
                $"\t\t{mainProductGuid} /* {productName} */ = {{\n" +
                "\t\t\tisa = XCSwiftPackageProductDependency;\n" +
                $"\t\t\tpackage = {packageRefGuid} /* XCLocalSwiftPackageReference \"{relativePackagePath}\" */;\n" +
                $"\t\t\tproductName = {productName};\n" +
                "\t\t};\n" +
                $"\t\t{frameworkProductGuid} /* {productName} */ = {{\n" +
                "\t\t\tisa = XCSwiftPackageProductDependency;\n" +
                $"\t\t\tpackage = {packageRefGuid} /* XCLocalSwiftPackageReference \"{relativePackagePath}\" */;\n" +
                $"\t\t\tproductName = {productName};\n" +
                "\t\t};\n" +
                "/* End XCSwiftPackageProductDependency section */\n";

            int objectsEnd = contents.LastIndexOf("/* End PBXProject section */");
            if (objectsEnd < 0)
            {
                Debug.LogError("[Bugsee] Could not locate PBXProject section to inject local SPM reference.");
                return;
            }

            contents = contents.Insert(objectsEnd, localRefSection + productSection);
            contents = AddPackageReferenceToProjectObject(contents, packageRefGuid, relativePackagePath);
            contents = AddProductDependencyToTarget(contents, mainTargetGuid, mainProductGuid, productName);
            contents = AddProductDependencyToTarget(contents, frameworkTargetGuid, frameworkProductGuid, productName);

            File.WriteAllText(projectPath, contents);
        }

        static string AddPackageReferenceToProjectObject(string contents, string packageRefGuid, string relativePackagePath)
        {
            const string marker = "packageReferences = (";
            int idx = contents.IndexOf(marker);
            if (idx < 0)
            {
                Debug.LogWarning(
                    "[Bugsee] PBXProject has no packageReferences list; " +
                    "Xcode may still resolve the local package after a manual refresh.");
                return contents;
            }

            int insertAt = idx + marker.Length;
            string entry = $"\n\t\t\t\t{packageRefGuid} /* XCLocalSwiftPackageReference \"{relativePackagePath}\" */,";
            return contents.Insert(insertAt, entry);
        }

        static string AddProductDependencyToTarget(string contents, string targetGuid, string productGuid, string productName)
        {
            string targetMarker = $"{targetGuid} /*";
            int targetIdx = contents.IndexOf(targetMarker);
            if (targetIdx < 0)
            {
                return contents;
            }

            int searchFrom = targetIdx;
            int depsIdx = contents.IndexOf("packageProductDependencies = (", searchFrom);
            int nextTarget = contents.IndexOf("isa = PBXNativeTarget", searchFrom + 1);
            if (depsIdx >= 0 && (nextTarget < 0 || depsIdx < nextTarget))
            {
                int insertAt = depsIdx + "packageProductDependencies = (".Length;
                string entry = $"\n\t\t\t\t{productGuid} /* {productName} */,";
                return contents.Insert(insertAt, entry);
            }

            int afterDeps = contents.IndexOf("dependencies = (", searchFrom);
            if (afterDeps > 0 && (nextTarget < 0 || afterDeps < nextTarget))
            {
                int close = contents.IndexOf(");", afterDeps);
                if (close > 0)
                {
                    string block =
                        $"\n\t\t\tpackageProductDependencies = (\n" +
                        $"\t\t\t\t{productGuid} /* {productName} */,\n" +
                        "\t\t\t);";
                    return contents.Insert(close + 2, block);
                }
            }

            return contents;
        }

        static string Guid24()
        {
            return System.Guid.NewGuid().ToString("N").Substring(0, 24).ToUpperInvariant();
        }
    }
}
