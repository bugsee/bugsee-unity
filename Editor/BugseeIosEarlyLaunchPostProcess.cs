using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Bugsee.WrapperPolicy;
using Debug = UnityEngine.Debug;

namespace Bugsee.Editor
{
    /// <summary>
    /// Opt-in native early launch for iOS. When on, the export writes the app token and a JSON subset of the launch options into the
    /// app's Info.plist (dictionary <see cref="IosEarlyLaunchPolicy.InfoPlistKey"/>); <c>Plugins/iOS/BugseeEarlyLaunch.mm</c> reads it and starts
    /// the iOS SDK during the app's startup, before Unity's first scene, and the first C# <c>Bugsee.Launch</c> adopts that session.
    /// Off by default: then nothing is written (an earlier export's entry is removed on an Append export) and nothing changes.
    ///
    /// Settings: <see cref="IosEarlyLaunchPolicy.EnabledEnvVar"/> (1/0), <see cref="IosEarlyLaunchPolicy.TokenEnvVar"/>,
    /// <see cref="IosEarlyLaunchPolicy.OptionsEnvVar"/> (a JSON object of launch options, same keys as the C# options); each overrides the
    /// Bugsee/iOS/Early Launch window (EditorPrefs, per machine, like the Crash Report Extension setting). Build scripts can also set
    /// <see cref="Token"/> and <see cref="OptionsJson"/> here. Everything is best effort: the export is never failed by it.
    /// The decision and the Info.plist text are <see cref="IosEarlyLaunchPolicy"/> (unit-tested).
    /// </summary>
    public static class BugseeIosEarlyLaunchPostProcess
    {
        /// <summary>Build scripts may set the token here (the environment variable wins).</summary>
        public static string Token;
        /// <summary>Build scripts may set the options JSON here (the environment variable wins).</summary>
        public static string OptionsJson;

        internal const string PrefMode = "Bugsee.Ios.EarlyLaunch";
        internal const string PrefToken = "Bugsee.Ios.EarlyLaunch.Token";
        internal const string PrefOptions = "Bugsee.Ios.EarlyLaunch.Options";

        internal static IosEarlyLaunchMode CurrentMode()
        {
            string env = Environment.GetEnvironmentVariable(IosEarlyLaunchPolicy.EnabledEnvVar);
            return IosEarlyLaunchPolicy.ParseMode(string.IsNullOrWhiteSpace(env) ? EditorPrefs.GetString(PrefMode, "off") : env);
        }

        static string First(string env, string script, string pref)
        {
            string value = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrWhiteSpace(value)) return value;
            if (!string.IsNullOrWhiteSpace(script)) return script;
            return EditorPrefs.GetString(pref, "");
        }

        /// <summary>Entry point; never throws.</summary>
        internal static void Run(BuildTarget target, string pathToBuiltProject)
        {
            if (target != BuildTarget.iOS) return;
            try
            {
                Apply(pathToBuiltProject);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Bugsee] Native early launch not configured: {e.GetType().Name}: {e.Message}. The Xcode project is unchanged by this step.");
            }
        }

        static void Apply(string exportPath)
        {
            string plistPath = Path.Combine(exportPath, "Info.plist");
            if (!File.Exists(plistPath)) return;
            string original = File.ReadAllText(plistPath);

            var inputs = new IosEarlyLaunchInputs
            {
                Mode = CurrentMode(),
                Token = First(IosEarlyLaunchPolicy.TokenEnvVar, Token, PrefToken),
                OptionsJson = First(IosEarlyLaunchPolicy.OptionsEnvVar, OptionsJson, PrefOptions),
                WrapperVersion = BugseePackageVersion.Version,
                WrapperBuild = BugseePackageVersion.Build,
                UnityVersion = Application.unityVersion,
                ProductName = PlayerSettings.productName,
                Metal = FirstApiIsMetal(),
            };
            var decision = IosEarlyLaunchPolicy.Decide(inputs);

            string updated = decision.Enabled
                ? IosEarlyLaunchPolicy.SetInfoPlistEntry(original, decision.Entries)
                : IosEarlyLaunchPolicy.RemoveInfoPlistEntry(original);

            if (decision.Enabled && ReferenceEquals(updated, original))
            {
                Debug.LogWarning("[Bugsee] Native early launch not added: the exported Info.plist could not be edited.");
                return;
            }
            if (updated != original) File.WriteAllText(plistPath, updated, new UTF8Encoding(false));

            if (decision.Enabled || decision.IsWarning)
            {
                string line = "[Bugsee] " + char.ToUpperInvariant(decision.Reason[0]) + decision.Reason.Substring(1) + ".";
                if (decision.IsWarning) Debug.LogWarning(line); else Debug.Log(line);
            }
            else if (IosEarlyLaunchPolicy.HasInfoPlistEntry(original))
            {
                Debug.Log("[Bugsee] Removed the native early launch an earlier export had added (it is not wanted for this export).");
            }
            else if (HasCrashExtension(exportPath))
            {
                Debug.Log("[Bugsee] The Crash Report Extension is in this project. Launch watchdog kills (0x8badf00d) reach Bugsee only when Bugsee was launched in that "
                    + "process, and C# Launch comes too late for them: set " + IosEarlyLaunchPolicy.EnabledEnvVar + "=1 and " + IosEarlyLaunchPolicy.TokenEnvVar
                    + " (or Bugsee/iOS/Early Launch) to start Bugsee natively at launch.");
            }
        }

        static bool FirstApiIsMetal()
        {
            try
            {
                if (PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.iOS)) return true;
                var apis = PlayerSettings.GetGraphicsAPIs(BuildTarget.iOS);
                return apis == null || apis.Length == 0 || apis[0] == UnityEngine.Rendering.GraphicsDeviceType.Metal;
            }
            catch (Exception)
            {
                return true;
            }
        }

        static bool HasCrashExtension(string exportPath)
        {
            try
            {
                foreach (string project in Directory.GetDirectories(exportPath, "*.xcodeproj"))
                {
                    string pbx = Path.Combine(project, "project.pbxproj");
                    if (File.Exists(pbx) && XcodeCrashExtensionProject.Has(File.ReadAllText(pbx))) return true;
                }
            }
            catch (Exception) { }
            return false;
        }
    }

    /// <summary>Runs <see cref="BugseeIosEarlyLaunchPostProcess"/> after the SPM link (999), the symbol upload (1000) and the Crash Report Extension (1100).</summary>
    class BugseeIosEarlyLaunchPostProcessEntry : UnityEditor.Build.IPostprocessBuildWithReport
    {
        public int callbackOrder => 1200;

        public void OnPostprocessBuild(UnityEditor.Build.Reporting.BuildReport report) =>
            BugseeIosEarlyLaunchPostProcess.Run(report.summary.platform, report.summary.outputPath);
    }

    /// <summary>The Bugsee/iOS/Early Launch window: the on/off setting, token and options of the native early launch (per machine; CI uses the environment variables).</summary>
    class BugseeIosEarlyLaunchWindow : EditorWindow
    {
        string _token, _options;
        bool _on;

        [MenuItem("Bugsee/iOS/Early Launch...")]
        static void Open() => GetWindow<BugseeIosEarlyLaunchWindow>(true, "Bugsee iOS Early Launch").Show();

        void OnEnable()
        {
            _on = IosEarlyLaunchPolicy.ParseMode(EditorPrefs.GetString(BugseeIosEarlyLaunchPostProcess.PrefMode, "off")) == IosEarlyLaunchMode.On;
            _token = EditorPrefs.GetString(BugseeIosEarlyLaunchPostProcess.PrefToken, "");
            _options = EditorPrefs.GetString(BugseeIosEarlyLaunchPostProcess.PrefOptions, "");
        }

        void OnGUI()
        {
            EditorGUILayout.HelpBox("Starts the Bugsee iOS SDK natively at app launch, after Unity's didFinishLaunching returns and before the first scene. "
                + "Bugsee.Launch from C# then adopts that session. Off by default. PRIVACY: the SDK then starts before any C# code or consent check runs, "
                + "even if your app never calls Bugsee.Launch; do not enable it if Bugsee must wait for user consent. Expected to let the Crash Report "
                + "Extension file launch-watchdog kills (per the Bugsee iOS docs), not verified on a device in this package. The token is embedded in the "
                + "app (Info.plist). Environment variables " + IosEarlyLaunchPolicy.EnabledEnvVar + ", " + IosEarlyLaunchPolicy.TokenEnvVar + " and "
                + IosEarlyLaunchPolicy.OptionsEnvVar + " override these.", MessageType.Warning);
            _on = EditorGUILayout.Toggle("Enabled", _on);
            _token = EditorGUILayout.TextField("iOS app token", _token);
            EditorGUILayout.LabelField("Launch options (JSON object, optional)");
            _options = EditorGUILayout.TextArea(_options, GUILayout.MinHeight(60));
            if (GUILayout.Button("Save"))
            {
                EditorPrefs.SetString(BugseeIosEarlyLaunchPostProcess.PrefMode, _on ? "on" : "off");
                EditorPrefs.SetString(BugseeIosEarlyLaunchPostProcess.PrefToken, _token ?? "");
                EditorPrefs.SetString(BugseeIosEarlyLaunchPostProcess.PrefOptions, _options ?? "");
            }
        }
    }
}
