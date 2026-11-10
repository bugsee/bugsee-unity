using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using Bugsee.WrapperPolicy;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Bugsee.Editor
{
    /// <summary>
    /// Adds the Crash Report Extension to the exported Xcode project: an ExtensionKit extension target linking the
    /// BugseeCrashExtension product of the Bugsee SPM package, plus the App Group both targets share. On iOS 27+ devices it
    /// reports what no in-process handler survives: the launch watchdog (0x8badf00d), foreground and background jetsam.
    ///
    /// The decision and every text it produces live in <see cref="IosCrashExtensionPolicy"/> and
    /// <see cref="XcodeCrashExtensionProject"/> (unit-tested). Everything here is best effort: the export is never failed by it.
    /// The project is edited as text in memory and written once at the end, so a failure leaves it as the SPM step left it.
    ///
    /// Setting: <see cref="IosCrashExtensionPolicy.ModeEnvVar"/> (auto, 1/0) or the Bugsee/iOS/Crash Report Extension menu
    /// (an EditorPrefs value, so per machine: the package has no project-level settings store; CI uses the variable).
    /// An export into an existing project (Append) refreshes the extension's settings, or removes it when it is no longer wanted.
    /// App Group: <see cref="IosCrashExtensionPolicy.AppGroupEnvVar"/>, else <see cref="AppGroupOverride"/>, else group.&lt;bundle id&gt;.
    /// </summary>
    public static class BugseeIosCrashExtensionPostProcess
    {
        /// <summary>Build scripts may set the App Group id here (the environment variable wins).</summary>
        public static string AppGroupOverride;

        const string PrefMode = "Bugsee.Ios.CrashExtension";
        const string MenuRoot = "Bugsee/iOS/Crash Report Extension/";

        [MenuItem(MenuRoot + "Auto (Xcode 27+ and automatic signing)")] static void MenuAuto() => SetMode("auto");
        [MenuItem(MenuRoot + "On")] static void MenuOn() => SetMode("on");
        [MenuItem(MenuRoot + "Off")] static void MenuOff() => SetMode("off");
        [MenuItem(MenuRoot + "Auto (Xcode 27+ and automatic signing)", true)] static bool MenuAutoValidate() => Check("auto");
        [MenuItem(MenuRoot + "On", true)] static bool MenuOnValidate() => Check("on");
        [MenuItem(MenuRoot + "Off", true)] static bool MenuOffValidate() => Check("off");

        static void SetMode(string mode) => EditorPrefs.SetString(PrefMode, mode);

        static bool Check(string mode)
        {
            string label = mode == "auto" ? "Auto (Xcode 27+ and automatic signing)" : mode == "on" ? "On" : "Off";
            Menu.SetChecked(MenuRoot + label, IosCrashExtensionPolicy.ParseMode(EditorPrefs.GetString(PrefMode, "auto")) == ModeOf(mode));
            return true;
        }

        static IosCrashExtensionMode ModeOf(string mode) => IosCrashExtensionPolicy.ParseMode(mode);

        internal static IosCrashExtensionMode CurrentMode()
        {
            string env = Environment.GetEnvironmentVariable(IosCrashExtensionPolicy.ModeEnvVar);
            return IosCrashExtensionPolicy.ParseMode(string.IsNullOrWhiteSpace(env) ? EditorPrefs.GetString(PrefMode, "auto") : env);
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
                Debug.LogWarning($"[Bugsee] Crash Report Extension not added: {e.GetType().Name}: {e.Message}. The Xcode project is unchanged by this step.");
            }
        }

        static void Apply(string exportPath)
        {
            string projectPath = XcodeProject.GetPBXProjectPath(exportPath);
            string original = File.ReadAllText(projectPath);
            var project = new XcodeProject();
            project.ReadFromFile(projectPath);
            string appGuid = project.GetUnityMainTargetGuid();

            string bundleId = XcodeCrashExtensionProject.AppBundleId(original, appGuid);
            string group = Environment.GetEnvironmentVariable(IosCrashExtensionPolicy.AppGroupEnvVar);
            if (string.IsNullOrWhiteSpace(group)) group = AppGroupOverride;
            bool has = XcodeCrashExtensionProject.Has(original);

            var inputs = new IosCrashExtensionInputs
            {
                Mode = CurrentMode(),
                XcodeVersionText = XcodeVersionText(),
                AutomaticSigningWithTeam = PlayerSettings.iOS.appleEnableAutomaticSigning && !string.IsNullOrEmpty(PlayerSettings.iOS.appleDeveloperTeamID),
                BatchMode = Application.isBatchMode,
                Package = PackageKind(original, exportPath),
                ForeignCrashReporterExtension = !has && ForeignCrashReporter(original, exportPath),
                BundleId = bundleId,
                ConfiguredAppGroup = group,
            };
            IosCrashExtensionDecision decision = IosCrashExtensionPolicy.Decide(inputs);
            if (!decision.Enabled)
            {
                if (decision.IsWarning) Debug.LogWarning("[Bugsee] " + decision.Reason); else Debug.Log("[Bugsee] " + decision.Reason);
                // An Append export keeps what an earlier export added: the project must show today's decision, and an iOS 27
                // target on an Xcode that cannot build it (or a group the profile lacks) would break the build.
                if (has) Retire(exportPath, projectPath, original, appGuid);
                return;
            }

            string infoPlistPath = Path.Combine(exportPath, "Info.plist");
            string appInfo = File.Exists(infoPlistPath) ? File.ReadAllText(infoPlistPath) : null;
            if (!IosCrashExtensionPolicy.TryResolveAppVersion(appInfo, name => XcodeCrashExtensionProject.AppSetting(original, appGuid, name), out string version, out string build))
            {
                Debug.LogWarning("[Bugsee] Crash Report Extension not added: could not read the app's version and build (CFBundleShortVersionString, CFBundleVersion) from Info.plist; the extension's must match.");
                return;
            }

            // The app's entitlements: every file its configurations name (they may differ), or a new one for them to name.
            var existing = XcodeCrashExtensionProject.AppEntitlementsPaths(original, appGuid);
            string newPath = "Unity-iPhone.entitlements";
            var files = new System.Collections.Generic.List<string>();
            foreach (string value in existing)
            {
                string relative = IosCrashExtensionPolicy.ProjectRelativePath(value);
                if (relative == null)
                {
                    Debug.LogWarning($"[Bugsee] Crash Report Extension not added: the app's CODE_SIGN_ENTITLEMENTS ({value}) uses a build variable this step cannot resolve.");
                    return;
                }
                if (!files.Contains(relative)) files.Add(relative);
            }
            if (existing.Count == 0) files.Add(newPath);

            // What an earlier export recorded: the group it used, and the files it added that group to itself. An Append export after
            // the bundle id (hence the derived group) changed drops the old group from those files only, and never a group the
            // developer already had there.
            string folder = Path.Combine(exportPath, XcodeCrashExtensionProject.FolderName);
            var previous = has ? PreviousState(folder) : null;

            var entitlements = new System.Collections.Generic.Dictionary<string, string>();
            var owned = new System.Collections.Generic.List<string>();
            foreach (string relative in files)
            {
                string full = Path.IsPathRooted(relative) ? relative : Path.Combine(exportPath, relative);
                var rewrite = IosCrashExtensionPolicy.RewriteEntitlements(File.Exists(full) ? File.ReadAllText(full) : null, relative, decision.AppGroup, previous);
                if (rewrite.Text == null)
                {
                    Debug.LogWarning($"[Bugsee] Crash Report Extension not added: could not add the App Group to {relative}.");
                    return;
                }
                entitlements[full] = rewrite.Text;
                if (rewrite.Owned) owned.Add(relative);
            }

            var spec = new XcodeCrashExtensionSpec
            {
                AppTargetGuid = appGuid,
                AppBundleId = bundleId,
                Version = version,
                Build = build,
                // Only configurations without a CODE_SIGN_ENTITLEMENTS get it; the others keep theirs.
                AppEntitlementsPathToSet = existing.Count > 0 ? existing[0] : newPath,
            };
            string edited = XcodeCrashExtensionProject.Apply(original, spec, out string error);
            if (edited == null)
            {
                Debug.LogWarning($"[Bugsee] Crash Report Extension not added: {error}. The Xcode project is unchanged by this step.");
                return;
            }

            // Everything that could fail has run; now write the files, the project last.
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, XcodeCrashExtensionProject.SwiftFileName), IosCrashExtensionPolicy.SwiftSource());
            string displayName = Regex.Match(appInfo ?? "", "<key>CFBundleDisplayName</key>\\s*<string>([^<$]+)</string>") is Match m && m.Success ? m.Groups[1].Value : Application.productName;
            File.WriteAllText(Path.Combine(folder, XcodeCrashExtensionProject.InfoPlistFileName), IosCrashExtensionPolicy.InfoPlist(decision.AppGroup, displayName, owned));
            File.WriteAllText(Path.Combine(folder, XcodeCrashExtensionProject.EntitlementsFileName), IosCrashExtensionPolicy.Entitlements(decision.AppGroup));
            foreach (var pair in entitlements)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(pair.Key));
                File.WriteAllText(pair.Key, pair.Value);
            }
            if (edited != original) File.WriteAllText(projectPath, edited);

            Debug.Log("[Bugsee] " + decision.Reason +
                $" Register the App Group on the app's and the extension's ({IosCrashExtensionPolicy.ExtensionBundleId(bundleId)}) App IDs unless Xcode signs automatically (from Xcode itself or with -allowProvisioningUpdates).");

            // A cheap re-check: whatever the app's entitlements setting names now must list the group. Plugins that run after this
            // step and replace CODE_SIGN_ENTITLEMENTS (ProjectCapabilityManager with its own file) can still drop it.
            foreach (string value in XcodeCrashExtensionProject.AppEntitlementsPaths(edited, appGuid))
            {
                string relative = IosCrashExtensionPolicy.ProjectRelativePath(value);
                string full = relative == null ? null : Path.IsPathRooted(relative) ? relative : Path.Combine(exportPath, relative);
                if (full == null || !File.Exists(full) || !IosCrashExtensionPolicy.EntitlementsHaveGroup(File.ReadAllText(full), decision.AppGroup))
                {
                    Debug.LogWarning($"[Bugsee] The app's entitlements ({value}) do not list the App Group {decision.AppGroup}; the Crash Report Extension will not report until they do.");
                }
            }
        }

        /// <summary>Takes a Crash Report Extension an earlier export added out of the project, with its files and the App Group on the app.</summary>
        static void Retire(string exportPath, string projectPath, string original, string appGuid)
        {
            string stripped = XcodeCrashExtensionProject.Remove(original, out string error);
            if (stripped == null)
            {
                Debug.LogWarning($"[Bugsee] Could not remove the Crash Report Extension an earlier export added: {error}. Delete the {IosCrashExtensionPolicy.TargetName} target in Xcode if the build fails.");
                return;
            }

            string folder = Path.Combine(exportPath, XcodeCrashExtensionProject.FolderName);
            var previous = PreviousState(folder);

            File.WriteAllText(projectPath, stripped);
            if (previous != null)
            {
                foreach (string value in XcodeCrashExtensionProject.AppEntitlementsPaths(stripped, appGuid))
                {
                    string relative = IosCrashExtensionPolicy.ProjectRelativePath(value);
                    if (relative == null) continue;
                    string full = Path.IsPathRooted(relative) ? relative : Path.Combine(exportPath, relative);
                    if (!File.Exists(full)) continue;
                    string text = File.ReadAllText(full);
                    string without = IosCrashExtensionPolicy.RetireEntitlements(text, relative, previous);
                    if (without != text) File.WriteAllText(full, without);
                }
            }

            foreach (string file in new[] { XcodeCrashExtensionProject.SwiftFileName, XcodeCrashExtensionProject.InfoPlistFileName, XcodeCrashExtensionProject.EntitlementsFileName })
            {
                try { File.Delete(Path.Combine(folder, file)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            try { if (Directory.Exists(folder) && Directory.GetFileSystemEntries(folder).Length == 0) Directory.Delete(folder); } catch (IOException) { }
            Debug.Log("[Bugsee] Removed the Crash Report Extension an earlier export had added (it is not wanted for this export).");
        }

        /// <summary>What the earlier export recorded in the extension's Info.plist (its App Group and the files it added it to), or null.</summary>
        static IosCrashExtensionPolicy.AppGroupState PreviousState(string folder)
        {
            string file = Path.Combine(folder, XcodeCrashExtensionProject.InfoPlistFileName);
            return File.Exists(file) ? IosCrashExtensionPolicy.ReadAppGroupState(File.ReadAllText(file)) : null;
        }

        static IosSdkPackageKind PackageKind(string pbxproj, string exportPath)
        {
            if (pbxproj.IndexOf("isa = XCLocalSwiftPackageReference;", StringComparison.Ordinal) < 0) return IosSdkPackageKind.Remote;
            string manifest = Path.Combine(exportPath, "Bugsee", "Package.swift");
            return File.Exists(manifest) && File.ReadAllText(manifest).Contains(IosCrashExtensionPolicy.SpmProduct)
                ? IosSdkPackageKind.LocalWithCrashExtension : IosSdkPackageKind.LocalWithoutCrashExtension;
        }

        /// <summary>A crash-reporter extension of the developer's own: an ExtensionKit target (not ours) whose Info.plist names the crash-reporter extension point.</summary>
        static bool ForeignCrashReporter(string pbxproj, string exportPath)
        {
            if (!XcodeCrashExtensionProject.HasAnyExtensionKitTarget(pbxproj)) return false;
            try
            {
                foreach (string dir in Directory.GetDirectories(exportPath))
                {
                    string leaf = Path.GetFileName(dir);
                    if (leaf == XcodeCrashExtensionProject.FolderName || leaf == "Classes" || leaf == "Libraries" || leaf == "Data" ||
                        leaf.StartsWith("Il2Cpp", StringComparison.Ordinal) || leaf.EndsWith(".xcodeproj", StringComparison.Ordinal)) continue;
                    string plist = Path.Combine(dir, "Info.plist");
                    if (File.Exists(plist) && IosCrashExtensionPolicy.IsCrashReporterInfoPlist(File.ReadAllText(plist))) return true;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            // An ExtensionKit extension whose kind we cannot tell is treated as a possible conflict only when it is not ours.
            return false;
        }

        static string XcodeVersionText()
        {
            try
            {
                var psi = new ProcessStartInfo("/usr/bin/xcodebuild", "-version")
                {
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                };
                using (var p = Process.Start(psi))
                {
                    // Read asynchronously: a blocking ReadToEnd would ignore the timeout when xcodebuild hangs (a first-run license prompt).
                    var output = new System.Text.StringBuilder();
                    p.OutputDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
                    p.ErrorDataReceived += (_, e) => { };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(15000)) { try { p.Kill(); } catch (Exception) { } return null; }
                    p.WaitForExit();
                    lock (output) return p.ExitCode == 0 ? output.ToString() : null;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>Runs <see cref="BugseeIosCrashExtensionPostProcess"/> after the SPM link (order 999) and the symbol-upload step (1000).</summary>
    class BugseeIosCrashExtensionPostProcessEntry : UnityEditor.Build.IPostprocessBuildWithReport
    {
        public int callbackOrder => 1100;

        public void OnPostprocessBuild(UnityEditor.Build.Reporting.BuildReport report) =>
            BugseeIosCrashExtensionPostProcess.Run(report.summary.platform, report.summary.outputPath);
    }
}
