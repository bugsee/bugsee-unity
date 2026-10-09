#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Bugsee.Editor
{
    /// <summary>
    /// Shared <c>bugsee-cli</c> process runner for Editor symbol uploads.
    /// Never throws — callers treat non-zero exits as warn-and-continue.
    /// </summary>
    internal static class BugseeCliRunner
    {
        public const int DefaultTimeoutMs = 5 * 60 * 1000;

        public static string CliPath =>
            Environment.GetEnvironmentVariable("BUGSEE_CLI_PATH") ?? "bugsee-cli";

        public static string AppToken =>
            Environment.GetEnvironmentVariable("BUGSEE_APP_TOKEN");

        public static bool HasAppToken => !string.IsNullOrEmpty(AppToken);

        public static int Run(IList<string> args, int timeoutMs = DefaultTimeoutMs)
        {
            if (args == null || args.Count == 0)
            {
                return -1;
            }

            var quoted = new string[args.Count];
            for (var i = 0; i < args.Count; i++)
            {
                quoted[i] = Quote(args[i] ?? "");
            }

            var psi = new ProcessStartInfo
            {
                FileName = CliPath,
                Arguments = string.Join(" ", quoted),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            var token = AppToken;
            if (!string.IsNullOrEmpty(token))
            {
                psi.EnvironmentVariables["BUGSEE_APP_TOKEN"] = token;
            }

            var endpoint = Environment.GetEnvironmentVariable("BUGSEE_ENDPOINT");
            if (!string.IsNullOrEmpty(endpoint))
            {
                psi.EnvironmentVariables["BUGSEE_ENDPOINT"] = endpoint;
            }

            var cliPathEnv = Environment.GetEnvironmentVariable("BUGSEE_CLI_PATH");
            if (!string.IsNullOrEmpty(cliPathEnv))
            {
                psi.EnvironmentVariables["BUGSEE_CLI_PATH"] = cliPathEnv;
            }

            try
            {
                using (var proc = Process.Start(psi))
                {
                    if (proc == null)
                    {
                        Debug.LogWarning($"Bugsee: failed to start {CliPath}.");
                        return -1;
                    }

                    string stdout = null;
                    string stderr = null;
                    var outThread = new Thread(() => { stdout = proc.StandardOutput.ReadToEnd(); });
                    var errThread = new Thread(() => { stderr = proc.StandardError.ReadToEnd(); });
                    outThread.IsBackground = true;
                    errThread.IsBackground = true;
                    outThread.Start();
                    errThread.Start();

                    if (!proc.WaitForExit(timeoutMs))
                    {
                        try { proc.Kill(); } catch { /* ignore */ }
                        try { outThread.Join(2000); } catch { /* ignore */ }
                        try { errThread.Join(2000); } catch { /* ignore */ }
                        Debug.LogWarning(
                            $"Bugsee: {CliPath} timed out after {timeoutMs / 1000}s — build continues.");
                        return -1;
                    }

                    outThread.Join(5000);
                    errThread.Join(5000);

                    if (!string.IsNullOrEmpty(stderr))
                    {
                        Debug.Log(stderr);
                    }
                    if (!string.IsNullOrEmpty(stdout))
                    {
                        Debug.Log(stdout);
                    }
                    return proc.ExitCode;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Bugsee: {CliPath} error — {ex.Message}. Build continues.");
                return -1;
            }
        }

        public static string Quote(string s) => $"\"{(s ?? "").Replace("\"", "\\\"")}\"";
    }
}
#endif
