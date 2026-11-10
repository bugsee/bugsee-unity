using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class IosEarlyLaunchPolicyTests
    {
        const string Token = "11111111-2222-3333-4444-555555555555";

        const string Plist =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
            "<plist version=\"1.0\">\n" +
            "<dict>\n" +
            "\t<key>CFBundleName</key>\n" +
            "\t<string>Game</string>\n" +
            "\t<key>UIRequiredDeviceCapabilities</key>\n" +
            "\t<array>\n" +
            "\t\t<string>arm64</string>\n" +
            "\t</array>\n" +
            "</dict>\n" +
            "</plist>\n";

        static IosEarlyLaunchInputs On(System.Action<IosEarlyLaunchInputs> tweak = null)
        {
            var i = new IosEarlyLaunchInputs
            {
                Mode = IosEarlyLaunchMode.On,
                Token = Token,
                WrapperVersion = "0.1.0",
                WrapperBuild = "dev",
                UnityVersion = "2022.3.62f3",
                ProductName = "My Game",
            };
            tweak?.Invoke(i);
            return i;
        }

        // ---------------------------------------------------------------- mode and decision

        [TestCase("1", IosEarlyLaunchMode.On)]
        [TestCase("on", IosEarlyLaunchMode.On)]
        [TestCase(" TRUE ", IosEarlyLaunchMode.On)]
        [TestCase("yes", IosEarlyLaunchMode.On)]
        [TestCase("0", IosEarlyLaunchMode.Off)]
        [TestCase("off", IosEarlyLaunchMode.Off)]
        [TestCase("", IosEarlyLaunchMode.Off)]
        [TestCase(null, IosEarlyLaunchMode.Off)]
        [TestCase("auto", IosEarlyLaunchMode.Off)]
        [TestCase("onn", IosEarlyLaunchMode.Off)]
        public void ParseMode_only_explicit_yes_turns_it_on(string text, IosEarlyLaunchMode expected) =>
            Assert.That(IosEarlyLaunchPolicy.ParseMode(text), Is.EqualTo(expected));

        [Test]
        public void Default_is_off_and_adds_nothing()
        {
            Assert.That(IosEarlyLaunchPolicy.Decide(new IosEarlyLaunchInputs()).Enabled, Is.False);
            Assert.That(IosEarlyLaunchPolicy.Decide(null).Enabled, Is.False);
            var off = IosEarlyLaunchPolicy.Decide(On(i => i.Mode = IosEarlyLaunchMode.Off));
            Assert.That(off.Enabled, Is.False);
            Assert.That(off.IsWarning, Is.False);
            Assert.That(off.Entries, Is.Null);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void On_without_a_token_is_not_added_and_warns(string token)
        {
            var d = IosEarlyLaunchPolicy.Decide(On(i => i.Token = token));
            Assert.That(d.Enabled, Is.False);
            Assert.That(d.IsWarning, Is.True);
            Assert.That(d.Reason, Does.Contain("BUGSEE_IOS_APP_TOKEN"));
        }

        [TestCase("two words")]
        [TestCase("tab\there")]
        [TestCase("line\nbreak")]
        [TestCase("a<b")]
        [TestCase("a&b")]
        [TestCase("a\"b")]
        public void A_token_that_is_not_a_single_printable_token_is_refused(string token)
        {
            var d = IosEarlyLaunchPolicy.Decide(On(i => i.Token = token));
            Assert.That(d.Enabled, Is.False);
            Assert.That(d.IsWarning, Is.True);
        }

        [Test]
        public void An_overlong_token_is_refused() =>
            Assert.That(IosEarlyLaunchPolicy.Decide(On(i => i.Token = new string('a', IosEarlyLaunchPolicy.MaxTokenLength + 1))).Enabled, Is.False);

        [Test]
        public void The_token_is_trimmed_and_the_entries_carry_the_wrapper_identity()
        {
            var d = IosEarlyLaunchPolicy.Decide(On(i => i.Token = "  " + Token + "\n"));
            Assert.That(d.Enabled, Is.True, d.Reason);
            Assert.That(d.IsWarning, Is.False);
            Assert.That(d.Entries[IosEarlyLaunchPolicy.TokenKey], Is.EqualTo(Token));
            Assert.That(d.Entries[IosEarlyLaunchPolicy.WrapperVersionKey], Is.EqualTo("0.1.0"));
            Assert.That(d.Entries[IosEarlyLaunchPolicy.WrapperBuildKey], Is.EqualTo("dev"));
            Assert.That(d.Entries[IosEarlyLaunchPolicy.UnityVersionKey], Is.EqualTo("2022.3.62f3"));
            Assert.That(d.Entries[IosEarlyLaunchPolicy.ProductNameKey], Is.EqualTo("My Game"));
        }

        // ---------------------------------------------------------------- option subset

        [Test]
        public void Without_options_the_early_options_are_only_the_wrapper_info()
        {
            var json = IosEarlyLaunchPolicy.Decide(On()).Entries[IosEarlyLaunchPolicy.OptionsKey];
            var map = JsonRead.Object(json);
            Assert.That(map.Keys, Is.EquivalentTo(new[] { "wrapper_info" }));
            var info = (Dictionary<string, object>)map["wrapper_info"];
            Assert.That(info["type"], Is.EqualTo("UNITY"));
            Assert.That(info["version"], Is.EqualTo("0.1.0"));
            Assert.That(info["build"], Is.EqualTo("dev"));
            Assert.That(info["metal"], Is.EqualTo(true));
            Assert.That(info["fps"], Is.EqualTo((long)IosEarlyLaunchPolicy.DefaultFrameRate));
        }

        [Test]
        public void The_options_subset_is_kept_and_android_only_keys_are_dropped()
        {
            var d = IosEarlyLaunchPolicy.Decide(On(i => i.OptionsJson =
                "{\"com.bugsee.option.detect.hang\":true,\"com.bugsee.option.capture.video.mode\":21,\"com.bugsee.option.detect.exit.low_memory\":false,\"x\":\"quote\\\" and \\\\ and é\"}"));
            Assert.That(d.Enabled, Is.True, d.Reason);
            var map = JsonRead.Object(d.Entries[IosEarlyLaunchPolicy.OptionsKey]);
            Assert.That(map["com.bugsee.option.detect.hang"], Is.EqualTo(true));
            Assert.That(map["x"], Is.EqualTo("quote\" and \\ and é"));
            Assert.That(map.ContainsKey("com.bugsee.option.capture.video.mode"), Is.False, "VideoMode is Android-only on the wire");
            Assert.That(map.ContainsKey("com.bugsee.option.detect.exit.low_memory"), Is.False);
            Assert.That(map.ContainsKey("wrapper_info"), Is.True);
        }

        [Test]
        public void A_usable_wrapper_info_of_the_caller_is_kept_and_a_broken_one_is_replaced()
        {
            var kept = JsonRead.Object(IosEarlyLaunchPolicy.Decide(On(i => i.OptionsJson = "{\"wrapper_info\":{\"type\":\"CUSTOM\"}}")).Entries[IosEarlyLaunchPolicy.OptionsKey]);
            Assert.That(((Dictionary<string, object>)kept["wrapper_info"])["type"], Is.EqualTo("CUSTOM"));
            var replaced = JsonRead.Object(IosEarlyLaunchPolicy.Decide(On(i => i.OptionsJson = "{\"wrapper_info\":\"x\"}")).Entries[IosEarlyLaunchPolicy.OptionsKey]);
            Assert.That(((Dictionary<string, object>)replaced["wrapper_info"])["type"], Is.EqualTo("UNITY"));
        }

        [TestCase("[1,2]")]
        [TestCase("not json")]
        [TestCase("{\"a\":")]
        [TestCase("\"text\"")]
        public void Options_that_are_not_a_json_object_are_ignored_with_a_warning_and_the_launch_still_added(string options)
        {
            var d = IosEarlyLaunchPolicy.Decide(On(i => i.OptionsJson = options));
            Assert.That(d.Enabled, Is.True);
            Assert.That(d.IsWarning, Is.True);
            Assert.That(JsonRead.Object(d.Entries[IosEarlyLaunchPolicy.OptionsKey]).Keys, Is.EquivalentTo(new[] { "wrapper_info" }));
        }

        [Test]
        public void Overlong_options_are_ignored_with_a_warning()
        {
            var d = IosEarlyLaunchPolicy.Decide(On(i => i.OptionsJson = "{\"k\":\"" + new string('a', IosEarlyLaunchPolicy.MaxOptionsLength) + "\"}"));
            Assert.That(d.Enabled, Is.True);
            Assert.That(d.IsWarning, Is.True);
            Assert.That(d.Entries[IosEarlyLaunchPolicy.OptionsKey].Length, Is.LessThan(500));
        }

        [Test]
        public void Metal_false_is_passed_through() =>
            Assert.That(((Dictionary<string, object>)JsonRead.Object(IosEarlyLaunchPolicy.Decide(On(i => i.Metal = false)).Entries[IosEarlyLaunchPolicy.OptionsKey])["wrapper_info"])["metal"], Is.EqualTo(false));

        [Test]
        public void The_decision_is_deterministic()
        {
            var a = IosEarlyLaunchPolicy.Decide(On(i => i.OptionsJson = "{\"b\":1,\"a\":2}")).Entries[IosEarlyLaunchPolicy.OptionsKey];
            var b = IosEarlyLaunchPolicy.Decide(On(i => i.OptionsJson = "{\"b\":1,\"a\":2}")).Entries[IosEarlyLaunchPolicy.OptionsKey];
            Assert.That(a, Is.EqualTo(b));
        }

        // ---------------------------------------------------------------- reconciliation

        [Test]
        public void DifferingKeys_lists_what_the_early_session_does_not_have_sorted()
        {
            const string early = "{\"a\":true,\"n\":5,\"wrapper_info\":{\"type\":\"UNITY\"}}";
            var requested = new Dictionary<string, object> { ["n"] = 5, ["a"] = true, ["z"] = 1, ["m"] = "x", ["wrapper_info"] = new Dictionary<string, object> { ["type"] = "OTHER" } };
            Assert.That(IosEarlyLaunchPolicy.DifferingKeys(requested, early), Is.EqualTo(new[] { "m", "z" }));
        }

        [Test]
        public void DifferingKeys_ignores_the_internal_dollar_dollar_keys_behind_the_option_prefix()
        {
            var requested = new Dictionary<string, object>
            {
                ["com.bugsee.option.$$ENDPOINT"] = "https://x/v2",
                ["com.bugsee.option.$$DEBUG"] = true,
                ["$$other"] = 1,
                ["m"] = 1,
            };
            Assert.That(IosEarlyLaunchPolicy.DifferingKeys(requested, "{}"), Is.EqualTo(new[] { "m" }));
        }

        [Test]
        public void DifferingKeys_compares_values_as_the_wire_sees_them()
        {
            const string early = "{\"i\":2,\"f\":1.5,\"s\":\"x\",\"b\":false}";
            var same = new Dictionary<string, object> { ["i"] = 2, ["f"] = 1.5f, ["s"] = "x", ["b"] = false };
            Assert.That(IosEarlyLaunchPolicy.DifferingKeys(same, early), Is.Empty);
            var other = new Dictionary<string, object> { ["i"] = 3, ["b"] = true };
            Assert.That(IosEarlyLaunchPolicy.DifferingKeys(other, early), Is.EqualTo(new[] { "b", "i" }));
        }

        [Test]
        public void Options_in_the_plist_have_no_dollar_sign_for_xcode_to_expand_and_decode_back_to_the_same_keys()
        {
            // Xcode turns "$$" in an Info.plist string into "$", which renamed com.bugsee.option.$$ENDPOINT to $ENDPOINT.
            const string endpoint = "com.bugsee.option.$$ENDPOINT";
            var d = IosEarlyLaunchPolicy.Decide(On(i => i.OptionsJson = "{\"" + endpoint + "\":\"https://apidev.bugsee.com/v2\",\"x\":\"$(PRODUCT_NAME)\"}"));
            Assert.That(d.Enabled, Is.True);
            string stored = d.Entries[IosEarlyLaunchPolicy.OptionsKey];
            Assert.That(stored, Does.Not.Contain("$"));
            var read = JsonRead.Object(stored);
            Assert.That(read[endpoint], Is.EqualTo("https://apidev.bugsee.com/v2"));
            Assert.That(read["x"], Is.EqualTo("$(PRODUCT_NAME)"));
            // The adoption check reads the same stored text, so the same options do not differ.
            var requested = new Dictionary<string, object> { [endpoint] = "https://apidev.bugsee.com/v2" };
            Assert.That(IosEarlyLaunchPolicy.DifferingKeys(requested, stored), Is.Empty);
        }

        [Test]
        public void PlistSafeJson_is_null_safe_and_leaves_text_without_a_dollar_alone()
        {
            Assert.That(IosEarlyLaunchPolicy.PlistSafeJson(null), Is.Null);
            Assert.That(IosEarlyLaunchPolicy.PlistSafeJson("{\"a\":1}"), Is.EqualTo("{\"a\":1}"));
        }

        [Test]
        public void DifferingKeys_with_nothing_requested_or_nothing_known_is_safe()
        {
            Assert.That(IosEarlyLaunchPolicy.DifferingKeys(null, "{}"), Is.Empty);
            Assert.That(IosEarlyLaunchPolicy.DifferingKeys(new Dictionary<string, object>(), null), Is.Empty);
            Assert.That(IosEarlyLaunchPolicy.DifferingKeys(new Dictionary<string, object> { ["k"] = 1 }, null), Is.EqualTo(new[] { "k" }));
            Assert.That(IosEarlyLaunchPolicy.DifferingKeys(new Dictionary<string, object> { ["$$INTERNAL"] = 1 }, "{}"), Is.Empty);
        }

        [Test]
        public void AdoptionWarning_is_silent_when_nothing_differs_and_names_what_does()
        {
            Assert.That(IosEarlyLaunchPolicy.AdoptionWarning(IosEarlyAdoption.Adopt, new string[0]), Is.Null);
            Assert.That(IosEarlyLaunchPolicy.AdoptionWarning(IosEarlyAdoption.Adopt, null), Is.Null);
            var keys = IosEarlyLaunchPolicy.AdoptionWarning(IosEarlyAdoption.Adopt, new[] { "k1", "k2" });
            Assert.That(keys, Does.Contain("k1, k2").And.Contain("launch-only"));
            var token = IosEarlyLaunchPolicy.AdoptionWarning(IosEarlyAdoption.AdoptTokenDiffers, null);
            Assert.That(token, Does.Contain("token").And.Contain("build-time"));
            Assert.That(token, Does.Not.Contain(Token));
        }

        // ---------------------------------------------------------------- Info.plist text

        [Test]
        public void SetInfoPlistEntry_adds_the_dictionary_before_the_closing_dict_and_keeps_the_rest()
        {
            var entries = IosEarlyLaunchPolicy.Decide(On()).Entries;
            string result = IosEarlyLaunchPolicy.SetInfoPlistEntry(Plist, entries);
            Assert.That(result, Does.StartWith(Plist.Substring(0, Plist.LastIndexOf("</dict>"))));
            Assert.That(result, Does.EndWith("\t</dict>\n</dict>\n</plist>\n"));
            Assert.That(result, Does.Contain("<key>BugseeEarlyLaunch</key>"));
            Assert.That(result, Does.Contain("<key>Token</key>\n\t\t<string>" + Token + "</string>"));
            Assert.That(IosEarlyLaunchPolicy.HasInfoPlistEntry(result), Is.True);
        }

        [Test]
        public void SetInfoPlistEntry_is_idempotent_and_replaces_an_earlier_entry()
        {
            var entries = IosEarlyLaunchPolicy.Decide(On()).Entries;
            string once = IosEarlyLaunchPolicy.SetInfoPlistEntry(Plist, entries);
            Assert.That(IosEarlyLaunchPolicy.SetInfoPlistEntry(once, entries), Is.EqualTo(once));
            var other = IosEarlyLaunchPolicy.Decide(On(i => i.Token = "other-token")).Entries;
            string replaced = IosEarlyLaunchPolicy.SetInfoPlistEntry(once, other);
            Assert.That(replaced, Does.Contain("other-token").And.Not.Contain(Token));
            Assert.That(CountOf(replaced, "<key>BugseeEarlyLaunch</key>"), Is.EqualTo(1));
        }

        [Test]
        public void RemoveInfoPlistEntry_restores_the_original_text_byte_for_byte()
        {
            var entries = IosEarlyLaunchPolicy.Decide(On(i => i.OptionsJson = "{\"a\":1}")).Entries;
            string with = IosEarlyLaunchPolicy.SetInfoPlistEntry(Plist, entries);
            Assert.That(IosEarlyLaunchPolicy.RemoveInfoPlistEntry(with), Is.EqualTo(Plist));
            Assert.That(IosEarlyLaunchPolicy.HasInfoPlistEntry(IosEarlyLaunchPolicy.RemoveInfoPlistEntry(with)), Is.False);
        }

        [Test]
        public void Without_an_entry_remove_returns_the_same_string_so_a_disabled_export_changes_nothing()
        {
            Assert.That(IosEarlyLaunchPolicy.RemoveInfoPlistEntry(Plist), Is.SameAs(Plist));
            Assert.That(IosEarlyLaunchPolicy.RemoveInfoPlistEntry(null), Is.Null);
        }

        [Test]
        public void Crlf_plists_keep_their_line_endings()
        {
            string crlf = Plist.Replace("\n", "\r\n");
            var entries = IosEarlyLaunchPolicy.Decide(On()).Entries;
            string with = IosEarlyLaunchPolicy.SetInfoPlistEntry(crlf, entries);
            Assert.That(with.Replace("\r\n", "").Contains("\n"), Is.False);
            Assert.That(IosEarlyLaunchPolicy.RemoveInfoPlistEntry(with), Is.EqualTo(crlf));
        }

        [Test]
        public void Values_are_xml_escaped_and_round_trip_through_a_real_xml_parser()
        {
            var entries = IosEarlyLaunchPolicy.Decide(On(i =>
            {
                i.ProductName = "R&D <\"Game\"> 'x'";
                i.OptionsJson = "{\"k\":\"a<b>&\\\"c\"}";
            })).Entries;
            string with = IosEarlyLaunchPolicy.SetInfoPlistEntry(Plist, entries);
            var doc = new System.Xml.XmlDocument { XmlResolver = null };
            doc.XmlResolver = null;
            var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore, XmlResolver = null };
            using (var reader = System.Xml.XmlReader.Create(new StringReader(with), settings)) doc.Load(reader);
            var keys = doc.SelectNodes("/plist/dict/dict/key");
            var values = doc.SelectNodes("/plist/dict/dict/string");
            var read = new Dictionary<string, string>();
            for (int i = 0; i < keys.Count; i++) read[keys[i].InnerText] = values[i].InnerText;
            Assert.That(read[IosEarlyLaunchPolicy.ProductNameKey], Is.EqualTo("R&D <\"Game\"> 'x'"));
            Assert.That(read[IosEarlyLaunchPolicy.OptionsKey], Is.EqualTo(entries[IosEarlyLaunchPolicy.OptionsKey]));
        }

        [Test]
        public void An_unrecognisable_plist_is_returned_unchanged()
        {
            var entries = IosEarlyLaunchPolicy.Decide(On()).Entries;
            Assert.That(IosEarlyLaunchPolicy.SetInfoPlistEntry("not a plist", entries), Is.EqualTo("not a plist"));
            Assert.That(IosEarlyLaunchPolicy.SetInfoPlistEntry(null, entries), Is.Null);
        }

        [Test]
        public void A_foreign_key_with_a_similar_name_is_not_removed()
        {
            const string foreign = "<plist version=\"1.0\">\n<dict>\n\t<key>BugseeEarlyLaunch</key>\n\t<true/>\n\t<key>x</key>\n\t<dict>\n\t</dict>\n</dict>\n</plist>\n";
            Assert.That(IosEarlyLaunchPolicy.RemoveInfoPlistEntry(foreign), Is.SameAs(foreign));
        }

        [Test]
        public void A_plist_with_a_nested_dict_before_our_key_keeps_it_and_round_trips()
        {
            const string nested =
                "<plist version=\"1.0\">\n<dict>\n\t<key>NSAppTransportSecurity</key>\n\t<dict>\n\t\t<key>NSAllowsArbitraryLoads</key>\n\t\t<true/>\n\t</dict>\n" +
                "\t<key>CFBundleName</key>\n\t<string>G</string>\n</dict>\n</plist>\n";
            var entries = IosEarlyLaunchPolicy.Decide(On()).Entries;
            string with = IosEarlyLaunchPolicy.SetInfoPlistEntry(nested, entries);
            Assert.That(with, Does.Contain("<key>NSAllowsArbitraryLoads</key>"));
            Assert.That(CountOf(with, "<key>BugseeEarlyLaunch</key>"), Is.EqualTo(1));
            Assert.That(IosEarlyLaunchPolicy.SetInfoPlistEntry(with, entries), Is.EqualTo(with));
            Assert.That(IosEarlyLaunchPolicy.RemoveInfoPlistEntry(with), Is.EqualTo(nested));
            // Our entry placed before a nested dict is removed without eating the neighbour.
            int tail = nested.LastIndexOf("</dict>\n</plist>", System.StringComparison.Ordinal);
            string block = with.Substring(tail, with.Length - nested.Length);
            Assert.That(block, Does.StartWith("\t<key>BugseeEarlyLaunch</key>"));
            string before = nested.Insert(nested.IndexOf("<dict>\n", System.StringComparison.Ordinal) + "<dict>\n".Length, block);
            Assert.That(before.IndexOf("<key>BugseeEarlyLaunch</key>", System.StringComparison.Ordinal),
                Is.LessThan(before.IndexOf("<key>NSAppTransportSecurity</key>", System.StringComparison.Ordinal)), "our entry comes first");
            Assert.That(IosEarlyLaunchPolicy.SetInfoPlistEntry(before, entries), Is.EqualTo(with), "replaced, then appended at the end");
            Assert.That(IosEarlyLaunchPolicy.RemoveInfoPlistEntry(before), Is.EqualTo(nested), "the nested neighbour is not eaten");
        }

        // ---------------------------------------------------------------- adoption state machine

        [TestCase(0, 0, IosEarlyAdoption.None)]
        [TestCase(1, 0, IosEarlyAdoption.Adopt)]   // ran, deferred: status Stopped
        [TestCase(1, 1, IosEarlyAdoption.Adopt)]   // launching
        [TestCase(1, 2, IosEarlyAdoption.Adopt)]   // launched
        [TestCase(1, 3, IosEarlyAdoption.None)]    // a stop in flight is left to the normal Launch
        [TestCase(2, 0, IosEarlyAdoption.AdoptTokenDiffers)]
        [TestCase(2, 2, IosEarlyAdoption.AdoptTokenDiffers)]
        [TestCase(7, 2, IosEarlyAdoption.None)]
        public void ResolveAdoption_by_native_answer_and_status(int answer, int status, IosEarlyAdoption expected) =>
            Assert.That(IosEarlyLaunchPolicy.ResolveAdoption(answer, status), Is.EqualTo(expected));

        [Test]
        public void Session_not_run_never_adopts()
        {
            var s = new EarlyLaunchSession();
            Assert.That(s.Adopt(Token), Is.EqualTo(0));
        }

        [Test]
        public void Session_ran_adopts_once_regardless_of_status_then_launches_normally()
        {
            var s = new EarlyLaunchSession();
            s.Ran(Token);
            Assert.That(IosEarlyLaunchPolicy.ResolveAdoption(s.Adopt(Token), 0), Is.EqualTo(IosEarlyAdoption.Adopt), "deferred (Stopped)");
            Assert.That(IosEarlyLaunchPolicy.ResolveAdoption(s.Adopt(Token), 0), Is.EqualTo(IosEarlyAdoption.None), "Stop then Launch: normal");
        }

        [Test]
        public void Session_with_another_token_reports_it_once()
        {
            var s = new EarlyLaunchSession();
            s.Ran(Token);
            Assert.That(s.Adopt("other"), Is.EqualTo(2));
            Assert.That(s.Adopt("other"), Is.EqualTo(0));
        }

        [Test]
        public void Session_with_a_null_token_given_counts_as_different()
        {
            var s = new EarlyLaunchSession();
            s.Ran(Token);
            Assert.That(s.Adopt(null), Is.EqualTo(2));
        }

        [Test]
        public void A_stop_in_flight_consumes_the_adoption_so_the_later_launch_is_normal()
        {
            var s = new EarlyLaunchSession();
            s.Ran(Token);
            Assert.That(IosEarlyLaunchPolicy.ResolveAdoption(s.Adopt(Token), 3), Is.EqualTo(IosEarlyAdoption.None));
            Assert.That(s.Adopt(Token), Is.EqualTo(0));
        }

        [Test]
        public void A_stop_before_the_first_launch_claims_the_session_so_that_launch_starts_the_sdk()
        {
            // The native cancel sets the same flag the first question sets; the policy mirror is Cancel().
            var s = new EarlyLaunchSession();
            s.Ran(Token);
            s.Cancel();
            Assert.That(IosEarlyLaunchPolicy.ResolveAdoption(s.Adopt(Token), 0), Is.EqualTo(IosEarlyAdoption.None));
        }

        [Test]
        public void A_stop_before_the_early_launch_ran_leaves_nothing_to_adopt_later()
        {
            var s = new EarlyLaunchSession();
            s.Cancel();
            s.Ran(Token);
            Assert.That(s.Adopt(Token), Is.EqualTo(0));
        }

        [Test]
        public void C_sharp_asking_first_claims_the_slot_so_the_early_launch_that_fires_later_does_not_launch()
        {
            var s = new EarlyLaunchSession();
            Assert.That(s.Adopt(Token), Is.EqualTo(0), "nothing to adopt yet: this Launch starts the SDK");
            Assert.That(s.Ran(Token), Is.False, "the early launch must not start it a second time");
            Assert.That(s.Adopt(Token), Is.EqualTo(0));
        }

        [TestCase(null)]
        [TestCase("")]
        public void An_early_run_without_a_usable_configuration_leaves_nothing_to_adopt_so_the_launch_starts_normally(string token)
        {
            var s = new EarlyLaunchSession();
            Assert.That(s.Ran(token), Is.False, "no launch call was made");
            Assert.That(IosEarlyLaunchPolicy.ResolveAdoption(s.Adopt(Token), 0), Is.EqualTo(IosEarlyAdoption.None));
        }

        [Test]
        public void A_deferred_early_start_stays_adoptable_while_the_status_is_still_stopped()
        {
            var s = new EarlyLaunchSession();
            Assert.That(s.Ran(Token), Is.True);
            Assert.That(IosEarlyLaunchPolicy.ResolveAdoption(s.Adopt(Token), 0), Is.EqualTo(IosEarlyAdoption.Adopt));
        }

        [Test]
        public void A_stop_before_the_early_launch_ran_prevents_it_from_launching()
        {
            var s = new EarlyLaunchSession();
            s.Cancel();
            Assert.That(s.Ran(Token), Is.False);
        }

        [Test]
        public void The_early_launch_launches_when_nobody_claimed_the_slot_and_only_once()
        {
            var s = new EarlyLaunchSession();
            Assert.That(s.Ran(Token), Is.True);
            Assert.That(s.Ran(Token), Is.False);
            Assert.That(s.Adopt(Token), Is.EqualTo(1));
        }

        static int CountOf(string text, string needle)
        {
            int count = 0, at = 0;
            while ((at = text.IndexOf(needle, at, System.StringComparison.Ordinal)) >= 0) { count++; at += needle.Length; }
            return count;
        }
    }

    public class EarlyLifecycleReplayTests
    {
        const string Launching = "com.bugsee.lifecycle.Launching";
        const string Launched = "com.bugsee.lifecycle.Launched";
        const string Stopped = "com.bugsee.lifecycle.Stopped";
        const string Other = "com.bugsee.lifecycle.AfterReportUploaded";

        [Test]
        public void Not_armed_everything_passes_even_repeats()
        {
            var r = new EarlyLifecycleReplay();
            Assert.That(r.Admit(Launched), Is.True);
            Assert.That(r.Admit(Launched), Is.True);
            Assert.That(r.Admit(Other), Is.True);
        }

        [Test]
        public void Arm_replays_by_native_status()
        {
            Assert.That(new EarlyLifecycleReplay().Arm(2), Is.EqualTo(new[] { Launching, Launched }));
            Assert.That(new EarlyLifecycleReplay().Arm(1), Is.EqualTo(new[] { Launching }));
            Assert.That(new EarlyLifecycleReplay().Arm(0), Is.Empty);
            Assert.That(new EarlyLifecycleReplay().Arm(3), Is.Empty);
        }

        [Test]
        public void A_live_launched_after_the_replay_is_dropped_once()
        {
            var r = new EarlyLifecycleReplay();
            foreach (var e in r.Arm(2)) Assert.That(r.Admit(e), Is.True);
            Assert.That(r.Admit(Launched), Is.False);
            Assert.That(r.Admit(Other), Is.True);
        }

        [Test]
        public void Launching_status_replays_launching_and_the_live_launched_follows_once()
        {
            var r = new EarlyLifecycleReplay();
            foreach (var e in r.Arm(1)) Assert.That(r.Admit(e), Is.True);
            Assert.That(r.Admit(Launched), Is.True);
            Assert.That(r.Admit(Launched), Is.False);
        }

        [Test]
        public void An_event_already_delivered_live_before_the_arming_is_not_replayed()
        {
            var r = new EarlyLifecycleReplay();
            Assert.That(r.Admit(Launching), Is.True);
            Assert.That(r.Admit(Launched), Is.True);
            Assert.That(r.Arm(2), Is.Empty);
            Assert.That(r.Admit(Launched), Is.False);
        }

        [Test]
        public void Adopted_while_deferred_replays_nothing_and_the_later_live_events_pass_once()
        {
            var r = new EarlyLifecycleReplay();
            Assert.That(r.Arm(0), Is.Empty);
            Assert.That(r.Admit(Launching), Is.True);
            Assert.That(r.Admit(Launching), Is.False);
            Assert.That(r.Admit(Launched), Is.True);
            Assert.That(r.Admit(Launched), Is.False);
        }

        [Test]
        public void Adopted_while_deferred_then_stop_then_a_new_session_passes()
        {
            var r = new EarlyLifecycleReplay();
            r.Arm(0);
            r.Admit(Launching); r.Admit(Launched);
            Assert.That(r.Admit(Stopped), Is.True);
            Assert.That(r.Admit(Launching), Is.True);
            Assert.That(r.Admit(Launched), Is.True);
            Assert.That(r.Admit(Launched), Is.True);
        }

        [Test]
        public void Stop_ends_the_adoption_and_a_new_session_events_pass()
        {
            var r = new EarlyLifecycleReplay();
            foreach (var e in r.Arm(2)) r.Admit(e);
            Assert.That(r.Admit(Stopped), Is.True);
            Assert.That(r.Admit(Launching), Is.True);
            Assert.That(r.Admit(Launched), Is.True);
            Assert.That(r.Admit(Launched), Is.True, "no longer armed");
        }

        [Test]
        public void A_launch_of_its_own_disarms()
        {
            var r = new EarlyLifecycleReplay();
            foreach (var e in r.Arm(2)) r.Admit(e);
            r.Disarm();
            Assert.That(r.Admit(Launched), Is.True);
            Assert.That(r.Admit(Launched), Is.True);
        }
    }

    /// <summary>The wiring that cannot run without a device is pinned by source: the default path of Launch is unchanged and the native side is guarded.</summary>
    public class IosEarlyLaunchSourceTests
    {
        static string Root()
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "package.json"))) dir = Directory.GetParent(dir)?.FullName;
            return dir;
        }

        static string Read(string relative) => File.ReadAllText(Path.Combine(Root(), relative));

        [Test]
        public void The_native_file_does_nothing_without_the_info_plist_dictionary()
        {
            string mm = Read("Plugins/iOS/BugseeEarlyLaunch.mm");
            Assert.That(mm, Does.Contain("objectForInfoDictionaryKey:kBugseeEarlyKey"));
            Assert.That(mm, Does.Contain("kBugseeEarlyKey = @\"" + IosEarlyLaunchPolicy.InfoPlistKey + "\""));
            Assert.That(mm, Does.Contain("TARGET_OS_SIMULATOR"));
            // The +load hook returns before registering anything when the dictionary is absent.
            int load = mm.IndexOf("+ (void)load", System.StringComparison.Ordinal);
            int guard = mm.IndexOf("return;", load, System.StringComparison.Ordinal);
            int observer = mm.IndexOf("addObserverForName", load, System.StringComparison.Ordinal);
            Assert.That(guard, Is.GreaterThan(load).And.LessThan(observer));
        }

        [Test]
        public void The_protected_native_bridge_files_do_not_mention_the_early_launch()
        {
            Assert.That(Read("Plugins/iOS/BugseeUnityBridge.mm"), Does.Not.Contain("early_launch"));
            Assert.That(Read("Plugins/iOS/BugseeUnityCallbacks.mm"), Does.Not.Contain("early_launch"));
        }

        [Test]
        public void The_native_entry_points_the_managed_side_imports_exist_in_both_branches_of_the_native_file()
        {
            string mm = Read("Plugins/iOS/BugseeEarlyLaunch.mm");
            string cs = Read("Runtime/Platform/IOS/IOSBridge.cs");
            foreach (string symbol in new[] { "_bugsee_early_launch_adopt", "_bugsee_early_launch_cancel", "_bugsee_early_launch_options" })
            {
                Assert.That(cs, Does.Contain("static extern").And.Contain(symbol));
                Assert.That(CountOf(mm, symbol + "("), Is.EqualTo(2), symbol + " must be defined with and without the SDK");
            }
        }

        [Test]
        public void Launch_asks_for_adoption_after_the_wrapper_is_registered_and_before_the_native_launch()
        {
            string cs = Read("Runtime/Platform/IOS/IOSBridge.cs");
            int launchNow = cs.IndexOf("void LaunchNow(", System.StringComparison.Ordinal);
            int ensure = cs.IndexOf("EnsureWrapperRegistered();", launchNow, System.StringComparison.Ordinal);
            int adopt = cs.IndexOf("_bugsee_early_launch_adopt(appToken)", launchNow, System.StringComparison.Ordinal);
            int native = cs.IndexOf("_bugsee_launch(appToken", launchNow, System.StringComparison.Ordinal);
            Assert.That(ensure, Is.GreaterThan(launchNow));
            Assert.That(adopt, Is.GreaterThan(ensure).And.LessThan(native));
        }

        [Test]
        public void Adoption_is_wired_through_the_policy_the_replay_filter_and_the_started_callback()
        {
            string cs = Read("Runtime/Platform/IOS/IOSBridge.cs");
            Assert.That(cs, Does.Contain("IosEarlyLaunchPolicy.ResolveAdoption(_bugsee_early_launch_adopt(appToken), _bugsee_get_status())"));
            int adopt = cs.IndexOf("void AdoptEarlyLaunch(", System.StringComparison.Ordinal);
            int end = cs.IndexOf("void RelaunchNow(", adopt, System.StringComparison.Ordinal);
            string body = cs.Substring(adopt, end - adopt);
            Assert.That(body, Does.Contain("DifferingKeys").And.Contain("AdoptionWarning"));
            Assert.That(body, Does.Contain("ExceptionPipeline.Install").And.Contain("HostLogForwarder.InstallOnce"));
            Assert.That(body, Does.Contain("EarlyReplay.Arm").And.Contain("DeliverLifecycle"));
            Assert.That(body, Does.Contain("RunResult(started, true)"));
            Assert.That(body, Does.Not.Contain("_bugsee_launch("), "an adopting Launch must not start the SDK again");
            Assert.That(Read("Runtime/Platform/IOS/IosNativeCallbacks.cs"), Does.Contain("EarlyReplay.Admit(eventType)"));
        }

        [Test]
        public void The_native_adopt_does_not_depend_on_the_sdk_status()
        {
            string mm = Read("Plugins/iOS/BugseeEarlyLaunch.mm");
            int at = mm.IndexOf("int _bugsee_early_launch_adopt(const char *appToken)\n{", System.StringComparison.Ordinal);
            string body = mm.Substring(at, mm.IndexOf("char *_bugsee_early_launch_options", at, System.StringComparison.Ordinal) - at);
            Assert.That(body, Does.Not.Contain("status"));
        }

        [Test]
        public void The_policy_stays_free_of_Unity_types()
        {
            Assert.That(Read("Runtime/WrapperPolicy/IosEarlyLaunchPolicy.cs"), Does.Not.Contain("UnityEngine"));
        }

        [Test]
        public void Every_native_stop_and_delete_claims_the_early_session_first()
        {
            string cs = Read("Runtime/Platform/IOS/IOSBridge.cs");
            int at = cs.IndexOf("bool StopNativeThen(", System.StringComparison.Ordinal);
            int cancel = cs.IndexOf("_bugsee_early_launch_cancel();", at, System.StringComparison.Ordinal);
            int stop = cs.IndexOf("_bugsee_stop(", at, System.StringComparison.Ordinal);
            Assert.That(cancel, Is.GreaterThan(at).And.LessThan(stop));
            string mm = Read("Plugins/iOS/BugseeEarlyLaunch.mm");
            int body = mm.IndexOf("void _bugsee_early_launch_cancel(void)\n{", System.StringComparison.Ordinal);
            Assert.That(mm.Substring(body, 160), Does.Contain("gEarlyAdopted = YES"));
        }

        [Test]
        public void Only_the_state_set_right_before_the_launch_call_can_be_adopted_in_the_native_file()
        {
            string mm = Read("Plugins/iOS/BugseeEarlyLaunch.mm");
            int run = mm.IndexOf("static void BugseeEarlyLaunchRun(", System.StringComparison.Ordinal);
            int launch = mm.IndexOf("[Bugsee launchWithToken:", run, System.StringComparison.Ordinal);
            int set = mm.IndexOf("gEarlyLaunched = YES;", run, System.StringComparison.Ordinal);
            Assert.That(set, Is.GreaterThan(run).And.LessThan(launch));
            // Every early return of the run (missing or invalid configuration) comes before that assignment.
            int lastReturn = mm.LastIndexOf("return;", set, System.StringComparison.Ordinal);
            Assert.That(lastReturn, Is.GreaterThan(run).And.LessThan(set));
            Assert.That(CountOf(mm, "gEarlyLaunched = YES;"), Is.EqualTo(1), "set in one place only");
        }

        [Test]
        public void The_native_early_launch_run_consults_the_claim_and_the_adopt_claims_even_before_it_ran()
        {
            string mm = Read("Plugins/iOS/BugseeEarlyLaunch.mm");
            int run = mm.IndexOf("static void BugseeEarlyLaunchRun(", System.StringComparison.Ordinal);
            int launch = mm.IndexOf("[Bugsee launchWithToken:", run, System.StringComparison.Ordinal);
            // Anchored on code, not comments.
            int check = mm.IndexOf("BOOL already = gEarlyAttempted || gEarlyAdopted;", run, System.StringComparison.Ordinal);
            Assert.That(check, Is.GreaterThan(run).And.LessThan(launch), "the claim is checked before the launch call");
            int recheck = mm.IndexOf("BOOL claimed = gEarlyAdopted;", check, System.StringComparison.Ordinal);
            int record = mm.IndexOf("gEarlyLaunched = YES;", check, System.StringComparison.Ordinal);
            Assert.That(recheck, Is.GreaterThan(check).And.LessThan(record), "the claim is re-checked in the step that records the launch");
            Assert.That(mm.IndexOf("if (!claimed) {", recheck, System.StringComparison.Ordinal), Is.GreaterThan(recheck).And.LessThan(record));
            Assert.That(record, Is.LessThan(launch));
            int adopt = mm.IndexOf("int _bugsee_early_launch_adopt(const char *appToken)\n{", System.StringComparison.Ordinal);
            string body = mm.Substring(adopt, mm.IndexOf("void _bugsee_early_launch_cancel", adopt, System.StringComparison.Ordinal) - adopt);
            Assert.That(body.IndexOf("gEarlyAdopted = YES", System.StringComparison.Ordinal),
                Is.GreaterThan(0).And.LessThan(body.IndexOf("gEarlyLaunched", System.StringComparison.Ordinal)), "claimed before looking at whether it ran");
        }

        static int CountOf(string text, string needle)
        {
            int count = 0, at = 0;
            while ((at = text.IndexOf(needle, at, System.StringComparison.Ordinal)) >= 0) { count++; at += needle.Length; }
            return count;
        }
    }
}
