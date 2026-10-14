using System;
using System.IO;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class IosSymbolUploadGateTests
    {
        static string SymbolUploadSource()
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, "package.json")))
                    return File.ReadAllText(Path.Combine(dir, "Editor", "BugseeSymbolUpload.cs"));
                dir = Directory.GetParent(dir)?.FullName;
            }

            throw new InvalidOperationException("Could not locate repo root (package.json).");
        }

        // A crash from a Debug (Development) build is unsymbolicated without its dSYM, so no configuration may be
        // skipped unless the build environment asks.
        [Test]
        public void Archive_script_uploads_every_configuration_unless_debug_is_opted_out()
        {
            string text = SymbolUploadSource();
            Assert.That(text, Does.Not.Contain("!= \\\"Release\\\""));
            Assert.That(text, Does.Not.Contain("BUGSEE_UPLOAD_ON_DEBUG"));
            Assert.That(text, Does.Contain("${BUGSEE_SKIP_UPLOAD_ON_DEBUG}"));
            Assert.That(text, Does.Contain("${CONFIGURATION}\\\" = \\\"Debug\\\""));
        }

        // Unity's Debug configuration writes no dSYM, so an upload there would otherwise end silently with nothing sent.
        [Test]
        public void Archive_script_warns_when_the_build_has_no_dsym()
        {
            string text = SymbolUploadSource();
            Assert.That(text, Does.Contain(
                "if [ ! -d \\\"${DWARF_DSYM_FOLDER_PATH}/${DWARF_DSYM_FILE_NAME}\\\" ] && [ ! -d \\\"${DWARF_DSYM_FOLDER_PATH}/UnityFramework.framework.dSYM\\\" ]; then"));
            Assert.That(text, Does.Contain("echo \\\"warning: Bugsee: no dSYM for ${CONFIGURATION}"));
        }
    }
}
