using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class EdmSettingPolicyTests
    {
        const string Key = "Google.IOSResolver.AllowEmptyPodfileGeneration";

        [Test]
        public void A_setting_present_in_the_document_is_configured()
        {
            var xml = "<projectSettings><projectSetting name=\"" + Key + "\" value=\"True\" /></projectSettings>";
            Assert.That(EdmSettingPolicy.IsConfigured(xml, Key), Is.True);
        }

        [Test]
        public void A_setting_configured_to_false_still_counts_as_chosen()
        {
            var xml = "<projectSettings><projectSetting name='" + Key + "' value='False' /></projectSettings>";
            Assert.That(EdmSettingPolicy.IsConfigured(xml, Key), Is.True);
        }

        [Test]
        public void Other_settings_do_not_count()
        {
            var xml = "<projectSettings><projectSetting name=\"Google.IOSResolver.PodfileEnabled\" value=\"True\" /></projectSettings>";
            Assert.That(EdmSettingPolicy.IsConfigured(xml, Key), Is.False);
        }

        [Test]
        public void A_key_that_only_prefixes_another_name_does_not_count()
        {
            var xml = "<projectSetting name=\"" + Key + "Extra\" value=\"True\" />";
            Assert.That(EdmSettingPolicy.IsConfigured(xml, Key), Is.False);
        }

        // EDM4U writes every setting to EditorPrefs too, so another project on the machine leaves the key there;
        // with project settings in use (the default) EDM4U ignores it and falls back to its default (true).
        [Test]
        public void An_editor_prefs_entry_does_not_count_while_project_settings_are_used()
        {
            Assert.That(EdmSettingPolicy.IsConfigured(null, Key, useProjectSettings: true, editorPrefsHasKey: true), Is.False);
            var xml = "<projectSetting name=\"" + Key + "\" value=\"True\" />";
            Assert.That(EdmSettingPolicy.IsConfigured(xml, Key, useProjectSettings: true, editorPrefsHasKey: false), Is.True);
        }

        [Test]
        public void Only_editor_prefs_count_when_project_settings_are_off()
        {
            var xml = "<projectSetting name=\"" + Key + "\" value=\"True\" />";
            Assert.That(EdmSettingPolicy.IsConfigured(xml, Key, useProjectSettings: false, editorPrefsHasKey: false), Is.False);
            Assert.That(EdmSettingPolicy.IsConfigured(null, Key, useProjectSettings: false, editorPrefsHasKey: true), Is.True);
        }

        [Test]
        public void The_editor_hook_asks_the_store_edm4u_reads()
        {
            var dir = new System.IO.DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "package.json"))) dir = dir.Parent;
            Assert.That(dir, Is.Not.Null, "repo root (package.json) not found");
            var source = System.IO.File.ReadAllText(System.IO.Path.Combine(dir.FullName, "Editor", "BugseeIosEmptyPodfile.cs"));
            Assert.That(source, Does.Contain("EdmSettingPolicy.IsConfigured(xml, SettingKey, useProjectSettings, EditorPrefs.HasKey(SettingKey))"));
            Assert.That(source, Does.Contain("\"Google.IOSResolver.UseProjectSettings\""));
            Assert.That(source, Does.Not.Contain("|| EditorPrefs.HasKey(SettingKey)"));
        }

        [Test]
        public void A_missing_document_means_nothing_is_configured()
        {
            Assert.That(EdmSettingPolicy.IsConfigured(null, Key), Is.False);
            Assert.That(EdmSettingPolicy.IsConfigured("", Key), Is.False);
            Assert.That(EdmSettingPolicy.IsConfigured("<x/>", null), Is.False);
        }
    }
}
