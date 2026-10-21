using System;

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
            "Add the scripting define " + Define + " (Player Settings > Other Settings > Scripting Define Symbols) or set the "
            + "environment variable " + EnvVar + "=1 for the build, then rebuild.";

        public const string AndroidMissingMessage =
            "[Bugsee] Feedback is not available: the bugsee-android-feedback AAR is not part of this app, so the call did "
            + "nothing. " + HowToEnable + " (An app that declares com.bugsee:bugsee-android-feedback itself needs no define.)";

        public const string IosMissingMessage =
            "[Bugsee] Feedback is not available: the BugseeFeedback package (github.com/bugsee/feedback-spm) is not linked "
            + "into this app, so the call did nothing. " + HowToEnable;

        public const string EditorMessage =
            "[Bugsee] Feedback has no Editor implementation: the call did nothing. It works in an Android or iOS player "
            + "built with the add-on (scripting define " + Define + ").";

        /// <summary>
        /// Why the Android Feedback AAR cannot build on this Android Gradle plugin, or null. The AAR pulls Material 1.13 and
        /// with it <c>androidx.dynamicanimation:1.1.0</c>, whose AAR metadata needs AGP 8.1.1 or newer: Unity 2021.3 and
        /// 2022.3 generate AGP 7.4.2 and Gradle fails there in <c>checkDebugAarMetadata</c> (seen with Unity 2022.3.62f3).
        /// </summary>
        public static string AndroidAgpProblem(int agpMajor, int agpMinor)
        {
            if (agpMajor > 8 || (agpMajor == 8 && agpMinor >= 1)) return null;
            return "[Bugsee] " + Define + " is set, but the generated Gradle project uses Android Gradle plugin " + agpMajor + "." + agpMinor
                + ", and bugsee-android-feedback needs 8.1.1 or newer (androidx.dynamicanimation 1.1.0, pulled in by Material 1.13): "
                + "the Gradle build would fail in checkDebugAarMetadata. Unity 2021.3 and 2022.3 generate AGP 7.4.2; use Unity 6 "
                + "(AGP 8.3 and newer), or remove the define for this build.";
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
