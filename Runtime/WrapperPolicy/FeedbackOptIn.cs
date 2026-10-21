using System;
using System.Text.RegularExpressions;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Feedback (the in-app chat) is an optional native add-on on both SDKs: the Android <c>bugsee-android-feedback</c>
    /// AAR and the iOS <c>BugseeFeedback</c> Swift package (github.com/bugsee/feedback-spm). The wrapper links it only
    /// when the project opts in, so an app that does not use it carries no Compose/SwiftUI chat code.
    ///
    /// The switch is the scripting define <see cref="Define"/> (Player Settings, Scripting Define Symbols, for the
    /// build target), or the environment variable <see cref="EnvVar"/> set to <c>1</c>/<c>true</c> for a CI build.
    /// </summary>
    public static class FeedbackOptIn
    {
        public const string Define = "BUGSEE_FEEDBACK";
        public const string EnvVar = "BUGSEE_FEEDBACK";

        public const string AndroidArtifact = "bugsee-android-feedback";

        /// <summary>The iOS add-on's package. Published at the same version as the core package it pins.</summary>
        public const string IosPackageUrl = "https://github.com/bugsee/feedback-spm.git";
        public const string IosProduct = "BugseeFeedback";

        const string HowToEnable =
            "Add " + Define + " to Player Settings > Other Settings > Scripting Define Symbols for the platform (or to the active "
            + "Unity 6 build profile), or set the environment variable " + EnvVar + "=1 in the Editor process that exports the "
            + "build, then rebuild. (Defines passed only through BuildPlayerOptions.extraScriptingDefines are not seen.)";

        public const string AndroidMissingMessage =
            "[Bugsee] Feedback is not available: the bugsee-android-feedback AAR is not part of this app, so the call did "
            + "nothing. " + HowToEnable + " (An app that declares com.bugsee:bugsee-android-feedback itself needs no define.)";

        public const string IosMissingMessage =
            "[Bugsee] Feedback is not available: the BugseeFeedback package (github.com/bugsee/feedback-spm) is not linked "
            + "into this app, so the call did nothing. " + HowToEnable;

        public const string EditorMessage =
            "[Bugsee] Feedback has no Editor implementation: the call did nothing. It works in an Android or iOS player "
            + "built with the add-on (scripting define " + Define + ").";

        public const string PlayerMessage =
            "[Bugsee] Feedback is only available in Android and iOS players: the call did nothing on this platform.";

        /// <summary>
        /// Why the Android Feedback AAR cannot build on this Android Gradle plugin, or null. The AAR pulls Material 1.13 and
        /// with it <c>androidx.dynamicanimation:1.1.0</c>, whose AAR metadata needs AGP 8.1.1 or newer: Unity 2021.3 and
        /// 2022.3 generate AGP 4.0.1 to 7.4.2 and Gradle fails there in <c>checkDebugAarMetadata</c> (seen with Unity 2022.3.62f3).
        /// </summary>
        public static string AndroidAgpProblem(int agpMajor, int agpMinor, int agpPatch = 0)
        {
            bool ok = agpMajor > 8 || (agpMajor == 8 && (agpMinor > 1 || (agpMinor == 1 && agpPatch >= 1)));
            if (ok) return null;
            return "[Bugsee] " + Define + " is set, but the generated Gradle project uses Android Gradle plugin " + agpMajor + "." + agpMinor
                + "." + agpPatch + ", and bugsee-android-feedback needs 8.1.1 or newer (androidx.dynamicanimation 1.1.0, pulled in by "
                + "Material 1.13): the Gradle build would fail in checkDebugAarMetadata. Unity 2021.3 and 2022.3 generate AGP 4.0.1 to "
                + "7.4.2; use Unity 6 (AGP 8.3 and newer), or remove the define for this build.";
        }

        /// <summary>The lowest compileSdk the Feedback AAR's dependencies build with (androidx.compose.ui 1.8 via richeditor-compose).</summary>
        public const int MinCompileSdk = 35;

        static readonly Regex CompileSdkLine = new Regex(
            @"^\s*compileSdk(?:Version)?\s*(?:=\s*)?(?<v>\d+)\b", RegexOptions.Multiline | RegexOptions.CultureInvariant);

        /// <summary>
        /// Why the Feedback AAR cannot build with the compileSdk the generated <c>unityLibrary/build.gradle(.kts)</c> declares, or
        /// null (also when the value is not a plain number). androidx.compose.ui 1.8 needs 35; activity 1.8, appcompat 1.7 and
        /// dynamicanimation 1.1 need 34.
        /// </summary>
        public static string AndroidCompileSdkProblem(string libraryBuildGradle)
        {
            if (libraryBuildGradle == null) return null;
            Match m = CompileSdkLine.Match(libraryBuildGradle);
            if (!m.Success || !int.TryParse(m.Groups["v"].Value, out int sdk) || sdk >= MinCompileSdk) return null;
            return "[Bugsee] " + Define + " is set, but the generated Gradle project compiles against API " + sdk
                + ", and bugsee-android-feedback needs compileSdk " + MinCompileSdk + " or newer (androidx.compose.ui 1.8). "
                + "Raise Player Settings > Other Settings > Target API Level (or the project's compileSdk), or remove the define for this build.";
        }

        /// <summary>
        /// The first reason the Feedback AAR cannot build with the generated project (AGP, then compileSdk), or null. Files that
        /// are missing or whose version cannot be read are not a reason.
        /// </summary>
        public static string AndroidToolchainProblem(string rootBuildGradle, string catalog, string libraryBuildGradle)
        {
            if (rootBuildGradle != null && GradleTemplateDefaults.TryGetAgpVersion(rootBuildGradle, catalog, out int major, out int minor, out int patch))
            {
                string problem = AndroidAgpProblem(major, minor, patch);
                if (problem != null) return problem;
            }

            return AndroidCompileSdkProblem(libraryBuildGradle);
        }

        /// <summary>True when <paramref name="defines"/> (a <c>;</c>-separated define list) names <see cref="Define"/>, or <paramref name="envValue"/> is <c>1</c> or <c>true</c>.</summary>
        public static bool IsEnabled(string defines, string envValue)
        {
            if (!string.IsNullOrWhiteSpace(envValue))
            {
                string value = envValue.Trim();
                if (value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)) return true;
            }

            if (string.IsNullOrEmpty(defines)) return false;
            foreach (string symbol in defines.Split(';'))
            {
                if (string.Equals(symbol.Trim(), Define, StringComparison.Ordinal)) return true;
            }

            return false;
        }
    }
}
