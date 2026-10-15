using System;
using System.Reflection;
using Bugsee.WrapperPolicy;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Bugsee.Editor
{
    /// <summary>
    /// Android native symbols for upload. Unity 6 defaults "Create symbols.zip" to Disabled and Public holds symbol
    /// tables only, so file:line symbolication needs Debugging (see <see cref="AndroidSymbolPolicy"/>). The build warns
    /// when a Bugsee app token is set (symbols are uploaded) and the level is not Debugging; the opt-in
    /// (<c>Bugsee &gt; Android &gt; Use Full Native Symbols</c>, or BUGSEE_ANDROID_FULL_SYMBOLS=1) builds with Debugging for
    /// that build only and restores the setting afterwards (also after a failed build, on the next editor load).
    /// </summary>
    [InitializeOnLoad]
    sealed class BugseeAndroidSymbolLevel : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        const string PrefFullSymbols = "Bugsee.Android.FullNativeSymbols";
        const string RestoreKey = "Bugsee.Android.RestoreSymbolLevel";
        const string MenuPath = "Bugsee/Android/Use Full Native Symbols (Debugging) For Symbol Upload";

        // Before the upload (910) reads the zip; the restore is last.
        public int callbackOrder => 20;

        static BugseeAndroidSymbolLevel()
        {
            EditorApplication.delayCall += RestoreIfInterrupted;
        }

        static bool UseFullSymbols =>
            EditorPrefs.GetBool(PrefFullSymbols, false) ||
            Environment.GetEnvironmentVariable("BUGSEE_ANDROID_FULL_SYMBOLS") == "1";

        static bool UploadConfigured =>
            BugseeCliRunner.HasAppToken && Environment.GetEnvironmentVariable("BUGSEE_NO_SYMBOL_UPLOAD") == null;

        // EditorUserBuildSettings.androidCreateSymbols (an AndroidCreateSymbols enum: Disabled/Public/Debugging) lives in
        // the Android editor module; reached by reflection so the package compiles for every active build target.
        static readonly PropertyInfo CreateSymbols = typeof(EditorUserBuildSettings).GetProperty("androidCreateSymbols");

        static NativeSymbolLevel Current()
        {
            string name = CreateSymbols?.GetValue(null)?.ToString();
            switch (name)
            {
                case "Debugging": return NativeSymbolLevel.Debugging;
                case "Public": return NativeSymbolLevel.Public;
                default: return NativeSymbolLevel.Disabled;
            }
        }

        static bool SetCreateSymbols(string name)
        {
            if (CreateSymbols == null)
            {
                return false;
            }

            CreateSymbols.SetValue(null, Enum.Parse(CreateSymbols.PropertyType, name));
            return true;
        }

        [MenuItem(MenuPath)]
        static void ToggleFullSymbols()
        {
            EditorPrefs.SetBool(PrefFullSymbols, !EditorPrefs.GetBool(PrefFullSymbols, false));
        }

        [MenuItem(MenuPath, true)]
        static bool ToggleFullSymbolsValidate()
        {
            Menu.SetChecked(MenuPath, EditorPrefs.GetBool(PrefFullSymbols, false));
            return true;
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.Android)
            {
                return;
            }

            NativeSymbolLevel current = Current();
            NativeSymbolDecision decision = AndroidSymbolPolicy.Decide(UseFullSymbols, UploadConfigured, current);
            if (decision.Warning != null)
            {
                Debug.LogWarning(decision.Warning);
            }

            if (decision.Change && CreateSymbols != null)
            {
                SessionState.SetString(RestoreKey, CreateSymbols.GetValue(null).ToString());
                SetCreateSymbols("Debugging");
                Debug.Log("[Bugsee] Building with Create symbols.zip = Debugging for this build (restored afterwards).");
            }
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            RestoreIfInterrupted();
        }

        static void RestoreIfInterrupted()
        {
            string previous = SessionState.GetString(RestoreKey, "");
            if (previous.Length == 0)
            {
                return;
            }

            SessionState.EraseString(RestoreKey);
            SetCreateSymbols(previous);
        }
    }
}
