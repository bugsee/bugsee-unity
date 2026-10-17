using System.Text.RegularExpressions;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Unity's PBXProject API writes remote Swift package references only. This turns the one it wrote for a
    /// repository into a local path reference (XCLocalSwiftPackageReference), leaving everything the link needs
    /// (the project's package list, the targets' product dependencies and Frameworks entries) as Unity wrote it.
    /// </summary>
    public static class XcodeLocalPackage
    {
        const string RemoteSection = "XCRemoteSwiftPackageReference";

        /// <summary>
        /// Removes every link to the Bugsee package a project already has (remote references to <paramref name="repositoryUrl"/>,
        /// local references to <paramref name="relativePath"/>, their product dependencies, the "Bugsee in Frameworks"
        /// build files and the list entries pointing at them), so an export into an existing project adds it once.
        /// </summary>
        public static string RemoveBugseePackage(string pbxproj, string repositoryUrl, string relativePath)
        {
            var guids = new System.Collections.Generic.List<string>();
            string text = RemoveBlocks(pbxproj, new Regex(
                @"[ \t]*(?<guid>[0-9A-F]{24}) /\* " + RemoteSection + @" ""[^""]*"" \*/ = \{\s*isa = " + RemoteSection +
                @";\s*repositoryURL = " + Regex.Escape("\"" + repositoryUrl + "\"") + @";\s*requirement = \{[^}]*\};\s*\};\r?\n",
                RegexOptions.CultureInvariant), guids);
            text = RemoveBlocks(text, new Regex(
                @"[ \t]*(?<guid>[0-9A-F]{24}) /\* XCLocalSwiftPackageReference ""[^""]*"" \*/ = \{\s*isa = XCLocalSwiftPackageReference;\s*relativePath = " +
                Regex.Escape(relativePath) + @";\s*\};\r?\n", RegexOptions.CultureInvariant), guids);
            var products = new System.Collections.Generic.List<string>();
            text = RemoveBlocks(text, new Regex(
                @"[ \t]*(?<guid>[0-9A-F]{24}) /\* Bugsee \*/ = \{\s*isa = XCSwiftPackageProductDependency;\s*(?:package = [^;]*;\s*)?productName = Bugsee;\s*\};\r?\n",
                RegexOptions.CultureInvariant), products);
            guids.AddRange(products);
            foreach (string product in products)
            {
                text = RemoveBlocks(text, new Regex(
                    @"[ \t]*(?<guid>[0-9A-F]{24}) /\* Bugsee in Frameworks \*/ = \{isa = PBXBuildFile; productRef = " + product + @" /\* Bugsee \*/; \};\r?\n",
                    RegexOptions.CultureInvariant), guids);
            }

            foreach (string guid in guids)
            {
                text = Regex.Replace(text, @"^[ \t]*" + guid + @" /\*[^\n]*\*/,\r?\n", "", RegexOptions.Multiline | RegexOptions.CultureInvariant);
            }

            foreach (string section in new[] { RemoteSection, "XCLocalSwiftPackageReference", "XCSwiftPackageProductDependency" })
            {
                text = Regex.Replace(text, @"/\* Begin " + section + @" section \*/\r?\n/\* End " + section + @" section \*/\r?\n", "", RegexOptions.CultureInvariant);
            }

            return text;
        }

        static string RemoveBlocks(string text, Regex block, System.Collections.Generic.List<string> removedGuids)
        {
            foreach (Match match in block.Matches(text))
            {
                removedGuids.Add(match.Groups["guid"].Value);
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
            var block = new Regex(
                @"[ \t]*(?<guid>[0-9A-F]{24}) /\* " + RemoteSection + @" ""[^""]*"" \*/ = \{\s*isa = " + RemoteSection +
                @";\s*repositoryURL = " + Regex.Escape("\"" + repositoryUrl + "\"") + @";\s*requirement = \{[^}]*\};\s*\};\r?\n",
                RegexOptions.CultureInvariant);
            Match match = block.Match(pbxproj);
            if (!match.Success)
            {
                return null;
            }

            string guid = match.Groups["guid"].Value;
            string comment = $"XCLocalSwiftPackageReference \"{relativePath}\"";
            string text = pbxproj.Remove(match.Index, match.Length);

            // The product dependencies of a local package name no package, as Xcode writes them.
            text = Regex.Replace(text, @"^[ \t]*package = " + guid + @" /\*[^*]*\*/;\r?\n", "", RegexOptions.Multiline | RegexOptions.CultureInvariant);
            text = text.Replace(guid + " /* " + RemoteSection + " \"" + ReferenceName(pbxproj, guid) + "\" */", guid + " /* " + comment + " */");

            string entry =
                $"\t\t{guid} /* {comment} */ = {{\n" +
                "\t\t\tisa = XCLocalSwiftPackageReference;\n" +
                $"\t\t\trelativePath = {relativePath};\n" +
                "\t\t};\n";

            const string localEnd = "/* End XCLocalSwiftPackageReference section */\n";
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

            string localSection = "/* Begin XCLocalSwiftPackageReference section */\n" + entry + localEnd;
            bool otherRemotes = text.IndexOf("isa = " + RemoteSection + ";", begin, end - begin, System.StringComparison.Ordinal) >= 0;
            if (otherRemotes)
            {
                return existingLocal >= 0 ? text : text.Insert(begin, localSection);
            }

            // Nothing remote is left: the local section takes the remote section's place.
            return text.Substring(0, begin) + (existingLocal >= 0 ? "" : localSection) + text.Substring(end + remoteEnd.Length);
        }

        static string ReferenceName(string pbxproj, string guid)
        {
            Match match = Regex.Match(pbxproj, guid + @" /\* " + RemoteSection + @" ""([^""]*)"" \*/", RegexOptions.CultureInvariant);
            return match.Success ? match.Groups[1].Value : "";
        }
    }
}
