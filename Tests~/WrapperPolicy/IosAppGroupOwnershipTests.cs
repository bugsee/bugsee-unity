using System.Linq;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    // The export adds an App Group to the app's entitlements for the Crash Report Extension. It records which files it added the
    // group to (in the extension's own Info.plist) and removes a group only from those: a group the developer already had stays.
    public class IosAppGroupOwnershipTests
    {
        const string Head = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n<plist version=\"1.0\">\n";
        const string A = "group.com.example.game";
        const string B = "group.com.example.renamed";
        const string File1 = "Unity-iPhone.entitlements";
        const string File2 = "App/Release.entitlements";

        static string Plist(string body) => Head + "<dict>\n" + body + "</dict>\n</plist>\n";
        static string Groups(params string[] groups) =>
            "\t<key>com.apple.security.application-groups</key>\n\t<array>\n" + string.Concat(groups.Select(g => "\t\t<string>" + g + "</string>\n")) + "\t</array>\n";
        const string Aps = "\t<key>aps-environment</key>\n\t<string>development</string>\n";

        static IosCrashExtensionPolicy.AppGroupState State(string group, params string[] owned)
        {
            var s = new IosCrashExtensionPolicy.AppGroupState { Group = group };
            s.OwnedFiles.AddRange(owned);
            return s;
        }

        // ---- a developer's own group

        [Test]
        public void A_group_the_developer_already_had_is_kept_and_is_not_ours()
        {
            string theirs = Plist(Aps + Groups(A));
            var first = IosCrashExtensionPolicy.RewriteEntitlements(theirs, File1, A, null);
            Assert.That(first.Text, Is.EqualTo(theirs), "unchanged");
            Assert.That(first.Owned, Is.False);

            // The marker an export writes has no entry for it, so every later export leaves it alone.
            string infoPlist = IosCrashExtensionPolicy.InfoPlist(A, "G", Enumerable.Empty<string>());
            var previous = IosCrashExtensionPolicy.ReadAppGroupState(infoPlist);
            Assert.That(previous.Group, Is.EqualTo(A));
            Assert.That(previous.OwnedFiles, Is.Empty);
            Assert.That(IosCrashExtensionPolicy.RetireEntitlements(theirs, File1, previous), Is.EqualTo(theirs), "Off");
            Assert.That(IosCrashExtensionPolicy.RewriteEntitlements(theirs, File1, A, previous).Text, Is.EqualTo(theirs), "Append");
            var renamed = IosCrashExtensionPolicy.RewriteEntitlements(theirs, File1, B, previous);
            Assert.That(IosCrashExtensionPolicy.EntitlementsHaveGroup(renamed.Text, A), Is.True, "bundle id change keeps theirs");
            Assert.That(IosCrashExtensionPolicy.EntitlementsHaveGroup(renamed.Text, B), Is.True);
            Assert.That(renamed.Owned, Is.True, "only the new group is ours");
        }

        [Test]
        public void A_project_exported_by_an_older_package_has_no_marker_so_nothing_is_removed()
        {
            const string old = "<plist><dict><key>BugseeAppGroup</key><string>group.com.example.game</string></dict></plist>";
            var previous = IosCrashExtensionPolicy.ReadAppGroupState(old);
            Assert.That(previous.OwnedFiles, Is.Empty);
            string text = Plist(Groups(A));
            Assert.That(IosCrashExtensionPolicy.RetireEntitlements(text, File1, previous), Is.EqualTo(text));
        }

        // ---- a group we added

        [Test]
        public void A_group_we_added_is_recorded_and_removed_on_retire_and_on_a_rename()
        {
            string plain = Plist(Aps);
            var added = IosCrashExtensionPolicy.RewriteEntitlements(plain, File1, A, null);
            Assert.That(added.Owned, Is.True);
            Assert.That(IosCrashExtensionPolicy.EntitlementsHaveGroup(added.Text, A), Is.True);

            var previous = IosCrashExtensionPolicy.ReadAppGroupState(IosCrashExtensionPolicy.InfoPlist(A, "G", new[] { File1 }));
            Assert.That(previous.OwnedFiles, Is.EqualTo(new[] { File1 }));

            string off = IosCrashExtensionPolicy.RetireEntitlements(added.Text, File1, previous);
            Assert.That(off, Does.Not.Contain("application-groups"));
            Assert.That(off, Does.Contain("aps-environment"), "other keys stay");

            var renamed = IosCrashExtensionPolicy.RewriteEntitlements(added.Text, File1, B, previous);
            Assert.That(renamed.Text, Does.Not.Contain(A));
            Assert.That(IosCrashExtensionPolicy.EntitlementsHaveGroup(renamed.Text, B), Is.True);
            Assert.That(renamed.Owned, Is.True);
        }

        [Test]
        public void A_new_file_is_ours_and_a_second_export_with_the_same_group_keeps_it_ours()
        {
            var first = IosCrashExtensionPolicy.RewriteEntitlements(null, File1, A, null);
            Assert.That(first.Owned, Is.True);
            var previous = State(A, File1);
            var second = IosCrashExtensionPolicy.RewriteEntitlements(first.Text, File1, A, previous);
            Assert.That(second.Text, Is.EqualTo(first.Text), "idempotent");
            Assert.That(second.Owned, Is.True, "still ours: the marker survives Append");
            var third = IosCrashExtensionPolicy.RewriteEntitlements(second.Text, File1, A, previous);
            Assert.That(third.Text, Is.EqualTo(first.Text));
        }

        [Test]
        public void Other_groups_and_keys_stay_when_ours_goes_and_an_emptied_array_takes_its_key_along()
        {
            string both = Plist(Aps + Groups("group.widget", A));
            string without = IosCrashExtensionPolicy.RetireEntitlements(both, File1, State(A, File1));
            Assert.That(without, Is.EqualTo(Plist(Aps + Groups("group.widget"))), "formatting stable");
            string only = IosCrashExtensionPolicy.RetireEntitlements(Plist(Aps + Groups(A)), File1, State(A, File1));
            Assert.That(only, Is.EqualTo(Plist(Aps)));
            Assert.That(IosCrashExtensionPolicy.RetireEntitlements(Plist(Groups(A)), File1, State(A, File1)), Is.EqualTo(Plist("")));
        }

        // ---- RemoveGroupFromEntitlements scoping

        [Test]
        public void The_group_is_removed_only_from_the_application_groups_array()
        {
            string elsewhere = Plist(
                "\t<key>com.apple.developer.associated-domains</key>\n\t<array>\n\t\t<string>" + A + "</string>\n\t</array>\n" + Groups("group.other"));
            Assert.That(IosCrashExtensionPolicy.RemoveGroupFromEntitlements(elsewhere, A), Is.EqualTo(elsewhere), "same string under another key");

            string both = Plist(
                "\t<key>com.apple.developer.associated-domains</key>\n\t<array>\n\t\t<string>" + A + "</string>\n\t</array>\n" + Groups(A, "group.other"));
            string result = IosCrashExtensionPolicy.RemoveGroupFromEntitlements(both, A);
            Assert.That(result, Is.EqualTo(Plist(
                "\t<key>com.apple.developer.associated-domains</key>\n\t<array>\n\t\t<string>" + A + "</string>\n\t</array>\n" + Groups("group.other"))));

            Assert.That(IosCrashExtensionPolicy.EntitlementsHaveGroup(elsewhere, A), Is.False);
            string asValue = Plist("\t<key>x</key>\n\t<string>" + A + "</string>\n");
            Assert.That(IosCrashExtensionPolicy.RemoveGroupFromEntitlements(asValue, A), Is.EqualTo(asValue));
        }

        [Test]
        public void A_group_that_only_shares_a_prefix_with_ours_is_not_matched()
        {
            string text = Plist(Groups(A + ".widget"));
            Assert.That(IosCrashExtensionPolicy.RemoveGroupFromEntitlements(text, A), Is.EqualTo(text));
            Assert.That(IosCrashExtensionPolicy.EntitlementsHaveGroup(text, A), Is.False);
        }

        // ---- malformed or missing files

        [Test]
        public void Malformed_missing_and_empty_input_never_throws()
        {
            Assert.That(IosCrashExtensionPolicy.RewriteEntitlements("not a plist", File1, A, null).Text, Is.Null);
            Assert.That(IosCrashExtensionPolicy.RewriteEntitlements("not a plist", File1, A, State(A, File1)).Text, Is.Null);
            Assert.That(IosCrashExtensionPolicy.RewriteEntitlements("", File1, A, null).Owned, Is.True);
            Assert.That(IosCrashExtensionPolicy.RetireEntitlements(null, File1, State(A, File1)), Is.Null);
            Assert.That(IosCrashExtensionPolicy.RetireEntitlements("not a plist", File1, State(A, File1)), Is.EqualTo("not a plist"));
            Assert.That(IosCrashExtensionPolicy.RetireEntitlements("x", File1, null), Is.EqualTo("x"));
            Assert.That(IosCrashExtensionPolicy.RemoveGroupFromEntitlements(null, A), Is.Null);
            Assert.That(IosCrashExtensionPolicy.RemoveGroupFromEntitlements("<key>com.apple.security.application-groups</key><array>", A), Is.EqualTo("<key>com.apple.security.application-groups</key><array>"));
            Assert.That(IosCrashExtensionPolicy.ReadAppGroupState(null), Is.Null);
            Assert.That(IosCrashExtensionPolicy.ReadAppGroupState("garbage"), Is.Null);
            Assert.That(IosCrashExtensionPolicy.RewriteEntitlements(Plist(Aps), File1, A, State(null)).Owned, Is.True);
        }

        // ---- several configurations / paths

        [Test]
        public void Ownership_is_kept_per_file()
        {
            string mine = Plist(Aps);              // we add the group here
            string theirs = Plist(Groups(A));      // the developer already had it here
            var r1 = IosCrashExtensionPolicy.RewriteEntitlements(mine, File1, A, null);
            var r2 = IosCrashExtensionPolicy.RewriteEntitlements(theirs, File2, A, null);
            var owned = new[] { (File1, r1), (File2, r2) }.Where(x => x.Item2.Owned).Select(x => x.Item1).ToArray();
            Assert.That(owned, Is.EqualTo(new[] { File1 }));

            var previous = IosCrashExtensionPolicy.ReadAppGroupState(IosCrashExtensionPolicy.InfoPlist(A, "G", owned));
            Assert.That(IosCrashExtensionPolicy.RetireEntitlements(r1.Text, File1, previous), Does.Not.Contain("application-groups"));
            Assert.That(IosCrashExtensionPolicy.RetireEntitlements(r2.Text, File2, previous), Is.EqualTo(theirs));
        }

        [Test]
        public void The_marker_round_trips_paths_with_special_characters_and_is_only_written_when_something_is_owned()
        {
            string odd = "My App/A&B <x>.entitlements";
            var state = IosCrashExtensionPolicy.ReadAppGroupState(IosCrashExtensionPolicy.InfoPlist(A, "G", new[] { odd, File2 }));
            Assert.That(state.OwnedFiles, Is.EqualTo(new[] { odd, File2 }));
            Assert.That(IosCrashExtensionPolicy.InfoPlist(A, "G"), Does.Not.Contain(IosCrashExtensionPolicy.OwnedFilesKey));
            Assert.That(IosCrashExtensionPolicy.InfoPlist(A, "G", new[] { File1 }), Does.Contain("<key>BugseeAppGroupAddedTo</key>"));
            // The extension point and group are intact next to the marker.
            string plist = IosCrashExtensionPolicy.InfoPlist(A, "G", new[] { File1 });
            Assert.That(plist, Does.Contain("<key>BugseeAppGroup</key>\n\t<string>" + A + "</string>"));
            Assert.That(plist, Does.Contain("com.apple.crash-reporter.extension"));
        }
    }
}
