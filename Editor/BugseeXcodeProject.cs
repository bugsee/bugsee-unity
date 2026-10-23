using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Bugsee.Editor
{
    /// <summary>
    /// The part of Unity's UnityEditor.iOS.Xcode.PBXProject that the iOS post-processors use, reached by reflection.
    /// That API lives in UnityEditor.iOS.Extensions.Xcode.dll, which an Editor without the iOS Build Support module
    /// (for example a Windows Editor with only Android support) does not have: a compile-time reference to it breaks
    /// the whole Editor assembly, and with it every build in the project. Only an iOS/tvOS export reaches this class,
    /// and that needs the module, so the type is looked up on first use.
    /// </summary>
    internal sealed class XcodeProject
    {
        const string TypeName = "UnityEditor.iOS.Xcode.PBXProject";

        static Type projectType;

        static Type ProjectType
        {
            get
            {
                if (projectType != null) return projectType;
                projectType = Type.GetType(TypeName + ", UnityEditor.iOS.Extensions.Xcode", false)
                    ?? AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(TypeName, false)).FirstOrDefault(t => t != null);
                if (projectType == null)
                {
                    throw new InvalidOperationException("[Bugsee] UnityEditor.iOS.Xcode is not available: install the iOS Build Support module " +
                                                        "of this Unity Editor to export an iOS or tvOS project.");
                }

                return projectType;
            }
        }

        static MethodInfo Method(string name, params Type[] args)
        {
            var m = ProjectType.GetMethod(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static, null, args, null);
            if (m == null) throw new MissingMethodException(TypeName, name);
            return m;
        }

        static object Call(MethodInfo m, object target, params object[] args)
        {
            try
            {
                return m.Invoke(target, args);
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                // Rethrow what PBXProject threw (an IOException, say), as a direct call would have.
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        }

        static readonly Type S = typeof(string);

        readonly object inner;

        public XcodeProject()
        {
            inner = Activator.CreateInstance(ProjectType);
        }

        public static string GetPBXProjectPath(string buildPath) => (string)Call(Method("GetPBXProjectPath", S), null, buildPath);

        public void ReadFromFile(string path) => Call(Method("ReadFromFile", S), inner, path);
        public void ReadFromString(string src) => Call(Method("ReadFromString", S), inner, src);
        public void WriteToFile(string path) => Call(Method("WriteToFile", S), inner, path);
        public string WriteToString() => (string)Call(Method("WriteToString"), inner);
        public string GetUnityMainTargetGuid() => (string)Call(Method("GetUnityMainTargetGuid"), inner);
        public string GetUnityFrameworkTargetGuid() => (string)Call(Method("GetUnityFrameworkTargetGuid"), inner);

        public string AddRemotePackageReferenceAtVersion(string repositoryUrl, string version) =>
            (string)Call(Method("AddRemotePackageReferenceAtVersion", S, S), inner, repositoryUrl, version);

        public void AddRemotePackageFrameworkToProject(string targetGuid, string productName, string packageGuid, bool weak) =>
            Call(Method("AddRemotePackageFrameworkToProject", S, S, S, typeof(bool)), inner, targetGuid, productName, packageGuid, weak);

        public IEnumerable<string> BuildConfigNames() => (IEnumerable<string>)Call(Method("BuildConfigNames"), inner);
        public string BuildConfigByName(string targetGuid, string name) => (string)Call(Method("BuildConfigByName", S, S), inner, targetGuid, name);

        public void SetBuildPropertyForConfig(string configGuid, string name, string value) =>
            Call(Method("SetBuildPropertyForConfig", S, S, S), inner, configGuid, name, value);

        public string GetBuildPropertyForConfig(string configGuid, string name) =>
            (string)Call(Method("GetBuildPropertyForConfig", S, S), inner, configGuid, name);

        public string GetBuildPropertyForAnyConfig(string targetGuid, string name) =>
            (string)Call(Method("GetBuildPropertyForAnyConfig", S, S), inner, targetGuid, name);

        public void SetBuildProperty(string targetGuid, string name, string value) =>
            Call(Method("SetBuildProperty", S, S, S), inner, targetGuid, name, value);

        public void AddShellScriptBuildPhase(string targetGuid, string name, string shellPath, string shellScript) =>
            Call(Method("AddShellScriptBuildPhase", S, S, S, S), inner, targetGuid, name, shellPath, shellScript);
    }
}
