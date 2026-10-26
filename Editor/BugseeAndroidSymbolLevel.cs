using System;
using System.IO;
using System.Reflection;
using Bugsee.WrapperPolicy;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Bugsee.Editor
{
    /// <summary>
    /// Android native symbols for upload. Unity 6 defaults the debug symbol level to None (symbols.zip off) and
    /// SymbolTable holds symbol tables only, so file:line symbolication needs Full (Debugging) with a Zip format
    /// (see <see cref="AndroidSymbolPolicy"/>). The build warns when a Bugsee app token is set and that is not the
    /// case; the opt-in (<c>Bugsee &gt; Android &gt; Use Full Native Symbols</c>, or BUGSEE_ANDROID_FULL_SYMBOLS=1)
    /// builds with it and restores the settings afterwards.
    ///
    /// The setting lives in <c>UnityEditor.Android.UserBuildSettings.DebugSymbols.level/format</c> on Unity 6 (checked
    /// on 6000.0.84f1: <c>EditorUserBuildSettings.androidCreateSymbols</c> is obsolete there and only forwards to it) and in
    /// <c>EditorUserBuildSettings.androidCreateSymbols</c> on Unity 2021.3. Both are in the Android editor module, so they
    /// are reached by reflection and the package compiles for every active build target.
    ///
    /// The saved state is kept in Library/Bugsee (it survives an editor exit after an interrupted build) and is restored
    /// only while the settings still hold what the opt-in applied, never by an unrelated (non-Android) build.
    /// </summary>
    [InitializeOnLoad]
    sealed class BugseeAndroidSymbolLevel : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        const string PrefFullSymbols = "Bugsee.Android.FullNativeSymbols";
        const string InProgressKey = "Bugsee.Android.SymbolBuildInProgress";
        const string MenuPath = "Bugsee/Android/Use Full Native Symbols (Debugging) For Symbol Upload";

        // Level names per API: Unity 6 None/SymbolTable/Full, Unity 2021.3 Disabled/Public/Debugging.
        static readonly PropertyInfo NewLevel;
        static readonly PropertyInfo NewFormat;
        static readonly PropertyInfo OldCreateSymbols = typeof(EditorUserBuildSettings).GetProperty("androidCreateSymbols");

        public int callbackOrder => 20;

        static BugseeAndroidSymbolLevel()
        {
            Type debugSymbols = null;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    debugSymbols = assembly.GetType("UnityEditor.Android.UserBuildSettings+DebugSymbols");
                }
                catch (Exception)
                {
                }

                if (debugSymbols != null)
                {
                    break;
                }
            }

            NewLevel = debugSymbols?.GetProperty("level");
            NewFormat = debugSymbols?.GetProperty("format");
            EditorApplication.delayCall += () => ResolvePending(androidBuildOrEditorLoad: true, inProgress: SessionState.GetBool(InProgressKey, false));
        }

        [Serializable]
        sealed class PendingRestore
        {
            public string previousLevel;
            public int previousFormat;
            public string appliedLevel;
            public int appliedFormat;
        }

        static string PendingPath =>
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Application.dataPath)), "Library", "Bugsee", "native-symbols-restore.json");

        static bool UseFullSymbols =>
            EditorPrefs.GetBool(PrefFullSymbols, false) ||
            Environment.GetEnvironmentVariable("BUGSEE_ANDROID_FULL_SYMBOLS") == "1";

        static bool UploadConfigured =>
            BugseeCliRunner.HasAppToken && Environment.GetEnvironmentVariable("BUGSEE_NO_SYMBOL_UPLOAD") == null;

        static bool Available => NewLevel != null || OldCreateSymbols != null;

        static string ReadLevel()
        {
            return (NewLevel != null ? NewLevel.GetValue(null) : OldCreateSymbols?.GetValue(null))?.ToString();
        }

        static int ReadFormat()
        {
            return NewFormat != null ? Convert.ToInt32(NewFormat.GetValue(null), System.Globalization.CultureInfo.InvariantCulture) : -1;
        }

        static int ZipFlag()
        {
            return NewFormat != null ? Convert.ToInt32(Enum.Parse(NewFormat.PropertyType, "Zip"), System.Globalization.CultureInfo.InvariantCulture) : 0;
        }

        static void Write(string level, int format)
        {
            if (NewLevel != null)
            {
                NewLevel.SetValue(null, Enum.Parse(NewLevel.PropertyType, level));
                if (NewFormat != null && format >= 0)
                {
                    NewFormat.SetValue(null, Enum.ToObject(NewFormat.PropertyType, format));
                }
            }
            else if (OldCreateSymbols != null)
            {
                OldCreateSymbols.SetValue(null, Enum.Parse(OldCreateSymbols.PropertyType, level));
            }
        }

        static NativeSymbolLevel ToLevel(string name)
        {
            switch (name)
            {
                case "Full":
                case "Debugging": return NativeSymbolLevel.Debugging;
                case "SymbolTable":
                case "Public": return NativeSymbolLevel.Public;
                default: return NativeSymbolLevel.Disabled;
            }
        }

        static string FullName => NewLevel != null ? "Full" : "Debugging";

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

        /// <summary>
        /// Whether IL2CPP stack trace information carries file and line ("Method Name, File Name, and Line Number"), by
        /// reflection (PlayerSettings.GetIl2CppStacktraceInformation exists from Unity 2022.1); null when it cannot be read.
        /// </summary>
        static bool? Il2CppHasLineInfo()
        {
            try
            {
                MethodInfo get = typeof(PlayerSettings).GetMethod("GetIl2CppStacktraceInformation", new[] { typeof(NamedBuildTarget) });
                object value = get?.Invoke(null, new object[] { NamedBuildTarget.Android });
                return value == null ? null : AndroidSymbolPolicy.HasLineInfo(Enum.GetName(value.GetType(), value));
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.Android || !Available)
            {
                return;
            }

            // A restore left by an interrupted build is settled first, from what the settings hold now.
            ResolvePending(androidBuildOrEditorLoad: true, inProgress: false);

            string level = ReadLevel();
            int format = ReadFormat();
            bool zip = NewFormat == null || (format & ZipFlag()) != 0;
            bool development = (report.summary.options & BuildOptions.Development) != 0;
            bool managedNeedsLines = AndroidSymbolPolicy.NeedsNativeLinesForManagedFrames(
                Enum.GetName(typeof(ScriptingImplementation), PlayerSettings.GetScriptingBackend(NamedBuildTarget.Android)), development, Il2CppHasLineInfo());
            NativeSymbolDecision decision = AndroidSymbolPolicy.Decide(UseFullSymbols, UploadConfigured, ToLevel(level), zip, managedNeedsLines);
            if (decision.Warning != null)
            {
                Debug.LogWarning(decision.Warning);
            }

            if (!decision.Change)
            {
                return;
            }

            int appliedFormat = format >= 0 ? format | ZipFlag() : -1;
            var pending = new PendingRestore { previousLevel = level, previousFormat = format, appliedLevel = FullName, appliedFormat = appliedFormat };
            Directory.CreateDirectory(Path.GetDirectoryName(PendingPath));
            File.WriteAllText(PendingPath, JsonUtility.ToJson(pending));
            SessionState.SetBool(InProgressKey, true);
            Write(FullName, appliedFormat);
            Debug.Log("[Bugsee] Building with native debug symbols = " + FullName + " and a symbols zip for this build (restored afterwards).");
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.Android)
            {
                return;
            }

            SessionState.EraseBool(InProgressKey);
            ResolvePending(androidBuildOrEditorLoad: true, inProgress: false);
        }

        static void ResolvePending(bool androidBuildOrEditorLoad, bool inProgress)
        {
            if (!File.Exists(PendingPath) || !Available)
            {
                return;
            }

            PendingRestore pending;
            try
            {
                pending = JsonUtility.FromJson<PendingRestore>(File.ReadAllText(PendingPath));
            }
            catch (Exception)
            {
                File.Delete(PendingPath);
                return;
            }

            bool unchanged = ReadLevel() == pending.appliedLevel && ReadFormat() == pending.appliedFormat;
            switch (AndroidSymbolPolicy.Restore(androidBuildOrEditorLoad, inProgress, unchanged))
            {
                case SymbolRestoreAction.Skip:
                    return;
                case SymbolRestoreAction.Restore:
                    Write(pending.previousLevel, pending.previousFormat);
                    Debug.Log("[Bugsee] Restored the native debug symbols setting to " + pending.previousLevel + ".");
                    break;
                default:
                    Debug.Log("[Bugsee] Native debug symbols setting was changed since the Bugsee opt-in; keeping your setting.");
                    break;
            }

            File.Delete(PendingPath);
        }
    }
}
