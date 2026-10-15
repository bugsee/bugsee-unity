#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Bugsee.WrapperPolicy;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Bugsee.Editor
{
    /// <summary>
    /// Resolves the <c>bugsee-cli</c> binary for the Editor symbol uploads (see <see cref="BugseeCliPolicy"/> for the
    /// order and why): BUGSEE_CLI_PATH, PATH, the well-known install directories, a cache, then a SHA-256-verified
    /// download of the pinned release from download.bugsee.com (opt out: BUGSEE_NO_CLI_DOWNLOAD; pin another release:
    /// BUGSEE_CLI_VERSION; mirror: BUGSEE_CLI_BASE_URL, the variables <c>install.sh</c> reads). Never throws; the
    /// result is remembered until the next build starts.
    /// </summary>
    internal static class BugseeCliLocator
    {
        static CliResolution _resolved;
        static bool _notFoundReported;

        public static CliResolution Resolve()
        {
            if (_resolved != null && (!_resolved.Found || File.Exists(_resolved.Path)))
            {
                return _resolved;
            }

            try
            {
                CliSettings settings = ReadSettings();
                _resolved = BugseeCliPolicy.Resolve(settings, new CliProbe
                {
                    FileExists = File.Exists,
                    QueryVersion = QueryVersion,
                    Download = (cacheDir, version) => Download(settings, cacheDir, version),
                });
            }
            catch (Exception ex)
            {
                _resolved = new CliResolution();
                _resolved.Notes.Add("Looking for bugsee-cli failed: " + ex.Message);
            }

            if (_resolved.Found)
            {
                Debug.Log("[Bugsee] Using bugsee-cli " + (_resolved.Version ?? "(version unknown)") + " (" + Describe(_resolved.Source) + "): " + _resolved.Path);
                foreach (string note in _resolved.Notes)
                {
                    Debug.LogWarning("[Bugsee] " + note);
                }
            }

            return _resolved;
        }

        /// <summary>Warns once per build that no CLI was found; false when one was.</summary>
        public static bool ReportIfMissing()
        {
            CliResolution resolution = Resolve();
            if (resolution.Found)
            {
                return false;
            }

            if (!_notFoundReported)
            {
                _notFoundReported = true;
                Debug.LogWarning(BugseeCliPolicy.NotFoundMessage(resolution));
            }

            return true;
        }

        internal static void Reset()
        {
            _resolved = null;
            _notFoundReported = false;
        }

        static string Describe(CliSource source)
        {
            switch (source)
            {
                case CliSource.Explicit: return "BUGSEE_CLI_PATH";
                case CliSource.Path: return "PATH";
                case CliSource.KnownDirectory: return "install directory";
                case CliSource.Cache: return "cached download";
                case CliSource.Downloaded: return "downloaded";
                default: return "unresolved";
            }
        }

        static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        static string HostOs =>
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macos"
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux"
            : IsWindows ? "windows" : "";

        static CliSettings ReadSettings()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string localAppData = IsWindows ? Environment.GetEnvironmentVariable("LOCALAPPDATA") : null;
            string version = Environment.GetEnvironmentVariable("BUGSEE_CLI_VERSION");
            var settings = new CliSettings
            {
                ExplicitPath = Environment.GetEnvironmentVariable("BUGSEE_CLI_PATH"),
                PathEnv = Environment.GetEnvironmentVariable("PATH"),
                Home = home,
                LocalAppData = localAppData,
                IsWindows = IsWindows,
                Version = string.IsNullOrWhiteSpace(version) ? BugseeCliPolicy.DefaultVersion : version.Trim().TrimStart('v'),
                Triple = BugseeCliPolicy.HostTriple(HostOs, RuntimeInformation.OSArchitecture.ToString()),
                DownloadAllowed = BugseeCliPolicy.DownloadAllowed(
                    BugseeCliRunner.HasAppToken, Environment.GetEnvironmentVariable("BUGSEE_NO_CLI_DOWNLOAD") != null),
            };

            // Shared by every project of this user first; the project's own Library when that is not writable.
            string userCache = null;
            if (IsWindows && !string.IsNullOrEmpty(localAppData))
            {
                userCache = Path.Combine(localAppData, "Bugsee", "cache", "cli");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && !string.IsNullOrEmpty(home))
            {
                userCache = Path.Combine(home, "Library", "Caches", "Bugsee", "cli");
            }
            else if (!string.IsNullOrEmpty(home))
            {
                string xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
                userCache = Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".cache") : xdg, "bugsee", "cli");
            }

            if (userCache != null)
            {
                settings.CacheDirectories.Add(userCache);
            }

            settings.CacheDirectories.Add(Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(Application.dataPath)), "Library", "Bugsee", "cli"));
            return settings;
        }

        static string QueryVersion(string path)
        {
            string output;
            int exit = Capture(path, "--version", 15000, out output);
            return exit == 0 ? BugseeCliPolicy.ParseVersionOutput(output) : null;
        }

        /// <summary>Runs a process, returns its exit code (-1 when it did not start or run to the end) and stdout.</summary>
        static int Capture(string file, string args, int timeoutMs, out string stdout)
        {
            stdout = null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = file,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using (Process proc = Process.Start(psi))
                {
                    if (proc == null)
                    {
                        return -1;
                    }

                    // Both pipes are drained off-thread: a child that fills one would stall, and a hung one must not
                    // block the build past the timeout.
                    string text = null;
                    var outThread = new Thread(() => { try { text = proc.StandardOutput.ReadToEnd(); } catch { /* ignore */ } });
                    var errThread = new Thread(() => { try { proc.StandardError.ReadToEnd(); } catch { /* ignore */ } });
                    outThread.IsBackground = true;
                    errThread.IsBackground = true;
                    outThread.Start();
                    errThread.Start();
                    if (!proc.WaitForExit(timeoutMs))
                    {
                        try { proc.Kill(); } catch { /* ignore */ }
                        return -1;
                    }

                    outThread.Join(5000);
                    errThread.Join(5000);
                    stdout = text;
                    return proc.ExitCode;
                }
            }
            catch (Exception)
            {
                return -1;
            }
        }

        static string Download(CliSettings settings, string cacheDir, string version)
        {
            string triple = settings.Triple;
            string binaryName = BugseeCliPolicy.BinaryName(BugseeCliPolicy.IsWindowsTriple(triple));
            string finalDir = Path.GetDirectoryName(BugseeCliPolicy.CachedBinaryPath(settings, cacheDir, version, triple));
            string finalBinary = Path.Combine(finalDir, binaryName);

            string baseUrl = Environment.GetEnvironmentVariable("BUGSEE_CLI_BASE_URL");
            string url = BugseeCliPolicy.ArchiveUrl(baseUrl, version, triple);
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("refusing a non-https download URL " + url);
            }

            string work = Path.Combine(Path.GetDirectoryName(finalDir), triple + ".part-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(work);
            try
            {
                Debug.Log("[Bugsee] Downloading bugsee-cli " + version + " for " + triple + " from " + url);
                string archive = Path.Combine(work, BugseeCliPolicy.ArchiveName(triple));
                Fetch(url, archive);
                string expected = BugseeCliPolicy.ParseSha256Sidecar(FetchText(BugseeCliPolicy.ChecksumUrl(url)));
                if (expected == null)
                {
                    throw new IOException("the published checksum for " + Path.GetFileName(archive) + " is malformed");
                }

                string actual = Sha256Hex(archive);
                if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("SHA-256 mismatch for " + Path.GetFileName(archive) + ": expected " + expected + ", got " + actual);
                }

                string extracted = Path.Combine(work, "x");
                Directory.CreateDirectory(extracted);
                Extract(archive, extracted, BugseeCliPolicy.IsWindowsTriple(triple));
                string binary = FindBinary(extracted, binaryName);
                if (binary == null)
                {
                    throw new IOException("the archive holds no " + binaryName);
                }

                if (!BugseeCliPolicy.IsWindowsTriple(triple))
                {
                    int chmod = Capture("chmod", "+x \"" + binary + "\"", 15000, out _);
                    if (chmod != 0)
                    {
                        throw new IOException("chmod +x failed on " + binary);
                    }
                }

                // A concurrent build may have installed the same release meanwhile: keep theirs.
                Directory.CreateDirectory(finalDir);
                if (!File.Exists(finalBinary))
                {
                    File.Copy(binary, finalBinary, true);
                }

                if (!BugseeCliPolicy.IsWindowsTriple(triple))
                {
                    Capture("chmod", "+x \"" + finalBinary + "\"", 15000, out _);
                }

                if (QueryVersion(finalBinary) == null)
                {
                    try { File.Delete(finalBinary); } catch { /* ignore */ }
                    throw new IOException("the downloaded " + binaryName + " does not run on this machine");
                }

                return finalBinary;
            }
            finally
            {
                try { Directory.Delete(work, true); } catch { /* ignore */ }
            }
        }

        static void Fetch(string url, string destination)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Timeout = 30000;
                request.ReadWriteTimeout = 120000;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (Stream input = response.GetResponseStream())
                using (var output = File.Create(destination))
                {
                    input.CopyTo(output);
                }
            }
            catch (Exception ex)
            {
                // Mono's TLS stack rejects some hosts' certificate chains: curl uses the system trust store.
                string ignored;
                int exit = Capture("curl", "--proto \"=https\" --tlsv1.2 -fsSL --max-time 300 -o \"" + destination + "\" \"" + url + "\"", 330000, out ignored);
                if (exit != 0 || !File.Exists(destination))
                {
                    throw new IOException("GET " + url + " failed (" + ex.Message + "; curl exit " + exit + ")");
                }
            }
        }

        static string FetchText(string url)
        {
            string temp = Path.Combine(Path.GetTempPath(), "bugsee-cli-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".sha256");
            try
            {
                Fetch(url, temp);
                return File.ReadAllText(temp);
            }
            finally
            {
                try { File.Delete(temp); } catch { /* ignore */ }
            }
        }

        static string Sha256Hex(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                var hex = new StringBuilder();
                foreach (byte b in sha.ComputeHash(stream))
                {
                    hex.Append(b.ToString("x2"));
                }

                return hex.ToString();
            }
        }

        static void Extract(string archive, string destination, bool zip)
        {
            if (zip)
            {
                System.IO.Compression.ZipFile.ExtractToDirectory(archive, destination);
                return;
            }

            string ignored;
            int exit = Capture("tar", "-xf \"" + archive + "\" -C \"" + destination + "\"", 120000, out ignored);
            if (exit != 0)
            {
                throw new IOException("tar -xf " + Path.GetFileName(archive) + " failed (exit " + exit + "); xz support is needed to unpack it");
            }
        }

        static string FindBinary(string root, string name)
        {
            foreach (string file in Directory.GetFiles(root, name, SearchOption.AllDirectories))
            {
                return file;
            }

            return null;
        }
    }

    /// <summary>Forgets the last resolution when a build starts, so a CLI installed since is found.</summary>
    sealed class BugseeCliResolutionReset : IPreprocessBuildWithReport
    {
        public int callbackOrder => int.MinValue;

        public void OnPreprocessBuild(BuildReport report) => BugseeCliLocator.Reset();
    }
}
#endif
