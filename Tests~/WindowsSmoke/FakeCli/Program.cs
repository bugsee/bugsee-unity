using System;
using System.IO;
using System.Text;

// Stand-in for bugsee-cli in the Windows smoke tests: "--version" prints what the real one prints, anything else echoes
// its arguments exactly as this process received them, one per line as [text], so a test can compare them with what
// ProcessStartInfo.ArgumentList was given ("--probe=<file>" adds an exists/length line). BUGSEE_FAKE_EXIT sets the exit code.
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
            // "--probe=<path>": also report whether this process can see that file and how long it is, the way the CLI
            // must read a mapping or symbols file under a long build output path.
            if (a.StartsWith("--probe=", StringComparison.Ordinal))
            {
                string f = a.Substring("--probe=".Length);
                bool exists = File.Exists(f);
                o.Write("probe exists=" + exists + " length=" + (exists ? new FileInfo(f).Length : -1) + "\n");
            }
        }

        o.Write("token=" + (Environment.GetEnvironmentVariable("BUGSEE_APP_TOKEN") ?? "") + "\n");
        o.Flush();
        int.TryParse(Environment.GetEnvironmentVariable("BUGSEE_FAKE_EXIT"), out var code);
        return code;
    }
}
