using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Unity's PBXProject API writes remote Swift package references only. This turns the one it wrote for a
    /// repository into a local path reference (XCLocalSwiftPackageReference), leaving everything the link needs
    /// (the project's package list, the targets' product dependencies and Frameworks entries) as Unity wrote it.
    /// It also removes what an earlier export linked, so an export into an existing project links the package once.
    ///
    /// Unity rewrites the project (every PBXProject.WriteToFile) without the trailing comments Xcode writes after
    /// object ids for package references, so every pattern here accepts an id with or without its comment.
    /// </summary>
    public static class XcodeLocalPackage
    {
        const string RemoteSection = "XCRemoteSwiftPackageReference";
        const string LocalSection = "XCLocalSwiftPackageReference";
        const string Guid = "[0-9A-F]{24}";
        const string Comment = @"(?: /\*[^\n]*?\*/)?";
        const RegexOptions Options = RegexOptions.CultureInvariant;

        static Regex RemoteBlock(string repositoryUrl)
        {
            return new Regex(
                @"[ \t]*(?<guid>" + Guid + ")" + Comment + @" = \{\s*isa = " + RemoteSection + @";\s*repositoryURL = " +
                Regex.Escape("\"" + repositoryUrl + "\"") + @";\s*requirement = \{[^}]*\};\s*\};\r?\n", Options);
        }

        static Regex LocalBlock(string relativePath)
        {
            return new Regex(
                @"[ \t]*(?<guid>" + Guid + ")" + Comment + @" = \{\s*isa = " + LocalSection + @";\s*relativePath = " +
                Regex.Escape(relativePath) + @";\s*\};\r?\n", Options);
        }

        const string ProductBlock =
            @"[ \t]*(?<guid>" + Guid + ")" + Comment + @" = \{\s*isa = XCSwiftPackageProductDependency;\s*" +
            @"(?:package = (?<package>" + Guid + ")" + Comment + @";\s*)?productName = (?<name>[^;\s]+);\s*\};\r?\n";

        /// <summary>
        /// Removes the Bugsee package links a project already has: remote references to <paramref name="repositoryUrl"/>,
        /// local references to <paramref name="relativePath"/>, the product dependencies that belong to them, the build
        /// files that use those products and the list entries pointing at any of it. A package of the user's own
        /// (another URL, a fork) is left alone, whatever products it provides.
        /// </summary>
        public static string RemoveBugseePackage(string pbxproj, string repositoryUrl, string relativePath)
        {
            return RemoveBugseePackage(pbxproj, repositoryUrl, relativePath, out _);
        }

        /// <param name="removedPackageIds">The ids of the package references that were removed, for <see cref="RepointProducts"/>.</param>
        public static string RemoveBugseePackage(string pbxproj, string repositoryUrl, string relativePath, out ISet<string> removedPackageIds)
        {
            return RemoveBugseePackage(pbxproj, repositoryUrl, relativePath, "Bugsee", out removedPackageIds);
        }

        /// <summary>
        /// Removes the links to another package of ours, such as the Feedback add-on (<paramref name="productName"/>
        /// <c>BugseeFeedback</c>): only remote references to <paramref name="repositoryUrl"/> exist for it, never a local one.
        /// </summary>
        public static string RemovePackageProduct(string pbxproj, string repositoryUrl, string productName)
        {
            return RemoveBugseePackage(pbxproj, repositoryUrl, null, productName, out _);
        }

        static string RemoveBugseePackage(string pbxproj, string repositoryUrl, string relativePath, string productName, out ISet<string> removedPackageIds)
        {
            var references = new HashSet<string>();
            removedPackageIds = references;
            var localReferences = new HashSet<string>();
            string text = RemoveBlocks(pbxproj, RemoteBlock(repositoryUrl), references, null);
            if (relativePath != null) text = RemoveBlocks(text, LocalBlock(relativePath), references, localReferences);

            // A remote package's products name it in `package`; a local one's name no package, so only the product
            // Bugsee (what the export links) is taken for ours, and only when a local reference of ours was removed.
            var products = new HashSet<string>();
            text = new Regex(ProductBlock, Options).Replace(text, match =>
            {
                string package = match.Groups["package"].Value;
                // Only the Bugsee product is the export's own. Another product of the same package (BugseeSwiftUI,
                // added in Xcode) is the user's: it stays, with its build file, and RepointProducts moves it to the new reference.
                bool ours = match.Groups["name"].Value == productName &&
                    (package.Length > 0 ? references.Contains(package) : localReferences.Count > 0);
                if (!ours)
                {
                    // The package's other product has no package key when the package was local; give it the removed
                    // reference's id so RepointProducts can move it to whichever reference the export adds.
                    if (package.Length == 0 && localReferences.Count > 0 && match.Groups["name"].Value == "BugseeSwiftUI")
                    {
                        string keyed = match.Value.Replace("productName = ", "package = " + First(localReferences) + ";\n\t\t\tproductName = ");
                        return keyed;
                    }

                    return match.Value;
                }

                products.Add(match.Groups["guid"].Value);
                return "";
            });

            var removed = new HashSet<string>(references);
            removed.UnionWith(products);
            foreach (string product in products)
            {
                text = new Regex(
                    @"[ \t]*(?<guid>" + Guid + ")" + Comment + @" = \{isa = PBXBuildFile; productRef = " + product + Comment +
                    @"; \};\r?\n", Options).Replace(text, match =>
                {
                    removed.Add(match.Groups["guid"].Value);
                    return "";
                });
            }

            foreach (string guid in removed)
            {
                text = Regex.Replace(text, @"^[ \t]*" + guid + Comment + @",\r?\n", "", RegexOptions.Multiline | Options);
            }

            foreach (string section in new[] { RemoteSection, LocalSection, "XCSwiftPackageProductDependency" })
            {
                text = Regex.Replace(text, @"/\* Begin " + section + @" section \*/\r?\n(?:\r?\n)?/\* End " + section + @" section \*/\r?\n(?:\r?\n)?", "", Options);
            }

            return text;
        }

        /// <summary>
        /// Points the product dependencies of a package <see cref="RemoveBugseePackage"/> removed (the user's other products
        /// of it, listed by <paramref name="removedPackageIds"/>; any other product is left alone) at <paramref name="packageId"/>, the reference just added.
        /// </summary>
        public static string RepointProducts(string pbxproj, string packageId, ISet<string> removedPackageIds)
        {
            return new Regex(ProductBlock, Options).Replace(pbxproj, match =>
            {
                string package = match.Groups["package"].Value;
                if (package.Length == 0 || package == packageId || !removedPackageIds.Contains(package))
                {
                    return match.Value;
                }

                Group group = match.Groups["package"];
                return match.Value.Substring(0, group.Index - match.Index) + packageId + match.Value.Substring(group.Index - match.Index + group.Length);
            });
        }

        static string First(HashSet<string> set)
        {
            foreach (string item in set) return item;
            return "";
        }

        static string RemoveBlocks(string text, Regex block, HashSet<string> removedGuids, HashSet<string> alsoRecorded)
        {
            foreach (Match match in block.Matches(text))
            {
                removedGuids.Add(match.Groups["guid"].Value);
                alsoRecorded?.Add(match.Groups["guid"].Value);
            }

            return block.Replace(text, "");
        }

        /// <returns>The project text with every remote reference to <paramref name="repositoryUrl"/> now pointing at
        /// <paramref name="relativePath"/>, or null when the project has no such remote reference.</returns>
        public static string FromRemote(string pbxproj, string repositoryUrl, string relativePath)
        {
            string text = pbxproj;
            bool any = false;
            string converted;
            while ((converted = ConvertFirst(text, repositoryUrl, relativePath)) != null)
            {
                text = converted;
                any = true;
            }

            return any ? text : null;
        }

        static string ConvertFirst(string pbxproj, string repositoryUrl, string relativePath)
        {
            Match match = RemoteBlock(repositoryUrl).Match(pbxproj);
            if (!match.Success)
            {
                return null;
            }

            string guid = match.Groups["guid"].Value;
            string comment = $"{LocalSection} \"{relativePath}\"";
            string text = pbxproj.Remove(match.Index, match.Length);

            // The product dependencies of a local package name no package, as Xcode writes them.
            text = Regex.Replace(text, @"^[ \t]*package = " + guid + Comment + @";\r?\n", "", RegexOptions.Multiline | Options);
            // The package list entry now names the local reference.
            text = Regex.Replace(text, @"^([ \t]*)" + guid + Comment + @",", "${1}" + guid + " /* " + comment + " */,", RegexOptions.Multiline | Options);

            string entry =
                $"\t\t{guid} /* {comment} */ = {{\n" +
                $"\t\t\tisa = {LocalSection};\n" +
                $"\t\t\trelativePath = {relativePath};\n" +
                "\t\t};\n";

            string localEnd = "/* End " + LocalSection + " section */\n";
            int existingLocal = text.IndexOf(localEnd, System.StringComparison.Ordinal);
            if (existingLocal >= 0)
            {
                text = text.Insert(existingLocal, entry);
            }

            string remoteBegin = "/* Begin " + RemoteSection + " section */\n";
            string remoteEnd = "/* End " + RemoteSection + " section */\n";
            int begin = text.IndexOf(remoteBegin, System.StringComparison.Ordinal);
            int end = begin < 0 ? -1 : text.IndexOf(remoteEnd, begin, System.StringComparison.Ordinal);
            if (end < 0)
            {
                return null;
            }

            string localSection = "/* Begin " + LocalSection + " section */\n" + entry + localEnd;
            bool otherRemotes = text.IndexOf("isa = " + RemoteSection + ";", begin, end - begin, System.StringComparison.Ordinal) >= 0;
            if (otherRemotes)
            {
                return existingLocal >= 0 ? text : text.Insert(begin, localSection);
            }

            // Nothing remote is left: the local section takes the remote section's place.
            return text.Substring(0, begin) + (existingLocal >= 0 ? "" : localSection) + text.Substring(end + remoteEnd.Length);
        }
    }
}
