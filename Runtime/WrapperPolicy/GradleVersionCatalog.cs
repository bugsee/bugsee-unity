using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Reads the Android Gradle plugin version through a Gradle version catalog (<c>gradle/libs.versions.toml</c>)
    /// when the root build file names the plugin by alias instead of pinning a literal version. Reads what the
    /// documented catalog syntax writes on one line per entry; anything else resolves to null.
    /// </summary>
    public static class GradleVersionCatalog
    {
        const string ApplicationId = "com.android.application";
        const string LibraryId = "com.android.library";
        const string ClasspathModule = "com.android.tools.build:gradle";

        static readonly Regex AliasUse = new Regex(
            @"alias\s*\(\s*(?<cat>[A-Za-z_]\w*)\.plugins\.(?<k>[A-Za-z0-9_.]+?)\s*\)", RegexOptions.CultureInvariant);

        static readonly Regex VersionUse = new Regex(
            @"id\s*\(?\s*['""](?:com\.android\.application|com\.android\.library)['""]\s*\)?\s*version\s*\(?\s*(?<cat>[A-Za-z_]\w*)\.versions\.(?<k>[A-Za-z0-9_.]+)",
            RegexOptions.CultureInvariant);

        static readonly Regex ClasspathUse = new Regex(
            @"classpath\s*\(?\s*(?<cat>[A-Za-z_]\w*)\.(?<k>[A-Za-z][A-Za-z0-9_.]*)", RegexOptions.CultureInvariant);

        /// <summary>The AGP version <paramref name="build"/> resolves to through <paramref name="catalog"/>, or null.</summary>
        public static string ResolveAgpVersion(string build, string catalog)
        {
            if (build == null || string.IsNullOrEmpty(catalog))
            {
                return null;
            }

            var sections = Parse(catalog);
            Dictionary<string, string> versions = Section(sections, "versions");
            Dictionary<string, string> plugins = Section(sections, "plugins");
            Dictionary<string, string> libraries = Section(sections, "libraries");

            foreach (Match m in VersionUse.Matches(build))
            {
                string key = TrimAccessorSuffix(m.Groups["k"].Value);
                string value = Lookup(versions, key);
                string version = value == null ? null : FirstString(value);
                if (version != null)
                {
                    return version;
                }
            }

            foreach (Match m in AliasUse.Matches(build))
            {
                string entry = Lookup(plugins, TrimAccessorSuffix(m.Groups["k"].Value));
                string id = entry == null ? null : Field(entry, "id");
                if (id == ApplicationId || id == LibraryId)
                {
                    string version = EntryVersion(entry, versions);
                    if (version != null)
                    {
                        return version;
                    }
                }
            }

            foreach (Match m in ClasspathUse.Matches(build))
            {
                string entry = Lookup(libraries, TrimAccessorSuffix(m.Groups["k"].Value));
                if (entry == null)
                {
                    continue;
                }

                string module = Field(entry, "module");
                if (module == null && Field(entry, "group") != null && Field(entry, "name") != null)
                {
                    module = Field(entry, "group") + ":" + Field(entry, "name");
                }

                if (module == ClasspathModule)
                {
                    string version = EntryVersion(entry, versions);
                    if (version != null)
                    {
                        return version;
                    }
                }
            }

            return null;
        }

        static string TrimAccessorSuffix(string key)
        {
            foreach (string suffix in new[] { ".get", ".asProvider", ".get()" })
            {
                if (key.EndsWith(suffix, StringComparison.Ordinal))
                {
                    return key.Substring(0, key.Length - suffix.Length);
                }
            }

            return key;
        }

        // The accessor spells a catalog key with dots where the key has '-', '_' or '.'.
        static string Normalize(string key)
        {
            return key.Replace('-', '.').Replace('_', '.');
        }

        static string Lookup(Dictionary<string, string> section, string accessorKey)
        {
            string wanted = Normalize(accessorKey);
            foreach (KeyValuePair<string, string> pair in section)
            {
                if (Normalize(pair.Key) == wanted)
                {
                    return pair.Value;
                }
            }

            return null;
        }

        static string EntryVersion(string entry, Dictionary<string, string> versions)
        {
            Match reference = Regex.Match(entry, @"version\s*\.\s*ref\s*=\s*""(?<r>[^""]+)""|version\s*=\s*\{\s*ref\s*=\s*""(?<r>[^""]+)""");
            if (reference.Success)
            {
                string value = Lookup(versions, reference.Groups["r"].Value);
                return value == null ? null : FirstString(value);
            }

            return Field(entry, "version");
        }

        static string Field(string entry, string name)
        {
            Match m = Regex.Match(entry, @"(?<![\w.])" + name + @"\s*=\s*""(?<v>[^""]*)""");
            return m.Success ? m.Groups["v"].Value : null;
        }

        // `"8.7.2"`, or `{ strictly = "8.7.2" }` / `{ require = ... }` / `{ prefer = ... }`: the first quoted string.
        static string FirstString(string value)
        {
            Match m = Regex.Match(value, @"""(?<v>[^""]*)""");
            return m.Success ? m.Groups["v"].Value : null;
        }

        static Dictionary<string, string> Section(Dictionary<string, Dictionary<string, string>> sections, string name)
        {
            return sections.TryGetValue(name, out var section) ? section : new Dictionary<string, string>();
        }

        static Dictionary<string, Dictionary<string, string>> Parse(string toml)
        {
            var sections = new Dictionary<string, Dictionary<string, string>>();
            Dictionary<string, string> current = null;
            foreach (string raw in toml.Replace("\r\n", "\n").Split('\n'))
            {
                string line = StripComment(raw).Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    string name = line.Substring(1, line.Length - 2).Trim();
                    current = new Dictionary<string, string>();
                    sections[name] = current;
                    continue;
                }

                int eq = line.IndexOf('=');
                if (current == null || eq <= 0)
                {
                    continue;
                }

                string key = line.Substring(0, eq).Trim().Trim('"');
                current[key] = line.Substring(eq + 1).Trim();
            }

            return sections;
        }

        static string StripComment(string line)
        {
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '"')
                {
                    quoted = !quoted;
                }
                else if (line[i] == '#' && !quoted)
                {
                    return line.Substring(0, i);
                }
            }

            return line;
        }
    }
}
