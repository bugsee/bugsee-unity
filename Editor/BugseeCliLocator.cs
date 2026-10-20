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
        // Kept for the editor session, across domain reloads (SessionState; Reset does not clear it): a blackholed network
        // must cost one timeout, not one per build.
        const string DownloadFailureKey = "Bugsee.CliDownloadFailure";
        static string DownloadFailure
        {
            get { var value = UnityEditor.SessionState.GetString(DownloadFailureKey, ""); return value.Length == 0 ? null : value; }
            set { UnityEditor.SessionState.SetString(DownloadFailureKey, value ?? ""); }
        }
        static string _stagingDir;

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
                    Fetch = version => Fetch(settings, version),
                    Install = (staged, cacheDir, version) => Install(settings, staged, cacheDir, version),
                    Discard = _ => DiscardStaging(),
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
                // Routine when something was found after all (a shim that does not run, an old install passed over).
                foreach (string note in _resolved.Notes)
                {
                    // A too-old BUGSEE_CLI_PATH is used anyway: that is worth a warning, the rest is routine.
                    if (_resolved.Source == CliSource.Explicit) Debug.LogWarning("[Bugsee] " + note);
                    else Debug.Log("[Bugsee] " + note);
                }
            }

            return _resolved;
        }

        /// <summary>
        /// The CLI already installed, for baking into the iOS Run Script: no download, no session state, no log. Null
        /// when none is found. (The Run Script never downloads, so a download here would only be wasted.)
        /// </summary>
        public static string FindInstalled()
        {
            if (_resolved != null && _resolved.Found && File.Exists(_resolved.Path))
            {
                return _resolved.Path;
            }

            try
            {
                CliSettings settings = ReadSettings(quiet: true);
                settings.DownloadAllowed = false;
                // The Run Script has its own, lower floor (it uploads no ELF files): a 0.8.0 the Editor passes over for
                // its own uploads is still worth baking, since Xcode's minimal PATH would not find it.
                settings.MinVersion = BugseeCliPolicy.IosMinVersion;
                var resolution = BugseeCliPolicy.Resolve(settings, new CliProbe { FileExists = File.Exists, QueryVersion = QueryVersion });
                return resolution.Found ? resolution.Path : null;
            }
            catch (Exception)
            {
                return null;
            }
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
                Debug.LogWarning(BugseeCliPolicy.NotFoundMessage(resolution, IsWindows));
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

        static CliSettings ReadSettings(bool quiet = false)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string localAppData = IsWindows ? Environment.GetEnvironmentVariable("LOCALAPPDATA") : null;
            string version = Environment.GetEnvironmentVariable("BUGSEE_CLI_VERSION");
            version = string.IsNullOrWhiteSpace(version) ? null : version.Trim().TrimStart('v');
            if (version != null && !BugseeCliPolicy.IsValidVersion(version))
            {
                if (!quiet) Debug.LogWarning("[Bugsee] Ignoring BUGSEE_CLI_VERSION=" + version + ": not a release version like 0.8.1. Using " + BugseeCliPolicy.DefaultVersion + ".");
                version = null;
            }
            else if (version != null && !BugseeCliPolicy.VersionAtLeast(version, BugseeCliPolicy.MinVersion))
            {
                if (!quiet) Debug.LogWarning("[Bugsee] Ignoring BUGSEE_CLI_VERSION=" + version + ": older than " + BugseeCliPolicy.MinVersion + ", which the uploads need. Using " + BugseeCliPolicy.DefaultVersion + ".");
                version = null;
            }

            var settings = new CliSettings
            {
                ExplicitPath = Environment.GetEnvironmentVariable("BUGSEE_CLI_PATH"),
                PathEnv = Environment.GetEnvironmentVariable("PATH"),
                Home = home,
                LocalAppData = localAppData,
                IsWindows = IsWindows,
                Version = version ?? BugseeCliPolicy.DefaultVersion,
                Triple = BugseeCliPolicy.HostTriple(HostOs, RuntimeInformation.OSArchitecture.ToString()),
                DownloadAllowed = BugseeCliPolicy.DownloadAllowed(
                    BugseeCliRunner.HasAppToken, Environment.GetEnvironmentVariable("BUGSEE_NO_CLI_DOWNLOAD") != null),
            };
            if (DownloadFailure != null)
            {
                settings.DownloadAllowed = false;
                settings.DownloadDisabledNote = "Downloading bugsee-cli failed earlier in this Editor session (" + DownloadFailure + "); not retried until it restarts.";
            }

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
            int exit = Capture(path, new[] { "--version" }, 15000, out output);
            return exit == 0 ? BugseeCliPolicy.ParseVersionOutput(output) : null;
        }

        /// <summary>Runs a process, returns its exit code (-1 when it did not start or run to the end) and stdout.</summary>
        static int Capture(string file, string[] args, int timeoutMs, out string stdout)
        {
            stdout = null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = file,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                foreach (string arg in args)
                {
                    psi.ArgumentList.Add(arg);
                }

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

        /// <summary>Downloads, verifies and unpacks the release once into a temp directory; returns the binary there.</summary>
        static string Fetch(CliSettings settings, string version)
        {
            try
            {
                return FetchUnchecked(settings, version);
            }
            catch (Exception ex)
            {
                DownloadFailure = ex.Message;
                DiscardStaging();
                throw;
            }
        }

        static string FetchUnchecked(CliSettings settings, string version)
        {
            string triple = settings.Triple;
            string binaryName = BugseeCliPolicy.BinaryName(BugseeCliPolicy.IsWindowsTriple(triple));
            string baseUrl = Environment.GetEnvironmentVariable("BUGSEE_CLI_BASE_URL");
            string url = BugseeCliPolicy.ArchiveUrl(baseUrl, version, triple);
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("refusing a non-https download URL " + BugseeCliPolicy.RedactUrl(url));
            }

            _stagingDir = Path.Combine(Path.GetTempPath(), "bugsee-cli-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_stagingDir);
            Debug.Log("[Bugsee] Downloading bugsee-cli " + version + " for " + triple + " from " + BugseeCliPolicy.RedactUrl(url));
            string archive = Path.Combine(_stagingDir, BugseeCliPolicy.ArchiveName(triple));
            FetchFile(url, archive);

            // The pinned hash for the default release; the published sidecar for any other.
            string expected = BugseeCliPolicy.KnownSha256(version, triple);
            if (expected == null)
            {
                string sidecar = Path.Combine(_stagingDir, "archive.sha256");
                FetchFile(BugseeCliPolicy.ChecksumUrl(url), sidecar);
                expected = BugseeCliPolicy.ParseSha256Sidecar(File.ReadAllText(sidecar));
                if (expected == null)
                {
                    throw new IOException("the published checksum for " + Path.GetFileName(archive) + " is malformed");
                }
            }

            string actual = Sha256Hex(archive);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("SHA-256 mismatch for " + Path.GetFileName(archive) + ": expected " + expected + ", got " + actual);
            }

            string extracted = Path.Combine(_stagingDir, "x");
            Directory.CreateDirectory(extracted);
            Extract(archive, extracted, BugseeCliPolicy.IsWindowsTriple(triple));
            string binary = FindBinary(extracted, binaryName);
            if (binary == null)
            {
                throw new IOException("the archive holds no " + binaryName);
            }

            if (!BugseeCliPolicy.IsWindowsTriple(triple) && Capture("chmod", new[] { "+x", binary }, 15000, out _) != 0)
            {
                throw new IOException("chmod +x failed on " + binary);
            }

            if (QueryVersion(binary) == null)
            {
                throw new IOException("the downloaded " + binaryName + " does not run on this machine");
            }

            return binary;
        }

        /// <summary>
        /// Puts the staged binary into the cache atomically: copied to a temp name next to the target, then moved. A
        /// binary another build installed meanwhile is kept; only files created by this call are ever deleted.
        /// </summary>
        static string Install(CliSettings settings, string staged, string cacheDir, string version)
        {
            string finalBinary = BugseeCliPolicy.CachedBinaryPath(settings, cacheDir, version, settings.Triple);
            string finalDir = Path.GetDirectoryName(finalBinary);
            Directory.CreateDirectory(finalDir);
            if (File.Exists(finalBinary))
            {
                if (QueryVersion(finalBinary) != null)
                {
                    return finalBinary;
                }

                // A leftover that does not run (an interrupted install, another machine's architecture) would make the
                // move below fail for ever: replace it.
                try { File.Delete(finalBinary); } catch { /* the move reports it */ }
            }

            string temp = Path.Combine(finalDir, Path.GetFileName(finalBinary) + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                File.Copy(staged, temp, false);
                if (!BugseeCliPolicy.IsWindowsTriple(settings.Triple) && Capture("chmod", new[] { "+x", temp }, 15000, out _) != 0)
                {
                    throw new IOException("chmod +x failed on " + temp);
                }

                try
                {
                    File.Move(temp, finalBinary);
                }
                catch (IOException)
                {
                    // Another build won the race; theirs is kept if it runs.
                    if (!(File.Exists(finalBinary) && QueryVersion(finalBinary) != null))
                    {
                        throw;
                    }
                }

                return finalBinary;
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* ignore */ }
            }
        }

        static void DiscardStaging()
        {
            string dir = _stagingDir;
            _stagingDir = null;
            if (dir != null)
            {
                try { Directory.Delete(dir, true); } catch { /* ignore */ }
            }
        }

        /// <summary>An HTTP error status: the server answered, so a second attempt with curl would get the same answer.</summary>
        sealed class HttpStatusException : IOException
        {
            public HttpStatusException(string message) : base(message) { }
        }

        static void FetchFile(string url, string destination)
        {
            try
            {
                FetchWithRedirects(url, destination);
            }
            catch (HttpStatusException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Mono's TLS stack rejects some hosts' certificate chains and has its own timeouts: curl uses the system
                // trust store. Bounded, https only (also across redirects).
                string ignored;
                int exit = Capture(
                    "curl",
                    new[] { "--proto", "=https", "--proto-redir", "=https", "--tlsv1.2", "-fsSL", "--connect-timeout", "10", "--max-time", "120", "-o", destination, url },
                    130000, out ignored);
                if (exit != 0 || !File.Exists(destination))
                {
                    throw new IOException("GET " + BugseeCliPolicy.RedactUrl(url) + " failed (" + ex.Message + "; curl exit " + exit + ")");
                }
            }
        }

        static void FetchWithRedirects(string url, string destination)
        {
            string current = url;
            for (int hop = 0; hop <= 5; hop++)
            {
                var request = (HttpWebRequest)WebRequest.Create(current);
                request.Timeout = 20000;
                request.ReadWriteTimeout = 60000;
                request.AllowAutoRedirect = false;
                HttpWebResponse response;
                try
                {
                    response = (HttpWebResponse)request.GetResponse();
                }
                catch (WebException ex) when (ex.Response is HttpWebResponse errorResponse)
                {
                    response = errorResponse;
                }

                using (response)
                {
                    int status = (int)response.StatusCode;
                    if (status >= 300 && status < 400)
                    {
                        var next = new Uri(new Uri(current), response.Headers["Location"] ?? "");
                        if (next.Scheme != Uri.UriSchemeHttps)
                        {
                            throw new HttpStatusException("refusing a redirect to " + BugseeCliPolicy.RedactUrl(next.AbsoluteUri) + " (not https)");
                        }

                        current = next.AbsoluteUri;
                        continue;
                    }

                    if (status < 200 || status >= 300)
                    {
                        throw new HttpStatusException("GET " + BugseeCliPolicy.RedactUrl(current) + " returned " + status);
                    }

                    using (Stream input = response.GetResponseStream())
                    using (var output = File.Create(destination))
                    {
                        input.CopyTo(output);
                    }

                    return;
                }
            }

            throw new HttpStatusException("too many redirects from " + BugseeCliPolicy.RedactUrl(url));
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
            int exit = Capture("tar", new[] { "-xf", archive, "-C", destination }, 120000, out ignored);
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
