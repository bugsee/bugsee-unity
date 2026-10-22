using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    // The Run Script records the fingerprint of BUGSEE_APP_TOKEN in the built Info.plist; Launch compares it (#207).
    public class IosBuildTokenFingerprintScriptTests
    {
        const string Token = "2de6d56a-f38b-4fa6-8645-5d723c2d3ea7";
        const string Key = "BugseeBuildTokenFingerprint";

        sealed class Run
        {
            public string Output;
            public int ExitCode;
            public string Plist;
            public bool Binary;
        }

        static Run RunScript(Action<IDictionary<string, string>> env, string existingKey = null, bool binaryPlist = false, string fakeSed = null)
        {
            if (!File.Exists("/usr/libexec/PlistBuddy") || !File.Exists("/usr/bin/shasum"))
                Assert.Ignore("needs PlistBuddy and shasum");
            string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "bugsee-fp-" + Guid.NewGuid().ToString("N"))).FullName;
            try
            {
                string products = Path.Combine(root, "Products");
                Directory.CreateDirectory(Path.Combine(products, "App.app"));
                string plist = Path.Combine(products, "App.app", "Info.plist");
                File.WriteAllText(plist, "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<plist version=\"1.0\"><dict><key>CFBundleVersion</key><string>1</string>"
                    + (existingKey != null ? "<key>" + Key + "</key><string>" + existingKey + "</string>" : "") + "</dict></plist>\n");
                if (binaryPlist)
                    Process.Start("/usr/bin/plutil", "-convert binary1 \"" + plist + "\"").WaitForExit();
                string script = Path.Combine(root, "upload-symbols.sh");
                File.WriteAllText(script, IosArchiveUploadScript.Build());
                var info = new ProcessStartInfo("/bin/sh", "\"" + script + "\"")
                {
                    RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false,
                };
                info.Environment.Clear();
                info.Environment["PATH"] = "/usr/bin:/bin";
                if (fakeSed != null)
                {
                    string bin = Directory.CreateDirectory(Path.Combine(root, "fakebin")).FullName;
                    File.WriteAllText(Path.Combine(bin, "sed"), fakeSed);
                    File.SetUnixFileMode(Path.Combine(bin, "sed"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    info.Environment["PATH"] = bin + ":/usr/bin:/bin";
                }

                info.Environment["HOME"] = root;
                info.Environment["BUGSEE_CLI_SYSTEM_DIRS"] = Path.Combine(root, "none");
                info.Environment["CONFIGURATION"] = "Release";
                info.Environment["TARGET_BUILD_DIR"] = products;
                info.Environment["INFOPLIST_PATH"] = "App.app/Info.plist";
                info.Environment["PROJECT_DIR"] = root;
                info.Environment["BUGSEE_APP_TOKEN"] = Token;
                env?.Invoke(info.Environment);
                string output;
                int code;
                using (var p = Process.Start(info))
                {
                    p.StandardInput.Close();
                    var err = p.StandardError.ReadToEndAsync();
                    output = p.StandardOutput.ReadToEnd() + err.Result;
                    p.WaitForExit();
                    code = p.ExitCode;
                }

                bool binary;
                using (var f = File.OpenRead(plist))
                {
                    var head = new byte[6];
                    binary = f.Read(head, 0, 6) == 6 && System.Text.Encoding.ASCII.GetString(head) == "bplist";
                }

                return new Run { Output = output, ExitCode = code, Plist = ReadKey(plist), Binary = binary };
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        }

        static string ReadKey(string plist)
        {
            var info = new ProcessStartInfo("/usr/libexec/PlistBuddy", "-c \"Print :" + Key + "\" \"" + plist + "\"")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            using (var p = Process.Start(info))
            {
                string o = p.StandardOutput.ReadToEnd().Trim();
                p.StandardError.ReadToEnd();
                p.WaitForExit();
                return p.ExitCode == 0 ? o : null;
            }
        }

        [Test]
        public void Script_records_the_same_fingerprint_the_runtime_computes()
        {
            var run = RunScript(null);
            Assert.That(run.ExitCode, Is.EqualTo(0), run.Output);
            Assert.That(run.Plist, Is.EqualTo(BuildTokenFingerprint.Compute(Token)), run.Output);
            Assert.That(BuildTokenFingerprint.Check(run.Plist, Token), Is.EqualTo(BuildTokenFingerprint.Verdict.Match));
            Assert.That(run.Output, Does.Not.Contain(Token));
        }

        [Test]
        public void Script_trims_the_token_like_the_runtime()
        {
            var run = RunScript(env => env["BUGSEE_APP_TOKEN"] = "  " + Token + " ");
            Assert.That(run.Plist, Is.EqualTo(BuildTokenFingerprint.Compute(Token)), run.Output);
        }

        [Test]
        public void Script_records_another_token_as_a_mismatch()
        {
            var run = RunScript(env => env["BUGSEE_APP_TOKEN"] = "99999999-8888-7777-6666-555555555555");
            Assert.That(BuildTokenFingerprint.Check(run.Plist, Token), Is.EqualTo(BuildTokenFingerprint.Verdict.Mismatch));
        }

        [Test]
        public void Script_replaces_a_fingerprint_left_by_an_earlier_build()
        {
            var run = RunScript(null, existingKey: "0000000000000000");
            Assert.That(run.Plist, Is.EqualTo(BuildTokenFingerprint.Compute(Token)), run.Output);
        }

        [TestCase("BUGSEE_NO_TOKEN_CHECK")]
        [TestCase("BUGSEE_NO_SYMBOL_UPLOAD")]
        public void Script_removes_a_stale_fingerprint_when_the_check_or_upload_is_off(string variable)
        {
            var run = RunScript(env => env[variable] = "1", existingKey: "0000000000000000");
            Assert.That(run.ExitCode, Is.EqualTo(0), run.Output);
            Assert.That(run.Plist, Is.Null, run.Output);
        }

        [Test]
        public void Script_removes_a_stale_fingerprint_when_there_is_no_token()
        {
            var run = RunScript(env => env["BUGSEE_APP_TOKEN"] = "", existingKey: "0000000000000000");
            Assert.That(run.Plist, Is.Null, run.Output);
        }

        [Test]
        public void Script_records_nothing_when_a_debug_build_skips_the_upload()
        {
            var run = RunScript(env => { env["CONFIGURATION"] = "Debug"; env["BUGSEE_SKIP_UPLOAD_ON_DEBUG"] = "1"; }, existingKey: "0000000000000000");
            Assert.That(run.Plist, Is.Null, run.Output);
        }

        [Test]
        public void Script_keeps_a_binary_plist_binary()
        {
            var run = RunScript(null, binaryPlist: true);
            Assert.That(run.Plist, Is.EqualTo(BuildTokenFingerprint.Compute(Token)), run.Output);
            Assert.That(run.Binary, Is.True);
            var stale = RunScript(env => env["BUGSEE_NO_TOKEN_CHECK"] = "1", existingKey: "0000000000000000", binaryPlist: true);
            Assert.That(stale.Plist, Is.Null, stale.Output);
            Assert.That(stale.Binary, Is.True);
        }

        [Test]
        public void Script_trims_a_trailing_line_break_but_records_nothing_for_one_inside_the_token()
        {
            var crlf = RunScript(env => env["BUGSEE_APP_TOKEN"] = Token + "\r\n");
            Assert.That(crlf.Plist, Is.EqualTo(BuildTokenFingerprint.Compute(Token)), crlf.Output);
            // sed trims each line and C# only the ends, so the two would disagree: no fingerprint, no false warning.
            var inside = RunScript(env => env["BUGSEE_APP_TOKEN"] = " ab\n cd ", existingKey: "0000000000000000");
            Assert.That(inside.ExitCode, Is.EqualTo(0), inside.Output);
            Assert.That(inside.Plist, Is.Null, inside.Output);
        }

        [Test]
        public void Script_survives_a_token_sed_cannot_read()
        {
            // sed fails on a token that is not valid UTF-8 in a UTF-8 locale; set -e must not end the script there.
            var run = RunScript(null, existingKey: "0000000000000000", fakeSed: "#!/bin/sh\ncat >/dev/null\nexit 1\n");
            Assert.That(run.ExitCode, Is.EqualTo(0), run.Output);
            Assert.That(run.Plist, Is.Null, run.Output);
            Assert.That(run.Output, Does.Contain("Bugsee: install it"), "the script went on to the upload");
        }

        [Test]
        public void Script_records_nothing_for_tvOS_which_has_no_check()
        {
            var run = RunScript(env => env["PLATFORM_NAME"] = "appletvos", existingKey: "0000000000000000");
            Assert.That(run.Plist, Is.Null, run.Output);
        }

        [Test]
        public void Script_records_the_fingerprint_even_when_the_cli_is_missing()
        {
            var run = RunScript(null);
            Assert.That(run.Output, Does.Contain("bugsee-cli was not found"));
            Assert.That(run.Plist, Is.EqualTo(BuildTokenFingerprint.Compute(Token)), run.Output);
        }

        [Test]
        public void Script_without_a_plist_still_exits_zero()
        {
            var run = RunScript(env => env["INFOPLIST_PATH"] = "missing/Info.plist");
            Assert.That(run.ExitCode, Is.EqualTo(0), run.Output);
        }

        [Test]
        public void Launch_check_reads_the_plist_value_on_iOS_through_the_bridge_export()
        {
            string root = AppDomain.CurrentDomain.BaseDirectory;
            while (root != null && !File.Exists(Path.Combine(root, "Runtime", "Internal", "BuildTokenCheck.cs")))
                root = Path.GetDirectoryName(root);
            Assert.That(root, Is.Not.Null);
            string check = File.ReadAllText(Path.Combine(root, "Runtime", "Internal", "BuildTokenCheck.cs"));
            Assert.That(check, Does.Contain("RecordedBuildTokenFingerprint"));
            Assert.That(File.ReadAllText(Path.Combine(root, "Plugins", "iOS", "BugseeUnityBridge.mm")),
                Does.Contain("objectForInfoDictionaryKey:@\"" + Key + "\""));
        }
    }
}
