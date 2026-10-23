// Stub of the subset of UnityEditor.iOS.Xcode (UnityEditor.iOS.Extensions.Xcode.dll, shipped only
// inside the Editor install) that Editor/ uses. Signatures mirror Unity 2021.3. Extend when Editor/
// starts using more of the API. Used by the compile-check Editor project (left out by BugseeNoXcode=true) and by
// Tests~/WrapperPolicy/XcodeProjectReflectionTests.cs, which checks that every reflection lookup of Editor/BugseeXcodeProject.cs
// resolves against these signatures.
namespace UnityEditor.iOS.Xcode
{
    public class PBXProject
    {
        public static string GetPBXProjectPath(string buildPath) => null;
        public void ReadFromFile(string path) { }
        public void ReadFromString(string src) { }
        public void WriteToFile(string path) { }
        public string WriteToString() => null;
        public string GetUnityMainTargetGuid() => null;
        public string GetUnityFrameworkTargetGuid() => null;
        public string AddRemotePackageReferenceAtVersion(string repositoryURL, string version) => null;
        public void AddRemotePackageFrameworkToProject(string targetGuid, string productName, string packageGuid, bool weak) { }
        public System.Collections.Generic.IEnumerable<string> BuildConfigNames() => null;
        public string BuildConfigByName(string targetGuid, string name) => null;
        public void SetBuildPropertyForConfig(string configGuid, string name, string value) { }
        public string GetBuildPropertyForConfig(string configGuid, string name) => null;
        public string GetBuildPropertyForAnyConfig(string targetGuid, string name) => null;
        public void SetBuildProperty(string targetGuid, string name, string value) { }
        public string AddShellScriptBuildPhase(string targetGuid, string name, string shellPath, string shellScript) => null;
    }
}
