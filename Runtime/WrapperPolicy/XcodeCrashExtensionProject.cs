using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Bugsee.WrapperPolicy
{
    public sealed class XcodeCrashExtensionSpec
    {
        /// <summary>The object id of the Unity app target ("Unity-iPhone").</summary>
        public string AppTargetGuid;
        public string AppBundleId;
        /// <summary>The app's version and build, copied to MARKETING_VERSION / CURRENT_PROJECT_VERSION on the extension.</summary>
        public string Version;
        public string Build;
        /// <summary>Project-relative path of an entitlements file to set on each app configuration that names none (a configuration that names one keeps it); null sets nothing.</summary>
        public string AppEntitlementsPathToSet;
    }

    /// <summary>
    /// Text edits on a serialized Xcode project (project.pbxproj) that add the Crash Report Extension target: an ExtensionKit
    /// extension (<see cref="IosCrashExtensionPolicy.ProductType"/>) linking the <see cref="IosCrashExtensionPolicy.SpmProduct"/>
    /// product of the package the app already links, embedded in the app ("Embed ExtensionKit Extensions"). The app target keeps
    /// the Bugsee product. Unity's PBXProject API has no ExtensionKit target type, hence the text edits.
    /// The files the target names (<see cref="FolderName"/>/*) are written by the caller.
    /// </summary>
    public static class XcodeCrashExtensionProject
    {
        public const string FolderName = IosCrashExtensionPolicy.TargetName;
        public const string SwiftFileName = "CrashReport.swift";
        public const string InfoPlistFileName = "Info.plist";
        public const string EntitlementsFileName = IosCrashExtensionPolicy.TargetName + ".entitlements";
        public const string EmbedPhaseName = "Embed ExtensionKit Extensions";
        const string Guid = "[0-9A-F]{24}";
        const RegexOptions Options = RegexOptions.CultureInvariant;

        /// <summary>Whether the project already has this target (an earlier export).</summary>
        public static bool Has(string pbxproj) =>
            Regex.IsMatch(pbxproj, @"isa = PBXNativeTarget;[^}]*?name = " + IosCrashExtensionPolicy.TargetName + ";", Options | RegexOptions.Singleline);

        /// <summary>Whether the project has an ExtensionKit extension target of anyone's (it may be a crash reporter).</summary>
        public static bool HasAnyExtensionKitTarget(string pbxproj) =>
            pbxproj.IndexOf("productType = \"" + IosCrashExtensionPolicy.ProductType + "\"", StringComparison.Ordinal) >= 0;

        /// <summary>The CODE_SIGN_ENTITLEMENTS the app target's first configuration sets, or null.</summary>
        public static string AppEntitlementsPath(string pbxproj, string appTargetGuid) =>
            AppSetting(pbxproj, appTargetGuid, "CODE_SIGN_ENTITLEMENTS");

        /// <summary>The distinct CODE_SIGN_ENTITLEMENTS values of the app target's configurations (they may differ per configuration).</summary>
        public static List<string> AppEntitlementsPaths(string pbxproj, string appTargetGuid)
        {
            var result = new List<string>();
            try
            {
                foreach (string cfg in ConfigGuids(pbxproj, appTargetGuid))
                {
                    string v = Setting(Object(pbxproj, cfg), "CODE_SIGN_ENTITLEMENTS");
                    if (!string.IsNullOrEmpty(v) && !result.Contains(v)) result.Add(v);
                }
            }
            catch (InvalidOperationException) { }
            return result;
        }

        /// <summary>A build setting of the app target (first configuration that sets it), unquoted; null when unset.</summary>
        public static string AppSetting(string pbxproj, string appTargetGuid, string name)
        {
            try
            {
                foreach (string cfg in ConfigGuids(pbxproj, appTargetGuid))
                {
                    string v = Setting(Object(pbxproj, cfg), name);
                    if (v != null) return v;
                }
            }
            catch (InvalidOperationException) { }
            return null;
        }

        /// <summary>The project's bundle id for the app target, or null.</summary>
        public static string AppBundleId(string pbxproj, string appTargetGuid) => AppSetting(pbxproj, appTargetGuid, "PRODUCT_BUNDLE_IDENTIFIER");

        /// <returns>The project with the extension target added, or null (with the reason in <paramref name="error"/>) when the project
        /// is not shaped as expected. An already-present target (an Append export) keeps its objects and has the settings that
        /// follow the app refreshed (bundle id, version, build, team, signing style, entitlements path), so the result is the same as for a fresh add.</returns>
        public static string Apply(string pbxproj, XcodeCrashExtensionSpec spec, out string error)
        {
            error = null;
            if (pbxproj == null || spec == null) { error = "no project text or spec"; return null; }
            try { return Has(pbxproj) ? Refresh(pbxproj, spec) : Build(pbxproj, spec); }
            catch (InvalidOperationException e) { error = e.Message; return null; }
        }

        static string Build(string text, XcodeCrashExtensionSpec spec)
        {
            string name = IosCrashExtensionPolicy.TargetName;
            string appTarget = spec.AppTargetGuid ?? throw new InvalidOperationException("no app target id");
            string root = Match1(text, @"rootObject = (" + Guid + ")", "rootObject");
            Object(text, appTarget); // exists

            string appBlock = Object(text, appTarget);
            if (!appBlock.Contains("isa = PBXNativeTarget;")) throw new InvalidOperationException("the app target is not a PBXNativeTarget");

            // The package the app already links: the extension product comes from the same reference.
            Match bugsee = Regex.Match(text,
                @"[ \t]*" + Guid + @"(?: /\*[^\n]*?\*/)? = \{\s*isa = XCSwiftPackageProductDependency;\s*(?:package = (?<pkg>" + Guid + @")(?: /\*[^\n]*?\*/)?;\s*)?productName = Bugsee;", Options);
            if (!bugsee.Success) throw new InvalidOperationException("the project does not link the Bugsee package product");
            string packageLine = bugsee.Groups["pkg"].Success
                ? "\t\t\tpackage = " + bugsee.Groups["pkg"].Value + ";\n" : "";

            var ids = new Ids(text);
            string target = ids.New("target"), proxy = ids.New("proxy"), dependency = ids.New("dependency"),
                configList = ids.New("configList"), sources = ids.New("sources"), frameworks = ids.New("frameworks"),
                embed = ids.New("embed"), group = ids.New("group"),
                swiftRef = ids.New("swiftRef"), plistRef = ids.New("plistRef"), entRef = ids.New("entRef"), product = ids.New("product"),
                swiftBuild = ids.New("swiftBuild"), packageBuild = ids.New("packageBuild"), embedBuild = ids.New("embedBuild"),
                packageProduct = ids.New("packageProduct");

            string appConfigList = Match1(appBlock, @"buildConfigurationList = (" + Guid + ")", "the app target's buildConfigurationList");
            var appConfigs = ConfigGuids(text, appTarget);
            if (appConfigs.Count == 0) throw new InvalidOperationException("the app target has no build configurations");

            string productFile = name + ".appex";
            string ver = spec.Version, bld = spec.Build;

            // ---- extension build configurations: same names as the app's.
            var configIds = new List<string>();
            var configBlocks = new StringBuilder();
            string defaultName = "Release";
            Match def = Regex.Match(Object(text, appConfigList), @"defaultConfigurationName = ([^;]+);");
            if (def.Success) defaultName = Unquote(def.Groups[1].Value);
            string style = AppSigningStyle(text, appTarget, appConfigs);
            foreach (string appConfig in appConfigs)
            {
                string appCfgBlock = Object(text, appConfig);
                string cfgName = Unquote(Match1(appCfgBlock, @"\bname = ([^;]+);", "a build configuration name"));
                string id = ids.New("config:" + cfgName);
                configIds.Add(id);
                bool debug = cfgName.IndexOf("Debug", StringComparison.OrdinalIgnoreCase) >= 0;
                var s = new List<KeyValuePair<string, string>>();
                void Set(string k, string v) => s.Add(new KeyValuePair<string, string>(k, v));
                Set("ALWAYS_SEARCH_USER_PATHS", "NO");
                Set("CLANG_ENABLE_MODULES", "YES");
                Set("CODE_SIGN_ENTITLEMENTS", FolderName + "/" + EntitlementsFileName);
                Set("CODE_SIGN_STYLE", style);
                if (style == "Manual")
                {
                    foreach (string identity in new[] { "CODE_SIGN_IDENTITY", "CODE_SIGN_IDENTITY[sdk=iphoneos*]" })
                    {
                        string value = Setting(appCfgBlock, identity);
                        if (!string.IsNullOrEmpty(value)) Set(identity, value);
                    }
                }
                Set("CURRENT_PROJECT_VERSION", bld);
                Set("DEBUG_INFORMATION_FORMAT", debug ? "dwarf" : "dwarf-with-dsym");
                string team = Setting(appCfgBlock, "DEVELOPMENT_TEAM");
                if (!string.IsNullOrEmpty(team)) Set("DEVELOPMENT_TEAM", team);
                Set("GENERATE_INFOPLIST_FILE", "NO");
                Set("INFOPLIST_FILE", FolderName + "/" + InfoPlistFileName);
                Set("IPHONEOS_DEPLOYMENT_TARGET", IosCrashExtensionPolicy.DeploymentTarget);
                Set("LD_RUNPATH_SEARCH_PATHS", "$(inherited) @executable_path/Frameworks @executable_path/../../Frameworks");
                Set("MARKETING_VERSION", ver);
                Set("PRODUCT_BUNDLE_IDENTIFIER", ExtensionIdFor(appCfgBlock, spec));
                Set("PRODUCT_NAME", "$(TARGET_NAME)");
                string sdkroot = Setting(appCfgBlock, "SDKROOT");
                Set("SDKROOT", string.IsNullOrEmpty(sdkroot) ? "iphoneos" : sdkroot);
                Set("SKIP_INSTALL", "YES");
                string platforms = Setting(appCfgBlock, "SUPPORTED_PLATFORMS");
                if (!string.IsNullOrEmpty(platforms)) Set("SUPPORTED_PLATFORMS", platforms);
                Set("SWIFT_OPTIMIZATION_LEVEL", debug ? "-Onone" : "-O");
                Set("SWIFT_VERSION", "5.0");
                string family = Setting(appCfgBlock, "TARGETED_DEVICE_FAMILY");
                Set("TARGETED_DEVICE_FAMILY", string.IsNullOrEmpty(family) ? "1,2" : family);
                s.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

                configBlocks.Append($"\t\t{id} /* {cfgName} */ = {{\n\t\t\tisa = XCBuildConfiguration;\n\t\t\tbuildSettings = {{\n");
                foreach (var kv in s) configBlocks.Append($"\t\t\t\t{Q(kv.Key)} = {Q(kv.Value)};\n");
                configBlocks.Append($"\t\t\t}};\n\t\t\tname = {Q(cfgName)};\n\t\t}};\n");
            }

            string listComment = $"Build configuration list for PBXNativeTarget \"{name}\"";
            var configListEntry = new StringBuilder();
            configListEntry.Append($"\t\t{configList} /* {listComment} */ = {{\n\t\t\tisa = XCConfigurationList;\n\t\t\tbuildConfigurations = (\n");
            for (int i = 0; i < configIds.Count; i++) configListEntry.Append($"\t\t\t\t{configIds[i]},\n");
            configListEntry.Append($"\t\t\t);\n\t\t\tdefaultConfigurationIsVisible = 0;\n\t\t\tdefaultConfigurationName = {Q(defaultName)};\n\t\t}};\n");

            // ---- sections
            text = AddToSection(text, "PBXBuildFile",
                $"\t\t{swiftBuild} /* {SwiftFileName} in Sources */ = {{isa = PBXBuildFile; fileRef = {swiftRef} /* {SwiftFileName} */; }};\n" +
                $"\t\t{packageBuild} /* {IosCrashExtensionPolicy.SpmProduct} in Frameworks */ = {{isa = PBXBuildFile; productRef = {packageProduct} /* {IosCrashExtensionPolicy.SpmProduct} */; }};\n" +
                $"\t\t{embedBuild} /* {productFile} in {EmbedPhaseName} */ = {{isa = PBXBuildFile; fileRef = {product} /* {productFile} */; settings = {{ATTRIBUTES = (RemoveHeadersOnCopy, ); }}; }};\n");

            text = AddToSection(text, "PBXContainerItemProxy",
                $"\t\t{proxy} /* PBXContainerItemProxy */ = {{\n\t\t\tisa = PBXContainerItemProxy;\n\t\t\tcontainerPortal = {root} /* Project object */;\n" +
                $"\t\t\tproxyType = 1;\n\t\t\tremoteGlobalIDString = {target};\n\t\t\tremoteInfo = {name};\n\t\t}};\n");

            text = AddToSection(text, "PBXCopyFilesBuildPhase",
                $"\t\t{embed} /* {EmbedPhaseName} */ = {{\n\t\t\tisa = PBXCopyFilesBuildPhase;\n\t\t\tbuildActionMask = 2147483647;\n" +
                "\t\t\tdstPath = \"$(EXTENSIONS_FOLDER_PATH)\";\n\t\t\tdstSubfolderSpec = 16;\n" +
                $"\t\t\tfiles = (\n\t\t\t\t{embedBuild} /* {productFile} in {EmbedPhaseName} */,\n\t\t\t);\n" +
                $"\t\t\tname = {Q(EmbedPhaseName)};\n\t\t\trunOnlyForDeploymentPostprocessing = 0;\n\t\t}};\n");

            text = AddToSection(text, "PBXFileReference",
                $"\t\t{swiftRef} /* {SwiftFileName} */ = {{isa = PBXFileReference; lastKnownFileType = sourcecode.swift; path = {SwiftFileName}; sourceTree = \"<group>\"; }};\n" +
                $"\t\t{plistRef} /* {InfoPlistFileName} */ = {{isa = PBXFileReference; lastKnownFileType = text.plist.xml; path = {InfoPlistFileName}; sourceTree = \"<group>\"; }};\n" +
                $"\t\t{entRef} /* {EntitlementsFileName} */ = {{isa = PBXFileReference; lastKnownFileType = text.plist.entitlements; path = {EntitlementsFileName}; sourceTree = \"<group>\"; }};\n" +
                $"\t\t{product} /* {productFile} */ = {{isa = PBXFileReference; explicitFileType = \"wrapper.extensionkit-extension\"; includeInIndex = 0; path = {productFile}; sourceTree = BUILT_PRODUCTS_DIR; }};\n");

            text = AddToSection(text, "PBXFrameworksBuildPhase",
                $"\t\t{frameworks} /* Frameworks */ = {{\n\t\t\tisa = PBXFrameworksBuildPhase;\n\t\t\tbuildActionMask = 2147483647;\n" +
                $"\t\t\tfiles = (\n\t\t\t\t{packageBuild} /* {IosCrashExtensionPolicy.SpmProduct} in Frameworks */,\n\t\t\t);\n\t\t\trunOnlyForDeploymentPostprocessing = 0;\n\t\t}};\n");

            text = AddToSection(text, "PBXGroup",
                $"\t\t{group} /* {FolderName} */ = {{\n\t\t\tisa = PBXGroup;\n\t\t\tchildren = (\n" +
                $"\t\t\t\t{swiftRef} /* {SwiftFileName} */,\n\t\t\t\t{plistRef} /* {InfoPlistFileName} */,\n\t\t\t\t{entRef} /* {EntitlementsFileName} */,\n\t\t\t);\n" +
                $"\t\t\tpath = {FolderName};\n\t\t\tsourceTree = SOURCE_ROOT;\n\t\t}};\n");

            text = AddToSection(text, "PBXNativeTarget",
                $"\t\t{target} /* {name} */ = {{\n\t\t\tisa = PBXNativeTarget;\n" +
                $"\t\t\tbuildConfigurationList = {configList} /* {listComment} */;\n" +
                $"\t\t\tbuildPhases = (\n\t\t\t\t{sources} /* Sources */,\n\t\t\t\t{frameworks} /* Frameworks */,\n\t\t\t);\n" +
                "\t\t\tbuildRules = (\n\t\t\t);\n\t\t\tdependencies = (\n\t\t\t);\n" +
                $"\t\t\tname = {name};\n\t\t\tpackageProductDependencies = (\n\t\t\t\t{packageProduct} /* {IosCrashExtensionPolicy.SpmProduct} */,\n\t\t\t);\n" +
                $"\t\t\tproductName = {name};\n\t\t\tproductReference = {product} /* {productFile} */;\n" +
                $"\t\t\tproductType = \"{IosCrashExtensionPolicy.ProductType}\";\n\t\t}};\n");

            text = AddToSection(text, "PBXSourcesBuildPhase",
                $"\t\t{sources} /* Sources */ = {{\n\t\t\tisa = PBXSourcesBuildPhase;\n\t\t\tbuildActionMask = 2147483647;\n" +
                $"\t\t\tfiles = (\n\t\t\t\t{swiftBuild} /* {SwiftFileName} in Sources */,\n\t\t\t);\n\t\t\trunOnlyForDeploymentPostprocessing = 0;\n\t\t}};\n");

            text = AddToSection(text, "PBXTargetDependency",
                $"\t\t{dependency} /* PBXTargetDependency */ = {{\n\t\t\tisa = PBXTargetDependency;\n\t\t\ttarget = {target} /* {name} */;\n\t\t\ttargetProxy = {proxy} /* PBXContainerItemProxy */;\n\t\t}};\n");

            text = AddToSection(text, "XCBuildConfiguration", configBlocks.ToString());
            text = AddToSection(text, "XCConfigurationList", configListEntry.ToString());
            text = AddToSection(text, "XCSwiftPackageProductDependency",
                $"\t\t{packageProduct} /* {IosCrashExtensionPolicy.SpmProduct} */ = {{\n\t\t\tisa = XCSwiftPackageProductDependency;\n{packageLine}\t\t\tproductName = {IosCrashExtensionPolicy.SpmProduct};\n\t\t}};\n");

            // ---- existing objects that now refer to the new ones
            text = AppendToList(text, root, "targets", $"{target} /* {name} */");
            string mainGroup = Match1(Object(text, root), @"mainGroup = (" + Guid + ")", "mainGroup");
            text = AppendToList(text, mainGroup, "children", $"{group} /* {FolderName} */");
            Match products = Regex.Match(Object(text, root), @"productRefGroup = (" + Guid + ")");
            if (products.Success) text = AppendToList(text, products.Groups[1].Value, "children", $"{product} /* {productFile} */");
            text = AppendToList(text, appTarget, "buildPhases", $"{embed} /* {EmbedPhaseName} */");
            text = AppendToList(text, appTarget, "dependencies", $"{dependency} /* PBXTargetDependency */");

            if (spec.AppEntitlementsPathToSet != null)
            {
                foreach (string appConfig in appConfigs)
                {
                    text = AddSetting(text, appConfig, "CODE_SIGN_ENTITLEMENTS", spec.AppEntitlementsPathToSet);
                }
            }

            return text;
        }

        /// <summary>
        /// The extension's bundle id for one build configuration: the app's id in the configuration of the same name plus the extension
        /// suffix, so a Release archive of an app with its own Release id gets an extension under that id (App Store validation requires
        /// the extension id to be prefixed by the app's). A configuration that sets no id, or one with a build variable, falls back to
        /// <see cref="XcodeCrashExtensionSpec.AppBundleId"/>. The App Group stays derived from that one id: a group is not tied to a
        /// bundle id, so the same group serves every configuration.
        /// </summary>
        static string ExtensionIdFor(string appConfigBlock, XcodeCrashExtensionSpec spec)
        {
            string id = Setting(appConfigBlock, "PRODUCT_BUNDLE_IDENTIFIER");
            if (string.IsNullOrWhiteSpace(id) || id.Contains("$")) id = spec.AppBundleId;
            if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("no app bundle id");
            return IosCrashExtensionPolicy.ExtensionBundleId(id);
        }

        /// <summary>The app's signing style: its CODE_SIGN_STYLE, else Unity's ProvisioningStyle attribute for the target, else Automatic.</summary>
        static string AppSigningStyle(string text, string appTarget, List<string> appConfigs)
        {
            foreach (string cfg in appConfigs)
            {
                string v = Setting(Object(text, cfg), "CODE_SIGN_STYLE");
                if (v == "Manual" || v == "Automatic") return v;
            }
            Match attr = Regex.Match(text, appTarget + @"(?: /\*[^\n]*?\*/)? = \{[^}]*?ProvisioningStyle = (\w+);", Options | RegexOptions.Singleline);
            return attr.Success && attr.Groups[1].Value == "Manual" ? "Manual" : "Automatic";
        }

        static string ExtensionTargetGuid(string text)
        {
            Match m = Regex.Match(text, @"^\t\t(" + Guid + @")(?: /\*[^\n]*?\*/)? = \{\n\t\t\tisa = PBXNativeTarget;[^}]*?name = " + IosCrashExtensionPolicy.TargetName + ";",
                RegexOptions.Multiline | RegexOptions.Singleline | Options);
            if (!m.Success) throw new InvalidOperationException("extension target not found");
            return m.Groups[1].Value;
        }

        /// <summary>The settings that follow the app, rewritten on every configuration of the extension (the target itself is kept).</summary>
        static string Refresh(string text, XcodeCrashExtensionSpec spec)
        {
            string appTarget = spec.AppTargetGuid ?? throw new InvalidOperationException("no app target id");
            string target = ExtensionTargetGuid(text);
            var appConfigs = ConfigGuids(text, appTarget);
            if (appConfigs.Count == 0) throw new InvalidOperationException("the app target has no build configurations");
            string style = AppSigningStyle(text, appTarget, appConfigs);
            var appByName = new Dictionary<string, string>();
            foreach (string cfg in appConfigs) appByName[Unquote(Match1(Object(text, cfg), @"\bname = ([^;]+);", "a build configuration name"))] = cfg;

            foreach (string cfg in ConfigGuids(text, target))
            {
                string block = Object(text, cfg);
                string cfgName = Unquote(Match1(block, @"\bname = ([^;]+);", "a build configuration name"));
                string appCfg = appByName.TryGetValue(cfgName, out string byName) ? byName : appConfigs[0];
                string appBlock = Object(text, appCfg);
                bool debug = cfgName.IndexOf("Debug", StringComparison.OrdinalIgnoreCase) >= 0;
                text = SetSetting(text, cfg, "PRODUCT_BUNDLE_IDENTIFIER", ExtensionIdFor(appBlock, spec));
                text = SetSetting(text, cfg, "MARKETING_VERSION", spec.Version);
                text = SetSetting(text, cfg, "CURRENT_PROJECT_VERSION", spec.Build);
                text = SetSetting(text, cfg, "DEVELOPMENT_TEAM", Setting(appBlock, "DEVELOPMENT_TEAM"));
                text = SetSetting(text, cfg, "CODE_SIGN_STYLE", style);
                text = SetSetting(text, cfg, "DEBUG_INFORMATION_FORMAT", debug ? "dwarf" : "dwarf-with-dsym");
                foreach (string identity in new[] { "CODE_SIGN_IDENTITY", "CODE_SIGN_IDENTITY[sdk=iphoneos*]" })
                    text = SetSetting(text, cfg, identity, style == "Manual" ? Setting(appBlock, identity) : null);
            }

            if (spec.AppEntitlementsPathToSet != null)
            {
                foreach (string appConfig in appConfigs) text = AddSetting(text, appConfig, "CODE_SIGN_ENTITLEMENTS", spec.AppEntitlementsPathToSet);
            }
            return text;
        }

        /// <summary>
        /// Takes the extension out: the target with its configurations, phases, build files, product, package product dependency,
        /// source group and files, and the app's embed phase entry and target dependency. The app's entitlements setting and the
        /// shared package stay (the caller removes the App Group from the entitlements file). Text without the target comes back as is.
        /// </summary>
        public static string Remove(string pbxproj, out string error)
        {
            error = null;
            if (pbxproj == null) { error = "no project text"; return null; }
            if (!Has(pbxproj)) return pbxproj;
            try { return Strip(pbxproj); }
            catch (InvalidOperationException e) { error = e.Message; return null; }
        }

        static string Strip(string text)
        {
            string target = ExtensionTargetGuid(text);
            string block = Object(text, target);
            var gone = new List<string> { target };
            string configList = Match1(block, @"buildConfigurationList = (" + Guid + ")", "buildConfigurationList");
            foreach (string cfg in ConfigGuids(text, target)) gone.Add(cfg);
            gone.Add(configList);

            string product = Match1(block, @"productReference = (" + Guid + ")", "productReference");
            gone.Add(product);
            foreach (Match m in Regex.Matches(ListBody(block, "packageProductDependencies"), Guid)) gone.Add(m.Value);
            foreach (Match phase in Regex.Matches(ListBody(block, "buildPhases"), Guid))
            {
                gone.Add(phase.Value);
                foreach (Match file in Regex.Matches(ListBody(Object(text, phase.Value), "files"), Guid)) gone.Add(file.Value);
            }

            // The build file that embeds the product, and the app's embed phase (whole, when it held nothing else).
            Match embedFile = Regex.Match(text, @"^\t\t(" + Guid + @")(?: /\*[^\n]*?\*/)? = \{isa = PBXBuildFile; fileRef = " + product, RegexOptions.Multiline | Options);
            if (embedFile.Success)
            {
                string embedId = embedFile.Groups[1].Value;
                gone.Add(embedId);
                foreach (Match phase in Regex.Matches(text, @"^\t\t(" + Guid + @")(?: /\*[^\n]*?\*/)? = \{\n\t\t\tisa = PBXCopyFilesBuildPhase;.*?^\t\t\};\n", RegexOptions.Multiline | RegexOptions.Singleline | Options))
                {
                    if (!phase.Value.Contains(embedId)) continue;
                    var others = Regex.Matches(ListBody(phase.Value, "files"), Guid).Cast<Match>().Where(f => f.Value != embedId).Count();
                    if (others == 0) gone.Add(phase.Groups[1].Value);
                }
            }

            // The app's dependency on the target, and its proxy.
            foreach (Match dep in Regex.Matches(text, @"^\t\t(" + Guid + @")(?: /\*[^\n]*?\*/)? = \{\n\t\t\tisa = PBXTargetDependency;.*?^\t\t\};\n", RegexOptions.Multiline | RegexOptions.Singleline | Options))
            {
                if (!Regex.IsMatch(dep.Value, "target = " + target + @"\b")) continue;
                gone.Add(dep.Groups[1].Value);
                Match proxy = Regex.Match(dep.Value, @"targetProxy = (" + Guid + ")");
                if (proxy.Success) gone.Add(proxy.Groups[1].Value);
            }

            // The source group and the files in it.
            foreach (Match group in Regex.Matches(text, @"^\t\t(" + Guid + @")(?: /\*[^\n]*?\*/)? = \{\n\t\t\tisa = PBXGroup;.*?^\t\t\};\n", RegexOptions.Multiline | RegexOptions.Singleline | Options))
            {
                if (!Regex.IsMatch(group.Value, @"^\t\t\tpath = " + FolderName + ";", RegexOptions.Multiline)) continue;
                gone.Add(group.Groups[1].Value);
                foreach (Match child in Regex.Matches(ListBody(group.Value, "children"), Guid)) gone.Add(child.Value);
            }

            foreach (string id in gone)
            {
                if (FindObject(text, id, out int s, out int e)) text = text.Remove(s, e - s);
                text = Regex.Replace(text, @"^[ \t]*" + id + @"(?: /\*[^\n]*?\*/)?,\r?\n", "", RegexOptions.Multiline | Options);
            }
            // Sections the removal emptied (the add creates the ones a project lacked).
            return Regex.Replace(text, @"/\* Begin (\w+) section \*/\r?\n/\* End \1 section \*/\r?\n(?:\r?\n)?", "", Options);
        }

        static string ListBody(string block, string list)
        {
            Match m = Regex.Match(block, @"^\t\t\t" + list + @" = \((.*?)^\t\t\t\);", RegexOptions.Multiline | RegexOptions.Singleline);
            return m.Success ? m.Groups[1].Value : "";
        }

        /// <summary>Sets (replaces or adds) a build setting of a configuration; a null value removes it.</summary>
        static string SetSetting(string text, string configGuid, string name, string value)
        {
            if (!FindObject(text, configGuid, out int s, out int e)) throw new InvalidOperationException("config " + configGuid + " not found");
            string block = text.Substring(s, e - s);
            Match m = Regex.Match(block, @"^\t\t\t\t""?" + Regex.Escape(name) + @"""? = [^;\n]*;\r?\n", RegexOptions.Multiline | Options);
            string line = value == null ? "" : $"\t\t\t\t{Q(name)} = {Q(value)};\n";
            if (m.Success) block = block.Substring(0, m.Index) + line + block.Substring(m.Index + m.Length);
            else if (value != null)
            {
                int at = block.IndexOf("buildSettings = {\n", StringComparison.Ordinal);
                if (at < 0) throw new InvalidOperationException("config without buildSettings");
                at += "buildSettings = {\n".Length;
                block = block.Substring(0, at) + line + block.Substring(at);
            }
            return text.Substring(0, s) + block + text.Substring(e);
        }

        // ------------------------------------------------------------------ pbxproj helpers

        /// <summary>Hands out ids that are not in the project: stable for the same project text and role.</summary>
        sealed class Ids
        {
            readonly string _text;
            readonly HashSet<string> _used = new HashSet<string>();
            public Ids(string text) { _text = text; }

            public string New(string role)
            {
                for (int salt = 0; ; salt++)
                {
                    string id = Hash(IosCrashExtensionPolicy.TargetName + ":" + role + ":" + salt);
                    if (_text.IndexOf(id, StringComparison.Ordinal) < 0 && _used.Add(id)) return id;
                }
            }

            static string Hash(string s)
            {
                ulong a = 14695981039346656037UL, b = 1099511628211UL ^ 0x9E3779B97F4A7C15UL;
                foreach (char c in s)
                {
                    a = (a ^ c) * 1099511628211UL;
                    b = (b ^ c) * 14029467366897019727UL + 0x632BE59BD9B4E019UL;
                }
                return (a.ToString("X16") + b.ToString("X16")).Substring(0, 24);
            }
        }

        static string Match1(string text, string pattern, string what)
        {
            Match m = Regex.Match(text, pattern, Options);
            if (!m.Success) throw new InvalidOperationException("missing " + what);
            return m.Groups[m.Groups.Count - 1].Value;
        }

        static bool FindObject(string text, string guid, out int start, out int end)
        {
            start = end = 0;
            Match m = Regex.Match(text, @"^\t\t" + guid + @"\b[^\n]*= \{[^\n]*\n", RegexOptions.Multiline | Options);
            if (!m.Success) return false;
            start = m.Index;
            if (m.Value.TrimEnd().EndsWith("};", StringComparison.Ordinal)) { end = m.Index + m.Length; return true; }
            int close = text.IndexOf("\n\t\t};\n", m.Index + m.Length - 1, StringComparison.Ordinal);
            if (close < 0) return false;
            end = close + "\n\t\t};\n".Length;
            return true;
        }

        static string Object(string text, string guid)
        {
            if (!FindObject(text, guid, out int s, out int e)) throw new InvalidOperationException("object " + guid + " not found");
            return text.Substring(s, e - s);
        }

        static List<string> ConfigGuids(string text, string targetGuid)
        {
            string list = Match1(Object(text, targetGuid), @"buildConfigurationList = (" + Guid + ")", "buildConfigurationList");
            string block = Object(text, list);
            var result = new List<string>();
            Match section = Regex.Match(block, @"buildConfigurations = \((.*?)\);", Options | RegexOptions.Singleline);
            if (!section.Success) return result;
            foreach (Match m in Regex.Matches(section.Groups[1].Value, Guid)) result.Add(m.Value);
            return result;
        }

        static string Setting(string block, string name)
        {
            Match m = Regex.Match(block, @"^\t\t\t\t""?" + Regex.Escape(name) + @"""? = (?<v>[^;\n]*);\s*$", RegexOptions.Multiline | Options);
            return m.Success ? Unquote(m.Groups["v"].Value) : null;
        }

        static string AddSetting(string text, string configGuid, string name, string value)
        {
            if (!FindObject(text, configGuid, out int s, out int e)) throw new InvalidOperationException("config " + configGuid + " not found");
            string block = text.Substring(s, e - s);
            if (Setting(block, name) != null) return text;
            int at = block.IndexOf("buildSettings = {\n", StringComparison.Ordinal);
            if (at < 0) throw new InvalidOperationException("config without buildSettings");
            at += "buildSettings = {\n".Length;
            return text.Substring(0, s) + block.Substring(0, at) + $"\t\t\t\t{Q(name)} = {Q(value)};\n" + block.Substring(at) + text.Substring(e);
        }

        static string AppendToList(string text, string objectGuid, string list, string entry)
        {
            if (!FindObject(text, objectGuid, out int s, out int e)) throw new InvalidOperationException("object " + objectGuid + " not found");
            string block = text.Substring(s, e - s);
            Match open = Regex.Match(block, @"^\t\t\t" + list + @" = \(\s*\n", RegexOptions.Multiline | Options);
            string item = "\t\t\t\t" + entry + ",\n";
            if (open.Success)
            {
                Match close = Regex.Match(block.Substring(open.Index + open.Length), @"^\t\t\t\);", RegexOptions.Multiline);
                if (!close.Success) throw new InvalidOperationException(list + " list is not closed");
                int at = open.Index + open.Length + close.Index;
                block = block.Substring(0, at) + item + block.Substring(at);
            }
            else
            {
                Match isa = Regex.Match(block, @"^\t\t\tisa = [^;]+;\n", RegexOptions.Multiline);
                if (!isa.Success) throw new InvalidOperationException("object without isa");
                int at = isa.Index + isa.Length;
                block = block.Substring(0, at) + $"\t\t\t{list} = (\n{item}\t\t\t);\n" + block.Substring(at);
            }
            return text.Substring(0, s) + block + text.Substring(e);
        }

        static string AddToSection(string text, string section, string entries)
        {
            string end = "/* End " + section + " section */";
            int at = text.IndexOf(end, StringComparison.Ordinal);
            if (at >= 0) return text.Insert(at, entries);
            string begin = "/* Begin PBXProject section */";
            int project = text.IndexOf(begin, StringComparison.Ordinal);
            if (project < 0) throw new InvalidOperationException("no PBXProject section");
            return text.Insert(project, "/* Begin " + section + " section */\n" + entries + end + "\n\n");
        }

        static string Q(string v) =>
            Regex.IsMatch(v, @"^[A-Za-z0-9_./]+$") ? v : "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        static string Unquote(string v)
        {
            v = v.Trim();
            return v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"' ? v.Substring(1, v.Length - 2).Replace("\\\"", "\"").Replace("\\\\", "\\") : v;
        }
    }
}
