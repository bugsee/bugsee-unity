using System.IO;
using System.Runtime.InteropServices;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    // The iOS Run Script tests need macOS tools. Off macOS (Linux CI) they skip; ON macOS a missing tool is a failure, so the
    // skip can't hide there (the macOS CI leg exists to run them).
    static class IosScriptTools
    {
        public static void Require(string path, string what)
        {
            if (File.Exists(path))
                return;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Assert.Fail("macOS must provide " + what + " (" + path + "); the iOS Run Script tests must not be skipped here.");
            Assert.Ignore("needs " + what);
        }
    }
}
