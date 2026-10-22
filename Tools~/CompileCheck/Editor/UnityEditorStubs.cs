// The only UnityEditor reference assembly on nuget.org is Unity 2020.2, which lacks the 2021+ Editor
// APIs below. These source types shadow the same-named types in that assembly (source wins; CS0436
// is suppressed) with just the members Editor/ uses. Extend when Editor/ uses more of them.
// Stubs cannot detect API gaps against Unity 2021.3 itself.
namespace UnityEditor.Build
{
    public readonly struct NamedBuildTarget
    {
        public static NamedBuildTarget Android => default;
        public static NamedBuildTarget iOS => default;
    }
}

namespace UnityEditor.PackageManager
{
    public class PackageInfo
    {
        public string name;
        public string resolvedPath;
        public static PackageInfo FindForAssembly(System.Reflection.Assembly assembly) => null;
        public static PackageInfo[] GetAllRegisteredPackages() => null;
    }
}

namespace UnityEditor
{
    public static class PlayerSettings
    {
        public static string bundleVersion;
        public static ScriptingImplementation GetScriptingBackend(UnityEditor.Build.NamedBuildTarget buildTarget) => default;
        public static string GetScriptingDefineSymbols(UnityEditor.Build.NamedBuildTarget buildTarget) => null;
        public static string GetAdditionalIl2CppArgs() => null;
        public static void SetAdditionalIl2CppArgs(string args) { }

        public static class Android
        {
            public static int bundleVersionCode;
            public static bool minifyRelease;
            public static bool minifyDebug;
        }

        public static class iOS
        {
            public static string buildNumber;
        }
    }
}
