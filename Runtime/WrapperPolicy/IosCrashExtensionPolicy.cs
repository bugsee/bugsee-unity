using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Bugsee.WrapperPolicy
{
    /// <summary>What the export should do about the iOS Crash Report Extension, and why.</summary>
    public sealed class IosCrashExtensionDecision
    {
        public bool Enabled;
        /// <summary>One line for the build log: what is done, or why it is not.</summary>
        public string Reason;
        /// <summary>True when a developer who asked for the extension (or whose project holds a conflict) must read <see cref="Reason"/>.</summary>
        public bool IsWarning;
        /// <summary>The App Group id to use when <see cref="Enabled"/>.</summary>
        public string AppGroup;
    }

    /// <summary>The Swift package the Xcode project links the SDK from.</summary>
    public enum IosSdkPackageKind
    {
        /// <summary>github.com/bugsee/spm, resolved by Xcode: provides every product of the tag.</summary>
        Remote,
        /// <summary>A local package directory whose Package.swift declares the BugseeCrashExtension product.</summary>
        LocalWithCrashExtension,
        /// <summary>A local package without it (the Native~/ios/Bugsee stub): the extension target could not resolve it.</summary>
        LocalWithoutCrashExtension,
    }

    /// <summary>The on/off setting; Auto turns it on only where the build is likely to sign it.</summary>
    public enum IosCrashExtensionMode { Auto, On, Off }

    public sealed class IosCrashExtensionInputs
    {
        public IosCrashExtensionMode Mode;
        /// <summary>Output of <c>xcodebuild -version</c> (or any text with "Xcode N.M"); null when Xcode could not be asked.</summary>
        public string XcodeVersionText;
        /// <summary>PlayerSettings: "Automatically Sign" with a team id. Automatic signing registers the extension's App ID
        /// and the App Group by itself; a manual (wildcard) profile cannot carry an App Group.</summary>
        public bool AutomaticSigningWithTeam;
        /// <summary>Unity runs in -batchmode (CI, scripted builds). Those archive and export with their own signing
        /// (plain xcodebuild without -allowProvisioningUpdates, a manual profile, fastlane match), where a new App Group
        /// entitlement would break signing, so Auto stays off there.</summary>
        public bool BatchMode;
        public IosSdkPackageKind Package;
        /// <summary>The project already has a crash-reporter extension that is not ours (only one per app).</summary>
        public bool ForeignCrashReporterExtension;
        public string BundleId;
        /// <summary>The configured App Group (setting or <see cref="IosCrashExtensionPolicy.AppGroupEnvVar"/>); null/blank derives one.</summary>
        public string ConfiguredAppGroup;
    }

    /// <summary>
    /// Decisions and file contents for the Crash Report Extension target the export adds to the Xcode project
    /// (iOS 27+ launch watchdog, foreground and background jetsam reports; github.com/bugsee/spm, product
    /// <see cref="SpmProduct"/>). The sources of truth are the Xcode 27 "Crash Reporter Extension" template
    /// (ExtensionKit extension, extension point <see cref="ExtensionPointIdentifier"/>) and the Bugsee iOS docs.
    /// </summary>
    public static class IosCrashExtensionPolicy
    {
        public const string TargetName = "BugseeCrashReport";
        public const string SpmProduct = "BugseeCrashExtension";
        /// <summary>The Info.plist EXAppExtensionAttributes / EXExtensionPointIdentifier of Xcode 27's Crash Reporter Extension template.</summary>
        public const string ExtensionPointIdentifier = "com.apple.crash-reporter.extension";
        public const string ProductType = "com.apple.product-type.extensionkit-extension";
        public const string DeploymentTarget = "27.0";
        public const int MinXcodeMajor = 27;
        public const string AppGroupKey = "BugseeAppGroup";
        /// <summary>Extension Info.plist key listing the app entitlements files the export added the group to.</summary>
        public const string OwnedFilesKey = "BugseeAppGroupAddedTo";
        public const string ModeEnvVar = "BUGSEE_IOS_CRASH_EXTENSION";
        public const string AppGroupEnvVar = "BUGSEE_IOS_APP_GROUP";
        public const int MaxGroupLength = 128;

        /// <summary>The setting text: "1"/"on"/"true"/"yes" (On), "0"/"off"/"false"/"no" (Off), anything else (Auto).</summary>
        public static IosCrashExtensionMode ParseMode(string value)
        {
            switch ((value ?? "").Trim().ToLowerInvariant())
            {
                case "1": case "on": case "true": case "yes": case "enable": case "enabled": return IosCrashExtensionMode.On;
                case "0": case "off": case "false": case "no": case "disable": case "disabled": return IosCrashExtensionMode.Off;
                default: return IosCrashExtensionMode.Auto;
            }
        }

        static readonly Regex XcodeVersion = new Regex(@"Xcode\s+(\d+)(?:\.(\d+))?(?:\.(\d+))?", RegexOptions.CultureInvariant);

        /// <summary>Reads "Xcode 27.1" (also "Xcode 27", "Xcode 27.0.1 beta"); false for anything else.</summary>
        public static bool TryParseXcodeVersion(string text, out Version version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            Match m = XcodeVersion.Match(text);
            if (!m.Success) return false;
            if (!int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int major)) return false;
            int minor = m.Groups[2].Success && int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int mi) ? mi : 0;
            int patch = m.Groups[3].Success && int.TryParse(m.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int pa) ? pa : 0;
            version = new Version(major, minor, patch);
            return true;
        }

        /// <summary>"group.&lt;bundle id&gt;", or null when the bundle id cannot form a valid group id.</summary>
        public static string DeriveGroupId(string bundleId)
        {
            if (string.IsNullOrWhiteSpace(bundleId)) return null;
            string candidate = "group." + bundleId.Trim();
            return GroupIdProblem(candidate) == null ? candidate : null;
        }

        /// <summary>Null when valid; otherwise what is wrong. Apple: "group." then reverse-DNS characters (letters, digits, '.', '-').</summary>
        public static string GroupIdProblem(string group)
        {
            if (string.IsNullOrEmpty(group)) return "it is empty";
            if (group.Length > MaxGroupLength) return $"it is longer than {MaxGroupLength} characters";
            if (!group.StartsWith("group.", StringComparison.Ordinal)) return "it must start with \"group.\"";
            if (group.Length == "group.".Length) return "it has nothing after \"group.\"";
            foreach (char c in group)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '-';
                if (!ok) return $"it contains '{c}' (only letters, digits, '.' and '-' are allowed)";
            }
            if (group.EndsWith(".", StringComparison.Ordinal) || group.Contains("..")) return "it has an empty component";
            return null;
        }

        /// <summary>The extension's bundle id: it must be prefixed by the app's.</summary>
        public static string ExtensionBundleId(string appBundleId) => appBundleId.Trim() + "." + TargetName;

        /// <summary>Whether an Info.plist is a crash-reporter extension's (a developer's own extension).</summary>
        public static bool IsCrashReporterInfoPlist(string plistText) =>
            plistText != null && plistText.IndexOf(ExtensionPointIdentifier, StringComparison.Ordinal) >= 0;

        public static IosCrashExtensionDecision Decide(IosCrashExtensionInputs input)
        {
            IosCrashExtensionDecision Skip(string reason, bool warn) =>
                new IosCrashExtensionDecision { Enabled = false, Reason = "Crash Report Extension not added: " + reason, IsWarning = warn };

            if (input.Mode == IosCrashExtensionMode.Off)
                return Skip($"turned off ({ModeEnvVar}=0 or the Bugsee/iOS menu).", false);

            bool asked = input.Mode == IosCrashExtensionMode.On;
            if (!TryParseXcodeVersion(input.XcodeVersionText, out Version xcode))
                return Skip("could not tell the Xcode version (xcodebuild -version), and the extension needs Xcode 27.", asked);
            if (xcode.Major < MinXcodeMajor)
                return Skip($"Xcode {xcode.Major}.{xcode.Minor} cannot build it (Xcode {MinXcodeMajor}+ is required). Launch watchdog and out-of-memory kills are not reported.", asked);

            if (input.Package == IosSdkPackageKind.LocalWithoutCrashExtension)
                return Skip($"the local iOS SDK package has no {SpmProduct} product (use the published package, or a local package that declares it).", asked);

            if (input.ForeignCrashReporterExtension)
                return Skip("the project already has a crash-reporter extension and an app can have only one.", true);

            string group = string.IsNullOrWhiteSpace(input.ConfiguredAppGroup) ? DeriveGroupId(input.BundleId) : input.ConfiguredAppGroup.Trim();
            if (group == null)
                return Skip($"no App Group id could be derived from bundle id \"{input.BundleId}\"; set {AppGroupEnvVar}=group.<your id>.", true);
            string problem = GroupIdProblem(group);
            if (problem != null)
                return Skip($"the App Group id \"{group}\" is not valid: {problem}.", true);

            if (input.Mode == IosCrashExtensionMode.Auto && input.BatchMode)
                return Skip($"Auto is off in batch mode (it changes the app's entitlements, which scripted signing may not cover). Set {ModeEnvVar}=1 to add it, and sign with -allowProvisioningUpdates or add the App Group {group} to both App IDs.", false);

            if (input.Mode == IosCrashExtensionMode.Auto && !input.AutomaticSigningWithTeam)
                return Skip($"it needs the App Groups capability, which a manual provisioning profile may not carry. Turn on Automatically Sign with a team, or set {ModeEnvVar}=1 and add the App Group {group} to both App IDs.", false);

            return new IosCrashExtensionDecision
            {
                Enabled = true,
                AppGroup = group,
                Reason = $"Crash Report Extension added (iOS {DeploymentTarget}+ devices only; App Group {group}).",
            };
        }

        /// <summary>The extension's whole source: the Bugsee protocol supplies <c>processCrashReport</c>.</summary>
        public static string SwiftSource() =>
            "import BugseeCrashExtension\n\n@main struct CrashReport: BugseeCrashReporterExtension {}\n";

        /// <summary>The extension's Info.plist. Version and build are build settings, set to the app's values on this target.</summary>
        /// <param name="ownedEntitlementsFiles">Project-relative paths of the app entitlements files the export itself added
        /// <paramref name="appGroup"/> to (see <see cref="RewriteEntitlements"/>); recorded here, in a file of the extension's own
        /// (an unknown Info.plist key, as <see cref="AppGroupKey"/> is), so a later export knows which group is Bugsee's to remove.</param>
        public static string InfoPlist(string appGroup, string displayName, IEnumerable<string> ownedEntitlementsFiles = null)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            sb.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
            sb.Append("<plist version=\"1.0\">\n<dict>\n");
            Pair(sb, "BugseeAppGroup", appGroup);
            var owned = ownedEntitlementsFiles == null ? new List<string>() : new List<string>(ownedEntitlementsFiles);
            if (owned.Count > 0)
            {
                sb.Append("\t<key>" + OwnedFilesKey + "</key>\n\t<array>\n");
                foreach (string file in owned) sb.Append("\t\t<string>" + Escape(file) + "</string>\n");
                sb.Append("\t</array>\n");
            }
            Pair(sb, "CFBundleDevelopmentRegion", "en");
            Pair(sb, "CFBundleDisplayName", displayName);
            Pair(sb, "CFBundleExecutable", "$(EXECUTABLE_NAME)");
            Pair(sb, "CFBundleIdentifier", "$(PRODUCT_BUNDLE_IDENTIFIER)");
            Pair(sb, "CFBundleInfoDictionaryVersion", "6.0");
            Pair(sb, "CFBundleName", "$(PRODUCT_NAME)");
            Pair(sb, "CFBundlePackageType", "$(PRODUCT_BUNDLE_PACKAGE_TYPE)");
            Pair(sb, "CFBundleShortVersionString", "$(MARKETING_VERSION)");
            Pair(sb, "CFBundleVersion", "$(CURRENT_PROJECT_VERSION)");
            sb.Append("\t<key>EXAppExtensionAttributes</key>\n\t<dict>\n");
            sb.Append("\t\t<key>EXExtensionPointIdentifier</key>\n\t\t<string>" + Escape(ExtensionPointIdentifier) + "</string>\n");
            sb.Append("\t</dict>\n</dict>\n</plist>\n");
            return sb.ToString();
        }

        /// <summary>The extension's entitlements file.</summary>
        public static string Entitlements(string appGroup) => AddGroupToEntitlements(null, appGroup);

        /// <summary>
        /// The app's entitlements with <paramref name="appGroup"/> in com.apple.security.application-groups; every other
        /// entry stays. A null or blank <paramref name="existing"/> gives a new file. Returns the text unchanged when the
        /// group is already listed, and null when the file is not a plist this can edit.
        /// </summary>
        public static string AddGroupToEntitlements(string existing, string appGroup)
        {
            const string key = "<key>com.apple.security.application-groups</key>";
            string item = "<string>" + Escape(appGroup) + "</string>";
            if (string.IsNullOrWhiteSpace(existing))
            {
                return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                    "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
                    "<plist version=\"1.0\">\n<dict>\n\t" + key + "\n\t<array>\n\t\t" + item + "\n\t</array>\n</dict>\n</plist>\n";
            }

            Match existingKey = Regex.Match(existing, Regex.Escape(key) + @"\s*<array\s*/>");
            if (existingKey.Success)
            {
                return existing.Substring(0, existingKey.Index) + key + "\n\t<array>\n\t\t" + item + "\n\t</array>" + existing.Substring(existingKey.Index + existingKey.Length);
            }

            int at = existing.IndexOf(key, StringComparison.Ordinal);
            if (at >= 0)
            {
                int close = existing.IndexOf("</array>", at, StringComparison.Ordinal);
                if (close < 0 || existing.IndexOf("<array>", at, StringComparison.Ordinal) < 0) return null;
                string list = existing.Substring(at, close - at);
                if (list.Contains(item)) return existing;
                return existing.Substring(0, close) + "\t" + item + "\n\t" + existing.Substring(close);
            }

            if (Regex.IsMatch(existing, @"<dict\s*/>"))
            {
                return Regex.Replace(existing, @"<dict\s*/>", "<dict>\n\t" + key + "\n\t<array>\n\t\t" + item + "\n\t</array>\n</dict>", RegexOptions.None, TimeSpan.FromSeconds(1));
            }

            int end = existing.LastIndexOf("</dict>", StringComparison.Ordinal);
            if (end < 0) return null;
            return existing.Substring(0, end) + "\t" + key + "\n\t<array>\n\t\t" + item + "\n\t</array>\n" + existing.Substring(end);
        }

        /// <summary>
        /// The app's entitlements without <paramref name="appGroup"/>, removed only from the com.apple.security.application-groups
        /// array (the same string under another key stays); other groups and keys stay, and an array left empty goes with its key.
        /// Text that does not list the group there comes back unchanged.
        /// </summary>
        public static string RemoveGroupFromEntitlements(string existing, string appGroup)
        {
            if (string.IsNullOrEmpty(existing) || string.IsNullOrEmpty(appGroup)) return existing;
            const string key = @"<key>com\.apple\.security\.application-groups</key>\s*";
            Match m = Regex.Match(existing, key + @"<array>(?<body>.*?)</array>", RegexOptions.Singleline);
            if (!m.Success) return existing;
            string body = m.Groups["body"].Value;
            string item = "<string>" + Escape(appGroup) + "</string>";
            string without = Regex.Replace(body, @"[ \t]*" + Regex.Escape(item) + @"[ \t]*\r?\n?", "");
            if (without == body) return existing;
            Group bodyGroup = m.Groups["body"];
            if (without.Contains("<string>"))
                return existing.Substring(0, bodyGroup.Index) + without + existing.Substring(bodyGroup.Index + bodyGroup.Length);

            // Nothing left in the array: the key goes with it, with the line it was on.
            int start = m.Index;
            while (start > 0 && (existing[start - 1] == ' ' || existing[start - 1] == '\t')) start--;
            int end = m.Index + m.Length;
            if (end < existing.Length && existing[end] == '\r') end++;
            if (end < existing.Length && existing[end] == '\n') end++;
            return existing.Substring(0, start) + existing.Substring(end);
        }

        /// <summary>What an earlier export recorded in the extension's Info.plist: its App Group, and the app entitlements files it added that group to.</summary>
        public sealed class AppGroupState
        {
            public string Group;
            /// <summary>Project-relative paths of the entitlements files the export added <see cref="Group"/> to itself (empty for a project an older package exported).</summary>
            public List<string> OwnedFiles = new List<string>();
        }

        /// <summary>The previous export's App Group state from its extension Info.plist text; null when there is none (no file, or no group).</summary>
        public static AppGroupState ReadAppGroupState(string extensionInfoPlist)
        {
            if (string.IsNullOrEmpty(extensionInfoPlist)) return null;
            Match group = Regex.Match(extensionInfoPlist, "<key>" + AppGroupKey + @"</key>\s*<string>([^<]+)</string>");
            if (!group.Success) return null;
            var state = new AppGroupState { Group = Unescape(group.Groups[1].Value) };
            Match owned = Regex.Match(extensionInfoPlist, "<key>" + OwnedFilesKey + @"</key>\s*<array>(.*?)</array>", RegexOptions.Singleline);
            if (owned.Success)
                foreach (Match item in Regex.Matches(owned.Groups[1].Value, "<string>([^<]*)</string>")) state.OwnedFiles.Add(Unescape(item.Groups[1].Value));
            return state;
        }

        /// <summary>The result of <see cref="RewriteEntitlements"/>.</summary>
        public sealed class EntitlementsRewrite
        {
            /// <summary>The file's new text; null when the file is not a plist the group can be added to.</summary>
            public string Text;
            /// <summary>Whether the group in this file is the export's own (added by it, now or by an earlier export), and so its to remove later.</summary>
            public bool Owned;
        }

        /// <summary>
        /// One app entitlements file for an export that wants <paramref name="newGroup"/>. A group the export added earlier and that is
        /// no longer wanted (the bundle id changed) is removed; a group that was already in the file, or that the export did not add, is
        /// never removed. The new group is added unless the file lists it; it is the export's own only when the export added it.
        /// </summary>
        /// <param name="current">The file's text, or null when there is no file yet (a new file is the export's own).</param>
        /// <param name="relativePath">The file's project-relative path, the key of <see cref="AppGroupState.OwnedFiles"/>.</param>
        /// <param name="previous">What the earlier export recorded, or null on the first export.</param>
        public static EntitlementsRewrite RewriteEntitlements(string current, string relativePath, string newGroup, AppGroupState previous)
        {
            bool ownedBefore = previous != null && previous.Group != null && previous.OwnedFiles.Contains(relativePath);
            string text = current;
            if (ownedBefore && previous.Group != newGroup) text = RemoveGroupFromEntitlements(text, previous.Group);

            if (EntitlementsHaveGroup(text, newGroup))
                return new EntitlementsRewrite { Text = text, Owned = ownedBefore && previous.Group == newGroup };
            string added = AddGroupToEntitlements(text, newGroup);
            return new EntitlementsRewrite { Text = added, Owned = added != null };
        }

        /// <summary>The file's text for an export that no longer wants the extension: the earlier export's group goes only when that export added it to this file.</summary>
        public static string RetireEntitlements(string current, string relativePath, AppGroupState previous)
        {
            if (current == null || previous == null || previous.Group == null || !previous.OwnedFiles.Contains(relativePath)) return current;
            return RemoveGroupFromEntitlements(current, previous.Group);
        }

        /// <summary>Whether the entitlements plist lists <paramref name="appGroup"/> under com.apple.security.application-groups.</summary>
        public static bool EntitlementsHaveGroup(string plist, string appGroup)
        {
            if (string.IsNullOrEmpty(plist) || string.IsNullOrEmpty(appGroup)) return false;
            Match m = Regex.Match(plist, @"<key>com\.apple\.security\.application-groups</key>\s*<array>(.*?)</array>", RegexOptions.Singleline);
            return m.Success && m.Groups[1].Value.Contains("<string>" + Escape(appGroup) + "</string>");
        }

        /// <summary>
        /// A CODE_SIGN_ENTITLEMENTS value as a path under the project directory: "$(SRCROOT)/x", "${PROJECT_DIR}/x" and "x" are all
        /// "x"; an absolute path stays. Null when it names any other build variable (it cannot be resolved here).
        /// </summary>
        public static string ProjectRelativePath(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string v = Regex.Replace(value.Trim(), @"^\$[({](?:SRCROOT|PROJECT_DIR)[)}]/?", "");
            if (v.Contains("$(") || v.Contains("${") || v.Length == 0) return null;
            return v;
        }

        /// <summary>
        /// The app's version and build as the extension must carry them: the literal in the app's Info.plist, or, when it
        /// is a build-setting reference ($(MARKETING_VERSION)), that setting's value from the project. Null when unknown.
        /// </summary>
        public static bool TryResolveAppVersion(string appInfoPlist, Func<string, string> buildSetting, out string version, out string build)
        {
            version = ResolvePlistString(appInfoPlist, "CFBundleShortVersionString", buildSetting);
            build = ResolvePlistString(appInfoPlist, "CFBundleVersion", buildSetting);
            return !string.IsNullOrEmpty(version) && !string.IsNullOrEmpty(build);
        }

        static string ResolvePlistString(string plist, string key, Func<string, string> buildSetting)
        {
            if (plist == null) return null;
            Match m = Regex.Match(plist, "<key>" + Regex.Escape(key) + @"</key>\s*<string>([^<]*)</string>");
            if (!m.Success) return null;
            string value = m.Groups[1].Value.Trim();
            Match reference = Regex.Match(value, @"^\$[({](\w+)[)}]$");
            if (!reference.Success) return value.Length == 0 ? null : value;
            string resolved = buildSetting?.Invoke(reference.Groups[1].Value);
            return string.IsNullOrWhiteSpace(resolved) ? null : resolved.Trim();
        }

        static void Pair(StringBuilder sb, string key, string value) =>
            sb.Append("\t<key>" + key + "</key>\n\t<string>" + Escape(value) + "</string>\n");

        static string Unescape(string s) => s.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");

        static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
