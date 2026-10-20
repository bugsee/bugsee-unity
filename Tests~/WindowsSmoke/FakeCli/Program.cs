using System;
using System.IO;
using System.Text;

// Stand-in for bugsee-cli in the Windows smoke tests: "--version" prints what the real one prints, anything else echoes
// its arguments exactly as this process received them, one per line as [text], so a test can compare them with what
// ProcessStartInfo.ArgumentList was given. BUGSEE_FAKE_EXIT sets the exit code.
internal static class Program
{
    private static int Main(string[] args)
    {
        // UTF-8 bytes straight to stdout, whatever the console code page is.
        var o = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
        if (args.Length == 1 && args[0] == "--version")
        {
            o.Write("bugsee-cli 0.8.1\r\n");
            o.Flush();
            return 0;
        }

        foreach (var a in args)
        {
            o.Write("[" + a + "]\n");
        }

        o.Write("token=" + (Environment.GetEnvironmentVariable("BUGSEE_APP_TOKEN") ?? "") + "\n");
        o.Flush();
        int.TryParse(Environment.GetEnvironmentVariable("BUGSEE_FAKE_EXIT"), out var code);
        return code;
    }
}
