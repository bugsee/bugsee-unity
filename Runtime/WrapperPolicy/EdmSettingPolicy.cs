using System;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Decides whether an External Dependency Manager setting was chosen by the project. The package may only
    /// change a setting nobody configured: EDM4U's own default stays for a project that set the key itself.
    /// </summary>
    public static class EdmSettingPolicy
    {
        /// <summary>
        /// True when the project settings document (ProjectSettings/GvhProjectSettings.xml) has an entry for
        /// <paramref name="key"/>. A missing or unreadable document means nothing is configured.
        /// </summary>
        public static bool IsConfigured(string settingsXml, string key)
        {
            if (string.IsNullOrEmpty(settingsXml) || string.IsNullOrEmpty(key)) return false;
            return settingsXml.IndexOf("name=\"" + key + "\"", StringComparison.Ordinal) >= 0
                || settingsXml.IndexOf("name='" + key + "'", StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// True when the key is set in the store EDM4U reads it from. EDM4U writes a setting to both the project
        /// document and the user's EditorPrefs, but reads only the project document while its "use project
        /// settings" option is on (the default), so an EditorPrefs entry, possibly left by another project, does
        /// not count then. With the option off it reads only EditorPrefs.
        /// </summary>
        public static bool IsConfigured(string settingsXml, string key, bool useProjectSettings, bool editorPrefsHasKey)
        {
            return useProjectSettings ? IsConfigured(settingsXml, key) : editorPrefsHasKey;
        }
    }
}
