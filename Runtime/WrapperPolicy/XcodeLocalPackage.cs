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

        /// <returns>The project text with the reference to <paramref name="repositoryUrl"/> now pointing at
        /// <paramref name="relativePath"/>, or null when the project has no such remote reference.</returns>
        public static string FromRemote(string pbxproj, string repositoryUrl, string relativePath)
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

            string localSection =
                "/* Begin XCLocalSwiftPackageReference section */\n" +
                $"\t\t{guid} /* {comment} */ = {{\n" +
                "\t\t\tisa = XCLocalSwiftPackageReference;\n" +
                $"\t\t\trelativePath = {relativePath};\n" +
                "\t\t};\n" +
                "/* End XCLocalSwiftPackageReference section */\n";

            string remoteBegin = "/* Begin " + RemoteSection + " section */\n";
            string remoteEnd = "/* End " + RemoteSection + " section */\n";
            int begin = text.IndexOf(remoteBegin, System.StringComparison.Ordinal);
            if (begin < 0)
            {
                return null;
            }

            int end = text.IndexOf(remoteEnd, begin, System.StringComparison.Ordinal);
            if (end < 0)
            {
                return null;
            }

            bool otherRemotes = text.IndexOf("isa = " + RemoteSection + ";", begin, end - begin, System.StringComparison.Ordinal) >= 0;
            if (otherRemotes)
            {
                return text.Insert(begin, localSection);
            }

            // Nothing remote is left: the local section takes the remote section's place.
            return text.Substring(0, begin) + localSection + text.Substring(end + remoteEnd.Length);
        }

        static string ReferenceName(string pbxproj, string guid)
        {
            Match match = Regex.Match(pbxproj, guid + @" /\* " + RemoteSection + @" ""([^""]*)"" \*/", RegexOptions.CultureInvariant);
            return match.Success ? match.Groups[1].Value : "";
        }
    }
}
